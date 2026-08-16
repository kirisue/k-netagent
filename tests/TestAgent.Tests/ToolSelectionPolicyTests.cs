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
    [InlineData("打开浏览器读取页面", "open_browser_snapshot")]
    [InlineData("打开浏览器读取页面", "read_browser_dom")]
    [InlineData("打开浏览器读取页面", "capture_browser_viewport")]
    [InlineData("请在后台运行测试", "start_background_command")]
    [InlineData("找回以前会话历史", "search_session_history")]
    [InlineData("修改代码并应用 patch", "apply_patch")]
    [InlineData("查看 VS Code 工作区任务", "list_vscode_configured_tasks")]
    [InlineData("查看 VS Code 扩展推荐", "list_vscode_extension_recommendations")]
    [InlineData("查询 VS Code API 官方文档", "get_vscode_docs_link")]
    [InlineData("查看 VS Code 当前编辑器和光标位置", "get_vscode_active_editor")]
    [InlineData("读取 VS Code Problems 诊断", "get_vscode_diagnostics")]
    [InlineData("列出 VS Code fetchTasks 可用任务", "list_vscode_available_tasks")]
    [InlineData("查看 VS Code 已安装扩展清单", "list_vscode_installed_extensions")]
    public void Large_registry_activates_relevant_capability(string request, string expected)
    {
        var names = new[] { "list_files", "read_file", "search_text", "fetch_web_content", "save_memory",
            "search_session_history", "edit_file", "apply_patch", "run_command", "start_background_command",
            "get_background_command", "read_background_output", "stop_background_command", "get_vscode_workspace_status",
            "list_vscode_configured_tasks", "list_vscode_extension_recommendations", "get_vscode_docs_link",
            "get_vscode_active_editor", "get_vscode_diagnostics", "list_vscode_available_tasks",
            "list_vscode_installed_extensions", "open_browser_snapshot", "read_browser_dom",
            "capture_browser_viewport" };
        var tools = names.Select(Definition).Concat(Enumerable.Range(0, 90).Select(x => Definition("extra_" + x))).ToArray();
        var selected = ToolSelectionPolicy.Select(tools, request);
        Assert.Contains(selected, x => x.Name == expected);
        Assert.True(selected.Count <= 8);
        Assert.Equal(selected.Count, selected.Select(x => x.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(selected.Select(x => x.Name), ToolSelectionPolicy.Select(tools, request).Select(x => x.Name));
    }

    [Fact]
    public void Background_capability_is_never_exposed_without_its_status_and_stop_tools()
    {
        var names=new[]{"list_files","read_file","search_text","start_background_command","get_background_command","read_background_output","stop_background_command","get_vscode_workspace_status","list_vscode_configured_tasks","list_vscode_extension_recommendations","get_vscode_docs_link"};
        var tools=names.Select(Definition).Concat(Enumerable.Range(0,20).Select(x=>Definition("extra_"+x))).ToArray();
        var selected=ToolSelectionPolicy.Select(tools,"VS Code vscode .vscode tasks.json 扩展推荐 extensions.json VS Code API 官方文档 后台");
        var background=selected.Where(x=>x.Name.Contains("background",StringComparison.OrdinalIgnoreCase)).ToArray();
        Assert.True(background.Length is 0 or 4);
    }

    [Fact]
    public void Browser_capability_is_selected_as_one_atomic_three_tool_session_group()
    {
        var names=new[]{"list_files","read_file","search_text","fetch_web_content","open_browser_snapshot","read_browser_dom","capture_browser_viewport","save_memory","search_session_history","edit_file","apply_patch","run_command"};
        var tools=names.Select(Definition).Concat(Enumerable.Range(0,30).Select(x=>Definition("extra_"+x))).ToArray();
        var selected=ToolSelectionPolicy.Select(tools,"请打开浏览器页面并读取 DOM 截图");
        var browser=selected.Where(x=>x.Name is "open_browser_snapshot" or "read_browser_dom" or "capture_browser_viewport").ToArray();
        Assert.Equal(3,browser.Length);
        Assert.True(selected.Count<=8);

        var constrained=ToolSelectionPolicy.Select(tools,"打开 browser 页面",maxActive:6);
        Assert.Equal(3,constrained.Count(x=>x.Name is "open_browser_snapshot" or "read_browser_dom" or "capture_browser_viewport"));
        Assert.True(constrained.Count<=6);
    }

    private static ToolDefinition Definition(string name) => new(name, name, ToolRiskLevel.ReadOnly, []);
}
