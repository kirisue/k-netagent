using Microsoft.UI.Xaml.Controls;

namespace KNetAgent.Desktop.WinUI.Features.WindowsServices;

public sealed partial class ServiceCenterView : UserControl
{
    public ServiceCenterView(ServiceCenterViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
