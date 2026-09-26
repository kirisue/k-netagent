namespace TestAgent.Core;

/// <summary>A reference to local evidence; deliberately contains no event message or raw XML.</summary>
public sealed record WindowsIncidentEvidence(
    string Channel, long RecordId, int EventId, string Provider, int Level,
    DateTimeOffset? Timestamp);

/// <summary>A deterministic local observation, not a diagnosis or an authorization to repair.</summary>
public sealed record WindowsIncident(
    string Id, string Title, DateTimeOffset? From, DateTimeOffset? To, int Severity,
    IReadOnlyList<WindowsIncidentEvidence> Evidence,
    IReadOnlyList<string> Facts, IReadOnlyList<string> Hypotheses,
    IReadOnlyList<string> ReadOnlyNextSteps, string ConfidenceLabel);

public interface IWindowsIncidentCorrelator
{
    IReadOnlyList<WindowsIncident> Build(IReadOnlyList<WindowsEventItem> events);
}
