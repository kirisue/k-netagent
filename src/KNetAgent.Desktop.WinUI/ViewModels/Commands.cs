using System.Windows.Input;

namespace KNetAgent.Desktop.WinUI.ViewModels;

public abstract class CommandBase : ICommand
{
    public event EventHandler? CanExecuteChanged;

    public abstract bool CanExecute(object? parameter);

    public abstract void Execute(object? parameter);

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

public sealed class RelayCommand(Action action, Func<bool>? canExecute = null) : CommandBase
{
    public override bool CanExecute(object? parameter) => canExecute?.Invoke() ?? true;

    public override void Execute(object? parameter) => action();
}

public sealed class AsyncRelayCommand : CommandBase
{
    private readonly Func<Task> _action;
    private readonly Func<bool>? _canExecute;
    private readonly Func<Exception, Task>? _onError;
    private bool _isRunning;

    public AsyncRelayCommand(
        Func<Task> action,
        Func<bool>? canExecute = null,
        Func<Exception, Task>? onError = null)
    {
        _action = action ?? throw new ArgumentNullException(nameof(action));
        _canExecute = canExecute;
        _onError = onError;
    }

    public bool IsRunning => _isRunning;

    public override bool CanExecute(object? parameter) => !_isRunning && (_canExecute?.Invoke() ?? true);

    public async Task ExecuteAsync()
    {
        if (!CanExecute(null))
            return;

        _isRunning = true;
        RaiseCanExecuteChanged();
        try
        {
            await _action().ConfigureAwait(true);
        }
        catch (Exception ex) when (_onError is not null)
        {
            await _onError(ex).ConfigureAwait(true);
        }
        finally
        {
            _isRunning = false;
            RaiseCanExecuteChanged();
        }
    }

    public override async void Execute(object? parameter) => await ExecuteAsync();
}
