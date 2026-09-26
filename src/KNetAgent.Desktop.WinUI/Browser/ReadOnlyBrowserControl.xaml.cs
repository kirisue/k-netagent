using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace KNetAgent.Desktop.WinUI.Browser;

/// <summary>Embeddable visible browser slice. It can only render session-provided local snapshots.</summary>
public sealed partial class ReadOnlyBrowserControl : UserControl
{
    private BrowserFeatureViewModel? _viewModel;

    public ReadOnlyBrowserControl() => InitializeComponent();

    public void Attach(BrowserFeatureViewModel viewModel, WinUiReadOnlyBrowserSession session,
        TestAgent.Core.IAgentObserver observer, Func<string?> sessionIdAccessor)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(observer);
        ArgumentNullException.ThrowIfNull(sessionIdAccessor);
        DataContext = viewModel;
        viewModel.Initialize(observer, sessionIdAccessor);
        session.Attach(SafeBrowserView);
    }

    private async void Open_Click(object sender, RoutedEventArgs e) =>
        await RunAsync(() => _viewModel?.OpenAsync() ?? Task.CompletedTask);

    private async void ReadDom_Click(object sender, RoutedEventArgs e) =>
        await RunAsync(() => _viewModel?.ReadDomAsync() ?? Task.CompletedTask);

    private async void Capture_Click(object sender, RoutedEventArgs e) =>
        await RunAsync(() => _viewModel?.CaptureAndAttachAsync() ?? Task.CompletedTask);

    private async Task RunAsync(Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex)
        {
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "只读浏览器",
                Content = new TextBlock { Text = ex.Message, TextWrapping = TextWrapping.Wrap },
                CloseButtonText = "关闭"
            };
            await dialog.ShowAsync();
        }
    }
}
