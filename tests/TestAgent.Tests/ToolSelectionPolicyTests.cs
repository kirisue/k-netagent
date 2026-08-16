using TestAgent.Core;
using Xunit;

namespace TestAgent.Tests;

public sealed class ToolSelectionPolicyTests
{
    [Fact]
    public void Small_registry_is_returned_in_full()
    {
        var tools = Enumerable.Range(0, 8).Select(x => Definition("tool_" + x)).ToArray();
        Assert.Equal(tools, ToolSelectionPolicy.Select(tools, "anything"));
    }

    [Theory]
    [InlineData("读取这个网页 https://example.com", "fetch_web_content")]
    [InlineData("请在后台运行测试", "start_background_command")]
    [InlineData("找回以前会话历史", "search_session_history")]
    [InlineData("修改代码并应用 patch", "apply_patch")]
    public void Large_registry_activates_relevant_capability(string request, string expected)
    {
        var names = new[] { "list_files", "read_file", "search_text", "fetch_web_content", "save_memory",
            "search_session_history", "edit_file", "apply_patch", "run_command", "start_background_command",
            "get_background_command", "read_background_output", "stop_background_command" };
        var tools = names.Select(Definition).Concat(Enumerable.Range(0, 90).Select(x => Definition("extra_" + x))).ToArray();
        var selected = ToolSelectionPolicy.Select(tools, request);
        Assert.Contains(selected, x => x.Name == expected);
        Assert.True(selected.Count <= 8);
        Assert.Equal(selected.Count, selected.Select(x => x.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(selected.Select(x => x.Name), ToolSelectionPolicy.Select(tools, request).Select(x => x.Name));
    }

    private static ToolDefinition Definition(string name) => new(name, name, ToolRiskLevel.ReadOnly, []);
}
