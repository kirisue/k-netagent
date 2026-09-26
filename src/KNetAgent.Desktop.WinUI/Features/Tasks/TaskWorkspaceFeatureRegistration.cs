using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.UI.Xaml;
using KNetAgent.Desktop.WinUI.Services;
using TestAgent.Core;
using TestAgent.Infrastructure;
using Windows.Graphics;

namespace KNetAgent.Desktop.WinUI.Features.Tasks;

public static class TaskWorkspaceFeatureRegistration
{
    public static IServiceCollection AddWinUiTaskWorkspaceFeature(this IServiceCollection services)
    {
        services.TryAddSingleton<ITaskPlanStore, JsonTaskPlanStore>();
        services.TryAddSingleton<IProjectScanner, BoundedProjectScanner>();
        services.TryAddSingleton<ITaskWorkflowService, SingleAgentTaskWorkflowService>();
        services.TryAddSingleton<IWorkspaceExplorer, BoundedWorkspaceExplorer>();
        services.TryAddSingleton<IWorkspaceChangeSource, GitOrApprovedToolWorkspaceChangeSource>();
        services.TryAddSingleton<TaskWorkspaceViewModel>();
        services.TryAddSingleton<TaskWorkspaceFeatureHost>();
        return services;
    }
}

/// <summary>
/// Uses a separate Window so per-tool approval ContentDialogs can be hosted while a task runs.
/// </summary>
public sealed class TaskWorkspaceFeatureHost(
    TaskWorkspaceViewModel viewModel,
    IUiDispatcher dispatcher,
    WinUiUserInteractionService interaction) : IDisposable
{
    private int _showing;
    private bool _disposed;
    private Window? _window;

    public TaskWorkspaceViewModel ViewModel { get; } = viewModel;

    public async Task ShowAsync(XamlRoot xamlRoot, IAgentObserver observer,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(xamlRoot);
        ArgumentNullException.ThrowIfNull(observer);
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (Interlocked.Exchange(ref _showing, 1) != 0)
        {
            _window?.Activate();
            return;
        }
        try
        {
            await ViewModel.InitializeAsync(new ApprovalOnlyObserver(observer), cancellationToken);
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var view = new TaskWorkspaceView(ViewModel);
            var window = new Window
            {
                Title = "任务、文件与真实变更",
                Content = view
            };
            _window = window;
            view.Loaded += (_, _) => interaction.Attach(view.XamlRoot);
            window.Closed += async (_, _) =>
            {
                ViewModel.CancelActiveOperation();
                await ViewModel.WaitForIdleAsync();
                interaction.Attach(xamlRoot);
                _window = null;
                Interlocked.Exchange(ref _showing, 0);
                completion.TrySetResult();
            };
            window.Activate();
            window.AppWindow.Resize(new SizeInt32(1100, 760));
            using var registration = cancellationToken.Register(() =>
                dispatcher.Post(() => window.Close()));
            await completion.Task;
        }
        catch
        {
            ViewModel.CancelActiveOperation();
            interaction.Attach(xamlRoot);
            Interlocked.Exchange(ref _showing, 0);
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _window?.Close();
        ViewModel.CancelActiveOperation();
    }

    private sealed class ApprovalOnlyObserver(IAgentObserver inner) : IAgentObserver
    {
        public ValueTask OnStateAsync(AgentState state) => ValueTask.CompletedTask;
        public ValueTask OnEventAsync(StreamEvent value) => ValueTask.CompletedTask;
        public ValueTask<bool> RequestToolApprovalAsync(
            ToolApprovalRequest request, CancellationToken ct) =>
            inner.RequestToolApprovalAsync(request, ct);
    }
}
