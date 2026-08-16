using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.IO;
using System.Windows;
using TestAgent.Core;
using TestAgent.Infrastructure;

namespace TestAgent.Desktop;
public partial class App : Application
{
    private ServiceProvider? _services;
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
        var services = new ServiceCollection();
        services.AddSingleton<AppPaths>(); services.AddSingleton<IMemoryStore, JsonMemoryStore>(); services.AddSingleton<ISessionStore, JsonSessionStore>(); services.AddSingleton<ISessionHistorySearch, SessionHistorySearch>();
        services.AddSingleton<ISettingsStore, JsonSettingsStore>(); services.AddSingleton<ISecureSecretStore, DpapiSecretStore>();
        services.AddSingleton<ITaskPlanStore, JsonTaskPlanStore>(); services.AddSingleton<IProjectScanner, BoundedProjectScanner>(); services.AddSingleton<ITaskWorkflowService, SingleAgentTaskWorkflowService>();
        services.AddSingleton<IToolSessionStore, JsonToolSessionStore>(); services.AddSingleton<IToolSessionCoordinator, ToolSessionCoordinator>();
        services.AddSingleton<IBackgroundCommandService, BackgroundCommandService>();
        services.AddSingleton<WorkspaceLocator>(); services.AddSingleton<IIterationGuideStore, MarkdownIterationGuideStore>(); services.AddSingleton<ICodeIterationService, CodeIterationService>();
        services.AddSingleton<IAgentTool, ListFilesTool>(); services.AddSingleton<IAgentTool, ReadFileTool>(); services.AddSingleton<IAgentTool, SearchTextTool>();
        services.AddSingleton<IAgentTool, EditFileTool>(); services.AddSingleton<IAgentTool, ApplyPatchTool>(); services.AddSingleton<IAgentTool, RunDeveloperCommandTool>(); services.AddSingleton<IAgentTool, SaveMemoryAgentTool>(); services.AddSingleton<IAgentTool, SearchSessionHistoryTool>();
        services.AddSingleton<IAgentTool, StartBackgroundCommandTool>(); services.AddSingleton<IAgentTool, GetBackgroundCommandTool>(); services.AddSingleton<IAgentTool, ReadBackgroundOutputTool>(); services.AddSingleton<IAgentTool, StopBackgroundCommandTool>();
        services.AddHttpClient<ISafeWebContentReader, SafeWebContentReader>(client => client.Timeout = TimeSpan.FromSeconds(30))
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                UseProxy = false,
                UseCookies = false,
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
                ConnectCallback = async (context, ct) =>
                {
                    var addresses = await SafeWebAddressPolicy.ResolvePublicAsync(context.DnsEndPoint.Host, ct);
                    Exception? lastError = null;
                    foreach (var address in addresses)
                    {
                        var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                        try
                        {
                            await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), ct);
                            return new NetworkStream(socket, ownsSocket: true);
                        }
                        catch (Exception ex) when (ex is SocketException or IOException)
                        {
                            lastError = ex; socket.Dispose();
                        }
                    }
                    throw new HttpRequestException("Could not connect to a validated public web address.", lastError);
                }
            });
        services.AddSingleton<IAgentTool, FetchWebContentTool>();
        services.AddSingleton<IToolRegistry, ToolRegistry>(); services.AddSingleton<IToolAuditStore, JsonlToolAuditStore>(); services.AddSingleton<IToolExecutionService, ToolExecutionService>();
        services.AddHttpClient<IModelProvider, OpenAiCompatibleProvider>(); services.AddSingleton<IAgentRuntime>(sp => new AgentRuntime(
            sp.GetRequiredService<IModelProvider>(), sp.GetRequiredService<IMemoryStore>(), sp.GetRequiredService<ISessionStore>(),
            sp.GetRequiredService<IToolExecutionService>(), sp.GetRequiredService<IToolSessionCoordinator>()));
        services.AddSingleton<MainViewModel>(); services.AddSingleton<MainWindow>(); _services = services.BuildServiceProvider();
        await _services.GetRequiredService<IBackgroundCommandService>().ReconcileAsync();
        var window = _services.GetRequiredService<MainWindow>(); await window.ViewModel.InitializeAsync(); window.Show();
        }
        catch(Exception ex)
        {
            _services?.Dispose(); _services=null;
            MessageBox.Show("K.netagentV0.1 启动失败：\n"+ex.Message+"\n\n请检查 %LOCALAPPDATA%\\TestAgent 的配置/数据权限，或删除损坏的 config.json 后重试。","K.netagentV0.1",MessageBoxButton.OK,MessageBoxImage.Error);
            Shutdown(-1);
        }
    }
    protected override void OnExit(ExitEventArgs e) { _services?.Dispose(); base.OnExit(e); }
}
