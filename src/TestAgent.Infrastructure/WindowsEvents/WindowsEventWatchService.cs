using TestAgent.Core;

namespace TestAgent.Infrastructure;

public sealed class WindowsEventWatchService : IWindowsEventWatchService
{
    private readonly object _sync = new();
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly IWindowsEventNativeSource _source;
    private readonly WindowsEventBookmarks _bookmarks;
    private readonly Queue<WindowsEventItem> _events = new();
    private readonly CancellationTokenSource _shutdown = new();
    private CancellationTokenSource? _startupCancellation;
    private Task _cleanup = Task.CompletedTask;
    private IWindowsEventNativeWatch? _watch;
    private WindowsEventQuery? _query;
    private WindowsEventWatchState _state;
    private DateTimeOffset? _startedAt;
    private string? _notice;
    private string? _bookmarkXml;
    private int _dropped;
    private long _generation;
    private bool _disposed;
    public const int BufferCapacity = 200;
    private static readonly TimeSpan NativeBudget = TimeSpan.FromSeconds(10);

    public WindowsEventWatchService(AppPaths paths, WorkspaceLocator workspace, IWindowsEventNativeSource? source = null)
    {
        _source = source ?? new WindowsEventNativeSource();
        _bookmarks = new(paths, workspace.Id);
    }

    public event Action? Changed;
    public WindowsEventWatchSnapshot Snapshot
    {
        get { lock (_sync) return new(_state, _query?.Channel, _events.ToArray(), _dropped, _startedAt, _notice); }
    }

    public async Task StartAsync(WindowsEventQuery query, bool resumeBookmark = false, CancellationToken ct = default)
    {
        query = WindowsEventQueryPolicy.Validate(query);
        if (query.BeforeRecordId is not null)
            throw new ArgumentException("实时监控不使用历史查询的分页位置。", nameof(query));
        lock (_sync) ObjectDisposedException.ThrowIf(_disposed, this);
        using var requestedStop = new CancellationTokenSource();
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct, _shutdown.Token, requestedStop.Token);
        budget.CancelAfter(NativeBudget);
        var token = budget.Token;
        await _lifecycle.WaitAsync(token).ConfigureAwait(false);
        try
        {
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_watch is not null || _state == WindowsEventWatchState.Running)
                    throw new InvalidOperationException("已有事件监控运行。请先停止，再切换频道或筛选条件。");
                if (!_cleanup.IsCompleted)
                    throw new InvalidOperationException("上次监控仍在释放资源，完成后才能重新启动。");
                _startupCancellation = requestedStop;
            }
            token.ThrowIfCancellationRequested();
            string? bookmark = null;
            IWindowsEventNativeWatch? created = null;
            Task<IWindowsEventNativeWatch>? creation = null;
            Task? pendingOperation = null;
            try
            {
                if (resumeBookmark)
                {
                    var loading = Task.Run(() => _bookmarks.Load(query), CancellationToken.None);
                    pendingOperation = loading;
                    bookmark = await loading.WaitAsync(token).ConfigureAwait(false);
                }
                if (resumeBookmark && bookmark is null)
                    throw new InvalidDataException("当前工作区及筛选条件没有可恢复的书签。请从现在开始监控。");
                long generation;
                lock (_sync) generation = ++_generation;
                creation = Task.Run(() => _source.CreateWatch(query.Channel, WindowsEventQueryPolicy.BuildXPath(query), bookmark,
                    item => Receive(generation, item), error => Fail(generation, error, resumeBookmark)), CancellationToken.None);
                pendingOperation = creation;
                created = await creation.WaitAsync(token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                lock (_sync)
                {
                    ObjectDisposedException.ThrowIf(_disposed, this);
                    _query = query; _events.Clear(); _dropped = 0; _notice = null;
                    _bookmarkXml = bookmark; _startedAt = DateTimeOffset.UtcNow;
                    _watch = created; _state = WindowsEventWatchState.Running;
                }
                // Subscribe after state publication: native events may arrive immediately.
                pendingOperation = Task.Run(created.Start, CancellationToken.None);
                await pendingOperation.WaitAsync(token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if (!resumeBookmark)
                {
                    pendingOperation = Task.Run(() => _bookmarks.Forget(query), CancellationToken.None);
                    await pendingOperation.WaitAsync(token).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                var cancelled = ct.IsCancellationRequested || requestedStop.IsCancellationRequested || _shutdown.IsCancellationRequested;
                var timedOut = token.IsCancellationRequested && !cancelled;
                lock (_sync)
                {
                    ++_generation; _watch = null; _query = query; _bookmarkXml = null;
                    _events.Clear(); _dropped = 0; _startedAt = null;
                    _state = cancelled ? WindowsEventWatchState.Stopped : WindowsEventWatchState.Failed;
                    _notice = cancelled ? "监控启动已取消，正在释放资源。" : timedOut
                        ? "监控启动超过 10 秒，正在释放资源。" : WindowsEventErrors.Describe(ex, resumeBookmark);
                }
                // Never dispose a handle concurrently with an unfinished native Start.
                // Late successful creation is observed and disposed even after the caller leaves.
                _ = QueueCleanup(pendingOperation, creation, created, null, null);
                if (cancelled) throw new OperationCanceledException("监控启动已取消。", ex, token);
                if (timedOut) throw new TimeoutException("Windows 事件监控启动超过 10 秒。请等待资源释放后重试。");
                if (ex is OperationCanceledException) throw;
                throw WindowsEventErrors.Wrap(ex, resumeBookmark);
            }
        }
        finally
        {
            lock (_sync) { if (ReferenceEquals(_startupCancellation, requestedStop)) _startupCancellation = null; }
            _lifecycle.Release(); Notify();
        }
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        CancellationTokenSource? startup;
        lock (_sync) startup = _startupCancellation;
        try { startup?.Cancel(); } catch (ObjectDisposedException) { }
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(NativeBudget);
        await _lifecycle.WaitAsync(budget.Token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var cleanup = StopCore(keepFailure: false);
            // A cancelled startup already detached its handle; logical stop is complete.
            if (cleanup is not null) await cleanup.WaitAsync(budget.Token).ConfigureAwait(false);
        }
        finally { _lifecycle.Release(); Notify(); }
    }

    private void Receive(long generation, WindowsEventNativeData data)
    {
        lock (_sync)
        {
            if (_disposed || generation != _generation || _state != WindowsEventWatchState.Running || _query is null) return;
            var item = data.Item with
            {
                Channel = _query.Channel,
                Provider = WindowsEventPrivacy.Sanitize(data.Item.Provider, 200),
                Message = WindowsEventPrivacy.Sanitize(data.Item.Message)
            };
            if (_events.Count == BufferCapacity) { _events.Dequeue(); if (_dropped < int.MaxValue) _dropped++; }
            _events.Enqueue(item);
            if (data.BookmarkXml is { } bookmark)
            {
                WindowsEventBookmarks.ReadRecordId(bookmark, _query.Channel);
                _bookmarkXml = bookmark;
            }
        }
        Notify();
    }

    private void Fail(long generation, Exception error, bool bookmark)
    {
        lock (_sync)
        {
            if (_disposed || generation != _generation || _state != WindowsEventWatchState.Running) return;
            _state = WindowsEventWatchState.Failed;
            _notice = WindowsEventErrors.Describe(error, bookmark);
        }
        Notify();
        // Disposing EventLogWatcher from its callback can wait for that same callback.
        _ = Task.Run(async () =>
        {
            await _lifecycle.WaitAsync().ConfigureAwait(false);
            try { if (!_disposed && generation == _generation) _ = StopCore(keepFailure: true); }
            finally { _lifecycle.Release(); Notify(); }
        });
    }

    private Task? StopCore(bool keepFailure)
    {
        IWindowsEventNativeWatch? watch;
        WindowsEventQuery? query;
        string? bookmark;
        lock (_sync)
        {
            ++_generation; watch = _watch; _watch = null; query = _query; bookmark = _bookmarkXml;
            if (!keepFailure) _state = WindowsEventWatchState.Stopped;
        }
        if (watch is null) return null;
        return QueueCleanup(null, null, watch, query, bookmark);
    }

    private Task QueueCleanup(Task? operation, Task<IWindowsEventNativeWatch>? creation,
        IWindowsEventNativeWatch? native, WindowsEventQuery? query, string? bookmark)
    {
        Task previous;
        lock (_sync) previous = _cleanup;
        var cleanup = Task.Run(async () =>
        {
            try { await previous.ConfigureAwait(false); } catch { }
            if (operation is not null) try { await operation.ConfigureAwait(false); } catch { }
            if (creation is not null) try { native = await creation.ConfigureAwait(false); } catch { }
            try { native?.Dispose(); }
            catch (Exception ex) { lock (_sync) { _state = WindowsEventWatchState.Failed; _notice = WindowsEventErrors.Describe(ex); } }
            if (query is not null && bookmark is not null)
            {
                try { _bookmarks.Save(query, bookmark); }
                catch (Exception ex) { lock (_sync) _notice = WindowsEventErrors.Describe(ex); }
            }
            Notify();
        });
        lock (_sync) _cleanup = cleanup;
        return cleanup;
    }

    private void Notify()
    {
        var listeners = Changed;
        if (listeners is null) return;
        foreach (Action callback in listeners.GetInvocationList())
            try { callback(); } catch { /* A closed UI subscriber cannot stop native cleanup. */ }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true; ++_generation; _state = WindowsEventWatchState.Stopped; Changed = null;
        }
        _shutdown.Cancel();
        var closing = Task.Run(async () =>
        {
            await _lifecycle.WaitAsync().ConfigureAwait(false);
            Task? cleanup;
            try { cleanup = StopCore(keepFailure: false); }
            finally { _lifecycle.Release(); }
            if (cleanup is not null) await cleanup.ConfigureAwait(false);
        });
        // Ordinary shutdown finishes synchronously; an unresponsive Windows call cannot
        // hold the UI thread. Its eventual completion is still owned by background cleanup.
        try { closing.Wait(TimeSpan.FromMilliseconds(250)); } catch { }
        GC.SuppressFinalize(this);
    }
}
