using KNetAgent.Desktop.WinUI.Features.WindowsEvents;
using KNetAgent.Desktop.WinUI.Services;
using TestAgent.Core;
using TestAgent.Infrastructure;
using Xunit;

namespace TestAgent.Tests;

public sealed class WindowsIncidentViewModelTests
{
    [Fact]
    public async Task Incident_selection_requires_explicit_preview_before_exact_draft_handoff()
    {
        var time = new DateTimeOffset(2026, 9, 26, 12, 0, 0, TimeSpan.Zero);
        var rows = Enumerable.Range(1, 30).Select(id => new WindowsEventItem("Application", id,
            1000, "Application Error", 2, time.AddSeconds(id), "Sanitized crash marker")).ToArray();
        var reader = new Reader(rows);
        var vm = new EventCenterViewModel(reader, new Watch(), new WindowsEventEvidenceBuilder(),
            new InlineUiDispatcher(), new WindowsIncidentCorrelator());
        string? staged = null;
        vm.Open(text => { staged = text; return Task.CompletedTask; });
        Assert.Equal(0, reader.Calls);
        await vm.QueryAsync();
        vm.BuildIncidents();
        Assert.Single(vm.Incidents);
        Assert.Equal(30, vm.SelectedIncident!.Evidence.Count);
        vm.SelectIncidentEvidence();
        Assert.Equal(20, vm.Events.Count(row => row.IsSelected));
        Assert.False(vm.CanStage);
        Assert.Null(staged);
        vm.BuildEvidencePreview();
        var preview = vm.EvidencePreview;
        Assert.True(vm.CanStage);
        await vm.StageEvidenceAsync();
        Assert.Equal(preview, staged);
        Assert.Equal(1, reader.Calls);
        await vm.CloseAsync();
    }

    [Fact]
    public async Task Refresh_invalidates_both_incident_and_selected_evidence_preview()
    {
        var row = new WindowsEventItem("System", 22, 7031, "Service Control Manager", 2,
            DateTimeOffset.UtcNow, "Sanitized service metadata");
        var vm = new EventCenterViewModel(new Reader([row]), new Watch(), new WindowsEventEvidenceBuilder(),
            new InlineUiDispatcher(), new WindowsIncidentCorrelator());
        vm.Open(_ => Task.CompletedTask);
        await vm.QueryAsync();
        vm.BuildIncidents(); vm.SelectIncidentEvidence(); vm.BuildEvidencePreview();
        Assert.True(vm.CanStage);
        await vm.QueryAsync();
        Assert.Empty(vm.Incidents);
        Assert.Null(vm.SelectedIncident);
        Assert.False(vm.CanStage);
        await vm.CloseAsync();
    }

    [Fact]
    public async Task Incident_selection_matches_channel_record_and_timestamp_including_equivalent_offsets()
    {
        var time = new DateTimeOffset(2026, 9, 26, 12, 0, 0, TimeSpan.FromHours(8));
        var original = new WindowsEventItem("Application", 7, 42, "Example Provider", 2, time, "first");
        var otherDate = original with { Timestamp = time.AddDays(1), Message = "another-date" };
        var otherChannel = original with { Channel = "System", Message = "another-channel" };
        var vm = new EventCenterViewModel(new Reader([original, otherDate, otherChannel]), new Watch(),
            new WindowsEventEvidenceBuilder(), new InlineUiDispatcher(), new WindowsIncidentCorrelator());
        vm.Open(_ => Task.CompletedTask);
        await vm.QueryAsync();
        vm.BuildIncidents();
        Assert.Equal(3, vm.Incidents.Count);
        vm.SelectedIncident = Assert.Single(vm.Incidents, incident => incident.Evidence[0].Channel == "Application" &&
            incident.Evidence[0].Timestamp == time);
        vm.SelectIncidentEvidence();
        Assert.Equal(original, Assert.Single(vm.Events, row => row.IsSelected).Event);
        await vm.CloseAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Watch_refresh_retains_selection_only_for_the_same_event_instant(bool sameInstant)
    {
        var time = new DateTimeOffset(2026, 9, 26, 12, 0, 0, TimeSpan.Zero);
        var original = new WindowsEventItem("Application", 7, 42, "Example Provider", 2, time, "first");
        var updated = original with { Timestamp = sameInstant ? time.ToOffset(TimeSpan.FromHours(8)) : time.AddDays(1) };
        var watch = new MutableWatch([original]);
        var vm = new EventCenterViewModel(new Reader([]), watch, new WindowsEventEvidenceBuilder(),
            new InlineUiDispatcher(), new WindowsIncidentCorrelator());
        vm.Open(_ => Task.CompletedTask);
        await vm.StartWatchAsync(false);
        vm.SelectedEvent = vm.Events[0];
        vm.Events[0].IsSelected = true;
        vm.BuildIncidents();
        vm.BuildEvidencePreview();
        Assert.True(vm.CanStage);
        var previousRow = vm.Events[0];
        var refreshed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        vm.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(vm.SelectionLabel)) refreshed.TrySetResult();
        };
        watch.Publish([updated]);
        await refreshed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotSame(previousRow, vm.Events[0]);
        Assert.Equal(sameInstant, vm.Events[0].IsSelected);
        Assert.Equal(sameInstant, vm.SelectedEvent is not null);
        Assert.Empty(vm.Incidents);
        Assert.Null(vm.SelectedIncident);
        Assert.Empty(vm.EvidencePreview);
        Assert.False(vm.CanStage);
        await vm.CloseAsync();
    }

    [Fact]
    public async Task Failed_history_refresh_clears_old_incidents_selection_and_shareable_evidence()
    {
        var reader = new Reader([new("Application", 7, 42, "Example Provider", 2,
            DateTimeOffset.UtcNow, "first")]);
        var vm = new EventCenterViewModel(reader, new Watch(), new WindowsEventEvidenceBuilder(),
            new InlineUiDispatcher(), new WindowsIncidentCorrelator());
        vm.Open(_ => Task.CompletedTask);
        await vm.QueryAsync();
        vm.BuildIncidents(); vm.SelectIncidentEvidence(); vm.BuildEvidencePreview();
        Assert.True(vm.CanStage);
        reader.Fail = true;
        await vm.QueryAsync();
        Assert.Empty(vm.Events);
        Assert.Empty(vm.Incidents);
        Assert.Null(vm.SelectedIncident);
        Assert.Null(vm.SelectedEvent);
        Assert.Empty(vm.EvidencePreview);
        Assert.False(vm.CanStage);
        Assert.Contains("无法查询事件", vm.Status);
        await vm.CloseAsync();
    }

    [Fact]
    public async Task Incident_build_is_bounded_to_200_visible_events_and_selection_uses_latest_20()
    {
        var time = new DateTimeOffset(2026, 9, 26, 12, 0, 0, TimeSpan.Zero);
        var rows = Enumerable.Range(1, 250).Select(id => new WindowsEventItem("Application", id,
            1000, "Application Error", 2, time.AddSeconds(id), "marker")).ToArray();
        var vm = new EventCenterViewModel(new Reader(rows), new Watch(), new WindowsEventEvidenceBuilder(),
            new InlineUiDispatcher(), new WindowsIncidentCorrelator());
        vm.Open(_ => Task.CompletedTask);
        vm.MaxEventsText = "200";
        await vm.QueryAsync();
        Assert.Equal(200, vm.Events.Count);
        vm.BuildIncidents();
        Assert.Equal(200, Assert.Single(vm.Incidents).Evidence.Count);
        vm.SelectIncidentEvidence();
        Assert.Equal(Enumerable.Range(181, 20).Select(id => (long)id),
            vm.Events.Where(row => row.IsSelected).Select(row => row.Event.RecordId));
        Assert.False(vm.CanStage);
        await vm.CloseAsync();
    }

    private sealed class Reader(IReadOnlyList<WindowsEventItem> rows) : IWindowsEventReader
    {
        public int Calls;
        public bool Fail;
        public Task<IReadOnlyList<WindowsEventChannelInfo>> ListChannelsAsync(CancellationToken ct = default) =>
            throw new InvalidOperationException("No implicit channel probe.");
        public Task<WindowsEventQueryResult> QueryAsync(WindowsEventQuery query, CancellationToken ct = default)
        {
            Calls++;
            return Fail ? Task.FromException<WindowsEventQueryResult>(new InvalidOperationException("fixture failure")) :
                Task.FromResult(new WindowsEventQueryResult(rows, false, null, DateTimeOffset.UtcNow));
        }
    }

    private sealed class MutableWatch(IReadOnlyList<WindowsEventItem> rows) : IWindowsEventWatchService
    {
        public event Action? Changed;
        public WindowsEventWatchSnapshot Snapshot { get; private set; } =
            new(WindowsEventWatchState.Stopped, "Application", rows, 0, null);
        public Task StartAsync(WindowsEventQuery query, bool resumeBookmark = false, CancellationToken ct = default)
        {
            Snapshot = Snapshot with { State = WindowsEventWatchState.Running };
            return Task.CompletedTask;
        }
        public void Publish(IReadOnlyList<WindowsEventItem> items)
        { Snapshot = Snapshot with { Events = items }; Changed?.Invoke(); }
        public Task StopAsync(CancellationToken ct = default)
        { Snapshot = Snapshot with { State = WindowsEventWatchState.Stopped }; return Task.CompletedTask; }
        public void Dispose() { }
    }
    private sealed class Watch : IWindowsEventWatchService
    {
        public event Action? Changed { add { } remove { } }
        public WindowsEventWatchSnapshot Snapshot => new(WindowsEventWatchState.Stopped, null, [], 0, null);
        public Task StartAsync(WindowsEventQuery query, bool resumeBookmark = false, CancellationToken ct = default) =>
            throw new InvalidOperationException("No implicit watch.");
        public Task StopAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void Dispose() { }
    }
}
