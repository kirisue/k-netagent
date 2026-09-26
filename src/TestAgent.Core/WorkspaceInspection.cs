namespace TestAgent.Core;

/// <summary>A bounded, metadata-only view of one workspace entry.</summary>
public sealed record WorkspaceEntryInfo(
    string RelativePath,
    string Name,
    bool IsDirectory,
    int Depth,
    long? Size = null);

public sealed record WorkspaceTreeSnapshot(
    IReadOnlyList<WorkspaceEntryInfo> Entries,
    bool Truncated,
    int SkippedEntryCount,
    DateTimeOffset ScannedAt);

public interface IWorkspaceExplorer
{
    Task<WorkspaceTreeSnapshot> ScanAsync(CancellationToken cancellationToken = default);
    Task<string> ReadPreviewAsync(string relativePath, CancellationToken cancellationToken = default);
}

public enum WorkspaceChangeSource
{
    Git,
    ApprovedToolResults,
    None
}

/// <summary>
/// A real change observed from Git or an approved tool result. Patch is null when the fallback
/// can prove the modified path but has no trustworthy before-image from which to build a diff.
/// </summary>
public sealed record WorkspaceChangedFile(
    string RelativePath,
    string Status,
    string? Patch,
    bool PatchTruncated = false,
    string? PatchUnavailableReason = null);

public sealed record WorkspaceChangeSnapshot(
    WorkspaceChangeSource Source,
    IReadOnlyList<WorkspaceChangedFile> Files,
    bool Truncated,
    string Summary,
    DateTimeOffset ObservedAt);

public interface IWorkspaceChangeSource
{
    Task<WorkspaceChangeSnapshot> GetChangesAsync(string? taskGraphId = null,
        CancellationToken cancellationToken = default);
}
