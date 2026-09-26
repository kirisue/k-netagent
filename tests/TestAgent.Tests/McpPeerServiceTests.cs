using System.Reflection;
using TestAgent.Core;
using TestAgent.Infrastructure;
using Xunit;

namespace TestAgent.Tests;

public sealed class McpPeerServiceTests
{
    [Fact]
    public async Task Profile_store_round_trips_bounded_loopback_configuration()
    {
        using var directory = new TemporaryDirectory();
        var store = new JsonMcpPeerProfileStore(new AppPaths(directory.Path));
        var profile = new McpPeerProfile("local-peer", "Local peer", McpPeerKind.CustomHttp, true,
            Endpoint: "http://127.0.0.1:43123/mcp", AllowedTools: ["analyze"]);

        await store.SaveAsync(profile);
        var loaded = Assert.Single(await store.ListAsync());

        Assert.Equal(profile.Id, loaded.Id);
        Assert.Equal(profile.Endpoint, loaded.Endpoint);
        Assert.Equal(["analyze"], loaded.AllowedTools);
        Assert.Null(loaded.ExecutablePath);
    }

    [Theory]
    [InlineData("https://example.com/mcp")]
    [InlineData("http://192.168.1.10/mcp")]
    [InlineData("http://user:pass@127.0.0.1/mcp")]
    [InlineData("http://127.0.0.1/mcp?token=secret")]
    public async Task Profile_store_rejects_non_loopback_or_credentialed_http(string endpoint)
    {
        using var directory = new TemporaryDirectory();
        var store = new JsonMcpPeerProfileStore(new AppPaths(directory.Path));
        var profile = new McpPeerProfile("unsafe", "Unsafe", McpPeerKind.CustomHttp, true,
            Endpoint: endpoint);

        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(profile));
    }

    [Fact]
    public async Task Disconnected_service_never_lists_or_calls_a_peer_implicitly()
    {
        using var directory = new TemporaryDirectory();
        var paths = new AppPaths(directory.Path);
        var store = new JsonMcpPeerProfileStore(paths);
        await store.SaveAsync(new("local", "Local", McpPeerKind.CustomHttp, true,
            Endpoint: "http://127.0.0.1:43124/mcp", AllowedTools: ["analyze"]));
        await using var service = new McpPeerService(store, new WorkspaceLocator());

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ListToolsAsync("local"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CallToolAsync("local", "analyze",
            System.Text.Json.JsonSerializer.SerializeToElement(new { })));

        var peer = Assert.Single(await service.ListAsync());
        Assert.Equal(McpPeerConnectionState.Disconnected, peer.State);
    }

    [Fact]
    public async Task Connect_rejects_an_executable_staged_inside_the_agent_workspace_before_launch()
    {
        using var directory = new TemporaryDirectory();
        var workspace = new WorkspaceLocator();
        var fakeExecutable = Path.Combine(workspace.Root, $".mcp-peer-test-{Guid.NewGuid():N}.exe");
        await File.WriteAllBytesAsync(fakeExecutable, [0x4D, 0x5A]);
        try
        {
            var store = new JsonMcpPeerProfileStore(new AppPaths(directory.Path));
            await store.SaveAsync(new("fake-codex", "Fake Codex", McpPeerKind.Codex, true,
                ExecutablePath: fakeExecutable, AllowedTools: ["codex"]));
            await using var service = new McpPeerService(store, workspace);

            var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
                service.ConnectAsync("fake-codex"));
            Assert.Contains("workspace", error.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(fakeExecutable);
        }
    }

    [Theory]
    [InlineData("..\\outside.txt")]
    [InlineData("C:\\Windows\\win.ini")]
    [InlineData("%USERPROFILE%\\secret.txt")]
    [InlineData("file:///C:/Windows/win.ini")]
    public void Claude_path_policy_rejects_workspace_escape_forms(string value)
    {
        using var directory = new TemporaryDirectory();
        using var service = new McpPeerService(new AppPaths(directory.Path), new WorkspaceLocator());
        var method = typeof(McpPeerService).GetMethod("ValidateWorkspaceRelativePeerPath",
            BindingFlags.Instance | BindingFlags.NonPublic)!;

        var exception = Assert.Throws<TargetInvocationException>(() => method.Invoke(service, [value]));
        Assert.IsType<InvalidDataException>(exception.InnerException);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "TestAgent.Tests", Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, true);
        }
    }
}
