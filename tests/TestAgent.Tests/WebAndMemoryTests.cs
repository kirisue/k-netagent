using System.Net;
using System.Text;
using TestAgent.Core;
using TestAgent.Infrastructure;
using Xunit;

namespace TestAgent.Tests;

public sealed class WebAndMemoryTests
{
    [Fact]
    public void Audit_redaction_removes_web_path_and_query_values()
    {
        var redacted=SensitiveDataRedactor.Arguments("{\"url\":\"https://example.com/private/document?user=alice\"}");
        Assert.Contains("example.com",redacted);
        Assert.DoesNotContain("private",redacted);
        Assert.DoesNotContain("alice",redacted);
    }
    [Fact]
    public async Task Fetch_web_content_strips_active_html_and_wraps_it_as_external_data()
    {
        const string html = """
            <html>
              <head>
                <title>Example &amp; Docs</title>
                <style>.secret { display: none; }</style>
                <script>alert('hostile script');</script>
              </head>
              <body>
                <h1>Visible heading</h1>
                <p>Hello &amp; world</p>
                <noscript>hidden fallback</noscript>
              </body>
            </html>
            """;
        var handler = new StubHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(html, Encoding.UTF8, "text/html")
        });
        var tool = new FetchWebContentTool(new SafeWebContentReader(new HttpClient(handler)));

        var result = await tool.ExecuteAsync(new ToolRequest(
            "request-1", "fetch_web_content",
            "{\"url\":\"https://93.184.216.34/docs\",\"maxChars\":30000}", "session-1"));

        Assert.Equal(ToolExecutionStatus.Success, result.Status);
        Assert.Contains("<external-web-data>", result.Output);
        Assert.Contains("</external-web-data>", result.Output);
        Assert.Contains("Title: Example & Docs", result.Output);
        Assert.Contains("Visible heading", result.Output);
        Assert.Contains("Hello & world", result.Output);
        Assert.DoesNotContain("hostile script", result.Output);
        Assert.DoesNotContain(".secret", result.Output);
        Assert.DoesNotContain("hidden fallback", result.Output);
        Assert.Equal(1, handler.CallCount);
    }

    [Theory]
    [InlineData("http://93.184.216.34/page")]
    [InlineData("https://localhost/page")]
    [InlineData("https://10.0.0.1/page")]
    [InlineData("https://192.168.1.10/page")]
    public async Task Safe_web_reader_rejects_non_https_loopback_and_private_urls_without_sending(string url)
    {
        var handler = new StubHttpHandler(_ => throw new InvalidOperationException("HTTP must not be sent."));
        var reader = new SafeWebContentReader(new HttpClient(handler));

        await Assert.ThrowsAsync<InvalidDataException>(() => reader.ReadAsync(url, 10_000));

        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task Safe_web_reader_rejects_declared_content_length_above_download_limit()
    {
        var handler = new StubHttpHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("small body", Encoding.UTF8, "text/plain")
            };
            response.Content.Headers.ContentLength = 2_000_001;
            return response;
        });
        var reader = new SafeWebContentReader(new HttpClient(handler));

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            reader.ReadAsync("https://93.184.216.34/oversized", 10_000));

        Assert.Contains("2 MB", error.Message);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task Session_history_search_excludes_current_session_and_bounds_hits_and_snippets()
    {
        var now = DateTimeOffset.UtcNow;
        var sessions = new InMemorySessionStore(
        [
            Session("current", "Current", new ChatMessage(ChatRole.User, "needle in current", now)),
            Session("older-1", "One", new ChatMessage(ChatRole.User, new string('a', 500) + " needle " + new string('b', 800), now.AddSeconds(-1))),
            Session("older-2", "Two", new ChatMessage(ChatRole.Assistant, "needle second", now.AddMinutes(-3))),
            Session("older-3", "Three", new ChatMessage(ChatRole.User, "needle third", now.AddMinutes(-2))),
            Session("unrelated", "No match", new ChatMessage(ChatRole.User, "different text", now.AddMinutes(-1)))
        ]);
        var search = new SessionHistorySearch(sessions);

        var results = await search.SearchAsync("needle", excludeSessionId: "CURRENT", maxResults: 2);

        Assert.Equal(2, results.Count);
        Assert.DoesNotContain(results, hit => hit.SessionId.Equals("current", StringComparison.OrdinalIgnoreCase));
        Assert.All(results, hit =>
        {
            Assert.Contains("needle", hit.Snippet, StringComparison.OrdinalIgnoreCase);
            Assert.True(hit.Snippet.Length <= 700, $"Snippet had {hit.Snippet.Length} characters.");
        });
    }

    [Fact]
    public void Build_context_injects_only_the_memory_set_selected_for_the_current_scope()
    {
        var now = DateTimeOffset.UtcNow;
        var all = new[]
        {
            new MemoryEntry("user", "user fact", "USER-MARKER", true, now, MemoryScope.User),
            new MemoryEntry("matching", "session fact", "MATCHING-MARKER", true, now, MemoryScope.Session, "session-a"),
            new MemoryEntry("other", "other session", "OTHER-MARKER", true, now, MemoryScope.Session, "session-b")
        };
        var selected = all.Where(memory => memory.Scope == MemoryScope.User || memory.ScopeId == "session-a");

        var context = AgentRuntime.BuildContext([], selected, 10);
        var text = string.Join("\n", context.Select(message => message.Content));

        Assert.Contains("<memory-data>", text);
        Assert.Contains("USER-MARKER", text);
        Assert.Contains("MATCHING-MARKER", text);
        Assert.DoesNotContain("OTHER-MARKER", text);
    }

    [Fact]
    public async Task Runtime_injects_user_and_matching_session_memory_but_not_other_session_memory()
    {
        var now = DateTimeOffset.UtcNow;
        var memories = new InMemoryMemoryStore(
        [
            new("user", "user fact", "USER-MARKER", true, now, MemoryScope.User),
            new("matching", "matching fact", "MATCHING-MARKER", true, now, MemoryScope.Session, "SESSION-A"),
            new("other", "other fact", "OTHER-MARKER", true, now, MemoryScope.Session, "session-b"),
            new("disabled", "disabled fact", "DISABLED-MARKER", false, now, MemoryScope.User)
        ]);
        var provider = new CapturingProvider();
        var runtime = new AgentRuntime(provider, memories, new InMemorySessionStore([]), new NoTools());
        var session = new ChatSession("session-a", "Test", [], now, now);

        var result = await runtime.RunAsync(session, "hello",
            new ProviderSettings("custom", "https://example.invalid/v1", "fake", SelfReviewEnabled: false),
            null, new AcceptingObserver(), CancellationToken.None);

        Assert.Equal(AgentState.Completed, result.State);
        var request = Assert.Single(provider.Requests);
        var text = string.Join("\n", request.Messages.Select(message => message.Content));
        Assert.Contains("USER-MARKER", text);
        Assert.Contains("MATCHING-MARKER", text);
        Assert.DoesNotContain("OTHER-MARKER", text);
        Assert.DoesNotContain("DISABLED-MARKER", text);
    }

    private static ChatSession Session(string id, string title, params ChatMessage[] messages)
    {
        var created = messages.Min(message => message.CreatedAt);
        var updated = messages.Max(message => message.CreatedAt);
        return new(id, title, [.. messages], created, updated);
    }

    private sealed class StubHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(response(request));
        }
    }

    private sealed class InMemoryMemoryStore(IReadOnlyList<MemoryEntry> values) : IMemoryStore
    {
        public Task<IReadOnlyList<MemoryEntry>> ListAsync(CancellationToken ct = default) => Task.FromResult(values);
        public Task SaveAsync(MemoryEntry memory, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeleteAsync(string id, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class InMemorySessionStore(IReadOnlyList<ChatSession> values) : ISessionStore
    {
        public Task<IReadOnlyList<ChatSession>> ListAsync(CancellationToken ct = default) => Task.FromResult(values);
        public Task<ChatSession?> GetAsync(string id, CancellationToken ct = default) =>
            Task.FromResult(values.FirstOrDefault(session => session.Id == id));
        public Task SaveAsync(ChatSession session, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeleteAsync(string id, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class CapturingProvider : IModelProvider
    {
        public List<ChatRequest> Requests { get; } = [];

        public async IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Requests.Add(request);
            yield return new(StreamEventKind.Content, "answer");
            await Task.CompletedTask;
        }
    }

    private sealed class NoTools : IToolExecutionService
    {
        public IReadOnlyList<ToolDefinition> GetDefinitions() => [];
        public Task<ToolResult> ExecuteAsync(ToolRequest request, IAgentObserver observer, CancellationToken ct = default) =>
            throw new InvalidOperationException("No tool call was expected.");
    }

    private sealed class AcceptingObserver : IAgentObserver
    {
        public ValueTask OnStateAsync(AgentState state) => ValueTask.CompletedTask;
        public ValueTask OnEventAsync(StreamEvent value) => ValueTask.CompletedTask;
        public ValueTask<bool> RequestToolApprovalAsync(ToolApprovalRequest request, CancellationToken ct) =>
            ValueTask.FromResult(true);
    }
}
