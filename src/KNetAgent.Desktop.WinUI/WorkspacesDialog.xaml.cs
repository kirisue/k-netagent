using KNetAgent.Desktop.WinUI.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TestAgent.Infrastructure;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace KNetAgent.Desktop.WinUI;

public enum WorkspaceDialogStartMode
{
    Overview,
    Create,
    Open
}

public sealed partial class WorkspacesDialog : ContentDialog
{
    private readonly Window _owner;
    private readonly WorkspaceDialogStartMode _startMode;
    private bool _busy;

    public WorkspacesDialog(
        ShellViewModel viewModel,
        Window owner,
        WorkspaceDialogStartMode startMode = WorkspaceDialogStartMode.Overview)
    {
        ViewModel = viewModel;
        _owner = owner;
        _startMode = startMode;
        InitializeComponent();
        DataContext = ViewModel;

        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        ParentFolderBox.Text = Directory.Exists(documents)
            ? documents
            : Path.GetDirectoryName(ViewModel.CurrentWorkspacePath) ?? ViewModel.CurrentWorkspacePath;
    }

    public ShellViewModel ViewModel { get; }

    private async void WorkspacesDialog_Loaded(object sender, RoutedEventArgs e)
    {
        await RunAsync(() => ViewModel.RefreshWorkspacesAsync());
        if (_startMode == WorkspaceDialogStartMode.Create)
        {
            ProjectNameBox.Focus(FocusState.Programmatic);
            return;
        }

        if (_startMode == WorkspaceDialogStartMode.Open)
        {
            await Task.Yield();
            await PickAndOpenWorkspaceAsync();
        }
    }

    private async void OpenFolder_Click(object sender, RoutedEventArgs e) =>
        await PickAndOpenWorkspaceAsync();

    private async void BrowseParentFolder_Click(object sender, RoutedEventArgs e)
    {
        await RunAsync(async () =>
        {
            var folder = await PickFolderAsync(PickerLocationId.DocumentsLibrary);
            if (folder is not null)
                ParentFolderBox.Text = folder.Path;
        });
    }

    private async void CreateWorkspace_Click(object sender, RoutedEventArgs e)
    {
        await RunAsync(() => ViewModel.CreateWorkspaceAsync(
            ParentFolderBox.Text,
            ProjectNameBox.Text));
    }

    private async void OpenRecentWorkspace_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: WorkspaceEntry workspace })
            await RunAsync(() => ViewModel.OpenWorkspaceAsync(workspace.Root));
    }

    private async void ForgetWorkspace_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: WorkspaceEntry workspace })
            await RunAsync(() => ViewModel.ForgetWorkspaceAsync(workspace));
    }

    private async void RefreshWorkspaces_Click(object sender, RoutedEventArgs e) =>
        await RunAsync(() => ViewModel.RefreshWorkspacesAsync());

    private async Task PickAndOpenWorkspaceAsync()
    {
        await RunAsync(async () =>
        {
            await ViewModel.EnsureWorkspaceSwitchAllowedAsync();
            var folder = await PickFolderAsync(PickerLocationId.ComputerFolder);
            if (folder is not null)
                await ViewModel.OpenWorkspaceAsync(folder.Path);
        });
    }

    private async Task<Windows.Storage.StorageFolder?> PickFolderAsync(PickerLocationId location)
    {
        var picker = new FolderPicker
        {
            SuggestedStartLocation = location
        };
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(_owner));
        return await picker.PickSingleFolderAsync();
    }

    private async Task RunAsync(Func<Task> action)
    {
        if (_busy)
            return;

        SetBusy(true);
        WorkspaceErrorBar.IsOpen = false;
        try
        {
            await action();
        }
        catch (Exception exception)
        {
            ShowError(exception.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void SetBusy(bool value)
    {
        _busy = value;
        WorkspaceDialogRoot.IsHitTestVisible = !value;
        WorkspaceDialogRoot.Opacity = value ? 0.72 : 1;
        IsPrimaryButtonEnabled = !value;
    }

    private void ShowError(string message)
    {
        WorkspaceErrorBar.Message = message;
        WorkspaceErrorBar.IsOpen = true;
    }
}
