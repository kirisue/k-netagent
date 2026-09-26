using KNetAgent.Desktop.WinUI.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TestAgent.Core;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace KNetAgent.Desktop.WinUI;

public sealed partial class McpPeersDialog : ContentDialog
{
    private readonly Window _owner;
    private string? _pendingDeletePeerId;

    public McpPeersDialog(ShellViewModel viewModel, Window owner)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));

        InitializeComponent();
        DataContext = ViewModel;
    }

    public ShellViewModel ViewModel { get; }

    private async void McpPeersDialog_Loaded(object sender, RoutedEventArgs e) =>
        await RunDialogOperationAsync(() => ViewModel.RefreshMcpPeersAsync());

    private async void AddCodex_Click(object sender, RoutedEventArgs e) =>
        await PickAndAddExecutablePeerAsync(McpPeerKind.Codex);

    private async void AddClaude_Click(object sender, RoutedEventArgs e) =>
        await PickAndAddExecutablePeerAsync(McpPeerKind.Claude);

    private async Task PickAndAddExecutablePeerAsync(McpPeerKind kind)
    {
        var picker = new FileOpenPicker
        {
            SuggestedStartLocation = PickerLocationId.Desktop
        };
        picker.FileTypeFilter.Add(".exe");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(_owner));

        var file = await picker.PickSingleFileAsync();
        if (file is null)
            return;

        await RunDialogOperationAsync(() => ViewModel.AddExecutableMcpPeerAsync(kind, file.Path));
    }

    private void ShowCustomHttpEditor_Click(object sender, RoutedEventArgs e)
    {
        McpOperationInfoBar.IsOpen = false;
        CustomHttpEditor.Visibility = Visibility.Visible;
        CustomHttpEndpointBox.Focus(FocusState.Programmatic);
    }

    private void HideCustomHttpEditor_Click(object sender, RoutedEventArgs e) =>
        CustomHttpEditor.Visibility = Visibility.Collapsed;

    private async void AddCustomHttp_Click(object sender, RoutedEventArgs e)
    {
        var added = await RunDialogOperationAsync(() =>
            ViewModel.AddCustomHttpMcpPeerAsync(CustomHttpNameBox.Text, CustomHttpEndpointBox.Text));
        if (!added)
            return;

        CustomHttpNameBox.Text = string.Empty;
        CustomHttpEndpointBox.Text = string.Empty;
        CustomHttpEditor.Visibility = Visibility.Collapsed;
    }

    private async void RefreshPeers_Click(object sender, RoutedEventArgs e) =>
        await RunDialogOperationAsync(() => ViewModel.RefreshMcpPeersAsync());

    private async void ConnectPeer_Click(object sender, RoutedEventArgs e) =>
        await RunDialogOperationAsync(() => ViewModel.ConnectSelectedMcpPeerAsync());

    private async void DisconnectPeer_Click(object sender, RoutedEventArgs e) =>
        await RunDialogOperationAsync(() => ViewModel.DisconnectSelectedMcpPeerAsync());

    private async void RefreshTools_Click(object sender, RoutedEventArgs e) =>
        await RunDialogOperationAsync(() => ViewModel.RefreshSelectedMcpPeerToolsAsync());

    private async void DeletePeer_Click(object sender, RoutedEventArgs e)
    {
        var selected = ViewModel.SelectedMcpPeer;
        if (selected is null)
            return;

        if (_pendingDeletePeerId != selected.Id)
        {
            _pendingDeletePeerId = selected.Id;
            McpOperationInfoBar.Severity = InfoBarSeverity.Warning;
            McpOperationInfoBar.Title = "确认删除配置";
            McpOperationInfoBar.Message = $"再次点击“删除配置”以删除 {selected.Name}；外部程序本身不会被删除。";
            McpOperationInfoBar.IsOpen = true;
            return;
        }

        _pendingDeletePeerId = null;
        await RunDialogOperationAsync(() => ViewModel.DeleteSelectedMcpPeerAsync());
    }

    private async void TogglePeerEnabled_Toggled(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleSwitch toggle ||
            ViewModel.SelectedMcpPeer is null ||
            toggle.IsOn == ViewModel.SelectedMcpPeerEnabled)
        {
            return;
        }

        await RunDialogOperationAsync(() => ViewModel.SetSelectedMcpPeerEnabledAsync(toggle.IsOn));
    }

    private async void ToggleAutomaticEscalation_Toggled(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleSwitch toggle ||
            ViewModel.SelectedMcpPeer is null ||
            toggle.IsOn == ViewModel.SelectedMcpPeerAllowAutomaticEscalation)
        {
            return;
        }

        var saved = await RunDialogOperationAsync(() =>
            ViewModel.SetSelectedMcpPeerAutomaticEscalationAsync(toggle.IsOn));
        if (!saved)
            toggle.IsOn = ViewModel.SelectedMcpPeerAllowAutomaticEscalation;
    }

    private async void ToggleToolAllowed_Toggled(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleSwitch
            {
                DataContext: McpPeerToolItemViewModel tool
            } toggle || toggle.IsOn == tool.IsAllowed)
        {
            return;
        }

        await RunDialogOperationAsync(() =>
            ViewModel.SetSelectedMcpPeerToolAllowedAsync(tool.Name, toggle.IsOn));
    }

    private async Task<bool> RunDialogOperationAsync(Func<Task> operation)
    {
        _pendingDeletePeerId = null;
        McpOperationInfoBar.Severity = InfoBarSeverity.Error;
        McpOperationInfoBar.Title = "外部 Agent 操作未完成";
        McpOperationInfoBar.IsOpen = false;
        try
        {
            await operation();
            return true;
        }
        catch (Exception exception)
        {
            McpOperationInfoBar.Message = exception.Message;
            McpOperationInfoBar.IsOpen = true;
            return false;
        }
    }
}
