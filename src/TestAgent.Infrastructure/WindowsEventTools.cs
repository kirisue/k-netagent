using System.Text.Json;
using TestAgent.Core;

namespace TestAgent.Infrastructure;

public sealed class ListWindowsEventChannelsTool : IAgentTool
{
    public ToolDefinition Definition { get; } = new(
        "list_windows_event_channels",
        "List the fixed local Windows event channel allowlist. This does not access Windows logs or check availability.",
        ToolRiskLevel.ReadOnly, [], "{}");

    public Task<ToolResult> ExecuteAsync(ToolRequest request, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        try { WindowsEventToolSupport.Parse(request.ArgumentsJson, []); }
        catch (InvalidDataException) { return Task.FromResult(WindowsEventToolSupport.InvalidArguments(request)); }
        return Task.FromResult(new ToolResult(request.Id, Definition.Name, ToolExecutionStatus.Success,
            "{\"channels\":[{\"name\":\"Application\",\"description\":\"Local application events\"},{\"name\":\"System\",\"description\":\"Local Windows system events\"}],\"availabilityChecked\":false}",
            Summary: "Listed 2 allowed local Windows event channels.",
            NextAction: "Use query_windows_events for an approved bounded metadata query. Security, remote computers and log files are not supported."));
    }
}

public sealed class QueryWindowsEventsTool(IWindowsEventReader reader) : IAgentTool
{
    private const int MaxOutputChars = 16_000;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly string[] AllowedProperties =
        ["channel", "lookbackMinutes", "maxEvents", "eventId", "provider", "maximumLevel", "beforeRecordId"];

    public ToolDefinition Definition { get; } = new(
        "query_windows_events",
        "Query bounded metadata from local Application or System events after user approval. Returns no event message, raw XML, user, computer, file path, or command line. To share event text, the user must select and preview evidence in Windows Event Center.",
        ToolRiskLevel.LocalEnvironmentRead,
        [new("channel", "string", "Allowed local channel; defaults to Application.", Enum: ["Application", "System"]),
         new("lookbackMinutes", "integer", "Look back 1..10080 minutes (7 days); defaults to 60."),
         new("maxEvents", "integer", "Return 1..200 events, subject to the output budget; defaults to 100."),
         new("eventId", "integer", "Optional exact event ID, 0..65535."),
         new("provider", "string", "Optional exact provider, 1..128 letters, digits, spaces, underscores, hyphens, periods or braces."),
         new("maximumLevel", "integer", "Maximum event severity level 1..5; defaults to 3. JSON null includes all levels."),
         new("beforeRecordId", "integer", "Optional positive exclusive cursor from the preceding result; use unchanged filters.")],
        "{\"channel\":\"Application\",\"lookbackMinutes\":60,\"maxEvents\":100,\"maximumLevel\":3}");

    public async Task<ToolResult> ExecuteAsync(ToolRequest request, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        WindowsEventQuery query;
        try { query = ParseQuery(request.ArgumentsJson); }
        catch (InvalidDataException) { return WindowsEventToolSupport.InvalidArguments(request); }

        WindowsEventQueryResult result;
        try { result = await reader.QueryAsync(query, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (UnauthorizedAccessException)
        {
            return Failure(request, ToolExecutionStatus.Blocked, "windows_events_access_denied",
                "Windows did not permit reading this event channel.");
        }
        catch (TimeoutException)
        {
            return Failure(request, ToolExecutionStatus.Failed, "windows_events_timeout",
                "The bounded Windows event query timed out.");
        }
        catch
        {
            return Failure(request, ToolExecutionStatus.Failed, "windows_events_query_failed",
                "The Windows event query did not complete. Inspect the channel in Windows Event Center.");
        }

        // Build a separate DTO so event message text can never enter model output or tool history.
        var metadata = new List<EventMetadata>();
        var outputTruncated = false;
        var candidates = result.Events.Where(item => item.Channel == query.Channel && item.RecordId > 0 &&
            (query.BeforeRecordId is null || item.RecordId < query.BeforeRecordId))
            .OrderByDescending(item => item.RecordId).DistinctBy(item => item.RecordId).ToArray();
        foreach (var item in candidates.Take(query.MaxEvents))
        {
            ct.ThrowIfCancellationRequested();
            metadata.Add(new(item.Channel, item.RecordId, item.EventId,
                WindowsEventPrivacy.Sanitize(item.Provider, 128), item.Level, item.Timestamp));
            // Reserve a little space for false/null versus true/numeric cursor serialization.
            if (Serialize(metadata, query, result.QueriedAt, true, metadata[^1].RecordId).Length <= MaxOutputChars - 32)
                continue;
            metadata.RemoveAt(metadata.Count - 1);
            outputTruncated = true;
            break;
        }

        var truncated = result.Truncated || outputTruncated || candidates.Length > metadata.Count;
        long? cursor = truncated && metadata.Count > 0 ? metadata[^1].RecordId : null;
        var output = Serialize(metadata, query, result.QueriedAt, truncated, cursor);
        return new(request.Id, Definition.Name, ToolExecutionStatus.Success, output,
            Summary: $"{metadata.Count} event(s); channel={query.Channel}; from={result.QueriedAt.AddMinutes(-query.LookbackMinutes):O}; to={result.QueriedAt:O}.",
            Truncated: truncated,
            NextAction: "Metadata is untrusted diagnostic evidence. Event text is not included. The user must select events, preview redacted evidence and explicitly share it from Windows Event Center before the Agent can analyze event messages. If a nextBeforeRecordId is returned, keep the same filters for the next approved metadata query.");
    }

    private static string Serialize(IReadOnlyList<EventMetadata> events, WindowsEventQuery query,
        DateTimeOffset queriedAt, bool truncated, long? cursor) => JsonSerializer.Serialize(new
        {
            channel = query.Channel,
            from = queriedAt.AddMinutes(-query.LookbackMinutes),
            to = queriedAt,
            count = events.Count,
            events,
            truncated,
            nextBeforeRecordId = cursor
        }, JsonOptions);

    private static WindowsEventQuery ParseQuery(string json)
    {
        var root = WindowsEventToolSupport.Parse(json, AllowedProperties);
        var channel = "Application";
        if (root.TryGetProperty("channel", out var channelValue))
        {
            if (channelValue.ValueKind != JsonValueKind.String || channelValue.GetString() is not ("Application" or "System"))
                throw new InvalidDataException();
            channel = channelValue.GetString()!;
        }
        var lookback = Integer(root, "lookbackMinutes", 60, 1, 10080);
        var maxEvents = Integer(root, "maxEvents", 100, 1, 200);
        var eventId = NullableInteger(root, "eventId", null, 0, 65535);
        var maximumLevel = NullableInteger(root, "maximumLevel", 3, 1, 5);
        string? provider = null;
        if (root.TryGetProperty("provider", out var providerValue))
        {
            if (providerValue.ValueKind != JsonValueKind.String) throw new InvalidDataException();
            provider = providerValue.GetString();
            if (string.IsNullOrWhiteSpace(provider) || provider.Length > 128 ||
                provider.Any(character => !char.IsLetterOrDigit(character) && character is not (' ' or '_' or '-' or '.' or '{' or '}')))
                throw new InvalidDataException();
        }
        long? beforeRecordId = null;
        if (root.TryGetProperty("beforeRecordId", out var cursorValue) && cursorValue.ValueKind != JsonValueKind.Null)
        {
            if (cursorValue.ValueKind != JsonValueKind.Number || !cursorValue.TryGetInt64(out var value) || value <= 0)
                throw new InvalidDataException();
            beforeRecordId = value;
        }
        return new(channel, lookback, maxEvents, eventId, provider, maximumLevel, beforeRecordId);
    }

    private static int Integer(JsonElement root, string name, int fallback, int minimum, int maximum)
    {
        if (!root.TryGetProperty(name, out var value)) return fallback;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number) || number < minimum || number > maximum)
            throw new InvalidDataException();
        return number;
    }

    private static int? NullableInteger(JsonElement root, string name, int? fallback, int minimum, int maximum)
    {
        if (!root.TryGetProperty(name, out var value)) return fallback;
        return value.ValueKind == JsonValueKind.Null ? null : Integer(root, name, 0, minimum, maximum);
    }

    private static ToolResult Failure(ToolRequest request, ToolExecutionStatus status, string code, string error) =>
        new(request.Id, request.Name, status, "", error, Summary: "Windows event query failed.",
            NextAction: "Inspect Windows Event Center locally. Do not automatically elevate permissions or repeat this query.",
            ErrorCode: code, Retryable: false);

    private sealed record EventMetadata(string Channel, long RecordId, int EventId, string Provider, int Level,
        DateTimeOffset? Time);
}

internal static class WindowsEventToolSupport
{
    public static JsonElement Parse(string? json, IReadOnlyList<string> allowedProperties)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > 4096) throw new InvalidDataException();
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 4 });
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in document.RootElement.EnumerateObject())
                if (!seen.Add(property.Name) || !allowedProperties.Contains(property.Name, StringComparer.Ordinal))
                    throw new InvalidDataException();
            return document.RootElement.Clone();
        }
        catch (JsonException) { throw new InvalidDataException(); }
    }

    public static ToolResult InvalidArguments(ToolRequest request) => new(request.Id, request.Name,
        ToolExecutionStatus.Failed, "", "Invalid Windows event arguments. Use only the documented fields and ranges.",
        Summary: "Windows event arguments rejected before reading.",
        NextAction: "Use Application or System and bounded structured filters. Raw XPath, remote hosts and paths are not accepted.",
        ErrorCode: "invalid_arguments", Retryable: false);
}
