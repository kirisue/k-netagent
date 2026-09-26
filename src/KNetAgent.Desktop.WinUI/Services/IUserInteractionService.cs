using TestAgent.Core;

namespace KNetAgent.Desktop.WinUI.Services;

public enum UserNotificationKind
{
    Information,
    Warning,
    Error
}

/// <summary>
/// UI boundary for modal approval and notifications. A ContentDialog implementation
/// belongs in the WinUI shell; the safe fallback below never approves a tool call.
/// </summary>
public interface IUserInteractionService
{
    Task<bool> RequestToolApprovalAsync(ToolApprovalRequest request, CancellationToken cancellationToken);

    Task NotifyAsync(
        string title,
        string message,
        UserNotificationKind kind = UserNotificationKind.Information,
        CancellationToken cancellationToken = default);
}

public sealed class DenyToolApprovalInteractionService : IUserInteractionService
{
    public Task<bool> RequestToolApprovalAsync(
        ToolApprovalRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(false);
    }

    public Task NotifyAsync(
        string title,
        string message,
        UserNotificationKind kind = UserNotificationKind.Information,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }
}
