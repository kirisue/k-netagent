using System.Collections.ObjectModel;
using KNetAgent.Desktop.WinUI.Services;
using KNetAgent.Desktop.WinUI.ViewModels;
using TestAgent.Core;
using TestAgent.Infrastructure;

namespace KNetAgent.Desktop.WinUI.Features.Tasks;

public sealed class TaskWorkspaceViewModel : ObservableObject
{
    private readonly ITaskWorkflowService _workflow;
    private readonly ISettingsStore _settings;
    private readonly ISecureSecretStore _secrets;
    private readonly WorkspaceLocator _workspace;
    private readonly IWorkspaceExplorer _explorer;
    private readonly IWorkspaceChangeSource _changes;
    private readonly IUiDispatcher _dispatcher;
    private IAgentObserver? _observer;
    private CancellationTokenSource? _runCancellation;
    private CancellationTokenSource? _activeOperationCancellation;
    private string _goal = "";
    private string _status = "尚未加载任务工作区";
    private string _filePreview = "选择文件后点击“预览文件”。";
    private bool _isBusy;
    private bool _isApproved;
    private bool _treeTruncated;
    private TaskPlanItemViewModel? _selectedPlan;
    private WorkspaceFileItemViewModel? _selectedFile;
    private WorkspaceChangeItemViewModel? _selectedChange;

    public TaskWorkspaceViewModel(
        ITaskWorkflowService workflow,
        ISettingsStore settings,
        ISecureSecretStore secrets,
        WorkspaceLocator workspace,
        IWorkspaceExplorer explorer,
        IWorkspaceChangeSource changes,
        IUiDispatcher dispatcher)
    {
        _workflow = workflow;
        _settings = settings;
        _secrets = secrets;
        _workspace = workspace;
        _explorer = explorer;
        _changes = changes;
        _dispatcher = dispatcher;
        RefreshCommand = new(RefreshAsync, () => !IsBusy, HandleErrorAsync);
        CreatePlanCommand = new(CreatePlanAsync,
            () => !IsBusy && _observer is not null && !string.IsNullOrWhiteSpace(Goal), HandleErrorAsync);
        ApproveCommand = new(Approve, () => !IsBusy && SelectedPlan is not null &&
            SelectedPlan.Status is TaskGraphStatus.Ready or TaskGraphStatus.Interrupted);
        RunOrResumeCommand = new(RunOrResumeAsync,
            () => !IsBusy && IsApproved && SelectedPlan is not null && _observer is not null &&
                  SelectedPlan.Status is TaskGraphStatus.Ready or TaskGraphStatus.Interrupted,
            HandleErrorAsync);
        AcknowledgeReviewCommand = new(AcknowledgeReviewAsync,
            () => !IsBusy && SelectedPlan?.Status == TaskGraphStatus.NeedsReview, HandleErrorAsync);
        StopCommand = new(Stop, () => IsBusy && _runCancellation is not null);
        PreviewFileCommand = new(PreviewFileAsync,
            () => !IsBusy && SelectedFile is { IsDirectory: false }, HandleErrorAsync);
        RefreshChangesCommand = new(RefreshChangesAsync, () => !IsBusy, HandleErrorAsync);
    }

    public ObservableCollection<TaskPlanItemViewModel> Plans { get; } = [];
    public ObservableCollection<TaskNodeItemViewModel> Nodes { get; } = [];
    public ObservableCollection<WorkspaceFileItemViewModel> Files { get; } = [];
    public ObservableCollection<WorkspaceChangeItemViewModel> Changes { get; } = [];

    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand CreatePlanCommand { get; }
    public RelayCommand ApproveCommand { get; }
    public AsyncRelayCommand RunOrResumeCommand { get; }
    public AsyncRelayCommand AcknowledgeReviewCommand { get; }
    public RelayCommand StopCommand { get; }
    public AsyncRelayCommand PreviewFileCommand { get; }
    public AsyncRelayCommand RefreshChangesCommand { get; }

    public string WorkspaceName => _workspace.Name;
    public string WorkspaceRoot => _workspace.Root;
    public string WorkspaceId => _workspace.Id;
    public string Goal { get => _goal; set { if (SetProperty(ref _goal, value)) RaiseCommands(); } }
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public string FilePreview { get => _filePreview; private set => SetProperty(ref _filePreview, value); }
    public bool IsBusy { get => _isBusy; private set { if (SetProperty(ref _isBusy, value)) RaiseCommands(); } }
    public bool IsApproved { get => _isApproved; private set { if (SetProperty(ref _isApproved, value)) RaiseCommands(); } }
    public bool TreeTruncated { get => _treeTruncated; private set => SetProperty(ref _treeTruncated, value); }
    public string ApprovalLabel => IsApproved ? "已批准本次执行" : "尚未批准执行";
    public string SelectedPatch => SelectedChange?.Patch ?? SelectedChange?.PatchUnavailableReason ?? "选择一个变更以查看真实 diff。";

    public TaskPlanItemViewModel? SelectedPlan
    {
        get => _selectedPlan;
        set
        {
            if (!SetProperty(ref _selectedPlan, value)) return;
            IsApproved = false;
            SyncNodes();
            RaiseCommands();
        }
    }

    public WorkspaceFileItemViewModel? SelectedFile
    {
        get => _selectedFile;
        set { if (SetProperty(ref _selectedFile, value)) RaiseCommands(); }
    }

    public WorkspaceChangeItemViewModel? SelectedChange
    {
        get => _selectedChange;
        set
        {
            if (!SetProperty(ref _selectedChange, value)) return;
            OnPropertyChanged(nameof(SelectedPatch));
        }
    }

    public async Task InitializeAsync(IAgentObserver observer, CancellationToken cancellationToken = default)
    {
        _observer = observer ?? throw new ArgumentNullException(nameof(observer));
        await RefreshCoreAsync(cancellationToken);
        Status = $"工作区已加载：{WorkspaceName}";
        RaiseCommands();
    }

    public void CancelActiveOperation()
    {
        _runCancellation?.Cancel();
        _activeOperationCancellation?.Cancel();
    }

    public async Task WaitForIdleAsync()
    {
        while (IsBusy)
            await Task.Delay(50).ConfigureAwait(false);
    }

    private async Task RefreshAsync()
    {
        await RunBusyAsync(RefreshCoreAsync, "任务、文件与变更已刷新");
    }

    private async Task RefreshCoreAsync(CancellationToken ct)
    {
        var selectedId = SelectedPlan?.Plan.Id;
        var plansTask = _workflow.ListAsync(ct);
        var filesTask = _explorer.ScanAsync(ct);
        await Task.WhenAll(plansTask, filesTask);
        var planItems = plansTask.Result.Select(item => new TaskPlanItemViewModel(item.Plan, item.Checkpoint)).ToArray();
        var tree = filesTask.Result;
        await _dispatcher.InvokeAsync(() =>
        {
            Replace(Plans, planItems);
            SelectedPlan = Plans.FirstOrDefault(item => item.Plan.Id == selectedId) ?? Plans.FirstOrDefault();
            Replace(Files, tree.Entries.Select(WorkspaceFileItemViewModel.From));
            TreeTruncated = tree.Truncated;
        }, ct);
        await RefreshChangesCoreAsync(ct);
    }

    private async Task CreatePlanAsync()
    {
        await RunBusyAsync(async ct =>
        {
            var settings = await _settings.LoadAsync(ct);
            var apiKey = await _secrets.GetAsync(settings.Provider.ProviderId, ct);
            var plan = await _workflow.CreatePlanAsync(Goal.Trim(), settings.Provider, apiKey, ct);
            var checkpoint = (await _workflow.ListAsync(ct)).FirstOrDefault(item => item.Plan.Id == plan.Id)?.Checkpoint;
            await _dispatcher.InvokeAsync(() =>
            {
                var item = new TaskPlanItemViewModel(plan, checkpoint);
                Plans.Insert(0, item);
                SelectedPlan = item;
            }, ct);
        }, "计划已生成，请检查节点后明确批准");
    }

    private void Approve()
    {
        IsApproved = true;
        OnPropertyChanged(nameof(ApprovalLabel));
        Status = "计划已获本次运行批准；修改选择或运行结束后批准失效";
    }

    private async Task RunOrResumeAsync()
    {
        var selected = SelectedPlan ?? throw new InvalidOperationException("请先选择计划。");
        var observer = _observer ?? throw new InvalidOperationException("任务工作区尚未初始化。");
        IsApproved = false;
        OnPropertyChanged(nameof(ApprovalLabel));
        _runCancellation = new CancellationTokenSource();
        IsBusy = true;
        Status = selected.Status == TaskGraphStatus.Interrupted ? "正在从检查点恢复…" : "正在执行任务图…";
        try
        {
            var settings = await _settings.LoadAsync(_runCancellation.Token);
            var apiKey = await _secrets.GetAsync(settings.Provider.ProviderId, _runCancellation.Token);
            var progress = new Progress<TaskGraphCheckpoint>(checkpoint =>
                _dispatcher.Post(() => ApplyCheckpoint(selected.Plan, checkpoint)));
            var checkpoint = await _workflow.RunAsync(selected.Plan, settings.Provider, apiKey, observer,
                _runCancellation.Token, progress, settings.SystemPrompt);
            ApplyCheckpoint(selected.Plan, checkpoint);
            await RefreshChangesCoreAsync(CancellationToken.None);
            Status = checkpoint.Status switch
            {
                TaskGraphStatus.Completed => "任务图已完成",
                TaskGraphStatus.NeedsReview => "发现上次未知结果；检查真实变更后再确认重试",
                TaskGraphStatus.Interrupted => "任务已停止；可重新批准后恢复",
                _ => $"任务结束：{checkpoint.Status}"
            };
        }
        catch (OperationCanceledException) { Status = "停止请求已记录，可稍后恢复"; }
        finally
        {
            _runCancellation.Dispose();
            _runCancellation = null;
            IsBusy = false;
            RaiseCommands();
        }
    }

    private async Task AcknowledgeReviewAsync()
    {
        var selected = SelectedPlan ?? throw new InvalidOperationException("请先选择计划。");
        await RunBusyAsync(async ct =>
        {
            var checkpoint = await _workflow.AcknowledgeNeedsReviewAsync(selected.Plan, ct);
            ApplyCheckpoint(selected.Plan, checkpoint);
        }, "人工检查已记录；如需重试，请再次批准计划");
    }

    private void Stop() => _runCancellation?.Cancel();

    private async Task PreviewFileAsync()
    {
        var file = SelectedFile ?? throw new InvalidOperationException("请先选择文件。");
        FilePreview = await _explorer.ReadPreviewAsync(file.RelativePath);
        Status = $"正在预览 {file.RelativePath}";
    }

    private Task RefreshChangesAsync() => RunBusyAsync(RefreshChangesCoreAsync, "真实变更已刷新");

    private async Task RefreshChangesCoreAsync(CancellationToken ct)
    {
        var snapshot = await _changes.GetChangesAsync(SelectedPlan?.Plan.Id, ct);
        await _dispatcher.InvokeAsync(() =>
        {
            Replace(Changes, snapshot.Files.Select(WorkspaceChangeItemViewModel.From));
            SelectedChange = Changes.FirstOrDefault();
            Status = snapshot.Summary + (snapshot.Truncated ? "（结果已按安全预算截断）" : "");
        }, ct);
    }

    private async Task RunBusyAsync(Func<CancellationToken, Task> action, string success)
    {
        using var operation = new CancellationTokenSource();
        _activeOperationCancellation = operation;
        IsBusy = true;
        try { await action(operation.Token); Status = success; }
        finally
        {
            if (ReferenceEquals(_activeOperationCancellation, operation))
                _activeOperationCancellation = null;
            IsBusy = false;
        }
    }

    private void ApplyCheckpoint(TaskGraphPlan plan, TaskGraphCheckpoint checkpoint)
    {
        var replacement = new TaskPlanItemViewModel(plan, checkpoint);
        var index = Plans.ToList().FindIndex(item => item.Plan.Id == plan.Id);
        if (index >= 0) Plans[index] = replacement; else Plans.Insert(0, replacement);
        SelectedPlan = replacement;
    }

    private void SyncNodes()
    {
        Nodes.Clear();
        if (SelectedPlan is null) return;
        var states = SelectedPlan.Checkpoint?.Nodes.ToDictionary(node => node.NodeId, StringComparer.OrdinalIgnoreCase);
        foreach (var node in SelectedPlan.Plan.Nodes)
        {
            TaskNodeCheckpoint? state = null;
            if (states is not null) states.TryGetValue(node.Id, out state);
            Nodes.Add(new(node, state));
        }
    }

    private Task HandleErrorAsync(Exception exception)
    {
        Status = "操作失败：" + exception.Message;
        IsBusy = false;
        return Task.CompletedTask;
    }

    private void RaiseCommands()
    {
        RefreshCommand.RaiseCanExecuteChanged();
        CreatePlanCommand.RaiseCanExecuteChanged();
        ApproveCommand.RaiseCanExecuteChanged();
        RunOrResumeCommand.RaiseCanExecuteChanged();
        AcknowledgeReviewCommand.RaiseCanExecuteChanged();
        StopCommand.RaiseCanExecuteChanged();
        PreviewFileCommand.RaiseCanExecuteChanged();
        RefreshChangesCommand.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(ApprovalLabel));
    }

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> values)
    {
        target.Clear();
        foreach (var value in values) target.Add(value);
    }
}

public sealed record TaskPlanItemViewModel(TaskGraphPlan Plan, TaskGraphCheckpoint? Checkpoint)
{
    public string Title => Plan.Goal;
    public TaskGraphStatus Status => Checkpoint?.Status ?? TaskGraphStatus.Ready;
    public string Subtitle => $"{Status} · {Plan.Nodes.Count} 节点";
}

public sealed record TaskNodeItemViewModel(TaskGraphNode Node, TaskNodeCheckpoint? Checkpoint)
{
    public string Title => Node.Title;
    public TaskNodeStatus Status => Checkpoint?.Status ?? TaskNodeStatus.Pending;
    public string Subtitle => $"{Node.Mode} · {Status}";
    public string Details => $"{Node.Instructions}\n\n依赖：{(Node.DependsOn.Count == 0 ? "无" : string.Join(", ", Node.DependsOn))}\n验收：{string.Join("；", Node.AcceptanceCriteria ?? [])}\n{Checkpoint?.Error}";
}

public sealed record WorkspaceFileItemViewModel(string RelativePath, string Name, bool IsDirectory, int Depth, long? Size)
{
    public string DisplayName => new string('　', Math.Min(Depth, 8)) + (IsDirectory ? "▸ " : "  ") + Name;
    public string SizeLabel => IsDirectory || Size is null ? "" : Size < 1024 ? $"{Size} B" : $"{Size / 1024d:N1} KB";
    public static WorkspaceFileItemViewModel From(WorkspaceEntryInfo value) =>
        new(value.RelativePath, value.Name, value.IsDirectory, value.Depth, value.Size);
}

public sealed record WorkspaceChangeItemViewModel(string RelativePath, string Status, string? Patch,
    bool PatchTruncated, string? PatchUnavailableReason)
{
    public string Title => $"{Status}  {RelativePath}";
    public static WorkspaceChangeItemViewModel From(WorkspaceChangedFile value) =>
        new(value.RelativePath, value.Status, value.Patch, value.PatchTruncated, value.PatchUnavailableReason);
}
