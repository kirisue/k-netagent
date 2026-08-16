using System.Windows; using System.Windows.Input;
namespace TestAgent.Desktop;
public partial class MainWindow : Window
{
    public MainViewModel ViewModel { get; }
    public MainWindow(MainViewModel vm)
    {
        InitializeComponent(); ViewModel = vm; DataContext = vm;
        var backgroundTab = new System.Windows.Controls.TabItem { Header = "后台任务" };
        var panel = new System.Windows.Controls.DockPanel { Margin = new Thickness(8) };
        var toolbar = new System.Windows.Controls.StackPanel();
        toolbar.Children.Add(new System.Windows.Controls.TextBlock { Text = "非交互后台命令", FontWeight = FontWeights.Bold, FontSize = 15 });
        toolbar.Children.Add(new System.Windows.Controls.TextBlock { Text = "Agent 停止按钮不会终止后台进程；后台命令不接受交互输入。", TextWrapping = TextWrapping.Wrap });
        var status = new System.Windows.Controls.TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = System.Windows.Media.Brushes.Goldenrod };
        status.SetBinding(System.Windows.Controls.TextBlock.TextProperty, "BackgroundStatus"); toolbar.Children.Add(status);
        var buttons = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
        buttons.Children.Add(Button("刷新", vm.RefreshBackgroundCommandsCommand));
        buttons.Children.Add(Button("读取输出", vm.ReadBackgroundOutputCommand));
        buttons.Children.Add(Button("停止进程树", vm.StopBackgroundCommandCommand));
        toolbar.Children.Add(buttons); System.Windows.Controls.DockPanel.SetDock(toolbar, System.Windows.Controls.Dock.Top); panel.Children.Add(toolbar);
        var output = new System.Windows.Controls.TextBox { IsReadOnly = true, AcceptsReturn = true, FontFamily = new System.Windows.Media.FontFamily("Consolas"), Height = 180, VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto };
        output.SetBinding(System.Windows.Controls.TextBox.TextProperty, "BackgroundOutput"); System.Windows.Controls.DockPanel.SetDock(output, System.Windows.Controls.Dock.Bottom); panel.Children.Add(output);
        var list = new System.Windows.Controls.ListBox { DisplayMemberPath = "DisplayLabel" }; list.SetBinding(System.Windows.Controls.ItemsControl.ItemsSourceProperty, "BackgroundCommands"); list.SetBinding(System.Windows.Controls.Primitives.Selector.SelectedItemProperty, "SelectedBackgroundCommand"); panel.Children.Add(list);
        backgroundTab.Content = panel;
        Loaded += (_, _) =>
        {
            if (backgroundTab.Parent is null && FindVisualChild<System.Windows.Controls.TabControl>(this) is { } tabs)
                tabs.Items.Insert(Math.Max(0, tabs.Items.Count - 1), backgroundTab);
        };
    }
    private static System.Windows.Controls.Button Button(string text, ICommand command) => new() { Content = text, Command = command, Margin = new Thickness(0, 5, 5, 5) };
    private static T? FindVisualChild<T>(System.Windows.DependencyObject parent) where T : System.Windows.DependencyObject
    {
        for (var index = 0; index < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, index); if (child is T value) return value;
            if (FindVisualChild<T>(child) is { } nested) return nested;
        }
        return null;
    }
    private async void SaveSettings_Click(object sender, RoutedEventArgs e)
    {
        try { await ViewModel.SaveSettingsAsync(ApiKeyBox.Password); }
        catch(Exception ex){MessageBox.Show("设置保存失败："+ex.Message,"K.netagentV0.1",MessageBoxButton.OK,MessageBoxImage.Error);}
    }
    private void PromptBox_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter && Keyboard.Modifiers != ModifierKeys.Shift) { e.Handled = true; if (ViewModel.SendCommand.CanExecute(null)) ViewModel.SendCommand.Execute(null); } }
}
