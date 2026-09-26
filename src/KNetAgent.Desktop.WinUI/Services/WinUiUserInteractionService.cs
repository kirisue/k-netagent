using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TestAgent.Core;

namespace KNetAgent.Desktop.WinUI.Services;

/// <summary>
/// ContentDialog implementation for production. Register this concrete type and
/// IUserInteractionService to the same singleton, then call Attach after the root
/// element has loaded and owns a XamlRoot.
/// </summary>
public sealed class WinUiUserInteractionService(IUiDispatcher dispatcher) : IUserInteractionService
{
    private readonly SemaphoreSlim _dialogGate = new(1, 1);
    private XamlRoot? _xamlRoot;

    public bool IsAttached => _xamlRoot is not null;

    public void Attach(XamlRoot xamlRoot)
    {
        ArgumentNullException.ThrowIfNull(xamlRoot);
        _xamlRoot = xamlRoot;
    }

    public void Detach() => _xamlRoot = null;

    public async Task<bool> RequestToolApprovalAsync(
        ToolApprovalRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var result = await ShowDialogAsync(
                title: "K.netagent 工具审批",
                message: $"工具：{request.ToolName}\n风险：{RiskLabel(request.RiskLevel)}\n\n参数摘要：\n{request.Summary}",
                primaryButton: "允许本次",
                closeButton: "拒绝",
                cancellationToken);
            return result == ContentDialogResult.Primary;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Dialog/root failures are a rejection, never implicit approval.
            return false;
        }
    }

    public async Task NotifyAsync(
        string title,
        string message,
        UserNotificationKind kind = UserNotificationKind.Information,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            await ShowDialogAsync(
                string.IsNullOrWhiteSpace(title) ? "K.netagent" : title,
                message,
                primaryButton: null,
                closeButton: "关闭",
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Notifications must not terminate the Agent loop or approve anything.
        }
    }

    private async Task<ContentDialogResult> ShowDialogAsync(
        string title,
        string message,
        string? primaryButton,
        string closeButton,
        CancellationToken cancellationToken)
    {
        await _dialogGate.WaitAsync(cancellationToken);
        try
        {
            var completion = new TaskCompletionSource<ContentDialogResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            dispatcher.Post(async () =>
            {
                ContentDialog? dialog = null;
                CancellationTokenRegistration cancellationRegistration = default;
                try
                {
                    var root = _xamlRoot;
                    if (root is null)
                    {
                        completion.TrySetResult(ContentDialogResult.None);
                        return;
                    }

                    dialog = new ContentDialog
                    {
                        XamlRoot = root,
                        Title = title,
                        Content = new ScrollViewer
                        {
                            MaxHeight = 420,
                            Content = new TextBlock
                            {
                                Text = Truncate(message, 12_000),
                                TextWrapping = TextWrapping.Wrap,
                                IsTextSelectionEnabled = true
                            }
                        },
                        PrimaryButtonText = primaryButton ?? string.Empty,
                        CloseButtonText = closeButton,
                        DefaultButton = ContentDialogButton.Close
                    };

                    cancellationRegistration = cancellationToken.Register(() =>
                        dispatcher.Post(() =>
                        {
                            try { dialog?.Hide(); }
                            catch { /* A closing dialog remains a safe rejection. */ }
                        }));

                    var result = await dialog.ShowAsync();
                    if (cancellationToken.IsCancellationRequested)
                        completion.TrySetCanceled(cancellationToken);
                    else
                        completion.TrySetResult(result);
                }
                catch (Exception ex)
                {
                    // A missing/closing XamlRoot must never turn into implicit approval.
                    completion.TrySetException(ex);
                }
                finally
                {
                    cancellationRegistration.Dispose();
                }
            });

            return await completion.Task;
        }
        finally
        {
            _dialogGate.Release();
        }
    }

    private static string RiskLabel(ToolRiskLevel riskLevel) => riskLevel switch
    {
        ToolRiskLevel.ReadOnly => "只读",
        ToolRiskLevel.WorkspaceWrite => "工作区写入",
        ToolRiskLevel.ProcessExecution => "进程执行",
        ToolRiskLevel.Destructive => "破坏性操作",
        ToolRiskLevel.ExternalNetwork => "外部网络",
        ToolRiskLevel.SensitiveCapture => "敏感画面捕获",
        ToolRiskLevel.LocalEnvironmentRead => "本机环境读取",
        _ => riskLevel.ToString()
    };

    private static string Truncate(string value, int maximumLength) =>
        value.Length <= maximumLength ? value : value[..maximumLength] + "\n…内容已截断";
}
