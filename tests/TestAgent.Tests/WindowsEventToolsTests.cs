using System.Text.Json;
using TestAgent.Core;
using TestAgent.Infrastructure;
using Xunit;

namespace TestAgent.Tests;

public sealed class WindowsEventToolsTests
{
    [Fact]
    public async Task Listing_is_a_static_allowlist_and_does_not_claim_availability()
    {
        var tool = new ListWindowsEventChannelsTool();
        var result = await tool.ExecuteAsync(Request(tool.Definition.Name));
        Assert.Equal(ToolRiskLevel.ReadOnly, tool.Definition.RiskLevel);
        Assert.Equal(ToolExecutionStatus.Success, result.Status);
        using var json = JsonDocument.Parse(result.Output);
        Assert.False(json.RootElement.GetProperty("availabilityChecked").GetBoolean());
        Assert.Equal(["Application", "System"], json.RootElement.GetProperty("channels").EnumerateArray()
            .Select(item => item.GetProperty("name").GetString()!).ToArray());
        var rejected = await tool.ExecuteAsync(Request(tool.Definition.Name, "{\"remote\":\"private-host\"}"));
        Assert.Equal("invalid_arguments", rejected.ErrorCode);
        Assert.DoesNotContain("private-host", JsonSerializer.Serialize(rejected));
    }

    [Fact]
    public async Task Rejected_approval_prevents_all_reader_calls()
    {
        var reader = new FakeReader();
        var tool = new QueryWindowsEventsTool(reader);
        var observer = new Observer(false);
        var service = Service(tool);
        var result = await service.ExecuteAsync(Request(tool.Definition.Name), observer);
        Assert.Equal(ToolExecutionStatus.Blocked, result.Status);
        Assert.Equal("user_rejected", result.ErrorCode);
        Assert.Equal(ToolRiskLevel.LocalEnvironmentRead, observer.Request!.RiskLevel);
        Assert.Equal(0, reader.QueryCalls);
        Assert.Equal(0, reader.ListCalls);
    }

    [Fact]
    public async Task Approved_query_exposes_only_separate_metadata_dto_and_safe_summary()
    {
        const string sentinel = "RAW-MESSAGE-PRIVATE-SENTINEL";
        var reader = new FakeReader
        {
            Result = new([Item(9) with { Message = sentinel }], false, null, Now, sentinel)
        };
        var tool = new QueryWindowsEventsTool(reader);
        var observer = new Observer(true);
        var audit = new Audit();
        var sessions = new Sessions();
        var result = await Service(tool, audit, sessions).ExecuteAsync(Request(tool.Definition.Name), observer);
        Assert.Equal(ToolExecutionStatus.Success, result.Status);
        Assert.Equal(new WindowsEventQuery(), reader.LastQuery);
        Assert.Equal(1, reader.QueryCalls);
        Assert.Equal(0, reader.ListCalls);
        using var json = JsonDocument.Parse(result.Output);
        var metadata = Assert.Single(json.RootElement.GetProperty("events").EnumerateArray());
        Assert.Equal(["channel", "recordId", "eventId", "provider", "level", "time"],
            metadata.EnumerateObject().Select(property => property.Name).ToArray());
        Assert.DoesNotContain(sentinel, JsonSerializer.Serialize(result));
        Assert.DoesNotContain(sentinel, JsonSerializer.Serialize(audit.Entries));
        Assert.DoesNotContain(sentinel, JsonSerializer.Serialize(await sessions.ListAsync()));
        Assert.Contains("preview", result.NextAction);
        Assert.DoesNotContain("Example.Provider", result.Summary);
    }

    [Theory]
    [InlineData("{\"rawXPath\":\"*\"}")]
    [InlineData("{\"remote\":\"host\"}")]
    [InlineData("{\"path\":\"secret.evtx\"}")]
    [InlineData("{\"channel\":\"Security\"}")]
    [InlineData("{\"channel\":\"application\"}")]
    [InlineData("{\"channel\":null}")]
    [InlineData("{\"channel\":\"System\",\"channel\":\"Application\"}")]
    [InlineData("{\"lookbackMinutes\":0}")]
    [InlineData("{\"lookbackMinutes\":10081}")]
    [InlineData("{\"lookbackMinutes\":null}")]
    [InlineData("{\"maxEvents\":0}")]
    [InlineData("{\"maxEvents\":201}")]
    [InlineData("{\"maxEvents\":1.5}")]
    [InlineData("{\"maxEvents\":\"100\"}")]
    [InlineData("{\"eventId\":-1}")]
    [InlineData("{\"eventId\":65536}")]
    [InlineData("{\"maximumLevel\":0}")]
    [InlineData("{\"maximumLevel\":6}")]
    [InlineData("{\"beforeRecordId\":0}")]
    [InlineData("{\"beforeRecordId\":9223372036854775808}")]
    [InlineData("{\"provider\":\"\"}")]
    [InlineData("{\"provider\":null}")]
    [InlineData("{\"provider\":\"name' or 1=1\"}")]
    [InlineData("{\"provider\":\"C:\\\\private\"}")]
    [InlineData("{\"provider\":\"name\\ncommand\"}")]
    [InlineData("[]")]
    [InlineData("{")]
    public async Task Invalid_arguments_never_reach_reader(string arguments)
    {
        var reader = new FakeReader();
        var result = await new QueryWindowsEventsTool(reader).ExecuteAsync(Request("query_windows_events", arguments));
        Assert.Equal("invalid_arguments", result.ErrorCode);
        Assert.Equal(0, reader.QueryCalls);
    }

    [Fact]
    public async Task Oversized_provider_and_json_are_rejected_without_reading()
    {
        var reader = new FakeReader();
        var tool = new QueryWindowsEventsTool(reader);
        foreach (var size in new[] { 129, 5000 })
        {
            var result = await tool.ExecuteAsync(Request(tool.Definition.Name,
                JsonSerializer.Serialize(new { provider = new string('A', size) })));
            Assert.Equal("invalid_arguments", result.ErrorCode);
        }
        Assert.Equal(0, reader.QueryCalls);
    }

    [Fact]
    public async Task Structured_filters_and_boundary_values_pass_through_exactly()
    {
        var reader = new FakeReader();
        var tool = new QueryWindowsEventsTool(reader);
        await tool.ExecuteAsync(Request(tool.Definition.Name,
            "{\"channel\":\"System\",\"lookbackMinutes\":10080,\"maxEvents\":200,\"eventId\":65535,\"maximumLevel\":null,\"provider\":\"Microsoft-Windows-测试 {Guid}_1.0\",\"beforeRecordId\":1234}"));
        Assert.Equal(new WindowsEventQuery("System", 10080, 200, 65535,
            "Microsoft-Windows-测试 {Guid}_1.0", null, 1234), reader.LastQuery);
    }

    [Fact]
    public async Task Paged_metadata_uses_exclusive_cursor_without_skipping_omitted_records()
    {
        var reader = new FakeReader { Result = new([Item(12), Item(11), Item(10)], true, 8, Now) };
        var tool = new QueryWindowsEventsTool(reader);
        var first = await tool.ExecuteAsync(Request(tool.Definition.Name, "{\"maxEvents\":2}"));
        using var json = JsonDocument.Parse(first.Output);
        Assert.Equal([12L, 11L], json.RootElement.GetProperty("events").EnumerateArray()
            .Select(item => item.GetProperty("recordId").GetInt64()).ToArray());
        Assert.True(first.Truncated);
        Assert.Equal(11, json.RootElement.GetProperty("nextBeforeRecordId").GetInt64());
        reader.Result = new([Item(10)], false, null, Now);
        var next = await tool.ExecuteAsync(Request(tool.Definition.Name, "{\"maxEvents\":2,\"beforeRecordId\":11}"));
        Assert.Equal(11, reader.LastQuery!.BeforeRecordId);
        using var nextJson = JsonDocument.Parse(next.Output);
        Assert.Equal(10, Assert.Single(nextJson.RootElement.GetProperty("events").EnumerateArray())
            .GetProperty("recordId").GetInt64());
        Assert.Equal(JsonValueKind.Null, nextJson.RootElement.GetProperty("nextBeforeRecordId").ValueKind);
    }

    [Fact]
    public async Task Output_budget_keeps_json_valid_and_cursor_at_last_included_event()
    {
        var reader = new FakeReader
        {
            Result = new(Enumerable.Range(1, 200).Reverse()
                .Select(id => Item(id) with { Provider = new string('中', 128), Message = new string('X', 20000) })
                .ToArray(), false, null, Now)
        };
        var result = await new QueryWindowsEventsTool(reader).ExecuteAsync(Request("query_windows_events", "{\"maxEvents\":200}"));
        Assert.True(result.Truncated);
        Assert.InRange(result.Output.Length, 1, 16000);
        using var json = JsonDocument.Parse(result.Output);
        var events = json.RootElement.GetProperty("events").EnumerateArray().ToArray();
        Assert.InRange(events.Length, 1, 199);
        Assert.Equal(events[^1].GetProperty("recordId").GetInt64(),
            json.RootElement.GetProperty("nextBeforeRecordId").GetInt64());
        Assert.DoesNotContain(new string('X', 20), result.Output);
    }

    [Theory]
    [InlineData("denied", "windows_events_access_denied")]
    [InlineData("timeout", "windows_events_timeout")]
    [InlineData("other", "windows_events_query_failed")]
    public async Task Failures_have_stable_codes_and_do_not_echo_exception_text(string kind, string code)
    {
        const string sentinel = "private-host C:\\Users\\secret API-KEY-SENTINEL";
        var reader = new FakeReader
        {
            Failure = kind switch
            {
                "denied" => new UnauthorizedAccessException(sentinel),
                "timeout" => new TimeoutException(sentinel),
                _ => new InvalidOperationException(sentinel)
            }
        };
        var result = await new QueryWindowsEventsTool(reader).ExecuteAsync(Request("query_windows_events"));
        Assert.Equal(code, result.ErrorCode);
        Assert.False(result.Retryable);
        Assert.DoesNotContain(sentinel, JsonSerializer.Serialize(result));
    }

    [Fact]
    public async Task Cancellation_before_query_does_not_access_reader()
    {
        var reader = new FakeReader();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new QueryWindowsEventsTool(reader)
            .ExecuteAsync(Request("query_windows_events"), cancellation.Token));
        Assert.Equal(0, reader.QueryCalls);
    }

    private static readonly DateTimeOffset Now = new(2026, 9, 8, 12, 0, 0, TimeSpan.Zero);
    private static WindowsEventItem Item(long id) => new("Application", id, 1000, "Example.Provider", 2, Now, "private message");
    private static ToolRequest Request(string name, string arguments = "{}") => new(Guid.NewGuid().ToString("N"), name, arguments, "session");
    private static ToolExecutionService Service(IAgentTool tool, Audit? audit = null, Sessions? sessions = null) =>
        new(new ToolRegistry([tool]), audit ?? new Audit(), new ToolSessionCoordinator(sessions ?? new Sessions()));

    private sealed class FakeReader : IWindowsEventReader
    {
        public int QueryCalls { get; private set; }
        public int ListCalls { get; private set; }
        public WindowsEventQuery? LastQuery { get; private set; }
        public WindowsEventQueryResult Result { get; set; } = new([], false, null, Now);
        public Exception? Failure { get; init; }
        public Task<IReadOnlyList<WindowsEventChannelInfo>> ListChannelsAsync(CancellationToken ct = default)
        {
            ListCalls++;
            return Task.FromResult<IReadOnlyList<WindowsEventChannelInfo>>([]);
        }
        public Task<WindowsEventQueryResult> QueryAsync(WindowsEventQuery query, CancellationToken ct = default)
        {
            QueryCalls++;
            LastQuery = query;
            return Failure is null ? Task.FromResult(Result) : Task.FromException<WindowsEventQueryResult>(Failure);
        }
    }

    private sealed class Observer(bool approve) : IAgentObserver
    {
        public ToolApprovalRequest? Request { get; private set; }
        public ValueTask OnStateAsync(AgentState state) => ValueTask.CompletedTask;
        public ValueTask OnEventAsync(StreamEvent value) => ValueTask.CompletedTask;
        public ValueTask<bool> RequestToolApprovalAsync(ToolApprovalRequest request, CancellationToken ct)
        {
            Request = request;
            return ValueTask.FromResult(approve);
        }
    }

    private sealed class Audit : IToolAuditStore
    {
        public List<ToolAuditEntry> Entries { get; } = [];
        public Task AppendAsync(ToolAuditEntry entry, CancellationToken ct = default)
        {
            Entries.Add(entry);
            return Task.CompletedTask;
        }
    }

    private sealed class Sessions : IToolSessionStore
    {
        private readonly Dictionary<string, ToolSession> _values = [];
        public Task<IReadOnlyList<ToolSession>> ListAsync(string? parentSessionId = null, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ToolSession>>(_values.Values.ToArray());
        public Task<ToolSession?> GetAsync(string parentSessionId, string toolName, CancellationToken ct = default) =>
            Task.FromResult(_values.Values.FirstOrDefault(value => value.ParentSessionId == parentSessionId && value.ToolName == toolName));
        public Task SaveAsync(ToolSession session, CancellationToken ct = default)
        {
            _values[session.Id] = session;
            return Task.CompletedTask;
        }
    }
}
