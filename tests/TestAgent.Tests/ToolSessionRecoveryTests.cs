using System.Collections.Concurrent;
using TestAgent.Core;
using TestAgent.Infrastructure;
using Xunit;

namespace TestAgent.Tests;

public sealed class ToolSessionRecoveryTests
{
    [Fact]
    public async Task CompleteAsync_Is_idempotent_for_the_same_request_id()
    {
        var store = new MemoryStore();
        var coordinator = new ToolSessionCoordinator(store);
        var request = new ToolRequest(
            "request-1",
            "read_file",
            "{\"path\":\"README.md\"}",
            "chat-1");

        var started = await coordinator.StartAsync(request, approved: true);
        var result = new ToolResult(
            request.Id,
            request.Name,
            ToolExecutionStatus.Success,
            "file contents",
            Summary: "read succeeded");

        var first = await coordinator.CompleteAsync(started, request, result, approved: true);
        var second = await coordinator.CompleteAsync(first, request, result, approved: true);

        Assert.Equal(1, second.TotalCalls);
        Assert.Equal(1, second.SuccessCount);
        Assert.Equal(0, second.FailureCount);
        Assert.Single(second.RecentInvocations);
        Assert.Equal(request.Id, second.RecentInvocations[0].RequestId);
    }

    [Fact]
    public async Task CompleteAsync_Redacts_sensitive_success_output_before_persisting()
    {
        var store = new MemoryStore();
        var coordinator = new ToolSessionCoordinator(store);
        var request = new ToolRequest("request-2", "run_command", "{}", "chat-2");
        var started = await coordinator.StartAsync(request, approved: true);

        await coordinator.CompleteAsync(
            started,
            request,
            new ToolResult(
                request.Id,
                request.Name,
                ToolExecutionStatus.Success,
                "command finished; token=sentinel-secret",
                Summary: null),
            approved: true);

        var persisted = Assert.Single(await store.ListAsync("chat-2"));
        var serialized = System.Text.Json.JsonSerializer.Serialize(persisted);
        Assert.DoesNotContain("sentinel-secret", serialized, StringComparison.Ordinal);
        Assert.Contains("redacted", serialized, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Parent_scope_identity_is_case_insensitive()
    {
        var store = new MemoryStore();
        var coordinator = new ToolSessionCoordinator(store);
        ToolDefinition[] definitions =
        [
            new("read_file", "Read a file.", ToolRiskLevel.ReadOnly, [])
        ];

        var upper = await coordinator.EnsureSessionsAsync("CHAT-SCOPE", definitions);
        var lower = await coordinator.EnsureSessionsAsync("chat-scope", definitions);

        Assert.Equal(upper[0].Id, lower[0].Id);
        Assert.Single(await store.ListAsync());
    }

    [Fact]
    public async Task Interrupted_write_session_requires_review_but_read_session_recovers_idle()
    {
        var store = new MemoryStore(); var coordinator = new ToolSessionCoordinator(store);
        var write = new ToolDefinition("edit_file", "Edit", ToolRiskLevel.WorkspaceWrite, []);
        var read = new ToolDefinition("read_file", "Read", ToolRiskLevel.ReadOnly, []);
        await coordinator.EnsureSessionsAsync("scope", [write, read]);
        await coordinator.StartAsync(new("write-1", "edit_file", "{}", "scope"), true);
        await coordinator.StartAsync(new("read-1", "read_file", "{}", "scope"), true);

        var recovered = await coordinator.EnsureSessionsAsync("scope", [write, read]);

        Assert.Equal(ToolSessionState.NeedsReview, recovered.Single(x => x.ToolName == "edit_file").State);
        Assert.Equal(ToolSessionState.Idle, recovered.Single(x => x.ToolName == "read_file").State);
        await Assert.ThrowsAsync<ToolSessionNeedsReviewException>(() => coordinator.StartAsync(new("write-2", "edit_file", "{}", "scope"), true));
        await coordinator.AcknowledgeNeedsReviewAsync("scope", "edit_file");
        var restarted = await coordinator.StartAsync(new("write-2", "edit_file", "{}", "scope"), true);
        Assert.Equal(ToolSessionState.Running, restarted.State);
    }

    [Fact]
    public async Task Reconcile_returns_exact_active_count_and_retires_removed_tool()
    {
        var store = new MemoryStore(); var coordinator = new ToolSessionCoordinator(store);
        var read = new ToolDefinition("read_file", "Read", ToolRiskLevel.ReadOnly, []);
        var edit = new ToolDefinition("edit_file", "Edit", ToolRiskLevel.WorkspaceWrite, []);
        await coordinator.EnsureSessionsAsync("scope", [read, edit]);

        var active = await coordinator.EnsureSessionsAsync("scope", [read]);
        var all = await store.ListAsync("scope");

        Assert.Single(active); Assert.Equal("read_file", active[0].ToolName);
        Assert.Equal(ToolSessionState.Retired, all.Single(x => x.ToolName == "edit_file").State);
    }

    private sealed class MemoryStore : IToolSessionStore
    {
        private readonly ConcurrentDictionary<string, ToolSession> _items = new(StringComparer.Ordinal);

        public Task<IReadOnlyList<ToolSession>> ListAsync(
            string? parentSessionId = null,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            IReadOnlyList<ToolSession> result = _items.Values
                .Where(value => parentSessionId is null ||
                    value.ParentSessionId.Equals(parentSessionId, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            return Task.FromResult(result);
        }

        public Task<ToolSession?> GetAsync(
            string parentSessionId,
            string toolName,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            _items.TryGetValue(ToolSessionCoordinator.BuildId(parentSessionId, toolName), out var value);
            return Task.FromResult(value);
        }

        public Task SaveAsync(ToolSession session, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            _items[session.Id] = session;
            return Task.CompletedTask;
        }
    }
}
