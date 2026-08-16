using System.Buffers.Binary;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TestAgent.Infrastructure;

public sealed record VsCodeBridgePairingInfo(
    [property: JsonPropertyName("protocol")] int Protocol,
    [property: JsonPropertyName("pipeName")] string PipeName,
    [property: JsonPropertyName("secret")] string Secret);
public sealed record VsCodeBridgeWorkspaceFolder(string Scheme, string Path);
public sealed record VsCodeBridgeWorkspaceClaim(bool Trusted, string UiKind, string? RemoteName,
    IReadOnlyList<VsCodeBridgeWorkspaceFolder> Folders);

public interface IVsCodeBridgeClient
{
    bool IsConnected { get; }
    event Action? Changed;
    VsCodeBridgePairingInfo GetPairingInfo();
    Task StartAsync(CancellationToken ct = default);
    Task<JsonElement> QueryAsync(string operation, CancellationToken ct = default);
}

public static class VsCodeBridgeProtocol
{
    public const int Version = 1;
    public const int MaxFrameBytes = 256 * 1024;
    public const int AuthenticationBytes = 32;
    private static readonly byte[] AuthenticationContext =
        Encoding.UTF8.GetBytes("K.netagent VS Code bridge v1\0");
    private static readonly byte[] ServerProofContext =
        Encoding.UTF8.GetBytes("K.netagent VS Code bridge server v1\0");

    public static byte[] ComputeAuthenticationMac(ReadOnlySpan<byte> secret,
        ReadOnlySpan<byte> challenge, ReadOnlySpan<byte> clientNonce)
        => ComputeMac(secret, challenge, clientNonce, AuthenticationContext);

    public static byte[] ComputeServerProof(ReadOnlySpan<byte> secret,
        ReadOnlySpan<byte> challenge, ReadOnlySpan<byte> clientNonce)
        => ComputeMac(secret, challenge, clientNonce, ServerProofContext);

    private static byte[] ComputeMac(ReadOnlySpan<byte> secret, ReadOnlySpan<byte> challenge,
        ReadOnlySpan<byte> clientNonce, byte[] context)
    {
        if (secret.Length != AuthenticationBytes || challenge.Length != AuthenticationBytes ||
            clientNonce.Length != AuthenticationBytes)
            throw new ArgumentException("Secret, challenge, and client nonce must each be 32 bytes.");
        var input = new byte[context.Length + challenge.Length + clientNonce.Length];
        context.CopyTo(input, 0);
        challenge.CopyTo(input.AsSpan(context.Length));
        clientNonce.CopyTo(input.AsSpan(context.Length + challenge.Length));
        try { return HMACSHA256.HashData(secret, input); }
        finally { CryptographicOperations.ZeroMemory(input); }
    }

    public static void ValidateWorkspaceClaim(string expectedRoot, VsCodeBridgeWorkspaceClaim claim)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The VS Code bridge is available only on Windows.");
        if (!claim.Trusted) throw new InvalidDataException("VS Code workspace trust is required.");
        if (!claim.UiKind.Equals("desktop", StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrWhiteSpace(claim.RemoteName))
            throw new InvalidDataException("Only a local desktop VS Code window can pair.");
        if (claim.Folders.Count != 1)
            throw new InvalidDataException("Exactly one VS Code workspace folder is required.");
        var folder = claim.Folders[0];
        if (!folder.Scheme.Equals("file", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(folder.Path) || !Path.IsPathFullyQualified(folder.Path))
            throw new InvalidDataException("The VS Code workspace must be a local file folder.");
        string expected;
        string actual;
        try
        {
            expected = Path.TrimEndingDirectorySeparator(Path.GetFullPath(expectedRoot));
            actual = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder.Path));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new InvalidDataException("The VS Code workspace path is invalid.", ex);
        }
        if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The VS Code workspace root does not match the Agent workspace.");
        try
        {
            if (!Directory.Exists(expected) || (File.GetAttributes(expected) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("The Agent workspace root is unavailable or is a reparse point.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidDataException("The Agent workspace root cannot be verified.", ex);
        }
    }

    public static async Task WriteFrameAsync(Stream stream, object value, CancellationToken ct = default)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
        if (payload.Length is <= 0 or > MaxFrameBytes)
            throw new InvalidDataException("VS Code bridge frame exceeds the 256 KiB limit.");
        var prefix = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(prefix, payload.Length);
        await stream.WriteAsync(prefix, ct);
        await stream.WriteAsync(payload, ct);
        await stream.FlushAsync(ct);
    }

    public static async Task<JsonDocument> ReadFrameAsync(Stream stream, CancellationToken ct = default)
    {
        var prefix = new byte[4];
        await ReadExactlyAsync(stream, prefix, ct);
        var length = BinaryPrimitives.ReadInt32LittleEndian(prefix);
        if (length is <= 0 or > MaxFrameBytes)
            throw new InvalidDataException("VS Code bridge frame has an invalid length.");
        var payload = GC.AllocateUninitializedArray<byte>(length);
        await ReadExactlyAsync(stream, payload, ct);
        try
        {
            return JsonDocument.Parse(payload, new JsonDocumentOptions
            {
                MaxDepth = 32,
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow
            });
        }
        catch (JsonException ex) { throw new InvalidDataException("VS Code bridge frame is not valid JSON.", ex); }
    }

    private static async Task ReadExactlyAsync(Stream stream, Memory<byte> destination, CancellationToken ct)
    {
        var offset = 0;
        while (offset < destination.Length)
        {
            var read = await stream.ReadAsync(destination[offset..], ct);
            if (read == 0) throw new EndOfStreamException("VS Code bridge disconnected during a frame.");
            offset += read;
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
}

public sealed class VsCodeBridgeServer : IVsCodeBridgeClient, IDisposable, IAsyncDisposable
{
    private static readonly HashSet<string> AllowedOperations =
        ["activeEditor", "diagnostics", "tasks", "extensions"];
    private readonly string _workspaceRoot;
    private readonly string _pipeName = "knetagent-" + Guid.NewGuid().ToString("N");
    private readonly byte[] _secret = RandomNumberGenerator.GetBytes(VsCodeBridgeProtocol.AuthenticationBytes);
    private readonly TimeSpan _authenticationTimeout;
    private readonly TimeSpan _queryTimeout;
    private readonly TimeSpan _heartbeatInterval;
    private readonly object _sync = new();
    private readonly SemaphoreSlim _queryGate = new(1, 1);
    private CancellationTokenSource? _lifetime;
    private Task? _listener;
    private BridgeConnection? _connection;
    private bool _disposed;

    public VsCodeBridgeServer(WorkspaceLocator workspace, TimeSpan? authenticationTimeout = null,
        TimeSpan? queryTimeout = null, TimeSpan? heartbeatInterval = null)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The VS Code bridge is available only on Windows.");
        _workspaceRoot = workspace.Root;
        _authenticationTimeout = authenticationTimeout ?? TimeSpan.FromSeconds(5);
        _queryTimeout = queryTimeout ?? TimeSpan.FromSeconds(8);
        _heartbeatInterval = heartbeatInterval ?? TimeSpan.FromSeconds(5);
        if (_authenticationTimeout <= TimeSpan.Zero || _queryTimeout <= TimeSpan.Zero ||
            _heartbeatInterval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(queryTimeout), "Bridge timeouts must be positive.");
    }

    public bool IsConnected { get { lock (_sync) return _connection is not null; } }
    public event Action? Changed;

    public VsCodeBridgePairingInfo GetPairingInfo()
    {
        ThrowIfDisposed();
        return new(VsCodeBridgeProtocol.Version, _pipeName, Convert.ToBase64String(_secret));
    }

    public Task StartAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_sync)
        {
            ThrowIfDisposed();
            if (_listener is not null) return Task.CompletedTask;
            _lifetime = new CancellationTokenSource();
            var lifetime = _lifetime;
            _listener = Task.Run(() => AcceptLoopAsync(lifetime.Token));
        }
        return Task.CompletedTask;
    }

    public async Task<JsonElement> QueryAsync(string operation, CancellationToken ct = default)
    {
        if (!AllowedOperations.Contains(operation))
            throw new ArgumentException("Unsupported VS Code bridge operation.", nameof(operation));
        await StartAsync(ct);
        await _queryGate.WaitAsync(ct);
        BridgeConnection? connection = null;
        try
        {
            lock (_sync)
            {
                ThrowIfDisposed();
                connection = _connection ?? throw new InvalidOperationException(
                    "VS Code read-only bridge is not paired. Pair the trusted local workspace in the app first.");
            }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(_queryTimeout);
            var id = Guid.NewGuid().ToString("N");
            await VsCodeBridgeProtocol.WriteFrameAsync(connection.Pipe,
                new { type = "request", id, operation }, timeout.Token);
            using var response = await VsCodeBridgeProtocol.ReadFrameAsync(connection.Pipe, timeout.Token);
            var root = response.RootElement;
            if (!IsString(root, "type", "response") || !GetString(root, "id").Equals(id, StringComparison.Ordinal) ||
                !root.TryGetProperty("ok", out var ok) || ok.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new InvalidDataException("VS Code bridge returned an invalid response envelope.");
            if (!ok.GetBoolean()) throw new IOException("VS Code rejected the local metadata query.");
            if (!root.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("VS Code bridge response has no metadata result.");
            return result.Clone();
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            if (connection is not null) MarkDisconnected(connection);
            throw new TimeoutException($"VS Code bridge query timed out after {_queryTimeout.TotalSeconds:0.#} seconds.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // A cancelled read may leave a partial frame; discard the connection before propagating cancellation.
            if (connection is not null) MarkDisconnected(connection);
            throw;
        }
        catch (Exception ex) when (ex is IOException or EndOfStreamException or ObjectDisposedException or InvalidDataException)
        {
            if (connection is not null) MarkDisconnected(connection);
            throw;
        }
        finally { _queryGate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        Task? listener;
        BridgeConnection? connection;
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _lifetime?.Cancel();
            connection = _connection;
            _connection = null;
            listener = _listener;
        }
        connection?.Close();
        try
        {
            if (listener is not null)
            {
                try { await listener.ConfigureAwait(false); }
                catch (OperationCanceledException) { }
                catch { /* Disposal must still erase the in-memory pairing secret. */ }
            }
        }
        finally
        {
            _lifetime?.Dispose();
            CryptographicOperations.ZeroMemory(_secret);
        }
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await using var pipe = new NamedPipeServerStream(_pipeName, PipeDirection.InOut, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly,
                0, 0);
            BridgeConnection? connection = null;
            try
            {
                await pipe.WaitForConnectionAsync(ct);
                connection = await AuthenticateAsync(pipe, ct);
                // Publish the connection and send the final ready frame under the same gate used by
                // queries. Once the client observes ready, no query can still see a stale null state
                // or interleave its request with the handshake write.
                await _queryGate.WaitAsync(ct);
                try
                {
                    SetConnected(connection);
                    await VsCodeBridgeProtocol.WriteFrameAsync(pipe,
                        new { type = "ready", protocol = VsCodeBridgeProtocol.Version }, ct);
                }
                finally { _queryGate.Release(); }
                await MonitorConnectionAsync(connection, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) when (ex is IOException or EndOfStreamException or InvalidDataException or
                                       CryptographicException or OperationCanceledException or JsonException)
            {
                // Reject malformed, unauthenticated, timed-out, or disconnected clients and listen again.
            }
            finally
            {
                if (connection is not null) MarkDisconnected(connection);
            }
        }
    }

    private async Task MonitorConnectionAsync(BridgeConnection connection, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var delay = Task.Delay(_heartbeatInterval, ct);
            if (await Task.WhenAny(delay, connection.Closed.Task).ConfigureAwait(false) == connection.Closed.Task)
                return;
            await delay.ConfigureAwait(false);

            await _queryGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                lock (_sync)
                {
                    if (!ReferenceEquals(_connection, connection)) return;
                }

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(_queryTimeout);
                var id = Guid.NewGuid().ToString("N");
                await VsCodeBridgeProtocol.WriteFrameAsync(connection.Pipe,
                    new { type = "ping", id }, timeout.Token).ConfigureAwait(false);
                using var pong = await VsCodeBridgeProtocol.ReadFrameAsync(connection.Pipe, timeout.Token)
                    .ConfigureAwait(false);
                var root = pong.RootElement;
                if (!IsString(root, "type", "pong") ||
                    !GetString(root, "id").Equals(id, StringComparison.Ordinal))
                    throw new InvalidDataException("VS Code bridge returned an invalid heartbeat response.");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or EndOfStreamException or
                                       ObjectDisposedException or InvalidDataException or JsonException)
            {
                return;
            }
            finally { _queryGate.Release(); }
        }
    }

    private async Task<BridgeConnection> AuthenticateAsync(NamedPipeServerStream pipe, CancellationToken lifetime)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        timeout.CancelAfter(_authenticationTimeout);
        var challenge = RandomNumberGenerator.GetBytes(VsCodeBridgeProtocol.AuthenticationBytes);
        try
        {
            await VsCodeBridgeProtocol.WriteFrameAsync(pipe, new
            {
                type = "challenge",
                protocol = VsCodeBridgeProtocol.Version,
                challenge = Convert.ToBase64String(challenge)
            }, timeout.Token);
            using var authentication = await VsCodeBridgeProtocol.ReadFrameAsync(pipe, timeout.Token);
            var auth = authentication.RootElement;
            if (!IsString(auth, "type", "authenticate") || GetInt(auth, "protocol") != VsCodeBridgeProtocol.Version)
                throw new InvalidDataException("VS Code bridge authentication envelope is invalid.");
            var nonce = Decode32(GetString(auth, "clientNonce"));
            var supplied = Decode32(GetString(auth, "mac"));
            var expected = VsCodeBridgeProtocol.ComputeAuthenticationMac(_secret, challenge, nonce);
            var serverProof = VsCodeBridgeProtocol.ComputeServerProof(_secret, challenge, nonce);
            try
            {
                if (!CryptographicOperations.FixedTimeEquals(supplied, expected))
                    throw new CryptographicException("VS Code bridge authentication failed.");
                await VsCodeBridgeProtocol.WriteFrameAsync(pipe,
                    new { type = "authenticated", protocol = VsCodeBridgeProtocol.Version, serverMac = Convert.ToBase64String(serverProof) },
                    timeout.Token);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(nonce);
                CryptographicOperations.ZeroMemory(supplied);
                CryptographicOperations.ZeroMemory(expected);
                CryptographicOperations.ZeroMemory(serverProof);
            }
            using var workspace = await VsCodeBridgeProtocol.ReadFrameAsync(pipe, timeout.Token);
            var claim = ParseWorkspaceClaim(workspace.RootElement);
            VsCodeBridgeProtocol.ValidateWorkspaceClaim(_workspaceRoot, claim);
            return new(pipe);
        }
        finally { CryptographicOperations.ZeroMemory(challenge); }
    }

    private void SetConnected(BridgeConnection connection)
    {
        lock (_sync)
        {
            if (_disposed) { connection.Close(); return; }
            _connection = connection;
        }
        NotifyChanged();
    }

    private void MarkDisconnected(BridgeConnection connection)
    {
        var changed = false;
        lock (_sync)
        {
            connection.Close();
            if (ReferenceEquals(_connection, connection))
            {
                _connection = null;
                changed = true;
            }
        }
        if (changed) NotifyChanged();
    }

    private static VsCodeBridgeWorkspaceClaim ParseWorkspaceClaim(JsonElement root)
    {
        if (!IsString(root, "type", "workspace") || !root.TryGetProperty("trusted", out var trusted) ||
            trusted.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
            !root.TryGetProperty("folders", out var folders) || folders.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("VS Code workspace claim is invalid.");
        var items = folders.EnumerateArray().Take(3).Select(folder => new VsCodeBridgeWorkspaceFolder(
            GetString(folder, "scheme"), GetString(folder, "path"))).ToArray();
        return new(trusted.GetBoolean(), GetString(root, "uiKind"),
            NullString(root, "remoteName"), items);
    }

    private static byte[] Decode32(string value)
    {
        byte[] bytes;
        try { bytes = Convert.FromBase64String(value); }
        catch (FormatException ex) { throw new InvalidDataException("VS Code authentication value is invalid.", ex); }
        if (bytes.Length != VsCodeBridgeProtocol.AuthenticationBytes)
        {
            CryptographicOperations.ZeroMemory(bytes);
            throw new InvalidDataException("VS Code authentication value has an invalid length.");
        }
        return bytes;
    }

    private static bool IsString(JsonElement root, string name, string expected) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String && value.GetString() == expected;
    private static string GetString(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
    private static string? NullString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;
    private static int GetInt(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.TryGetInt32(out var result) ? result : -1;
    private void NotifyChanged()
    {
        try { Changed?.Invoke(); }
        catch { /* UI observation cannot change transport security or lifetime. */ }
    }
    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private sealed class BridgeConnection(NamedPipeServerStream pipe)
    {
        public NamedPipeServerStream Pipe { get; } = pipe;
        public TaskCompletionSource<bool> Closed { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Close()
        {
            try { Pipe.Dispose(); } catch { }
            Closed.TrySetResult(true);
        }
    }
}
