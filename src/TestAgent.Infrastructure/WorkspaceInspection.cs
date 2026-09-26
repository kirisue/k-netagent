using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using TestAgent.Core;

namespace TestAgent.Infrastructure;

public sealed class BoundedWorkspaceExplorer(WorkspaceLocator workspace) : IWorkspaceExplorer
{
    public const int MaxEntries = 2_000;
    public const int MaxDepth = 6;
    public const int MaxPreviewBytes = 128 * 1024;
    private static readonly HashSet<string> SkippedGeneratedDirectories = new(StringComparer.OrdinalIgnoreCase)
    { ".vs", ".idea", "node_modules", "dist", "build", ".venv", "venv" };

    public Task<WorkspaceTreeSnapshot> ScanAsync(CancellationToken ct = default)
    {
        var entries = new List<WorkspaceEntryInfo>();
        var queue = new Queue<(string Path, int Depth)>();
        var skipped = 0;
        queue.Enqueue((workspace.Root, 0));
        while (queue.Count > 0 && entries.Count < MaxEntries)
        {
            ct.ThrowIfCancellationRequested();
            var (directory, depth) = queue.Dequeue();
            IEnumerable<string> children;
            try { children = Directory.EnumerateFileSystemEntries(directory).OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase).ToArray(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { skipped++; continue; }
            foreach (var path in children)
            {
                ct.ThrowIfCancellationRequested();
                if (entries.Count >= MaxEntries) { skipped++; break; }
                var name = Path.GetFileName(path);
                try { WorkspaceTool.EnsureSafeWorkspacePath(workspace.Root, path); }
                catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
                { skipped++; continue; }
                bool isDirectory;
                FileAttributes attributes;
                try { attributes = File.GetAttributes(path); isDirectory = (attributes & FileAttributes.Directory) != 0; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { skipped++; continue; }
                if ((attributes & FileAttributes.ReparsePoint) != 0 ||
                    isDirectory && SkippedGeneratedDirectories.Contains(name)) { skipped++; continue; }
                var relative = Relative(path);
                long? size = null;
                if (!isDirectory) try { size = new FileInfo(path).Length; } catch { }
                entries.Add(new(relative, name, isDirectory, depth, size));
                if (isDirectory)
                {
                    if (depth < MaxDepth) queue.Enqueue((path, depth + 1));
                    else skipped++;
                }
            }
        }
        return Task.FromResult(new WorkspaceTreeSnapshot(entries, queue.Count > 0 || entries.Count >= MaxEntries,
            skipped, DateTimeOffset.UtcNow));
    }

    public async Task<string> ReadPreviewAsync(string relativePath, CancellationToken ct = default)
    {
        var path = ResolveFile(relativePath);
        var info = new FileInfo(path);
        if (info.Length > MaxPreviewBytes) throw new InvalidDataException($"File preview exceeds {MaxPreviewBytes:N0} bytes.");
        var bytes = await File.ReadAllBytesAsync(path, ct);
        if (bytes.AsSpan().IndexOf((byte)0) >= 0) throw new InvalidDataException("Binary files cannot be previewed as text.");
        return new UTF8Encoding(false, true).GetString(bytes);
    }

    private string ResolveFile(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathFullyQualified(relativePath))
            throw new InvalidDataException("A workspace-relative file path is required.");
        var full = Path.GetFullPath(Path.Combine(workspace.Root, relativePath));
        var prefix = workspace.Root + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !File.Exists(full))
            throw new FileNotFoundException("The requested file is outside the workspace or no longer exists.");
        WorkspaceTool.EnsureSafeWorkspacePath(workspace.Root, full);
        return full;
    }

    private string Relative(string path) => Path.GetRelativePath(workspace.Root, path).Replace('\\', '/');
}

public sealed class GitOrApprovedToolWorkspaceChangeSource(
    WorkspaceLocator workspace,
    IToolSessionCoordinator toolSessions) : IWorkspaceChangeSource
{
    public const int MaxFiles = 100;
    public const int MaxPatchCharacters = 64 * 1024;
    public const int MaxTotalPatchCharacters = 256 * 1024;

    public async Task<WorkspaceChangeSnapshot> GetChangesAsync(string? taskGraphId = null,
        CancellationToken ct = default)
    {
        var git = await TryGitAsync(ct);
        if (git is not null) return git;
        if (string.IsNullOrWhiteSpace(taskGraphId))
            return Empty("Git is unavailable and no task run was selected for approved-tool evidence.");

        var paths = (await toolSessions.ListAsync(taskGraphId, ct))
            .SelectMany(session => session.RecentInvocations)
            .Where(invocation => invocation.Approved && invocation.Status == ToolExecutionStatus.Success)
            .SelectMany(invocation => invocation.ModifiedFiles ?? [])
            .Select(NormalizeSafePath)
            .Where(path => path is not null)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxFiles + 1).ToArray();
        var truncated = paths.Length > MaxFiles;
        var files = paths.Take(MaxFiles).Select(path => new WorkspaceChangedFile(path!, "M", null,
            PatchUnavailableReason: "The approved tool result proves this path was modified, but no trusted before-image is available.")).ToArray();
        return new(WorkspaceChangeSource.ApprovedToolResults, files, truncated,
            files.Length == 0 ? "No approved modified-file evidence was recorded for this task." :
            $"{files.Length} path(s) reported by approved successful tool calls.", DateTimeOffset.UtcNow);
    }

    private async Task<WorkspaceChangeSnapshot?> TryGitAsync(CancellationToken ct)
    {
        var top = await RunGitAsync(["rev-parse", "--show-toplevel"], ct, 4_096);
        if (!top.Started || top.ExitCode != 0) return null;
        string repositoryRoot;
        try { repositoryRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(top.Output.Trim())); }
        catch { return null; }
        if (!repositoryRoot.Equals(workspace.Root, StringComparison.OrdinalIgnoreCase)) return null;

        var status = await RunGitAsync(["status", "--porcelain=v1", "-z", "--untracked-files=all", "--", "."], ct, 512 * 1024);
        if (status.ExitCode != 0) return null;
        var statusItems = ParseStatus(status.Output).Take(MaxFiles + 1).ToArray();
        var truncated = statusItems.Length > MaxFiles;
        var files = new List<WorkspaceChangedFile>();
        var total = 0;
        foreach (var item in statusItems.Take(MaxFiles))
        {
            ct.ThrowIfCancellationRequested();
            var patch = await GetPatchAsync(item.Path, item.Code, ct);
            var patchTruncated = false;
            if (patch.Length > MaxPatchCharacters) { patch = patch[..MaxPatchCharacters] + "\n… diff truncated …"; patchTruncated = true; }
            if (total + patch.Length > MaxTotalPatchCharacters)
            {
                var remaining = Math.Max(0, MaxTotalPatchCharacters - total);
                patch = remaining == 0 ? "" : patch[..Math.Min(remaining, patch.Length)] + "\n… total diff budget reached …";
                patchTruncated = true; truncated = true;
            }
            total += patch.Length;
            files.Add(new(item.Path, item.Code, string.IsNullOrWhiteSpace(patch) ? null : patch,
                patchTruncated, string.IsNullOrWhiteSpace(patch) ? "Git reported the path but returned no textual patch (for example, a binary file)." : null));
        }
        return new(WorkspaceChangeSource.Git, files, truncated,
            files.Count == 0 ? "Git reports a clean workspace." : $"Git reports {files.Count} changed path(s).",
            DateTimeOffset.UtcNow);
    }

    private async Task<string> GetPatchAsync(string relativePath, string code, CancellationToken ct)
    {
        var builder = new StringBuilder();
        if (code.Length > 0 && code[0] != ' ' && code[0] != '?')
        {
            var staged = await RunGitAsync(["--no-pager", "diff", "--cached", "--no-ext-diff", "--no-textconv", "--unified=3", "--", relativePath], ct, MaxPatchCharacters + 1);
            if (staged.ExitCode == 0) builder.Append(staged.Output);
        }
        if (code.Length > 1 && code[1] != ' ' && code[1] != '?')
        {
            var unstaged = await RunGitAsync(["--no-pager", "diff", "--no-ext-diff", "--no-textconv", "--unified=3", "--", relativePath], ct, MaxPatchCharacters + 1);
            if (unstaged.ExitCode == 0) builder.Append(unstaged.Output);
        }
        if (code == "??")
        {
            var untracked = await RunGitAsync(["--no-pager", "diff", "--no-index", "--no-ext-diff", "--no-textconv", "--unified=3", "--", "/dev/null", relativePath], ct, MaxPatchCharacters + 1);
            if (untracked.ExitCode is 0 or 1) builder.Append(untracked.Output);
        }
        return builder.ToString();
    }

    private IEnumerable<(string Code, string Path)> ParseStatus(string output)
    {
        var parts = output.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        for (var index = 0; index < parts.Length; index++)
        {
            var entry = parts[index];
            if (entry.Length < 4) continue;
            var code = entry[..2];
            var path = NormalizeSafePath(entry[3..]);
            string? sourcePath = null;
            if ((code[0] is 'R' or 'C' || code[1] is 'R' or 'C') && index + 1 < parts.Length)
                sourcePath = NormalizeSafePath(parts[++index]);
            if (sourcePath is null && (code[0] is 'R' or 'C' || code[1] is 'R' or 'C')) continue;
            if (path is not null) yield return (code, path);
        }
    }

    private string? NormalizeSafePath(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || Path.IsPathFullyQualified(value)) return null;
        var full = Path.GetFullPath(Path.Combine(workspace.Root, value));
        if (!full.StartsWith(workspace.Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return null;
        try { WorkspaceTool.EnsureSafeWorkspacePath(workspace.Root, full); }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException) { return null; }
        return Path.GetRelativePath(workspace.Root, full).Replace('\\', '/');
    }

    private async Task<GitResult> RunGitAsync(IReadOnlyList<string> arguments, CancellationToken ct,
        int maxOutputCharacters = 128 * 1024)
    {
        string executable;
        try { executable = TrustedDeveloperExecutable.Resolve("git", workspace.Root); }
        catch (InvalidDataException) { return new(false, -1, ""); }
        using var process = new Process { StartInfo = new ProcessStartInfo
        {
            FileName = executable, WorkingDirectory = workspace.Root, UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        } };
        process.StartInfo.ArgumentList.Add("-c"); process.StartInfo.ArgumentList.Add("core.fsmonitor=false");
        process.StartInfo.ArgumentList.Add("-c"); process.StartInfo.ArgumentList.Add("core.hooksPath=NUL");
        process.StartInfo.ArgumentList.Add("-c"); process.StartInfo.ArgumentList.Add("core.pager=cat");
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        TrustedDeveloperExecutable.ApplySafeEnvironment(process.StartInfo);
        process.StartInfo.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        process.StartInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
        process.StartInfo.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        try
        {
            if (!process.Start()) return new(false, -1, "");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            var outputTask = DrainBoundedAsync(process.StandardOutput, maxOutputCharacters, timeout.Token);
            var errorTask = DrainBoundedAsync(process.StandardError, 16_384, timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            var output = await outputTask;
            _ = await errorTask;
            return new(true, process.ExitCode, output);
        }
        catch (Win32Exception) { return new(false, -1, ""); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try { process.Kill(true); } catch { }
            return new(true, -1, "");
        }
    }

    private static async Task<string> DrainBoundedAsync(StreamReader reader, int maxCharacters,
        CancellationToken ct)
    {
        var result = new StringBuilder(Math.Min(maxCharacters, 4_096));
        var buffer = new char[4_096];
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), ct);
            if (read == 0) break;
            var remaining = maxCharacters - result.Length;
            if (remaining > 0) result.Append(buffer, 0, Math.Min(remaining, read));
        }
        return result.ToString();
    }

    private static WorkspaceChangeSnapshot Empty(string summary) =>
        new(WorkspaceChangeSource.None, [], false, summary, DateTimeOffset.UtcNow);
    private sealed record GitResult(bool Started, int ExitCode, string Output);
}
