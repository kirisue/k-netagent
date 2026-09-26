using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TestAgent.Infrastructure;

public sealed record WorkspaceEntry(
    string Id,
    string Name,
    string Root,
    DateTimeOffset LastOpenedAt,
    int Version = 1);

public sealed record WorkspaceCatalogState(
    string? ActiveWorkspaceId,
    IReadOnlyList<WorkspaceEntry> Recent,
    int Version = 1);

public interface IWorkspaceCatalog
{
    Task<WorkspaceCatalogState> GetStateAsync(CancellationToken ct = default);
    Task<WorkspaceEntry> RegisterExistingAsync(string root, CancellationToken ct = default);
    Task<WorkspaceEntry> CreateAsync(string parentRoot, string projectName,
        CancellationToken ct = default);
    Task<WorkspaceEntry> ResolveStartupAsync(string? requestedRoot = null,
        string? fallbackRoot = null, CancellationToken ct = default);
    Task ForgetAsync(string workspaceId, CancellationToken ct = default);
}

/// <summary>
/// Persists only user-selected local workspace folders. Selecting a different entry is applied
/// on the next process start so every workspace-bound singleton is rebuilt against one root.
/// </summary>
public sealed class JsonWorkspaceCatalog : IWorkspaceCatalog
{
    public const int MaxRecentWorkspaces = 20;
    private const long MaxCatalogBytes = 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public JsonWorkspaceCatalog(AppPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _path = paths.Workspaces;
    }

    public async Task<WorkspaceCatalogState> GetStateAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try { return await LoadCoreAsync(ct); }
        finally { _gate.Release(); }
    }

    public async Task<WorkspaceEntry> RegisterExistingAsync(string root,
        CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var canonical = WorkspacePathPolicy.ValidateExisting(root);
            var state = await LoadCoreAsync(ct);
            var entry = WorkspacePathPolicy.Entry(canonical, DateTimeOffset.UtcNow);
            await SaveCoreAsync(Upsert(state, entry), ct);
            return entry;
        }
        finally { _gate.Release(); }
    }

    public async Task<WorkspaceEntry> CreateAsync(string parentRoot, string projectName,
        CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        string? created = null;
        try
        {
            WorkspacePathPolicy.ValidateProjectName(projectName);
            var parent = WorkspacePathPolicy.ValidateExisting(parentRoot);
            var target = Path.GetFullPath(Path.Combine(parent, projectName));
            WorkspacePathPolicy.EnsureDirectChild(parent, target);
            WorkspacePathPolicy.ValidateCandidate(target);
            if (Directory.Exists(target) || File.Exists(target))
                throw new IOException("A file or folder with this project name already exists. Open it instead of creating it again.");

            Directory.CreateDirectory(target);
            created = target;
            var canonical = WorkspacePathPolicy.ValidateExisting(target);
            var state = await LoadCoreAsync(ct);
            var entry = WorkspacePathPolicy.Entry(canonical, DateTimeOffset.UtcNow);
            await SaveCoreAsync(Upsert(state, entry), ct);
            return entry;
        }
        catch
        {
            if (created is not null)
            {
                try
                {
                    if (Directory.Exists(created) && !Directory.EnumerateFileSystemEntries(created).Any())
                        Directory.Delete(created, false);
                }
                catch { /* A newly created empty directory is safe to leave for explicit recovery. */ }
            }
            throw;
        }
        finally { _gate.Release(); }
    }

    public async Task<WorkspaceEntry> ResolveStartupAsync(string? requestedRoot = null,
        string? fallbackRoot = null, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var state = await LoadCoreAsync(ct);
            if (!string.IsNullOrWhiteSpace(requestedRoot))
            {
                var requested = WorkspacePathPolicy.Entry(
                    WorkspacePathPolicy.ValidateExisting(requestedRoot), DateTimeOffset.UtcNow);
                await SaveCoreAsync(Upsert(state, requested), ct);
                return requested;
            }

            var candidates = state.Recent
                .OrderByDescending(entry => entry.Id.Equals(state.ActiveWorkspaceId,
                    StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(entry => entry.LastOpenedAt)
                .ToArray();
            foreach (var candidate in candidates)
            {
                ct.ThrowIfCancellationRequested();
                if (!WorkspacePathPolicy.TryValidateExisting(candidate.Root, out var canonical)) continue;
                var resolved = WorkspacePathPolicy.Entry(canonical, DateTimeOffset.UtcNow);
                await SaveCoreAsync(Upsert(state, resolved), ct);
                return resolved;
            }

            var fallback = string.IsNullOrWhiteSpace(fallbackRoot)
                ? WorkspaceLocator.DiscoverDefaultRoot()
                : fallbackRoot;
            var defaultEntry = WorkspacePathPolicy.Entry(
                WorkspacePathPolicy.ValidateExisting(fallback), DateTimeOffset.UtcNow);
            await SaveCoreAsync(Upsert(state, defaultEntry), ct);
            return defaultEntry;
        }
        finally { _gate.Release(); }
    }

    public async Task ForgetAsync(string workspaceId, CancellationToken ct = default)
    {
        WorkspacePathPolicy.ValidateId(workspaceId);
        await _gate.WaitAsync(ct);
        try
        {
            var state = await LoadCoreAsync(ct);
            var recent = state.Recent.Where(entry =>
                    !entry.Id.Equals(workspaceId, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(entry => entry.LastOpenedAt)
                .Take(MaxRecentWorkspaces)
                .ToArray();
            var active = string.Equals(state.ActiveWorkspaceId, workspaceId,
                StringComparison.OrdinalIgnoreCase)
                ? recent.FirstOrDefault()?.Id
                : state.ActiveWorkspaceId;
            await SaveCoreAsync(new(active, recent), ct);
        }
        finally { _gate.Release(); }
    }

    private static WorkspaceCatalogState Upsert(WorkspaceCatalogState state, WorkspaceEntry entry)
    {
        var recent = state.Recent.Where(item =>
                !item.Id.Equals(entry.Id, StringComparison.OrdinalIgnoreCase) &&
                !item.Root.Equals(entry.Root, StringComparison.OrdinalIgnoreCase))
            .Prepend(entry)
            .OrderByDescending(item => item.LastOpenedAt)
            .Take(MaxRecentWorkspaces)
            .ToArray();
        return new(entry.Id, recent);
    }

    private async Task<WorkspaceCatalogState> LoadCoreAsync(CancellationToken ct)
    {
        if (!File.Exists(_path)) return Empty();
        try
        {
            var info = new FileInfo(_path);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("The workspace catalog cannot be a reparse point.");
            if (info.Length > MaxCatalogBytes)
                throw new InvalidDataException("The workspace catalog exceeds its size limit.");
            var state = JsonSerializer.Deserialize<WorkspaceCatalogState>(
                await File.ReadAllTextAsync(_path, ct), Json)
                ?? throw new InvalidDataException("The workspace catalog is empty.");
            return NormalizeState(state);
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or NotSupportedException)
        {
            Quarantine();
            return Empty();
        }
    }

    private async Task SaveCoreAsync(WorkspaceCatalogState state, CancellationToken ct)
    {
        state = NormalizeState(state);
        var directory = Path.GetDirectoryName(_path)
            ?? throw new InvalidOperationException("The workspace catalog has no parent directory.");
        Directory.CreateDirectory(directory);
        var temp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(state, Json),
                new UTF8Encoding(false), ct);
            File.Move(temp, _path, true);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); }
            catch { }
        }
    }

    private static WorkspaceCatalogState NormalizeState(WorkspaceCatalogState state)
    {
        if (state.Version != 1 || state.Recent is null)
            throw new InvalidDataException("The workspace catalog version is not supported.");
        if (state.Recent.Count > 1_000)
            throw new InvalidDataException("The workspace catalog contains too many entries.");

        var normalized = new List<WorkspaceEntry>();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in state.Recent)
        {
            if (value is null || value.Version != 1)
                throw new InvalidDataException("The workspace catalog contains an unsupported entry.");
            var root = WorkspacePathPolicy.ValidateStored(value.Root);
            var expectedId = WorkspacePathPolicy.CreateId(root);
            if (!expectedId.Equals(value.Id, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("A workspace catalog entry has an invalid identity.");
            if (!ids.Add(expectedId) || !roots.Add(root)) continue;
            normalized.Add(new(expectedId, WorkspacePathPolicy.DisplayName(root), root,
                value.LastOpenedAt == default ? DateTimeOffset.UnixEpoch : value.LastOpenedAt));
        }
        var recent = normalized.OrderByDescending(entry => entry.LastOpenedAt)
            .Take(MaxRecentWorkspaces).ToArray();

        var active = state.ActiveWorkspaceId;
        if (active is not null && !recent.Any(entry =>
                entry.Id.Equals(active, StringComparison.OrdinalIgnoreCase)))
            active = null;
        return new(active, recent, 1);
    }

    private void Quarantine()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var suffix = DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmssfff");
            File.Move(_path, _path + ".corrupt." + suffix, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static WorkspaceCatalogState Empty() => new(null, [], 1);
}

public sealed class WorkspaceLocator
{
    public WorkspaceLocator() : this(DiscoverDefaultRoot()) { }

    public WorkspaceLocator(string root)
    {
        if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root))
            throw new ArgumentException("An absolute workspace root is required.", nameof(root));
        Root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (!Directory.Exists(Root))
            throw new DirectoryNotFoundException($"Workspace root '{Root}' was not found.");
        if ((File.GetAttributes(Root) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("The workspace root cannot be a reparse point.");
        Id = WorkspacePathPolicy.CreateId(Root);
        Name = WorkspacePathPolicy.DisplayName(Root);
    }

    public string Root { get; }
    public string Id { get; }
    public string Name { get; }

    public static string DiscoverDefaultRoot() =>
        FindRepository(Environment.CurrentDirectory) ??
        FindRepository(AppContext.BaseDirectory) ??
        CreateInstalledWorkspace();

    private static string? FindRepository(string start)
    {
        if (string.IsNullOrWhiteSpace(start)) return null;
        for (var directory = new DirectoryInfo(Path.GetFullPath(start));
             directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "TestAgent.slnx")))
                return Path.TrimEndingDirectorySeparator(directory.FullName);
        return null;
    }

    private static string CreateInstalledWorkspace()
    {
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (string.IsNullOrWhiteSpace(documents))
        {
            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            documents = Path.Combine(profile, "Documents");
        }
        var workspace = Path.GetFullPath(Path.Combine(documents, "K.netagent Workspace"));
        Directory.CreateDirectory(workspace);
        return Path.TrimEndingDirectorySeparator(workspace);
    }
}

internal static class WorkspacePathPolicy
{
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    public static WorkspaceEntry Entry(string canonicalRoot, DateTimeOffset openedAt) =>
        new(CreateId(canonicalRoot), DisplayName(canonicalRoot), canonicalRoot, openedAt);

    public static string ValidateExisting(string root)
    {
        var canonical = ValidateCandidate(root);
        if (!Directory.Exists(canonical))
            throw new DirectoryNotFoundException($"Workspace folder '{canonical}' was not found.");
        EnsureNoReparsePoints(canonical);
        return canonical;
    }

    public static bool TryValidateExisting(string root, out string canonical)
    {
        try
        {
            canonical = ValidateExisting(root);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidDataException or IOException or
                                   UnauthorizedAccessException or NotSupportedException)
        {
            canonical = string.Empty;
            return false;
        }
    }

    public static string ValidateStored(string root)
    {
        var canonical = ValidateCandidate(root);
        if (Directory.Exists(canonical)) EnsureNoReparsePoints(canonical);
        return canonical;
    }

    public static string ValidateCandidate(string root)
    {
        if (string.IsNullOrWhiteSpace(root) || root.StartsWith("\\\\", StringComparison.Ordinal) ||
            !Path.IsPathFullyQualified(root))
            throw new InvalidDataException("Workspaces must be absolute local folders; UNC and relative paths are not allowed.");
        string canonical;
        try { canonical = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        { throw new InvalidDataException("The workspace path is invalid.", ex); }

        var pathRoot = Path.GetPathRoot(canonical);
        if (string.IsNullOrWhiteSpace(pathRoot) || pathRoot.StartsWith("\\\\", StringComparison.Ordinal))
            throw new InvalidDataException("UNC workspaces are not allowed.");
        if (SamePath(canonical, Path.TrimEndingDirectorySeparator(pathRoot)))
            throw new InvalidDataException("A drive root is too broad to use as an Agent workspace.");

        foreach (var blocked in RestrictedRoots())
            if (IsSameOrDescendant(canonical, blocked))
                throw new InvalidDataException("Windows, Program Files, and AppData folders cannot be Agent workspaces.");

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(profile) && SamePath(canonical,
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(profile))))
            throw new InvalidDataException("The whole user profile is too broad to use as an Agent workspace.");
        return canonical;
    }

    public static void ValidateProjectName(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 100 || value != value.Trim() ||
            value is "." or ".." || value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            value.Contains(Path.DirectorySeparatorChar) || value.Contains(Path.AltDirectorySeparatorChar) ||
            value.EndsWith('.') || value.EndsWith(' '))
            throw new InvalidDataException("Project name must be one valid Windows folder name of at most 100 characters.");
        if (ReservedNames.Contains(Path.GetFileNameWithoutExtension(value)))
            throw new InvalidDataException("The project name is reserved by Windows.");
    }

    public static void EnsureDirectChild(string parent, string target)
    {
        var relative = Path.GetRelativePath(parent, target);
        if (Path.IsPathFullyQualified(relative) || relative is "." or ".." ||
            relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal) ||
            relative.Contains(Path.DirectorySeparatorChar) || relative.Contains(Path.AltDirectorySeparatorChar))
            throw new InvalidDataException("The project folder must be a direct child of the selected parent.");
    }

    public static void ValidateId(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128 || value.Any(char.IsControl))
            throw new ArgumentException("Workspace ID is invalid.", nameof(value));
    }

    public static string CreateId(string root)
    {
        var canonical = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)).ToUpperInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
        return "WS-" + hash[..32];
    }

    public static string DisplayName(string root)
    {
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(root));
        return string.IsNullOrWhiteSpace(name) ? root : name;
    }

    private static IEnumerable<string> RestrictedRoots()
    {
        var folders = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
        };
        return folders.Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => Path.TrimEndingDirectorySeparator(Path.GetFullPath(value)))
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static void EnsureNoReparsePoints(string path)
    {
        for (var directory = new DirectoryInfo(path); directory is not null; directory = directory.Parent)
        {
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Workspace paths cannot traverse symbolic links or junctions.");
        }
    }

    private static bool IsSameOrDescendant(string candidate, string parent) =>
        SamePath(candidate, parent) || candidate.StartsWith(
            parent.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
            Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static bool SamePath(string left, string right) =>
        left.Equals(right, StringComparison.OrdinalIgnoreCase);
}
