namespace TestAgent.Core;

public sealed record WindowsServiceQuery(string? Name = null, string? Filter = null, int MaxServices = 50);

// Deliberately excludes executable paths, service accounts, credentials and command lines.
public sealed record WindowsServiceItem(string Name, string DisplayName, string Status,
    string StartType, IReadOnlyList<string> Dependencies, bool DetailsUnavailable = false);

public sealed record WindowsServiceQueryResult(IReadOnlyList<WindowsServiceItem> Services,
    bool Truncated, DateTimeOffset QueriedAt, string? Notice = null);

public interface IWindowsServiceReader
{
    Task<WindowsServiceQueryResult> QueryAsync(WindowsServiceQuery query, CancellationToken ct = default);
}

public interface IWindowsServiceEvidenceBuilder
{
    string Build(WindowsServiceItem service, DateTimeOffset queriedAt);
}
