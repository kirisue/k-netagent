using System.Collections.ObjectModel;
using System.Windows.Input;
using KNetAgent.Desktop.WinUI.Features.Media;
using KNetAgent.Desktop.WinUI.Services;
using TestAgent.Core;
using TestAgent.Infrastructure;
using AgentRunState = TestAgent.Core.AgentState;

namespace KNetAgent.Desktop.WinUI.ViewModels;

/// <summary>
/// WinUI presentation adapter over the existing Agent/Core stores. It deliberately
/// owns no HTTP, persistence, tool execution, or memory-selection business logic.
/// </summary>
public sealed class ShellViewModel : ObservableObject, IAgentObserver, IDisposable
{
    private readonly IAgentRuntime _agent;
    private readonly ISessionStore _sessions;
    private readonly IMemoryStore _memories;
    private readonly ISettingsStore _settings;
    private readonly ISecureSecretStore _secrets;
    private readonly IToolSessionCoordinator _toolSessions;
    private readonly IToolRegistry _toolRegistry;
    private readonly IMcpPeerService _mcpPeers;
    private readonly IWorkspaceCatalog _workspaceCatalog;
    private readonly WorkspaceLocator _workspace;
    private readonly IBackgroundCommandService _backgroundCommands;
    private readonly MediaAttachmentViewModel _media;
    private readonly WorkspaceRelaunchService _workspaceRelaunch;
    private readonly IUiDispatcher _dispatcher;
    private readonly IUserInteractionService _interaction;

    private CancellationTokenSource? _runCancellation;
    private AppSettings _appSettings = JsonSettingsStore.Defaults();
    private ChatSession? _selectedSession;
    private MemoryEntry? _selectedMemory;
    private McpPeerItemViewModel? _selectedMcpPeer;
    private InspectorTabItem? _selectedInspectorTab;
    private string _input = string.Empty;
    private string _sessionSearchText = string.Empty;
    private string _streamingContent = string.Empty;
    private string _reasoning = string.Empty;
    private string _status = "就绪";
    private string _toolSessionStatus = "工具会话尚未初始化";
    private string _mcpPeerStatus = "外部 Agent / MCP 尚未初始化";
    private string _mcpPeerToolsStatus = "选择并连接一个外部 Agent 后发现工具";
    private string _latestEscalationEvent = "本次运行尚无委托评估记录。";
    private bool _busy;
    private bool _mcpPeerBusy;
    private bool _isInspectorOpen = true;
    private bool _disposed;
    private AgentRunState _agentState = AgentRunState.Idle;
    private int? _lastTokenUsage;

    private string _providerId = "openai";
    private string _endpoint = "https://api.openai.com/v1";
    private string _model = "gpt-5.6-sol";
    private string _reasoningEffort = "medium";
    private int _maxTokens = 32_768;
    private int _timeoutSeconds = 300;
    private bool _selfReviewEnabled = true;
    private bool _supportsImageInput;
    private string _apiKey = string.Empty;

    private string _memoryName = string.Empty;
    private string _memoryContent = string.Empty;
    private bool _memoryEnabled = true;
    private MemoryScope _memoryScope = MemoryScope.User;
    private string _memoryScopeId = string.Empty;

    public ShellViewModel(
        IAgentRuntime agent,
        ISessionStore sessions,
        IMemoryStore memories,
        ISettingsStore settings,
        ISecureSecretStore secrets,
        IToolSessionCoordinator toolSessions,
        IToolRegistry toolRegistry,
        IMcpPeerService mcpPeers,
        IWorkspaceCatalog workspaceCatalog,
        WorkspaceLocator workspace,
        IBackgroundCommandService backgroundCommands,
        MediaAttachmentViewModel media,
        WorkspaceRelaunchService workspaceRelaunch,
        IUiDispatcher dispatcher,
        IUserInteractionService interaction)
    {
        _agent = agent;
        _sessions = sessions;
        _memories = memories;
        _settings = settings;
        _secrets = secrets;
        _toolSessions = toolSessions;
        _toolRegistry = toolRegistry;
        _mcpPeers = mcpPeers;
        _workspaceCatalog = workspaceCatalog;
        _workspace = workspace;
        _backgroundCommands = backgroundCommands;
        _media = media;
        _workspaceRelaunch = workspaceRelaunch;
        _dispatcher = dispatcher;
        _interaction = interaction;
        _mcpPeers.Changed += McpPeers_Changed;
        _media.PropertyChanged += Media_PropertyChanged;

        InspectorTabs =
        [
            new("plan", "计划", "\uE9D9"),
            new("files", "变更", "\uE8A5"),
            new("tools", "工具", "\uE90F"),
            new("memory", "记忆", "\uE82D"),
            new("context", "上下文", "\uE8F1")
        ];
        _selectedInspectorTab = InspectorTabs[2];

        SendCommand = CreateAsyncCommand(SendAsync, () => CanSend);
        StopCommand = new RelayCommand(Stop, () => CanStop);
        NewSessionCommand = CreateAsyncCommand(NewSessionAsync, () => !_disposed && !Busy);
        ClearSessionCommand = CreateAsyncCommand(ClearSessionAsync,
            () => !_disposed && !Busy && SelectedSession is not null);
        SaveMemoryCommand = CreateAsyncCommand(SaveMemoryAsync,
            () => !_disposed && !Busy && !string.IsNullOrWhiteSpace(MemoryName) &&
                  !string.IsNullOrWhiteSpace(MemoryContent));
        DeleteMemoryCommand = CreateAsyncCommand(DeleteMemoryAsync,
            () => !_disposed && !Busy && SelectedMemory is not null);
        SaveSettingsCommand = CreateAsyncCommand(SaveSettingsFromEditorAsync,
            () => !_disposed && !Busy && !string.IsNullOrWhiteSpace(ProviderId) &&
                  !string.IsNullOrWhiteSpace(Endpoint) && !string.IsNullOrWhiteSpace(Model));
        RefreshToolSessionsCommand = CreateAsyncCommand(RefreshToolSessionsAsync,
            () => !_disposed && !Busy && SelectedSession is not null);
        AcknowledgeToolSessionsCommand = CreateAsyncCommand(AcknowledgeToolSessionsAsync,
            () => !_disposed && !Busy && ToolSessions.Any(item => item.NeedsReview));
        ToggleInspectorCommand = new RelayCommand(() => IsInspectorOpen = !IsInspectorOpen,
            () => !_disposed);
        ApplyRecommendedModelSettingsCommand = new RelayCommand(ApplyRecommendedModelSettings,
            () => !_disposed && !Busy);
    }

    public ObservableCollection<ChatSession> Sessions { get; } = [];
    public ObservableCollection<ChatSession> VisibleSessions { get; } = [];
    public ObservableCollection<MessageItemViewModel> Messages { get; } = [];
    public ObservableCollection<MemoryEntry> Memories { get; } = [];
    public ObservableCollection<ToolSessionItemViewModel> ToolSessions { get; } = [];
    public ObservableCollection<McpPeerItemViewModel> McpPeers { get; } = [];
    public ObservableCollection<McpPeerToolItemViewModel> SelectedMcpPeerTools { get; } = [];
    public ObservableCollection<WorkspaceEntry> RecentWorkspaces { get; } = [];
    public MediaAttachmentViewModel Media => _media;

    public IReadOnlyList<InspectorTabItem> InspectorTabs { get; }
    public IReadOnlyList<string> ProviderIds { get; } =
        ["deepseek", "openai", "openrouter", "ollama", "custom"];
    public IReadOnlyList<string> ModelSuggestions { get; } =
        ["gpt-5.6-sol", "gpt-5.6-terra", "gpt-5.6-luna"];
    public IReadOnlyList<string> ReasoningEfforts { get; } =
        ["none", "low", "medium", "high", "xhigh", "max"];
    public IReadOnlyList<MemoryScope> MemoryScopes { get; } = Enum.GetValues<MemoryScope>();

    public string CurrentWorkspaceId => _workspace.Id;
    public string CurrentWorkspaceName => _workspace.Name;
    public string CurrentWorkspacePath => _workspace.Root;
    public string CurrentWorkspaceLabel => $"{_workspace.Name} · {_workspace.Root}";

    public void StageWindowsEventEvidence(string evidence)
    {
        ThrowIfDisposed();
        if (Busy || Media.Busy || Media.HasPendingImage)
            throw new InvalidOperationException("请先结束当前操作或处理图片附件，再加入事件证据。");
        if (!string.IsNullOrWhiteSpace(Input))
            throw new InvalidOperationException("聊天中已有未发送草稿。请先发送或清空，事件证据预览仍保留在事件中心。");
        if (string.IsNullOrWhiteSpace(evidence) || evidence.Length > 16_000)
            throw new InvalidDataException("事件证据为空或超出 16,000 字符上限。");
        Input = evidence;
        Status = "Windows 事件证据已加入草稿；请检查后手动发送。";
    }

    public string SessionSearchText
    {
        get => _sessionSearchText;
        set
        {
            if (SetProperty(ref _sessionSearchText, value))
                ApplySessionFilter();
        }
    }

    public void StageWindowsServiceEvidence(string evidence)
    {
        StageWindowsEventEvidence(evidence);
        Status = "Windows 服务状态证据已加入草稿；请检查后手动发送。";
    }

    public ChatSession? SelectedSession
    {
        get => _selectedSession;
        set
        {
            if (!SetProperty(ref _selectedSession, value))
                return;

            SyncMessages();
            OnPropertyChanged(nameof(SelectedSessionTitle));
            OnPropertyChanged(nameof(HasMessages));
            RaiseCommandStates();
            _ = RefreshToolSessionsSafelyAsync();
        }
    }

    public string SelectedSessionTitle => SelectedSession?.Title ?? "新任务";

    public MemoryEntry? SelectedMemory
    {
        get => _selectedMemory;
        set
        {
            if (!SetProperty(ref _selectedMemory, value))
                return;

            if (value is not null)
            {
                MemoryName = value.Name;
                MemoryContent = value.Content;
                MemoryEnabled = value.Enabled;
                MemoryScope = value.Scope;
                MemoryScopeId = value.ScopeId ?? string.Empty;
            }

            RaiseCommandStates();
        }
    }

    public McpPeerItemViewModel? SelectedMcpPeer
    {
        get => _selectedMcpPeer;
        set
        {
            if (!SetProperty(ref _selectedMcpPeer, value))
                return;

            OnPropertyChanged(nameof(HasSelectedMcpPeer));
            OnPropertyChanged(nameof(SelectedMcpPeerEnabled));
            OnPropertyChanged(nameof(SelectedMcpPeerAllowAutomaticEscalation));
            RaiseMcpPeerStateProperties();
            _ = RefreshSelectedMcpPeerToolsSafelyAsync();
        }
    }

    public bool HasSelectedMcpPeer => SelectedMcpPeer is not null;
    public bool SelectedMcpPeerEnabled => SelectedMcpPeer?.Enabled ?? false;
    public bool SelectedMcpPeerAllowAutomaticEscalation =>
        SelectedMcpPeer?.Profile.AllowAutomaticEscalation ?? false;
    public bool CanConfigureSelectedMcpPeerAutomaticEscalation =>
        !_disposed && !McpPeerBusy && SelectedMcpPeer?.Kind == McpPeerKind.Codex;
    public bool IsSelectedMcpPeerAutomaticEscalationEffective =>
        SelectedMcpPeer is
        {
            Kind: McpPeerKind.Codex,
            Enabled: true,
            State: McpPeerConnectionState.Connected,
            Profile.AllowAutomaticEscalation: true
        } peer && (peer.Profile.AllowedTools ?? []).Contains("codex", StringComparer.OrdinalIgnoreCase);
    public string SelectedMcpPeerAutomaticEscalationStatus => SelectedMcpPeer switch
    {
        null => "选择 Codex Peer 后可配置；任何实际委托仍会逐次请求审批。",
        { Kind: not McpPeerKind.Codex } => "自动评估委托仅支持 Codex Peer。",
        { Profile.AllowAutomaticEscalation: false } => "已关闭；Agent 不会因评估不足主动提出委托。",
        { Enabled: false } => "已保存，但 Peer 未启用，当前不生效。",
        { State: not McpPeerConnectionState.Connected } => "已保存；连接 Codex 后才会生效。",
        var peer when !(peer.Profile.AllowedTools ?? []).Contains("codex", StringComparer.OrdinalIgnoreCase) =>
            "已保存；还需在工具列表中允许 codex 才会生效。",
        _ => "已生效：评估不足时可提出委托；每次调用仍需单独审批。"
    };
    public bool CanConnectSelectedMcpPeer =>
        !_disposed && !McpPeerBusy && SelectedMcpPeer is
        {
            Enabled: true,
            State: McpPeerConnectionState.Disconnected or McpPeerConnectionState.Faulted
        };
    public bool CanDisconnectSelectedMcpPeer =>
        !_disposed && !McpPeerBusy && SelectedMcpPeer?.State is
            McpPeerConnectionState.Connected or McpPeerConnectionState.Connecting;
    public bool CanDeleteSelectedMcpPeer =>
        !_disposed && !McpPeerBusy && SelectedMcpPeer is
        {
            State: McpPeerConnectionState.Disconnected or McpPeerConnectionState.Faulted
        };

    public InspectorTabItem? SelectedInspectorTab
    {
        get => _selectedInspectorTab;
        set => SetProperty(ref _selectedInspectorTab, value);
    }

    public string Input
    {
        get => _input;
        set
        {
            if (SetProperty(ref _input, value))
            {
                OnPropertyChanged(nameof(CanSend));
                RaiseCommandStates();
            }
        }
    }

    public string StreamingContent
    {
        get => _streamingContent;
        private set
        {
            if (SetProperty(ref _streamingContent, value))
                OnPropertyChanged(nameof(HasStreamingContent));
        }
    }

    public string Reasoning
    {
        get => _reasoning;
        private set
        {
            if (SetProperty(ref _reasoning, value))
                OnPropertyChanged(nameof(HasReasoning));
        }
    }

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public string ToolSessionStatus
    {
        get => _toolSessionStatus;
        private set => SetProperty(ref _toolSessionStatus, value);
    }

    public string McpPeerStatus
    {
        get => _mcpPeerStatus;
        private set => SetProperty(ref _mcpPeerStatus, value);
    }

    public string McpPeerToolsStatus
    {
        get => _mcpPeerToolsStatus;
        private set => SetProperty(ref _mcpPeerToolsStatus, value);
    }

    public string LatestEscalationEvent
    {
        get => _latestEscalationEvent;
        private set => SetProperty(ref _latestEscalationEvent, value);
    }

    public bool McpPeerBusy
    {
        get => _mcpPeerBusy;
        private set
        {
            if (SetProperty(ref _mcpPeerBusy, value))
                RaiseMcpPeerStateProperties();
        }
    }

    public bool Busy
    {
        get => _busy;
        private set
        {
            if (!SetProperty(ref _busy, value))
                return;

            OnPropertyChanged(nameof(CanSend));
            OnPropertyChanged(nameof(CanStop));
            RaiseCommandStates();
        }
    }

    public bool IsInspectorOpen
    {
        get => _isInspectorOpen;
        set => SetProperty(ref _isInspectorOpen, value);
    }

    public AgentRunState AgentState
    {
        get => _agentState;
        private set => SetProperty(ref _agentState, value);
    }

    public int? LastTokenUsage
    {
        get => _lastTokenUsage;
        private set => SetProperty(ref _lastTokenUsage, value);
    }

    public bool CanSend => !_disposed && !Busy && !Media.Busy && SelectedSession is not null &&
                           (!string.IsNullOrWhiteSpace(Input) || Media.HasPendingImage);
    public bool CanStop => !_disposed && Busy && _runCancellation is not null;
    public bool HasMessages => Messages.Count > 0;
    public bool HasStreamingContent => !string.IsNullOrWhiteSpace(StreamingContent);
    public bool HasReasoning => !string.IsNullOrWhiteSpace(Reasoning);

    public string ProviderId
    {
        get => _providerId;
        set
        {
            if (!SetProperty(ref _providerId, value))
                return;
            SupportsImageInput = false;
            ApplyProviderPreset();
            RaiseCommandStates();
        }
    }

    public string Endpoint
    {
        get => _endpoint;
        set
        {
            if (SetProperty(ref _endpoint, value))
            {
                SupportsImageInput = false;
                RaiseCommandStates();
            }
        }
    }

    public string Model
    {
        get => _model;
        set
        {
            if (SetProperty(ref _model, value))
            {
                SupportsImageInput = false;
                RaiseCommandStates();
            }
        }
    }

    public string ReasoningEffort
    {
        get => _reasoningEffort;
        set => SetProperty(ref _reasoningEffort, ProviderSettings.NormalizeReasoningEffort(value));
    }

    public int MaxTokens
    {
        get => _maxTokens;
        set => SetProperty(ref _maxTokens, value);
    }

    public int TimeoutSeconds
    {
        get => _timeoutSeconds;
        set => SetProperty(ref _timeoutSeconds, value);
    }

    public bool SelfReviewEnabled
    {
        get => _selfReviewEnabled;
        set => SetProperty(ref _selfReviewEnabled, value);
    }

    public bool SupportsImageInput
    {
        get => _supportsImageInput;
        set => SetProperty(ref _supportsImageInput, value);
    }

    /// <summary>
    /// Transient settings-editor value. It is cleared immediately after DPAPI storage.
    /// Do not log, persist, or place this property in navigation state.
    /// </summary>
    public string ApiKey
    {
        get => _apiKey;
        set => SetProperty(ref _apiKey, value);
    }

    public string MemoryName
    {
        get => _memoryName;
        set
        {
            if (SetProperty(ref _memoryName, value))
                RaiseCommandStates();
        }
    }

    public string MemoryContent
    {
        get => _memoryContent;
        set
        {
            if (SetProperty(ref _memoryContent, value))
                RaiseCommandStates();
        }
    }

    public bool MemoryEnabled
    {
        get => _memoryEnabled;
        set => SetProperty(ref _memoryEnabled, value);
    }

    public MemoryScope MemoryScope
    {
        get => _memoryScope;
        set => SetProperty(ref _memoryScope, value);
    }

    public string MemoryScopeId
    {
        get => _memoryScopeId;
        set => SetProperty(ref _memoryScopeId, value);
    }

    public AsyncRelayCommand SendCommand { get; }
    public RelayCommand StopCommand { get; }
    public AsyncRelayCommand NewSessionCommand { get; }
    public AsyncRelayCommand ClearSessionCommand { get; }
    public AsyncRelayCommand SaveMemoryCommand { get; }
    public AsyncRelayCommand DeleteMemoryCommand { get; }
    public AsyncRelayCommand SaveSettingsCommand { get; }
    public AsyncRelayCommand RefreshToolSessionsCommand { get; }
    public AsyncRelayCommand AcknowledgeToolSessionsCommand { get; }
    public RelayCommand ToggleInspectorCommand { get; }
    public RelayCommand ApplyRecommendedModelSettingsCommand { get; }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        _appSettings = await _settings.LoadAsync(cancellationToken);
        ApplySettings(_appSettings.Provider);
        await MigrateLegacyWorkspaceDataAsync(cancellationToken);
        await RefreshWorkspacesAsync(cancellationToken);
        await RefreshSessionsAsync(cancellationToken: cancellationToken);
        await RefreshMemoriesAsync(cancellationToken);

        if (SelectedSession is null)
            await NewSessionAsync(cancellationToken);

        await RefreshToolSessionsAsync(cancellationToken);
        await RefreshMcpPeersAsync(cancellationToken);
        Status = "就绪";
    }

    public async Task RefreshWorkspacesAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var state = await _workspaceCatalog.GetStateAsync(cancellationToken);
        var recent = state.Recent
            .OrderByDescending(item => item.LastOpenedAt)
            .ToArray();
        await _dispatcher.InvokeAsync(() =>
        {
            RecentWorkspaces.Clear();
            foreach (var item in recent)
                RecentWorkspaces.Add(item);
        }, cancellationToken);
    }

    public async Task OpenWorkspaceAsync(
        string root,
        CancellationToken cancellationToken = default)
    {
        await EnsureWorkspaceSwitchAllowedAsync(cancellationToken);
        var workspace = await _workspaceCatalog.RegisterExistingAsync(root, cancellationToken);
        await ActivateWorkspaceAsync(workspace, cancellationToken);
    }

    public async Task CreateWorkspaceAsync(
        string parentRoot,
        string projectName,
        CancellationToken cancellationToken = default)
    {
        await EnsureWorkspaceSwitchAllowedAsync(cancellationToken);
        var workspace = await _workspaceCatalog.CreateAsync(parentRoot, projectName, cancellationToken);
        await ActivateWorkspaceAsync(workspace, cancellationToken);
    }

    public async Task ForgetWorkspaceAsync(
        WorkspaceEntry workspace,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        if (workspace.Id.Equals(CurrentWorkspaceId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("当前正在使用的工作区不能从最近项目中移除。");

        await _workspaceCatalog.ForgetAsync(workspace.Id, cancellationToken);
        await RefreshWorkspacesAsync(cancellationToken);
        Status = $"已从最近项目移除 {workspace.Name}";
    }

    public async Task EnsureWorkspaceSwitchAllowedAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (Busy)
            throw new InvalidOperationException("请先停止当前生成，再切换工作区。");
        if (!string.IsNullOrWhiteSpace(Input))
            throw new InvalidOperationException("输入框还有未发送的内容；请先发送或清空，再切换工作区。");
        if (Media.HasPendingImage)
            throw new InvalidOperationException("仍有未发送的图片附件；请先发送或移除，再切换工作区。");

        var jobs = await _backgroundCommands.ListAsync(cancellationToken: cancellationToken);
        var active = jobs.Count(item => item.State is BackgroundCommandState.Starting or
            BackgroundCommandState.Running or BackgroundCommandState.Stopping);
        if (active > 0)
            throw new InvalidOperationException($"仍有 {active} 个后台命令在运行；请先停止它们，再切换工作区。");
    }

    public async ValueTask OnStateAsync(AgentRunState state)
    {
        await _dispatcher.InvokeAsync(() => AgentState = state);
    }

    public async ValueTask OnEventAsync(StreamEvent value)
    {
        var refreshTools = value.Kind == StreamEventKind.ToolSessionUpdated;
        await _dispatcher.InvokeAsync(() =>
        {
            switch (value.Kind)
            {
                case StreamEventKind.Content:
                    StreamingContent += value.Text;
                    break;
                case StreamEventKind.Reasoning:
                    Reasoning += value.Text;
                    break;
                case StreamEventKind.Revision:
                    StreamingContent = value.Text;
                    break;
                case StreamEventKind.Usage:
                    LastTokenUsage = value.Tokens;
                    break;
                case StreamEventKind.ToolStarted:
                    Status = $"工具请求：{value.Text}";
                    break;
                case StreamEventKind.ToolCompleted:
                    Status = value.ToolResult?.Status == ToolExecutionStatus.Success
                        ? $"工具完成：{value.ToolResult.ToolName}"
                        : $"工具未完成：{value.ToolResult?.Error ?? value.Text}";
                    break;
                case StreamEventKind.Escalation:
                    LatestEscalationEvent = FormatEscalationEvent(value);
                    Status = value.Escalation?.ShouldEscalate == true
                        ? "委托评估完成：建议向已配置的 Peer 提出委托"
                        : "委托评估完成：本轮不提出委托";
                    break;
            }
        });

        if (refreshTools)
            await RefreshToolSessionsSafelyAsync();
    }

    public ValueTask<bool> RequestToolApprovalAsync(
        ToolApprovalRequest request,
        CancellationToken cancellationToken) =>
        new(_interaction.RequestToolApprovalAsync(request, cancellationToken));

    public async Task SaveSettingsAsync(string? apiKey, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var provider = CurrentProviderSettings();
        _appSettings = new AppSettings(provider, _appSettings.SystemPrompt, _appSettings.Version);
        await _settings.SaveAsync(_appSettings, cancellationToken);
        if (!string.IsNullOrWhiteSpace(apiKey))
            await _secrets.SetAsync(provider.ProviderId, apiKey, cancellationToken);

        Status = "设置已安全保存";
    }

    public async Task RefreshMcpPeersAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var selectedId = SelectedMcpPeer?.Id;
        var peers = (await _mcpPeers.ListAsync(cancellationToken))
            .OrderBy(item => item.Kind)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .Select(item => new McpPeerItemViewModel(item))
            .ToArray();

        await _dispatcher.InvokeAsync(() =>
        {
            McpPeers.Clear();
            foreach (var peer in peers)
                McpPeers.Add(peer);

            SelectedMcpPeer = peers.FirstOrDefault(item => item.Id == selectedId) ?? peers.FirstOrDefault();
            McpPeerStatus = peers.Length == 0
                ? "尚未配置外部 Agent；添加后仍需连接并明确允许工具。"
                : $"已配置 {peers.Length} 个外部 Agent；只有已启用且明确允许的工具可供 Agent 请求。";
        }, cancellationToken);
    }

    public async Task AddExecutableMcpPeerAsync(
        McpPeerKind kind,
        string executablePath,
        CancellationToken cancellationToken = default)
    {
        if (kind is not (McpPeerKind.Codex or McpPeerKind.Claude))
            throw new ArgumentOutOfRangeException(nameof(kind), "本地可执行文件预设仅支持 Codex 或 Claude Code。");
        if (string.IsNullOrWhiteSpace(executablePath) || !Path.IsPathFullyQualified(executablePath))
            throw new InvalidDataException("请选择绝对路径的 .exe 可执行文件。");

        var fullPath = Path.GetFullPath(executablePath);
        if (!Path.GetExtension(fullPath).Equals(".exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(fullPath))
            throw new InvalidDataException("选择的 .exe 不存在，未添加外部 Agent。");

        var name = kind == McpPeerKind.Codex ? "Codex" : "Claude Code";
        var profile = new McpPeerProfile(
            $"mcp-{Guid.NewGuid():N}",
            name,
            kind,
            Enabled: true,
            ExecutablePath: fullPath,
            AllowedTools: []);

        await SaveMcpPeerAsync(profile, $"已添加 {name}；请先连接，再逐项允许它提供的工具。", cancellationToken);
    }

    public async Task AddCustomHttpMcpPeerAsync(
        string name,
        string endpoint,
        CancellationToken cancellationToken = default)
    {
        var displayName = string.IsNullOrWhiteSpace(name) ? "本地 MCP" : name.Trim();
        if (!Uri.TryCreate(endpoint?.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            !uri.IsLoopback || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0)
        {
            throw new InvalidDataException(
                "自定义 HTTP MCP 只允许 http(s)://localhost、127.0.0.1 或 [::1] 回环地址，且不能包含凭据、查询参数或片段。");
        }

        var profile = new McpPeerProfile(
            $"mcp-{Guid.NewGuid():N}",
            displayName,
            McpPeerKind.CustomHttp,
            Enabled: true,
            Endpoint: uri.AbsoluteUri,
            AllowedTools: []);

        await SaveMcpPeerAsync(profile, $"已添加 {displayName}；请先连接，再逐项允许它提供的工具。", cancellationToken);
    }

    public async Task SetSelectedMcpPeerEnabledAsync(
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        var selected = SelectedMcpPeer ?? throw new InvalidOperationException("请先选择一个外部 Agent。");
        if (selected.Enabled == enabled)
            return;

        await SaveMcpPeerAsync(
            selected.Profile with { Enabled = enabled },
            enabled ? $"已启用 {selected.Name}。" : $"已停用 {selected.Name}。",
            cancellationToken);
    }

    public async Task SetSelectedMcpPeerAutomaticEscalationAsync(
        bool allowed,
        CancellationToken cancellationToken = default)
    {
        var selected = SelectedMcpPeer ?? throw new InvalidOperationException("请先选择一个外部 Agent。");
        if (selected.Kind != McpPeerKind.Codex)
            throw new InvalidOperationException("自动评估委托仅支持 Codex Peer。");
        if (selected.Profile.AllowAutomaticEscalation == allowed)
            return;

        await SaveMcpPeerAsync(
            selected.Profile with { AllowAutomaticEscalation = allowed },
            allowed
                ? "已允许评估不足时提出 Codex 委托；实际调用仍会逐次请求审批。"
                : "已关闭自动评估委托。",
            cancellationToken);
    }

    public async Task SetSelectedMcpPeerToolAllowedAsync(
        string toolName,
        bool allowed,
        CancellationToken cancellationToken = default)
    {
        var selected = SelectedMcpPeer ?? throw new InvalidOperationException("请先选择一个外部 Agent。");
        if (selected.State != McpPeerConnectionState.Connected)
            throw new InvalidOperationException("外部 Agent 未连接，不能修改工具白名单。");

        var allowedTools = new HashSet<string>(selected.Profile.AllowedTools ?? [], StringComparer.OrdinalIgnoreCase);
        if (allowed)
            allowedTools.Add(toolName);
        else
            allowedTools.Remove(toolName);

        await SaveMcpPeerAsync(
            selected.Profile with
            {
                AllowedTools = allowedTools.OrderBy(item => item, StringComparer.OrdinalIgnoreCase).ToArray()
            },
            allowed ? $"已允许工具 {toolName}。" : $"已禁止工具 {toolName}。",
            cancellationToken);
    }

    public async Task ConnectSelectedMcpPeerAsync(CancellationToken cancellationToken = default)
    {
        var selected = SelectedMcpPeer ?? throw new InvalidOperationException("请先选择一个外部 Agent。");
        await RunMcpPeerOperationAsync(
            async ct => await _mcpPeers.ConnectAsync(selected.Id, ct),
            cancellationToken);
        McpPeerStatus = $"{selected.Name} 已连接；正在发现工具。";
        await RefreshSelectedMcpPeerToolsAsync(cancellationToken);
    }

    public async Task DisconnectSelectedMcpPeerAsync(CancellationToken cancellationToken = default)
    {
        var selected = SelectedMcpPeer ?? throw new InvalidOperationException("请先选择一个外部 Agent。");
        await RunMcpPeerOperationAsync(
            ct => _mcpPeers.DisconnectAsync(selected.Id, ct),
            cancellationToken);
        McpPeerStatus = $"{selected.Name} 已断开。";
    }

    public async Task DeleteSelectedMcpPeerAsync(CancellationToken cancellationToken = default)
    {
        var selected = SelectedMcpPeer ?? throw new InvalidOperationException("请先选择一个外部 Agent。");
        if (selected.State is McpPeerConnectionState.Connected or McpPeerConnectionState.Connecting)
            throw new InvalidOperationException("请先断开外部 Agent，再删除配置。");

        await RunMcpPeerOperationAsync(
            ct => _mcpPeers.DeleteAsync(selected.Id, ct),
            cancellationToken);
        McpPeerStatus = $"已删除 {selected.Name} 的 MCP 配置。";
    }

    private AsyncRelayCommand CreateAsyncCommand(Func<Task> action, Func<bool>? canExecute = null) =>
        new(action, canExecute, HandleCommandErrorAsync);

    private async Task SendAsync()
    {
        var session = SelectedSession;
        var text = Input.Trim();
        if (session is null || (string.IsNullOrWhiteSpace(text) && !Media.HasPendingImage) || Busy)
            return;

        StreamingContent = string.Empty;
        Reasoning = string.Empty;
        LastTokenUsage = null;

        using var operation = new CancellationTokenSource();
        _runCancellation = operation;
        Busy = true;
        Status = "生成中…";
        AgentState = AgentRunState.Streaming;

        try
        {
            var provider = CurrentProviderSettings();
            var baseOptions = new AgentRunOptions(
                SystemPrompt: _appSettings.SystemPrompt,
                WorkspaceId: CurrentWorkspaceId);
            using var imageLease = await Media.PrepareRunAsync(
                provider, baseOptions, operation.Token, maxModelRequests: 4);
            if (imageLease is null)
            {
                AgentState = AgentRunState.Idle;
                Status = "图片发送已取消";
                return;
            }
            if (string.IsNullOrWhiteSpace(text) && imageLease.RunOptions.Images is { Count: > 0 })
                text = "请分析这张图片并说明重要信息、证据与不确定之处。";
            Input = string.Empty;
            var apiKey = await _secrets.GetAsync(provider.ProviderId, operation.Token);
            var result = await _agent.RunAsync(
                session,
                text,
                provider,
                apiKey,
                this,
                operation.Token,
                imageLease.RunOptions);

            await _dispatcher.InvokeAsync(() =>
            {
                SelectedSession = result.Session;
                StreamingContent = string.Empty;
                LastTokenUsage = result.Tokens;
                AgentState = result.State;
                Status = result.State switch
                {
                    AgentRunState.Failed => $"错误：{result.Error}",
                    AgentRunState.Cancelled => "已停止",
                    _ => $"完成 · Tokens {result.Tokens?.ToString() ?? "未知"}"
                };
            });
            await RefreshSessionsAsync(result.Session.Id, operation.Token);
        }
        catch (OperationCanceledException)
        {
            await _dispatcher.InvokeAsync(() =>
            {
                AgentState = AgentRunState.Cancelled;
                Status = "已停止";
            });
        }
        catch (Exception ex)
        {
            await _dispatcher.InvokeAsync(() =>
            {
                AgentState = AgentRunState.Failed;
                Status = $"错误：{ex.Message}";
            });
        }
        finally
        {
            if (ReferenceEquals(_runCancellation, operation))
                _runCancellation = null;
            await _dispatcher.InvokeAsync(() => Busy = false);
            await RefreshToolSessionsSafelyAsync();
        }
    }

    private void Stop() => _runCancellation?.Cancel();

    private async Task ActivateWorkspaceAsync(
        WorkspaceEntry workspace,
        CancellationToken cancellationToken)
    {
        if (workspace.Id.Equals(CurrentWorkspaceId, StringComparison.OrdinalIgnoreCase))
        {
            await RefreshWorkspacesAsync(cancellationToken);
            Status = $"当前已在工作区 {workspace.Name}";
            return;
        }

        Status = $"正在切换到 {workspace.Name}…";
        try
        {
            _workspaceRelaunch.Relaunch(workspace.Root);
        }
        catch
        {
            // RegisterExistingAsync/CreateAsync make the candidate current. If the
            // new process cannot start, restore this still-running window's root.
            await _workspaceCatalog.RegisterExistingAsync(CurrentWorkspacePath, cancellationToken);
            await RefreshWorkspacesAsync(cancellationToken);
            Status = "工作区切换失败；已保留当前工作区";
            throw;
        }
    }

    private Task NewSessionAsync() => NewSessionAsync(CancellationToken.None);

    private async Task NewSessionAsync(CancellationToken cancellationToken)
    {
        var session = AgentRuntime.NewSession(CurrentWorkspaceId);
        await _sessions.SaveAsync(session, cancellationToken);
        await _dispatcher.InvokeAsync(() =>
        {
            SessionSearchText = string.Empty;
            Sessions.Insert(0, session);
            ApplySessionFilter();
            SelectedSession = session;
            Status = "已创建新任务";
        }, cancellationToken);
    }

    private async Task ClearSessionAsync()
    {
        var selected = SelectedSession;
        if (selected is null)
            return;

        var cleared = selected with
        {
            Title = "New chat",
            Messages = [],
            UpdatedAt = DateTimeOffset.UtcNow
        };
        await _sessions.SaveAsync(cleared);
        await RefreshSessionsAsync(cleared.Id);
        Status = "当前会话已清空";
    }

    private async Task RefreshSessionsAsync(
        string? selectedId = null,
        CancellationToken cancellationToken = default)
    {
        var items = (await _sessions.ListAsync(cancellationToken))
            .Where(item => item.WorkspaceId?.Equals(
                CurrentWorkspaceId, StringComparison.OrdinalIgnoreCase) == true)
            .ToArray();
        var wanted = selectedId ?? SelectedSession?.Id;
        await _dispatcher.InvokeAsync(() =>
        {
            Sessions.Clear();
            foreach (var item in items)
                Sessions.Add(item);
            ApplySessionFilter();
            SelectedSession = Sessions.FirstOrDefault(item => item.Id == wanted) ?? Sessions.FirstOrDefault();
        }, cancellationToken);
    }

    private void ApplySessionFilter()
    {
        var query = SessionSearchText.Trim();
        VisibleSessions.Clear();
        foreach (var session in Sessions.Where(session =>
                     query.Length == 0 ||
                     session.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                     session.Messages.TakeLast(12).Any(message =>
                         (message.Role is ChatRole.User or ChatRole.Assistant) &&
                         message.Content.Contains(query, StringComparison.OrdinalIgnoreCase))))
        {
            VisibleSessions.Add(session);
        }
    }

    private async Task RefreshMemoriesAsync(CancellationToken cancellationToken = default)
    {
        var currentSessionIds = (await _sessions.ListAsync(cancellationToken))
            .Where(item => item.WorkspaceId?.Equals(
                CurrentWorkspaceId, StringComparison.OrdinalIgnoreCase) == true)
            .Select(item => item.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var items = (await _memories.ListAsync(cancellationToken))
            .Where(item => item.Scope switch
            {
                MemoryScope.User => true,
                MemoryScope.Project => item.WorkspaceId?.Equals(
                    CurrentWorkspaceId, StringComparison.OrdinalIgnoreCase) == true,
                MemoryScope.Session => item.ScopeId is not null && currentSessionIds.Contains(item.ScopeId),
                _ => false
            })
            .ToArray();
        await _dispatcher.InvokeAsync(() =>
        {
            Memories.Clear();
            foreach (var item in items)
                Memories.Add(item);
        }, cancellationToken);
    }

    private async Task SaveMemoryAsync()
    {
        if (MemoryName.Length > 120 || MemoryContent.Length > 12_000)
            throw new InvalidDataException("记忆名称最多 120 字，内容最多 12,000 字。");
        if (SensitiveDataRedactor.ContainsLikelySecret(MemoryContent))
            throw new InvalidDataException("检测到疑似密钥、Token、密码或私钥，记忆未保存。");
        if (MemoryScope == MemoryScope.Project && string.IsNullOrWhiteSpace(MemoryScopeId))
            throw new InvalidDataException("项目记忆需要相对路径范围。");

        var scopeId = MemoryScope switch
        {
            MemoryScope.User => null,
            MemoryScope.Session when string.IsNullOrWhiteSpace(MemoryScopeId) => SelectedSession?.Id,
            _ => MemoryScopeId.Trim()
        };
        var memory = new MemoryEntry(
            SelectedMemory?.Id ?? $"MEM-{Guid.NewGuid():N}",
            MemoryName.Trim(),
            MemoryContent.Trim(),
            MemoryEnabled,
            DateTimeOffset.UtcNow,
            MemoryScope,
            scopeId,
            WorkspaceId: MemoryScope == MemoryScope.Project ? CurrentWorkspaceId : null);
        await _memories.SaveAsync(memory);
        await RefreshMemoriesAsync();
        SelectedMemory = memory;
        Status = "记忆已保存";
    }

    private async Task DeleteMemoryAsync()
    {
        var selected = SelectedMemory;
        if (selected is null)
            return;

        await _memories.DeleteAsync(selected.Id);
        SelectedMemory = null;
        MemoryName = string.Empty;
        MemoryContent = string.Empty;
        await RefreshMemoriesAsync();
        Status = "记忆已删除";
    }

    private async Task MigrateLegacyWorkspaceDataAsync(CancellationToken cancellationToken)
    {
        foreach (var session in (await _sessions.ListAsync(cancellationToken))
                     .Where(item => string.IsNullOrWhiteSpace(item.WorkspaceId)))
        {
            await _sessions.SaveAsync(session with
            {
                WorkspaceId = CurrentWorkspaceId,
                Version = Math.Max(session.Version, 2)
            }, cancellationToken);
        }

        foreach (var memory in (await _memories.ListAsync(cancellationToken))
                     .Where(item => item.Scope == MemoryScope.Project &&
                                    string.IsNullOrWhiteSpace(item.WorkspaceId)))
        {
            await _memories.SaveAsync(memory with { WorkspaceId = CurrentWorkspaceId }, cancellationToken);
        }
    }

    private async Task SaveSettingsFromEditorAsync()
    {
        var secret = ApiKey;
        try
        {
            await SaveSettingsAsync(secret);
        }
        finally
        {
            ApiKey = string.Empty;
        }
    }

    private Task RefreshToolSessionsAsync() => RefreshToolSessionsAsync(CancellationToken.None);

    private async Task RefreshToolSessionsAsync(CancellationToken cancellationToken)
    {
        var sessionId = SelectedSession?.Id;
        if (sessionId is null)
        {
            await _dispatcher.InvokeAsync(() =>
            {
                ToolSessions.Clear();
                ToolSessionStatus = "没有选中的聊天";
            }, cancellationToken);
            return;
        }

        var definitions = _toolRegistry.GetDefinitions();
        await _toolSessions.EnsureSessionsAsync(sessionId, definitions, cancellationToken);
        var definitionByName = definitions.ToDictionary(item => item.Name, StringComparer.OrdinalIgnoreCase);
        var persisted = (await _toolSessions.ListAsync(sessionId, cancellationToken))
            .Where(item => definitionByName.ContainsKey(item.ToolName))
            .OrderBy(item => item.ToolName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        await _dispatcher.InvokeAsync(() =>
        {
            ToolSessions.Clear();
            foreach (var item in persisted)
            {
                var definition = definitionByName[item.ToolName];
                var last = item.RecentInvocations.LastOrDefault();
                ToolSessions.Add(new(
                    item.ToolName,
                    definition.Description,
                    definition.RiskLevel,
                    item.State,
                    item.TotalCalls,
                    item.SuccessCount,
                    item.FailureCount,
                    item.TotalCalls == 0 ? "-" : $"{100d * item.SuccessCount / item.TotalCalls:F0}%",
                    last?.ResultSummary ?? "尚未调用",
                    last?.Error ?? string.Empty,
                    item.LastSuccessfulStrategy ?? string.Empty));
            }

            ToolSessionStatus =
                $"当前聊天已建立 {persisted.Length}/{definitions.Count} 个工具会话；模型每轮最多激活 8 个相关工具。";
            RaiseCommandStates();
        }, cancellationToken);
    }

    private async Task RefreshToolSessionsSafelyAsync()
    {
        try
        {
            await RefreshToolSessionsAsync();
        }
        catch (Exception) when (!_disposed)
        {
            await _dispatcher.InvokeAsync(() =>
                ToolSessionStatus = "工具会话统计暂不可用；Agent 仍可执行工具。");
        }
    }

    private async Task AcknowledgeToolSessionsAsync()
    {
        var sessionId = SelectedSession?.Id;
        if (sessionId is null)
            return;

        Busy = true;
        try
        {
            var persisted = await _toolSessions.ListAsync(sessionId);
            foreach (var item in persisted.Where(item => item.State == ToolSessionState.NeedsReview))
                await _toolSessions.AcknowledgeNeedsReviewAsync(sessionId, item.ToolName);
            await RefreshToolSessionsAsync();
            ToolSessionStatus = "已记录人工检查；遗留工具会话可接受新的请求。";
        }
        finally
        {
            Busy = false;
        }
    }

    private async Task SaveMcpPeerAsync(
        McpPeerProfile profile,
        string successStatus,
        CancellationToken cancellationToken)
    {
        await RunMcpPeerOperationAsync(
            ct => _mcpPeers.SaveAsync(profile, ct),
            cancellationToken);

        await _dispatcher.InvokeAsync(() =>
        {
            SelectedMcpPeer = McpPeers.FirstOrDefault(item => item.Id == profile.Id) ?? SelectedMcpPeer;
            McpPeerStatus = successStatus;
        }, cancellationToken);
    }

    private async Task RunMcpPeerOperationAsync(
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (McpPeerBusy)
            throw new InvalidOperationException("另一个外部 Agent 操作仍在进行中。");

        McpPeerBusy = true;
        try
        {
            await operation(cancellationToken);
            await RefreshMcpPeersAsync(cancellationToken);
        }
        catch
        {
            try
            {
                await RefreshMcpPeersAsync(CancellationToken.None);
            }
            catch
            {
                // Preserve the original connection/configuration error for the dialog.
            }

            throw;
        }
        finally
        {
            McpPeerBusy = false;
        }
    }

    public async Task RefreshSelectedMcpPeerToolsAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var selected = SelectedMcpPeer;
        if (selected?.State != McpPeerConnectionState.Connected)
        {
            await _dispatcher.InvokeAsync(() =>
            {
                SelectedMcpPeerTools.Clear();
                McpPeerToolsStatus = selected is null
                    ? "选择并连接一个外部 Agent 后发现工具"
                    : "此外部 Agent 尚未连接；刷新不会隐式启动外部进程。";
            }, cancellationToken);
            return;
        }

        var peerId = selected.Id;
        var allowedTools = new HashSet<string>(selected.Profile.AllowedTools ?? [], StringComparer.OrdinalIgnoreCase);
        var tools = (await _mcpPeers.ListToolsAsync(peerId, cancellationToken))
            .OrderBy(item => item.Name, StringComparer.Ordinal)
            .Select(item => new McpPeerToolItemViewModel(
                item.Name,
                item.Description,
                item.InputSchemaJson,
                allowedTools.Contains(item.Name) || item.Allowed,
                item.Truncated))
            .ToArray();

        await _dispatcher.InvokeAsync(() =>
        {
            if (SelectedMcpPeer?.Id != peerId)
                return;

            SelectedMcpPeerTools.Clear();
            foreach (var tool in tools)
                SelectedMcpPeerTools.Add(tool);
            McpPeerToolsStatus = tools.Length == 0
                ? "连接成功，但对方没有公开 MCP 工具。"
                : $"发现 {tools.Length} 个工具；默认禁用，请逐项允许。实际调用仍会经过工具审批。";
        }, cancellationToken);
    }

    private async Task RefreshSelectedMcpPeerToolsSafelyAsync()
    {
        try
        {
            await RefreshSelectedMcpPeerToolsAsync();
        }
        catch (Exception exception) when (!_disposed)
        {
            await _dispatcher.InvokeAsync(() =>
            {
                SelectedMcpPeerTools.Clear();
                McpPeerToolsStatus = $"工具发现失败：{exception.Message}";
            });
        }
    }

    private async Task RefreshMcpPeersSafelyAsync()
    {
        try
        {
            await RefreshMcpPeersAsync();
        }
        catch (Exception exception) when (!_disposed)
        {
            await _dispatcher.InvokeAsync(() =>
                McpPeerStatus = $"外部 Agent 状态刷新失败：{exception.Message}");
        }
    }

    private void McpPeers_Changed()
    {
        if (!_disposed && !McpPeerBusy)
            _ = RefreshMcpPeersSafelyAsync();
    }

    private void Media_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MediaAttachmentViewModel.HasPendingImage) or
            nameof(MediaAttachmentViewModel.Busy))
        {
            OnPropertyChanged(nameof(CanSend));
            RaiseCommandStates();
        }
    }

    private void RaiseMcpPeerStateProperties()
    {
        OnPropertyChanged(nameof(CanConnectSelectedMcpPeer));
        OnPropertyChanged(nameof(CanDisconnectSelectedMcpPeer));
        OnPropertyChanged(nameof(CanDeleteSelectedMcpPeer));
        OnPropertyChanged(nameof(CanConfigureSelectedMcpPeerAutomaticEscalation));
        OnPropertyChanged(nameof(IsSelectedMcpPeerAutomaticEscalationEffective));
        OnPropertyChanged(nameof(SelectedMcpPeerAutomaticEscalationStatus));
    }

    private static string FormatEscalationEvent(StreamEvent value)
    {
        var timestamp = DateTimeOffset.Now.ToString("HH:mm:ss");
        var decision = value.Escalation;
        if (decision is null)
            return $"{timestamp} · {SensitiveDataRedactor.Text(value.Text, 1_000)}";

        var reasons = decision.Reasons.Count == 0
            ? "未提供评分理由"
            : string.Join("；", decision.Reasons.Take(6)
                .Select(reason => SensitiveDataRedactor.Text(reason, 300)));
        var summary = string.IsNullOrWhiteSpace(value.Text)
            ? decision.ShouldEscalate ? "建议委托" : "不建议委托"
            : SensitiveDataRedactor.Text(value.Text, 600);
        var suppressed = string.IsNullOrWhiteSpace(decision.SuppressedReason)
            ? string.Empty
            : $"\n未触发原因：{SensitiveDataRedactor.Text(decision.SuppressedReason, 500)}";
        return $"{timestamp} · {summary}\n评分：{decision.Score}；理由：{reasons}{suppressed}";
    }

    private void SyncMessages()
    {
        Messages.Clear();
        if (SelectedSession is not null)
        {
            foreach (var message in SelectedSession.Messages)
            {
                Messages.Add(new(
                    message.Role,
                    message.Content,
                    message.CreatedAt,
                    ToolName: message.ToolName));
            }
        }

        OnPropertyChanged(nameof(HasMessages));
    }

    private ProviderSettings CurrentProviderSettings() => new(
        ProviderId.Trim(),
        Endpoint.Trim(),
        Model.Trim(),
        MaxOutputTokens: Math.Clamp(MaxTokens, 256, 128_000),
        TimeoutSeconds: Math.Clamp(TimeoutSeconds, 5, 600),
        MaxContextMessages: _appSettings.Provider.MaxContextMessages,
        SelfReviewEnabled: SelfReviewEnabled,
        MaxSelfReviewRounds: _appSettings.Provider.MaxSelfReviewRounds,
        SupportsImageInput: SupportsImageInput,
        ReasoningEffort: ProviderSettings.NormalizeReasoningEffort(ReasoningEffort));

    private void ApplySettings(ProviderSettings provider)
    {
        _providerId = provider.ProviderId;
        _endpoint = provider.Endpoint;
        _model = provider.Model;
        _reasoningEffort = ProviderSettings.NormalizeReasoningEffort(provider.ReasoningEffort);
        _maxTokens = provider.MaxOutputTokens;
        _timeoutSeconds = provider.TimeoutSeconds;
        _selfReviewEnabled = provider.SelfReviewEnabled;
        _supportsImageInput = provider.SupportsImageInput;
        OnPropertyChanged(nameof(ProviderId));
        OnPropertyChanged(nameof(Endpoint));
        OnPropertyChanged(nameof(Model));
        OnPropertyChanged(nameof(ReasoningEffort));
        OnPropertyChanged(nameof(MaxTokens));
        OnPropertyChanged(nameof(TimeoutSeconds));
        OnPropertyChanged(nameof(SelfReviewEnabled));
        OnPropertyChanged(nameof(SupportsImageInput));
    }

    private void ApplyProviderPreset()
    {
        var preset = ProviderId switch
        {
            "deepseek" => ("https://api.deepseek.com/v1", "deepseek-chat"),
            "openai" => ("https://api.openai.com/v1", "gpt-5.6-sol"),
            "openrouter" => ("https://openrouter.ai/api/v1", "openai/gpt-5.6-sol"),
            "ollama" => ("http://localhost:11434/v1", "qwen2.5:7b"),
            _ => (Endpoint, Model)
        };
        Endpoint = preset.Item1;
        Model = preset.Item2;
    }

    private void ApplyRecommendedModelSettings()
    {
        ProviderId = "openai";
        Endpoint = "https://api.openai.com/v1";
        Model = "gpt-5.6-sol";
        ReasoningEffort = "medium";
        MaxTokens = 32_768;
        TimeoutSeconds = 300;
        SupportsImageInput = false;
        Status = "已载入推荐 GPT-5.6 设置；点击保存设置后生效";
        RaiseCommandStates();
    }

    private async Task HandleCommandErrorAsync(Exception exception)
    {
        Status = $"错误：{exception.Message}";
        await _interaction.NotifyAsync("K.netagent", exception.Message, UserNotificationKind.Error);
    }

    private void RaiseCommandStates()
    {
        foreach (var command in new ICommand[]
                 {
                     SendCommand,
                     StopCommand,
                     NewSessionCommand,
                     ClearSessionCommand,
                     SaveMemoryCommand,
                     DeleteMemoryCommand,
                     SaveSettingsCommand,
                     RefreshToolSessionsCommand,
                     AcknowledgeToolSessionsCommand,
                     ToggleInspectorCommand,
                     ApplyRecommendedModelSettingsCommand
                 }.OfType<CommandBase>())
        {
            command.RaiseCanExecuteChanged();
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _mcpPeers.Changed -= McpPeers_Changed;
        _media.PropertyChanged -= Media_PropertyChanged;
        _runCancellation?.Cancel();
        _runCancellation?.Dispose();
        _runCancellation = null;
        ApiKey = string.Empty;
        RaiseCommandStates();
        GC.SuppressFinalize(this);
    }
}

public sealed class McpPeerItemViewModel
{
    public McpPeerItemViewModel(McpPeerInfo info)
    {
        Profile = info.Profile;
        State = info.State;
        LastError = info.LastError ?? string.Empty;
        ConnectedAtText = info.ConnectedAt is null
            ? string.Empty
            : $"连接于 {info.ConnectedAt.Value.ToLocalTime():yyyy-MM-dd HH:mm:ss}";
    }

    public McpPeerProfile Profile { get; }
    public string Id => Profile.Id;
    public string Name => Profile.Name;
    public McpPeerKind Kind => Profile.Kind;
    public bool Enabled => Profile.Enabled;
    public McpPeerConnectionState State { get; }
    public string LastError { get; }
    public string ConnectedAtText { get; }
    public string KindText => Kind switch
    {
        McpPeerKind.Codex => "Codex · stdio",
        McpPeerKind.Claude => "Claude Code · stdio",
        _ => "自定义 HTTP · loopback"
    };
    public string StateText => State switch
    {
        McpPeerConnectionState.Disconnected => "已断开",
        McpPeerConnectionState.Connecting => "连接中",
        McpPeerConnectionState.Connected => "已连接",
        McpPeerConnectionState.Disconnecting => "断开中",
        _ => "故障"
    };
    public string EnabledText => Enabled ? "已启用" : "已停用";
    public string SourceText => Profile.ExecutablePath ?? Profile.Endpoint ?? "未配置连接目标";
}

public sealed record McpPeerToolItemViewModel(
    string Name,
    string Description,
    string InputSchemaJson,
    bool IsAllowed,
    bool Truncated);
