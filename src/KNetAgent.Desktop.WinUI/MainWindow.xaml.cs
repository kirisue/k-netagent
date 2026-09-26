using Microsoft.UI.Xaml;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Composition.SystemBackdrops;
using Windows.System;
using Windows.UI.Core;
using Windows.Graphics;
using System.Reflection;
using KNetAgent.Desktop.WinUI.Browser;
using KNetAgent.Desktop.WinUI.Features.Media;
using KNetAgent.Desktop.WinUI.Features.Operations;
using KNetAgent.Desktop.WinUI.Features.Tasks;
using KNetAgent.Desktop.WinUI.Features.WindowsEvents;
using KNetAgent.Desktop.WinUI.Features.WindowsServices;
using KNetAgent.Desktop.WinUI.Services;
using KNetAgent.Desktop.WinUI.ViewModels;

namespace KNetAgent.Desktop.WinUI;

public sealed partial class MainWindow : Window
{
    private readonly WinUiUserInteractionService _interaction;
    private readonly TaskWorkspaceFeatureHost _taskWorkspace;
    private readonly OperationsFeatureHost _operations;
    private readonly WindowsEventCenterHost _eventCenter;
    private readonly WindowsServiceCenterHost _serviceCenter;
    private readonly TaskCompletionSource _shellLoaded = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _isSettingsDialogOpen;
    private bool _isMcpPeersDialogOpen;
    private bool _isWorkspacesDialogOpen;
    private bool _isFeatureWindowOpen;

    public MainWindow(
        ShellViewModel viewModel,
        WinUiUserInteractionService interaction,
        TaskWorkspaceFeatureHost taskWorkspace,
        OperationsFeatureHost operations,
        WindowsEventCenterHost eventCenter,
        WindowsServiceCenterHost serviceCenter,
        MediaAttachmentViewModel media,
        BrowserFeatureViewModel browserViewModel,
        WinUiReadOnlyBrowserSession browserSession)
    {
        ViewModel = viewModel;
        _interaction = interaction;
        _taskWorkspace = taskWorkspace;
        _operations = operations;
        _eventCenter = eventCenter;
        _serviceCenter = serviceCenter;
        InitializeComponent();
        Title = "K.netagent · " + AppVersionLabel;
        ShellRoot.DataContext = ViewModel;
        MediaControl.Attach(media, this);
        BrowserControl.Attach(browserViewModel, browserSession, ViewModel,
            () => ViewModel.SelectedSession?.Id);
        ShellRoot.Loaded += ShellRoot_Loaded;
        ShellRoot.SizeChanged += ShellRoot_SizeChanged;
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        SystemBackdrop = new MicaBackdrop
        {
            Kind = MicaKind.BaseAlt
        };

        AppWindow.Resize(new SizeInt32(1360, 840));
        Closed += MainWindow_Closed;
    }

    public ShellViewModel ViewModel { get; }
    public string AppVersionLabel => typeof(MainWindow).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "开发版";

    private void ShellRoot_Loaded(object sender, RoutedEventArgs e)
    {
        if (ShellRoot.XamlRoot is not null)
            _interaction.Attach(ShellRoot.XamlRoot);
        _shellLoaded.TrySetResult();
    }

    private void ShellRoot_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        // The compact inspector temporarily occupies the whole content area. Restore
        // the conversation column as soon as the window leaves that narrow mode.
        if (e.NewSize.Width < 700 || ConversationColumn.Width.Value != 0)
            return;

        ConversationColumn.MinWidth = 360;
        ConversationColumn.Width = new GridLength(1, GridUnitType.Star);
    }

    private void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        _interaction.Detach();
        ViewModel.Dispose();
    }

    private void OpenNavigation_Click(object sender, RoutedEventArgs e)
    {
        LeftRail.Visibility = Visibility.Visible;
        LeftRailColumn.Width = new GridLength(ShellRoot.ActualWidth >= 1180 ? 252 : 224);
    }

    private void CloseNavigation_Click(object sender, RoutedEventArgs e)
    {
        LeftRail.Visibility = Visibility.Collapsed;
        LeftRailColumn.Width = new GridLength(0);
    }

    private void ToggleNavigation_Click(object sender, RoutedEventArgs e)
    {
        if (LeftRail.Visibility == Visibility.Visible && LeftRailColumn.Width.Value > 0)
            CloseNavigation_Click(sender, e);
        else
            OpenNavigation_Click(sender, e);
    }

    private void OpenInspector_Click(object sender, RoutedEventArgs e)
    {
        ShowInspector();
    }

    private void OpenInspectorTab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string tabId })
            return;

        var selected = ViewModel.InspectorTabs.FirstOrDefault(item => item.Id == tabId);
        if (selected is null)
            return;

        ViewModel.SelectedInspectorTab = selected;
        InspectorTabsControl.SelectedIndex = InspectorIndex(tabId);
        ShowInspector();
    }

    private async void OpenSettings_Click(object sender, RoutedEventArgs e)
    {
        if (_isFeatureWindowOpen || _isSettingsDialogOpen || ShellRoot.XamlRoot is null)
            return;

        _isSettingsDialogOpen = true;
        try
        {
            var dialog = new SettingsDialog(ViewModel)
            {
                XamlRoot = ShellRoot.XamlRoot
            };
            await dialog.ShowAsync();
        }
        finally
        {
            _isSettingsDialogOpen = false;
        }
    }

    private async void OpenMcpPeers_Click(object sender, RoutedEventArgs e)
    {
        if (_isFeatureWindowOpen || _isMcpPeersDialogOpen || ShellRoot.XamlRoot is null)
            return;

        _isMcpPeersDialogOpen = true;
        try
        {
            var dialog = new McpPeersDialog(ViewModel, this)
            {
                XamlRoot = ShellRoot.XamlRoot
            };
            await dialog.ShowAsync();
        }
        finally
        {
            _isMcpPeersDialogOpen = false;
        }
    }

    private async void OpenWorkspaces_Click(object sender, RoutedEventArgs e) =>
        await ShowWorkspacesDialogAsync(WorkspaceDialogStartMode.Overview);

    private async void OpenTaskWorkspace_Click(object sender, RoutedEventArgs e)
    {
        if (_isFeatureWindowOpen || ViewModel.Busy || ViewModel.Media.Busy || ShellRoot.XamlRoot is null) return;
        _isFeatureWindowOpen = true;
        MainInteractiveContent.IsEnabled = false;
        try { await _taskWorkspace.ShowAsync(ShellRoot.XamlRoot, ViewModel); }
        finally
        {
            MainInteractiveContent.IsEnabled = true;
            _isFeatureWindowOpen = false;
        }
    }

    private async void OpenOperations_Click(object sender, RoutedEventArgs e)
    {
        if (_isFeatureWindowOpen || ViewModel.Busy || ViewModel.Media.Busy || ShellRoot.XamlRoot is null) return;
        _isFeatureWindowOpen = true;
        MainInteractiveContent.IsEnabled = false;
        try { await _operations.ShowAsync(ShellRoot.XamlRoot); }
        finally
        {
            MainInteractiveContent.IsEnabled = true;
            _isFeatureWindowOpen = false;
        }
    }

    private async void OpenWindowsEvents_Click(object sender, RoutedEventArgs e) => await ShowWindowsEventsAsync();

    private async Task ShowWindowsEventsAsync()
    {
        if (_isFeatureWindowOpen || _isSettingsDialogOpen || _isMcpPeersDialogOpen ||
            _isWorkspacesDialogOpen || ShellRoot.XamlRoot is null) return;
        if (ViewModel.Busy || ViewModel.Media.Busy)
        {
            await _interaction.NotifyAsync("Windows 事件中心", "请先停止当前生成或等待图片加载完成。");
            return;
        }
        _isFeatureWindowOpen = true;
        MainInteractiveContent.IsEnabled = false;
        try
        {
            await _eventCenter.ShowAsync(ShellRoot.XamlRoot, evidence =>
            {
                ViewModel.StageWindowsEventEvidence(evidence);
                BrowserArea.Visibility = Visibility.Collapsed;
                ConversationArea.Visibility = Visibility.Visible;
                return Task.CompletedTask;
            });
        }
        catch (Exception)
        {
            await _interaction.NotifyAsync("Windows 事件中心", "事件窗口未能打开，请重试。");
        }
        finally
        {
            MainInteractiveContent.IsEnabled = true;
            _isFeatureWindowOpen = false;
        }
    }

    private async void OpenWindowsServices_Click(object sender, RoutedEventArgs e) => await ShowWindowsServicesAsync();

    private async Task ShowWindowsServicesAsync()
    {
        if (_isFeatureWindowOpen || _isSettingsDialogOpen || _isMcpPeersDialogOpen || _isWorkspacesDialogOpen ||
            ViewModel.Busy || ViewModel.Media.Busy || ShellRoot.XamlRoot is null) return;
        _isFeatureWindowOpen = true;
        MainInteractiveContent.IsEnabled = false;
        try
        {
            await _serviceCenter.ShowAsync(ShellRoot.XamlRoot, evidence =>
            {
                ViewModel.StageWindowsServiceEvidence(evidence);
                BrowserArea.Visibility = Visibility.Collapsed;
                ConversationArea.Visibility = Visibility.Visible;
                return Task.CompletedTask;
            });
        }
        catch (Exception)
        { await _interaction.NotifyAsync("Windows 服务状态", "服务状态窗口未能打开，请重试。"); }
        finally
        {
            MainInteractiveContent.IsEnabled = true;
            _isFeatureWindowOpen = false;
        }
    }

    public async Task OpenDiagnosticsPageAsync(string page)
    {
        await _shellLoaded.Task.WaitAsync(TimeSpan.FromSeconds(10));
        switch (page)
        {
            case "events": await ShowWindowsEventsAsync(); break;
            case "services": await ShowWindowsServicesAsync(); break;
            default: throw new ArgumentException("Unknown diagnostic page.", nameof(page));
        }
    }

    private void OpenBrowser_Click(object sender, RoutedEventArgs e)
    {
        if (_isFeatureWindowOpen) return;
        ConversationArea.Visibility = Visibility.Collapsed;
        BrowserArea.Visibility = Visibility.Visible;
    }

    private void BackToChat_Click(object sender, RoutedEventArgs e)
    {
        BrowserArea.Visibility = Visibility.Collapsed;
        ConversationArea.Visibility = Visibility.Visible;
    }

    private async void CreateWorkspace_Click(object sender, RoutedEventArgs e) =>
        await ShowWorkspacesDialogAsync(WorkspaceDialogStartMode.Create);

    private async void OpenWorkspaceFolder_Click(object sender, RoutedEventArgs e) =>
        await ShowWorkspacesDialogAsync(WorkspaceDialogStartMode.Open);

    private async Task ShowWorkspacesDialogAsync(WorkspaceDialogStartMode startMode)
    {
        if (_isFeatureWindowOpen || _isWorkspacesDialogOpen || ShellRoot.XamlRoot is null)
            return;

        _isWorkspacesDialogOpen = true;
        try
        {
            var dialog = new WorkspacesDialog(ViewModel, this, startMode)
            {
                XamlRoot = ShellRoot.XamlRoot
            };
            await dialog.ShowAsync();
        }
        finally
        {
            _isWorkspacesDialogOpen = false;
        }
    }

    private void CloseInspector_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.IsInspectorOpen = false;
        InspectorPanel.Visibility = Visibility.Collapsed;
        InspectorColumn.Width = new GridLength(0);
        ConversationColumn.MinWidth = 360;
        ConversationColumn.Width = new GridLength(1, GridUnitType.Star);
    }

    private void ShowInspector()
    {
        ViewModel.IsInspectorOpen = true;
        InspectorPanel.Visibility = Visibility.Visible;

        var availableWidth = ShellRoot.ActualWidth;
        if (availableWidth > 0 && availableWidth < 1180)
        {
            LeftRail.Visibility = Visibility.Collapsed;
            LeftRailColumn.Width = new GridLength(0);
        }

        if (availableWidth > 0 && availableWidth < 700)
        {
            ConversationColumn.MinWidth = 0;
            ConversationColumn.Width = new GridLength(0);
            InspectorColumn.Width = new GridLength(1, GridUnitType.Star);
            return;
        }

        ConversationColumn.MinWidth = 360;
        ConversationColumn.Width = new GridLength(1, GridUnitType.Star);
        InspectorColumn.Width = new GridLength(320);
    }

    private void Composer_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter)
            return;

        var shiftState = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift);
        if ((shiftState & CoreVirtualKeyStates.Down) != 0)
            return;

        if (ViewModel.SendCommand.CanExecute(null))
            ViewModel.SendCommand.Execute(null);
        e.Handled = true;
    }

    private void InspectorTabSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ViewModel.SelectedInspectorTab is not { } selected)
            return;

        InspectorTabsControl.SelectedIndex = InspectorIndex(selected.Id);
    }

    private void InspectorContent_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var index = InspectorTabsControl.SelectedIndex;
        if (index < 0 || index >= ViewModel.InspectorTabs.Count)
            return;

        ViewModel.SelectedInspectorTab = ViewModel.InspectorTabs[index];
    }

    private static int InspectorIndex(string id) => id switch
    {
        "plan" => 0,
        "files" => 1,
        "tools" => 2,
        "memory" => 3,
        "context" => 4,
        _ => 2
    };
}
