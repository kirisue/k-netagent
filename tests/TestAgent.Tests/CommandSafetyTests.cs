using System.Text.Json;
using System.Text.RegularExpressions;
using TestAgent.Core;
using TestAgent.Infrastructure;
using Xunit;

namespace TestAgent.Tests;

public sealed class CommandSafetyTests
{
    [Fact]
    public void Developer_command_policy_excludes_sensitive_rg_paths_and_blocks_content_git_diff()
    {
        var prepared=DeveloperCommandPolicy.Prepare("rg",["needle","."],background:false);
        Assert.Contains("!**/v4_agent_config.json",prepared);
        Assert.Contains("!**/credentials.*",prepared);
        Assert.Throws<InvalidDataException>(()=>DeveloperCommandPolicy.Prepare("rg",["needle","--no-ignore-vcs","."],false));
        Assert.Throws<InvalidDataException>(()=>DeveloperCommandPolicy.Prepare("rg",["needle","v4_agent_config.json"],false));
        Assert.Throws<InvalidDataException>(()=>DeveloperCommandPolicy.Prepare("git",["diff"],false));
        Assert.Throws<InvalidDataException>(()=>DeveloperCommandPolicy.Prepare("git",["show","--stat","HEAD:README.md"],false));
        Assert.Throws<InvalidDataException>(()=>DeveloperCommandPolicy.Prepare("git",["diff","--patch-with-stat"],false));
        Assert.Throws<InvalidDataException>(()=>DeveloperCommandPolicy.Prepare("git",["log","--patch-with-raw"],false));
        Assert.NotEmpty(DeveloperCommandPolicy.Prepare("git",["status","--short"],false));
    }
    [Fact]
    public async Task Oversized_stdout_is_bounded_and_marked_truncated()
    {
        var root = NewWorkspace("large-output");
        var previousDirectory = Environment.CurrentDirectory;
        Environment.CurrentDirectory = root;
        try
        {
            WriteConsoleProject(root, "Emitter", "Console.Write(new string('x', 50_000));");
            var result = await ExecuteAsync(new
            {
                executable = "dotnet",
                arguments = "run --project Emitter/Emitter.csproj --nologo",
                workingDirectory = ".",
                timeoutSeconds = 60
            });

            Assert.Equal(ToolExecutionStatus.Success, result.Status);
            Assert.True(result.Truncated);
            Assert.True(result.Output.Length <= 30_100, $"Output had {result.Output.Length} characters.");
            Assert.Contains("output truncated", result.Output, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Environment.CurrentDirectory = previousDirectory;
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Caller_cancellation_is_reported_as_cancelled_not_timeout()
    {
        var root = NewWorkspace("cancel");
        var previousDirectory = Environment.CurrentDirectory;
        Environment.CurrentDirectory = root;
        try
        {
            WriteConsoleProject(root, "Sleeper", "Thread.Sleep(TimeSpan.FromSeconds(30));");
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(1));

            var result = await ExecuteAsync(new
            {
                executable = "dotnet",
                arguments = "run --project Sleeper/Sleeper.csproj --nologo",
                workingDirectory = ".",
                timeoutSeconds = 60
            }, cancellation.Token);

            Assert.Equal(ToolExecutionStatus.Cancelled, result.Status);
            Assert.Equal("cancelled", result.ErrorCode);
            Assert.NotEqual(ToolExecutionStatus.Timeout, result.Status);
        }
        finally
        {
            Environment.CurrentDirectory = previousDirectory;
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Workspace_dotnet_executable_cannot_shadow_trusted_sdk()
    {
        var root = NewWorkspace("path-shadow");
        var previousDirectory = Environment.CurrentDirectory;
        Environment.CurrentDirectory = root;
        try
        {
            var systemDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System);
            var harmlessImpostor = Path.Combine(systemDirectory, "where.exe");
            Assert.True(File.Exists(harmlessImpostor), "Windows where.exe is required for this regression test.");
            File.Copy(harmlessImpostor, Path.Combine(root, "dotnet.exe"));

            var result = await ExecuteAsync(new
            {
                executable = "dotnet",
                arguments = "--version",
                workingDirectory = ".",
                timeoutSeconds = 20
            });

            Assert.Equal(ToolExecutionStatus.Success, result.Status);
            Assert.Matches(new Regex(@"\b\d+\.\d+\.\d+", RegexOptions.CultureInvariant), result.Output);
        }
        finally
        {
            Environment.CurrentDirectory = previousDirectory;
            Directory.Delete(root, true);
        }
    }

    private static Task<ToolResult> ExecuteAsync(object arguments, CancellationToken cancellationToken = default) =>
        new RunDeveloperCommandTool(new WorkspaceLocator()).ExecuteAsync(
            new ToolRequest("command-request", "run_command", JsonSerializer.Serialize(arguments), "test-session"),
            cancellationToken);

    private static void WriteConsoleProject(string root, string name, string program)
    {
        var directory = Path.Combine(root, name);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, $"{name}.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings></PropertyGroup></Project>");
        File.WriteAllText(Path.Combine(directory, "Program.cs"), program);
    }

    private static string NewWorkspace(string name)
    {
        var root = Path.Combine(Path.GetTempPath(), $"TestAgent-command-{name}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "TestAgent.slnx"), "<Solution />");
        return root;
    }
}
