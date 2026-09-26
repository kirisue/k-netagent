namespace KNetAgent.Desktop.WinUI.Services;

/// <summary>
/// Keeps ViewModels independent from Microsoft.UI while ensuring that observable
/// collections and properties are changed on the WinUI thread.
/// </summary>
public interface IUiDispatcher
{
    bool HasThreadAccess { get; }

    void Post(Action action);

    Task InvokeAsync(Action action, CancellationToken cancellationToken = default);
}

/// <summary>
/// Production adapter. Construct it on the WinUI thread after the app has started.
/// WinUI installs a SynchronizationContext backed by its DispatcherQueue.
/// </summary>
public sealed class SynchronizationContextUiDispatcher : IUiDispatcher
{
    private readonly SynchronizationContext _context;
    private readonly int _threadId;

    public SynchronizationContextUiDispatcher()
        : this(SynchronizationContext.Current
               ?? throw new InvalidOperationException(
                   "The UI dispatcher must be created on the WinUI thread after startup."))
    {
    }

    public SynchronizationContextUiDispatcher(SynchronizationContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _threadId = Environment.CurrentManagedThreadId;
    }

    public bool HasThreadAccess => Environment.CurrentManagedThreadId == _threadId;

    public void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        _context.Post(static state => ((Action)state!).Invoke(), action);
    }

    public Task InvokeAsync(Action action, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        cancellationToken.ThrowIfCancellationRequested();

        if (HasThreadAccess)
        {
            action();
            return Task.CompletedTask;
        }

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _context.Post(static state =>
        {
            var work = (DispatchWork)state!;
            if (work.CancellationToken.IsCancellationRequested)
            {
                work.Completion.TrySetCanceled(work.CancellationToken);
                return;
            }

            try
            {
                work.Action();
                work.Completion.TrySetResult();
            }
            catch (Exception ex)
            {
                work.Completion.TrySetException(ex);
            }
        }, new DispatchWork(action, completion, cancellationToken));

        return completion.Task;
    }

    private sealed record DispatchWork(
        Action Action,
        TaskCompletionSource Completion,
        CancellationToken CancellationToken);
}

/// <summary>Deterministic dispatcher for headless ViewModel tests.</summary>
public sealed class InlineUiDispatcher : IUiDispatcher
{
    public bool HasThreadAccess => true;

    public void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        action();
    }

    public Task InvokeAsync(Action action, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        cancellationToken.ThrowIfCancellationRequested();
        action();
        return Task.CompletedTask;
    }
}
