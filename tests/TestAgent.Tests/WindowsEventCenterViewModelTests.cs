using KNetAgent.Desktop.WinUI.Features.WindowsEvents;
using KNetAgent.Desktop.WinUI.Services;
using TestAgent.Core;
using Xunit;

namespace TestAgent.Tests;

public sealed class WindowsEventCenterViewModelTests
{
    [Fact]
    public async Task OpenDoesNotReadOrMonitorLogs()
    {
        var reader = new Reader();
        var watch = new Watch();
        var vm = Create(reader, watch);
        vm.Open(_ => Task.CompletedTask);
        Assert.Equal(0, reader.Calls);
        Assert.Equal(0, watch.Starts);
        Assert.Empty(vm.Events);
        Assert.True(vm.CanQuery);
        await vm.CloseAsync();
    }

    [Fact]
    public async Task QueryUsesStructuredFiltersAndBoundsVisibleResults()
    {
        var reader = new Reader { Result = Enumerable.Range(1, 250).Select(Item).ToArray() };
        var vm = Create(reader);
        vm.Open(_ => Task.CompletedTask);
        vm.Channel = "System";
        vm.LookbackMinutesText = "120";
        vm.MaxEventsText = "200";
        vm.EventIdText = "1000";
        vm.Provider = "TestProvider";
        vm.SelectedLevel = vm.Levels[1];
        await vm.QueryAsync();
        Assert.Equal(new WindowsEventQuery("System", 120, 200, 1000, "TestProvider", 2), reader.LastQuery);
        Assert.Equal(200, vm.Events.Count);
        await vm.CloseAsync();
    }

    [Theory]
    [InlineData("Security", "60", "100", "")]
    [InlineData("Application", "0", "100", "")]
    [InlineData("Application", "10081", "100", "")]
    [InlineData("Application", "60", "201", "")]
    [InlineData("Application", "60", "100", "-1")]
    [InlineData("Application", "60", "100", "65536")]
    public async Task InvalidFilterNeverCallsReader(string channel, string minutes, string max, string eventId)
    {
        var reader = new Reader();
        var vm = Create(reader);
        vm.Open(_ => Task.CompletedTask);
        vm.Channel = channel;
        vm.LookbackMinutesText = minutes;
        vm.MaxEventsText = max;
        vm.EventIdText = eventId;
        await vm.QueryAsync();
        Assert.Equal(0, reader.Calls);
        await vm.CloseAsync();
    }

    [Fact]
    public async Task PreviewContainsOnlyCheckedEventsAndStageCopiesExactPreview()
    {
        var evidence = new Evidence();
        var vm = Create(new Reader { Result = [Item(1), Item(2)] }, evidence: evidence);
        string? staged = null;
        vm.Open(text => { staged = text; return Task.CompletedTask; });
        await vm.QueryAsync();
        vm.SelectedEvent = vm.Events[0]; // Viewing details alone does not select evidence.
        vm.Events[1].IsSelected = true;
        vm.BuildEvidencePreview();
        Assert.Single(evidence.Selected!);
        Assert.Equal(2, evidence.Selected![0].RecordId);
        Assert.Null(staged);
        var preview = vm.EvidencePreview;
        await vm.StageEvidenceAsync();
        Assert.Equal(preview, staged);
        Assert.Contains("尚未发送", vm.EvidenceStatus);
        await vm.CloseAsync();
    }

    [Fact]
    public async Task SelectionChangeInvalidatesPreviewAndPreventsStaleStage()
    {
        var vm = Create(new Reader { Result = [Item(1), Item(2)] });
        var stageCalls = 0;
        vm.Open(_ => { stageCalls++; return Task.CompletedTask; });
        await vm.QueryAsync();
        vm.Events[0].IsSelected = true;
        vm.BuildEvidencePreview();
        Assert.True(vm.CanStage);
        vm.Events[1].IsSelected = true;
        Assert.False(vm.CanStage);
        Assert.Empty(vm.EvidencePreview);
        await vm.StageEvidenceAsync();
        Assert.Equal(0, stageCalls);
        await vm.CloseAsync();
    }

    [Fact]
    public async Task PreviewRejectsMoreThanTwentyAndCapsFinalText()
    {
        var evidence = new Evidence { Text = new string('x', 20_000) };
        var vm = Create(new Reader { Result = Enumerable.Range(1, 21).Select(Item).ToArray() }, evidence: evidence);
        vm.Open(_ => Task.CompletedTask);
        await vm.QueryAsync();
        foreach (var row in vm.Events) row.IsSelected = true;
        vm.BuildEvidencePreview();
        Assert.Equal(0, evidence.Calls);
        Assert.False(vm.CanStage);
        vm.Events[20].IsSelected = false;
        vm.BuildEvidencePreview();
        Assert.Equal(16_000, vm.EvidencePreview.Length);
        Assert.Contains("截断", vm.EvidenceStatus);
        await vm.CloseAsync();
    }

    [Fact]
    public async Task CloseCancelsPendingReadAndWaitsForReaderCleanup()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanedUp = false;
        var reader = new Reader
        {
            Read = async ct =>
            {
                entered.SetResult();
                try { await Task.Delay(Timeout.Infinite, ct); }
                finally { cleanedUp = true; }
                return new WindowsEventQueryResult([], false, null, DateTimeOffset.UtcNow);
            }
        };
        var watch = new Watch();
        var vm = Create(reader, watch);
        vm.Open(_ => Task.CompletedTask);
        var query = vm.QueryAsync();
        await entered.Task;
        await vm.CloseAsync();
        await query;
        Assert.True(cleanedUp);
        Assert.Equal(1, watch.Stops);
        Assert.False(vm.IsBusy);
        Assert.False(vm.CanQuery);
    }

    [Fact]
    public async Task WatchResumeIsExplicitAndCloseAwaitsStop()
    {
        var watch = new Watch();
        var vm = Create(watch: watch);
        vm.Open(_ => Task.CompletedTask);
        await vm.StartWatchAsync(true);
        Assert.True(watch.Resumed);
        Assert.True(vm.IsWatching);
        Assert.False(vm.CanQuery);
        await vm.CloseAsync();
        Assert.False(vm.IsWatching);
        Assert.Equal(1, watch.Stops);
        Assert.False(watch.HasSubscriber);
    }

    [Fact]
    public async Task WatchChangesAreCoalescedAndInvalidateOldEvidence()
    {
        var watch = new Watch();
        var dispatcher = new CountingDispatcher();
        var vm = new EventCenterViewModel(new Reader(), watch, new Evidence(), dispatcher);
        vm.Open(_ => Task.CompletedTask);
        await vm.StartWatchAsync(false);
        watch.Current = new WindowsEventWatchSnapshot(WindowsEventWatchState.Running, "Application", [Item(1)], 0, DateTimeOffset.UtcNow);
        for (var i = 0; i < 20; i++) watch.RaiseChanged();
        var initialDispatches = dispatcher.Invocations;
        await WaitUntilAsync(() => vm.Events.Count == 1);
        Assert.InRange(dispatcher.Invocations - initialDispatches, 1, 2);
        vm.Events[0].IsSelected = true;
        vm.BuildEvidencePreview();
        Assert.True(vm.CanStage);
        watch.Current = watch.Current with { Events = [Item(1), Item(2)] };
        watch.RaiseChanged();
        await WaitUntilAsync(() => vm.Events.Count == 2);
        Assert.False(vm.CanStage);
        Assert.Empty(vm.EvidencePreview);
        await vm.CloseAsync();
    }

    [Fact]
    public async Task PendingWatchRefreshCannotReplaceNewHistoryQuery()
    {
        var watch = new Watch();
        var vm = Create(new Reader { Result = [Item(99)] }, watch);
        vm.Open(_ => Task.CompletedTask);
        await vm.StartWatchAsync(false);
        watch.Current = watch.Current with { Events = [Item(1)] };
        await vm.StopWatchAsync();
        await vm.QueryAsync();
        Assert.Equal(99, Assert.Single(vm.Events).Event.RecordId);
        await Task.Delay(400); // Let the queued monitor change drain after the new query.
        Assert.Equal(99, Assert.Single(vm.Events).Event.RecordId);
        await vm.CloseAsync();
    }

    [Fact]
    public async Task ProviderFailuresDoNotExposeRawExceptionText()
    {
        var vm = Create(new Reader { Read = _ => throw new InvalidOperationException("secret-payload-user-path") });
        vm.Open(_ => Task.CompletedTask);
        await vm.QueryAsync();
        Assert.False(vm.IsBusy);
        Assert.Contains("无法查询", vm.Status);
        Assert.DoesNotContain("secret-payload", vm.Status);
        await vm.CloseAsync();
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        var timeout = DateTime.UtcNow.AddSeconds(5);
        while (!predicate() && DateTime.UtcNow < timeout) await Task.Delay(25);
        Assert.True(predicate());
    }

    private static EventCenterViewModel Create(Reader? reader = null, Watch? watch = null, Evidence? evidence = null) =>
        new(reader ?? new Reader(), watch ?? new Watch(), evidence ?? new Evidence(), new InlineUiDispatcher());

    private static WindowsEventItem Item(int record) =>
        new("Application", record, 1000, "TestProvider", 2, DateTimeOffset.UtcNow, "Sanitized event " + record);

    private sealed class Reader : IWindowsEventReader
    {
        public int Calls { get; private set; }
        public WindowsEventQuery? LastQuery { get; private set; }
        public IReadOnlyList<WindowsEventItem> Result { get; set; } = [];
        public Func<CancellationToken, Task<WindowsEventQueryResult>>? Read { get; init; }
        public Task<IReadOnlyList<WindowsEventChannelInfo>> ListChannelsAsync(CancellationToken ct = default) =>
            throw new InvalidOperationException("The UI must not enumerate channels implicitly.");
        public Task<WindowsEventQueryResult> QueryAsync(WindowsEventQuery query, CancellationToken ct = default)
        {
            Calls++;
            LastQuery = query;
            return Read?.Invoke(ct) ?? Task.FromResult(new WindowsEventQueryResult(Result, false, null, DateTimeOffset.UtcNow));
        }
    }

    private sealed class Watch : IWindowsEventWatchService
    {
        public event Action? Changed;
        public WindowsEventWatchSnapshot Current { get; set; } = new(WindowsEventWatchState.Stopped, null, [], 0, null);
        public WindowsEventWatchSnapshot Snapshot => Current;
        public bool HasSubscriber => Changed is not null;
        public int Starts { get; private set; }
        public int Stops { get; private set; }
        public bool Resumed { get; private set; }
        public Task StartAsync(WindowsEventQuery query, bool resumeBookmark = false, CancellationToken ct = default)
        {
            Starts++;
            Resumed = resumeBookmark;
            Current = Current with { State = WindowsEventWatchState.Running, Channel = query.Channel };
            Changed?.Invoke();
            return Task.CompletedTask;
        }
        public Task StopAsync(CancellationToken ct = default)
        {
            Stops++;
            Current = Current with { State = WindowsEventWatchState.Stopped };
            Changed?.Invoke();
            return Task.CompletedTask;
        }
        public void RaiseChanged() => Changed?.Invoke();
        public void Dispose() { }
    }

    private sealed class Evidence : IWindowsEventEvidenceBuilder
    {
        public string Text { get; init; } = "Only selected sanitized evidence.";
        public int Calls { get; private set; }
        public IReadOnlyList<WindowsEventItem>? Selected { get; private set; }
        public WindowsEventEvidence Build(IReadOnlyList<WindowsEventItem> selectedEvents)
        {
            Calls++;
            Selected = selectedEvents;
            return new WindowsEventEvidence(Text, selectedEvents.Count, false);
        }
    }

    private sealed class CountingDispatcher : IUiDispatcher
    {
        public bool HasThreadAccess => true;
        public int Invocations;
        public void Post(Action action) { Interlocked.Increment(ref Invocations); action(); }
        public Task InvokeAsync(Action action, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref Invocations);
            action();
            return Task.CompletedTask;
        }
    }
}
