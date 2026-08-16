using System.Diagnostics;
using System.Text.Json;
using TestAgent.Core;
using TestAgent.Infrastructure;
using Xunit;

namespace TestAgent.Tests;

public sealed class BackgroundCommandTests
{
    [Fact]
    public async Task Background_tools_reject_a_job_owned_by_another_agent_scope()
    {
        var job=new BackgroundCommandJob("BG-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","other-scope",DotNetExecutable(),Path.GetTempPath(),"other",BackgroundCommandState.Completed,DateTimeOffset.UtcNow,ExitCode:0);
        var service=new FixedService(job);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>new GetBackgroundCommandTool(service).ExecuteAsync(new("get","get_background_command",$"{{\"jobId\":\"{job.Id}\"}}","my-scope")));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>new ReadBackgroundOutputTool(service).ExecuteAsync(new("read","read_background_output",$"{{\"jobId\":\"{job.Id}\"}}","my-scope")));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>new StopBackgroundCommandTool(service).ExecuteAsync(new("stop","stop_background_command",$"{{\"jobId\":\"{job.Id}\"}}","my-scope")));
    }
    [Fact]
    public async Task Start_returns_quickly_and_output_can_be_read_incrementally()
    {
        using var temporary = new TemporaryDirectory();
        using var service = new BackgroundCommandService(temporary.Path);
        using var agentTurn = new CancellationTokenSource();
        var stopwatch = Stopwatch.StartNew();

        var started = await service.StartAsync(new BackgroundCommandSpec(
            "chat-one",
            DotNetExecutable(),
            ["--version"],
            temporary.Path,
            TimeoutSeconds: 30,
            DisplayName: "Report dotnet version"), agentTurn.Token);
        stopwatch.Stop();
        agentTurn.Cancel();

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2), $"Start took {stopwatch.Elapsed}.");
        Assert.Equal(BackgroundCommandState.Running, started.State);
        var completed = await WaitForTerminalAsync(service, started.Id);
        Assert.Equal(BackgroundCommandState.Completed, completed.State);
        Assert.Equal(0, completed.ExitCode);

        var first = await service.ReadOutputAsync(started.Id, 0, 1024);
        Assert.Contains("[stdout]", first.Content, StringComparison.Ordinal);
        Assert.Matches(@"\d+\.\d+", first.Content);
        Assert.True(first.NextCursor > first.Cursor);

        var second = await service.ReadOutputAsync(started.Id, first.NextCursor, 1024);
        Assert.Equal(first.NextCursor, second.Cursor);
        Assert.Equal(second.Cursor, second.NextCursor);
        Assert.Empty(second.Content);
        Assert.True(second.IsComplete);
    }

    [Fact]
    public async Task Stop_kills_the_live_process_tree_and_persists_stopped_state()
    {
        using var temporary = new TemporaryDirectory();
        using var service = new BackgroundCommandService(temporary.Path);
        var started = await service.StartAsync(new BackgroundCommandSpec(
            "chat-stop",
            PingExecutable(),
            ["127.0.0.1", "-n", "30", "-w", "1000"],
            temporary.Path,
            TimeoutSeconds: 60));

        await Task.Delay(150);
        var stopped = await service.StopAsync(started.Id);

        Assert.Equal(BackgroundCommandState.Stopped, stopped.State);
        Assert.NotNull(stopped.CompletedAt);
        Assert.Null(stopped.ProcessId);
        Assert.Equal(BackgroundCommandState.Stopped, (await service.GetAsync(started.Id))!.State);
    }

    [Fact]
    public async Task Restart_reconcile_marks_active_records_needs_review_without_using_the_old_pid()
    {
        using var temporary = new TemporaryDirectory();
        var job = new BackgroundCommandJob(
            "BG-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            "chat-restart",
            PingExecutable(),
            temporary.Path,
            "orphaned command",
            BackgroundCommandState.Running,
            DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow.AddMinutes(-1),
            ProcessId: int.MaxValue);
        var json = JsonSerializer.Serialize(
            job,
            new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });
        await File.WriteAllTextAsync(System.IO.Path.Combine(temporary.Path, job.Id + ".json"), json);

        using var service = new BackgroundCommandService(temporary.Path);
        var result = Assert.Single(await service.ReconcileAsync());

        Assert.Equal(BackgroundCommandState.NeedsReview, result.State);
        Assert.Null(result.ProcessId);
        Assert.NotNull(result.CompletedAt);
        Assert.Contains("restarted", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(BackgroundCommandState.NeedsReview, (await service.GetAsync(job.Id))!.State);
    }

    private static async Task<BackgroundCommandJob> WaitForTerminalAsync(
        IBackgroundCommandService service,
        string jobId)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (true)
        {
            var job = await service.GetAsync(jobId, timeout.Token)
                ?? throw new InvalidOperationException("The background command disappeared.");
            if (job.State is not (BackgroundCommandState.Starting or BackgroundCommandState.Running or BackgroundCommandState.Stopping))
                return job;
            await Task.Delay(25, timeout.Token);
        }
    }

    private static string DotNetExecutable()
    {
        var candidates = new[]
        {
            Environment.GetEnvironmentVariable("DOTNET_HOST_PATH"),
            System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe")
        };
        return candidates.FirstOrDefault(candidate => !string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate))
            ?? throw new InvalidOperationException("The dotnet host was not found for this test.");
    }

    private static string PingExecutable()
    {
        var path = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "PING.EXE");
        if (!File.Exists(path)) throw new InvalidOperationException("PING.EXE was not found for this Windows test.");
        return path;
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "TestAgent.BackgroundCommands.Tests",
            Guid.NewGuid().ToString("N"));

        public TemporaryDirectory() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch { }
        }
    }

    private sealed class FixedService(BackgroundCommandJob job):IBackgroundCommandService
    {
        public Task<BackgroundCommandJob> StartAsync(BackgroundCommandSpec spec,CancellationToken cancellationToken=default)=>throw new NotSupportedException();
        public Task<BackgroundCommandJob?> GetAsync(string id,CancellationToken cancellationToken=default)=>Task.FromResult<BackgroundCommandJob?>(job);
        public Task<IReadOnlyList<BackgroundCommandJob>> ListAsync(string? scopeId=null,CancellationToken cancellationToken=default)=>Task.FromResult<IReadOnlyList<BackgroundCommandJob>>([job]);
        public Task<BackgroundCommandOutputChunk> ReadOutputAsync(string id,long cursor=0,int maxBytes=65536,CancellationToken cancellationToken=default)=>throw new InvalidOperationException("Scope check must happen first.");
        public Task<BackgroundCommandJob> StopAsync(string id,CancellationToken cancellationToken=default)=>throw new InvalidOperationException("Scope check must happen first.");
        public Task<IReadOnlyList<BackgroundCommandJob>> ReconcileAsync(CancellationToken cancellationToken=default)=>Task.FromResult<IReadOnlyList<BackgroundCommandJob>>([job]);
    }
}
