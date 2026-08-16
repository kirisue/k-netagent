using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TestAgent.Core;

namespace TestAgent.Infrastructure;

public sealed class AppPaths
{
    public string Root { get; }
    public AppPaths() : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TestAgent")) { }
    public AppPaths(string root) => Root = Path.GetFullPath(root);
    public string Sessions => Path.Combine(Root, "sessions"); public string Memory => Path.Combine(Root, "memory");
    public string Config => Path.Combine(Root, "config.json"); public string Secrets => Path.Combine(Root, "secrets");
    public string Audit => Path.Combine(Root, "audit");
    public string Tasks => Path.Combine(Root, "tasks");
    public string ToolSessions => Path.Combine(Root, "tool-sessions");
}

public abstract class JsonDirectoryStore<T>(string directory)
{
    private readonly SemaphoreSlim _gate = new(1, 1); protected readonly string DirectoryPath = directory;
    protected static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    protected async Task<IReadOnlyList<T>> ListFiles(CancellationToken ct) { Directory.CreateDirectory(DirectoryPath); var list = new List<T>(); foreach (var file in Directory.EnumerateFiles(DirectoryPath, "*.json")) try { var item = JsonSerializer.Deserialize<T>(await File.ReadAllTextAsync(file, ct), Json); if (item is not null) list.Add(item); } catch (JsonException) { File.Move(file, file + ".corrupt", true); } return list; }
    protected async Task SaveFile(string id, T item, CancellationToken ct) { Directory.CreateDirectory(DirectoryPath); await _gate.WaitAsync(ct); try { var path = Path.Combine(DirectoryPath, id + ".json"); var temp = path + ".tmp"; await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(item, Json), ct); File.Move(temp, path, true); } finally { _gate.Release(); } }
    protected Task DeleteFile(string id) { var path = Path.Combine(DirectoryPath, id + ".json"); if (File.Exists(path)) File.Delete(path); return Task.CompletedTask; }
}
public sealed class JsonMemoryStore(AppPaths paths) : JsonDirectoryStore<MemoryEntry>(paths.Memory), IMemoryStore
{ public async Task<IReadOnlyList<MemoryEntry>> ListAsync(CancellationToken ct = default) => (await ListFiles(ct)).OrderByDescending(x => x.UpdatedAt).ToArray(); public Task SaveAsync(MemoryEntry x, CancellationToken ct = default) => SaveFile(x.Id, x, ct); public Task DeleteAsync(string id, CancellationToken ct = default) => DeleteFile(id); }
public sealed class JsonSessionStore(AppPaths paths) : JsonDirectoryStore<ChatSession>(paths.Sessions), ISessionStore
{ public async Task<IReadOnlyList<ChatSession>> ListAsync(CancellationToken ct = default) => (await ListFiles(ct)).OrderByDescending(x => x.UpdatedAt).ToArray(); public async Task<ChatSession?> GetAsync(string id, CancellationToken ct = default) => (await ListFiles(ct)).FirstOrDefault(x => x.Id == id); public Task SaveAsync(ChatSession x, CancellationToken ct = default) => SaveFile(x.Id, x, ct); public Task DeleteAsync(string id, CancellationToken ct = default) => DeleteFile(id); }

public sealed class SessionHistorySearch(ISessionStore sessions) : ISessionHistorySearch
{
    public async Task<IReadOnlyList<SessionSearchHit>> SearchAsync(string query, string? excludeSessionId = null,
        int maxResults = 20, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query)) throw new ArgumentException("Search query is required.", nameof(query));
        var terms = query.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(8).ToArray();
        if (terms.Length == 0) return [];
        var hits = new List<(int Score, SessionSearchHit Hit)>();
        foreach (var session in await sessions.ListAsync(ct))
        {
            ct.ThrowIfCancellationRequested();
            if (!string.IsNullOrWhiteSpace(excludeSessionId) && session.Id.Equals(excludeSessionId, StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var message in session.Messages.Where(x => x.Role is ChatRole.User or ChatRole.Assistant))
            {
                var score = terms.Count(term => message.Content.Contains(term, StringComparison.OrdinalIgnoreCase));
                if (score == 0) continue;
                var first = terms.Select(term => message.Content.IndexOf(term, StringComparison.OrdinalIgnoreCase)).Where(index => index >= 0).DefaultIfEmpty(0).Min();
                var start = Math.Max(0, first - 160); var length = Math.Min(700, message.Content.Length - start);
                var snippet = SensitiveDataRedactor.Text(message.Content.Substring(start, length).Replace('\0', ' '), 700);
                hits.Add((score, new(session.Id, SensitiveDataRedactor.Text(session.Title, 160), message.Role, snippet, message.CreatedAt)));
            }
        }
        return hits.OrderByDescending(x => x.Score).ThenByDescending(x => x.Hit.Timestamp).Take(Math.Clamp(maxResults, 1, 50)).Select(x => x.Hit).ToArray();
    }
}

public sealed class JsonToolSessionStore(AppPaths paths) : IToolSessionStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly SemaphoreSlim _gate = new(1, 1);
    public async Task<IReadOnlyList<ToolSession>> ListAsync(string? parentSessionId = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(paths.ToolSessions); var result = new List<ToolSession>();
        foreach (var file in Directory.EnumerateFiles(paths.ToolSessions, "*.json"))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var value = JsonSerializer.Deserialize<ToolSession>(await File.ReadAllTextAsync(file, ct), Json);
                if (value is not null && (parentSessionId is null || value.ParentSessionId.Equals(parentSessionId, StringComparison.OrdinalIgnoreCase))) result.Add(value);
            }
            catch (JsonException) { File.Move(file, file + ".corrupt", true); }
        }
        return result.OrderByDescending(x => x.UpdatedAt).ToArray();
    }
    public async Task<ToolSession?> GetAsync(string parentSessionId, string toolName, CancellationToken ct = default)
    {
        var id = ToolSessionCoordinator.BuildId(parentSessionId, toolName); var path = Path.Combine(paths.ToolSessions, Safe(id) + ".json");
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<ToolSession>(await File.ReadAllTextAsync(path, ct), Json); }
        catch (JsonException) { File.Move(path, path + ".corrupt", true); return null; }
    }
    public async Task SaveAsync(ToolSession session, CancellationToken ct = default)
    {
        Directory.CreateDirectory(paths.ToolSessions); var path = Path.Combine(paths.ToolSessions, Safe(session.Id) + ".json");
        await _gate.WaitAsync(ct);
        try
        {
            var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try { await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(session, Json), new UTF8Encoding(false), ct); File.Move(temp, path, true); }
            finally { try { if (File.Exists(temp)) File.Delete(temp); } catch { } }
        }
        finally { _gate.Release(); }
    }
    private static string Safe(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}

public sealed class JsonTaskPlanStore(AppPaths paths) : ITaskPlanStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string PlanPath(string id) => Path.Combine(paths.Tasks, SafeId(id) + ".plan.json");
    private string CheckpointPath(string id) => Path.Combine(paths.Tasks, SafeId(id) + ".checkpoint.json");

    public async Task<IReadOnlyList<TaskGraphPlan>> ListPlansAsync(CancellationToken ct = default)
    {
        Directory.CreateDirectory(paths.Tasks); var result = new List<TaskGraphPlan>();
        foreach (var file in Directory.EnumerateFiles(paths.Tasks, "*.plan.json"))
        {
            ct.ThrowIfCancellationRequested();
            try { var value = JsonSerializer.Deserialize<TaskGraphPlan>(await File.ReadAllTextAsync(file, ct), Json); if (value is not null) result.Add(value); }
            catch (JsonException) { Quarantine(file); }
        }
        return result.OrderByDescending(x => File.GetLastWriteTimeUtc(PlanPath(x.Id))).ToArray();
    }

    public async Task<TaskGraphPlan?> LoadPlanAsync(string graphId, CancellationToken ct = default) =>
        await Load<TaskGraphPlan>(PlanPath(graphId), ct);
    public async Task<TaskGraphCheckpoint?> LoadAsync(string graphId, CancellationToken cancellationToken = default) =>
        await Load<TaskGraphCheckpoint>(CheckpointPath(graphId), cancellationToken);
    public Task SavePlanAsync(TaskGraphPlan plan, CancellationToken ct = default) => Save(PlanPath(plan.Id), plan, ct);
    public Task SaveAsync(TaskGraphCheckpoint checkpoint, CancellationToken ct = default) => Save(CheckpointPath(checkpoint.GraphId), checkpoint, ct);

    private async Task<T?> Load<T>(string path, CancellationToken ct)
    {
        if (!File.Exists(path)) return default;
        try { return JsonSerializer.Deserialize<T>(await File.ReadAllTextAsync(path, ct), Json); }
        catch (JsonException) { Quarantine(path); throw new InvalidDataException($"Task state was corrupt and moved to '{Path.GetFileName(path)}.corrupt'."); }
    }
    private async Task Save<T>(string path, T value, CancellationToken ct)
    {
        Directory.CreateDirectory(paths.Tasks); await _gate.WaitAsync(ct);
        try
        {
            var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try { await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(value, Json), new UTF8Encoding(false), ct); File.Move(temp, path, true); }
            finally { try { if (File.Exists(temp)) File.Delete(temp); } catch { } }
        }
        finally { _gate.Release(); }
    }
    private static string SafeId(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || id.Contains("..", StringComparison.Ordinal))
            throw new InvalidDataException("Task ID is not safe for storage.");
        return id;
    }
    private static void Quarantine(string path) { if (File.Exists(path)) File.Move(path, path + ".corrupt", true); }
}
public sealed class JsonSettingsStore(AppPaths paths) : ISettingsStore
{
    public async Task<AppSettings> LoadAsync(CancellationToken ct = default)
    {
        Directory.CreateDirectory(paths.Root); if (!File.Exists(paths.Config)) return Defaults();
        try
        {
            var value=JsonSerializer.Deserialize<AppSettings>(await File.ReadAllTextAsync(paths.Config,ct),new JsonSerializerOptions(JsonSerializerDefaults.Web));
            if(value?.Provider is not { } provider||string.IsNullOrWhiteSpace(provider.ProviderId)||string.IsNullOrWhiteSpace(provider.Endpoint)||string.IsNullOrWhiteSpace(provider.Model))return Defaults();
            if(!Uri.TryCreate(provider.Endpoint,UriKind.Absolute,out var endpoint)||endpoint.Scheme is not ("https" or "http"))return Defaults();
            return value with{Provider=provider with{MaxOutputTokens=Math.Clamp(provider.MaxOutputTokens,1,128_000),TimeoutSeconds=Math.Clamp(provider.TimeoutSeconds,5,600),MaxContextMessages=Math.Clamp(provider.MaxContextMessages,1,200),MaxSelfReviewRounds=Math.Clamp(provider.MaxSelfReviewRounds,0,2)}};
        }
        catch(Exception ex) when(ex is JsonException or IOException or UnauthorizedAccessException){return Defaults();}
    }
    public async Task SaveAsync(AppSettings value, CancellationToken ct = default) { Directory.CreateDirectory(paths.Root); await File.WriteAllTextAsync(paths.Config, JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }), ct); }
    public static AppSettings Defaults() => new(new("deepseek", "https://api.deepseek.com/v1", "deepseek-chat"));
}
public sealed class DpapiSecretStore(AppPaths paths) : ISecureSecretStore
{
    public async Task<string?> GetAsync(string id, CancellationToken ct = default) { var path = Path.Combine(paths.Secrets, Safe(id)); if (!File.Exists(path)) return null; try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(await File.ReadAllBytesAsync(path, ct), null, DataProtectionScope.CurrentUser)); } catch { return null; } }
    public async Task SetAsync(string id, string secret, CancellationToken ct = default) { Directory.CreateDirectory(paths.Secrets); var data = ProtectedData.Protect(Encoding.UTF8.GetBytes(secret), null, DataProtectionScope.CurrentUser); await File.WriteAllBytesAsync(Path.Combine(paths.Secrets, Safe(id)), data, ct); }
    private static string Safe(string id) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id))) + ".bin";
}
