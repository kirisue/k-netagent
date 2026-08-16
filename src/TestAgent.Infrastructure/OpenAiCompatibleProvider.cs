using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TestAgent.Core;

namespace TestAgent.Infrastructure;

public sealed class OpenAiCompatibleProvider(HttpClient http) : IModelProvider
{
    public async IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); timeout.CancelAfter(TimeSpan.FromSeconds(request.Settings.TimeoutSeconds));
        using var response = await OpenWithRetryAsync(request, timeout.Token);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException(await FriendlyError(response, timeout.Token), null, response.StatusCode);
        if (!response.Content.Headers.ContentType?.MediaType?.Equals("text/event-stream", StringComparison.OrdinalIgnoreCase) ?? true)
        {
            var body = await response.Content.ReadAsStringAsync(timeout.Token);
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0)
            {
                var choice = choices[0];
                var message = choice.TryGetProperty("message", out var fullMessage) ? fullMessage
                    : choice.TryGetProperty("delta", out var deltaMessage) ? deltaMessage : default;
                if (message.ValueKind == JsonValueKind.Object)
                {
                    if (message.TryGetProperty("reasoning_content", out var reasoning) && reasoning.ValueKind == JsonValueKind.String)
                        yield return new(StreamEventKind.Reasoning, reasoning.GetString() ?? "");
                    if (message.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
                        yield return new(StreamEventKind.Content, content.GetString() ?? "");
                    if (message.TryGetProperty("tool_calls", out var calls) && calls.ValueKind == JsonValueKind.Array)
                        foreach (var call in calls.EnumerateArray())
                            if (TryParseToolCall(call, out var toolCall)) yield return new(StreamEventKind.Completed, ToolCall: toolCall);
                }
            }
            if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object && usage.TryGetProperty("total_tokens", out var total) && total.TryGetInt32(out var tokenCount))
                yield return new(StreamEventKind.Usage, Tokens: tokenCount);
            yield return new(StreamEventKind.Completed);
            yield break;
        }
        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
        using var reader = new StreamReader(stream);
        var toolCalls = new List<ToolCallAccumulator>();
        while (true)
        {
            var line = await reader.ReadLineAsync(timeout.Token);
            if (line is null) break;
            if (string.IsNullOrWhiteSpace(line) || !line.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) continue;
            var json = line[5..].Trim(); if (json == "[DONE]") break;
            using var doc = JsonDocument.Parse(json); var root = doc.RootElement;
            if (root.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0 && choices[0].TryGetProperty("delta", out var delta))
            {
                if (delta.TryGetProperty("reasoning_content", out var reasoning) && reasoning.ValueKind == JsonValueKind.String) yield return new(StreamEventKind.Reasoning, reasoning.GetString() ?? "");
                if (delta.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String) yield return new(StreamEventKind.Content, content.GetString() ?? "");
                if (delta.TryGetProperty("tool_calls", out var calls) && calls.ValueKind == JsonValueKind.Array)
                {
                    var position = 0;
                    foreach (var call in calls.EnumerateArray())
                    {
                        int? index = call.TryGetProperty("index", out var idx) && idx.TryGetInt32(out var parsedIndex)
                            ? parsedIndex : null;
                        var id = call.TryGetProperty("id", out var idValue) && idValue.ValueKind == JsonValueKind.String
                            ? idValue.GetString() : null;
                        var acc = FindOrCreateAccumulator(toolCalls, index, id, position++);
                        if (call.TryGetProperty("function", out var function))
                        {
                            if (function.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String)
                                acc.AppendName(name.GetString());
                            if (function.TryGetProperty("arguments", out var args))
                                acc.AppendArguments(args);
                        }
                    }
                }
            }
            if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object && usage.TryGetProperty("total_tokens", out var total)) yield return new(StreamEventKind.Usage, Tokens: total.GetInt32());
        }
        foreach (var item in toolCalls.OrderBy(x => x.Index ?? int.MaxValue).ThenBy(x => x.Sequence))
            if (item.Name.Length > 0) yield return new(StreamEventKind.Completed, ToolCall: new(item.Id.Length == 0 ? $"call-{Guid.NewGuid():N}" : item.Id, item.Name.ToString(), item.Arguments.ToString()));
        yield return new(StreamEventKind.Completed);
    }

    private async Task<HttpResponseMessage> OpenWithRetryAsync(ChatRequest request, CancellationToken ct)
    {
        // A retry would resend the full image after a possibly successful but interrupted upload.
        // Text requests retain the existing two bounded retries; image requests are attempted once.
        var maxRetries = request.Images is { Count: > 0 } ? 0 : 2;
        for (var attempt = 0; ; attempt++)
        {
            var message = CreateRequest(request);
            try
            {
                var response = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct);
                if (attempt < maxRetries && (response.StatusCode == HttpStatusCode.TooManyRequests || response.StatusCode >= HttpStatusCode.InternalServerError))
                { response.Dispose(); message.Dispose(); await Task.Delay(300 * (attempt + 1), ct); continue; }
                message.Dispose(); return response;
            }
            catch (HttpRequestException) when (attempt < maxRetries) { message.Dispose(); await Task.Delay(300 * (attempt + 1), ct); }
        }
    }

    private static HttpRequestMessage CreateRequest(ChatRequest request)
    {
        var images = SnapshotImages(request);
        try
        {
            var endpoint = request.Settings.Endpoint.TrimEnd('/') + "/chat/completions";
            var payload = new Dictionary<string, object?>
            {
                ["model"] = request.Settings.Model, ["stream"] = true,
                ["stream_options"] = new { include_usage = true }, ["max_tokens"] = request.Settings.MaxOutputTokens,
                ["messages"] = ToWireMessages(request, images)
            };
            if (request.Tools is { Count: > 0 }) payload["tools"] = request.Tools.Select(ToWireTool).ToArray();
            var message = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json") };
            if (!string.IsNullOrWhiteSpace(request.ApiKey)) message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", request.ApiKey);
            if (request.Settings.ProviderId.Equals("openrouter", StringComparison.OrdinalIgnoreCase)) message.Headers.TryAddWithoutValidation("HTTP-Referer", "https://localhost/testagent");
            return message;
        }
        finally
        {
            if(images is not null)foreach(var image in images)CryptographicOperations.ZeroMemory(image.Data);
        }
    }

    private static object[] ToWireMessages(ChatRequest request, IReadOnlyList<ImageInput>? images)
    {
        var imageMessageIndex = -1;
        if (images is { Count: > 0 })
            for (var index = request.Messages.Count - 1; index >= 0; index--)
                if (request.Messages[index].Role == ChatRole.User)
                {
                    imageMessageIndex = index;
                    break;
                }
        if (images is { Count: > 0 } && imageMessageIndex < 0)
            throw new InvalidOperationException("Image input requires a user message.");

        return request.Messages.Select((message, index) => index == imageMessageIndex
            ? ToWireImageMessage(message, images!)
            : ToWireMessage(message)).ToArray();
    }

    private static object ToWireImageMessage(ChatMessage message, IReadOnlyList<ImageInput> images)
    {
        var parts = new List<object> { new { type = "text", text = message.Content } };
        parts.AddRange(images.Select(image => (object)new
        {
            type = "image_url",
            image_url = new
            {
                url = $"data:{image.MimeType};base64,{Convert.ToBase64String(image.Data)}",
                detail = "auto"
            }
        }));
        return new { role = "user", content = parts.ToArray() };
    }

    private static IReadOnlyList<ImageInput>? SnapshotImages(ChatRequest request)
    {
        if (request.Images is not { Count: > 0 } images) return null;
        if (!request.Settings.SupportsImageInput)
            throw new InvalidOperationException("The selected model is not configured to accept image input. Enable image support in settings only after confirming the model capability.");
        if (!Uri.TryCreate(request.Settings.Endpoint, UriKind.Absolute, out var endpoint) ||
            endpoint.UserInfo.Length > 0 ||
            endpoint.Query.Length > 0 || endpoint.Fragment.Length > 0 ||
            !(endpoint.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
              endpoint.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) && endpoint.IsLoopback))
            throw new InvalidOperationException("Images may only be sent to an HTTPS model endpoint or a loopback HTTP endpoint.");
        if (images.Count > 4) throw new InvalidDataException("At most four images can be sent in one request.");
        var snapshots = new List<ImageInput>(images.Count);
        long totalBytes = 0;
        try
        {
            foreach (var image in images)
            {
                if (image.MimeType is not ("image/png" or "image/jpeg"))
                    throw new InvalidDataException("Only sanitized PNG and JPEG image input is supported.");
                if (image.Data is not { Length: > 0 } || image.Data.Length > ImageInputPreflight.MaxSourceBytes)
                    throw new InvalidDataException("Each image must be between 1 byte and 10 MB.");
                var data=image.Data.ToArray();
                try
                {
                    var header=ImageInputPreflight.Inspect(data);
                    if(!header.MimeType.Equals(image.MimeType,StringComparison.OrdinalIgnoreCase)||header.Width!=image.Width||header.Height!=image.Height)
                        throw new InvalidDataException("Image type or dimensions do not match the sanitized bytes.");
                    var actualHash = Convert.ToHexString(SHA256.HashData(data));
                    if (!actualHash.Equals(image.Sha256, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("Image integrity verification failed.");
                    totalBytes += data.Length;
                    snapshots.Add(image with { Data=data });
                }
                catch
                {
                    CryptographicOperations.ZeroMemory(data);
                    throw;
                }
            }
            if (totalBytes > 20 * 1024 * 1024)
                throw new InvalidDataException("Combined image input exceeds 20 MB.");
            return snapshots;
        }
        catch
        {
            foreach(var snapshot in snapshots)CryptographicOperations.ZeroMemory(snapshot.Data);
            throw;
        }
    }

    private static object ToWireMessage(ChatMessage value)
    {
        if (value.Role == ChatRole.Tool) return new { role = "tool", content = value.Content, tool_call_id = value.ToolCallId };
        if (value.Role == ChatRole.Assistant && value.ToolCalls is { Count: > 0 })
            return new { role = "assistant", content = string.IsNullOrWhiteSpace(value.Content) ? null : value.Content,
                tool_calls = value.ToolCalls.Select(x => new { id = x.Id, type = "function", function = new { name = x.Name, arguments = x.ArgumentsJson } }).ToArray() };
        return new { role = value.Role.ToString().ToLowerInvariant(), content = value.Content };
    }

    private static object ToWireTool(ToolDefinition value) => new
    {
        type = "function",
        function = new
        {
            name = value.Name, description = value.UsageExample is { Length: > 0 }
                ? value.Description + " Example: " + value.UsageExample : value.Description,
            parameters = new
            {
                type = "object",
                properties = value.Parameters.ToDictionary(x => x.Name, x => (object)ParameterSchema(x)),
                required = value.Parameters.Where(x => x.Required).Select(x => x.Name).ToArray()
            }
        }
    };

    private static object ParameterSchema(ToolParameterDefinition value)
    {
        var schema = new Dictionary<string, object?> { ["type"] = value.Type, ["description"] = value.Description };
        if (value.Enum is { Count: > 0 }) schema["enum"] = value.Enum;
        if (value.Type == "array") schema["items"] = value.Name switch
        {
            "replacements" => ObjectSchema(new Dictionary<string,object?>
            {
                ["oldText"]=new{type="string",description="Exact text that must occur once."},
                ["newText"]=new{type="string",description="Replacement text."}
            },["oldText","newText"]),
            "changes" => ObjectSchema(new Dictionary<string,object?>
            {
                ["path"]=new{type="string",description="Workspace-relative existing file."},
                ["expectedSha256"]=new{type="string",description="Current SHA-256 returned by read_file."},
                ["replacements"]=new{type="array",items=ObjectSchema(new Dictionary<string,object?>
                {
                    ["oldText"]=new{type="string"},["newText"]=new{type="string"}
                },["oldText","newText"])}
            },["path","expectedSha256","replacements"]),
            _ => new { type = "string" }
        };
        return schema;
    }

    private static object ObjectSchema(Dictionary<string,object?> properties,string[] required)=>new
        {type="object",properties,required,additionalProperties=false};

    private static ToolCallAccumulator FindOrCreateAccumulator(List<ToolCallAccumulator> calls, int? index,
        string? id, int position)
    {
        ToolCallAccumulator? result = null;
        if (index is not null)
            result = calls.FirstOrDefault(x => x.Index == index);
        if (result is null && !string.IsNullOrWhiteSpace(id))
            result = calls.FirstOrDefault(x => x.Id.Equals(id, StringComparison.Ordinal));
        if (result is null && index is null && string.IsNullOrWhiteSpace(id))
            result = calls.FirstOrDefault(x => x.FallbackPosition == position);
        if (result is null)
        {
            result = new ToolCallAccumulator(calls.Count, position);
            calls.Add(result);
        }
        result.Index ??= index;
        if (!string.IsNullOrWhiteSpace(id)) result.Id = id;
        return result;
    }

    private sealed class ToolCallAccumulator(int sequence, int fallbackPosition)
    {
        public int Sequence { get; } = sequence;
        public int FallbackPosition { get; } = fallbackPosition;
        public int? Index { get; set; }
        public string Id { get; set; } = "";
        public StringBuilder Name { get; } = new();
        public StringBuilder Arguments { get; } = new();

        public void AppendName(string? value)
        {
            if (string.IsNullOrEmpty(value)) return;
            var current = Name.ToString();
            if (current.Equals(value, StringComparison.Ordinal)) return;
            if (value.StartsWith(current, StringComparison.Ordinal))
            {
                Name.Clear(); Name.Append(value); return;
            }
            if (current.StartsWith(value, StringComparison.Ordinal)) return;
            Name.Append(value);
        }

        public void AppendArguments(JsonElement value)
        {
            if (value.ValueKind == JsonValueKind.String)
            {
                var fragment = value.GetString();
                if (!string.IsNullOrEmpty(fragment)) Arguments.Append(fragment);
                return;
            }
            if (value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return;
            Arguments.Clear();
            Arguments.Append(value.GetRawText());
        }
    }

    private static bool TryParseToolCall(JsonElement value, out ModelToolCall call)
    {
        call = null!;
        if (!value.TryGetProperty("function", out var function) || function.ValueKind != JsonValueKind.Object ||
            !function.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String) return false;
        var id = value.TryGetProperty("id", out var idValue) && idValue.ValueKind == JsonValueKind.String
            ? idValue.GetString() : null;
        var arguments = function.TryGetProperty("arguments", out var args)
            ? args.ValueKind == JsonValueKind.String ? args.GetString() : args.GetRawText()
            : "{}";
        call = new(id ?? $"call-{Guid.NewGuid():N}", name.GetString()!, arguments ?? "{}"); return true;
    }

    private static async Task<string> FriendlyError(HttpResponseMessage response, CancellationToken ct) => response.StatusCode switch
    { HttpStatusCode.Unauthorized => "API Key 无效或缺失。", HttpStatusCode.TooManyRequests => "模型服务限流，请稍后重试。", _ => $"模型服务返回 {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(ct)}" };
}
