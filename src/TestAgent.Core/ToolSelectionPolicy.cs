namespace TestAgent.Core;

public static class ToolSelectionPolicy
{
    private static readonly string[] CoreTools = ["list_files", "read_file", "search_text"];

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
            Group(0,["后台","异步","持续运行","background","async","long running"],["start_background_command","get_background_command","read_background_output","stop_background_command"]),
            Group(1,["网页","网站","文档","链接","http","url","web","api docs"],["fetch_web_content"]),
            Group(2,["记忆","记住","历史","以前","memory","history"],["save_memory","search_session_history"]),
            Group(3,["修改","编辑","修复","代码","文件","implement","edit","patch","fix","code"],["edit_file","apply_patch"]),
            Group(4,["测试","构建","编译","命令","终端","test","build","command","terminal"],["run_command"])
        };
        foreach (var group in groups.Select(x => (Group:x,Score:x.Keywords.Count(keyword=>text.Contains(keyword,StringComparison.OrdinalIgnoreCase))))
                     .Where(x=>x.Score>0).OrderByDescending(x=>x.Score).ThenBy(x=>x.Group.Priority).Select(x=>x.Group))
        {
            var additions=group.Tools.Where(byName.ContainsKey)
                .Where(name=>selected.All(value=>!value.Name.Equals(name,StringComparison.OrdinalIgnoreCase))).Select(name=>byName[name]).ToArray();
            if(selected.Count+additions.Length>maxActive)continue;
            selected.AddRange(additions);
        }
        return selected;
    }

    private static CapabilityGroup Group(int priority,IReadOnlyList<string> keywords,IReadOnlyList<string> tools)=>new(priority,keywords,tools);
    private sealed record CapabilityGroup(int Priority,IReadOnlyList<string> Keywords,IReadOnlyList<string> Tools);
}
