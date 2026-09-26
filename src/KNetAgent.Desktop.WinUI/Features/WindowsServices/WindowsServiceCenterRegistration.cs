using KNetAgent.Desktop.WinUI.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace KNetAgent.Desktop.WinUI.Features.WindowsServices;

public static class WindowsServiceCenterRegistration
{
    public static IServiceCollection AddWinUiWindowsServiceCenter(this IServiceCollection services)
    {
        services.TryAddSingleton<ServiceCenterViewModel>();
        services.TryAddSingleton<WindowsServiceCenterHost>();
        return services;
    }
}

public sealed class WindowsServiceCenterHost(ServiceCenterViewModel viewModel, IUiDispatcher dispatcher) : IDisposable
{
    private Window? _window;
    private Task? _showTask;
    private bool _disposed;
    public ServiceCenterViewModel ViewModel { get; } = viewModel;

    public Task ShowAsync(XamlRoot xamlRoot, Func<string, Task> stageEvidence, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (_showTask is { IsCompleted: false }) { _window?.Activate(); return _showTask; }
        _showTask = ShowCoreAsync(xamlRoot, stageEvidence, cancellationToken);
        return _showTask;
    }

    public Task CloseAsync() { _window?.Close(); return _showTask ?? Task.CompletedTask; }

    private async Task ShowCoreAsync(XamlRoot xamlRoot, Func<string, Task> stageEvidence, CancellationToken ct)
    {
        ViewModel.Open(async text => { await stageEvidence(text); dispatcher.Post(() => _window?.Close()); });
        try
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var view = new ServiceCenterView(ViewModel)
            { RequestedTheme = (xamlRoot.Content as FrameworkElement)?.ActualTheme ?? ElementTheme.Default };
            var window = new Window { Title = "Windows 服务状态 · K.netagent", Content = view };
            _window = window;
            window.Closed += async (_, _) =>
            {
                try { await ViewModel.CloseAsync(); }
                catch { /* Closing a window must not surface an async-void exception. */ }
                finally { _window = null; completion.TrySetResult(); }
            };
            window.Activate();
            window.AppWindow.Resize(new SizeInt32(1000, 780));
            using var registration = ct.Register(() => dispatcher.Post(() => window.Close()));
            await completion.Task;
        }
        catch { await ViewModel.CloseAsync(); _window = null; throw; }
    }

    public void Dispose() { if (_disposed) return; _disposed = true; _window?.Close(); }
}
