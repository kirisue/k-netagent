using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Input;
using KNetAgent.Desktop.WinUI.Services;
using KNetAgent.Desktop.WinUI.ViewModels;
using TestAgent.Core;
using TestAgent.Infrastructure;

namespace KNetAgent.Desktop.WinUI.Features.Operations;

public sealed class OperationsHubViewModel(
    VsCodeBridgeOperationsViewModel vsCode,
    BackgroundJobsOperationsViewModel backgroundJobs,
    CodeIterationOperationsViewModel codeIteration) : IDisposable
{
    private bool _initialized;

    public VsCodeBridgeOperationsViewModel VsCode { get; } = vsCode;
    public BackgroundJobsOperationsViewModel BackgroundJobs { get; } = backgroundJobs;
    public CodeIterationOperationsViewModel CodeIteration { get; } = codeIteration;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized)
        {
            await Task.WhenAll(
                VsCode.RefreshAsync(cancellationToken),
                BackgroundJobs.RefreshAsync(cancellationToken),
                CodeIteration.RefreshGuidesAsync(cancellationToken));
            return;
        }

        _initialized = true;
        await Task.WhenAll(
            VsCode.InitializeAsync(cancellationToken),
            BackgroundJobs.RefreshAsync(cancellationToken),
            CodeIteration.RefreshGuidesAsync(cancellationToken));
    }

    public void CancelActiveOperation() => CodeIteration.CancelActiveOperation();

    public async Task WaitForIdleAsync()
    {
        while (BackgroundJobs.Busy || CodeIteration.Busy)
            await Task.Delay(50).ConfigureAwait(false);
    }

    public void Dispose()
    {
        VsCode.Dispose();
        CodeIteration.Dispose();
    }
}

public sealed class VsCodeBridgeOperationsViewModel : ObservableObject, IDisposable
{
    private const int PairingClipboardLifetimeSeconds = 60;
    private readonly IVsCodeBridgeClient _bridge;
    private readonly IPairingClipboard _clipboard;
    private readonly IUiDispatcher _dispatcher;
    private CancellationTokenSource? _clipboardLifetime;
    private string? _copiedPayload;
    private string _status = "尚未启动";
    private string _pairingPipeName = "—";
    private string _clipboardStatus = "配对密钥只会临时复制，不会写入配置或日志。";
    private int _clipboardSecondsRemaining;
    private bool _disposed;

    public VsCodeBridgeOperationsViewModel(
        IVsCodeBridgeClient bridge,
        IPairingClipboard clipboard,
        IUiDispatcher dispatcher)
    {
        _bridge = bridge;
        _clipboard = clipboard;
        _dispatcher = dispatcher;
        _bridge.Changed += BridgeChanged;
        CopyPairingJsonCommand = new AsyncRelayCommand(
            () => CopyPairingJsonAsync(),
            () => !_disposed && !_bridge.IsConnected,
            ReportErrorAsync);
        RefreshCommand = new AsyncRelayCommand(
            () => RefreshAsync(),
            () => !_disposed,
            ReportErrorAsync);
    }

    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public string PairingPipeName { get => _pairingPipeName; private set => SetProperty(ref _pairingPipeName, value); }
    public string ClipboardStatus { get => _clipboardStatus; private set => SetProperty(ref _clipboardStatus, value); }
    public int ClipboardSecondsRemaining
    {
        get => _clipboardSecondsRemaining;
        private set
        {
            if (SetProperty(ref _clipboardSecondsRemaining, value))
                OnPropertyChanged(nameof(ClipboardCountdownLabel));
        }
    }
    public string ClipboardCountdownLabel => ClipboardSecondsRemaining > 0
        ? $"将在 {ClipboardSecondsRemaining} 秒后清理匹配的配对信息"
        : "剪贴板中没有由本页管理的配对信息";
    public ICommand CopyPairingJsonCommand { get; }
    public ICommand RefreshCommand { get; }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _bridge.StartAsync(cancellationToken);
        await RefreshAsync(cancellationToken);
    }

    public Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var info = _bridge.GetPairingInfo();
        PairingPipeName = info.PipeName;
        UpdateConnectionState();
        return Task.CompletedTask;
    }

    public async Task CopyPairingJsonAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_bridge.IsConnected)
            throw new InvalidOperationException("VS Code 已连接，不再暴露配对密钥。");

        var info = _bridge.GetPairingInfo();
        var payload = JsonSerializer.Serialize(info, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        await ClearManagedClipboardAsync(CancellationToken.None);
        await _clipboard.SetTextAsync(payload, cancellationToken);
        _copiedPayload = payload;
        ClipboardSecondsRemaining = PairingClipboardLifetimeSeconds;
        ClipboardStatus = "临时配对 JSON 已复制；连接成功或倒计时结束时会清理。";
        _clipboardLifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _ = RunClipboardCountdownAsync(payload, _clipboardLifetime.Token);
    }

    public async Task ClearPairingClipboardAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await ClearManagedClipboardAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            ClipboardStatus = "配对剪贴板清理失败：" + SafeMessage(exception);
        }
    }

    private async Task RunClipboardCountdownAsync(string payload, CancellationToken cancellationToken)
    {
        try
        {
            for (var remaining = PairingClipboardLifetimeSeconds - 1; remaining >= 0; remaining--)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
                await _dispatcher.InvokeAsync(() => ClipboardSecondsRemaining = remaining, cancellationToken);
            }

            var cleared = await _clipboard.ClearIfMatchesAsync(payload, CancellationToken.None);
            await _dispatcher.InvokeAsync(() =>
            {
                if (string.Equals(_copiedPayload, payload, StringComparison.Ordinal))
                {
                    _copiedPayload = null;
                    ClipboardStatus = cleared
                        ? "配对信息已按时从剪贴板清理。"
                        : "剪贴板内容已由用户替换，因此未执行清理。";
                }
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            await _dispatcher.InvokeAsync(() => ClipboardStatus = "剪贴板清理失败：" + SafeMessage(exception));
        }
    }

    private void BridgeChanged() => _dispatcher.Post(() =>
    {
        UpdateConnectionState();
        if (_bridge.IsConnected && _copiedPayload is not null)
            _ = ClearAfterConnectionAsync(_copiedPayload);
    });

    private async Task ClearAfterConnectionAsync(string payload)
    {
        try
        {
            _clipboardLifetime?.Cancel();
            var cleared = await _clipboard.ClearIfMatchesAsync(payload, CancellationToken.None);
            await _dispatcher.InvokeAsync(() =>
            {
                if (!string.Equals(_copiedPayload, payload, StringComparison.Ordinal))
                    return;
                _copiedPayload = null;
                ClipboardSecondsRemaining = 0;
                ClipboardStatus = cleared
                    ? "连接成功；临时配对信息已从剪贴板清理。"
                    : "连接成功；剪贴板内容已经被用户替换。";
            });
        }
        catch (Exception exception)
        {
            await _dispatcher.InvokeAsync(() => ClipboardStatus = "连接成功，但剪贴板清理失败：" + SafeMessage(exception));
        }
    }

    private void UpdateConnectionState()
    {
        Status = _bridge.IsConnected ? "已连接到受信任的本地 VS Code 工作区" : "等待本地 VS Code 配对";
        ((CommandBase)CopyPairingJsonCommand).RaiseCanExecuteChanged();
    }

    private async Task ClearManagedClipboardAsync(CancellationToken cancellationToken)
    {
        _clipboardLifetime?.Cancel();
        _clipboardLifetime?.Dispose();
        _clipboardLifetime = null;
        var payload = _copiedPayload;
        ClipboardSecondsRemaining = 0;
        if (payload is not null)
        {
            await _clipboard.ClearIfMatchesAsync(payload, cancellationToken);
            Interlocked.CompareExchange(ref _copiedPayload, null, payload);
        }
    }

    private Task ReportErrorAsync(Exception exception)
    {
        ClipboardStatus = SafeMessage(exception);
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _bridge.Changed -= BridgeChanged;
        // Host closure awaits cleanup. Dispose only cancels; it must never block the UI apartment.
        _clipboardLifetime?.Cancel();
        _clipboardLifetime?.Dispose();
        _clipboardLifetime = null;
    }

    private static string SafeMessage(Exception exception) =>
        exception.Message.Replace('\r', ' ').Replace('\n', ' ').Trim();
}

public sealed class BackgroundJobItemViewModel(BackgroundCommandJob job)
{
    public BackgroundCommandJob Job { get; } = job;
    public string Id => Job.Id;
    public string DisplayName => Job.DisplayName;
    public string State => Job.State.ToString();
    public string Timing => $"创建 {Job.CreatedAt.LocalDateTime:g}" +
        (Job.CompletedAt is null ? string.Empty : $" · 完成 {Job.CompletedAt.Value.LocalDateTime:g}");
    public string ExitCode => Job.ExitCode?.ToString() ?? "—";
    public string Error => Job.Error ?? string.Empty;
    public bool IsActive => Job.State is BackgroundCommandState.Starting or BackgroundCommandState.Running
        or BackgroundCommandState.Stopping;
}

public sealed class BackgroundJobsOperationsViewModel : ObservableObject
{
    private readonly IBackgroundCommandService _commands;
    private readonly IToolExecutionService _toolExecution;
    private readonly IUserInteractionService _interaction;
    private readonly WorkspaceLocator _workspace;
    private BackgroundJobItemViewModel? _selectedJob;
    private string _output = string.Empty;
    private string _status = "尚未刷新";
    private long _outputCursor;
    private bool _stopApproved;
    private bool _busy;

    public BackgroundJobsOperationsViewModel(
        IBackgroundCommandService commands,
        IToolExecutionService toolExecution,
        IUserInteractionService interaction,
        WorkspaceLocator workspace)
    {
        _commands = commands;
        _toolExecution = toolExecution;
        _interaction = interaction;
        _workspace = workspace;
        RefreshCommand = new AsyncRelayCommand(() => RefreshAsync(), () => !Busy, ReportErrorAsync);
        ReadMoreOutputCommand = new AsyncRelayCommand(
            () => ReadMoreOutputAsync(),
            () => !Busy && SelectedJob is not null,
            ReportErrorAsync);
        StopCommand = new AsyncRelayCommand(
            StopSelectedAsync,
            () => !Busy && StopApproved && SelectedJob?.IsActive == true,
            ReportErrorAsync);
    }

    public ObservableCollection<BackgroundJobItemViewModel> Jobs { get; } = [];
    public BackgroundJobItemViewModel? SelectedJob
    {
        get => _selectedJob;
        set
        {
            if (!SetProperty(ref _selectedJob, value))
                return;
            Output = string.Empty;
            OutputCursor = 0;
            StopApproved = false;
            RaiseCommands();
        }
    }
    public string Output { get => _output; private set => SetProperty(ref _output, value); }
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public long OutputCursor
    {
        get => _outputCursor;
        private set
        {
            if (SetProperty(ref _outputCursor, value))
                OnPropertyChanged(nameof(OutputCursorLabel));
        }
    }
    public string OutputCursorLabel => $"输出 cursor: {OutputCursor:N0}";
    public bool StopApproved
    {
        get => _stopApproved;
        set
        {
            if (SetProperty(ref _stopApproved, value))
                RaiseCommands();
        }
    }
    public bool Busy
    {
        get => _busy;
        private set
        {
            if (SetProperty(ref _busy, value))
                RaiseCommands();
        }
    }
    public ICommand RefreshCommand { get; }
    public ICommand ReadMoreOutputCommand { get; }
    public ICommand StopCommand { get; }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        Busy = true;
        try
        {
            var selectedId = SelectedJob?.Id;
            var selectedOutput = Output;
            var selectedCursor = OutputCursor;
            await _commands.ReconcileAsync(cancellationToken);
            var jobs = (await _commands.ListAsync(cancellationToken: cancellationToken))
                .Where(job => IsCurrentWorkspace(job.WorkingDirectory))
                .ToArray();
            Jobs.Clear();
            foreach (var job in jobs.OrderByDescending(item => item.CreatedAt))
                Jobs.Add(new BackgroundJobItemViewModel(job));
            SelectedJob = Jobs.FirstOrDefault(item => item.Id == selectedId) ?? Jobs.FirstOrDefault();
            if (SelectedJob?.Id == selectedId)
            {
                Output = selectedOutput;
                OutputCursor = selectedCursor;
            }
            Status = jobs.Length == 0 ? "当前没有后台任务。" : $"已刷新 {jobs.Length} 个后台任务。";
        }
        finally { Busy = false; }
    }

    public async Task ReadMoreOutputAsync(CancellationToken cancellationToken = default)
    {
        var selected = SelectedJob ?? throw new InvalidOperationException("请先选择后台任务。");
        Busy = true;
        try
        {
            var chunk = await _commands.ReadOutputAsync(selected.Id, OutputCursor, 64 * 1024, cancellationToken);
            if (!string.IsNullOrEmpty(chunk.Content))
                Output += chunk.Content;
            OutputCursor = chunk.NextCursor;
            Status = chunk.Truncated
                ? "输出已达到后台任务的保留上限。"
                : chunk.IsComplete ? "已读取至任务输出末尾。" : "已读取最新增量输出。";
        }
        finally { Busy = false; }
    }

    private async Task StopSelectedAsync()
    {
        var selected = SelectedJob ?? throw new InvalidOperationException("请先选择后台任务。");
        if (!StopApproved)
            throw new InvalidOperationException("必须先勾选停止审批。" );
        Busy = true;
        try
        {
            var request = new ToolRequest(
                $"UI-STOP-{Guid.NewGuid():N}",
                "stop_background_command",
                JsonSerializer.Serialize(new { jobId = selected.Id }),
                selected.Job.ScopeId);
            var result = await _toolExecution.ExecuteAsync(
                request,
                new InteractiveApprovalObserver(_interaction),
                CancellationToken.None);
            StopApproved = false;
            Status = result.Status == ToolExecutionStatus.Success
                ? result.Summary ?? "后台任务停止请求已完成。"
                : result.Error ?? result.Summary ?? "后台任务停止请求未执行。";
            await RefreshAsync();
        }
        finally { Busy = false; }
    }

    private Task ReportErrorAsync(Exception exception)
    {
        Status = exception.Message;
        return Task.CompletedTask;
    }

    private void RaiseCommands()
    {
        ((CommandBase)RefreshCommand).RaiseCanExecuteChanged();
        ((CommandBase)ReadMoreOutputCommand).RaiseCanExecuteChanged();
        ((CommandBase)StopCommand).RaiseCanExecuteChanged();
    }

    private bool IsCurrentWorkspace(string workingDirectory)
    {
        try
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_workspace.Root));
            var candidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workingDirectory));
            return candidate.Equals(root, StringComparison.OrdinalIgnoreCase) ||
                   candidate.StartsWith(root + Path.DirectorySeparatorChar,
                       StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private sealed class InteractiveApprovalObserver(IUserInteractionService interaction) : IAgentObserver
    {
        public ValueTask OnStateAsync(AgentState state) => ValueTask.CompletedTask;
        public ValueTask OnEventAsync(StreamEvent value) => ValueTask.CompletedTask;
        public async ValueTask<bool> RequestToolApprovalAsync(
            ToolApprovalRequest request,
            CancellationToken ct) => await interaction.RequestToolApprovalAsync(request, ct);
    }
}

public sealed class CodeIterationOperationsViewModel : ObservableObject, IDisposable
{
    private readonly IIterationGuideStore _guides;
    private readonly ICodeIterationService _iterations;
    private readonly ISettingsStore _settings;
    private readonly ISecureSecretStore _secrets;
    private readonly SelfIterationWorkspace _selfWorkspace;
    private CancellationTokenSource? _activeOperation;
    private IterationGuide? _selectedGuide;
    private IterationProposal? _proposal;
    private string? _approvalFingerprint;
    private string _diffPreview = "选择指引后生成提案；这里会显示完整的旧内容与新内容。";
    private string _validationOutput = "尚未运行验证。";
    private string _result = "不会自动生成、验证或应用任何变更。";
    private bool _applyApproved;
    private bool _busy;

    public CodeIterationOperationsViewModel(
        IIterationGuideStore guides,
        ICodeIterationService iterations,
        ISettingsStore settings,
        ISecureSecretStore secrets,
        SelfIterationWorkspace selfWorkspace)
    {
        _guides = guides;
        _iterations = iterations;
        _settings = settings;
        _secrets = secrets;
        _selfWorkspace = selfWorkspace;
        RefreshGuidesCommand = new AsyncRelayCommand(() => RefreshGuidesAsync(), () => !Busy, ReportErrorAsync);
        GenerateAndValidateCommand = new AsyncRelayCommand(
            GenerateAndValidateAsync,
            () => _selfWorkspace.CanExecuteGeneratedCode && !Busy && SelectedGuide is not null,
            ReportErrorAsync);
        ApplyCommand = new AsyncRelayCommand(
            ApplyAsync,
            CanApply,
            ReportErrorAsync);
        CancelCommand = new RelayCommand(CancelActiveOperation, () => Busy);
    }

    public ObservableCollection<IterationGuide> Guides { get; } = [];
    public string SelfWorkspaceLabel => _selfWorkspace.DisplayLabel;
    public bool SelfIterationAvailable => _selfWorkspace.CanExecuteGeneratedCode;
    public IterationGuide? SelectedGuide
    {
        get => _selectedGuide;
        set
        {
            if (!SetProperty(ref _selectedGuide, value))
                return;
            ClearProposal("指引已切换；请显式生成并验证新提案。");
            RaiseCommands();
        }
    }
    public IterationProposal? Proposal
    {
        get => _proposal;
        private set
        {
            if (SetProperty(ref _proposal, value))
            {
                OnPropertyChanged(nameof(ProposalSummary));
                OnPropertyChanged(nameof(ProposalFingerprintLabel));
                RaiseCommands();
            }
        }
    }
    public string ProposalSummary => Proposal is null
        ? "尚无提案"
        : $"{Proposal.Title} · {Proposal.Changes.Count} 个文件 · {Proposal.Id}";
    public string ProposalFingerprintLabel => Proposal is null
        ? "提案 hash：—"
        : "提案 hash：" + ComputeProposalFingerprint(Proposal)[..16];
    public string DiffPreview { get => _diffPreview; private set => SetProperty(ref _diffPreview, value); }
    public string ValidationOutput { get => _validationOutput; private set => SetProperty(ref _validationOutput, value); }
    public string Result { get => _result; private set => SetProperty(ref _result, value); }
    public bool ApplyApproved
    {
        get => _applyApproved;
        set
        {
            if (!SetProperty(ref _applyApproved, value))
                return;
            _approvalFingerprint = value && Proposal is not null ? ComputeProposalFingerprint(Proposal) : null;
            RaiseCommands();
        }
    }
    public bool Busy
    {
        get => _busy;
        private set
        {
            if (SetProperty(ref _busy, value))
                RaiseCommands();
        }
    }
    public ICommand RefreshGuidesCommand { get; }
    public ICommand GenerateAndValidateCommand { get; }
    public ICommand ApplyCommand { get; }
    public ICommand CancelCommand { get; }

    public async Task RefreshGuidesAsync(CancellationToken cancellationToken = default)
    {
        Busy = true;
        try
        {
            var selectedId = SelectedGuide?.Id;
            var guides = await _guides.ListAsync(cancellationToken);
            Guides.Clear();
            foreach (var guide in guides)
                Guides.Add(guide);
            SelectedGuide = Guides.FirstOrDefault(item => item.Id == selectedId) ?? Guides.FirstOrDefault();
            Result = !_selfWorkspace.CanExecuteGeneratedCode
                ? _selfWorkspace.ExecutionDisabledReason
                : guides.Count == 0
                ? "没有找到 iteration-guides/*.md 指引。"
                : $"已载入 {guides.Count} 个受控迭代指引；不会自动运行。";
        }
        finally { Busy = false; }
    }

    private async Task GenerateAndValidateAsync()
    {
        if (!_selfWorkspace.CanExecuteGeneratedCode)
            throw new InvalidOperationException(_selfWorkspace.ExecutionDisabledReason);
        var guide = SelectedGuide ?? throw new InvalidOperationException("请先选择迭代指引。");
        var operationToken = ReplaceOperationCancellation();
        Busy = true;
        ApplyApproved = false;
        Result = "正在生成白名单内的提案，并在临时副本中执行构建与测试…";
        try
        {
            var settings = await _settings.LoadAsync(operationToken);
            var apiKey = await _secrets.GetAsync(settings.Provider.ProviderId, operationToken);
            var proposal = await _iterations.GenerateAsync(
                guide, settings.Provider, apiKey, operationToken);
            Proposal = proposal;
            DiffPreview = BuildCompleteDiff(proposal);
            ValidationOutput = BuildValidationOutput(proposal.Validation);
            Result = proposal.Validation.Success
                ? "提案验证通过。检查完整 Diff 后，勾选批准才能应用。"
                : "提案验证失败，Apply 已被阻止。";
        }
        catch (OperationCanceledException)
        {
            Result = "提案生成或验证已取消；没有修改工作区。";
        }
        finally { Busy = false; }
    }

    private async Task ApplyAsync()
    {
        var proposal = Proposal ?? throw new InvalidOperationException("没有可应用的提案。");
        var fingerprint = ComputeProposalFingerprint(proposal);
        if (!ApplyApproved || !string.Equals(_approvalFingerprint, fingerprint, StringComparison.Ordinal))
            throw new InvalidOperationException("审批已失效；请重新检查 Diff 并勾选批准。" );
        if (!proposal.Validation.Success)
            throw new InvalidOperationException("验证失败的提案不能应用。" );

        var operationToken = ReplaceOperationCancellation();
        Busy = true;
        try
        {
            // The infrastructure service independently checks each original file SHA-256
            // immediately before writing and rolls back if workspace validation fails.
            var result = await _iterations.ApplyAsync(proposal, operationToken);
            ValidationOutput = result.ValidationOutput;
            Result = result.Message + (result.RolledBack ? "（已回滚）" : string.Empty);
            ApplyApproved = false;
        }
        catch (OperationCanceledException)
        {
            Result = "Apply 已取消；请检查验证结果后再决定是否重新生成。";
            ApplyApproved = false;
        }
        finally { Busy = false; }
    }

    public void CancelActiveOperation() => _activeOperation?.Cancel();

    private bool CanApply() => _selfWorkspace.CanExecuteGeneratedCode && !Busy && Proposal?.Validation.Success == true && ApplyApproved &&
        string.Equals(_approvalFingerprint, ComputeProposalFingerprint(Proposal), StringComparison.Ordinal);

    private CancellationToken ReplaceOperationCancellation()
    {
        _activeOperation?.Cancel();
        _activeOperation?.Dispose();
        _activeOperation = new CancellationTokenSource();
        return _activeOperation.Token;
    }

    private void ClearProposal(string result)
    {
        Proposal = null;
        DiffPreview = "尚无提案。";
        ValidationOutput = "尚未运行验证。";
        ApplyApproved = false;
        Result = result;
    }

    public static string BuildCompleteDiff(IterationProposal proposal)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        var builder = new StringBuilder();
        foreach (var change in proposal.Changes)
        {
            builder.AppendLine($"--- a/{change.Path}");
            builder.AppendLine($"+++ b/{change.Path}");
            builder.AppendLine("@@ complete old/new content @@");
            AppendPrefixedLines(builder, '-', change.OriginalContent);
            AppendPrefixedLines(builder, '+', change.NewContent);
            builder.AppendLine();
        }
        return builder.ToString();
    }

    public static string ComputeProposalFingerprint(IterationProposal proposal)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        var builder = new StringBuilder(proposal.Id).Append('\n').Append(proposal.GuideId).Append('\n');
        foreach (var change in proposal.Changes.OrderBy(item => item.Path, StringComparer.Ordinal))
            builder.Append(change.Path).Append('\0').Append(change.OriginalSha256).Append('\0')
                .Append(change.NewContent).Append('\0');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    private static void AppendPrefixedLines(StringBuilder builder, char prefix, string content)
    {
        using var reader = new StringReader(content);
        string? line;
        while ((line = reader.ReadLine()) is not null)
            builder.Append(prefix).AppendLine(line);
        if (content.Length == 0)
            builder.Append(prefix).AppendLine("<empty>");
    }

    private static string BuildValidationOutput(IterationValidation validation) =>
        $"验证：{(validation.Success ? "通过" : "失败")} · {validation.CompletedAt.LocalDateTime:g}\n\n" +
        "BUILD\n" + validation.BuildOutput + "\n\nTEST\n" + validation.TestOutput;

    private Task ReportErrorAsync(Exception exception)
    {
        Result = exception.Message;
        Busy = false;
        return Task.CompletedTask;
    }

    private void RaiseCommands()
    {
        ((CommandBase)RefreshGuidesCommand).RaiseCanExecuteChanged();
        ((CommandBase)GenerateAndValidateCommand).RaiseCanExecuteChanged();
        ((CommandBase)ApplyCommand).RaiseCanExecuteChanged();
        ((CommandBase)CancelCommand).RaiseCanExecuteChanged();
    }

    public void Dispose()
    {
        _activeOperation?.Cancel();
        _activeOperation?.Dispose();
        _activeOperation = null;
    }
}
