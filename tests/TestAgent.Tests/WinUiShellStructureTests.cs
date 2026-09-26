using Xunit;
using System.Xml.Linq;

namespace TestAgent.Tests;

/// <summary>
/// These source-level gates deliberately do not reference Microsoft.WindowsAppSDK.
/// They keep the normal test host headless while XAML compilation remains the
/// authoritative syntax check in the WinUI project build.
/// </summary>
public sealed class WinUiShellStructureTests
{
    private static readonly string Root = FindRepositoryRoot();

    [Fact]
    public void Winui_project_is_an_unpackaged_winui_shell_not_a_second_wpf_project()
    {
        var project = Read("src", "KNetAgent.Desktop.WinUI", "KNetAgent.Desktop.WinUI.csproj");

        Assert.Contains("<UseWinUI>true</UseWinUI>", project, StringComparison.Ordinal);
        Assert.Contains("<WindowsPackageType>None</WindowsPackageType>", project, StringComparison.Ordinal);
        Assert.DoesNotContain("<UseWPF>true</UseWPF>", project, StringComparison.Ordinal);
    }

    [Fact]
    public void Shell_binds_real_threads_messages_composer_and_inspector_data()
    {
        var xaml = Read("src", "KNetAgent.Desktop.WinUI", "MainWindow.xaml");

        foreach (var contract in new[]
                 {
                     "ItemsSource=\"{Binding VisibleSessions}\"",
                     "Text=\"{Binding SessionSearchText, Mode=TwoWay",
                     "SelectedItem=\"{Binding SelectedSession, Mode=TwoWay}\"",
                     "ItemsSource=\"{Binding Messages}\"",
                     "Text=\"{Binding Input, Mode=TwoWay",
                     "Command=\"{Binding SendCommand}\"",
                     "Command=\"{Binding StopCommand}\"",
                     "ItemsSource=\"{Binding ToolSessions}\"",
                     "ItemsSource=\"{Binding Memories}\"",
                     "SelectedInspectorTab",
                     "Visibility=\"{x:Bind ViewModel.Busy, Mode=OneWay}\""
                 })
        {
            Assert.Contains(contract, xaml, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("设计 Codex 风格界面", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("正在准备真实会话绑定", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void Shell_has_named_responsive_states_and_keeps_conversation_available()
    {
        var xaml = Read("src", "KNetAgent.Desktop.WinUI", "MainWindow.xaml");

        Assert.Contains("x:Name=\"WideLayout\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"MediumLayout\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"NarrowLayout\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AdaptiveTrigger MinWindowWidth=\"1180\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AdaptiveTrigger MinWindowWidth=\"760\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AdaptiveTrigger MinWindowWidth=\"0\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"MessageFeed\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"OpenNavigationButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"OpenInspectorButton\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void Navigation_collapse_always_leaves_a_toggle_that_can_restore_the_rail()
    {
        var xaml = Read("src", "KNetAgent.Desktop.WinUI", "MainWindow.xaml");
        var codeBehind = Read("src", "KNetAgent.Desktop.WinUI", "MainWindow.xaml.cs");
        var document = XDocument.Parse(xaml);
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var toggle = document.Descendants()
            .Single(element =>
                element.Name.LocalName == "Button" &&
                string.Equals((string?)element.Attribute(x + "Name"), "OpenNavigationButton",
                    StringComparison.Ordinal));

        Assert.Equal("ToggleNavigation_Click", (string?)toggle.Attribute("Click"));
        Assert.Equal("Visible", (string?)toggle.Attribute("Visibility"));
        Assert.DoesNotContain(
            "Setter Target=\"OpenNavigationButton.Visibility\" Value=\"Collapsed\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains("Click=\"CloseNavigation_Click\"", xaml, StringComparison.Ordinal);
        Assert.Contains("private void ToggleNavigation_Click", codeBehind, StringComparison.Ordinal);
        Assert.Contains("private void CloseNavigation_Click", codeBehind, StringComparison.Ordinal);
        Assert.Contains("OpenNavigation_Click(sender, e)", codeBehind, StringComparison.Ordinal);
        Assert.Contains("CloseNavigation_Click(sender, e)", codeBehind, StringComparison.Ordinal);
        Assert.Contains("LeftRail.Visibility = Visibility.Visible", codeBehind, StringComparison.Ordinal);
        Assert.Contains("LeftRail.Visibility = Visibility.Collapsed", codeBehind, StringComparison.Ordinal);
    }

    [Fact]
    public void Composer_add_flyout_routes_each_item_to_a_real_command_or_inspector_tag()
    {
        var document = XDocument.Parse(Read("src", "KNetAgent.Desktop.WinUI", "MainWindow.xaml"));
        var addButton = document.Descendants()
            .Single(element =>
                element.Name.LocalName == "Button" &&
                string.Equals((string?)element.Attribute("Content"), "＋", StringComparison.Ordinal));
        var menuItems = addButton.Descendants()
            .Where(element => element.Name.LocalName == "MenuFlyoutItem")
            .ToArray();

        Assert.Contains(menuItems, item =>
            string.Equals((string?)item.Attribute("Command"),
                "{x:Bind ViewModel.NewSessionCommand}",
                StringComparison.Ordinal));

        foreach (var tag in new[] { "tools", "memory" })
        {
            Assert.Contains(menuItems, item =>
                string.Equals((string?)item.Attribute("Tag"), tag, StringComparison.Ordinal) &&
                string.Equals((string?)item.Attribute("Click"), "OpenInspectorTab_Click",
                    StringComparison.Ordinal));
        }

        Assert.Contains(menuItems, item =>
            string.Equals((string?)item.Attribute("Click"), "OpenTaskWorkspace_Click",
                StringComparison.Ordinal));
        Assert.Contains(menuItems, item =>
            string.Equals((string?)item.Attribute("Click"), "OpenBrowser_Click",
                StringComparison.Ordinal));
        Assert.Contains(menuItems, item =>
            string.Equals((string?)item.Attribute("Click"), "OpenOperations_Click",
                StringComparison.Ordinal));

        Assert.All(menuItems, item => Assert.True(
            item.Attribute("Command") is not null || item.Attribute("Tag") is not null ||
            item.Attribute("Click") is not null,
            $"Composer add-menu item '{item.Attribute("Text")?.Value}' has no real route."));
    }

    [Fact]
    public void Composer_routes_enter_and_shift_enter_without_duplicating_send_logic()
    {
        var xaml = Read("src", "KNetAgent.Desktop.WinUI", "MainWindow.xaml");
        var codeBehind = Read("src", "KNetAgent.Desktop.WinUI", "MainWindow.xaml.cs");

        Assert.Contains("KeyDown=", xaml, StringComparison.Ordinal);
        Assert.Contains("VirtualKey.Enter", codeBehind, StringComparison.Ordinal);
        Assert.Contains("VirtualKey.Shift", codeBehind, StringComparison.Ordinal);
        Assert.Contains("ViewModel.SendCommand", codeBehind, StringComparison.Ordinal);
    }

    [Fact]
    public void Navigation_and_model_controls_are_real_bindings_not_shell_placeholders()
    {
        var xaml = Read("src", "KNetAgent.Desktop.WinUI", "MainWindow.xaml");
        var codeBehind = Read("src", "KNetAgent.Desktop.WinUI", "MainWindow.xaml.cs");
        var settingsXaml = Read("src", "KNetAgent.Desktop.WinUI", "SettingsDialog.xaml");
        var viewModel = Read("src", "KNetAgent.Desktop.WinUI", "ViewModels", "ShellViewModel.cs");

        foreach (var tab in new[] { "memory", "tools" })
            Assert.Contains($"Tag=\"{tab}\" Click=\"OpenInspectorTab_Click\"", xaml,
                StringComparison.Ordinal);

        Assert.Contains("Click=\"OpenSettings_Click\"", xaml, StringComparison.Ordinal);
        Assert.Contains("private void OpenInspectorTab_Click", codeBehind, StringComparison.Ordinal);
        Assert.Contains("private async void OpenSettings_Click", codeBehind, StringComparison.Ordinal);
        Assert.Contains("ItemsSource=\"{Binding ReasoningEfforts}\"", settingsXaml,
            StringComparison.Ordinal);
        Assert.Contains("SelectedItem=\"{Binding ReasoningEffort, Mode=TwoWay}\"", settingsXaml,
            StringComparison.Ordinal);
        Assert.Contains("Maximum=\"128000\"", settingsXaml, StringComparison.Ordinal);
        Assert.Contains("Maximum=\"600\"", settingsXaml, StringComparison.Ordinal);
        Assert.Contains("Command=\"{Binding ApplyRecommendedModelSettingsCommand}\"", settingsXaml,
            StringComparison.Ordinal);
        Assert.Contains("gpt-5.6-sol", viewModel, StringComparison.Ordinal);
        Assert.DoesNotContain("Content=\"单 Agent  ▾\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void Settings_are_an_independent_dialog_and_not_an_inspector_tab()
    {
        var shellXaml = Read("src", "KNetAgent.Desktop.WinUI", "MainWindow.xaml");
        var shellCodeBehind = Read("src", "KNetAgent.Desktop.WinUI", "MainWindow.xaml.cs");
        var dialogXaml = Read("src", "KNetAgent.Desktop.WinUI", "SettingsDialog.xaml");

        Assert.StartsWith("<?xml", dialogXaml, StringComparison.Ordinal);
        Assert.Contains("<ContentDialog", dialogXaml, StringComparison.Ordinal);
        Assert.Contains("x:Class=\"KNetAgent.Desktop.WinUI.SettingsDialog\"", dialogXaml,
            StringComparison.Ordinal);
        Assert.Contains("PrimaryButtonText=\"保存设置\"", dialogXaml, StringComparison.Ordinal);
        Assert.Contains("CloseButtonText=\"取消\"", dialogXaml, StringComparison.Ordinal);
        Assert.Contains("new SettingsDialog(ViewModel)", shellCodeBehind, StringComparison.Ordinal);
        Assert.Contains("await dialog.ShowAsync()", shellCodeBehind, StringComparison.Ordinal);

        Assert.DoesNotContain("<TabViewItem Header=\"设置\"", shellXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Tag=\"settings\" Click=\"OpenInspectorTab_Click\"", shellXaml,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Settings_dialog_saves_only_on_primary_action_and_restores_on_cancel()
    {
        var xaml = Read("src", "KNetAgent.Desktop.WinUI", "SettingsDialog.xaml");
        var codeBehind = Read("src", "KNetAgent.Desktop.WinUI", "SettingsDialog.xaml.cs");

        Assert.Contains("PrimaryButtonClick=\"SettingsDialog_PrimaryButtonClick\"", xaml,
            StringComparison.Ordinal);
        Assert.Contains("Closed=\"SettingsDialog_Closed\"", xaml, StringComparison.Ordinal);
        Assert.Contains("_original = SettingsSnapshot.Capture(ViewModel)", codeBehind,
            StringComparison.Ordinal);
        Assert.Contains("await ViewModel.SaveSettingsAsync(apiKey)", codeBehind,
            StringComparison.Ordinal);
        Assert.Contains("SettingsErrorBar.Message = exception.Message", codeBehind,
            StringComparison.Ordinal);
        Assert.Contains("_saved = true", codeBehind, StringComparison.Ordinal);
        Assert.Contains("if (!_saved)", codeBehind, StringComparison.Ordinal);
        Assert.Contains("_original.Restore(ViewModel)", codeBehind, StringComparison.Ordinal);
        Assert.Contains("args.Cancel = true", codeBehind, StringComparison.Ordinal);
    }

    [Fact]
    public void Workspace_entry_and_dialog_are_real_responsive_routes_not_placeholders()
    {
        var shellXaml = Read("src", "KNetAgent.Desktop.WinUI", "MainWindow.xaml");
        var shellCodeBehind = Read("src", "KNetAgent.Desktop.WinUI", "MainWindow.xaml.cs");
        var dialogXaml = Read("src", "KNetAgent.Desktop.WinUI", "WorkspacesDialog.xaml");
        var dialogCodeBehind = Read("src", "KNetAgent.Desktop.WinUI", "WorkspacesDialog.xaml.cs");

        Assert.Contains("x:Name=\"WorkspaceSwitcherButton\"", shellXaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding CurrentWorkspaceName}\"", shellXaml, StringComparison.Ordinal);
        Assert.Contains("ToolTipService.ToolTip=\"{Binding CurrentWorkspacePath}\"", shellXaml,
            StringComparison.Ordinal);
        Assert.Contains("Click=\"OpenWorkspaces_Click\"", shellXaml, StringComparison.Ordinal);
        Assert.Contains("Tag=\"create-workspace\" Click=\"CreateWorkspace_Click\"", shellXaml,
            StringComparison.Ordinal);
        Assert.Contains("Tag=\"open-workspace\" Click=\"OpenWorkspaceFolder_Click\"", shellXaml,
            StringComparison.Ordinal);
        Assert.Contains("ShowWorkspacesDialogAsync(WorkspaceDialogStartMode.Overview)", shellCodeBehind,
            StringComparison.Ordinal);
        Assert.Contains("ShowWorkspacesDialogAsync(WorkspaceDialogStartMode.Create)", shellCodeBehind,
            StringComparison.Ordinal);
        Assert.Contains("ShowWorkspacesDialogAsync(WorkspaceDialogStartMode.Open)", shellCodeBehind,
            StringComparison.Ordinal);
        Assert.Contains("new WorkspacesDialog(ViewModel, this, startMode)", shellCodeBehind,
            StringComparison.Ordinal);
        Assert.Contains("await dialog.ShowAsync()", shellCodeBehind, StringComparison.Ordinal);

        Assert.StartsWith("<?xml", dialogXaml, StringComparison.Ordinal);
        Assert.Contains("x:Class=\"KNetAgent.Desktop.WinUI.WorkspacesDialog\"", dialogXaml,
            StringComparison.Ordinal);
        Assert.Contains("Title=\"项目与工作区\"", dialogXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"NarrowLayout\"", dialogXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"WideLayout\"", dialogXaml, StringComparison.Ordinal);
        Assert.Contains("ItemsSource=\"{Binding RecentWorkspaces}\"", dialogXaml,
            StringComparison.Ordinal);
        Assert.Contains("x:Name=\"ProjectNameBox\"", dialogXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"ParentFolderBox\"", dialogXaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"CreateWorkspace_Click\"", dialogXaml, StringComparison.Ordinal);
        Assert.Contains("TextTrimming=\"CharacterEllipsis\"", dialogXaml, StringComparison.Ordinal);

        Assert.Contains("new FolderPicker", dialogCodeBehind, StringComparison.Ordinal);
        Assert.Contains("InitializeWithWindow.Initialize", dialogCodeBehind, StringComparison.Ordinal);
        Assert.Contains("PickSingleFolderAsync", dialogCodeBehind, StringComparison.Ordinal);
        Assert.Contains("ViewModel.OpenWorkspaceAsync(folder.Path)", dialogCodeBehind,
            StringComparison.Ordinal);
        Assert.Contains("ViewModel.CreateWorkspaceAsync", dialogCodeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("Directory.CreateDirectory", dialogCodeBehind, StringComparison.Ordinal);
    }

    [Fact]
    public void Workspace_switch_uses_a_single_argument_safe_relaunch_and_rebuilds_root_bound_services()
    {
        var app = Read("src", "KNetAgent.Desktop.WinUI", "App.xaml.cs");
        var relaunch = Read("src", "KNetAgent.Desktop.WinUI", "Services",
            "WorkspaceRelaunchService.cs");
        var viewModel = Read("src", "KNetAgent.Desktop.WinUI", "ViewModels", "ShellViewModel.cs");

        Assert.Contains("ParseWorkspaceArgument(Environment.GetCommandLineArgs())", app,
            StringComparison.Ordinal);
        Assert.Contains("ResolveStartupAsync(", app, StringComparison.Ordinal);
        Assert.Contains("new WorkspaceLocator(workspaceEntry.Root)", app, StringComparison.Ordinal);
        Assert.Contains("services.AddSingleton(workspace)", app, StringComparison.Ordinal);
        Assert.Contains("requested is not null", app, StringComparison.Ordinal);
        Assert.Contains("arguments[index + 1].StartsWith(\"--\"", app, StringComparison.Ordinal);

        Assert.Contains("UseShellExecute = false", relaunch, StringComparison.Ordinal);
        Assert.Contains("startInfo.ArgumentList.Add(\"--workspace\")", relaunch,
            StringComparison.Ordinal);
        Assert.Contains("startInfo.ArgumentList.Add(Path.GetFullPath(workspaceRoot))", relaunch,
            StringComparison.Ordinal);
        Assert.Contains("if (Process.Start(startInfo) is null)", relaunch, StringComparison.Ordinal);
        Assert.True(relaunch.IndexOf("Process.Start(startInfo)", StringComparison.Ordinal) <
                    relaunch.IndexOf("Application.Current.Exit()", StringComparison.Ordinal));
        Assert.DoesNotContain("Arguments =", relaunch, StringComparison.Ordinal);
        Assert.DoesNotContain("cmd.exe", relaunch, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("powershell", relaunch, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("EnsureWorkspaceSwitchAllowedAsync", viewModel, StringComparison.Ordinal);
        Assert.Contains("if (Busy)", viewModel, StringComparison.Ordinal);
        Assert.Contains("!string.IsNullOrWhiteSpace(Input)", viewModel, StringComparison.Ordinal);
        Assert.Contains("BackgroundCommandState.Running", viewModel, StringComparison.Ordinal);
        Assert.Contains("_workspaceRelaunch.Relaunch(workspace.Root)", viewModel,
            StringComparison.Ordinal);
        Assert.Contains("RegisterExistingAsync(CurrentWorkspacePath", viewModel,
            StringComparison.Ordinal);
        Assert.Contains("AgentRuntime.NewSession(CurrentWorkspaceId)", viewModel,
            StringComparison.Ordinal);
        Assert.Contains("WorkspaceId: CurrentWorkspaceId", viewModel, StringComparison.Ordinal);
    }

    [Fact]
    public void Mcp_peers_are_managed_from_the_composer_in_an_independent_real_dialog()
    {
        var shellXaml = Read("src", "KNetAgent.Desktop.WinUI", "MainWindow.xaml");
        var shellCodeBehind = Read("src", "KNetAgent.Desktop.WinUI", "MainWindow.xaml.cs");
        var dialogXaml = Read("src", "KNetAgent.Desktop.WinUI", "McpPeersDialog.xaml");
        var dialogCodeBehind = Read("src", "KNetAgent.Desktop.WinUI", "McpPeersDialog.xaml.cs");
        var viewModel = Read("src", "KNetAgent.Desktop.WinUI", "ViewModels", "ShellViewModel.cs");
        var wpfViewModel = Read("src", "TestAgent.Desktop", "MainViewModel.cs");

        Assert.Contains(
            "Text=\"外部 Agent / MCP\" Tag=\"mcp-peers\" Click=\"OpenMcpPeers_Click\"",
            shellXaml,
            StringComparison.Ordinal);
        Assert.Contains("private async void OpenMcpPeers_Click", shellCodeBehind,
            StringComparison.Ordinal);
        Assert.Contains("new McpPeersDialog(ViewModel, this)", shellCodeBehind,
            StringComparison.Ordinal);
        Assert.Contains("await dialog.ShowAsync()", shellCodeBehind, StringComparison.Ordinal);

        Assert.StartsWith("<?xml", dialogXaml, StringComparison.Ordinal);
        Assert.Contains("<ContentDialog", dialogXaml, StringComparison.Ordinal);
        Assert.Contains("Title=\"外部 Agent / MCP\"", dialogXaml, StringComparison.Ordinal);
        Assert.Contains("ItemsSource=\"{Binding McpPeers}\"", dialogXaml, StringComparison.Ordinal);
        Assert.Contains("SelectedItem=\"{Binding SelectedMcpPeer, Mode=TwoWay}\"", dialogXaml,
            StringComparison.Ordinal);
        Assert.Contains("ItemsSource=\"{Binding SelectedMcpPeerTools}\"", dialogXaml,
            StringComparison.Ordinal);
        Assert.Contains("Click=\"ConnectPeer_Click\"", dialogXaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"DisconnectPeer_Click\"", dialogXaml, StringComparison.Ordinal);
        Assert.Contains("Toggled=\"TogglePeerEnabled_Toggled\"", dialogXaml,
            StringComparison.Ordinal);
        Assert.Contains("Toggled=\"ToggleToolAllowed_Toggled\"", dialogXaml,
            StringComparison.Ordinal);
        Assert.Contains("允许评估不足时提出委托（仍逐次审批）", dialogXaml,
            StringComparison.Ordinal);
        Assert.Contains("Toggled=\"ToggleAutomaticEscalation_Toggled\"", dialogXaml,
            StringComparison.Ordinal);
        Assert.Contains("Text=\"最近委托评估\"", dialogXaml, StringComparison.Ordinal);
        Assert.Contains("ViewModel.LatestEscalationEvent", dialogXaml, StringComparison.Ordinal);

        Assert.Contains("new FileOpenPicker", dialogCodeBehind, StringComparison.Ordinal);
        Assert.Contains("picker.FileTypeFilter.Add(\".exe\")", dialogCodeBehind,
            StringComparison.Ordinal);
        Assert.Contains("InitializeWithWindow.Initialize", dialogCodeBehind,
            StringComparison.Ordinal);
        Assert.Contains("AddExecutableMcpPeerAsync(kind, file.Path)", dialogCodeBehind,
            StringComparison.Ordinal);
        Assert.Contains("AddCustomHttpMcpPeerAsync", dialogCodeBehind, StringComparison.Ordinal);
        Assert.Contains("SetSelectedMcpPeerAutomaticEscalationAsync(toggle.IsOn)", dialogCodeBehind,
            StringComparison.Ordinal);
        Assert.DoesNotContain("CallToolAsync", dialogCodeBehind, StringComparison.Ordinal);

        Assert.Contains("IMcpPeerService mcpPeers", viewModel, StringComparison.Ordinal);
        Assert.Contains("Path.IsPathFullyQualified(executablePath)", viewModel,
            StringComparison.Ordinal);
        Assert.Contains("!uri.IsLoopback", viewModel, StringComparison.Ordinal);
        Assert.Contains("AllowedTools: []", viewModel, StringComparison.Ordinal);
        Assert.Contains("SetSelectedMcpPeerToolAllowedAsync", viewModel, StringComparison.Ordinal);
        Assert.Contains("SetSelectedMcpPeerAutomaticEscalationAsync", viewModel,
            StringComparison.Ordinal);
        Assert.Contains("AllowAutomaticEscalation = allowed", viewModel, StringComparison.Ordinal);
        Assert.Contains("StreamEventKind.Escalation", viewModel, StringComparison.Ordinal);
        Assert.Contains("LatestEscalationEvent", viewModel, StringComparison.Ordinal);
        Assert.Contains("StreamEventKind.Escalation)Status=\"委托评估：\"+value.Text",
            wpfViewModel, StringComparison.Ordinal);
        Assert.Contains("selected.State != McpPeerConnectionState.Connected", viewModel,
            StringComparison.Ordinal);
        Assert.Contains("_mcpPeers.SaveAsync", viewModel, StringComparison.Ordinal);
        Assert.Contains("_mcpPeers.ConnectAsync", viewModel, StringComparison.Ordinal);
        Assert.Contains("_mcpPeers.DisconnectAsync", viewModel, StringComparison.Ordinal);
        Assert.Contains("_mcpPeers.ListToolsAsync", viewModel, StringComparison.Ordinal);
        Assert.DoesNotContain("_mcpPeers.CallToolAsync", viewModel, StringComparison.Ordinal);
    }

    [Fact]
    public void Mcp_peers_dialog_reflows_for_narrow_windows_and_wraps_long_escalation_copy()
    {
        var xaml = Read("src", "KNetAgent.Desktop.WinUI", "McpPeersDialog.xaml");
        var document = XDocument.Parse(xaml);
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";

        Assert.DoesNotContain(document.Root!.DescendantsAndSelf(), element =>
            string.Equals((string?)element.Attribute("Width"), "880", StringComparison.Ordinal));

        var responsiveStates = document.Descendants()
            .Single(element =>
                element.Name.LocalName == "VisualStateGroup" &&
                string.Equals((string?)element.Attribute(x + "Name"), "ResponsiveLayoutStates",
                    StringComparison.Ordinal));
        var wideState = responsiveStates.Elements()
            .Single(element =>
                element.Name.LocalName == "VisualState" &&
                string.Equals((string?)element.Attribute(x + "Name"), "WideLayout",
                    StringComparison.Ordinal));
        var narrowState = responsiveStates.Elements()
            .Single(element =>
                element.Name.LocalName == "VisualState" &&
                string.Equals((string?)element.Attribute(x + "Name"), "NarrowLayout",
                    StringComparison.Ordinal));

        Assert.Contains(wideState.Descendants(), element =>
            element.Name.LocalName == "AdaptiveTrigger" &&
            int.TryParse((string?)element.Attribute("MinWindowWidth"), out var minimumWidth) &&
            minimumWidth > 0);
        Assert.Contains(narrowState.Descendants(), element =>
            element.Name.LocalName == "AdaptiveTrigger" &&
            string.Equals((string?)element.Attribute("MinWindowWidth"), "0",
                StringComparison.Ordinal));

        var peerWorkspace = NamedElement(document, x, "PeerWorkspaceGrid");
        AssertGridUsesMultipleRows(peerWorkspace, "Peer workspace");
        Assert.All(
            peerWorkspace.Elements().Where(IsLayoutPanel),
            panel => Assert.Equal("0", (string?)panel.Attribute("Grid.Column") ?? "0"));

        AssertGridUsesMultipleRows(NamedElement(document, x, "TopActionsGrid"),
            "MCP top actions");
        AssertGridUsesMultipleRows(NamedElement(document, x, "PeerActionsGrid"),
            "Peer actions");

        var escalationCopy = document.Descendants()
            .Single(element =>
                element.Name.LocalName == "TextBlock" &&
                ((string?)element.Attribute("Text"))?.Contains(
                    "允许评估不足时提出委托", StringComparison.Ordinal) == true);
        Assert.Equal("Wrap", (string?)escalationCopy.Attribute("TextWrapping"));
    }

    [Fact]
    public void Winui_viewmodel_boundary_has_headless_dispatch_and_fail_closed_approval()
    {
        var dispatcher = Read("src", "KNetAgent.Desktop.WinUI", "Services", "IUiDispatcher.cs");
        var interaction = Read("src", "KNetAgent.Desktop.WinUI", "Services", "IUserInteractionService.cs");
        var winUiInteraction = Read(
            "src", "KNetAgent.Desktop.WinUI", "Services", "WinUiUserInteractionService.cs");
        var app = Read("src", "KNetAgent.Desktop.WinUI", "App.xaml.cs");
        var window = Read("src", "KNetAgent.Desktop.WinUI", "MainWindow.xaml.cs");
        var sourceRoot = Path.Combine(Root, "src", "KNetAgent.Desktop.WinUI");
        var productionSources = Directory.EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories)
            .Select(File.ReadAllText)
            .ToArray();

        Assert.Contains("interface IUiDispatcher", dispatcher, StringComparison.Ordinal);
        Assert.Contains("sealed class InlineUiDispatcher", dispatcher, StringComparison.Ordinal);
        Assert.Contains("interface IUserInteractionService", interaction, StringComparison.Ordinal);
        Assert.Contains("return Task.FromResult(false)", interaction, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "AddSingleton<IUserInteractionService, DenyToolApprovalInteractionService>()",
            app,
            StringComparison.Ordinal);
        Assert.Contains("AddSingleton<WinUiUserInteractionService>()", app, StringComparison.Ordinal);
        Assert.Contains("GetRequiredService<WinUiUserInteractionService>()", app, StringComparison.Ordinal);
        Assert.Contains("ContentDialog", winUiInteraction, StringComparison.Ordinal);
        Assert.Contains("DefaultButton = ContentDialogButton.Close", winUiInteraction, StringComparison.Ordinal);
        Assert.Contains("return result == ContentDialogResult.Primary", winUiInteraction, StringComparison.Ordinal);
        Assert.Contains("_interaction.Attach(ShellRoot.XamlRoot)", window, StringComparison.Ordinal);
        Assert.All(productionSources, source =>
        {
            Assert.DoesNotContain("using System.Windows;", source, StringComparison.Ordinal);
            Assert.DoesNotContain("MessageBox.Show", source, StringComparison.Ordinal);
        });
    }

    private static string Read(params string[] relativeParts) =>
        File.ReadAllText(Path.Combine([Root, .. relativeParts]));

    private static XElement NamedElement(XDocument document, XNamespace x, string name) =>
        document.Descendants().Single(element =>
            string.Equals((string?)element.Attribute(x + "Name"), name, StringComparison.Ordinal));

    private static void AssertGridUsesMultipleRows(XElement grid, string description)
    {
        var rowDefinitions = grid.Elements()
            .Single(element => element.Name.LocalName == "Grid.RowDefinitions")
            .Elements()
            .Count(element => element.Name.LocalName == "RowDefinition");
        var occupiedRows = grid.Elements()
            .Where(IsLayoutPanel)
            .Select(element => (string?)element.Attribute("Grid.Row") ?? "0")
            .Distinct(StringComparer.Ordinal)
            .Count();

        Assert.True(rowDefinitions >= 2, $"{description} must define at least two rows.");
        Assert.True(occupiedRows >= 2, $"{description} must place controls on multiple rows.");
    }

    private static bool IsLayoutPanel(XElement element) =>
        element.Name.LocalName is not "Grid.RowDefinitions" and not "Grid.ColumnDefinitions" and
        not "VisualStateManager.VisualStateGroups";

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "TestAgent.slnx")))
                return directory.FullName;
        }

        throw new DirectoryNotFoundException(
            $"Could not locate TestAgent.slnx above {AppContext.BaseDirectory}.");
    }
}
