using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using TestAgent.Core;

namespace TestAgent.Infrastructure;

public sealed class MarkdownIterationGuideStore(WorkspaceLocator workspace) : IIterationGuideStore
{
    public async Task<IReadOnlyList<IterationGuide>> ListAsync(CancellationToken ct = default)
    {
        var directory = Path.Combine(workspace.Root, "iteration-guides");
        if (!Directory.Exists(directory)) return [];
        var result = new List<IterationGuide>();
        foreach (var path in Directory.EnumerateFiles(directory, "*.md").Where(x => !Path.GetFileName(x).Equals("README.md", StringComparison.OrdinalIgnoreCase)))
        {
            var content = await File.ReadAllTextAsync(path, ct);
            var title = Regex.Match(content, @"(?m)^#\s+(.+)$").Groups[1].Value.Trim();
            var goal = Regex.Match(content, @"(?mi)^Goal:\s*(.+)$").Groups[1].Value.Trim();
            var lines = content.Replace("\r\n", "\n").Split('\n');
            var targetStart = Array.FindIndex(lines, x => x.Trim().Equals("Targets:", StringComparison.OrdinalIgnoreCase));
            var targets = targetStart < 0 ? [] : lines.Skip(targetStart + 1)
                .TakeWhile(x => x.TrimStart().StartsWith("- ", StringComparison.Ordinal))
                .Select(x => x.Trim()[2..].Trim().Replace('\\', '/')).Where(x => x.Length > 0).ToArray();
            if (!string.IsNullOrWhiteSpace(title) && !string.IsNullOrWhiteSpace(goal) && targets.Length > 0)
                result.Add(new(Path.GetFileNameWithoutExtension(path), title, goal, targets, content));
        }
        return result.OrderBy(x => x.Title).ToArray();
    }
}

public sealed class CodeIterationService(IModelProvider provider, WorkspaceLocator workspace) : ICodeIterationService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly string[] DeniedParts = [".git", "bin", "obj", "dist", "build", ".claude", "memory"];

    public async Task<IterationProposal> GenerateAsync(IterationGuide guide, ProviderSettings settings, string? apiKey, CancellationToken ct = default)
    {
        var targets = guide.Targets.Select(ResolveTarget).ToArray();
        var prompt = new StringBuilder();
        prompt.AppendLine("You are proposing a guarded change to a .NET 10 WPF application.");
        prompt.AppendLine("Return JSON only with this shape: {\"summary\":\"...\",\"changes\":[{\"path\":\"relative/path\",\"content\":\"complete new file content\"}]}.");
        prompt.AppendLine("Only change the explicitly allowed target paths. Return complete file contents, not patches. Preserve unrelated behavior.");
        prompt.AppendLine("The proposal will be built and tested in an isolated copy before a human may approve it.");
        prompt.AppendLine("\nITERATION GUIDE:\n" + guide.Content);
        foreach (var target in targets)
        {
            prompt.AppendLine($"\n--- TARGET: {target.Relative} ---");
            prompt.AppendLine(target.Exists ? await File.ReadAllTextAsync(target.Absolute, ct) : "<NEW FILE>");
        }
        if (prompt.Length > 180_000) throw new InvalidOperationException("Iteration guide context is too large; split it into smaller target sets.");
        var response = new StringBuilder();
        var messages = new[] { new ChatMessage(ChatRole.System, "Produce safe, minimal, buildable code changes. Do not use Markdown fences.", DateTimeOffset.UtcNow), new ChatMessage(ChatRole.User, prompt.ToString(), DateTimeOffset.UtcNow) };
        await foreach (var item in provider.StreamAsync(new(messages, settings with { MaxOutputTokens = Math.Max(settings.MaxOutputTokens, 8192), SelfReviewEnabled = false }, apiKey), ct))
            if (item.Kind == StreamEventKind.Content) response.Append(item.Text);
        var dto = JsonSerializer.Deserialize<ProposalDto>(ExtractJson(response.ToString()), Json)
            ?? throw new InvalidDataException("The model did not return a valid iteration proposal.");
        if (dto.Changes is null || dto.Changes.Count == 0) throw new InvalidDataException("The proposal contains no file changes.");
        var allowed = targets.ToDictionary(x => x.Relative, StringComparer.OrdinalIgnoreCase);
        var changes = new List<ProposedFileChange>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var change in dto.Changes)
        {
            var relative = Normalize(change.Path);
            if (!allowed.TryGetValue(relative, out var target)) throw new InvalidDataException($"Model attempted a non-whitelisted path: {relative}");
            if (!seen.Add(relative)) throw new InvalidDataException($"Model returned the same target more than once: {relative}");
            if (string.IsNullOrWhiteSpace(change.Content)) throw new InvalidDataException($"Empty file content is not allowed: {relative}");
            var originalContent = target.Exists ? await File.ReadAllTextAsync(target.Absolute, ct) : string.Empty;
            changes.Add(new(relative, target.Exists ? Sha256(await File.ReadAllBytesAsync(target.Absolute, ct)) : "NEW", originalContent, change.Content));
        }
        var validation = await ValidateInSandboxAsync(changes, ct);
        return new($"ITER-{Guid.NewGuid():N}", guide.Id, guide.Title, dto.Summary ?? guide.Goal, changes, validation, DateTimeOffset.UtcNow);
    }

    public async Task<IterationApplyResult> ApplyAsync(IterationProposal proposal, CancellationToken ct = default)
    {
        if (!proposal.Validation.Success) return new(false, false, "Proposal validation failed; it cannot be applied.", proposal.Validation.BuildOutput + proposal.Validation.TestOutput);
        var originals = new Dictionary<string, byte[]?>();
        foreach (var change in proposal.Changes)
        {
            var target = ResolveTarget(change.Path);
            var current = target.Exists ? Sha256(await File.ReadAllBytesAsync(target.Absolute, ct)) : "NEW";
            if (!current.Equals(change.OriginalSha256, StringComparison.OrdinalIgnoreCase))
                return new(false, false, $"File changed after proposal generation: {change.Path}", "Hash conflict. Generate a new proposal.");
            originals[target.Absolute] = target.Exists ? await File.ReadAllBytesAsync(target.Absolute, ct) : null;
        }
        try
        {
            foreach (var change in proposal.Changes)
            {
                var target = ResolveTarget(change.Path); Directory.CreateDirectory(Path.GetDirectoryName(target.Absolute)!);
                await File.WriteAllTextAsync(target.Absolute, change.NewContent, new UTF8Encoding(false), ct);
            }
            var validation = await ValidateWorkspaceAsync(workspace.Root, ct);
            if (!validation.Success) throw new ValidationException(validation.BuildOutput + Environment.NewLine + validation.TestOutput);
            await WriteIterationRecordAsync(proposal, ct);
            return new(true, false, "Iteration applied and verified.", validation.BuildOutput + Environment.NewLine + validation.TestOutput);
        }
        catch (Exception ex)
        {
            foreach (var item in originals)
            {
                if (item.Value is null) { if (File.Exists(item.Key)) File.Delete(item.Key); }
                else await File.WriteAllBytesAsync(item.Key, item.Value, CancellationToken.None);
            }
            return new(false, true, "Validation failed; all proposed file changes were rolled back.", ex.Message);
        }
    }

    private async Task<IterationValidation> ValidateInSandboxAsync(IReadOnlyList<ProposedFileChange> changes, CancellationToken ct)
    {
        var sandbox = Path.Combine(Path.GetTempPath(), "TestAgent-iteration-" + Guid.NewGuid().ToString("N"));
        try
        {
            CopyWorkspace(workspace.Root, sandbox);
            foreach (var change in changes) { var path = Path.Combine(sandbox, change.Path.Replace('/', Path.DirectorySeparatorChar)); Directory.CreateDirectory(Path.GetDirectoryName(path)!); await File.WriteAllTextAsync(path, change.NewContent, new UTF8Encoding(false), ct); }
            return await ValidateWorkspaceAsync(sandbox, ct);
        }
        finally { try { if (Directory.Exists(sandbox)) Directory.Delete(sandbox, true); } catch { } }
    }

    private static async Task<IterationValidation> ValidateWorkspaceAsync(string root, CancellationToken ct)
    {
        var output = Path.Combine(Path.GetTempPath(), "TestAgent-validation-output-" + Guid.NewGuid().ToString("N")) + Path.DirectorySeparatorChar;
        try
        {
            var build = await RunAsync("dotnet", $"build TestAgent.slnx --nologo -p:BaseOutputPath={output}", root, TimeSpan.FromMinutes(3), ct);
            if (build.ExitCode != 0) return new(false, build.Output, "Tests skipped because build failed.", DateTimeOffset.UtcNow);
            var test = await RunAsync("dotnet", $"test tests/TestAgent.Tests/TestAgent.Tests.csproj --no-build --nologo -p:BaseOutputPath={output}", root, TimeSpan.FromMinutes(3), ct);
            return new(test.ExitCode == 0, build.Output, test.Output, DateTimeOffset.UtcNow);
        }
        finally { try { if (Directory.Exists(output)) Directory.Delete(output, true); } catch { } }
    }

    private async Task WriteIterationRecordAsync(IterationProposal proposal, CancellationToken ct)
    {
        var now = DateTimeOffset.Now; var directory = Path.Combine(workspace.Root, "iterations"); Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"{now:yyyy-MM-dd}.md");
        var text = $"\n## {now:HH:mm} - {proposal.Title}\n\n**摘要**: {proposal.Summary}\n\n**变更文件**:\n{string.Join("\n", proposal.Changes.Select(x => $"- `{x.Path}`"))}\n\n**验证**: `dotnet build` 与 `dotnet test` 通过\n\n**提案 ID**: `{proposal.Id}`\n";
        if (!File.Exists(path)) text = $"# 迭代记录 - {now:yyyy-MM-dd}\n" + text;
        await File.AppendAllTextAsync(path, text, new UTF8Encoding(false), ct);
    }

    private Target ResolveTarget(string relative)
    {
        relative = Normalize(relative);
        if (Path.IsPathRooted(relative) || relative.Split('/').Any(x => x == ".." || DeniedParts.Contains(x, StringComparer.OrdinalIgnoreCase))) throw new InvalidDataException($"Unsafe iteration target: {relative}");
        var extension = Path.GetExtension(relative); if (extension is not (".cs" or ".xaml" or ".md")) throw new InvalidDataException($"Unsupported target type: {relative}");
        var absolute = Path.GetFullPath(Path.Combine(workspace.Root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!absolute.StartsWith(workspace.Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"Target escapes workspace: {relative}");
        WorkspaceTool.EnsureSafeWorkspacePath(workspace.Root, absolute);
        return new(relative, absolute, File.Exists(absolute));
    }
    private static string Normalize(string value) => value.Trim().Replace('\\', '/').TrimStart('/');
    private static string ExtractJson(string value) { var start = value.IndexOf('{'); var end = value.LastIndexOf('}'); if (start < 0 || end <= start) throw new InvalidDataException("No JSON object found in model response."); return value[start..(end + 1)]; }
    private static string Sha256(byte[] value) => Convert.ToHexString(SHA256.HashData(value));
    private static void CopyWorkspace(string source, string target)
    {
        Directory.CreateDirectory(target);
        var pending = new Stack<string>();
        pending.Push(source);
        var copiedFiles = 0;
        long copiedBytes = 0;
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var child in Directory.EnumerateDirectories(directory, "*", SearchOption.TopDirectoryOnly))
            {
                var relative = Path.GetRelativePath(source, child);
                var parts = relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                    StringSplitOptions.RemoveEmptyEntries);
                if (parts.Any(part => DeniedParts.Contains(part, StringComparer.OrdinalIgnoreCase) ||
                                      WorkspaceTool.IsUnavailablePart(part))) continue;
                WorkspaceTool.EnsureSafeWorkspacePath(source, child);
                pending.Push(child);
            }
            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly))
            {
                if (WorkspaceTool.IsSensitiveFileName(Path.GetFileName(file))) continue;
                WorkspaceTool.EnsureSafeWorkspacePath(source, file);
                var relative = Path.GetRelativePath(source, file);
                var info = new FileInfo(file);
                copiedFiles++;
                copiedBytes += info.Length;
                if (copiedFiles > 20_000 || copiedBytes > 512L * 1024 * 1024)
                    throw new InvalidDataException("The self-iteration workspace exceeds the bounded copy budget.");
                var destination = Path.Combine(target, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(file, destination, true);
            }
        }
    }
    private static async Task<ProcessResult> RunAsync(string file, string arguments, string cwd, TimeSpan timeout, CancellationToken ct)
    {
        using var process = new Process { StartInfo = new(file, arguments) { WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true } };
        process.Start(); var stdout = process.StandardOutput.ReadToEndAsync(ct); var stderr = process.StandardError.ReadToEndAsync(ct);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct); linked.CancelAfter(timeout);
        try { await process.WaitForExitAsync(linked.Token); }
        catch (OperationCanceledException) { try { process.Kill(true); } catch { } throw; }
        return new(process.ExitCode, (await stdout) + Environment.NewLine + (await stderr));
    }
    private sealed record Target(string Relative, string Absolute, bool Exists);
    private sealed record ProposalDto(string? Summary, List<ChangeDto>? Changes);
    private sealed record ChangeDto(string Path, string Content);
    private sealed record ProcessResult(int ExitCode, string Output);
    private sealed class ValidationException(string message) : Exception(message);
}
