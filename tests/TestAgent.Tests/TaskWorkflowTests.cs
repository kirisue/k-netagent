using System.Runtime.CompilerServices;
using System.Text.Json;
using TestAgent.Core;
using TestAgent.Infrastructure;
using Xunit;

namespace TestAgent.Tests;

public sealed class TaskWorkflowTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TestAgent-workflow-" + Guid.NewGuid().ToString("N"));
    public TaskWorkflowTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    [Fact]
    public async Task Json_task_store_round_trips_plan_and_checkpoint()
    {
        var store = new JsonTaskPlanStore(new AppPaths(_root));
        var plan = new TaskGraphPlan("TASK-one", "goal", [new("a", "A", "Do A", [], TaskWorkMode.Coding, ["src"], ["builds"])]);
        await store.SavePlanAsync(plan);
        var checkpoint = new TaskGraphCheckpoint(plan.Id, TaskGraphValidator.Fingerprint(plan), TaskGraphStatus.Interrupted,
            [new("a", TaskNodeStatus.Interrupted, 1, null, "stopped", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        await store.SaveAsync(checkpoint);
        var loadedPlan = await store.LoadPlanAsync(plan.Id); var loadedCheckpoint = await store.LoadAsync(plan.Id);
        Assert.NotNull(loadedPlan); Assert.Equal(plan.Id, loadedPlan.Id); Assert.Equal(plan.Goal, loadedPlan.Goal);
        Assert.Single(loadedPlan.Nodes); Assert.Equal(plan.Nodes[0].Id, loadedPlan.Nodes[0].Id);
        Assert.Equal(plan.Nodes[0].Mode, loadedPlan.Nodes[0].Mode);
        Assert.Equal(plan.Nodes[0].RelevantPaths!.ToArray(), loadedPlan.Nodes[0].RelevantPaths!.ToArray());
        Assert.NotNull(loadedCheckpoint); Assert.Equal(checkpoint.GraphId, loadedCheckpoint.GraphId);
        Assert.Equal(checkpoint.Nodes.ToArray(), loadedCheckpoint.Nodes.ToArray());
        Assert.Single(await store.ListPlansAsync());
    }

    [Fact]
    public async Task Corrupt_task_state_is_quarantined_and_reported()
    {
        var paths = new AppPaths(_root); var store = new JsonTaskPlanStore(paths); Directory.CreateDirectory(paths.Tasks);
        await File.WriteAllTextAsync(Path.Combine(paths.Tasks, "TASK-bad.checkpoint.json"), "{broken");
        await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadAsync("TASK-bad"));
        Assert.True(File.Exists(Path.Combine(paths.Tasks, "TASK-bad.checkpoint.json.corrupt")));
    }

    [Fact]
    public async Task Planner_repairs_invalid_json_once_and_persists_valid_bounded_plan()
    {
        var provider = new ScriptedProvider("not json", """{"nodes":[{"id":"step-1","title":"Inspect","instructions":"Inspect files","dependsOn":[],"mode":"Analysis","relevantPaths":["src"],"acceptanceCriteria":["Find entry point"]}]}""");
        var store = new JsonTaskPlanStore(new AppPaths(_root));
        var service = new SingleAgentTaskWorkflowService(provider, new FakeScanner(), store, new NeverAgent());
        var plan = await service.CreatePlanAsync("understand project", new("custom", "http://localhost/v1", "fake"), null);
        Assert.Single(plan.Nodes); Assert.Equal(TaskWorkMode.Analysis, plan.Nodes[0].Mode); Assert.Equal(2, provider.Calls);
        Assert.NotNull(await store.LoadPlanAsync(plan.Id));
    }

    [Fact]
    public async Task Acknowledge_needs_review_requires_explicit_call_before_retry()
    {
        var store=new JsonTaskPlanStore(new AppPaths(_root));var plan=new TaskGraphPlan("TASK-review","goal",[new("a","A","work",[])]);
        await store.SavePlanAsync(plan);var now=DateTimeOffset.UtcNow;
        await store.SaveAsync(new(plan.Id,TaskGraphValidator.Fingerprint(plan),TaskGraphStatus.NeedsReview,
            [new("a",TaskNodeStatus.NeedsReview,1,null,"unknown",now,now)],now,now));
        var service=new SingleAgentTaskWorkflowService(new ScriptedProvider("{}"),new FakeScanner(),store,new NeverAgent());
        var reviewed=await service.AcknowledgeNeedsReviewAsync(plan);
        Assert.Equal(TaskGraphStatus.Interrupted,reviewed.Status);Assert.Equal(TaskNodeStatus.Interrupted,reviewed.Nodes[0].Status);
        Assert.Contains("explicitly allowed",reviewed.Nodes[0].Error);
    }

    private sealed class FakeScanner : IProjectScanner
    {
        public Task<ProjectProfile> ScanAsync(CancellationToken ct = default) => Task.FromResult(new ProjectProfile("test", [".NET"], ["TestAgent.slnx"], ["src"], ["README.md"], ["dotnet test"], 10, DateTimeOffset.UtcNow));
    }
    private sealed class ScriptedProvider(params string[] responses) : IModelProvider
    {
        public int Calls;
        public async IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken ct)
        { yield return new(StreamEventKind.Content, responses[Calls++]); await Task.CompletedTask; }
    }
    private sealed class NeverAgent : IAgentRuntime
    {
        public Task<AgentRunResult> RunAsync(ChatSession session, string userMessage, ProviderSettings settings, string? apiKey,
            IAgentObserver observer, CancellationToken cancellationToken, AgentRunOptions? options = null) => throw new InvalidOperationException("Planner must not execute the Agent runtime.");
    }
}
