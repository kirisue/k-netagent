using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.IO;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using KNetAgent.Desktop.WinUI.Features.Media;
using KNetAgent.Desktop.WinUI.Features.Operations;
using KNetAgent.Desktop.WinUI.Features.Tasks;
using KNetAgent.Desktop.WinUI.Features.WindowsEvents;
using KNetAgent.Desktop.WinUI.Features.WindowsServices;
using KNetAgent.Desktop.WinUI.Services;
using KNetAgent.Desktop.WinUI.ViewModels;
using TestAgent.Core;
using TestAgent.Infrastructure;

namespace KNetAgent.Desktop.WinUI;

public partial class App : Application
{
    private Window? _window;
    private ServiceProvider? _services;

    public App()
    {
        InitializeComponent();
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
        var appPaths = new AppPaths();
        var workspaceCatalog = new JsonWorkspaceCatalog(appPaths);
        var requestedWorkspace = ParseWorkspaceArgument(Environment.GetCommandLineArgs());
        var workspaceEntry = await workspaceCatalog.ResolveStartupAsync(
            requestedWorkspace,
            WorkspaceLocator.DiscoverDefaultRoot());
        var workspace = new WorkspaceLocator(workspaceEntry.Root);

        var services = new ServiceCollection();
        services.AddSingleton(appPaths);
        services.AddSingleton<IWorkspaceCatalog>(workspaceCatalog);
        services.AddSingleton(workspace);
        services.AddSingleton<WorkspaceRelaunchService>();
        services.AddSingleton<IMemoryStore, JsonMemoryStore>();
        services.AddSingleton<ISessionStore, JsonSessionStore>();
        services.AddSingleton<ISessionHistorySearch, SessionHistorySearch>();
        services.AddSingleton<ISettingsStore, JsonSettingsStore>();
        services.AddSingleton<ISecureSecretStore, DpapiSecretStore>();
        services.AddSingleton<IToolSessionStore, JsonToolSessionStore>();
        services.AddSingleton<IToolSessionCoordinator, ToolSessionCoordinator>();
        services.AddSingleton<IMcpPeerProfileStore, JsonMcpPeerProfileStore>();
        services.AddSingleton<McpPeerService>(provider => new McpPeerService(
            provider.GetRequiredService<IMcpPeerProfileStore>(),
            provider.GetRequiredService<WorkspaceLocator>()));
        services.AddSingleton<IMcpPeerService>(provider =>
            provider.GetRequiredService<McpPeerService>());
        services.AddSingleton<IEscalationEvaluator, DeterministicEscalationEvaluator>();
        services.AddSingleton<IWindowsEventReader, WindowsEventReader>();
        services.AddSingleton<IWindowsEventWatchService, WindowsEventWatchService>();
        services.AddSingleton<IWindowsEventEvidenceBuilder, WindowsEventEvidenceBuilder>();
        services.AddSingleton<IWindowsIncidentCorrelator, WindowsIncidentCorrelator>();
        services.AddSingleton<IWindowsServiceReader, WindowsServiceReader>();
        services.AddSingleton<IWindowsServiceEvidenceBuilder, WindowsServiceEvidenceBuilder>();
        services.AddSingleton<IBackgroundCommandService, BackgroundCommandService>();
        services.AddSingleton<VsCodeBridgeServer>();
        services.AddSingleton<IVsCodeBridgeClient>(provider =>
            provider.GetRequiredService<VsCodeBridgeServer>());

        services.AddSingleton<IAgentTool, ListFilesTool>();
        services.AddSingleton<IAgentTool, ReadFileTool>();
        services.AddSingleton<IAgentTool, SearchTextTool>();
        services.AddSingleton<IAgentTool, EditFileTool>();
        services.AddSingleton<IAgentTool, ApplyPatchTool>();
        services.AddSingleton<IAgentTool, RunDeveloperCommandTool>();
        services.AddSingleton<IAgentTool, SaveMemoryAgentTool>();
        services.AddSingleton<IAgentTool, SearchSessionHistoryTool>();
        services.AddSingleton<IAgentTool, GetVsCodeWorkspaceStatusTool>();
        services.AddSingleton<IAgentTool, ListVsCodeConfiguredTasksTool>();
        services.AddSingleton<IAgentTool, ListVsCodeExtensionRecommendationsTool>();
        services.AddSingleton<IAgentTool, GetVsCodeDocsLinkTool>();
        services.AddSingleton<IAgentTool, GetVsCodeActiveEditorTool>();
        services.AddSingleton<IAgentTool, GetVsCodeDiagnosticsTool>();
        services.AddSingleton<IAgentTool, ListVsCodeAvailableTasksTool>();
        services.AddSingleton<IAgentTool, ListVsCodeInstalledExtensionsTool>();
        services.AddSingleton<IAgentTool, StartBackgroundCommandTool>();
        services.AddSingleton<IAgentTool, GetBackgroundCommandTool>();
        services.AddSingleton<IAgentTool, ReadBackgroundOutputTool>();
        services.AddSingleton<IAgentTool, StopBackgroundCommandTool>();
        services.AddSingleton<IAgentTool, ListMcpPeersTool>();
        services.AddSingleton<IAgentTool, ListMcpPeerToolsTool>();
        services.AddSingleton<IAgentTool, CallMcpPeerTool>();
        services.AddSingleton<IAgentTool, ListWindowsEventChannelsTool>();
        services.AddSingleton<IAgentTool, QueryWindowsEventsTool>();
        services.AddSingleton<IAgentTool, QueryWindowsServicesTool>();
        services.AddHttpClient<ISafeWebContentReader, SafeWebContentReader>(client =>
                client.Timeout = TimeSpan.FromSeconds(30))
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                UseProxy = false,
                UseCookies = false,
                AutomaticDecompression = DecompressionMethods.GZip |
                                         DecompressionMethods.Deflate |
                                         DecompressionMethods.Brotli,
                ConnectCallback = async (context, cancellationToken) =>
                {
                    var addresses = await SafeWebAddressPolicy.ResolvePublicAsync(
                        context.DnsEndPoint.Host, cancellationToken);
                    Exception? lastError = null;
                    foreach (var address in addresses)
                    {
                        var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                        try
                        {
                            await socket.ConnectAsync(
                                new IPEndPoint(address, context.DnsEndPoint.Port), cancellationToken);
                            return new NetworkStream(socket, ownsSocket: true);
                        }
                        catch (Exception exception) when (exception is SocketException or IOException)
                        {
                            lastError = exception;
                            socket.Dispose();
                        }
                    }

                    throw new HttpRequestException(
                        "Could not connect to a validated public web address.", lastError);
                }
            });
        services.AddSingleton<IAgentTool, FetchWebContentTool>();
        services.AddWinUiMediaAndBrowser();
        services.AddSingleton<IToolRegistry, ToolRegistry>();
        services.AddSingleton<IToolAuditStore, JsonlToolAuditStore>();
        services.AddSingleton<IToolExecutionService, ToolExecutionService>();

        services.AddHttpClient<IModelProvider, OpenAiCompatibleProvider>(client =>
                client.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                UseCookies = false,
                AutomaticDecompression = DecompressionMethods.GZip |
                                         DecompressionMethods.Deflate |
                                         DecompressionMethods.Brotli
            });
        services.AddSingleton<IAgentRuntime>(provider => new AgentRuntime(
            provider.GetRequiredService<IModelProvider>(),
            provider.GetRequiredService<IMemoryStore>(),
            provider.GetRequiredService<ISessionStore>(),
            provider.GetRequiredService<IToolExecutionService>(),
            provider.GetRequiredService<IToolSessionCoordinator>(),
            provider.GetRequiredService<IEscalationEvaluator>()));

        services.AddSingleton<IUiDispatcher, SynchronizationContextUiDispatcher>();
        services.AddSingleton<WinUiUserInteractionService>();
        services.AddSingleton<IUserInteractionService>(provider =>
            provider.GetRequiredService<WinUiUserInteractionService>());
        services.AddWinUiTaskWorkspaceFeature();
        services.AddWinUiOperationsFeatures();
        services.AddWinUiWindowsEventCenter();
        services.AddWinUiWindowsServiceCenter();
        services.AddSingleton<ShellViewModel>();
        services.AddSingleton<MainWindow>();

        _services = services.BuildServiceProvider();
        await _services.GetRequiredService<IVsCodeBridgeClient>().StartAsync();
        await _services.GetRequiredService<IBackgroundCommandService>().ReconcileAsync();
        var viewModel = _services.GetRequiredService<ShellViewModel>();
        _window = _services.GetRequiredService<MainWindow>();
        _window.Closed += (_, _) =>
        {
            _services?.Dispose();
            _services = null;
        };
        _window.Activate();
        await viewModel.InitializeAsync();
        var launchPage = ParsePageArgument(Environment.GetCommandLineArgs());
        if (launchPage is not null)
            await ((MainWindow)_window).OpenDiagnosticsPageAsync(launchPage);
        }
        catch (Exception exception)
        {
            _services?.Dispose();
            _services = null;
            _window = new Window
            {
                Title = "K.netagent 启动失败",
                Content = new Grid
                {
                    Padding = new Thickness(28),
                    Children =
                    {
                        new TextBlock
                        {
                            Text = "K.netagent WinUI 3 启动失败\n\n" + exception.Message +
                                   "\n\nWPF V0.1 入口仍可继续使用。",
                            TextWrapping = TextWrapping.Wrap,
                            IsTextSelectionEnabled = true
                        }
                    }
                }
            };
            _window.Activate();
        }
    }

    private static string? ParseWorkspaceArgument(IReadOnlyList<string> arguments)
    {
        string? requested = null;
        for (var index = 1; index < arguments.Count; index++)
        {
            if (!arguments[index].Equals("--workspace", StringComparison.OrdinalIgnoreCase))
                continue;
            if (requested is not null || index + 1 >= arguments.Count ||
                arguments[index + 1].StartsWith("--", StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(arguments[index + 1]))
            {
                throw new InvalidDataException("--workspace 参数必须且只能提供一个工作区路径。");
            }

            requested = arguments[++index];
        }

        return requested;
    }

    private static string? ParsePageArgument(IReadOnlyList<string> arguments)
    {
        string? page = null;
        for (var index = 1; index < arguments.Count; index++)
        {
            if (!arguments[index].Equals("--page", StringComparison.OrdinalIgnoreCase)) continue;
            if (page is not null || ++index >= arguments.Count || arguments[index] is not ("events" or "services"))
                throw new InvalidDataException("--page 只支持 events 或 services，且只能指定一次。");
            page = arguments[index];
        }
        return page;
    }
}
