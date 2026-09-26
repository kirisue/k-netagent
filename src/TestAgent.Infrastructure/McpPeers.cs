using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using TestAgent.Core;

namespace TestAgent.Infrastructure;

public sealed class JsonMcpPeerProfileStore : IMcpPeerProfileStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };
    private readonly string _directory;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public JsonMcpPeerProfileStore(AppPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _directory = Path.Combine(paths.Root, "mcp-peers");
    }

    public async Task<IReadOnlyList<McpPeerProfile>> ListAsync(CancellationToken ct = default)
    {
        Directory.CreateDirectory(_directory);
        var result = new List<McpPeerProfile>();
        foreach (var file in Directory.EnumerateFiles(_directory, "*.json"))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var profile = JsonSerializer.Deserialize<McpPeerProfile>(
                    await File.ReadAllTextAsync(file, ct), Json);
                if (profile is not null) result.Add(McpPeerProfileValidator.Normalize(profile));
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException)
            {
                Quarantine(file);
            }
        }

        return result
            .GroupBy(x => x.Id, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.First())
            .OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    public async Task SaveAsync(McpPeerProfile profile, CancellationToken ct = default)
    {
        var value = McpPeerProfileValidator.Normalize(profile);
        Directory.CreateDirectory(_directory);
        var path = ProfilePath(value.Id);
        await _gate.WaitAsync(ct);
        try
        {
            var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(value, Json),
                    new UTF8Encoding(false), ct);
                File.Move(temp, path, true);
            }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); }
                catch { /* A stale temp file is safer than replacing a valid profile non-atomically. */ }
            }
        }
        finally { _gate.Release(); }
    }

    public async Task DeleteAsync(string id, CancellationToken ct = default)
    {
        McpPeerProfileValidator.ValidateId(id);
        await _gate.WaitAsync(ct);
        try
        {
            var path = ProfilePath(id);
            if (File.Exists(path)) File.Delete(path);
        }
        finally { _gate.Release(); }
    }

    private string ProfilePath(string id)
    {
        var canonical = id.Trim().ToUpperInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
        return Path.Combine(_directory, hash + ".json");
    }

    private static void Quarantine(string path)
    {
        try { if (File.Exists(path)) File.Move(path, path + ".corrupt", true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}

public sealed class McpPeerService : IMcpPeerService
{
    private const int MaxConnections = 3;
    private const int MaxAdvertisedTools = 256;
    private const int MaxToolDescriptionChars = 2_000;
    private const int MaxToolSchemaChars = 16_384;
    private const int MaxArgumentChars = 65_536;
    private static readonly HashSet<string> ClaudeReadOnlyTools =
        new(["read", "view", "ls"], StringComparer.OrdinalIgnoreCase);
    private static readonly string[] SafeEnvironmentNames =
    [
        "APPDATA", "HOME", "LOCALAPPDATA", "PATH", "PATHEXT", "SYSTEMDRIVE", "SYSTEMROOT",
        "TEMP", "TMP", "USERPROFILE", "ProgramFiles", "ProgramFiles(x86)", "ProgramW6432",
        "CommonProgramFiles", "CommonProgramFiles(x86)", "CommonProgramW6432"
    ];

    private readonly IMcpPeerProfileStore _store;
    private readonly string _workspaceRoot;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _peerGates =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, PeerConnection> _connections =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, RuntimeStatus> _statuses =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Task> _monitors =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _connectionSlots = new(MaxConnections, MaxConnections);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _activitySync = new();
    private TaskCompletionSource _idle = CompletedSource();
    private int _activeOperations;
    private int _disposed;

    public McpPeerService(IMcpPeerProfileStore store, WorkspaceLocator workspace)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        ArgumentNullException.ThrowIfNull(workspace);
        _workspaceRoot = Path.GetFullPath(workspace.Root);
    }

    public McpPeerService(AppPaths paths, WorkspaceLocator workspace)
        : this(new JsonMcpPeerProfileStore(paths), workspace) { }

    public event Action? Changed;

    public async Task<IReadOnlyList<McpPeerInfo>> ListAsync(CancellationToken ct = default)
    {
        using var operation = EnterOperation();
        var profiles = await _store.ListAsync(ct);
        return profiles.Select(profile =>
        {
            var status = _statuses.GetValueOrDefault(profile.Id) ?? RuntimeStatus.Disconnected;
            return new McpPeerInfo(profile, status.State, status.LastError, status.ConnectedAt);
        }).ToArray();
    }

    public async Task SaveAsync(McpPeerProfile profile, CancellationToken ct = default)
    {
        using var operation = EnterOperation();
        var value = McpPeerProfileValidator.Normalize(profile);
        var gate = Gate(value.Id);
        await gate.WaitAsync(ct);
        try
        {
            var existing = await FindProfileAsync(value.Id, ct);
            if (_connections.ContainsKey(value.Id) &&
                (existing is null || TransportChanged(existing, value)))
                throw new InvalidOperationException(
                    "Disconnect this MCP peer before changing its executable, endpoint, or preset type.");

            if (!value.Enabled && _connections.TryRemove(value.Id, out var connection))
            {
                SetStatus(value.Id, McpPeerConnectionState.Disconnecting);
                await CloseConnectionAsync(connection);
                SetStatus(value.Id, McpPeerConnectionState.Disconnected);
            }

            await _store.SaveAsync(value, ct);
        }
        finally { gate.Release(); }
        RaiseChanged();
    }

    public async Task DeleteAsync(string id, CancellationToken ct = default)
    {
        using var operation = EnterOperation();
        McpPeerProfileValidator.ValidateId(id);
        var gate = Gate(id);
        await gate.WaitAsync(ct);
        try
        {
            if (_connections.TryRemove(id, out var connection))
            {
                SetStatus(id, McpPeerConnectionState.Disconnecting);
                await CloseConnectionAsync(connection);
            }
            await _store.DeleteAsync(id, ct);
            _statuses.TryRemove(id, out _);
        }
        finally { gate.Release(); }
        RaiseChanged();
    }

    public async Task<McpPeerInfo> ConnectAsync(string id, CancellationToken ct = default)
    {
        using var operation = EnterOperation();
        McpPeerProfileValidator.ValidateId(id);
        var gate = Gate(id);
        await gate.WaitAsync(ct);
        try
        {
            var profile = await GetRequiredProfileAsync(id, ct);
            if (!profile.Enabled) throw new InvalidOperationException("The MCP peer is disabled.");
            if (_connections.TryGetValue(id, out _))
            {
                var current = _statuses.GetValueOrDefault(id) ?? RuntimeStatus.Disconnected;
                return new(profile, current.State, current.LastError, current.ConnectedAt);
            }

            if (!await _connectionSlots.WaitAsync(0, ct))
                throw new InvalidOperationException($"At most {MaxConnections} MCP peers can be connected at once.");

            var slotOwned = true;
            McpClient? client = null;
            SetStatus(id, McpPeerConnectionState.Connecting);
            RaiseChanged();
            try
            {
                using var timeout = CreateTimeout(profile.ConnectTimeoutSeconds, ct);
                ValidateExecutableTrust(profile);
                var transport = CreateTransport(profile);
                client = await McpClient.CreateAsync(transport, cancellationToken: timeout.Token);
                var connection = new PeerConnection(client);
                _connections[id] = connection;
                slotOwned = false;
                var connectedAt = DateTimeOffset.Now;
                SetStatus(id, McpPeerConnectionState.Connected, connectedAt: connectedAt);
                var monitorId = Guid.NewGuid().ToString("N");
                var monitor = MonitorCompletionAsync(id, monitorId, connection);
                _monitors[monitorId] = monitor;
                return new(profile, McpPeerConnectionState.Connected, null, connectedAt);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested && !_lifetime.IsCancellationRequested)
            {
                if (client is not null) await client.DisposeAsync();
                SetStatus(id, McpPeerConnectionState.Faulted,
                    $"Connection timed out after {profile.ConnectTimeoutSeconds} seconds.");
                throw new TimeoutException(
                    $"MCP peer connection timed out after {profile.ConnectTimeoutSeconds} seconds.");
            }
            catch (OperationCanceledException)
            {
                if (client is not null) await client.DisposeAsync();
                SetStatus(id, McpPeerConnectionState.Disconnected);
                throw;
            }
            catch (Exception ex)
            {
                if (client is not null) await client.DisposeAsync();
                SetStatus(id, McpPeerConnectionState.Faulted, SafeError(ex));
                throw;
            }
            finally
            {
                if (slotOwned) _connectionSlots.Release();
                RaiseChanged();
            }
        }
        finally { gate.Release(); }
    }

    public async Task DisconnectAsync(string id, CancellationToken ct = default)
    {
        using var operation = EnterOperation();
        McpPeerProfileValidator.ValidateId(id);
        var gate = Gate(id);
        await gate.WaitAsync(ct);
        try
        {
            if (_connections.TryRemove(id, out var connection))
            {
                SetStatus(id, McpPeerConnectionState.Disconnecting);
                RaiseChanged();
                await CloseConnectionAsync(connection);
            }
            SetStatus(id, McpPeerConnectionState.Disconnected);
        }
        finally { gate.Release(); }
        RaiseChanged();
    }

    public async Task<IReadOnlyList<McpPeerTool>> ListToolsAsync(string id,
        CancellationToken ct = default)
    {
        using var operation = EnterOperation();
        McpPeerProfileValidator.ValidateId(id);
        var gate = Gate(id);
        await gate.WaitAsync(ct);
        try
        {
            var profile = await GetRequiredProfileAsync(id, ct);
            EnsureEnabledAndConnected(profile, out var connection);
            using var timeout = CreateTimeout(profile.OperationTimeoutSeconds, ct);
            try
            {
                var tools = await connection.Client.ListToolsAsync(cancellationToken: timeout.Token);
                var allowed = new HashSet<string>(profile.AllowedTools ?? [],
                    StringComparer.OrdinalIgnoreCase);
                return tools.Take(MaxAdvertisedTools).Select((tool, index) =>
                {
                    var description = Bound(tool.Description ?? string.Empty,
                        MaxToolDescriptionChars, out var descriptionTruncated);
                    var schema = tool.ProtocolTool.InputSchema.GetRawText();
                    var schemaTruncated = schema.Length > MaxToolSchemaChars;
                    if (schemaTruncated)
                        schema = "{\"type\":\"object\",\"description\":\"Schema omitted: local display limit exceeded.\"}";
                    return new McpPeerTool(tool.Name, description, schema,
                        allowed.Contains(tool.Name) && IsPresetCallable(profile.Kind, tool.Name),
                        descriptionTruncated || schemaTruncated ||
                        (index == MaxAdvertisedTools - 1 && tools.Count > MaxAdvertisedTools));
                }).ToArray();
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested && !_lifetime.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"Listing MCP tools timed out after {profile.OperationTimeoutSeconds} seconds.");
            }
            catch (Exception ex) when (IsConnectionFailure(ex))
            {
                await MarkFaultedAsync(id, connection, ex);
                throw new IOException("The MCP peer disconnected while listing tools.", ex);
            }
        }
        finally { gate.Release(); }
    }

    public async Task<McpPeerCallResult> CallToolAsync(string peerId, string toolName,
        JsonElement arguments, CancellationToken ct = default)
    {
        using var operation = EnterOperation();
        McpPeerProfileValidator.ValidateId(peerId);
        ValidateToolName(toolName);
        if (arguments.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("MCP tool arguments must be a JSON object.");
        var rawArguments = arguments.GetRawText();
        if (rawArguments.Length > MaxArgumentChars)
            throw new InvalidDataException($"MCP tool arguments exceed {MaxArgumentChars} characters.");

        var gate = Gate(peerId);
        await gate.WaitAsync(ct);
        try
        {
            var profile = await GetRequiredProfileAsync(peerId, ct);
            EnsureEnabledAndConnected(profile, out var connection);
            if (!(profile.AllowedTools ?? []).Contains(toolName, StringComparer.OrdinalIgnoreCase))
                throw new UnauthorizedAccessException(
                    $"Tool '{toolName}' is not in this peer's explicit allowlist.");
            if (!IsPresetCallable(profile.Kind, toolName))
                throw new UnauthorizedAccessException(
                    $"Tool '{toolName}' is blocked by the {profile.Kind} preset safety policy.");

            var values = profile.Kind switch
            {
                McpPeerKind.Codex => BuildCodexArguments(arguments),
                McpPeerKind.Claude => BuildClaudeArguments(toolName, arguments),
                _ => JsonSerializer.Deserialize<Dictionary<string, object?>>(rawArguments)
                     ?? new Dictionary<string, object?>()
            };
            var timer = Stopwatch.StartNew();
            using var timeout = CreateTimeout(profile.OperationTimeoutSeconds, ct);
            try
            {
                var result = await connection.Client.CallToolAsync(toolName, values,
                    cancellationToken: timeout.Token);
                var output = RenderTextResult(result, profile.MaxOutputChars, out var truncated);
                timer.Stop();
                var success = result.IsError != true;
                return new(peerId, toolName, success, output, truncated,
                    success ? null : "The remote MCP tool reported an error.", timer.ElapsedMilliseconds);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested && !_lifetime.IsCancellationRequested)
            {
                timer.Stop();
                return new(peerId, toolName, false, string.Empty, false,
                    $"Tool call timed out after {profile.OperationTimeoutSeconds} seconds.",
                    timer.ElapsedMilliseconds);
            }
            catch (Exception ex) when (IsConnectionFailure(ex))
            {
                timer.Stop();
                await MarkFaultedAsync(peerId, connection, ex);
                return new(peerId, toolName, false, string.Empty, false,
                    "The MCP peer disconnected during the tool call; the call was not replayed.",
                    timer.ElapsedMilliseconds);
            }
            catch (McpException ex)
            {
                timer.Stop();
                return new(peerId, toolName, false, string.Empty, false, SafeError(ex),
                    timer.ElapsedMilliseconds);
            }
        }
        finally { gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _lifetime.Cancel();
        await WaitForIdleAsync();
        foreach (var id in _connections.Keys.ToArray())
        {
            var gate = Gate(id);
            await gate.WaitAsync();
            try
            {
                if (_connections.TryRemove(id, out var connection))
                {
                    try { await CloseConnectionAsync(connection); }
                    catch { /* Continue closing every other child process. */ }
                }
                SetStatus(id, McpPeerConnectionState.Disconnected);
            }
            finally { gate.Release(); }
        }

        var monitors = _monitors.Values.ToArray();
        if (monitors.Length > 0)
        {
            try { await Task.WhenAll(monitors); }
            catch { /* All connections are already closed; completion errors are status-only. */ }
        }

        _lifetime.Dispose();
        _connectionSlots.Dispose();
        foreach (var gate in _peerGates.Values) gate.Dispose();
    }

    public void Dispose() => Task.Run(async () =>
        await DisposeAsync().ConfigureAwait(false)).GetAwaiter().GetResult();

    private IClientTransport CreateTransport(McpPeerProfile profile)
    {
        if (profile.Kind == McpPeerKind.CustomHttp)
        {
            var handler = new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                UseProxy = false
            };
            var httpClient = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
            return new HttpClientTransport(new HttpClientTransportOptions
            {
                Name = profile.Name,
                Endpoint = new Uri(profile.Endpoint!, UriKind.Absolute),
                TransportMode = HttpTransportMode.StreamableHttp,
                ConnectionTimeout = TimeSpan.FromSeconds(profile.ConnectTimeoutSeconds),
                EnableStandaloneGetStream = false,
                MaxReconnectionAttempts = 0
            }, httpClient, null, true);
        }

        return new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = profile.Name,
            Command = profile.ExecutablePath!,
            Arguments = profile.Kind == McpPeerKind.Codex ? ["mcp-server"] : ["mcp", "serve"],
            WorkingDirectory = _workspaceRoot,
            InheritEnvironmentVariables = false,
            EnvironmentVariables = BuildSafeEnvironment(),
            ShutdownTimeout = TimeSpan.FromSeconds(5)
        });
    }

    private void ValidateExecutableTrust(McpPeerProfile profile)
    {
        if (profile.Kind == McpPeerKind.CustomHttp) return;
        var executable = Path.GetFullPath(profile.ExecutablePath!);
        if (!File.Exists(executable))
            throw new FileNotFoundException("The configured MCP peer executable no longer exists.", executable);

        var relative = Path.GetRelativePath(_workspaceRoot, executable);
        if (!Path.IsPathFullyQualified(relative) && relative != ".." &&
            !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
            !relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidDataException("MCP peer executables cannot be launched from the agent workspace.");

        try
        {
            if ((File.GetAttributes(executable) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("MCP peer executable cannot be a reparse point.");
            for (var directory = new FileInfo(executable).Directory;
                 directory is not null; directory = directory.Parent)
                if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException(
                        "MCP peer executable cannot be reached through a reparse-point directory.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidDataException("The MCP peer executable path could not be verified.", ex);
        }
    }

    private static string RenderTextResult(CallToolResult result, int maxChars, out bool truncated)
    {
        var output = new StringBuilder(Math.Min(maxChars, 4_096));
        truncated = false;
        foreach (var block in result.Content)
        {
            if (block is TextContentBlock text)
            {
                AppendBounded(output, text.Text, maxChars, ref truncated);
                continue;
            }

            truncated = true;
            AppendBounded(output, $"[non-text MCP content omitted: {block.Type}]", maxChars,
                ref truncated);
        }

        if (result.StructuredContent is not null)
        {
            truncated = true;
            AppendBounded(output, "[structured MCP content omitted]", maxChars, ref truncated);
        }

        if (truncated && output.Length < maxChars)
            AppendBounded(output, "…[output truncated or non-text content omitted by K.netagent]",
                maxChars, ref truncated);
        return output.ToString();
    }

    private static void AppendBounded(StringBuilder output, string? value, int maxChars,
        ref bool truncated)
    {
        if (string.IsNullOrEmpty(value) || output.Length >= maxChars)
        {
            if (!string.IsNullOrEmpty(value)) truncated = true;
            return;
        }
        if (output.Length > 0)
        {
            output.Append('\n');
            if (output.Length >= maxChars)
            {
                truncated = true;
                return;
            }
        }
        var remaining = maxChars - output.Length;
        if (value.Length <= remaining) output.Append(value);
        else
        {
            output.Append(value.AsSpan(0, remaining));
            truncated = true;
        }
    }

    private async Task MonitorCompletionAsync(string id, string monitorId, PeerConnection connection)
    {
        try { await connection.Client.Completion; }
        catch { /* The status below deliberately avoids leaking child stderr or remote payloads. */ }

        var gate = Gate(id);
        await gate.WaitAsync();
        try
        {
            if (_connections.TryGetValue(id, out var current) && ReferenceEquals(current, connection))
            {
                _connections.TryRemove(id, out _);
                SetStatus(id, McpPeerConnectionState.Faulted,
                    "The MCP peer process or transport closed unexpectedly.");
                await CloseConnectionAsync(connection);
            }
        }
        finally
        {
            gate.Release();
            _monitors.TryRemove(monitorId, out _);
            RaiseChanged();
        }
    }

    private async Task MarkFaultedAsync(string id, PeerConnection connection, Exception error)
    {
        if (_connections.TryGetValue(id, out var current) && ReferenceEquals(current, connection))
        {
            _connections.TryRemove(id, out _);
            SetStatus(id, McpPeerConnectionState.Faulted, SafeError(error));
            await CloseConnectionAsync(connection);
            RaiseChanged();
        }
    }

    private async Task CloseConnectionAsync(PeerConnection connection)
    {
        try { await connection.Client.DisposeAsync(); }
        finally { connection.ReleaseSlot(_connectionSlots); }
    }

    private async Task<McpPeerProfile?> FindProfileAsync(string id, CancellationToken ct) =>
        (await _store.ListAsync(ct)).FirstOrDefault(x =>
            x.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    private async Task<McpPeerProfile> GetRequiredProfileAsync(string id, CancellationToken ct) =>
        await FindProfileAsync(id, ct) ?? throw new KeyNotFoundException($"MCP peer '{id}' was not found.");

    private void EnsureEnabledAndConnected(McpPeerProfile profile, out PeerConnection connection)
    {
        if (!profile.Enabled) throw new InvalidOperationException("The MCP peer is disabled.");
        if (!_connections.TryGetValue(profile.Id, out connection!))
            throw new InvalidOperationException(
                "The MCP peer is not connected. Connect it explicitly before listing or calling tools.");
    }

    private CancellationTokenSource CreateTimeout(int seconds, CancellationToken caller)
    {
        var timeout = CancellationTokenSource.CreateLinkedTokenSource(caller, _lifetime.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(seconds));
        return timeout;
    }

    private static Dictionary<string, string?> BuildSafeEnvironment()
    {
        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in SafeEnvironmentNames)
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrEmpty(value)) result[name] = value;
        }
        return result;
    }

    private static bool TransportChanged(McpPeerProfile before, McpPeerProfile after) =>
        before.Kind != after.Kind ||
        !string.Equals(before.ExecutablePath, after.ExecutablePath,
            StringComparison.OrdinalIgnoreCase) ||
        !string.Equals(before.Endpoint, after.Endpoint, StringComparison.OrdinalIgnoreCase);

    private static bool IsConnectionFailure(Exception ex) =>
        ex is ClientTransportClosedException or IOException or EndOfStreamException or ObjectDisposedException;

    private static bool IsPresetCallable(McpPeerKind kind, string toolName) => kind switch
    {
        McpPeerKind.Codex => toolName.Equals("codex", StringComparison.OrdinalIgnoreCase),
        McpPeerKind.Claude => ClaudeReadOnlyTools.Contains(toolName),
        McpPeerKind.CustomHttp => true,
        _ => false
    };

    private Dictionary<string, object?> BuildCodexArguments(JsonElement arguments)
    {
        string? prompt = null;
        string? task = null;
        foreach (var property in arguments.EnumerateObject())
        {
            if (property.NameEquals("prompt"))
            {
                if (property.Value.ValueKind != JsonValueKind.String)
                    throw new InvalidDataException("Codex prompt must be a string.");
                prompt = property.Value.GetString();
            }
            else if (property.NameEquals("task"))
            {
                if (property.Value.ValueKind != JsonValueKind.String)
                    throw new InvalidDataException("Codex task must be a string.");
                task = property.Value.GetString();
            }
            else if (property.Name is not ("cwd" or "sandbox" or "approval-policy" or
                                           "developer-instructions"))
                throw new InvalidDataException(
                    $"Codex argument '{property.Name}' is not permitted by the fixed peer preset.");
        }

        if (string.IsNullOrWhiteSpace(prompt) == string.IsNullOrWhiteSpace(task))
            throw new InvalidDataException("Codex requires exactly one non-empty task or prompt.");
        prompt ??= task;
        if (prompt!.Length > 50_000 || prompt.IndexOf('\0') >= 0)
            throw new InvalidDataException("Codex prompt is invalid or exceeds 50,000 characters.");
        var instructions =
            $"Act only as a read-only peer inside the workspace rooted at '{_workspaceRoot}'. " +
            "Do not access files outside that workspace, credentials, secrets, or environment values. " +
            "Do not use the network. Return only analysis, verification evidence, suggestions, or a proposed patch as text; do not modify files.";
        return new Dictionary<string, object?>
        {
            ["prompt"] = prompt,
            ["cwd"] = _workspaceRoot,
            ["sandbox"] = "read-only",
            ["approval-policy"] = "never",
            ["developer-instructions"] = instructions
        };
    }

    private Dictionary<string, object?> BuildClaudeArguments(string toolName, JsonElement arguments)
    {
        ValidateClaudeArgumentPaths(arguments, toolName, null);
        return JsonSerializer.Deserialize<Dictionary<string, object?>>(arguments.GetRawText())
               ?? new Dictionary<string, object?>();
    }

    private void ValidateClaudeArgumentPaths(JsonElement value, string toolName, string? propertyName)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in value.EnumerateObject())
                    ValidateClaudeArgumentPaths(property.Value, toolName, property.Name);
                break;
            case JsonValueKind.Array:
                foreach (var item in value.EnumerateArray())
                    ValidateClaudeArgumentPaths(item, toolName, propertyName);
                break;
            case JsonValueKind.String when IsClaudePathProperty(propertyName, toolName):
                ValidateWorkspaceRelativePeerPath(value.GetString());
                break;
        }
    }

    private static bool IsClaudePathProperty(string? propertyName, string toolName)
    {
        if (string.IsNullOrWhiteSpace(propertyName)) return false;
        return propertyName.Equals("path", StringComparison.OrdinalIgnoreCase) ||
               propertyName.Equals("file_path", StringComparison.OrdinalIgnoreCase) ||
               propertyName.Equals("filePath", StringComparison.OrdinalIgnoreCase) ||
               propertyName.Equals("file", StringComparison.OrdinalIgnoreCase) ||
               propertyName.Equals("directory", StringComparison.OrdinalIgnoreCase) ||
               propertyName.Equals("dir", StringComparison.OrdinalIgnoreCase) ||
               propertyName.Equals("folder", StringComparison.OrdinalIgnoreCase) ||
               propertyName.Equals("root", StringComparison.OrdinalIgnoreCase) ||
               propertyName.Equals("cwd", StringComparison.OrdinalIgnoreCase) ||
               toolName.Equals("glob", StringComparison.OrdinalIgnoreCase) &&
               propertyName.Equals("pattern", StringComparison.OrdinalIgnoreCase) ||
               toolName.Equals("ls", StringComparison.OrdinalIgnoreCase) &&
               propertyName.Equals("target", StringComparison.OrdinalIgnoreCase);
    }

    private void ValidateWorkspaceRelativePeerPath(string? value)
    {
        var candidate = value?.Trim();
        if (string.IsNullOrEmpty(candidate) || candidate == ".") return;
        if (candidate.IndexOf('\0') >= 0 || Path.IsPathRooted(candidate) ||
            Path.IsPathFullyQualified(candidate) ||
            candidate.StartsWith('~') || candidate.StartsWith("file:", StringComparison.OrdinalIgnoreCase) ||
            candidate.Contains('%') || candidate.Contains('$'))
            throw new InvalidDataException("Claude peer paths must be plain workspace-relative paths.");

        var segments = candidate.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(segment => segment == ".."))
            throw new InvalidDataException("Claude peer paths cannot leave the workspace.");
        var fullPath = Path.GetFullPath(Path.Combine(_workspaceRoot, candidate));
        var relative = Path.GetRelativePath(_workspaceRoot, fullPath);
        if (relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar,
                StringComparison.Ordinal) || Path.IsPathFullyQualified(relative))
            throw new InvalidDataException("Claude peer paths cannot leave the workspace.");

        var current = _workspaceRoot;
        if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Claude peer paths cannot traverse a reparse point.");
        foreach (var segment in segments)
        {
            if (segment.IndexOfAny(['*', '?', '[', ']']) >= 0) break;
            current = Path.Combine(current, segment);
            if ((File.Exists(current) || Directory.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Claude peer paths cannot traverse a reparse point.");
        }
    }

    private static void ValidateToolName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 128 ||
            name.Any(char.IsControl))
            throw new InvalidDataException("MCP tool name is invalid.");
    }

    private static string Bound(string value, int maxChars, out bool truncated)
    {
        truncated = value.Length > maxChars;
        if (!truncated) return value;
        const string suffix = "\n…[truncated by K.netagent]";
        return value[..Math.Max(0, maxChars - suffix.Length)] + suffix;
    }

    private static string SafeError(Exception ex)
    {
        var message = ex.Message.Replace('\0', ' ').Replace('\r', ' ').Replace('\n', ' ').Trim();
        return message.Length <= 500 ? message : message[..500] + "…";
    }

    private SemaphoreSlim Gate(string id) =>
        _peerGates.GetOrAdd(id, _ => new SemaphoreSlim(1, 1));

    private void SetStatus(string id, McpPeerConnectionState state, string? error = null,
        DateTimeOffset? connectedAt = null) =>
        _statuses[id] = new(state, error, connectedAt);

    private void RaiseChanged()
    {
        try { Changed?.Invoke(); }
        catch { /* A UI observer must not change connection lifecycle semantics. */ }
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    private OperationLease EnterOperation()
    {
        lock (_activitySync)
        {
            ThrowIfDisposed();
            if (_activeOperations++ == 0)
                _idle = new(TaskCreationOptions.RunContinuationsAsynchronously);
            return new OperationLease(ExitOperation);
        }
    }

    private void ExitOperation()
    {
        lock (_activitySync)
        {
            if (--_activeOperations == 0) _idle.TrySetResult();
        }
    }

    private Task WaitForIdleAsync()
    {
        lock (_activitySync) return _activeOperations == 0 ? Task.CompletedTask : _idle.Task;
    }

    private static TaskCompletionSource CompletedSource()
    {
        var result = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        result.SetResult();
        return result;
    }

    private sealed record RuntimeStatus(McpPeerConnectionState State, string? LastError = null,
        DateTimeOffset? ConnectedAt = null)
    {
        public static RuntimeStatus Disconnected { get; } =
            new(McpPeerConnectionState.Disconnected);
    }

    private sealed class PeerConnection(McpClient client)
    {
        private int _slotReleased;
        public McpClient Client { get; } = client;

        public void ReleaseSlot(SemaphoreSlim slots)
        {
            if (Interlocked.Exchange(ref _slotReleased, 1) == 0) slots.Release();
        }
    }

    private sealed class OperationLease(Action release) : IDisposable
    {
        private Action? _release = release;
        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }
}

internal static class McpPeerProfileValidator
{
    public static McpPeerProfile Normalize(McpPeerProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ValidateId(profile.Id);
        var name = profile.Name?.Trim();
        if (string.IsNullOrWhiteSpace(name) || name.Length > 100 || name.Any(char.IsControl))
            throw new InvalidDataException("MCP peer name must contain 1 to 100 printable characters.");
        if (!Enum.IsDefined(profile.Kind)) throw new InvalidDataException("MCP peer preset type is invalid.");
        if (profile.ConnectTimeoutSeconds is < 5 or > 60)
            throw new InvalidDataException("MCP connect timeout must be between 5 and 60 seconds.");
        if (profile.OperationTimeoutSeconds is < 5 or > 120)
            throw new InvalidDataException("MCP operation timeout must be between 5 and 120 seconds.");
        if (profile.MaxOutputChars is < 1_024 or > 65_536)
            throw new InvalidDataException("MCP output limit must be between 1024 and 65536 characters.");

        var tools = (profile.AllowedTools ?? [])
            .Select(x => x?.Trim())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (tools.Length > 256 || tools.Any(x => x.Length > 128 || x.Any(char.IsControl)))
            throw new InvalidDataException("MCP tool allowlist is invalid or exceeds 256 entries.");

        if (profile.Kind is McpPeerKind.Codex or McpPeerKind.Claude)
        {
            if (string.IsNullOrWhiteSpace(profile.ExecutablePath) ||
                !Path.IsPathFullyQualified(profile.ExecutablePath))
                throw new InvalidDataException("STDIO peers require an absolute executable path.");
            var executable = Path.GetFullPath(profile.ExecutablePath);
            if (!Path.GetExtension(executable).Equals(".exe", StringComparison.OrdinalIgnoreCase) ||
                !File.Exists(executable))
                throw new InvalidDataException("STDIO peer executable must be an existing .exe file.");
            return profile with
            {
                Id = profile.Id.Trim(), Name = name, ExecutablePath = executable,
                Endpoint = null, AllowedTools = tools
            };
        }

        if (string.IsNullOrWhiteSpace(profile.Endpoint) ||
            !Uri.TryCreate(profile.Endpoint.Trim(), UriKind.Absolute, out var endpoint) ||
            endpoint.Scheme is not ("http" or "https") || !endpoint.IsLoopback ||
            !string.IsNullOrEmpty(endpoint.UserInfo) || !string.IsNullOrEmpty(endpoint.Query) ||
            !string.IsNullOrEmpty(endpoint.Fragment))
            throw new InvalidDataException(
                "Custom HTTP peers require a loopback http/https endpoint without credentials, query, or fragment.");
        return profile with
        {
            Id = profile.Id.Trim(), Name = name, ExecutablePath = null,
            Endpoint = endpoint.AbsoluteUri, AllowedTools = tools
        };
    }

    public static void ValidateId(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 64)
            throw new InvalidDataException("MCP peer ID must contain 1 to 64 characters.");
        var value = id.Trim();
        if (!char.IsAsciiLetterOrDigit(value[0]) ||
            value.Any(x => !(char.IsAsciiLetterOrDigit(x) || x is '-' or '_' or '.')))
            throw new InvalidDataException(
                "MCP peer ID may contain only ASCII letters, digits, period, dash, and underscore.");
    }
}
