namespace TestAgent.Core;

public enum AgentState { Idle, Streaming, Completed, Cancelled, Failed }
public enum ChatRole { System, User, Assistant, Tool }
public enum StreamEventKind { Content, Reasoning, Revision, ToolStarted, ToolCompleted, ToolSessionUpdated, Usage, Completed, Escalation }
public enum ToolRiskLevel
{
    ReadOnly = 0,
    WorkspaceWrite = 1,
    ProcessExecution = 2,
    Destructive = 3,
    // Appended to preserve numeric values already persisted in tool-session JSON.
    ExternalNetwork = 4,
    // Capturing a visible app can include private user data and always requires approval.
    SensitiveCapture = 5,
    // Reading live local application state can expose private metadata and requires approval.
    LocalEnvironmentRead = 6
}
public enum ToolExecutionStatus { Success, Failed, Blocked, Cancelled, Timeout }

public sealed record ModelToolCall(string Id, string Name, string ArgumentsJson);
public sealed record ChatMessage(ChatRole Role, string Content, DateTimeOffset CreatedAt,
    string? ToolCallId = null, string? ToolName = null, IReadOnlyList<ModelToolCall>? ToolCalls = null);
public sealed record ChatSession(string Id, string Title, List<ChatMessage> Messages, DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt, int Version = 1, string? WorkspaceId = null);
public enum MemoryScope { User, Session, Project }
public sealed record MemoryEntry(string Id, string Name, string Content, bool Enabled, DateTimeOffset UpdatedAt,
    MemoryScope Scope = MemoryScope.User, string? ScopeId = null, IReadOnlyList<string>? Tags = null,
    string? WorkspaceId = null);
public sealed record SessionSearchHit(string SessionId, string SessionTitle, ChatRole Role, string Snippet,
    DateTimeOffset Timestamp);
public sealed record ImageInput(string MimeType, byte[] Data, string Sha256, int Width, int Height);
public sealed record BrowserDomElement(string Kind, string Text, string? Target = null);
public sealed record BrowserDomSnapshot(string Url, string Title, string Text,
    IReadOnlyList<BrowserDomElement> Elements, bool Truncated, DateTimeOffset CapturedAt);
public sealed record BrowserPageDocument(string Url, string Title, BrowserDomSnapshot Dom,
    DateTimeOffset UpdatedAt);
public sealed record BrowserCaptureReceipt(string Url, string Title, int Width, int Height,
    DateTimeOffset CapturedAt);
public sealed record ProviderSettings(string ProviderId, string Endpoint, string Model, int MaxOutputTokens = 32_768,
    int TimeoutSeconds = 300, int MaxContextMessages = 30, bool SelfReviewEnabled = true, int MaxSelfReviewRounds = 1,
    bool SupportsImageInput = false, string ReasoningEffort = "medium")
{
    public static string NormalizeReasoningEffort(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "none" => "none",
        "low" => "low",
        "medium" => "medium",
        "high" => "high",
        "xhigh" => "xhigh",
        "max" => "max",
        _ => "medium"
    };
}
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
public enum EscalationSignalKind
{
    ExplicitCollaborationRequested,
    ToolRoundLimitReached,
    RepeatedToolCall,
    ToolFailed,
    ToolTimeout,
    WriteSucceeded,
    ValidationSucceeded,
    UserRejected,
    Cancelled
}
public sealed record EscalationSignal(EscalationSignalKind Kind, string? ToolName = null, string? Detail = null);
public sealed record EscalationContext(string UserMessage, string DraftAnswer,
    IReadOnlyList<EscalationSignal> Signals, IReadOnlyList<ToolDefinition> AvailableTools,
    int ModelRounds, int ToolCalls, bool CancellationRequested = false,
    IReadOnlyList<string>? RelevantPaths = null);
public sealed record EscalationDecision(bool ShouldEscalate, int Score, IReadOnlyList<string> Reasons,
    string? PeerId = null, string? DelegationTask = null, string? SuppressedReason = null);
public interface IEscalationEvaluator
{
    Task<EscalationDecision> EvaluateAsync(EscalationContext context, CancellationToken ct = default);
}
public sealed record StreamEvent(StreamEventKind Kind, string Text = "", int? Tokens = null,
    ModelToolCall? ToolCall = null, ToolResult? ToolResult = null,
    EscalationDecision? Escalation = null);
public sealed record AgentRunResult(ChatSession Session, AgentState State, string Content, string Reasoning,
    int? Tokens = null, string? Error = null, EscalationDecision? Escalation = null);
public sealed record AgentRunOptions(bool PersistSession = true, string? AdditionalSystemContext = null,
    IReadOnlyList<string>? RelevantPaths = null, string? SystemPrompt = null,
    bool IncludeLongTermMemory = true, string? ToolSessionScopeId = null,
    IReadOnlyList<ImageInput>? Images = null, string? WorkspaceId = null);
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
public interface IReadOnlyBrowserSession
{
    BrowserPageDocument? Current { get; }
    bool HasLatestCapture { get; }
    event Action? Changed;
    Task<BrowserPageDocument> OpenSnapshotAsync(string url, int maxChars = 30_000,
        CancellationToken ct = default);
    Task<BrowserDomSnapshot> ReadDomAsync(int maxChars = 30_000, CancellationToken ct = default);
    Task<BrowserCaptureReceipt> CaptureViewportAsync(CancellationToken ct = default);
    ImageInput? TakeLatestCapture();
}
public interface IIterationGuideStore { Task<IReadOnlyList<IterationGuide>> ListAsync(CancellationToken ct = default); }
public interface ICodeIterationService
{
    Task<IterationProposal> GenerateAsync(IterationGuide guide, ProviderSettings settings, string? apiKey, CancellationToken ct = default);
    Task<IterationApplyResult> ApplyAsync(IterationProposal proposal, CancellationToken ct = default);
}

public enum McpPeerKind { Codex, Claude, CustomHttp }
public enum McpPeerConnectionState { Disconnected, Connecting, Connected, Disconnecting, Faulted }

public sealed record McpPeerProfile(string Id, string Name, McpPeerKind Kind, bool Enabled,
    string? ExecutablePath = null, string? Endpoint = null,
    IReadOnlyList<string>? AllowedTools = null, int ConnectTimeoutSeconds = 20,
    int OperationTimeoutSeconds = 120, int MaxOutputChars = 30_000,
    bool AllowAutomaticEscalation = false);

public sealed record McpPeerInfo(McpPeerProfile Profile, McpPeerConnectionState State,
    string? LastError = null, DateTimeOffset? ConnectedAt = null)
{
    public string Id => Profile.Id;
    public string Name => Profile.Name;
    public McpPeerKind Kind => Profile.Kind;
    public bool Enabled => Profile.Enabled;
    public bool IsConnected => State == McpPeerConnectionState.Connected;
    public IReadOnlyList<string> AllowedTools => Profile.AllowedTools ?? [];
}

public sealed record McpPeerTool(string Name, string Description, string InputSchemaJson,
    bool Allowed = false, bool Truncated = false);
public sealed record McpPeerCallResult(string PeerId, string ToolName, bool Success, string Output,
    bool Truncated, string? Error, long DurationMs);

public interface IMcpPeerProfileStore
{
    Task<IReadOnlyList<McpPeerProfile>> ListAsync(CancellationToken ct = default);
    Task SaveAsync(McpPeerProfile profile, CancellationToken ct = default);
    Task DeleteAsync(string id, CancellationToken ct = default);
}

public interface IMcpPeerService : IDisposable, IAsyncDisposable
{
    event Action? Changed;
    Task<IReadOnlyList<McpPeerInfo>> ListAsync(CancellationToken ct = default);
    Task SaveAsync(McpPeerProfile profile, CancellationToken ct = default);
    Task DeleteAsync(string id, CancellationToken ct = default);
    Task<McpPeerInfo> ConnectAsync(string id, CancellationToken ct = default);
    Task DisconnectAsync(string id, CancellationToken ct = default);
    Task<IReadOnlyList<McpPeerTool>> ListToolsAsync(string id, CancellationToken ct = default);
    Task<McpPeerCallResult> CallToolAsync(string peerId, string toolName,
        System.Text.Json.JsonElement arguments, CancellationToken ct = default);
}
