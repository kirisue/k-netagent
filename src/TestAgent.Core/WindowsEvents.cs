namespace TestAgent.Core;

public sealed record WindowsEventQuery(
    string Channel = "Application", int LookbackMinutes = 60, int MaxEvents = 100,
    int? EventId = null, string? Provider = null, int? MaximumLevel = 3,
    long? BeforeRecordId = null);

// No username, computer name, SID, raw XML, process command line or bookmark is exposed here.
public sealed record WindowsEventItem(
    string Channel, long RecordId, int EventId, string Provider, int Level,
    DateTimeOffset? Timestamp, string Message, bool MessageUnavailable = false);

public sealed record WindowsEventQueryResult(
    IReadOnlyList<WindowsEventItem> Events, bool Truncated, long? NextBeforeRecordId,
    DateTimeOffset QueriedAt, string? Notice = null);

public sealed record WindowsEventChannelInfo(string Name, bool Available, string? Notice = null);

public interface IWindowsEventReader
{
    Task<IReadOnlyList<WindowsEventChannelInfo>> ListChannelsAsync(CancellationToken ct = default);
    Task<WindowsEventQueryResult> QueryAsync(WindowsEventQuery query, CancellationToken ct = default);
}

public enum WindowsEventWatchState { Stopped, Running, Failed }

public sealed record WindowsEventWatchSnapshot(
    WindowsEventWatchState State, string? Channel, IReadOnlyList<WindowsEventItem> Events,
    int DroppedEvents, DateTimeOffset? StartedAt, string? Notice = null);

public interface IWindowsEventWatchService : IDisposable
{
    // Raised without event text. Consumers retrieve the bounded local buffer explicitly.
    event Action? Changed;
    WindowsEventWatchSnapshot Snapshot { get; }
    Task StartAsync(WindowsEventQuery query, bool resumeBookmark = false, CancellationToken ct = default);
    Task StopAsync(CancellationToken ct = default);
}

public sealed record WindowsEventEvidence(string Text, int EventCount, bool Truncated);

public interface IWindowsEventEvidenceBuilder
{
    WindowsEventEvidence Build(IReadOnlyList<WindowsEventItem> selectedEvents);
}
