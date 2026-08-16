using System.Collections.Concurrent;
using TestAgent.Core;
using TestAgent.Infrastructure;
using Xunit;

namespace TestAgent.Tests;

public sealed class ToolSessionTests
{
    [Fact]
    public async Task Ensure_sessions_creates_one_session_per_registered_tool_and_parent()
    {
        var store=new MemoryStore();var coordinator=new ToolSessionCoordinator(store);
        var definitions=new ToolDefinition[]{new("read_file","read",ToolRiskLevel.ReadOnly,[]),new("edit_file","edit",ToolRiskLevel.WorkspaceWrite,[])};
        var first=await coordinator.EnsureSessionsAsync("chat-a",definitions);var again=await coordinator.EnsureSessionsAsync("chat-a",definitions);var other=await coordinator.EnsureSessionsAsync("chat-b",definitions);
        Assert.Equal(2,first.Count);Assert.Equal(first.Select(x=>x.Id),again.Select(x=>x.Id));Assert.Equal(4,(await store.ListAsync()).Count);
        Assert.DoesNotContain(first.Select(x=>x.Id),x=>other.Any(y=>y.Id==x));Assert.All(first,x=>Assert.Equal(ToolSessionState.Idle,x.State));
    }

    [Fact]
    public async Task Concurrent_completions_preserve_counts_and_last_twenty_records()
    {
        var store=new MemoryStore();var coordinator=new ToolSessionCoordinator(store);await coordinator.EnsureSessionsAsync("chat",[new("read_file","read",ToolRiskLevel.ReadOnly,[])]);
        var tasks=Enumerable.Range(0,30).Select(async i=>{var request=new ToolRequest("r"+i,"read_file","{\"path\":\"README.md\"}","chat");var session=await coordinator.StartAsync(request,true);await coordinator.CompleteAsync(session,request,new(request.Id,request.Name,ToolExecutionStatus.Success,"ok",Summary:"read ok"),true);});
        await Task.WhenAll(tasks);var saved=Assert.Single(await store.ListAsync("chat"));Assert.Equal(30,saved.TotalCalls);Assert.Equal(30,saved.SuccessCount);Assert.Equal(20,saved.RecentInvocations.Count);
    }

    [Fact]
    public async Task Tool_session_redacts_sensitive_arguments()
    {
        var store=new MemoryStore();var coordinator=new ToolSessionCoordinator(store);var request=new ToolRequest("r","save_memory","{\"ApiKey\":\"sentinel-secret\",\"nested\":{\"TOKEN\":\"another-secret\"}}","chat");
        var session=await coordinator.StartAsync(request,true);await coordinator.CompleteAsync(session,request,new(request.Id,request.Name,ToolExecutionStatus.Success,"ok"),true);
        var saved=Assert.Single(await store.ListAsync());var arguments=saved.RecentInvocations[0].ArgumentsSummary;Assert.DoesNotContain("sentinel-secret",arguments);Assert.DoesNotContain("another-secret",arguments);Assert.Contains("redacted",arguments);
    }

    private sealed class MemoryStore:IToolSessionStore
    {
        private readonly ConcurrentDictionary<string,ToolSession> _items=new(StringComparer.OrdinalIgnoreCase);
        public Task<IReadOnlyList<ToolSession>> ListAsync(string? parent=null,CancellationToken ct=default)=>Task.FromResult<IReadOnlyList<ToolSession>>(_items.Values.Where(x=>parent is null||x.ParentSessionId==parent).ToArray());
        public Task<ToolSession?> GetAsync(string parent,string tool,CancellationToken ct=default)=>Task.FromResult(_items.Values.FirstOrDefault(x=>x.ParentSessionId==parent&&x.ToolName.Equals(tool,StringComparison.OrdinalIgnoreCase)));
        public Task SaveAsync(ToolSession session,CancellationToken ct=default){_items[session.Id]=session;return Task.CompletedTask;}
    }
}
