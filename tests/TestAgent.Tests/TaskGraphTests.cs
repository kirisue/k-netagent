using TestAgent.Core;
using Xunit;

namespace TestAgent.Tests;

public sealed class TaskGraphTests
{
    [Fact]
    public void Validate_RejectsCyclesMissingDependenciesAndOversizedPlans()
    {
        var cycle = Plan(
            Node("a", "b"),
            Node("b", "a"));
        Assert.Throws<TaskGraphValidationException>(() => TaskGraphValidator.Validate(cycle));

        var missing = Plan(Node("a", "missing"));
        Assert.Throws<TaskGraphValidationException>(() => TaskGraphValidator.Validate(missing));

        var tooLarge = Plan(Enumerable.Range(0, TaskGraphValidator.MaxNodes + 1)
            .Select(index => Node($"n{index}"))
            .ToArray());
        Assert.Throws<TaskGraphValidationException>(() => TaskGraphValidator.Validate(tooLarge));
    }

    [Fact]
    public void Validate_RejectsUnsafeOrExcessiveRelevantPaths()
    {
        var unsafePath = Plan(new TaskGraphNode("a", "A", "Analyze.", [], RelevantPaths: ["../secret"]));
        Assert.Throws<TaskGraphValidationException>(() => TaskGraphValidator.Validate(unsafePath));

        var tooManyPaths = Plan(new TaskGraphNode("a", "A", "Analyze.", [], RelevantPaths:
            Enumerable.Range(0, TaskGraphValidator.MaxRelevantPathsPerNode + 1).Select(index => $"src/{index}").ToArray()));
        Assert.Throws<TaskGraphValidationException>(() => TaskGraphValidator.Validate(tooManyPaths));
    }

    [Fact]
    public void Fingerprint_CoversModePathsAndAcceptanceCriteria()
    {
        var original = Plan(new TaskGraphNode("a", "A", "Analyze.", [], TaskWorkMode.Analysis, ["src"], ["Report findings."]));
        var changed = Plan(new TaskGraphNode("a", "A", "Analyze.", [], TaskWorkMode.Verification, ["tests"], ["Tests pass."]));

        Assert.NotEqual(TaskGraphValidator.Fingerprint(original), TaskGraphValidator.Fingerprint(changed));
    }

    [Fact]
    public async Task RunAsync_ExecutesSequentiallyInStableDependencyOrder()
    {
        var store = new MemoryCheckpointStore();
        var runner = new RecordingRunner((context, _) =>
            Task.FromResult(TaskNodeRunResult.Completed("done-" + context.Node.Id)));
        var executor = new SequentialTaskGraphExecutor(runner, store);
        var notifications = new List<TaskGraphCheckpoint>();
        executor.CheckpointChanged += notifications.Add;
        var plan = Plan(
            Node("last", "left", "right"),
            Node("left", "root"),
            Node("root"),
            Node("right", "root"));

        var result = await executor.RunAsync(plan);

        Assert.Equal(TaskGraphStatus.Completed, result.Status);
        Assert.Equal(["root", "left", "right", "last"], runner.Calls);
        Assert.All(result.Nodes, state => Assert.Equal(TaskNodeStatus.Completed, state.Status));
        Assert.All(result.Nodes, state => Assert.Equal(1, state.Attempts));
        Assert.True(store.Saves.Count >= 6);
        Assert.Equal(store.Saves.Count, notifications.Count);
    }

    [Fact]
    public async Task RunAsync_FailureBlocksDependentsButContinuesIndependentNodes()
    {
        var store = new MemoryCheckpointStore();
        var runner = new RecordingRunner((context, _) => Task.FromResult(
            context.Node.Id == "failure"
                ? TaskNodeRunResult.Failed("expected failure")
                : TaskNodeRunResult.Completed("ok")));
        var executor = new SequentialTaskGraphExecutor(runner, store);
        var plan = Plan(
            Node("failure"),
            Node("blocked", "failure"),
            Node("transitively-blocked", "blocked"),
            Node("independent"));

        var result = await executor.RunAsync(plan);

        Assert.Equal(TaskGraphStatus.Failed, result.Status);
        Assert.Equal(["failure", "independent"], runner.Calls);
        Assert.Equal(TaskNodeStatus.Failed, State(result, "failure").Status);
        Assert.Equal(TaskNodeStatus.Blocked, State(result, "blocked").Status);
        Assert.Equal(TaskNodeStatus.Blocked, State(result, "transitively-blocked").Status);
        Assert.Equal(TaskNodeStatus.Completed, State(result, "independent").Status);
    }

    [Fact]
    public async Task RunAsync_CancellationIsCheckpointedAsInterrupted()
    {
        var store = new MemoryCheckpointStore();
        var runner = new RecordingRunner((_, _) => throw new OperationCanceledException("test interruption"));
        var executor = new SequentialTaskGraphExecutor(runner, store);

        var result = await executor.RunAsync(Plan(Node("work")));

        Assert.Equal(TaskGraphStatus.Interrupted, result.Status);
        Assert.Equal(TaskNodeStatus.Interrupted, State(result, "work").Status);
        Assert.Equal(1, State(result, "work").Attempts);
        Assert.Equal(TaskGraphStatus.Interrupted, store.Current!.Status);
    }

    [Fact]
    public async Task RunAsync_ResumesInterruptedNodeWithoutRepeatingCompletedWork()
    {
        var store = new MemoryCheckpointStore();
        var interruptedOnce = false;
        var runner = new RecordingRunner((context, _) =>
        {
            if (context.Node.Id == "second" && !interruptedOnce)
            {
                interruptedOnce = true;
                throw new OperationCanceledException("simulated process stop");
            }
            return Task.FromResult(TaskNodeRunResult.Completed("result-" + context.Node.Id));
        });
        var executor = new SequentialTaskGraphExecutor(runner, store);
        var plan = Plan(Node("first"), Node("second", "first"));

        var interrupted = await executor.RunAsync(plan);
        var resumed = await executor.RunAsync(plan);

        Assert.Equal(TaskGraphStatus.Interrupted, interrupted.Status);
        Assert.Equal(TaskGraphStatus.Completed, resumed.Status);
        Assert.Equal(1, runner.Calls.Count(id => id == "first"));
        Assert.Equal(2, runner.Calls.Count(id => id == "second"));
        Assert.Equal(1, State(resumed, "first").Attempts);
        Assert.Equal(2, State(resumed, "second").Attempts);
    }

    [Fact]
    public async Task RunAsync_RequiresReviewForNodeLeftRunningByPreviousProcess()
    {
        var plan = Plan(Node("work"));
        var now = DateTimeOffset.UtcNow;
        var store = new MemoryCheckpointStore
        {
            Current = new TaskGraphCheckpoint(
                plan.Id,
                TaskGraphValidator.Fingerprint(plan),
                TaskGraphStatus.Running,
                [new TaskNodeCheckpoint("work", TaskNodeStatus.Running, 1, null, null, now, null)],
                now,
                now)
        };
        var runner = new RecordingRunner((context, _) =>
            Task.FromResult(TaskNodeRunResult.Completed("recovered")));
        var executor = new SequentialTaskGraphExecutor(runner, store);

        var resumed = await executor.RunAsync(plan);

        Assert.Equal(TaskGraphStatus.NeedsReview, resumed.Status);
        Assert.Empty(runner.Calls);
        Assert.Equal(1, State(resumed, "work").Attempts);
        Assert.Equal(TaskNodeStatus.NeedsReview, State(resumed, "work").Status);
        Assert.Contains("unknown outcome", State(resumed, "work").Error);
    }

    [Fact]
    public async Task RunAsync_RejectsChangedPlanWhenCheckpointExists()
    {
        var store = new MemoryCheckpointStore();
        var runner = new RecordingRunner((_, _) => Task.FromResult(TaskNodeRunResult.Completed()));
        var executor = new SequentialTaskGraphExecutor(runner, store);
        var original = Plan(Node("one"));
        await executor.RunAsync(original);
        var changed = original with { Goal = "changed goal" };

        await Assert.ThrowsAsync<TaskGraphValidationException>(() => executor.RunAsync(changed));
    }

    private static TaskGraphPlan Plan(params TaskGraphNode[] nodes) =>
        new("graph-1", "Complete the requested work.", nodes);

    private static TaskGraphNode Node(string id, params string[] dependencies) =>
        new(id, "Task " + id, "Perform " + id + ".", dependencies);

    private static TaskNodeCheckpoint State(TaskGraphCheckpoint checkpoint, string id) =>
        checkpoint.Nodes.Single(node => node.NodeId == id);

    private sealed class RecordingRunner(
        Func<TaskNodeExecutionContext, CancellationToken, Task<TaskNodeRunResult>> execute) : ITaskNodeRunner
    {
        public List<string> Calls { get; } = [];

        public Task<TaskNodeRunResult> RunAsync(TaskNodeExecutionContext context, CancellationToken cancellationToken)
        {
            Calls.Add(context.Node.Id);
            return execute(context, cancellationToken);
        }
    }

    private sealed class MemoryCheckpointStore : ITaskGraphCheckpointStore
    {
        public TaskGraphCheckpoint? Current { get; set; }
        public List<TaskGraphCheckpoint> Saves { get; } = [];

        public Task<TaskGraphCheckpoint?> LoadAsync(string graphId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Current);

        public Task SaveAsync(TaskGraphCheckpoint checkpoint, CancellationToken cancellationToken = default)
        {
            Current = checkpoint;
            Saves.Add(checkpoint);
            return Task.CompletedTask;
        }
    }
}
