namespace TestAgent.Core;

public enum BackgroundCommandState
{
    Starting,
    Running,
    Stopping,
    Completed,
    Failed,
    Stopped,
    TimedOut,
    NeedsReview
}

public sealed record BackgroundCommandSpec(
    string ScopeId,
    string Executable,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    int TimeoutSeconds = 600,
    string? DisplayName = null);

public sealed record BackgroundCommandJob(
    string Id,
    string ScopeId,
    string Executable,
    string WorkingDirectory,
    string DisplayName,
    BackgroundCommandState State,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt = null,
    DateTimeOffset? CompletedAt = null,
    int? ProcessId = null,
    int? ExitCode = null,
    bool OutputTruncated = false,
    string? Error = null,
    int Version = 1)
{
    public string DisplayLabel => $"{State} · {DisplayName} · exit={ExitCode?.ToString() ?? "-"} · {Id}";
}

public sealed record BackgroundCommandOutputChunk(
    string JobId,
    long Cursor,
    long NextCursor,
    string Content,
    bool IsComplete,
    bool Truncated);

public interface IBackgroundCommandService
{
    Task<BackgroundCommandJob> StartAsync(
        BackgroundCommandSpec spec,
        CancellationToken cancellationToken = default);

    Task<BackgroundCommandJob?> GetAsync(
        string jobId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<BackgroundCommandJob>> ListAsync(
        string? scopeId = null,
        CancellationToken cancellationToken = default);

    Task<BackgroundCommandOutputChunk> ReadOutputAsync(
        string jobId,
        long cursor = 0,
        int maxBytes = 64 * 1024,
        CancellationToken cancellationToken = default);

    Task<BackgroundCommandJob> StopAsync(
        string jobId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<BackgroundCommandJob>> ReconcileAsync(
        CancellationToken cancellationToken = default);
}
