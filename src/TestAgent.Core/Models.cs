namespace TestAgent.Core;

public enum AgentState { Idle, Streaming, Completed, Cancelled, Failed }
public enum ChatRole { System, User, Assistant, Tool }
public enum StreamEventKind { Content, Reasoning, Revision, ToolStarted, ToolCompleted, ToolSessionUpdated, Usage, Completed }
public enum ToolRiskLevel
{
    ReadOnly = 0,
    WorkspaceWrite = 1,
    ProcessExecution = 2,
    Destructive = 3,
    // Appended to preserve numeric values already persisted in tool-session JSON.
    ExternalNetwork = 4
}
public enum ToolExecutionStatus { Success, Failed, Blocked, Cancelled, Timeout }

public sealed record ModelToolCall(string Id, string Name, string ArgumentsJson);
public sealed record ChatMessage(ChatRole Role, string Content, DateTimeOffset CreatedAt,
    string? ToolCallId = null, string? ToolName = null, IReadOnlyList<ModelToolCall>? ToolCalls = null);
public sealed record ChatSession(string Id, string Title, List<ChatMessage> Messages, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, int Version = 1);
public enum MemoryScope { User, Session, Project }
public sealed record MemoryEntry(string Id, string Name, string Content, bool Enabled, DateTimeOffset UpdatedAt,
    MemoryScope Scope = MemoryScope.User, string? ScopeId = null, IReadOnlyList<string>? Tags = null);
public sealed record SessionSearchHit(string SessionId, string SessionTitle, ChatRole Role, string Snippet,
    DateTimeOffset Timestamp);
public sealed record ImageInput(string MimeType, byte[] Data, string Sha256, int Width, int Height);
public sealed record ProviderSettings(string ProviderId, string Endpoint, string Model, int MaxOutputTokens = 4096,
    int TimeoutSeconds = 120, int MaxContextMessages = 30, bool SelfReviewEnabled = true, int MaxSelfReviewRounds = 1,
    bool SupportsImageInput = false);
public sealed record AppSettings(ProviderSettings Provider, string SystemPrompt = "You are a helpful desktop AI assistant.", int Version = 1);
public sealed record ToolParameterDefinition(string Name, string Type, string Description, bool Required = false,
    IReadOnlyList<string>? Enum = null);
public sealed record ToolDefinition(string Name, string Description, ToolRiskLevel RiskLevel,
    IReadOnlyList<ToolParameterDefinition> Parameters, string? UsageExample = null);
public sealed record ToolRequest(string Id, string Name, string ArgumentsJson, string SessionId,
    IReadOnlyList<string>? AllowedPaths = null, string? ParentToolSessionId = null);
public sealed record ToolResult(string RequestId, string ToolName, ToolExecutionStatus Status, string Output,
    string? Error = null, TimeSpan? Duration = null, string? Summary = null,
    IReadOnlyList<string>? ModifiedFiles = null, bool Truncated = false,
    string? NextAction = null, string? ToolSessionId = null,
    string? ErrorCode = null, bool Retryable = false);
public sealed record ToolApprovalRequest(string RequestId, string ToolName, ToolRiskLevel RiskLevel, string Summary);
public sealed record ToolAuditEntry(string Id, string SessionId, string ToolName, string ArgumentsSummary,
    ToolRiskLevel RiskLevel, bool Approved, ToolExecutionStatus Status, string ResultSummary,
    DateTimeOffset Timestamp, long DurationMs);
public enum ToolSessionState { Idle, Running, Completed, Failed, Blocked, Cancelled, Timeout, NeedsReview, Retired }
public sealed record ToolInvocationRecord(string RequestId, string ArgumentsSummary, ToolExecutionStatus Status,
    string ResultSummary, string? Error, DateTimeOffset StartedAt, long DurationMs,
    bool Approved, IReadOnlyList<string>? ModifiedFiles = null);
public sealed record ToolSession(string Id, string ParentSessionId, string ToolName, ToolSessionState State,
    int TotalCalls, int SuccessCount, int FailureCount, string? LastSuccessfulStrategy,
    IReadOnlyList<ToolInvocationRecord> RecentInvocations, DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt, int Version = 1, ToolRiskLevel RiskLevel = ToolRiskLevel.ReadOnly,
    string? DefinitionHash = null);
public sealed class ToolSessionNeedsReviewException(string message) : InvalidOperationException(message);
public sealed class DuplicateToolRequestException(string message) : InvalidOperationException(message);
public sealed record ChatRequest(IReadOnlyList<ChatMessage> Messages, ProviderSettings Settings, string? ApiKey,
    IReadOnlyList<ToolDefinition>? Tools = null, IReadOnlyList<ImageInput>? Images = null);
public sealed record StreamEvent(StreamEventKind Kind, string Text = "", int? Tokens = null,
    ModelToolCall? ToolCall = null, ToolResult? ToolResult = null);
public sealed record AgentRunResult(ChatSession Session, AgentState State, string Content, string Reasoning, int? Tokens = null, string? Error = null);
public sealed record AgentRunOptions(bool PersistSession = true, string? AdditionalSystemContext = null,
    IReadOnlyList<string>? RelevantPaths = null, string? SystemPrompt = null,
    bool IncludeLongTermMemory = true, string? ToolSessionScopeId = null,
    IReadOnlyList<ImageInput>? Images = null);
public sealed record IterationGuide(string Id, string Title, string Goal, IReadOnlyList<string> Targets, string Content);
public sealed record ProposedFileChange(string Path, string OriginalSha256, string OriginalContent, string NewContent);
public sealed record IterationValidation(bool Success, string BuildOutput, string TestOutput, DateTimeOffset CompletedAt);
public sealed record IterationProposal(string Id, string GuideId, string Title, string Summary,
    IReadOnlyList<ProposedFileChange> Changes, IterationValidation Validation, DateTimeOffset CreatedAt);
public sealed record IterationApplyResult(bool Success, bool RolledBack, string Message, string ValidationOutput);

public interface IModelProvider { IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, CancellationToken cancellationToken); }
public interface IAgentRuntime
{
    Task<AgentRunResult> RunAsync(ChatSession session, string userMessage, ProviderSettings settings, string? apiKey,
        IAgentObserver observer, CancellationToken cancellationToken, AgentRunOptions? options = null);
}
public interface IAgentObserver
{
    ValueTask OnStateAsync(AgentState state);
    ValueTask OnEventAsync(StreamEvent value);
    ValueTask<bool> RequestToolApprovalAsync(ToolApprovalRequest request, CancellationToken ct);
}
public interface IAgentTool
{
    ToolDefinition Definition { get; }
    Task<ToolResult> ExecuteAsync(ToolRequest request, CancellationToken ct = default);
}
public interface IToolRegistry
{
    IReadOnlyList<ToolDefinition> GetDefinitions();
    IAgentTool? Get(string name);
}
public interface IToolAuditStore { Task AppendAsync(ToolAuditEntry entry, CancellationToken ct = default); }
public interface IToolSessionStore
{
    Task<IReadOnlyList<ToolSession>> ListAsync(string? parentSessionId = null, CancellationToken ct = default);
    Task<ToolSession?> GetAsync(string parentSessionId, string toolName, CancellationToken ct = default);
    Task SaveAsync(ToolSession session, CancellationToken ct = default);
}
public interface IToolSessionCoordinator
{
    Task<IReadOnlyList<ToolSession>> EnsureSessionsAsync(string parentSessionId,
        IReadOnlyList<ToolDefinition> definitions, CancellationToken ct = default);
    Task<ToolSession> StartAsync(ToolRequest request, bool approved, CancellationToken ct = default);
    Task<ToolSession> CompleteAsync(ToolSession session, ToolRequest request, ToolResult result,
        bool approved, CancellationToken ct = default);
    Task<string?> GetStrategyHintAsync(string parentSessionId, string toolName,
        CancellationToken ct = default);
    Task<IReadOnlyList<ToolSession>> ListAsync(string? parentSessionId = null, CancellationToken ct = default);
    Task<ToolSession> AcknowledgeNeedsReviewAsync(string parentSessionId, string toolName,
        CancellationToken ct = default);
}
public interface IToolExecutionService
{
    IReadOnlyList<ToolDefinition> GetDefinitions();
    Task<ToolResult> ExecuteAsync(ToolRequest request, IAgentObserver observer, CancellationToken ct = default);
}
public interface IMemoryStore { Task<IReadOnlyList<MemoryEntry>> ListAsync(CancellationToken ct = default); Task SaveAsync(MemoryEntry memory, CancellationToken ct = default); Task DeleteAsync(string id, CancellationToken ct = default); }
public interface ISessionStore { Task<IReadOnlyList<ChatSession>> ListAsync(CancellationToken ct = default); Task<ChatSession?> GetAsync(string id, CancellationToken ct = default); Task SaveAsync(ChatSession session, CancellationToken ct = default); Task DeleteAsync(string id, CancellationToken ct = default); }
public interface ISessionHistorySearch
{
    Task<IReadOnlyList<SessionSearchHit>> SearchAsync(string query, string? excludeSessionId = null,
        int maxResults = 20, CancellationToken ct = default);
}
public interface ISettingsStore { Task<AppSettings> LoadAsync(CancellationToken ct = default); Task SaveAsync(AppSettings settings, CancellationToken ct = default); }
public interface ISecureSecretStore { Task<string?> GetAsync(string providerId, CancellationToken ct = default); Task SetAsync(string providerId, string secret, CancellationToken ct = default); }
public interface IImageInputService { Task<ImageInput> LoadAsync(string filePath, CancellationToken ct = default); }
public interface IIterationGuideStore { Task<IReadOnlyList<IterationGuide>> ListAsync(CancellationToken ct = default); }
public interface ICodeIterationService
{
    Task<IterationProposal> GenerateAsync(IterationGuide guide, ProviderSettings settings, string? apiKey, CancellationToken ct = default);
    Task<IterationApplyResult> ApplyAsync(IterationProposal proposal, CancellationToken ct = default);
}
