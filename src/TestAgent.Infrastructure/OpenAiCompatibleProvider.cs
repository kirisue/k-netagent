using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
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
        for (var attempt = 0; ; attempt++)
        {
            var message = CreateRequest(request);
            try
            {
                var response = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct);
                if (attempt < 2 && (response.StatusCode == HttpStatusCode.TooManyRequests || response.StatusCode >= HttpStatusCode.InternalServerError))
                { response.Dispose(); message.Dispose(); await Task.Delay(300 * (attempt + 1), ct); continue; }
                message.Dispose(); return response;
            }
            catch (HttpRequestException) when (attempt < 2) { message.Dispose(); await Task.Delay(300 * (attempt + 1), ct); }
        }
    }

    private static HttpRequestMessage CreateRequest(ChatRequest request)
    {
        var endpoint = request.Settings.Endpoint.TrimEnd('/') + "/chat/completions";
        var payload = new Dictionary<string, object?>
        {
            ["model"] = request.Settings.Model, ["stream"] = true,
            ["stream_options"] = new { include_usage = true }, ["max_tokens"] = request.Settings.MaxOutputTokens,
            ["messages"] = request.Messages.Select(ToWireMessage).ToArray()
        };
        if (request.Tools is { Count: > 0 }) payload["tools"] = request.Tools.Select(ToWireTool).ToArray();
        var message = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json") };
        if (!string.IsNullOrWhiteSpace(request.ApiKey)) message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", request.ApiKey);
        if (request.Settings.ProviderId.Equals("openrouter", StringComparison.OrdinalIgnoreCase)) message.Headers.TryAddWithoutValidation("HTTP-Referer", "https://localhost/testagent");
        return message;
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
