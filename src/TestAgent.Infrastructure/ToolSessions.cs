using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TestAgent.Core;

namespace TestAgent.Infrastructure;

/// <summary>
/// Gives every registered tool one durable, isolated session per parent Agent session.
/// A tool session is an execution notebook, not another Agent or model conversation.
/// </summary>
public sealed class ToolSessionCoordinator(IToolSessionStore store) : IToolSessionCoordinator
{
    private const int MaxRecentInvocations = 20;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.OrdinalIgnoreCase);

    public async Task<IReadOnlyList<ToolSession>> EnsureSessionsAsync(string parentSessionId,
        IReadOnlyList<ToolDefinition> definitions, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(parentSessionId)) throw new ArgumentException("Parent session ID is required.", nameof(parentSessionId));
        var now = DateTimeOffset.UtcNow; var result = new List<ToolSession>();
        var activeNames = definitions.Select(x => x.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var definition in definitions.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
        {
            var gate = Gate(parentSessionId, definition.Name); await gate.WaitAsync(ct);
            try
            {
                var existing = await store.GetAsync(parentSessionId, definition.Name, ct);
                var definitionHash = DefinitionHash(definition);
                if (existing is null)
                {
                    existing = new ToolSession(BuildId(parentSessionId, definition.Name), parentSessionId, definition.Name,
                        ToolSessionState.Idle, 0, 0, 0, null, [], now, now,
                        RiskLevel: definition.RiskLevel, DefinitionHash: definitionHash);
                }
                else
                {
                    var state = existing.State switch
                    {
                        ToolSessionState.Running when definition.RiskLevel == ToolRiskLevel.ReadOnly => ToolSessionState.Idle,
                        ToolSessionState.Running => ToolSessionState.NeedsReview,
                        ToolSessionState.Retired => ToolSessionState.Idle,
                        _ => existing.State
                    };
                    existing = existing with
                    {
                        State = state,
                        RiskLevel = definition.RiskLevel,
                        DefinitionHash = definitionHash,
                        LastSuccessfulStrategy = existing.DefinitionHash is not null &&
                            !existing.DefinitionHash.Equals(definitionHash, StringComparison.Ordinal)
                                ? null : existing.LastSuccessfulStrategy,
                        UpdatedAt = state != existing.State || existing.DefinitionHash != definitionHash
                            ? now : existing.UpdatedAt
                    };
                }
                await store.SaveAsync(existing, ct);
                result.Add(existing);
            }
            finally { gate.Release(); }
        }
        foreach (var obsolete in (await store.ListAsync(parentSessionId, ct))
                     .Where(x => !activeNames.Contains(x.ToolName) && x.State != ToolSessionState.Retired))
        {
            var gate = Gate(parentSessionId, obsolete.ToolName); await gate.WaitAsync(ct);
            try { await store.SaveAsync(obsolete with { State = ToolSessionState.Retired, UpdatedAt = now }, ct); }
            finally { gate.Release(); }
        }
        return result;
    }

    public async Task<ToolSession> StartAsync(ToolRequest request, bool approved, CancellationToken ct = default)
    {
        var gate = Gate(request.SessionId, request.Name); await gate.WaitAsync(ct);
        try
        {
            var existing = await store.GetAsync(request.SessionId, request.Name, ct); var now = DateTimeOffset.UtcNow;
            var session = existing ?? new ToolSession(BuildId(request.SessionId, request.Name), request.SessionId, request.Name,
                ToolSessionState.Idle, 0, 0, 0, null, [], now, now);
            if (session.State == ToolSessionState.NeedsReview || session.State == ToolSessionState.Running)
                throw new ToolSessionNeedsReviewException($"Tool session '{request.Name}' has an interrupted operation with an unknown result. Review the workspace and acknowledge it before retrying.");
            if (session.RecentInvocations.Any(x => x.RequestId.Equals(request.Id, StringComparison.Ordinal)))
                throw new DuplicateToolRequestException($"Tool request '{request.Id}' was already completed and will not be replayed.");
            session = session with { State = approved ? ToolSessionState.Running : ToolSessionState.Blocked, UpdatedAt = now };
            await store.SaveAsync(session, ct); return session;
        }
        finally { gate.Release(); }
    }

    public async Task<ToolSession> CompleteAsync(ToolSession session, ToolRequest request, ToolResult result,
        bool approved, CancellationToken ct = default)
    {
        var gate = Gate(request.SessionId, request.Name); await gate.WaitAsync(ct);
        try
        {
            session = await store.GetAsync(request.SessionId, request.Name, ct) ?? session;
            var now = DateTimeOffset.UtcNow; var argumentSummary = SummarizeArguments(request.ArgumentsJson);
            if (session.RecentInvocations.Any(x => x.RequestId.Equals(request.Id, StringComparison.Ordinal)))
                return session;
            var resultSummary = SensitiveDataRedactor.Text(result.Summary ?? result.Error ?? result.Output, 1_200);
            var record = new ToolInvocationRecord(request.Id, argumentSummary, result.Status, resultSummary,
                RedactText(result.Error), now - (result.Duration ?? TimeSpan.Zero), (long)(result.Duration ?? TimeSpan.Zero).TotalMilliseconds,
                approved, result.ModifiedFiles);
            var recent = session.RecentInvocations.Append(record).TakeLast(MaxRecentInvocations).ToArray(); var success = result.Status == ToolExecutionStatus.Success;
            var updated = session with { State = Map(result.Status), TotalCalls = session.TotalCalls + 1,
                SuccessCount = session.SuccessCount + (success ? 1 : 0), FailureCount = session.FailureCount + (success ? 0 : 1),
                LastSuccessfulStrategy = success ? argumentSummary : session.LastSuccessfulStrategy, RecentInvocations = recent, UpdatedAt = now };
            await store.SaveAsync(updated, ct); return updated;
        }
        finally { gate.Release(); }
    }

    public Task<IReadOnlyList<ToolSession>> ListAsync(string? parentSessionId = null, CancellationToken ct = default) =>
        store.ListAsync(parentSessionId, ct);

    public async Task<ToolSession> AcknowledgeNeedsReviewAsync(string parentSessionId, string toolName,
        CancellationToken ct = default)
    {
        var gate = Gate(parentSessionId, toolName); await gate.WaitAsync(ct);
        try
        {
            var session = await store.GetAsync(parentSessionId, toolName, ct) ??
                          throw new InvalidOperationException("Tool session was not found.");
            if (session.State != ToolSessionState.NeedsReview)
                throw new InvalidOperationException("Tool session is not waiting for crash review.");
            var acknowledged = session with { State = ToolSessionState.Idle, UpdatedAt = DateTimeOffset.UtcNow };
            await store.SaveAsync(acknowledged, ct); return acknowledged;
        }
        finally { gate.Release(); }
    }

    public async Task<string?> GetStrategyHintAsync(string parentSessionId, string toolName, CancellationToken ct = default)
    {
        var session = await store.GetAsync(parentSessionId, toolName, ct);
        if (session is null || session.TotalCalls == 0) return null;
        var recentFailure = session.RecentInvocations.LastOrDefault(x => x.Status != ToolExecutionStatus.Success);
        var lines = new List<string>
        {
            $"Tool session {session.Id}: {session.SuccessCount}/{session.TotalCalls} calls succeeded."
        };
        if (!string.IsNullOrWhiteSpace(session.LastSuccessfulStrategy)) lines.Add("Last successful argument shape: " + session.LastSuccessfulStrategy);
        if (recentFailure is not null) lines.Add("Most recent failure: " + recentFailure.ResultSummary);
        return Summarize(string.Join("\n", lines), 800);
    }

    public static string BuildId(string parentSessionId, string toolName)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(parentSessionId.ToUpperInvariant() + "\n" + toolName.ToUpperInvariant())));
        return "TOOL-" + hash[..24];
    }
    private static string DefinitionHash(ToolDefinition definition) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(definition))));
    private static ToolSessionState Map(ToolExecutionStatus status) => status switch
    {
        ToolExecutionStatus.Success => ToolSessionState.Completed,
        ToolExecutionStatus.Blocked => ToolSessionState.Blocked,
        ToolExecutionStatus.Cancelled => ToolSessionState.Cancelled,
        ToolExecutionStatus.Timeout => ToolSessionState.Timeout,
        _ => ToolSessionState.Failed
    };
    private static string SummarizeArguments(string json) => SensitiveDataRedactor.Arguments(json, 1_200);
    private static string Summarize(string value, int max) => value.Length <= max ? value : value[..max] + "…";
    private SemaphoreSlim Gate(string parentSessionId, string toolName) => _locks.GetOrAdd(BuildId(parentSessionId, toolName), _ => new(1, 1));
    private static string? RedactText(string? value) => value is null ? null : SensitiveDataRedactor.Text(value, 1_200);
}
