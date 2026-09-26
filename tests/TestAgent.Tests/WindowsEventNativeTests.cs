using TestAgent.Core;
using TestAgent.Infrastructure;
using Xunit;

namespace TestAgent.Tests;

public sealed class WindowsEventNativeTests
{
    [Theory]
    [InlineData("Security")]
    [InlineData("ForwardedEvents")]
    [InlineData("\\\\other\\System")]
    [InlineData("Application' or *")]
    public void Query_rejects_any_channel_outside_the_two_local_channels(string channel) =>
        Assert.Throws<ArgumentException>(() => WindowsEventQueryPolicy.BuildXPath(new(Channel: channel)));

    [Theory]
    [InlineData("x'] or *[System[Level=4]]")]
    [InlineData("one\"two")]
    [InlineData("one/two")]
    [InlineData("one\ntwo")]
    [InlineData("one`two")]
    public void Query_rejects_provider_syntax_injection(string provider) =>
        Assert.Throws<ArgumentException>(() => WindowsEventQueryPolicy.BuildXPath(new(Provider: provider)));

    [Fact]
    public void Query_validates_limits_and_builds_invariant_structured_filter()
    {
        Assert.Throws<ArgumentException>(() => WindowsEventQueryPolicy.Validate(new(MaxEvents: 201)));
        Assert.Throws<ArgumentException>(() => WindowsEventQueryPolicy.Validate(new(MaxEvents: 0)));
        Assert.Throws<ArgumentException>(() => WindowsEventQueryPolicy.Validate(new(LookbackMinutes: 10081)));
        Assert.Throws<ArgumentException>(() => WindowsEventQueryPolicy.Validate(new(EventId: 65536)));
        Assert.Throws<ArgumentException>(() => WindowsEventQueryPolicy.Validate(new(MaximumLevel: 0)));
        Assert.Throws<ArgumentException>(() => WindowsEventQueryPolicy.Validate(new(BeforeRecordId: 0)));
        Assert.Throws<ArgumentException>(() => WindowsEventQueryPolicy.Validate(new(Provider: new string('a', 129))));
        Assert.Equal("*[System[TimeCreated[timediff(@SystemTime) >= 0 and timediff(@SystemTime) <= 120000] and EventID=42 and (Level >= 1 and Level <= 3) and Provider[@Name='Microsoft-Windows-Test'] and EventRecordID < 99]]",
            WindowsEventQueryPolicy.BuildXPath(new(LookbackMinutes: 2, EventId: 42, Provider: " Microsoft-Windows-Test ", BeforeRecordId: 99)));
        Assert.DoesNotContain("Level", WindowsEventQueryPolicy.BuildXPath(new(MaximumLevel: null)));
    }

    [Fact]
    public async Task Reader_probes_only_approved_channels_and_redacts_failures()
    {
        var source = new FakeSource { ChannelError = new UnauthorizedAccessException("secret path username") };
        var channels = await new WindowsEventReader(source).ListChannelsAsync();
        Assert.Equal(new[] { "Application", "System" }, source.Channels);
        Assert.All(channels, channel => { Assert.False(channel.Available); Assert.DoesNotContain("secret", channel.Notice!); });
    }

    [Fact]
    public async Task Reader_enforces_maximum_and_disposes_cursor_with_pagination()
    {
        var source = new FakeSource();
        source.Cursor.Items.Enqueue(Data(3)); source.Cursor.Items.Enqueue(Data(2)); source.Cursor.Items.Enqueue(Data(1));
        var result = await new WindowsEventReader(source).QueryAsync(new(MaxEvents: 2));
        Assert.Equal(new long[] { 3, 2 }, result.Events.Select(x => x.RecordId));
        Assert.True(result.Truncated); Assert.Equal(2, result.NextBeforeRecordId);
        Assert.True(source.Cursor.IsDisposed);
    }

    [Fact]
    public async Task Reader_cancellation_cancels_native_read_and_releases_cursor()
    {
        var source = new FakeSource(); source.Cursor.Block = true;
        using var cancel = new CancellationTokenSource();
        var read = new WindowsEventReader(source).QueryAsync(new(), cancel.Token);
        await source.Cursor.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
        await source.Cursor.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(source.Cursor.Cancelled);
    }

    [Fact]
    public async Task Reader_pre_cancelled_request_never_opens_native_handles()
    {
        var source = new FakeSource();
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new WindowsEventReader(source).QueryAsync(new(), cancel.Token));
        Assert.Equal(0, source.ReaderOpens);
    }

    [Fact]
    public async Task Reader_keeps_access_denied_type_without_native_error_contents()
    {
        var source = new FakeSource { ReaderError = new UnauthorizedAccessException("private-user-path") };
        var error = await Assert.ThrowsAsync<UnauthorizedAccessException>(() => new WindowsEventReader(source).QueryAsync(new()));
        Assert.DoesNotContain("private-user-path", error.Message);
        Assert.Null(error.InnerException);
    }

    [Fact]
    public async Task Native_read_only_query_when_explicitly_requested()
    {
        // Opt-in only: routine fixtures never depend on, print, or write the host's logs.
        if (Environment.GetEnvironmentVariable("KNET_NATIVE_EVENT_SMOKE") != "1") return;
        var result = await new WindowsEventReader().QueryAsync(new(MaxEvents: 1, LookbackMinutes: 60));
        Assert.InRange(result.Events.Count, 0, 1);
        Assert.All(result.Events, item => Assert.Equal("Application", item.Channel));
    }

    [Fact]
    public async Task Watch_is_explicit_single_and_bounded_and_disposes_on_stop()
    {
        using var fixture = new Fixture(); var source = new FakeSource();
        using var watch = fixture.CreateWatch(source);
        Assert.Equal(WindowsEventWatchState.Stopped, watch.Snapshot.State); Assert.Null(source.Watch);
        await watch.StartAsync(new());
        Assert.Null(source.Bookmark); Assert.True(source.Watch!.Started);
        await Assert.ThrowsAsync<InvalidOperationException>(() => watch.StartAsync(new(Channel: "System")));
        for (var id = 1; id <= 225; id++) source.Emit(id);
        Assert.Equal(200, watch.Snapshot.Events.Count); Assert.Equal(25, watch.Snapshot.DroppedEvents);
        Assert.Equal(26, watch.Snapshot.Events[0].RecordId);
        await watch.StopAsync();
        Assert.True(source.Watch.IsDisposed); Assert.Equal(WindowsEventWatchState.Stopped, watch.Snapshot.State);
        source.Emit(226); Assert.Equal(225, watch.Snapshot.Events[^1].RecordId);
    }

    [Fact]
    public async Task Bookmark_contains_only_position_metadata_and_requires_matching_workspace_and_filter()
    {
        using var fixture = new Fixture(); var source = new FakeSource();
        var query = new WindowsEventQuery(Provider: ".NET Runtime", MaximumLevel: 3);
        using (var watch = fixture.CreateWatch(source))
        { await watch.StartAsync(query); source.Emit(41); await watch.StopAsync(); }
        var file = Assert.Single(Directory.GetFiles(Path.Combine(fixture.Paths.Root, "windows-events")));
        var contents = File.ReadAllText(file);
        Assert.Contains("RecordId=\"41\"", contents); Assert.DoesNotContain("example event", contents);
        using (var restored = fixture.CreateWatch(source))
        { await restored.StartAsync(query, resumeBookmark: true); Assert.Contains("41", source.Bookmark!); await restored.StopAsync(); }
        using (var changedFilter = fixture.CreateWatch(new FakeSource()))
            await Assert.ThrowsAsync<InvalidOperationException>(() => changedFilter.StartAsync(query with { MaximumLevel = 2 }, true));
        Directory.CreateDirectory(Path.Combine(fixture.Root, "other-workspace"));
        using var other = new WindowsEventWatchService(fixture.Paths, new WorkspaceLocator(Path.Combine(fixture.Root, "other-workspace")), new FakeSource());
        await Assert.ThrowsAsync<InvalidOperationException>(() => other.StartAsync(query, true));
    }

    [Fact]
    public async Task Bookmark_corruption_is_rejected_without_native_subscription_or_fallback()
    {
        using var fixture = new Fixture(); var source = new FakeSource();
        using (var watch = fixture.CreateWatch(source)) { await watch.StartAsync(new()); source.Emit(9); await watch.StopAsync(); }
        var file = Assert.Single(Directory.GetFiles(Path.Combine(fixture.Paths.Root, "windows-events")));
        File.WriteAllText(file, "<!DOCTYPE x [<!ENTITY x SYSTEM 'file:///secret'>]><BookmarkList>&x;</BookmarkList>");
        var restartedSource = new FakeSource(); using var restarted = fixture.CreateWatch(restartedSource);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => restarted.StartAsync(new(), true));
        Assert.Null(restartedSource.Watch); Assert.Equal(WindowsEventWatchState.Failed, restarted.Snapshot.State);
        Assert.DoesNotContain("secret", error.Message);
    }

    [Fact]
    public async Task Stale_bookmark_failure_does_not_silently_restart_at_the_beginning()
    {
        using var fixture = new Fixture(); var source = new FakeSource();
        using (var watch = fixture.CreateWatch(source)) { await watch.StartAsync(new()); source.Emit(9); await watch.StopAsync(); }
        var restartedSource = new FakeSource { WatchError = new InvalidDataException("stale") };
        using var restarted = fixture.CreateWatch(restartedSource);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => restarted.StartAsync(new(), true));
        Assert.Contains("书签", error.Message); Assert.Equal(1, restartedSource.WatchCreates);
        Assert.Null(restartedSource.Watch);
    }

    [Fact]
    public async Task Starting_fresh_does_not_leave_an_old_bookmark_to_be_resumed()
    {
        using var fixture = new Fixture(); var source = new FakeSource();
        using var watch = fixture.CreateWatch(source);
        await watch.StartAsync(new()); source.Emit(1); await watch.StopAsync();
        await watch.StartAsync(new()); await watch.StopAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => watch.StartAsync(new(), true));
    }

    [Fact]
    public async Task Failed_start_for_another_filter_never_persists_the_previous_filters_bookmark()
    {
        using var fixture = new Fixture(); var source = new FakeSource();
        var watch = fixture.CreateWatch(source);
        await watch.StartAsync(new()); source.Emit(17); await watch.StopAsync();
        var directory = Path.Combine(fixture.Paths.Root, "windows-events");
        var original = Assert.Single(Directory.GetFiles(directory));
        var originalContent = File.ReadAllText(original);
        var otherFilter = new WindowsEventQuery(Provider: "Other.Provider");
        await Assert.ThrowsAsync<InvalidOperationException>(() => watch.StartAsync(otherFilter, true));
        Assert.Empty(watch.Snapshot.Events);
        await watch.StopAsync(); watch.Dispose();
        Assert.Equal(original, Assert.Single(Directory.GetFiles(directory)));
        Assert.Equal(originalContent, File.ReadAllText(original));
        using var restarted = fixture.CreateWatch(new FakeSource());
        await Assert.ThrowsAsync<InvalidOperationException>(() => restarted.StartAsync(otherFilter, true));
    }

    [Fact]
    public async Task Dispose_persists_bookmark_and_rejects_further_starts()
    {
        using var fixture = new Fixture(); var source = new FakeSource(); var watch = fixture.CreateWatch(source);
        await watch.StartAsync(new()); source.Emit(7); watch.Dispose(); watch.Dispose();
        await source.Watch!.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(source.Watch!.IsDisposed);
        Assert.True(SpinWait.SpinUntil(() => Directory.Exists(Path.Combine(fixture.Paths.Root, "windows-events")) &&
            Directory.GetFiles(Path.Combine(fixture.Paths.Root, "windows-events")).Length == 1, TimeSpan.FromSeconds(3)));
        Assert.Single(Directory.GetFiles(Path.Combine(fixture.Paths.Root, "windows-events")));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => watch.StartAsync(new()));
    }

    [Fact]
    public async Task Failed_callback_cleans_up_subscription_without_leaking_exception_text()
    {
        using var fixture = new Fixture(); var source = new FakeSource(); using var watch = fixture.CreateWatch(source);
        await watch.StartAsync(new()); source.Fail!(new IOException("secret-file-path"));
        await source.Watch!.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(WindowsEventWatchState.Failed, watch.Snapshot.State);
        Assert.DoesNotContain("secret-file-path", watch.Snapshot.Notice!);
    }

    [Theory]
    [InlineData(true, "cancel")]
    [InlineData(false, "cancel")]
    [InlineData(true, "stop")]
    [InlineData(false, "stop")]
    [InlineData(true, "dispose")]
    [InlineData(false, "dispose")]
    public async Task Blocked_native_start_has_bounded_cancellation_and_late_handle_cleanup(bool duringCreation, string action)
    {
        using var fixture = new Fixture();
        using var release = new ManualResetEventSlim();
        var source = new FakeSource
        { CreateRelease = duringCreation ? release : null, StartRelease = duringCreation ? null : release };
        var watch = fixture.CreateWatch(source);
        using var cancel = new CancellationTokenSource();
        var starting = watch.StartAsync(new(), ct: cancel.Token);
        try
        {
            if (duringCreation) await source.CreateEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            else await (await source.Created.Task.WaitAsync(TimeSpan.FromSeconds(3))).StartEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            if (action == "cancel") cancel.Cancel();
            else if (action == "stop") await watch.StopAsync().WaitAsync(TimeSpan.FromSeconds(3));
            else await Task.Run(watch.Dispose).WaitAsync(TimeSpan.FromSeconds(3));

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => starting).WaitAsync(TimeSpan.FromSeconds(3));
            if (action != "dispose")
            {
                var retry = await Assert.ThrowsAsync<InvalidOperationException>(() => watch.StartAsync(new())).WaitAsync(TimeSpan.FromSeconds(3));
                Assert.Contains("释放资源", retry.Message);
                await watch.StopAsync().WaitAsync(TimeSpan.FromSeconds(3));
            }
            await Task.Run(watch.Dispose).WaitAsync(TimeSpan.FromSeconds(3));
            release.Set();
            var native = await source.Created.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await native.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.True(native.IsDisposed);
            source.Emit(18);
            Assert.Empty(watch.Snapshot.Events);
        }
        finally { release.Set(); watch.Dispose(); }
    }

    [Fact]
    public async Task Blocked_native_dispose_does_not_hold_stop_cancellation_or_ui_dispose()
    {
        using var fixture = new Fixture(); using var release = new ManualResetEventSlim();
        var source = new FakeSource { DisposeRelease = release }; var watch = fixture.CreateWatch(source);
        using var cancel = new CancellationTokenSource();
        try
        {
            await watch.StartAsync(new());
            var stopping = watch.StopAsync(cancel.Token);
            await source.Watch!.DisposeEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stopping).WaitAsync(TimeSpan.FromSeconds(3));
            await Assert.ThrowsAsync<InvalidOperationException>(() => watch.StartAsync(new())).WaitAsync(TimeSpan.FromSeconds(3));
            await Task.Run(watch.Dispose).WaitAsync(TimeSpan.FromSeconds(3));
            release.Set();
            await source.Watch.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(3));
        }
        finally { release.Set(); watch.Dispose(); }
    }

    private static WindowsEventNativeData Data(long id) => new(new("Application", id, 1000, "Test.Provider", 2,
        DateTimeOffset.UtcNow, "example event"), $"<BookmarkList><Bookmark Channel=\"Application\" RecordId=\"{id}\" IsCurrent=\"true\" /></BookmarkList>");

    private sealed class FakeSource : IWindowsEventNativeSource
    {
        public readonly FakeCursor Cursor = new();
        public readonly List<string> Channels = [];
        public Exception? ChannelError; public Exception? WatchError; public Exception? ReaderError;
        public ManualResetEventSlim? CreateRelease; public ManualResetEventSlim? StartRelease; public ManualResetEventSlim? DisposeRelease;
        public readonly TaskCompletionSource CreateEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource<FakeWatch> Created = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public FakeWatch? Watch; public string? Bookmark;
        public Action<WindowsEventNativeData>? Received; public Action<Exception>? Fail;
        public int ReaderOpens; public int WatchCreates;
        public bool IsChannelAvailable(string channel) { Channels.Add(channel); if (ChannelError is not null) throw ChannelError; return true; }
        public IWindowsEventNativeCursor OpenReader(string channel, string xpath) { ReaderOpens++; if (ReaderError is not null) throw ReaderError; return Cursor; }
        public IWindowsEventNativeWatch CreateWatch(string channel, string xpath, string? bookmarkXml, Action<WindowsEventNativeData> received, Action<Exception> failed)
        {
            WatchCreates++; if (WatchError is not null) throw WatchError;
            CreateEntered.TrySetResult(); CreateRelease?.Wait();
            Bookmark = bookmarkXml; Received = received; Fail = failed;
            Watch = new() { StartRelease = StartRelease, DisposeRelease = DisposeRelease };
            Created.TrySetResult(Watch);
            return Watch;
        }
        public void Emit(long id) => Received!(Data(id));
    }

    private sealed class FakeCursor : IWindowsEventNativeCursor
    {
        public readonly Queue<WindowsEventNativeData> Items = new();
        public readonly TaskCompletionSource ReadStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Disposed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ManualResetEventSlim _release = new();
        public bool Block; public bool Cancelled; public bool IsDisposed;
        public WindowsEventNativeData? Read(TimeSpan timeout)
        {
            ReadStarted.TrySetResult();
            if (Block) _release.Wait(TimeSpan.FromSeconds(5));
            return Items.TryDequeue(out var item) ? item : null;
        }
        public void Cancel() { Cancelled = true; _release.Set(); }
        public void Dispose() { IsDisposed = true; _release.Dispose(); Disposed.TrySetResult(); }
    }

    private sealed class FakeWatch : IWindowsEventNativeWatch
    {
        public bool Started; public bool IsDisposed;
        public ManualResetEventSlim? StartRelease; public ManualResetEventSlim? DisposeRelease;
        public readonly TaskCompletionSource StartEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource DisposeEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Disposed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Start() { StartEntered.TrySetResult(); StartRelease?.Wait(); Started = true; }
        public void Dispose() { DisposeEntered.TrySetResult(); DisposeRelease?.Wait(); IsDisposed = true; Disposed.TrySetResult(); }
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "KNet-WindowsEvents-" + Guid.NewGuid().ToString("N"));
        public AppPaths Paths { get; }
        public WorkspaceLocator Workspace { get; }
        public Fixture()
        {
            Directory.CreateDirectory(Root); Directory.CreateDirectory(Path.Combine(Root, "workspace"));
            Paths = new(Path.Combine(Root, "data")); Workspace = new(Path.Combine(Root, "workspace"));
        }
        public WindowsEventWatchService CreateWatch(FakeSource source) => new(Paths, Workspace, source);
        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }
}
