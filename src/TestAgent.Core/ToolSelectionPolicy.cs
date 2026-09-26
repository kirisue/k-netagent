namespace TestAgent.Core;

public static class ToolSelectionPolicy
{
    private static readonly string[] CoreTools =
        ["list_files", "read_file", "search_text", "call_mcp_peer_tool"];

    public static IReadOnlyList<ToolDefinition> Select(
        IReadOnlyList<ToolDefinition> all, string userMessage, int maxActive = 8)
    {
        if (all.Count <= 12) return all;
        maxActive = Math.Clamp(maxActive, 4, 12);
        var text = userMessage ?? "";
        var byName = all.ToDictionary(x => x.Name, StringComparer.OrdinalIgnoreCase);
        var selected = CoreTools.Where(byName.ContainsKey).Select(x => byName[x]).ToList();
        var groups = new[]
        {
            Group(-1,["事件查看器","事件日志","系统日志","windows 事件","windows event","event log","event viewer","eventid"],["list_windows_event_channels","query_windows_events"],atomic:true),
            Group(-1,["windows 服务","windows服务","系统服务","服务状态","service control manager","scm","service status","windows service"],["query_windows_services"]),
            Group(0,["mcp","codex","claude","claude code","外部 agent","其他 agent","别的 agent","协作","借用工具","peer agent"],["list_mcp_peers","list_mcp_peer_tools"]),
            Group(0,["后台","异步","持续运行","background","async","long running"],["start_background_command","get_background_command","read_background_output","stop_background_command"],atomic:true),
            Group(0,["当前编辑器","活动编辑器","光标位置","可见行","active editor","current editor","selection"],["get_vscode_active_editor"]),
            Group(0,["诊断","问题面板","错误列表","diagnostic","diagnostics","problems"],["get_vscode_diagnostics"]),
            Group(0,["可用任务","实时任务","任务提供器","available tasks","fetchtasks","task provider"],["list_vscode_available_tasks"]),
            Group(0,["已安装扩展","扩展清单","installed extension","installed extensions","extension list"],["list_vscode_installed_extensions"]),
            Group(1,["vs code","vscode",".vscode","tasks.json","工作区任务","workspace task"],["get_vscode_workspace_status","list_vscode_configured_tasks"]),
            Group(2,["vs code 扩展","vscode extension","extensions.json","扩展建议","扩展推荐","extension recommendation"],["list_vscode_extension_recommendations"]),
            Group(3,["vs code api","vscode api","vs code 文档","vscode docs","官方文档"],["get_vscode_docs_link"]),
            Group(4,["网页","网站","页面","浏览器","导航","链接","http","url","web","browser"],["open_browser_snapshot","read_browser_dom","capture_browser_viewport"],atomic:true),
            Group(4,["网页","网站","页面","浏览器","导航","链接","http","url","web","browser","api docs"],["fetch_web_content"]),
            Group(5,["记忆","记住","历史","以前","memory","history"],["save_memory","search_session_history"]),
            Group(6,["修改","编辑","修复","代码","文件","implement","edit","patch","fix","code"],["edit_file","apply_patch"]),
            Group(7,["测试","构建","编译","命令","终端","test","build","command","terminal"],["run_command"])
        };
        foreach (var group in groups.Select(x => (Group:x,Score:x.Keywords.Count(keyword=>text.Contains(keyword,StringComparison.OrdinalIgnoreCase))))
                     .Where(x=>x.Score>0).OrderByDescending(x=>x.Score).ThenBy(x=>x.Group.Priority).Select(x=>x.Group))
        {
            var additions=group.Tools.Where(byName.ContainsKey)
                .Where(name=>selected.All(value=>!value.Name.Equals(name,StringComparison.OrdinalIgnoreCase))).Select(name=>byName[name]).ToArray();
            if(group.Atomic&&selected.Count+additions.Length>maxActive)continue;
            foreach(var addition in additions)
            {
                if(selected.Count>=maxActive)break;
                selected.Add(addition);
            }
        }
        return selected;
    }

    private static CapabilityGroup Group(int priority,IReadOnlyList<string> keywords,IReadOnlyList<string> tools,bool atomic=false)=>new(priority,keywords,tools,atomic);
    private sealed record CapabilityGroup(int Priority,IReadOnlyList<string> Keywords,IReadOnlyList<string> Tools,bool Atomic);
}
