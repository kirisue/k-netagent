using System.Text.Json;
using TestAgent.Core;
using TestAgent.Infrastructure;
using Xunit;

namespace TestAgent.Tests;

public sealed class DeterministicEscalationEvaluatorTests
{
    [Fact]
    public async Task Explicit_collaboration_request_is_a_strong_signal_and_builds_read_only_task()
    {
        var peers = new FakePeerService(Eligible("codex-one"));
        var evaluator = new DeterministicEscalationEvaluator(peers);

        var decision = await evaluator.EvaluateAsync(Context(
            "请让 Codex 帮我检查当前实现。", "I am not sure the implementation is correct."));

        Assert.True(decision.ShouldEscalate);
        Assert.True(decision.Score >= DeterministicEscalationEvaluator.DefaultThreshold);
        Assert.Equal("codex-one", decision.PeerId);
        Assert.Contains("explicit_collaboration_requested", decision.Reasons);
        Assert.Contains("Objective (bounded summary)", decision.DelegationTask);
        Assert.Contains("Failure classification", decision.DelegationTask);
        Assert.Contains("Draft summary", decision.DelegationTask);
        Assert.Contains("read-only", decision.DelegationTask, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Do not modify files", decision.DelegationTask, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, peers.ListCalls);
        Assert.Equal(0, peers.ConnectCalls);
        Assert.Equal(0, peers.ToolCalls);
    }

    [Fact]
    public async Task User_vocabulary_for_another_ag_is_recognized_as_explicit_collaboration()
    {
        var evaluator = new DeterministicEscalationEvaluator(
            new FakePeerService(Eligible("codex")));

        var decision = await evaluator.EvaluateAsync(Context(
            "请借用别的 ag 的工具帮我复核。", "draft"));

        Assert.True(decision.ShouldEscalate);
        Assert.Contains("explicit_collaboration_requested", decision.Reasons);
    }

    [Fact]
    public async Task Missing_write_and_verification_raise_score_while_successful_evidence_lowers_it()
    {
        var peers = new FakePeerService(Eligible("codex"));
        var evaluator = new DeterministicEscalationEvaluator(peers);
        var request = "实现这个修改并运行测试验证。";

        var missing = await evaluator.EvaluateAsync(Context(request, "未完成。"));
        var successful = await evaluator.EvaluateAsync(Context(request, "Implemented and verified.",
            new(EscalationSignalKind.WriteSucceeded),
            new(EscalationSignalKind.ValidationSucceeded)));

        Assert.True(missing.ShouldEscalate);
        Assert.Contains("requested_implementation_without_successful_write", missing.Reasons);
        Assert.Contains("requested_validation_without_successful_verification", missing.Reasons);
        Assert.False(successful.ShouldEscalate);
        Assert.True(successful.Score < missing.Score);
        Assert.Contains("successful_write", successful.Reasons);
        Assert.Contains("successful_verification", successful.Reasons);
        Assert.Equal("score_below_threshold", successful.SuppressedReason);
    }

    [Fact]
    public async Task Failed_write_or_validation_tool_crosses_threshold_for_the_requested_capability()
    {
        var peers = new FakePeerService(Eligible("codex"));
        var evaluator = new DeterministicEscalationEvaluator(peers);
        var tools = new[]
        {
            new ToolDefinition("edit_file", "edit", ToolRiskLevel.WorkspaceWrite, []),
            new ToolDefinition("run_command", "verify", ToolRiskLevel.ProcessExecution, [])
        };
        var writeFailure = Context("Implement this change.", "Edit failed.",
            new EscalationSignal(EscalationSignalKind.ToolFailed, "edit_file")) with
        {
            AvailableTools = tools
        };
        var validationFailureAfterWrite = Context("Implement and test this change.", "Tests failed.",
            new EscalationSignal(EscalationSignalKind.WriteSucceeded, "edit_file"),
            new EscalationSignal(EscalationSignalKind.ToolFailed, "run_command")) with
        {
            AvailableTools = tools
        };

        var writeDecision = await evaluator.EvaluateAsync(writeFailure);
        var validationDecision = await evaluator.EvaluateAsync(validationFailureAfterWrite);

        Assert.True(writeDecision.ShouldEscalate);
        Assert.Contains("workspace_write_tool_failed", writeDecision.Reasons);
        Assert.True(validationDecision.ShouldEscalate);
        Assert.Contains("validation_tool_failed", validationDecision.Reasons);
    }

    [Fact]
    public async Task Exhaustion_repetition_failures_and_timeouts_are_scored_deterministically_with_caps()
    {
        var peers = new FakePeerService(Eligible("codex"));
        var evaluator = new DeterministicEscalationEvaluator(peers);
        var signals = new[]
        {
            new EscalationSignal(EscalationSignalKind.ToolRoundLimitReached),
            new EscalationSignal(EscalationSignalKind.RepeatedToolCall),
            new EscalationSignal(EscalationSignalKind.ToolFailed),
            new EscalationSignal(EscalationSignalKind.ToolTimeout)
        };

        var first = await evaluator.EvaluateAsync(Context("Explain the issue.", "Draft", signals));
        var second = await evaluator.EvaluateAsync(Context("Explain the issue.", "Draft", signals));
        var manyFailures = await evaluator.EvaluateAsync(Context("Explain the issue.", "Draft",
            Enumerable.Repeat(new EscalationSignal(EscalationSignalKind.ToolFailed), 20).ToArray()));

        Assert.Equal(first.Score, second.Score);
        Assert.Equal(133, first.Score);
        Assert.Equal(36, manyFailures.Score);
        Assert.False(manyFailures.ShouldEscalate);
        Assert.Equal("score_below_threshold", manyFailures.SuppressedReason);
    }

    [Theory]
    [InlineData(EscalationSignalKind.UserRejected, "user_rejected")]
    [InlineData(EscalationSignalKind.Cancelled, "cancelled")]
    public async Task Rejection_and_cancellation_hard_suppress_without_querying_peers(
        EscalationSignalKind signal, string expectedReason)
    {
        var peers = new FakePeerService(Eligible("codex"));
        var evaluator = new DeterministicEscalationEvaluator(peers);

        var decision = await evaluator.EvaluateAsync(Context(
            "Please ask Codex to implement and test this.", "", new EscalationSignal(signal)));

        Assert.False(decision.ShouldEscalate);
        Assert.Equal(0, decision.Score);
        Assert.Equal(expectedReason, decision.SuppressedReason);
        Assert.Equal(0, peers.ListCalls);
    }

    [Fact]
    public async Task Cancellation_context_hard_suppresses_and_user_opt_out_is_respected()
    {
        var peers = new FakePeerService(Eligible("codex"));
        var evaluator = new DeterministicEscalationEvaluator(peers);
        var cancelled = Context("Ask Codex to help.", "") with { CancellationRequested = true };

        var cancellationDecision = await evaluator.EvaluateAsync(cancelled);
        var optOutDecision = await evaluator.EvaluateAsync(Context(
            "Do not use Codex or another agent; implement and test it locally.", ""));

        Assert.Equal("cancelled", cancellationDecision.SuppressedReason);
        Assert.Equal("user_opted_out", optOutDecision.SuppressedReason);
        Assert.Equal(0, peers.ListCalls);
    }

    [Fact]
    public async Task Exactly_one_connected_enabled_opted_in_codex_with_codex_allowlisted_is_required()
    {
        var ineligible = new[]
        {
            Eligible("disconnected") with { State = McpPeerConnectionState.Disconnected },
            Eligible("disabled") with { Profile = Eligible("disabled").Profile with { Enabled = false } },
            Eligible("manual") with { Profile = Eligible("manual").Profile with { AllowAutomaticEscalation = false } },
            Eligible("claude") with { Profile = Eligible("claude").Profile with { Kind = McpPeerKind.Claude } },
            Eligible("no-tool") with { Profile = Eligible("no-tool").Profile with { AllowedTools = ["codex-reply"] } }
        };
        var peers = new FakePeerService(ineligible);
        var evaluator = new DeterministicEscalationEvaluator(peers);

        var none = await evaluator.EvaluateAsync(Context("Ask Codex to review this.", "draft"));
        peers.Items = [.. ineligible, Eligible("only")];
        var one = await evaluator.EvaluateAsync(Context("Ask Codex to review this.", "draft"));
        peers.Items = [.. ineligible, Eligible("one"), Eligible("two")];
        var many = await evaluator.EvaluateAsync(Context("Ask Codex to review this.", "draft"));

        Assert.False(none.ShouldEscalate);
        Assert.Equal("no_eligible_peer", none.SuppressedReason);
        Assert.True(one.ShouldEscalate);
        Assert.Equal("only", one.PeerId);
        Assert.False(many.ShouldEscalate);
        Assert.Equal("multiple_eligible_peers", many.SuppressedReason);
    }

    [Fact]
    public async Task Threshold_is_configurable_and_below_threshold_evaluation_does_not_query_peers()
    {
        var peers = new FakePeerService(Eligible("codex"));
        var low = new DeterministicEscalationEvaluator(peers, threshold: 30);
        var high = new DeterministicEscalationEvaluator(peers, threshold: 44);
        var context = Context("Explain the issue.", "",
            new EscalationSignal(EscalationSignalKind.ToolFailed));

        var accepted = await low.EvaluateAsync(context);
        var rejected = await high.EvaluateAsync(context);

        Assert.True(accepted.ShouldEscalate);
        Assert.Equal(43, accepted.Score);
        Assert.False(rejected.ShouldEscalate);
        Assert.Equal(43, rejected.Score);
        Assert.Equal(1, peers.ListCalls);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new DeterministicEscalationEvaluator(peers, threshold: 0));
    }

    [Fact]
    public async Task Delegation_task_is_bounded_redacts_likely_secrets_and_omits_signal_details()
    {
        const string token = "sk-1234567890abcdefghijklmnop";
        const string privateDetail = "FULL-HISTORY-SENTINEL";
        var peers = new FakePeerService(Eligible("codex"));
        var evaluator = new DeterministicEscalationEvaluator(peers);
        var context = Context(
            $"Ask Codex to inspect token={token} " + new string('x', 5_000),
            $"Draft contains {token} " + new string('y', 5_000),
            new EscalationSignal(EscalationSignalKind.ExplicitCollaborationRequested,
                Detail: privateDetail));

        var decision = await evaluator.EvaluateAsync(context);

        Assert.True(decision.ShouldEscalate);
        Assert.NotNull(decision.DelegationTask);
        Assert.True(decision.DelegationTask!.Length <= DeterministicEscalationEvaluator.MaxDelegationTaskChars);
        Assert.DoesNotContain(token, decision.DelegationTask, StringComparison.Ordinal);
        Assert.DoesNotContain(privateDetail, decision.DelegationTask, StringComparison.Ordinal);
        Assert.Contains("likely sensitive text", decision.DelegationTask, StringComparison.OrdinalIgnoreCase);
    }

    private static EscalationContext Context(string userMessage, string draft,
        params EscalationSignal[] signals) => new(userMessage, draft, signals, [], 0, signals.Length);

    private static McpPeerInfo Eligible(string id) => new(
        new McpPeerProfile(id, id, McpPeerKind.Codex, Enabled: true,
            AllowedTools: ["codex"], AllowAutomaticEscalation: true),
        McpPeerConnectionState.Connected);

    private sealed class FakePeerService(params McpPeerInfo[] peers) : IMcpPeerService
    {
        public IReadOnlyList<McpPeerInfo> Items { get; set; } = peers;
        public int ListCalls { get; private set; }
        public int ConnectCalls { get; private set; }
        public int ToolCalls { get; private set; }
        public event Action? Changed { add { } remove { } }

        public Task<IReadOnlyList<McpPeerInfo>> ListAsync(CancellationToken ct = default)
        {
            ListCalls++;
            return Task.FromResult(Items);
        }

        public Task SaveAsync(McpPeerProfile profile, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeleteAsync(string id, CancellationToken ct = default) => Task.CompletedTask;
        public Task<McpPeerInfo> ConnectAsync(string id, CancellationToken ct = default)
        {
            ConnectCalls++;
            throw new InvalidOperationException("Evaluator must not connect peers.");
        }

        public Task DisconnectAsync(string id, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<McpPeerTool>> ListToolsAsync(string id, CancellationToken ct = default)
        {
            ToolCalls++;
            throw new InvalidOperationException("Evaluator must not discover or call peer tools.");
        }

        public Task<McpPeerCallResult> CallToolAsync(string peerId, string toolName,
            JsonElement arguments, CancellationToken ct = default)
        {
            ToolCalls++;
            throw new InvalidOperationException("Evaluator must not call peer tools.");
        }

        public void Dispose() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
