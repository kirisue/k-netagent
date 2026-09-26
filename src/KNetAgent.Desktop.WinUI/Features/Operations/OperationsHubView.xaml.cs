using Microsoft.UI.Xaml.Controls;

namespace KNetAgent.Desktop.WinUI.Features.Operations;

public sealed partial class OperationsHubView : UserControl
{
    public OperationsHubView(OperationsHubViewModel viewModel)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
        DataContext = ViewModel;
    }

    public OperationsHubViewModel ViewModel { get; }
}
