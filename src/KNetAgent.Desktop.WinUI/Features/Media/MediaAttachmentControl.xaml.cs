using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinRT.Interop;

namespace KNetAgent.Desktop.WinUI.Features.Media;

/// <summary>Embeddable composer slice. Call Attach once from the owning window.</summary>
public sealed partial class MediaAttachmentControl : UserControl
{
    private MediaAttachmentViewModel? _viewModel;
    private Window? _owner;

    public MediaAttachmentControl() => InitializeComponent();

    public void Attach(MediaAttachmentViewModel viewModel, Window owner)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        DataContext = viewModel;
        if (XamlRoot is not null) viewModel.AttachConfirmationRoot(XamlRoot);
    }

    private void Control_Loaded(object sender, RoutedEventArgs e)
    {
        if (_viewModel is not null && XamlRoot is not null)
            _viewModel.AttachConfirmationRoot(XamlRoot);
    }

    private void Control_Unloaded(object sender, RoutedEventArgs e) =>
        _viewModel?.DetachConfirmationRoot();

    private async void Pick_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null || _owner is null) return;
        try
        {
            await _viewModel.PickAsync(WindowNative.GetWindowHandle(_owner));
        }
        catch (Exception ex)
        {
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "无法附加图片",
                Content = new TextBlock { Text = ex.Message, TextWrapping = TextWrapping.Wrap },
                CloseButtonText = "关闭"
            };
            await dialog.ShowAsync();
        }
    }

    private void Clear_Click(object sender, RoutedEventArgs e) => _viewModel?.Clear();
}
