namespace TestAgent.Core;

/// <summary>
/// A bounded, content-free description of the current repository. The scanner deliberately
/// returns facts and paths instead of loading the repository into the model context.
/// </summary>
public sealed record ProjectProfile(
    string Name,
    IReadOnlyList<string> Technologies,
    IReadOnlyList<string> EntryPoints,
    IReadOnlyList<string> Modules,
    IReadOnlyList<string> RuleFiles,
    IReadOnlyList<string> SuggestedCommands,
    int ScannedFileCount,
    DateTimeOffset ScannedAt)
{
    public string ToPromptSummary()
    {
        static string Join(IReadOnlyList<string> values) => values.Count == 0 ? "(none detected)" : string.Join(", ", values);
        return $"Project: {Name}\nTechnologies: {Join(Technologies)}\nEntry points: {Join(EntryPoints)}\n" +
               $"Modules: {Join(Modules)}\nRule files: {Join(RuleFiles)}\nSuggested verification: {Join(SuggestedCommands)}\n" +
               $"Files observed: {ScannedFileCount}";
    }
}

public sealed record TaskWorkflowItem(TaskGraphPlan Plan, TaskGraphCheckpoint? Checkpoint)
{
    public TaskGraphStatus Status => Checkpoint?.Status ?? TaskGraphStatus.Ready;
}

public interface IProjectScanner
{
    Task<ProjectProfile> ScanAsync(CancellationToken cancellationToken = default);
}

public interface ITaskPlanStore : ITaskGraphCheckpointStore
{
    Task<IReadOnlyList<TaskGraphPlan>> ListPlansAsync(CancellationToken cancellationToken = default);
    async Task<IReadOnlyList<TaskGraphPlan>> ListPlansAsync(string? workspaceId,
        CancellationToken cancellationToken = default) =>
        (await ListPlansAsync(cancellationToken)).Where(plan =>
            string.IsNullOrWhiteSpace(workspaceId)
                ? string.IsNullOrWhiteSpace(plan.WorkspaceId)
                : string.Equals(plan.WorkspaceId, workspaceId, StringComparison.OrdinalIgnoreCase)).ToArray();
    Task<TaskGraphPlan?> LoadPlanAsync(string graphId, CancellationToken cancellationToken = default);
    async Task<TaskGraphPlan?> LoadPlanAsync(string graphId, string? workspaceId,
        CancellationToken cancellationToken = default)
    {
        var plan = await LoadPlanAsync(graphId, cancellationToken);
        return string.IsNullOrWhiteSpace(workspaceId)
            ? string.IsNullOrWhiteSpace(plan?.WorkspaceId) ? plan : null
            : string.Equals(plan?.WorkspaceId, workspaceId, StringComparison.OrdinalIgnoreCase) ? plan : null;
    }
    Task SavePlanAsync(TaskGraphPlan plan, CancellationToken cancellationToken = default);
}

/// <summary>
/// Public application boundary for the single-Agent plan-review-run-resume workflow.
/// </summary>
public interface ITaskWorkflowService
{
    Task<TaskGraphPlan> CreatePlanAsync(string goal, ProviderSettings settings, string? apiKey,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TaskWorkflowItem>> ListAsync(CancellationToken cancellationToken = default);
    Task<TaskGraphCheckpoint> RunAsync(TaskGraphPlan plan, ProviderSettings settings, string? apiKey,
        IAgentObserver observer, CancellationToken cancellationToken,
        IProgress<TaskGraphCheckpoint>? progress = null, string? systemPrompt = null);
    Task<TaskGraphCheckpoint> AcknowledgeNeedsReviewAsync(TaskGraphPlan plan,
        CancellationToken cancellationToken = default);
}
