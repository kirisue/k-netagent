using TestAgent.Core;
using TestAgent.Infrastructure;
using Xunit;

namespace TestAgent.Tests;

public sealed class WorkspaceTaskFeatureTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "KNetAgent-task-feature-" + Guid.NewGuid().ToString("N"));

    public WorkspaceTaskFeatureTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    [Fact]
    public async Task Task_store_filters_plans_and_checkpoints_by_workspace()
    {
        var store = new JsonTaskPlanStore(new AppPaths(Path.Combine(_root, "state")));
        var first = Plan("one", "WS-ONE");
        var second = Plan("two", "WS-TWO");
        await store.SavePlanAsync(first);
        await store.SavePlanAsync(second);
        await store.SaveAsync(Checkpoint(first));
        await store.SaveAsync(Checkpoint(second));

        Assert.Equal([first.Id], (await store.ListPlansAsync("WS-ONE")).Select(plan => plan.Id));
        Assert.Null(await store.LoadPlanAsync(first.Id, "WS-TWO"));
        Assert.Null(await store.LoadAsync(first.Id, "WS-TWO"));
        Assert.Equal("WS-ONE", (await store.LoadAsync(first.Id, "WS-ONE"))!.WorkspaceId);
    }

    [Fact]
    public void Task_fingerprint_binds_the_workspace_identity()
    {
        var first = Plan("same", "WS-ONE");
        var second = first with { WorkspaceId = "WS-TWO" };
        Assert.NotEqual(TaskGraphValidator.Fingerprint(first), TaskGraphValidator.Fingerprint(second));
    }

    [Fact]
    public async Task Executor_rejects_same_graph_checkpoint_from_another_workspace_without_overwriting_it()
    {
        var plan = Plan("collision", "WS-ONE");
        var other = plan with { WorkspaceId = "WS-TWO" };
        var checkpoint = Checkpoint(other);
        var store = new FakeCheckpointStore(checkpoint);
        var executor = new SequentialTaskGraphExecutor(new NeverNodeRunner(), store);

        var error = await Assert.ThrowsAsync<TaskGraphValidationException>(() => executor.RunAsync(plan));

        Assert.Contains("workspace", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Same(checkpoint, store.Current);
        Assert.Equal(0, store.SaveCount);
    }

    [Fact]
    public async Task Explorer_is_bounded_and_hides_sensitive_or_generated_paths()
    {
        var workspaceRoot = Path.Combine(_root, "workspace");
        Directory.CreateDirectory(Path.Combine(workspaceRoot, "src"));
        Directory.CreateDirectory(Path.Combine(workspaceRoot, "bin"));
        await File.WriteAllTextAsync(Path.Combine(workspaceRoot, "src", "code.cs"), "class Example { }");
        await File.WriteAllTextAsync(Path.Combine(workspaceRoot, ".env"), "SECRET=value");
        await File.WriteAllTextAsync(Path.Combine(workspaceRoot, "token.txt"), "token=value");
        await File.WriteAllTextAsync(Path.Combine(workspaceRoot, "bin", "generated.dll"), "not-real");
        var explorer = new BoundedWorkspaceExplorer(new WorkspaceLocator(workspaceRoot));

        var snapshot = await explorer.ScanAsync();

        Assert.Contains(snapshot.Entries, entry => entry.RelativePath == "src/code.cs");
        Assert.DoesNotContain(snapshot.Entries, entry => entry.RelativePath.Contains(".env", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(snapshot.Entries, entry => entry.RelativePath.Contains("token", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(snapshot.Entries, entry => entry.RelativePath.StartsWith("bin/", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("class Example { }", await explorer.ReadPreviewAsync("src/code.cs"));
        await Assert.ThrowsAsync<FileNotFoundException>(() => explorer.ReadPreviewAsync("../outside.txt"));
    }

    [Fact]
    public async Task Change_source_falls_back_only_to_approved_successful_modified_files()
    {
        var workspaceRoot = Path.Combine(_root, "plain-workspace");
        Directory.CreateDirectory(workspaceRoot);
        await File.WriteAllTextAsync(Path.Combine(workspaceRoot, "approved.txt"), "changed");
        var now = DateTimeOffset.UtcNow;
        var invocations = new[]
        {
            new ToolInvocationRecord("approved", "{}", ToolExecutionStatus.Success, "done", null, now, 1, true,
                ["approved.txt", ".ssh/id_rsa", "token.txt"]),
            new ToolInvocationRecord("denied", "{}", ToolExecutionStatus.Success, "done", null, now, 1, false, ["denied.txt"]),
            new ToolInvocationRecord("failed", "{}", ToolExecutionStatus.Failed, "failed", "failed", now, 1, true, ["failed.txt"])
        };
        var session = new ToolSession("id", "TASK-one", "edit_file", ToolSessionState.Completed,
            3, 2, 1, null, invocations, now, now);
        var source = new GitOrApprovedToolWorkspaceChangeSource(new WorkspaceLocator(workspaceRoot),
            new FakeToolSessions(session));

        var snapshot = await source.GetChangesAsync("TASK-one");

        Assert.Equal(WorkspaceChangeSource.ApprovedToolResults, snapshot.Source);
        var file = Assert.Single(snapshot.Files);
        Assert.Equal("approved.txt", file.RelativePath);
        Assert.Null(file.Patch);
        Assert.Contains("no trusted before-image", file.PatchUnavailableReason);
    }

    [Fact]
    public async Task Change_source_returns_a_real_git_patch_when_trusted_git_is_available()
    {
        var workspaceRoot = Path.Combine(_root, "git-workspace");
        Directory.CreateDirectory(workspaceRoot);
        string git;
        try { git = TrustedDeveloperExecutable.Resolve("git", workspaceRoot); }
        catch (InvalidDataException) { return; }
        RunGit(git, workspaceRoot, "init", "--quiet");
        await File.WriteAllTextAsync(Path.Combine(workspaceRoot, "safe.txt"), "before\n");
        RunGit(git, workspaceRoot, "add", "safe.txt");
        RunGit(git, workspaceRoot, "-c", "user.name=KNetAgent Test", "-c", "user.email=test@example.invalid",
            "-c", "core.hooksPath=NUL", "commit", "--quiet", "-m", "baseline");
        await File.WriteAllTextAsync(Path.Combine(workspaceRoot, "safe.txt"), "after\n");
        var source = new GitOrApprovedToolWorkspaceChangeSource(new WorkspaceLocator(workspaceRoot),
            new FakeToolSessions());

        var snapshot = await source.GetChangesAsync();

        Assert.Equal(WorkspaceChangeSource.Git, snapshot.Source);
        var change = Assert.Single(snapshot.Files);
        Assert.Equal("safe.txt", change.RelativePath);
        Assert.Contains("-before", change.Patch);
        Assert.Contains("+after", change.Patch);
    }

    private static TaskGraphPlan Plan(string id, string workspaceId) =>
        new("TASK-" + id, "goal", [new("node", "Node", "Do work", [])], WorkspaceId: workspaceId);

    private static TaskGraphCheckpoint Checkpoint(TaskGraphPlan plan)
    {
        var now = DateTimeOffset.UtcNow;
        return new(plan.Id, TaskGraphValidator.Fingerprint(plan), TaskGraphStatus.Ready,
            [new("node", TaskNodeStatus.Pending, 0, null, null, null, null)], now, now,
            WorkspaceId: plan.WorkspaceId);
    }

    private static void RunGit(string executable, string workingDirectory, params string[] arguments)
    {
        var start = new System.Diagnostics.ProcessStartInfo(executable)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        TrustedDeveloperExecutable.ApplySafeEnvironment(start);
        using var process = System.Diagnostics.Process.Start(start) ?? throw new InvalidOperationException("Git did not start.");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, output + error);
    }

    private sealed class FakeToolSessions(params ToolSession[] sessions) : IToolSessionCoordinator
    {
        public Task<IReadOnlyList<ToolSession>> ListAsync(string? parentSessionId = null, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ToolSession>>(sessions.Where(session => parentSessionId is null ||
                session.ParentSessionId == parentSessionId).ToArray());
        public Task<IReadOnlyList<ToolSession>> EnsureSessionsAsync(string parentSessionId, IReadOnlyList<ToolDefinition> definitions, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ToolSession> StartAsync(ToolRequest request, bool approved, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ToolSession> CompleteAsync(ToolSession session, ToolRequest request, ToolResult result, bool approved, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string?> GetStrategyHintAsync(string parentSessionId, string toolName, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ToolSession> AcknowledgeNeedsReviewAsync(string parentSessionId, string toolName, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class NeverNodeRunner : ITaskNodeRunner
    {
        public Task<TaskNodeRunResult> RunAsync(TaskNodeExecutionContext context, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("A cross-workspace checkpoint must be rejected before execution.");
    }

    private sealed class FakeCheckpointStore(TaskGraphCheckpoint checkpoint) : ITaskGraphCheckpointStore
    {
        public TaskGraphCheckpoint Current { get; private set; } = checkpoint;
        public int SaveCount { get; private set; }
        public Task<TaskGraphCheckpoint?> LoadAsync(string graphId, CancellationToken cancellationToken = default) =>
            Task.FromResult<TaskGraphCheckpoint?>(Current);
        public Task SaveAsync(TaskGraphCheckpoint value, CancellationToken cancellationToken = default)
        {
            Current = value;
            SaveCount++;
            return Task.CompletedTask;
        }
    }
}
