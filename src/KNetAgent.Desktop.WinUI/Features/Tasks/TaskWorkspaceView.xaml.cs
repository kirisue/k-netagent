using Microsoft.UI.Xaml.Controls;

namespace KNetAgent.Desktop.WinUI.Features.Tasks;

public sealed partial class TaskWorkspaceView : UserControl
{
    public TaskWorkspaceView(TaskWorkspaceViewModel viewModel)
    {
        InitializeComponent();
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        DataContext = ViewModel;
    }

    public TaskWorkspaceViewModel ViewModel { get; }
}
