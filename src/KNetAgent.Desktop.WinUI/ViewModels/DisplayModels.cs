using TestAgent.Core;

namespace KNetAgent.Desktop.WinUI.ViewModels;

public sealed record MessageItemViewModel(
    ChatRole Role,
    string Content,
    DateTimeOffset Timestamp,
    string? Reasoning = null,
    string? ToolName = null)
{
    public string RoleLabel => Role switch
    {
        ChatRole.User => "你",
        ChatRole.Assistant => "K.netagent",
        ChatRole.Tool => ToolName is null ? "工具" : $"工具 · {ToolName}",
        _ => "系统"
    };

    public string TimestampText => Timestamp.ToLocalTime().ToString("HH:mm");
    public bool IsUser => Role == ChatRole.User;
    public bool IsAssistant => Role == ChatRole.Assistant;
    public bool IsTool => Role == ChatRole.Tool;
    public bool IsSystem => Role == ChatRole.System;
    public bool HasReasoning => !string.IsNullOrWhiteSpace(Reasoning);
}

public sealed record ToolSessionItemViewModel(
    string ToolName,
    string Description,
    ToolRiskLevel RiskLevel,
    ToolSessionState State,
    int TotalCalls,
    int SuccessCount,
    int FailureCount,
    string SuccessRate,
    string LastSummary,
    string LastError,
    string LastSuccessfulStrategy)
{
    public bool NeedsReview => State == ToolSessionState.NeedsReview;
    public bool IsRunning => State == ToolSessionState.Running;
}

public sealed record InspectorTabItem(string Id, string Title, string Glyph);
