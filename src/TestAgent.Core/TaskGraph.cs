using System.Security.Cryptography;
using System.Text;

namespace TestAgent.Core;

/// <summary>
/// Overall state of a durable, single-Agent task graph run.
/// </summary>
public enum TaskGraphStatus
{
    Ready,
    Running,
    Completed,
    Failed,
    NeedsReview,
    Interrupted
}

/// <summary>
/// Durable state of one task graph node.
/// </summary>
public enum TaskNodeStatus
{
    Pending,
    Running,
    Completed,
    Failed,
    Blocked,
    NeedsReview,
    Interrupted
}

public enum TaskWorkMode
{
    Analysis,
    Coding,
    Documentation,
    Verification
}

/// <summary>
/// A bounded unit of work. Dependencies refer to other node IDs in the same plan.
/// </summary>
public sealed record TaskGraphNode(
    string Id,
    string Title,
    string Instructions,
    IReadOnlyList<string> DependsOn,
    TaskWorkMode Mode = TaskWorkMode.Analysis,
    IReadOnlyList<string>? RelevantPaths = null,
    IReadOnlyList<string>? AcceptanceCriteria = null);

/// <summary>
/// A versioned directed acyclic plan executed by one Agent, one node at a time.
/// </summary>
public sealed record TaskGraphPlan(
    string Id,
    string Goal,
    IReadOnlyList<TaskGraphNode> Nodes,
    int Version = 1);

/// <summary>
/// Result returned by the adapter that performs one node with the current Agent runtime.
/// </summary>
public sealed record TaskNodeRunResult(bool Success, string? Output = null, string? Error = null)
{
    public static TaskNodeRunResult Completed(string? output = null) => new(true, output);
    public static TaskNodeRunResult Failed(string error, string? output = null) => new(false, output, error);
}

/// <summary>
/// Immutable execution state persisted after every state transition.
/// </summary>
public sealed record TaskNodeCheckpoint(
    string NodeId,
    TaskNodeStatus Status,
    int Attempts,
    string? Output,
    string? Error,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt);

/// <summary>
/// Durable graph checkpoint. PlanFingerprint prevents resuming a changed plan under the same ID.
/// </summary>
public sealed record TaskGraphCheckpoint(
    string GraphId,
    string PlanFingerprint,
    TaskGraphStatus Status,
    IReadOnlyList<TaskNodeCheckpoint> Nodes,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    int Version = 1);

/// <summary>
/// Scoped context for a node. Only completed dependency results are exposed to the runner.
/// </summary>
public sealed record TaskNodeExecutionContext(
    TaskGraphPlan Plan,
    TaskGraphNode Node,
    int Attempt,
    IReadOnlyDictionary<string, TaskNodeCheckpoint> Dependencies);

public interface ITaskNodeRunner
{
    Task<TaskNodeRunResult> RunAsync(TaskNodeExecutionContext context, CancellationToken cancellationToken);
}

public interface ITaskGraphCheckpointStore
{
    Task<TaskGraphCheckpoint?> LoadAsync(string graphId, CancellationToken cancellationToken = default);
    Task SaveAsync(TaskGraphCheckpoint checkpoint, CancellationToken cancellationToken = default);
}

public sealed class TaskGraphValidationException(string message) : ArgumentException(message);

/// <summary>
/// Enforces small, explicit plans before any node is allowed to execute.
/// </summary>
public static class TaskGraphValidator
{
    public const int MaxNodes = 12;
    public const int MaxDependenciesPerNode = 16;
    public const int MaxRelevantPathsPerNode = 12;
    public const int MaxAcceptanceCriteriaPerNode = 32;
    public const int MaxIdLength = 128;
    public const int MaxTitleLength = 256;
    public const int MaxGoalLength = 16_000;
    public const int MaxInstructionsLength = 32_000;
    public const int MaxRelevantPathLength = 512;
    public const int MaxAcceptanceCriterionLength = 2_000;

    public static void Validate(TaskGraphPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        RequireText(plan.Id, nameof(plan.Id), MaxIdLength);
        RequireText(plan.Goal, nameof(plan.Goal), MaxGoalLength);
        if (plan.Version < 1) throw new TaskGraphValidationException("Plan version must be at least 1.");
        if (plan.Nodes is null || plan.Nodes.Count == 0)
            throw new TaskGraphValidationException("A task graph must contain at least one node.");
        if (plan.Nodes.Count > MaxNodes)
            throw new TaskGraphValidationException($"A task graph cannot contain more than {MaxNodes} nodes.");

        var nodes = new Dictionary<string, TaskGraphNode>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in plan.Nodes)
        {
            if (node is null) throw new TaskGraphValidationException("Task graph nodes cannot be null.");
            RequireText(node.Id, "Node ID", MaxIdLength);
            RequireText(node.Title, $"Title for node '{node.Id}'", MaxTitleLength);
            RequireText(node.Instructions, $"Instructions for node '{node.Id}'", MaxInstructionsLength);
            if (!nodes.TryAdd(node.Id, node))
                throw new TaskGraphValidationException($"Duplicate node ID: '{node.Id}'.");
            if (node.DependsOn is null)
                throw new TaskGraphValidationException($"Dependencies for node '{node.Id}' cannot be null.");
            if (node.DependsOn.Count > MaxDependenciesPerNode)
                throw new TaskGraphValidationException($"Node '{node.Id}' cannot have more than {MaxDependenciesPerNode} dependencies.");
            if (!Enum.IsDefined(node.Mode))
                throw new TaskGraphValidationException($"Node '{node.Id}' has an unsupported work mode.");
            ValidateRelevantPaths(node);
            ValidateAcceptanceCriteria(node);
        }

        foreach (var node in plan.Nodes)
        {
            var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var dependencyId in node.DependsOn)
            {
                RequireText(dependencyId, $"Dependency ID for node '{node.Id}'", MaxIdLength);
                if (!unique.Add(dependencyId))
                    throw new TaskGraphValidationException($"Node '{node.Id}' contains duplicate dependency '{dependencyId}'.");
                if (node.Id.Equals(dependencyId, StringComparison.OrdinalIgnoreCase))
                    throw new TaskGraphValidationException($"Node '{node.Id}' cannot depend on itself.");
                if (!nodes.ContainsKey(dependencyId))
                    throw new TaskGraphValidationException($"Node '{node.Id}' depends on missing node '{dependencyId}'.");
            }
        }

        _ = StableTopologicalOrder(plan);
    }

    internal static IReadOnlyList<TaskGraphNode> StableTopologicalOrder(TaskGraphPlan plan)
    {
        var remainingDependencies = plan.Nodes.ToDictionary(
            node => node.Id,
            node => node.DependsOn.Count,
            StringComparer.OrdinalIgnoreCase);
        var emitted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ordered = new List<TaskGraphNode>(plan.Nodes.Count);

        while (ordered.Count < plan.Nodes.Count)
        {
            var next = plan.Nodes.FirstOrDefault(node =>
                !emitted.Contains(node.Id) && remainingDependencies[node.Id] == 0);
            if (next is null)
            {
                var cycleNodes = plan.Nodes.Where(node => !emitted.Contains(node.Id)).Select(node => node.Id);
                throw new TaskGraphValidationException("Task graph contains a dependency cycle involving: " + string.Join(", ", cycleNodes));
            }

            emitted.Add(next.Id);
            ordered.Add(next);
            foreach (var dependent in plan.Nodes.Where(node => node.DependsOn.Contains(next.Id, StringComparer.OrdinalIgnoreCase)))
                remainingDependencies[dependent.Id]--;
        }

        return ordered;
    }

    public static string Fingerprint(TaskGraphPlan plan)
    {
        Validate(plan);
        var normalized = new StringBuilder()
            .Append(plan.Id).Append('\n')
            .Append(plan.Version).Append('\n')
            .Append(plan.Goal).Append('\n');
        foreach (var node in plan.Nodes)
        {
            normalized.Append(node.Id).Append('\n')
                .Append(node.Title).Append('\n')
                .Append(node.Instructions).Append('\n');
            foreach (var dependency in node.DependsOn)
                normalized.Append("dep:").Append(dependency).Append('\n');
            normalized.Append("mode:").Append(node.Mode).Append('\n');
            foreach (var path in node.RelevantPaths ?? [])
                normalized.Append("path:").Append(path).Append('\n');
            foreach (var criterion in node.AcceptanceCriteria ?? [])
                normalized.Append("accept:").Append(criterion).Append('\n');
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized.ToString())));
    }

    private static void RequireText(string? value, string field, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new TaskGraphValidationException($"{field} is required.");
        if (value.Length > maxLength) throw new TaskGraphValidationException($"{field} exceeds the {maxLength} character limit.");
        if (value.IndexOfAny(['\r', '\n', '\0']) >= 0 && field.Contains("ID", StringComparison.OrdinalIgnoreCase))
            throw new TaskGraphValidationException($"{field} cannot contain control characters.");
    }

    private static void ValidateRelevantPaths(TaskGraphNode node)
    {
        var paths = node.RelevantPaths ?? [];
        if (paths.Count > MaxRelevantPathsPerNode)
            throw new TaskGraphValidationException($"Node '{node.Id}' cannot have more than {MaxRelevantPathsPerNode} relevant paths.");
        var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            RequireText(path, $"Relevant path for node '{node.Id}'", MaxRelevantPathLength);
            var normalized = path.Replace('\\', '/');
            if (Path.IsPathRooted(path) || (normalized.Length >= 2 && char.IsLetter(normalized[0]) && normalized[1] == ':'))
                throw new TaskGraphValidationException($"Relevant path '{path}' for node '{node.Id}' must be workspace-relative.");
            if (normalized.Split('/', StringSplitOptions.RemoveEmptyEntries).Any(part => part == ".."))
                throw new TaskGraphValidationException($"Relevant path '{path}' for node '{node.Id}' cannot escape the workspace.");
            if (!unique.Add(normalized.TrimEnd('/')))
                throw new TaskGraphValidationException($"Node '{node.Id}' contains duplicate relevant path '{path}'.");
        }
    }

    private static void ValidateAcceptanceCriteria(TaskGraphNode node)
    {
        var criteria = node.AcceptanceCriteria ?? [];
        if (criteria.Count > MaxAcceptanceCriteriaPerNode)
            throw new TaskGraphValidationException($"Node '{node.Id}' cannot have more than {MaxAcceptanceCriteriaPerNode} acceptance criteria.");
        foreach (var criterion in criteria)
            RequireText(criterion, $"Acceptance criterion for node '{node.Id}'", MaxAcceptanceCriterionLength);
    }
}

/// <summary>
/// Durable, sequential DAG executor for the current single-Agent architecture.
/// </summary>
public sealed class SequentialTaskGraphExecutor(
    ITaskNodeRunner runner,
    ITaskGraphCheckpointStore checkpoints,
    TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    /// <summary>Raised after each durable checkpoint is successfully saved.</summary>
    public event Action<TaskGraphCheckpoint>? CheckpointChanged;

    /// <summary>
    /// Starts or resumes a plan. Completed nodes are never executed again. A previously Running
    /// node is treated as Interrupted after a process restart and is retried once this call resumes.
    /// </summary>
    public async Task<TaskGraphCheckpoint> RunAsync(TaskGraphPlan plan, CancellationToken cancellationToken = default)
    {
        TaskGraphValidator.Validate(plan);
        var fingerprint = TaskGraphValidator.Fingerprint(plan);
        var checkpoint = await checkpoints.LoadAsync(plan.Id, CancellationToken.None)
            ?? CreateCheckpoint(plan, fingerprint);
        ValidateCheckpoint(plan, fingerprint, checkpoint);

        var states = checkpoint.Nodes.ToDictionary(node => node.NodeId, StringComparer.OrdinalIgnoreCase);
        var recoveredRunningNode = states.Values.FirstOrDefault(node => node.Status == TaskNodeStatus.Running);
        if (recoveredRunningNode is not null)
        {
            states[recoveredRunningNode.NodeId] = recoveredRunningNode with
            {
                Status = TaskNodeStatus.NeedsReview,
                Error = "Previous execution stopped with an unknown outcome. Review workspace changes before retrying this node.",
                CompletedAt = _time.GetUtcNow()
            };
        }

        if (states.Values.Any(node => node.Status == TaskNodeStatus.NeedsReview))
        {
            checkpoint = Snapshot(checkpoint, TaskGraphStatus.NeedsReview, plan, states);
            await SaveCheckpointAsync(checkpoint);
            return checkpoint;
        }

        checkpoint = Snapshot(checkpoint, TaskGraphStatus.Running, plan, states);
        await SaveCheckpointAsync(checkpoint);

        foreach (var node in TaskGraphValidator.StableTopologicalOrder(plan))
        {
            var state = states[node.Id];
            if (state.Status is TaskNodeStatus.Completed or TaskNodeStatus.Failed or TaskNodeStatus.Blocked or TaskNodeStatus.NeedsReview)
                continue;

            var dependencyStates = node.DependsOn.ToDictionary(id => id, id => states[id], StringComparer.OrdinalIgnoreCase);
            var failedDependencies = dependencyStates.Values
                .Where(value => value.Status is TaskNodeStatus.Failed or TaskNodeStatus.Blocked)
                .Select(value => value.NodeId)
                .ToArray();
            if (failedDependencies.Length > 0)
            {
                states[node.Id] = state with
                {
                    Status = TaskNodeStatus.Blocked,
                    Error = "Blocked by failed dependency: " + string.Join(", ", failedDependencies),
                    CompletedAt = _time.GetUtcNow()
                };
                checkpoint = Snapshot(checkpoint, TaskGraphStatus.Running, plan, states);
                await SaveCheckpointAsync(checkpoint);
                continue;
            }

            if (dependencyStates.Values.Any(value => value.Status != TaskNodeStatus.Completed))
                throw new InvalidOperationException($"Node '{node.Id}' became runnable before all dependencies completed.");

            if (cancellationToken.IsCancellationRequested)
                return await InterruptAsync(checkpoint, plan, states, node.Id, "Execution was cancelled before the node started.");

            var startedAt = _time.GetUtcNow();
            state = state with
            {
                Status = TaskNodeStatus.Running,
                Attempts = state.Attempts + 1,
                Output = null,
                Error = null,
                StartedAt = startedAt,
                CompletedAt = null
            };
            states[node.Id] = state;
            checkpoint = Snapshot(checkpoint, TaskGraphStatus.Running, plan, states);

            try
            {
                await SaveCheckpointAsync(checkpoint);
                var result = await runner.RunAsync(
                    new TaskNodeExecutionContext(plan, node, state.Attempts, dependencyStates),
                    cancellationToken);
                var finishedAt = _time.GetUtcNow();
                states[node.Id] = state with
                {
                    Status = result.Success ? TaskNodeStatus.Completed : TaskNodeStatus.Failed,
                    Output = result.Output,
                    Error = result.Success ? null : result.Error ?? "Node execution failed without an error message.",
                    CompletedAt = finishedAt
                };
                checkpoint = Snapshot(checkpoint, TaskGraphStatus.Running, plan, states);
                await SaveCheckpointAsync(checkpoint);
            }
            catch (OperationCanceledException)
            {
                return await InterruptAsync(checkpoint, plan, states, node.Id, "Node execution was interrupted.");
            }
            catch (Exception ex)
            {
                states[node.Id] = state with
                {
                    Status = TaskNodeStatus.Failed,
                    Error = ex.Message,
                    CompletedAt = _time.GetUtcNow()
                };
                checkpoint = Snapshot(checkpoint, TaskGraphStatus.Running, plan, states);
                await SaveCheckpointAsync(checkpoint);
            }
        }

        var finalStatus = states.Values.All(node => node.Status == TaskNodeStatus.Completed)
            ? TaskGraphStatus.Completed
            : states.Values.Any(node => node.Status is TaskNodeStatus.Failed or TaskNodeStatus.Blocked)
                ? TaskGraphStatus.Failed
                : TaskGraphStatus.Interrupted;
        checkpoint = Snapshot(checkpoint, finalStatus, plan, states);
        await SaveCheckpointAsync(checkpoint);
        return checkpoint;
    }

    private async Task<TaskGraphCheckpoint> InterruptAsync(
        TaskGraphCheckpoint checkpoint,
        TaskGraphPlan plan,
        Dictionary<string, TaskNodeCheckpoint> states,
        string nodeId,
        string error)
    {
        var state = states[nodeId];
        states[nodeId] = state with
        {
            Status = TaskNodeStatus.Interrupted,
            Error = error,
            CompletedAt = _time.GetUtcNow()
        };
        checkpoint = Snapshot(checkpoint, TaskGraphStatus.Interrupted, plan, states);
        await SaveCheckpointAsync(checkpoint);
        return checkpoint;
    }

    private async Task SaveCheckpointAsync(TaskGraphCheckpoint checkpoint)
    {
        await checkpoints.SaveAsync(checkpoint, CancellationToken.None);
        var handlers = CheckpointChanged;
        if (handlers is null) return;
        foreach (Action<TaskGraphCheckpoint> handler in handlers.GetInvocationList())
        {
            try { handler(checkpoint); }
            catch { /* Checkpoint notification must never change execution semantics. */ }
        }
    }

    private TaskGraphCheckpoint CreateCheckpoint(TaskGraphPlan plan, string fingerprint)
    {
        var now = _time.GetUtcNow();
        return new TaskGraphCheckpoint(
            plan.Id,
            fingerprint,
            TaskGraphStatus.Ready,
            plan.Nodes.Select(node => new TaskNodeCheckpoint(node.Id, TaskNodeStatus.Pending, 0, null, null, null, null)).ToArray(),
            now,
            now);
    }

    private TaskGraphCheckpoint Snapshot(
        TaskGraphCheckpoint previous,
        TaskGraphStatus status,
        TaskGraphPlan plan,
        IReadOnlyDictionary<string, TaskNodeCheckpoint> states) =>
        previous with
        {
            Status = status,
            Nodes = plan.Nodes.Select(node => states[node.Id]).ToArray(),
            UpdatedAt = _time.GetUtcNow()
        };

    private static void ValidateCheckpoint(TaskGraphPlan plan, string fingerprint, TaskGraphCheckpoint checkpoint)
    {
        if (!checkpoint.GraphId.Equals(plan.Id, StringComparison.OrdinalIgnoreCase))
            throw new TaskGraphValidationException("Checkpoint graph ID does not match the plan.");
        if (!checkpoint.PlanFingerprint.Equals(fingerprint, StringComparison.Ordinal))
            throw new TaskGraphValidationException("The plan changed after its checkpoint was created. Create a new graph ID or discard the old checkpoint.");
        if (checkpoint.Version != 1)
            throw new TaskGraphValidationException($"Unsupported checkpoint version: {checkpoint.Version}.");
        if (checkpoint.Nodes.Count != plan.Nodes.Count)
            throw new TaskGraphValidationException("Checkpoint node count does not match the plan.");

        var saved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var state in checkpoint.Nodes)
        {
            if (!saved.Add(state.NodeId) || !plan.Nodes.Any(node => node.Id.Equals(state.NodeId, StringComparison.OrdinalIgnoreCase)))
                throw new TaskGraphValidationException($"Checkpoint contains an unknown or duplicate node: '{state.NodeId}'.");
            if (state.Attempts < 0)
                throw new TaskGraphValidationException($"Checkpoint attempts cannot be negative for node '{state.NodeId}'.");
        }
    }
}
