using TestAgent.Core;
using TestAgent.Infrastructure;

namespace KNetAgent.Desktop.WinUI.Features.Operations;

/// <summary>
/// Resolves only the K.netagent source tree containing the running development build.
/// It never uses the process working directory or the user-selected Agent workspace.
/// Installed builds therefore expose self-iteration as unavailable instead of editing
/// an unrelated customer project.
/// </summary>
public sealed class SelfIterationWorkspace
{
    private SelfIterationWorkspace(WorkspaceLocator? locator, string unavailableReason)
    {
        Locator = locator;
        UnavailableReason = unavailableReason;
    }

    public WorkspaceLocator? Locator { get; }
    public bool IsAvailable => Locator is not null;
    public bool CanExecuteGeneratedCode => false;
    public string ExecutionDisabledReason =>
        "WinUI 自迭代暂时禁用：当前验证器会以登录用户权限和网络环境执行模型生成的构建/测试，尚未达到审批前安全执行要求。";
    public string UnavailableReason { get; }
    public string DisplayLabel => Locator is null
        ? "当前安装不包含可验证的 K.netagent 源码工作区"
        : Locator.Root;

    public static SelfIterationWorkspace Discover()
    {
        try
        {
            var start = new DirectoryInfo(Path.GetFullPath(AppContext.BaseDirectory));
            for (var directory = start; directory is not null; directory = directory.Parent)
            {
                var root = directory.FullName;
                if (!File.Exists(Path.Combine(root, "TestAgent.slnx")) ||
                    !File.Exists(Path.Combine(root, "src", "TestAgent.Core", "TestAgent.Core.csproj")) ||
                    !File.Exists(Path.Combine(root, "src", "TestAgent.Infrastructure", "TestAgent.Infrastructure.csproj")) ||
                    !File.Exists(Path.Combine(root, "src", "KNetAgent.Desktop.WinUI", "KNetAgent.Desktop.WinUI.csproj")))
                    continue;

                return new SelfIterationWorkspace(new WorkspaceLocator(root), string.Empty);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                           ArgumentException or NotSupportedException or InvalidDataException)
        {
            return new SelfIterationWorkspace(null,
                "无法验证开发源码根目录：" + SafeMessage(exception));
        }

        return new SelfIterationWorkspace(null,
            "安装版未携带完整 K.netagent 源码；为防止修改用户项目，自迭代已禁用。请从源码启动开发版。" );
    }

    private static string SafeMessage(Exception exception) =>
        exception.Message.Replace('\r', ' ').Replace('\n', ' ').Trim();
}

public sealed class SelfIterationGuideStore(SelfIterationWorkspace selfWorkspace) : IIterationGuideStore
{
    public Task<IReadOnlyList<IterationGuide>> ListAsync(CancellationToken ct = default) =>
        selfWorkspace.Locator is { } locator
            ? new MarkdownIterationGuideStore(locator).ListAsync(ct)
            : Task.FromResult<IReadOnlyList<IterationGuide>>([]);
}

public sealed class SelfCodeIterationService(
    SelfIterationWorkspace selfWorkspace) : ICodeIterationService
{
    public Task<IterationProposal> GenerateAsync(
        IterationGuide guide,
        ProviderSettings settings,
        string? apiKey,
        CancellationToken ct = default) =>
        Task.FromException<IterationProposal>(new InvalidOperationException(
            selfWorkspace.ExecutionDisabledReason));

    public Task<IterationApplyResult> ApplyAsync(
        IterationProposal proposal,
        CancellationToken ct = default) =>
        Task.FromException<IterationApplyResult>(new InvalidOperationException(
            selfWorkspace.ExecutionDisabledReason));
}
