using Xunit;

namespace TestAgent.Tests;

/// <summary>
/// Headless source-contract tests. The WinUI project build remains the authoritative
/// XAML and C# compiler check because this test host intentionally does not load WinAppSDK.
/// </summary>
public sealed class WinUiOperationsFeatureTests
{
    private static readonly string Root = FindRepositoryRoot();

    [Fact]
    public void Operations_feature_has_a_small_di_and_host_boundary()
    {
        var source = ReadFeature("OperationsFeatureRegistration.cs");

        Assert.Contains("AddWinUiOperationsFeatures", source, StringComparison.Ordinal);
        Assert.Contains("OperationsFeatureHost", source, StringComparison.Ordinal);
        Assert.Contains("ShowAsync(", source, StringComparison.Ordinal);
        Assert.Contains("XamlRoot xamlRoot", source, StringComparison.Ordinal);
        Assert.Contains("var window = new Window", source, StringComparison.Ordinal);
        Assert.Contains("interaction.Attach(view.XamlRoot)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("var dialog = new ContentDialog", source, StringComparison.Ordinal);
        Assert.Contains("IIterationGuideStore, SelfIterationGuideStore", source, StringComparison.Ordinal);
        Assert.Contains("ICodeIterationService, SelfCodeIterationService", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Pairing_json_is_temporary_and_only_matching_clipboard_data_is_cleared()
    {
        var clipboard = ReadFeature("PairingClipboard.cs");
        var viewModel = ReadFeature("OperationsViewModels.cs");

        Assert.Contains("ClearIfMatchesAsync", clipboard, StringComparison.Ordinal);
        Assert.Contains("string.Equals(latestText, expectedText", clipboard, StringComparison.Ordinal);
        Assert.Contains("PairingClipboardLifetimeSeconds = 60", viewModel, StringComparison.Ordinal);
        Assert.Contains("if (_bridge.IsConnected && _copiedPayload is not null)", viewModel, StringComparison.Ordinal);
        Assert.DoesNotContain("PairingSecret", ReadFeature("OperationsHubView.xaml"), StringComparison.Ordinal);
    }

    [Fact]
    public void Background_ui_reads_incrementally_and_requires_stop_approval_without_stdin()
    {
        var xaml = ReadFeature("OperationsHubView.xaml");
        var viewModel = ReadFeature("OperationsViewModels.cs");

        Assert.Contains("OutputCursor", viewModel, StringComparison.Ordinal);
        Assert.Contains("ReadOutputAsync(selected.Id, OutputCursor", viewModel, StringComparison.Ordinal);
        Assert.Contains("StopApproved", viewModel, StringComparison.Ordinal);
        Assert.Contains("IToolExecutionService", viewModel, StringComparison.Ordinal);
        Assert.Contains("InteractiveApprovalObserver", viewModel, StringComparison.Ordinal);
        Assert.Contains("IsCurrentWorkspace(job.WorkingDirectory)", viewModel, StringComparison.Ordinal);
        Assert.Contains("我确认停止当前选中的活动任务", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("_commands.StopAsync", viewModel, StringComparison.Ordinal);
        Assert.DoesNotContain("stdin", viewModel, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("WriteInput", viewModel, StringComparison.Ordinal);
    }

    [Fact]
    public void Self_iteration_uses_the_running_source_tree_not_the_active_user_workspace()
    {
        var source = ReadFeature("SelfIterationWorkspace.cs");
        var registration = ReadFeature("OperationsFeatureRegistration.cs");

        Assert.Contains("AppContext.BaseDirectory", source, StringComparison.Ordinal);
        Assert.Contains("TestAgent.slnx", source, StringComparison.Ordinal);
        Assert.Contains("KNetAgent.Desktop.WinUI.csproj", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Environment.CurrentDirectory", source, StringComparison.Ordinal);
        Assert.Contains("SelfIterationGuideStore", registration, StringComparison.Ordinal);
        Assert.Contains("SelfCodeIterationService", registration, StringComparison.Ordinal);
        Assert.DoesNotContain("IIterationGuideStore, MarkdownIterationGuideStore", registration,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Iteration_ui_separates_generate_review_approval_and_hash_bound_apply()
    {
        var xaml = ReadFeature("OperationsHubView.xaml");
        var viewModel = ReadFeature("OperationsViewModels.cs");

        Assert.Contains("GenerateAndValidateCommand", xaml, StringComparison.Ordinal);
        Assert.Contains("DiffPreview", xaml, StringComparison.Ordinal);
        Assert.Contains("ApplyApproved", xaml, StringComparison.Ordinal);
        Assert.Contains("ComputeProposalFingerprint", viewModel, StringComparison.Ordinal);
        Assert.Contains("_approvalFingerprint", viewModel, StringComparison.Ordinal);
        Assert.Contains("_iterations.ApplyAsync(proposal", viewModel, StringComparison.Ordinal);
        Assert.Contains("OriginalSha256", viewModel, StringComparison.Ordinal);
    }

    [Fact]
    public void Initialization_does_not_generate_or_apply_an_iteration()
    {
        var viewModel = ReadFeature("OperationsViewModels.cs");
        var initializeStart = viewModel.IndexOf("public async Task InitializeAsync", StringComparison.Ordinal);
        var initializeEnd = viewModel.IndexOf("public void CancelActiveOperation", initializeStart, StringComparison.Ordinal);
        var initializeBody = viewModel[initializeStart..initializeEnd];

        Assert.Contains("RefreshGuidesAsync", initializeBody, StringComparison.Ordinal);
        Assert.DoesNotContain("GenerateAndValidateAsync", initializeBody, StringComparison.Ordinal);
        Assert.DoesNotContain("ApplyAsync", initializeBody, StringComparison.Ordinal);
    }

    [Fact]
    public void Winui_self_iteration_is_disabled_until_preapproval_execution_is_isolated()
    {
        var workspace = ReadFeature("SelfIterationWorkspace.cs");
        var viewModel = ReadFeature("OperationsViewModels.cs");
        var xaml = ReadFeature("OperationsHubView.xaml");

        Assert.Contains("CanExecuteGeneratedCode => false", workspace, StringComparison.Ordinal);
        Assert.Contains("_selfWorkspace.CanExecuteGeneratedCode", viewModel, StringComparison.Ordinal);
        Assert.Contains("WinUI 自迭代当前安全禁用", xaml, StringComparison.Ordinal);
    }

    private static string ReadFeature(string fileName) => File.ReadAllText(Path.Combine(
        Root, "src", "KNetAgent.Desktop.WinUI", "Features", "Operations", fileName));

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "TestAgent.slnx")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate TestAgent.slnx.");
    }
}
