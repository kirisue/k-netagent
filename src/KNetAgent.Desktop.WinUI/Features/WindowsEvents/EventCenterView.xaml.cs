using Microsoft.UI.Xaml.Controls;

namespace KNetAgent.Desktop.WinUI.Features.WindowsEvents;

public sealed partial class EventCenterView : UserControl
{
    public EventCenterView(EventCenterViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
