using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.UI.Xaml;
using KNetAgent.Desktop.WinUI.Services;
using TestAgent.Core;
using TestAgent.Infrastructure;
using Windows.Graphics;

namespace KNetAgent.Desktop.WinUI.Features.Operations;

/// <summary>
/// Registers the optional operations surface without making the shell depend on its views.
/// The application continues to own the existing bridge and background-command lifetimes.
/// </summary>
public static class OperationsFeatureRegistration
{
    public static IServiceCollection AddWinUiOperationsFeatures(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(SelfIterationWorkspace.Discover());
        services.TryAddSingleton<IIterationGuideStore, SelfIterationGuideStore>();
        services.TryAddSingleton<ICodeIterationService, SelfCodeIterationService>();
        services.TryAddSingleton<IPairingClipboard, WinUiPairingClipboard>();
        services.TryAddSingleton<VsCodeBridgeOperationsViewModel>();
        services.TryAddSingleton<BackgroundJobsOperationsViewModel>();
        services.TryAddSingleton<CodeIterationOperationsViewModel>();
        services.TryAddSingleton<OperationsHubViewModel>();
        services.TryAddSingleton<OperationsFeatureHost>();
        return services;
    }
}

/// <summary>
/// Small host boundary used by MainWindow: inject it, then call ShowAsync with the
/// current XamlRoot. Operations use a separate Window rather than a ContentDialog so
/// per-operation approval ContentDialogs can still be shown on that Window.
/// </summary>
public sealed class OperationsFeatureHost(
    OperationsHubViewModel viewModel,
    IUiDispatcher dispatcher,
    WinUiUserInteractionService interaction) : IDisposable
{
    private int _showing;
    private bool _disposed;
    private Window? _window;

    public OperationsHubViewModel ViewModel { get; } = viewModel;

    public async Task ShowAsync(
        XamlRoot xamlRoot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(xamlRoot);
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (Interlocked.Exchange(ref _showing, 1) != 0)
        {
            _window?.Activate();
            return;
        }

        try
        {
            await ViewModel.InitializeAsync(cancellationToken);
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var view = new OperationsHubView(ViewModel);
            var window = new Window
            {
                Title = "项目运维",
                Content = view
            };
            _window = window;
            view.Loaded += (_, _) => interaction.Attach(view.XamlRoot);
            window.Closed += async (_, _) =>
            {
                try
                {
                    ViewModel.CancelActiveOperation();
                    await ViewModel.WaitForIdleAsync();
                    await ViewModel.VsCode.ClearPairingClipboardAsync(CancellationToken.None);
                }
                finally
                {
                    interaction.Attach(xamlRoot);
                    _window = null;
                    Interlocked.Exchange(ref _showing, 0);
                    completion.TrySetResult();
                }
            };
            window.Activate();
            window.AppWindow.Resize(new SizeInt32(920, 760));

            using var registration = cancellationToken.Register(() =>
                dispatcher.Post(() => window.Close()));
            await completion.Task;
        }
        catch
        {
            ViewModel.CancelActiveOperation();
            await ViewModel.VsCode.ClearPairingClipboardAsync(CancellationToken.None);
            Interlocked.Exchange(ref _showing, 0);
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _window?.Close();
        ViewModel.Dispose();
    }
}
