using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Text.RegularExpressions;
using KNetAgent.Desktop.WinUI.Services;
using KNetAgent.Desktop.WinUI.ViewModels;
using TestAgent.Core;

namespace KNetAgent.Desktop.WinUI.Features.WindowsEvents;

public sealed record EventLevelFilter(string Label, int? MaximumLevel);

public sealed class EventCenterRow(WindowsEventItem item) : ObservableObject
{
    private bool _isSelected;
    public WindowsEventItem Event { get; } = item;
    public bool IsSelected { get => _isSelected; set => SetProperty(ref _isSelected, value); }
    public string Heading => $"{LevelLabel} · {Event.Provider} · ID {Event.EventId}";
    public string TimeLabel => $"{Event.Timestamp?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "时间不可用"} · {Event.Channel} · #{Event.RecordId}";
    public string LevelLabel => Event.Level switch { 1 => "严重", 2 => "错误", 3 => "警告", 4 => "信息", 5 => "详细", _ => "其他" };
    public string Detail => $"{Heading}\n{TimeLabel}\n\n{Event.Message}";
}

/// <summary>
/// Local, bounded event browsing. Only an explicit stage action can copy a selected,
/// sanitized evidence preview to the host's draft; no model or network dependency exists.
/// </summary>
public sealed class EventCenterViewModel : ObservableObject
{
    private readonly IWindowsEventReader _reader;
    private readonly IWindowsEventWatchService _watch;
    private readonly IWindowsEventEvidenceBuilder _evidence;
    private readonly IUiDispatcher _dispatcher;
    private readonly IWindowsIncidentCorrelator? _correlator;
    private WindowsIncident? _selectedIncident;
    private string _incidentStatus = "查询事件后，可在本地整理重复事件和时间邻近的故障线索。";
    private CancellationTokenSource _lifetime = new();
    private Task _activeOperation = Task.CompletedTask;
    private Task _refreshTask = Task.CompletedTask;
    private Func<string, Task>? _stageEvidence;
    private bool _closed = true;
    private bool _subscribed;
    private int _refreshScheduled;
    private int _selectionRevision;
    private int _previewRevision = -1;
    private bool _isBusy;
    private bool _isWatching;
    private bool _displayWatchEvents;
    private string _channel = "Application";
    private string _lookbackMinutesText = "60";
    private string _maxEventsText = "100";
    private string _eventIdText = "";
    private string _provider = "";
    private string _status = "选择频道和筛选条件，再点击查询。打开本页不会读取系统日志。";
    private string _watchStatus = "监控未启动。仅在本窗口打开期间监听。";
    private string _evidencePreview = "";
    private string _evidenceStatus = "勾选事件后生成预览；最多 20 条、16,000 字符。";
    private EventCenterRow? _selectedEvent;
    private EventLevelFilter _selectedLevel;

    public EventCenterViewModel(IWindowsEventReader reader, IWindowsEventWatchService watch,
        IWindowsEventEvidenceBuilder evidence, IUiDispatcher dispatcher,
        IWindowsIncidentCorrelator? correlator = null)
    {
        _reader = reader;
        _watch = watch;
        _evidence = evidence;
        _dispatcher = dispatcher;
        _correlator = correlator;
        _selectedLevel = Levels[0];
        QueryCommand = new AsyncRelayCommand(QueryAsync, () => CanQuery, HandleUnexpectedAsync);
        StartWatchCommand = new AsyncRelayCommand(() => StartWatchAsync(false), () => CanQuery, HandleUnexpectedAsync);
        ResumeWatchCommand = new AsyncRelayCommand(() => StartWatchAsync(true), () => CanQuery, HandleUnexpectedAsync);
        StopWatchCommand = new AsyncRelayCommand(StopWatchAsync, () => CanStop, HandleUnexpectedAsync);
        PreviewEvidenceCommand = new RelayCommand(BuildEvidencePreview, () => !IsBusy && !_closed && Events.Any(x => x.IsSelected));
        StageEvidenceCommand = new AsyncRelayCommand(StageEvidenceAsync, () => CanStage, HandleUnexpectedAsync);
        BuildIncidentsCommand = new RelayCommand(BuildIncidents,
            () => !_closed && !IsBusy && _correlator is not null && Events.Count > 0);
        SelectIncidentEvidenceCommand = new RelayCommand(SelectIncidentEvidence,
            () => !_closed && !IsBusy && SelectedIncident is not null);
    }

    public IReadOnlyList<string> Channels { get; } = ["Application", "System"];
    public IReadOnlyList<EventLevelFilter> Levels { get; } =
    [new("警告及以上", 3), new("错误及以上", 2), new("仅严重", 1), new("信息及以上", 4), new("所有级别", null)];
    public ObservableCollection<EventCenterRow> Events { get; } = [];
    public ObservableCollection<WindowsIncident> Incidents { get; } = [];
    public WindowsIncident? SelectedIncident
    {
        get => _selectedIncident;
        set
        {
            if (!SetProperty(ref _selectedIncident, value)) return;
            OnPropertyChanged(nameof(IncidentDetail));
            RefreshCommands();
        }
    }
    public string IncidentStatus { get => _incidentStatus; private set => SetProperty(ref _incidentStatus, value); }
    public string IncidentDetail => SelectedIncident is not { } item ? "选择一组查看事实、假设与建议。" :
        $"{item.Title}\n{item.Id} · {item.ConfidenceLabel}\n{item.From?.ToLocalTime():g} — {item.To?.ToLocalTime():g}\n" +
        $"引用事件：{item.Evidence.Count} 条\n\n观察事实\n{string.Join("\n", item.Facts.Select(x => "• " + x))}\n\n" +
        $"待验证假设\n{string.Join("\n", item.Hypotheses.Select(x => "• " + x))}\n\n" +
        $"下一步只读检查\n{string.Join("\n", item.ReadOnlyNextSteps.Select(x => "• " + x))}";
    public string Channel { get => _channel; set => SetProperty(ref _channel, value); }
    public string LookbackMinutesText { get => _lookbackMinutesText; set => SetProperty(ref _lookbackMinutesText, value); }
    public string MaxEventsText { get => _maxEventsText; set => SetProperty(ref _maxEventsText, value); }
    public string EventIdText { get => _eventIdText; set => SetProperty(ref _eventIdText, value); }
    public string Provider { get => _provider; set => SetProperty(ref _provider, value); }
    public EventLevelFilter SelectedLevel { get => _selectedLevel; set => SetProperty(ref _selectedLevel, value); }
    public EventCenterRow? SelectedEvent
    {
        get => _selectedEvent;
        set { if (SetProperty(ref _selectedEvent, value)) OnPropertyChanged(nameof(SelectedDetail)); }
    }
    public string SelectedDetail => SelectedEvent?.Detail ?? "点击一条事件查看已脱敏详情；勾选复选框才会加入证据。";
    public bool IsBusy { get => _isBusy; private set { if (SetProperty(ref _isBusy, value)) RefreshCommands(); } }
    public bool IsWatching { get => _isWatching; private set { if (SetProperty(ref _isWatching, value)) RefreshCommands(); } }
    public bool CanQuery => !_closed && !IsBusy && !IsWatching;
    public bool CanStop => !_closed && !IsBusy && IsWatching;
    public bool CanStage => !_closed && !IsBusy && _stageEvidence is not null &&
        _previewRevision == _selectionRevision && !string.IsNullOrWhiteSpace(EvidencePreview);
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public string WatchStatus { get => _watchStatus; private set => SetProperty(ref _watchStatus, value); }
    public string EvidencePreview { get => _evidencePreview; private set => SetProperty(ref _evidencePreview, value); }
    public string EvidenceStatus { get => _evidenceStatus; private set => SetProperty(ref _evidenceStatus, value); }
    public string SelectionLabel => $"当前 {Events.Count} 条 · 已勾选 {Events.Count(x => x.IsSelected)} 条";
    public AsyncRelayCommand QueryCommand { get; }
    public AsyncRelayCommand StartWatchCommand { get; }
    public AsyncRelayCommand ResumeWatchCommand { get; }
    public AsyncRelayCommand StopWatchCommand { get; }
    public RelayCommand PreviewEvidenceCommand { get; }
    public AsyncRelayCommand StageEvidenceCommand { get; }
    public RelayCommand BuildIncidentsCommand { get; }
    public RelayCommand SelectIncidentEvidenceCommand { get; }

    public void BuildIncidents()
    {
        if (_closed || IsBusy || _correlator is null) return;
        try
        {
            var result = _correlator.Build(Events.Select(row => row.Event).ToArray());
            Incidents.Clear();
            foreach (var incident in result) Incidents.Add(incident);
            SelectedIncident = Incidents.FirstOrDefault();
            IncidentStatus = $"已从当前 {Events.Count} 条事件整理出 {Incidents.Count} 组本地线索。时间邻近不证明因果或同一进程。";
        }
        catch
        {
            Incidents.Clear(); SelectedIncident = null;
            IncidentStatus = "无法整理当前事件，请重新查询后重试。";
        }
        RefreshCommands();
    }

    public void SelectIncidentEvidence()
    {
        if (_closed || IsBusy || SelectedIncident is not { } incident) return;
        var keys = incident.Evidence.OrderByDescending(item => item.Timestamp).Take(20)
            .Select(item => (item.Channel, item.RecordId, item.Timestamp)).ToHashSet();
        foreach (var row in Events)
            row.IsSelected = keys.Contains((row.Event.Channel, row.Event.RecordId, row.Event.Timestamp));
        IncidentStatus = $"已选择本组 {Events.Count(row => row.IsSelected)} 条事件" +
            (incident.Evidence.Count > 20 ? "（单次证据上限 20 条，选取最新记录）" : "") +
            "。请先预览证据，再加入聊天草稿。";
    }

    public void Open(Func<string, Task> stageEvidence)
    {
        ArgumentNullException.ThrowIfNull(stageEvidence);
        if (!_closed) throw new InvalidOperationException("事件中心已经打开。");
        _lifetime.Dispose();
        _lifetime = new CancellationTokenSource();
        _closed = false;
        _stageEvidence = stageEvidence;
        _displayWatchEvents = false;
        ReplaceEvents([]);
        Status = "选择频道和筛选条件，再点击查询。打开本页不会读取系统日志。";
        WatchStatus = "监控未启动。仅在本窗口打开期间监听。";
        _watch.Changed += OnWatchChanged;
        _subscribed = true;
        InvalidatePreview();
        RefreshCommands();
    }

    public Task QueryAsync()
    {
        if (!CanQuery || !TryCreateQuery(out var query)) return Task.CompletedTask;
        _displayWatchEvents = false;
        return RunOperationAsync(async ct =>
        {
            await _dispatcher.InvokeAsync(() => { ReplaceEvents([]); Status = "正在查询本机事件…"; }, ct);
            var result = await _reader.QueryAsync(query!, ct);
            await _dispatcher.InvokeAsync(() =>
            {
                ReplaceEvents(result.Events.Take(query!.MaxEvents));
                Status = $"已读取 {Events.Count} 条本地事件。" + (result.Truncated ? "结果已达上限，可缩小时间范围。" : "") +
                    (string.IsNullOrWhiteSpace(result.Notice) ? "" : $" {result.Notice}");
            }, ct);
        }, "无法查询事件。请检查频道权限或筛选条件后重试。");
    }

    public Task StartWatchAsync(bool resumeBookmark)
    {
        if (!CanQuery || !TryCreateQuery(out var query)) return Task.CompletedTask;
        _displayWatchEvents = true;
        return RunOperationAsync(async ct =>
        {
            await _watch.StartAsync(query!, resumeBookmark, ct);
            await _dispatcher.InvokeAsync(() => ApplyWatchSnapshot(_watch.Snapshot), ct);
        }, "无法启动监控。请检查频道权限后重试。");
    }

    public Task StopWatchAsync()
    {
        if (_closed || IsBusy) return Task.CompletedTask;
        return RunOperationAsync(async ct =>
        {
            await _watch.StopAsync(ct);
            await _dispatcher.InvokeAsync(() => ApplyWatchSnapshot(_watch.Snapshot), ct);
        }, "停止监控时发生错误。关闭窗口将再次清理监控。");
    }

    public void BuildEvidencePreview()
    {
        if (_closed || IsBusy) return;
        InvalidatePreview();
        var selected = Events.Where(x => x.IsSelected).Select(x => x.Event).ToArray();
        if (selected.Length is < 1 or > 20)
        {
            EvidenceStatus = "请勾选 1 至 20 条事件后生成证据预览。";
            return;
        }
        try
        {
            var evidence = _evidence.Build(selected);
            if (string.IsNullOrWhiteSpace(evidence.Text))
            {
                EvidenceStatus = "所选事件没有可用的证据正文。";
                return;
            }
            EvidencePreview = evidence.Text.Length > 16_000 ? evidence.Text[..16_000] : evidence.Text;
            _previewRevision = _selectionRevision;
            EvidenceStatus = $"预览包含所选事件中的 {evidence.EventCount} 条（已勾选 {selected.Length} 条）。" +
                (evidence.Truncated || evidence.Text.Length > 16_000 ? "已按字符预算截断。" : "") +
                " 点击加入聊天草稿后，仍由你检查并手动发送。";
            RefreshCommands();
        }
        catch
        {
            EvidenceStatus = "无法生成证据预览，请重新选择事件后重试。";
        }
    }

    public Task StageEvidenceAsync()
    {
        if (!CanStage) return Task.CompletedTask;
        var preview = EvidencePreview;
        var stage = _stageEvidence!;
        return RunOperationAsync(async ct =>
        {
            ct.ThrowIfCancellationRequested();
            await stage(preview);
            await _dispatcher.InvokeAsync(() => EvidenceStatus = "已加入聊天草稿。尚未发送给模型，请回到主窗口检查后发送。", ct);
        }, "无法加入聊天草稿，请保留预览并稍后重试。");
    }

    public async Task CloseAsync()
    {
        if (_closed) return;
        _closed = true;
        _stageEvidence = null;
        if (_subscribed) { _watch.Changed -= OnWatchChanged; _subscribed = false; }
        _lifetime.Cancel();
        RefreshCommands();
        try { await _activeOperation; } catch { /* Operation errors are already shown locally. */ }
        try { await _refreshTask; } catch { /* Cancellation ends the coalesced refresh. */ }
        var stopped = true;
        try { await _watch.StopAsync(CancellationToken.None); }
        catch
        {
            stopped = false;
            await _dispatcher.InvokeAsync(() => Status = "监控清理失败，请重新启动应用后重试。");
        }
        await _dispatcher.InvokeAsync(() =>
        {
            IsWatching = false;
            WatchStatus = stopped ? "监控已停止。" : "监控清理失败，请重新启动应用。";
            InvalidatePreview();
        });
    }

    private Task RunOperationAsync(Func<CancellationToken, Task> action, string error)
    {
        IsBusy = true;
        _activeOperation = RunCoreAsync();
        return _activeOperation;

        async Task RunCoreAsync()
        {
            try { await action(_lifetime.Token); }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
            { await _dispatcher.InvokeAsync(() => Status = "操作已取消。"); }
            catch { await _dispatcher.InvokeAsync(() => Status = error); }
            finally { await _dispatcher.InvokeAsync(() => IsBusy = false); }
        }
    }

    private bool TryCreateQuery(out WindowsEventQuery? query)
    {
        query = null;
        if (!Channels.Contains(Channel)) { Status = "请选择 Application 或 System 频道。"; return false; }
        if (!int.TryParse(LookbackMinutesText, NumberStyles.None, CultureInfo.InvariantCulture, out var minutes) || minutes is < 1 or > 10_080)
        { Status = "时间范围请输入 1 至 10,080 分钟（最多 7 天）。"; return false; }
        if (!int.TryParse(MaxEventsText, NumberStyles.None, CultureInfo.InvariantCulture, out var max) || max is < 1 or > 200)
        { Status = "结果上限请输入 1 至 200。"; return false; }
        int? eventId = null;
        if (!string.IsNullOrWhiteSpace(EventIdText))
        {
            if (!int.TryParse(EventIdText, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id is < 0 or > 65_535)
            { Status = "事件 ID 请输入 0 至 65,535 的整数，或留空。"; return false; }
            eventId = id;
        }
        var provider = string.IsNullOrWhiteSpace(Provider) ? null : Provider.Trim();
        if (provider is not null && (provider.Length > 128 ||
            !Regex.IsMatch(provider, @"\A[\p{L}\p{N} ._{}\-]+\z", RegexOptions.CultureInvariant)))
        { Status = "来源仅支持字母、数字、空格、点、下划线、连字符和花括号，最多 128 字符。"; return false; }
        query = new WindowsEventQuery(Channel, minutes, max, eventId,
            provider, SelectedLevel?.MaximumLevel);
        return true;
    }

    private void ReplaceEvents(IEnumerable<WindowsEventItem> items)
    {
        Incidents.Clear(); SelectedIncident = null;
        IncidentStatus = "事件列表已更新，需要重新整理故障线索。";
        var checkedKeys = Events.Where(x => x.IsSelected)
            .Select(x => (x.Event.Channel, x.Event.RecordId, x.Event.Timestamp)).ToHashSet();
        var current = SelectedEvent?.Event;
        foreach (var row in Events) row.PropertyChanged -= OnRowChanged;
        Events.Clear();
        foreach (var item in items.Take(200))
        {
            var row = new EventCenterRow(item)
            { IsSelected = checkedKeys.Contains((item.Channel, item.RecordId, item.Timestamp)) };
            row.PropertyChanged += OnRowChanged;
            Events.Add(row);
        }
        SelectedEvent = Events.FirstOrDefault(x => current is not null && x.Event.Channel == current.Channel &&
            x.Event.RecordId == current.RecordId && x.Event.Timestamp == current.Timestamp);
        InvalidatePreview();
    }

    private void OnRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(EventCenterRow.IsSelected)) InvalidatePreview();
    }

    private void InvalidatePreview()
    {
        _selectionRevision++;
        _previewRevision = -1;
        EvidencePreview = "";
        EvidenceStatus = "勾选事件后生成预览；最多 20 条、16,000 字符。选择或列表更新后须重新预览。";
        OnPropertyChanged(nameof(SelectionLabel));
        RefreshCommands();
    }

    private void OnWatchChanged()
    {
        if (_closed || Interlocked.CompareExchange(ref _refreshScheduled, 1, 0) != 0) return;
        _refreshTask = RefreshAfterDelayAsync(_lifetime.Token);
    }

    private async Task RefreshAfterDelayAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(250, ct).ConfigureAwait(false);
            await _dispatcher.InvokeAsync(() =>
            {
                if (!_closed) ApplyWatchSnapshot(_watch.Snapshot);
            }, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch { await _dispatcher.InvokeAsync(() => WatchStatus = "监控状态暂不可用，请停止后重试。"); }
        finally { Interlocked.Exchange(ref _refreshScheduled, 0); }
    }

    private void ApplyWatchSnapshot(WindowsEventWatchSnapshot snapshot)
    {
        IsWatching = snapshot.State == WindowsEventWatchState.Running;
        WatchStatus = snapshot.State switch
        {
            WindowsEventWatchState.Running => $"正在监听 {snapshot.Channel} · 缓冲 {snapshot.Events.Count} 条 · 丢弃 {snapshot.DroppedEvents} 条",
            WindowsEventWatchState.Failed => "监控失败，可调整条件后重新启动。",
            _ => "监控已停止。"
        };
        if (!string.IsNullOrWhiteSpace(snapshot.Notice)) WatchStatus += " " + snapshot.Notice;
        if (_displayWatchEvents) ReplaceEvents(snapshot.Events);
    }

    private void RefreshCommands()
    {
        OnPropertyChanged(nameof(CanQuery));
        OnPropertyChanged(nameof(CanStop));
        OnPropertyChanged(nameof(CanStage));
        QueryCommand?.RaiseCanExecuteChanged();
        StartWatchCommand?.RaiseCanExecuteChanged();
        ResumeWatchCommand?.RaiseCanExecuteChanged();
        StopWatchCommand?.RaiseCanExecuteChanged();
        PreviewEvidenceCommand?.RaiseCanExecuteChanged();
        StageEvidenceCommand?.RaiseCanExecuteChanged();
        BuildIncidentsCommand?.RaiseCanExecuteChanged();
        SelectIncidentEvidenceCommand?.RaiseCanExecuteChanged();
    }

    private Task HandleUnexpectedAsync(Exception _) =>
        _dispatcher.InvokeAsync(() => Status = "操作未完成，请检查条件后重试。");
}
