using System.Collections.ObjectModel;
using KNetAgent.Desktop.WinUI.Services;
using KNetAgent.Desktop.WinUI.ViewModels;
using TestAgent.Core;

namespace KNetAgent.Desktop.WinUI.Features.WindowsServices;

public sealed record ServiceCenterRow(WindowsServiceItem Service)
{
    public string Heading => $"{Service.DisplayName} · {StateLabel}";
    public string Name => Service.Name;
    public string StateLabel => Service.Status switch
    {
        "Running" => "运行中", "Stopped" => "已停止", "StartPending" => "正在启动", "StopPending" => "正在停止",
        "Paused" => "已暂停", "PausePending" => "正在暂停", "ContinuePending" => "正在恢复", _ => "未知"
    };
    public string StartLabel => Service.StartType switch
    { "Automatic" => "自动", "Manual" => "手动", "Disabled" => "已禁用", "Boot" => "引导", "System" => "系统", _ => "不可读取" };
    public string Detail => $"服务名：{Service.Name}\n显示名：{Service.DisplayName}\n状态：{StateLabel}\n启动类型：{StartLabel}\n" +
        $"依赖项（最多 32 项）：{(Service.Dependencies.Count == 0 ? "无或不可读取" : string.Join(", ", Service.Dependencies))}\n" +
        (Service.DetailsUnavailable ? "部分配置无读取权限或已发生变化。\n" : "") +
        "\n这是查询时的状态快照。已停止或手动启动并不代表服务故障。";
}

public sealed class ServiceCenterViewModel : ObservableObject
{
    private readonly IWindowsServiceReader _reader;
    private readonly IWindowsServiceEvidenceBuilder _evidence;
    private readonly IUiDispatcher _dispatcher;
    private CancellationTokenSource _lifetime = new();
    private Task _activeOperation = Task.CompletedTask;
    private Func<string, Task>? _stageEvidence;
    private bool _closed = true;
    private bool _isBusy;
    private string _name = "", _filter = "", _maxServices = "50";
    private string _status = "点击查询后读取本机服务。打开本页不会自动读取。";
    private string _evidencePreview = "";
    private ServiceCenterRow? _selectedService;
    private DateTimeOffset? _queriedAt;

    public ServiceCenterViewModel(IWindowsServiceReader reader, IWindowsServiceEvidenceBuilder evidence, IUiDispatcher dispatcher)
    {
        _reader = reader;
        _evidence = evidence;
        _dispatcher = dispatcher;
        QueryCommand = new AsyncRelayCommand(QueryAsync, () => CanQuery);
        StageEvidenceCommand = new AsyncRelayCommand(StageEvidenceAsync, () => CanStage,
            _ => _dispatcher.InvokeAsync(() => Status = "无法加入聊天草稿，请返回聊天检查当前任务状态。"));
    }

    public ObservableCollection<ServiceCenterRow> Services { get; } = [];
    public string Name { get => _name; set => SetProperty(ref _name, value); }
    public string Filter { get => _filter; set => SetProperty(ref _filter, value); }
    public string MaxServices { get => _maxServices; set => SetProperty(ref _maxServices, value); }
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public bool IsBusy { get => _isBusy; private set { if (SetProperty(ref _isBusy, value)) RefreshCommands(); } }
    public bool CanQuery => !_closed && !IsBusy;
    public bool CanStage => CanQuery && SelectedService is not null && Services.Contains(SelectedService) &&
        _queriedAt.HasValue && !string.IsNullOrEmpty(_evidencePreview) && _stageEvidence is not null;
    public ServiceCenterRow? SelectedService
    {
        get => _selectedService;
        set
        {
            if (!SetProperty(ref _selectedService, value)) return;
            _evidencePreview = value is not null && Services.Contains(value) && _queriedAt.HasValue
                ? _evidence.Build(value.Service, _queriedAt.Value) : "";
            OnPropertyChanged(nameof(SelectedDetail));
            OnPropertyChanged(nameof(EvidencePreview));
            RefreshCommands();
        }
    }
    public string SelectedDetail => SelectedService?.Detail ?? "选择服务查看状态、启动类型和依赖。查询结果保留在本地，点击加入草稿后可在聊天中确认发送。";
    public string EvidencePreview => string.IsNullOrEmpty(_evidencePreview)
        ? "选择一项服务后，在这里显示准备加入聊天的脱敏证据。" : _evidencePreview;
    public AsyncRelayCommand QueryCommand { get; }
    public AsyncRelayCommand StageEvidenceCommand { get; }

    public void Open(Func<string, Task> stageEvidence)
    {
        if (!_closed) throw new InvalidOperationException("服务中心已打开。");
        _lifetime.Dispose();
        _lifetime = new CancellationTokenSource();
        _stageEvidence = stageEvidence;
        _closed = false;
        Services.Clear();
        SelectedService = null;
        _queriedAt = null;
        Status = "点击查询后读取本机服务。打开本页不会自动读取。";
        RefreshCommands();
    }

    public Task QueryAsync()
    {
        if (!CanQuery) return Task.CompletedTask;
        if (!int.TryParse(MaxServices, out var limit) || limit is < 1 or > 100)
        { Status = "结果上限必须是 1–100 的整数。"; return Task.CompletedTask; }
        if (!string.IsNullOrWhiteSpace(Name) && !string.IsNullOrWhiteSpace(Filter))
        { Status = "请使用精确服务名或模糊筛选其中一项。"; return Task.CompletedTask; }
        var query = new WindowsServiceQuery(string.IsNullOrWhiteSpace(Name) ? null : Name.Trim(),
            string.IsNullOrWhiteSpace(Filter) ? null : Filter.Trim(), limit);
        _activeOperation = QueryCoreAsync(query, _lifetime.Token);
        return _activeOperation;
    }

    private async Task QueryCoreAsync(WindowsServiceQuery query, CancellationToken ct)
    {
        IsBusy = true;
        SelectedService = null;
        Services.Clear();
        Status = "正在查询本机服务状态…";
        try
        {
            var result = await _reader.QueryAsync(query, ct).ConfigureAwait(false);
            await _dispatcher.InvokeAsync(() =>
            {
                foreach (var item in result.Services.Take(query.MaxServices)) Services.Add(new(item));
                _queriedAt = result.QueriedAt;
                Status = $"{result.QueriedAt.ToLocalTime():HH:mm:ss} 查询到 {Services.Count} 项。" +
                    (result.Truncated ? "达到结果上限，请缩小筛选范围。" : "") +
                    (Services.Any(item => item.Service.DetailsUnavailable) ? "部分服务配置不可读取。" : "");
            }, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception exception)
        {
            if (!ct.IsCancellationRequested)
                await _dispatcher.InvokeAsync(() => Status = exception switch
                {
                    ArgumentException => "筛选条件无效，请检查名称和结果上限。",
                    UnauthorizedAccessException => "Windows 未允许读取服务。应用不会自动提权。",
                    TimeoutException => "服务查询超过时间预算，请缩小筛选范围。",
                    _ => "无法读取本机服务状态，可稍后重新查询。"
                }, ct);
        }
        finally { await _dispatcher.InvokeAsync(() => IsBusy = false); }
    }

    private async Task StageEvidenceAsync()
    {
        if (!CanStage) return;
        await _stageEvidence!(EvidencePreview);
    }

    public async Task CloseAsync()
    {
        _closed = true;
        _lifetime.Cancel();
        _stageEvidence = null;
        RefreshCommands();
        try { await _activeOperation; } catch (OperationCanceledException) { }
        Services.Clear();
        SelectedService = null;
    }

    private void RefreshCommands()
    {
        OnPropertyChanged(nameof(CanQuery)); OnPropertyChanged(nameof(CanStage));
        QueryCommand.RaiseCanExecuteChanged(); StageEvidenceCommand.RaiseCanExecuteChanged();
    }
}
