using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Threading;
using TestAgent.Infrastructure;
using Xunit;

namespace TestAgent.Tests;

public sealed class VsCodeBridgeProtocolTests
{
    [Fact]
    public void Hmac_fixtures_match_the_dependency_free_node_protocol()
    {
        var secret = Enumerable.Range(0, 32).Select(value => (byte)value).ToArray();
        var challenge = Enumerable.Range(32, 32).Select(value => (byte)value).ToArray();
        var nonce = Enumerable.Range(64, 32).Select(value => (byte)value).ToArray();
        Assert.Equal("2089BF69FCEE708A781BB4ACA3E0C5C44BE4128C8CF86417E9DEA51D8B2FD59E",
            Convert.ToHexString(VsCodeBridgeProtocol.ComputeAuthenticationMac(secret, challenge, nonce)));
        Assert.Equal("8EA5BC5F851650FB2BB9FEA6968009F0347684961F0AAC9C684D7BFAB58D6C51",
            Convert.ToHexString(VsCodeBridgeProtocol.ComputeServerProof(secret, challenge, nonce)));
    }

    [Fact]
    public async Task Frames_round_trip_and_reject_lengths_above_256_kib()
    {
        await using var stream = new MemoryStream();
        await VsCodeBridgeProtocol.WriteFrameAsync(stream, new { type = "fixture", value = 42 });
        stream.Position = 0;
        using var frame = await VsCodeBridgeProtocol.ReadFrameAsync(stream);
        Assert.Equal("fixture", frame.RootElement.GetProperty("type").GetString());
        Assert.Equal(42, frame.RootElement.GetProperty("value").GetInt32());

        var invalid = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(invalid, VsCodeBridgeProtocol.MaxFrameBytes + 1);
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
        {
            using var _ = await VsCodeBridgeProtocol.ReadFrameAsync(new MemoryStream(invalid));
        });
        await Assert.ThrowsAsync<InvalidDataException>(() => VsCodeBridgeProtocol.WriteFrameAsync(
            new MemoryStream(), new { value = new string('x', VsCodeBridgeProtocol.MaxFrameBytes) }));
    }

    [Fact]
    public void Workspace_claim_requires_trusted_local_single_exact_file_root()
    {
        using var workspace = new TestWorkspace();
        var valid = new VsCodeBridgeWorkspaceClaim(true, "desktop", null,
            [new("file", workspace.Root)]);
        VsCodeBridgeProtocol.ValidateWorkspaceClaim(workspace.Root, valid);

        Assert.Throws<InvalidDataException>(() => VsCodeBridgeProtocol.ValidateWorkspaceClaim(workspace.Root,
            valid with { Trusted = false }));
        Assert.Throws<InvalidDataException>(() => VsCodeBridgeProtocol.ValidateWorkspaceClaim(workspace.Root,
            valid with { RemoteName = "ssh-remote" }));
        Assert.Throws<InvalidDataException>(() => VsCodeBridgeProtocol.ValidateWorkspaceClaim(workspace.Root,
            valid with { UiKind = "web" }));
        Assert.Throws<InvalidDataException>(() => VsCodeBridgeProtocol.ValidateWorkspaceClaim(workspace.Root,
            valid with { Folders = [new("file", workspace.Root), new("file", workspace.Root)] }));
        Assert.Throws<InvalidDataException>(() => VsCodeBridgeProtocol.ValidateWorkspaceClaim(workspace.Root,
            valid with { Folders = [new("vscode-remote", workspace.Root)] }));
        var mismatch = Path.Combine(Path.GetTempPath(), "KNetAgent-vscode-mismatch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(mismatch);
        try
        {
            Assert.Throws<InvalidDataException>(() => VsCodeBridgeProtocol.ValidateWorkspaceClaim(workspace.Root,
                valid with { Folders = [new("file", mismatch)] }));
        }
        finally { Directory.Delete(mismatch); }
    }

    [Fact]
    public void Pairing_uses_unique_random_pipe_names_and_256_bit_in_memory_secrets()
    {
        using var workspace = new TestWorkspace();
        using var first = new VsCodeBridgeServer(workspace.Locator);
        using var second = new VsCodeBridgeServer(workspace.Locator);
        var a = first.GetPairingInfo();
        var b = second.GetPairingInfo();
        Assert.Matches("^knetagent-[a-f0-9]{32}$", a.PipeName);
        Assert.NotEqual(a.PipeName, b.PipeName);
        Assert.Equal(32, Convert.FromBase64String(a.Secret).Length);
        Assert.Equal(32, Convert.FromBase64String(b.Secret).Length);
        Assert.NotEqual(a.Secret, b.Secret);
        var json = JsonSerializer.Serialize(a);
        Assert.Contains("\"pipeName\"", json);
        Assert.Contains("\"secret\"", json);
        Assert.DoesNotContain("\"PipeName\"", json);
    }

    [Fact]
    public async Task Query_without_a_pairing_fails_immediately_with_pairing_guidance()
    {
        using var workspace = new TestWorkspace();
        await using var server = new VsCodeBridgeServer(workspace.Locator,
            TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(8));
        var started = System.Diagnostics.Stopwatch.StartNew();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            server.QueryAsync("activeEditor"));
        started.Stop();
        Assert.Contains("not paired", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(1), $"Unpaired query took {started.Elapsed}.");
    }

    [Fact]
    public async Task Start_then_synchronous_dispose_completes_on_a_dispatcher_context()
    {
        using var workspace = new TestWorkspace();
        var completion = new TaskCompletionSource<TimeSpan>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(() =>
            {
                VsCodeBridgeServer? server = null;
                try
                {
                    server = new VsCodeBridgeServer(workspace.Locator);
                    var elapsed = Stopwatch.StartNew();
                    server.StartAsync().GetAwaiter().GetResult();
                    server.Dispose();
                    elapsed.Stop();
                    completion.TrySetResult(elapsed.Elapsed);
                }
                catch (Exception ex) { completion.TrySetException(ex); }
                finally
                {
                    try { server?.Dispose(); } catch { }
                    dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                }
            });
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();

        var elapsed = await completion.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(elapsed < TimeSpan.FromSeconds(2), $"Synchronous disposal took {elapsed}.");
        Assert.True(thread.Join(TimeSpan.FromSeconds(1)), "Dispatcher thread did not shut down.");
    }

    [Fact]
    public async Task Named_pipe_authenticates_queries_and_recovers_after_disconnect()
    {
        using var workspace = new TestWorkspace();
        await using var server = new VsCodeBridgeServer(workspace.Locator,
            TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
        await server.StartAsync();
        await using (var first = await TestClient.ConnectAsync(server.GetPairingInfo(), workspace.Root))
        {
            var response = first.RespondOnceAsync("activeEditor", new { hasEditor = false });
            var result = await server.QueryAsync("activeEditor");
            await response;
            Assert.False(result.GetProperty("hasEditor").GetBoolean());
        }

        await Assert.ThrowsAnyAsync<IOException>(() => server.QueryAsync("diagnostics"));
        Assert.False(server.IsConnected);

        await using var second = await TestClient.ConnectAsync(server.GetPairingInfo(), workspace.Root);
        var secondResponse = second.RespondOnceAsync("diagnostics",
            new { total = 0, truncated = false, items = Array.Empty<object>() });
        var recovered = await server.QueryAsync("diagnostics");
        await secondResponse;
        Assert.Equal(0, recovered.GetProperty("total").GetInt32());
        Assert.True(server.IsConnected);
    }

    [Fact]
    public async Task Idle_disconnect_is_detected_and_a_fresh_client_can_pair_without_a_query()
    {
        using var workspace = new TestWorkspace();
        await using var server = new VsCodeBridgeServer(workspace.Locator,
            TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(50));
        await server.StartAsync();
        var first = await TestClient.ConnectAsync(server.GetPairingInfo(), workspace.Root);

        await first.RespondToHeartbeatOnceAsync();
        Assert.True(server.IsConnected);
        await first.DisposeAsync();
        await WaitUntilAsync(() => !server.IsConnected, TimeSpan.FromSeconds(2));

        await using var second = await TestClient.ConnectAsync(server.GetPairingInfo(), workspace.Root);
        Assert.True(server.IsConnected);
    }

    [Fact]
    public async Task Named_pipe_rejects_an_invalid_hmac_then_accepts_a_fresh_client()
    {
        using var workspace = new TestWorkspace();
        await using var server = new VsCodeBridgeServer(workspace.Locator,
            TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
        await server.StartAsync();
        var pairing = server.GetPairingInfo();
        await using (var invalid = new NamedPipeClientStream(".", pairing.PipeName,
                         PipeDirection.InOut, PipeOptions.Asynchronous))
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await invalid.ConnectAsync(timeout.Token);
            using var challenge = await VsCodeBridgeProtocol.ReadFrameAsync(invalid, timeout.Token);
            Assert.Equal("challenge", challenge.RootElement.GetProperty("type").GetString());
            await VsCodeBridgeProtocol.WriteFrameAsync(invalid, new
            {
                type = "authenticate",
                protocol = VsCodeBridgeProtocol.Version,
                clientNonce = Convert.ToBase64String(new byte[32]),
                mac = Convert.ToBase64String(new byte[32])
            }, timeout.Token);
            await Assert.ThrowsAnyAsync<IOException>(async () =>
            {
                using var _ = await VsCodeBridgeProtocol.ReadFrameAsync(invalid, timeout.Token);
            });
        }

        await using var valid = await TestClient.ConnectAsync(pairing, workspace.Root);
        var response = valid.RespondOnceAsync("extensions",
            new { total = 0, truncated = false, items = Array.Empty<object>() });
        var result = await server.QueryAsync("extensions");
        await response;
        Assert.Equal(0, result.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task Connected_query_times_out_and_listener_accepts_a_fresh_pairing()
    {
        using var workspace = new TestWorkspace();
        await using var server = new VsCodeBridgeServer(workspace.Locator,
            TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(150));
        await server.StartAsync();
        await using var stalled = await TestClient.ConnectAsync(server.GetPairingInfo(), workspace.Root);
        await Assert.ThrowsAsync<TimeoutException>(() => server.QueryAsync("tasks"));
        Assert.False(server.IsConnected);

        await using var recoveredClient = await TestClient.ConnectAsync(server.GetPairingInfo(), workspace.Root);
        var response = recoveredClient.RespondOnceAsync("tasks",
            new { total = 0, truncated = false, items = Array.Empty<object>() });
        var result = await server.QueryAsync("tasks");
        await response;
        Assert.Equal(0, result.GetProperty("total").GetInt32());
    }

    private sealed class TestClient(NamedPipeClientStream pipe) : IAsyncDisposable
    {
        public static async Task<TestClient> ConnectAsync(VsCodeBridgePairingInfo pairing, string root)
        {
            var pipe = new NamedPipeClientStream(".", pairing.PipeName, PipeDirection.InOut,
                PipeOptions.Asynchronous);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                await pipe.ConnectAsync(timeout.Token);
                using var challengeFrame = await VsCodeBridgeProtocol.ReadFrameAsync(pipe, timeout.Token);
                var challengeRoot = challengeFrame.RootElement;
                Assert.Equal("challenge", challengeRoot.GetProperty("type").GetString());
                var challenge = Convert.FromBase64String(challengeRoot.GetProperty("challenge").GetString()!);
                var secret = Convert.FromBase64String(pairing.Secret);
                var nonce = RandomNumberGenerator.GetBytes(VsCodeBridgeProtocol.AuthenticationBytes);
                var mac = VsCodeBridgeProtocol.ComputeAuthenticationMac(secret, challenge, nonce);
                var expectedProof = VsCodeBridgeProtocol.ComputeServerProof(secret, challenge, nonce);
                try
                {
                    await VsCodeBridgeProtocol.WriteFrameAsync(pipe, new
                    {
                        type = "authenticate",
                        protocol = VsCodeBridgeProtocol.Version,
                        clientNonce = Convert.ToBase64String(nonce),
                        mac = Convert.ToBase64String(mac)
                    }, timeout.Token);
                    using var authenticated = await VsCodeBridgeProtocol.ReadFrameAsync(pipe, timeout.Token);
                    Assert.Equal("authenticated", authenticated.RootElement.GetProperty("type").GetString());
                    var proof = Convert.FromBase64String(authenticated.RootElement.GetProperty("serverMac").GetString()!);
                    try { Assert.True(CryptographicOperations.FixedTimeEquals(expectedProof, proof)); }
                    finally { CryptographicOperations.ZeroMemory(proof); }
                    await VsCodeBridgeProtocol.WriteFrameAsync(pipe, new
                    {
                        type = "workspace",
                        trusted = true,
                        uiKind = "desktop",
                        remoteName = (string?)null,
                        folders = new[] { new { scheme = "file", path = root } }
                    }, timeout.Token);
                    using var ready = await VsCodeBridgeProtocol.ReadFrameAsync(pipe, timeout.Token);
                    Assert.Equal("ready", ready.RootElement.GetProperty("type").GetString());
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(challenge);
                    CryptographicOperations.ZeroMemory(secret);
                    CryptographicOperations.ZeroMemory(nonce);
                    CryptographicOperations.ZeroMemory(mac);
                    CryptographicOperations.ZeroMemory(expectedProof);
                }
                return new(pipe);
            }
            catch
            {
                await pipe.DisposeAsync();
                throw;
            }
        }

        public async Task RespondOnceAsync(string operation, object result)
        {
            using var request = await VsCodeBridgeProtocol.ReadFrameAsync(pipe);
            Assert.Equal("request", request.RootElement.GetProperty("type").GetString());
            Assert.Equal(operation, request.RootElement.GetProperty("operation").GetString());
            var id = request.RootElement.GetProperty("id").GetString();
            await VsCodeBridgeProtocol.WriteFrameAsync(pipe,
                new { type = "response", id, ok = true, result });
        }

        public async Task RespondToHeartbeatOnceAsync()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            using var ping = await VsCodeBridgeProtocol.ReadFrameAsync(pipe, timeout.Token);
            Assert.Equal("ping", ping.RootElement.GetProperty("type").GetString());
            var id = ping.RootElement.GetProperty("id").GetString();
            Assert.Matches("^[a-f0-9]{32}$", id!);
            await VsCodeBridgeProtocol.WriteFrameAsync(pipe, new { type = "pong", id }, timeout.Token);
        }

        public ValueTask DisposeAsync() => pipe.DisposeAsync();
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!predicate() && stopwatch.Elapsed < timeout)
            await Task.Delay(20);
        Assert.True(predicate(), $"Condition was not met within {timeout}.");
    }

    private sealed class TestWorkspace : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(),
            "KNetAgent-vscode-bridge-" + Guid.NewGuid().ToString("N"));
        public WorkspaceLocator Locator { get; }
        public TestWorkspace()
        {
            Directory.CreateDirectory(Root);
            File.WriteAllText(Path.Combine(Root, "TestAgent.slnx"), "<Solution />");
            var old = Environment.CurrentDirectory;
            try { Environment.CurrentDirectory = Root; Locator = new WorkspaceLocator(); }
            finally { Environment.CurrentDirectory = old; }
            Assert.Equal(Path.GetFullPath(Root), Path.GetFullPath(Locator.Root));
        }
        public void Dispose() { try { Directory.Delete(Root, true); } catch { } }
    }
}
