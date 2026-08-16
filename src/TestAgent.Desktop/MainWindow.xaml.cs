using System.Security.Cryptography; using System.Text; using System.Text.Json; using System.Windows; using System.Windows.Input; using TestAgent.Infrastructure;
namespace TestAgent.Desktop;
public partial class MainWindow : Window
{
    private readonly IVsCodeBridgeClient _vsCodeBridge;
    private PairingClipboardLease? _pairingClipboard;
    public MainViewModel ViewModel { get; }
    public MainWindow(MainViewModel vm, WpfReadOnlyBrowserSession browser, IVsCodeBridgeClient vsCodeBridge)
    {
        InitializeComponent(); browser.Attach(SafeBrowserView); ViewModel = vm; DataContext = vm; _vsCodeBridge=vsCodeBridge;
        _vsCodeBridge.Changed+=VsCodeBridge_Changed;Closed+=MainWindow_Closed;UpdateVsCodeBridgeStatus();
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
            if (backgroundTab.Parent is null)
                ManagementTabs.Items.Insert(Math.Max(0, ManagementTabs.Items.Count - 1), backgroundTab);
        };
    }
    private static System.Windows.Controls.Button Button(string text, ICommand command) => new() { Content = text, Command = command, Margin = new Thickness(0, 5, 5, 5) };
    private async void SaveSettings_Click(object sender, RoutedEventArgs e)
    {
        try { await ViewModel.SaveSettingsAsync(ApiKeyBox.Password); }
        catch(Exception ex){MessageBox.Show("设置保存失败："+ex.Message,"K.netagentV0.1",MessageBoxButton.OK,MessageBoxImage.Error);}
    }
    private async void AttachImage_Click(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.CanAttachImage) return;
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择要发送给模型的图片",
            Filter = "支持的图片 (*.png;*.jpg;*.jpeg)|*.png;*.jpg;*.jpeg",
            Multiselect = false,
            CheckFileExists = true,
            AddToRecent = false,
            DereferenceLinks = false
        };
        if (dialog.ShowDialog(this) != true) return;
        try { await ViewModel.AttachImageAsync(dialog.FileName); }
        catch(Exception ex)
        {
            MessageBox.Show("图片无法使用："+ex.Message,"K.netagentV0.1",MessageBoxButton.OK,MessageBoxImage.Error);
        }
    }
    private async void CopyVsCodePairing_Click(object sender, RoutedEventArgs e)
    {
        byte[]? digest=null;
        try
        {
            var pairingJson=JsonSerializer.Serialize(_vsCodeBridge.GetPairingInfo());
            digest=HashClipboardText(pairingJson);
            Clipboard.SetText(pairingJson,TextDataFormat.UnicodeText);
        }
        catch
        {
            if(digest is not null)CryptographicOperations.ZeroMemory(digest);
            VsCodeBridgeStatusText.Text="无法访问系统剪贴板；没有复制或显示配对密钥。";
            return;
        }

        var lease=new PairingClipboardLease(digest);digest=null;
        var previous=_pairingClipboard;_pairingClipboard=lease;previous?.Dispose();UpdateVsCodeBridgeStatus();
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(75),lease.Token);
            if(ReferenceEquals(_pairingClipboard,lease))TryClearMatchingPairing(lease.Digest);
        }
        catch(OperationCanceledException){ }
        finally
        {
            if(ReferenceEquals(_pairingClipboard,lease))_pairingClipboard=null;
            lease.Dispose();
            if(IsLoaded)UpdateVsCodeBridgeStatus();
        }
    }
    private void VsCodeBridge_Changed()
    {
        if(Dispatcher.HasShutdownStarted)return;
        if(Dispatcher.CheckAccess())UpdateVsCodeBridgeStatus();
        else Dispatcher.BeginInvoke(UpdateVsCodeBridgeStatus);
    }
    private void UpdateVsCodeBridgeStatus()
    {
        if(_vsCodeBridge.IsConnected&&_pairingClipboard is { } lease)
        {
            _pairingClipboard=null;
            TryClearMatchingPairing(lease.Digest);
            lease.Dispose();
        }
        var connection=_vsCodeBridge.IsConnected
            ?"已连接到当前用户的本机可信 VS Code 工作区。"
            :"监听中，等待本机 VS Code 配对；当前没有实时元数据连接。";
        var clipboard=_pairingClipboard is null?"":"\n配对 JSON 已复制；若未被其他内容替换，将在 75 秒后从剪贴板清除。";
        VsCodeBridgeStatusText.Text=connection+clipboard;
    }
    private void MainWindow_Closed(object? sender,EventArgs e)
    {
        _vsCodeBridge.Changed-=VsCodeBridge_Changed;Closed-=MainWindow_Closed;
        var lease=_pairingClipboard;_pairingClipboard=null;
        if(lease is null)return;
        TryClearMatchingPairing(lease.Digest);lease.Dispose();
    }
    private static byte[] HashClipboardText(string value)
    {
        var bytes=Encoding.UTF8.GetBytes(value);
        try{return SHA256.HashData(bytes);}
        finally{CryptographicOperations.ZeroMemory(bytes);}
    }
    private static void TryClearMatchingPairing(byte[] expectedDigest)
    {
        try
        {
            if(!Clipboard.ContainsText(TextDataFormat.UnicodeText))return;
            var current=Clipboard.GetText(TextDataFormat.UnicodeText);
            if(current.Length>2_048)return;
            var actual=HashClipboardText(current);
            try{if(CryptographicOperations.FixedTimeEquals(actual,expectedDigest))Clipboard.Clear();}
            finally{CryptographicOperations.ZeroMemory(actual);}
        }
        catch{/* Clipboard ownership may change between inspection and clearing; preserve the new owner's content. */}
    }
    private void PromptBox_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter && Keyboard.Modifiers != ModifierKeys.Shift) { e.Handled = true; if (ViewModel.SendCommand.CanExecute(null)) ViewModel.SendCommand.Execute(null); } }

    private sealed class PairingClipboardLease(byte[] digest):IDisposable
    {
        private readonly CancellationTokenSource _lifetime=new();private bool _disposed;
        public byte[] Digest{get;}=digest;public CancellationToken Token=>_lifetime.Token;
        public void Dispose(){if(_disposed)return;_disposed=true;_lifetime.Cancel();_lifetime.Dispose();CryptographicOperations.ZeroMemory(Digest);}
    }
}
