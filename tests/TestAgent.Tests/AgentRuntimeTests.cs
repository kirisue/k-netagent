using TestAgent.Core;
using Xunit;
namespace TestAgent.Tests;
public sealed class AgentRuntimeTests
{
    [Fact] public void Context_places_system_then_memory_then_recent_history()
    {
        var history=Enumerable.Range(1,5).Select(i=>new ChatMessage(ChatRole.User,$"m{i}",DateTimeOffset.UtcNow));
        var result=AgentRuntime.BuildContext(history,[new("1","preference","use C#",true,DateTimeOffset.UtcNow)],2);
        Assert.Equal(ChatRole.System,result[0].Role);Assert.Contains("use C#",result[1].Content);Assert.Equal(["m4","m5"],result.Skip(2).Select(x=>x.Content));
    }
    [Fact] public void Context_keeps_a_tool_exchange_atomic_when_budget_would_split_it()
    {
        var now=DateTimeOffset.UtcNow;
        var history=new ChatMessage[]
        {
            new(ChatRole.User,"inspect",now),
            new(ChatRole.Assistant,"",now,ToolCalls:[new("call-1","read_file","{}"),new("call-2","read_file","{}")]),
            new(ChatRole.Tool,"one",now,"call-1","read_file"),
            new(ChatRole.Tool,"two",now,"call-2","read_file")
        };
        var result=AgentRuntime.BuildContext(history,[],2).Skip(1).ToArray();
        Assert.Equal([ChatRole.Assistant,ChatRole.Tool,ChatRole.Tool],result.Select(x=>x.Role));
        Assert.Equal(["call-1","call-2"],result.Skip(1).Select(x=>x.ToolCallId));
    }
    [Fact] public void Context_never_starts_with_an_orphan_tool_result()
    {
        var now=DateTimeOffset.UtcNow;
        var history=new ChatMessage[] { new(ChatRole.User,"keep me",now),new(ChatRole.Tool,"orphan",now,"missing","read_file") };
        var result=AgentRuntime.BuildContext(history,[],1);
        Assert.Equal([ChatRole.System,ChatRole.User],result.Select(x=>x.Role));
    }
    [Fact] public void Context_uses_configured_system_prompt()
    {
        var result=AgentRuntime.BuildContext([],[],1,systemPrompt:"Follow the configured policy.");
        Assert.StartsWith("Follow the configured policy.",result[0].Content);Assert.Contains("Security boundary",result[0].Content);
    }
    [Fact] public async Task Runtime_persists_user_and_assistant_messages()
    {
        var sessions=new MemorySessions();var runtime=new AgentRuntime(new FakeProvider(),new MemoryMemories(),sessions,new NoTools());var observer=new Observer();
        var result=await runtime.RunAsync(AgentRuntime.NewSession(),"hello",new("custom","http://localhost/v1","fake"),null,observer,CancellationToken.None);
        Assert.Equal(AgentState.Completed,result.State);Assert.Equal("answer",result.Content);Assert.Equal(2,result.Session.Messages.Count);Assert.NotNull(sessions.Value);
    }
    [Fact] public async Task Self_review_replaces_draft_only_when_reviewer_requests_revision()
    {
        var sessions=new MemorySessions();var runtime=new AgentRuntime(new RevisingProvider(),new MemoryMemories(),sessions,new NoTools());var observer=new Observer();
        var result=await runtime.RunAsync(AgentRuntime.NewSession(),"hello",new("custom","http://localhost/v1","fake",SelfReviewEnabled:true),null,observer,CancellationToken.None);
        Assert.Equal("better answer",result.Content);Assert.Equal("better answer",result.Session.Messages.Last().Content);
    }
    [Fact] public async Task Invalid_self_review_keeps_original_answer()
    {
        var runtime=new AgentRuntime(new FakeProvider(),new MemoryMemories(),new MemorySessions(),new NoTools());
        var result=await runtime.RunAsync(AgentRuntime.NewSession(),"hello",new("custom","http://localhost/v1","fake",SelfReviewEnabled:true),null,new Observer(),CancellationToken.None);
        Assert.Equal("answer",result.Content);
    }
    [Fact] public async Task Runtime_executes_tool_and_returns_result_to_model()
    {
        var provider=new ToolCallingProvider();var tools=new CapturingTools();var runtime=new AgentRuntime(provider,new MemoryMemories(),new MemorySessions(),tools);
        var result=await runtime.RunAsync(AgentRuntime.NewSession(),"read it",new("custom","http://localhost/v1","fake",SelfReviewEnabled:false),null,new Observer(),CancellationToken.None);
        Assert.Equal("final",result.Content);Assert.Single(tools.Requests);Assert.Contains(provider.Requests[1].Messages,x=>x.Role==ChatRole.Tool&&x.ToolCallId=="call-1"&&x.Content.Contains("file contents"));
    }
    [Fact] public async Task Scoped_runtime_does_not_persist_or_mutate_source_session_and_passes_allowed_paths()
    {
        var provider=new ToolCallingProvider();var tools=new CapturingTools();var sessions=new MemorySessions();
        var runtime=new AgentRuntime(provider,new MemoryMemories(),sessions,tools);var source=AgentRuntime.NewSession();
        var result=await runtime.RunAsync(source,"read it",new("custom","http://localhost/v1","fake",SelfReviewEnabled:false),null,new Observer(),CancellationToken.None,
            new(false,"Only work on the selected task node.",["src/TestAgent.Core"]));
        Assert.Equal(AgentState.Completed,result.State);Assert.Empty(source.Messages);Assert.Equal(2,result.Session.Messages.Count);
        Assert.Equal(0,sessions.SaveCount);Assert.Null(sessions.Value);
        Assert.Contains(provider.Requests[0].Messages,x=>x.Role==ChatRole.System&&x.Content=="Only work on the selected task node.");
        Assert.Equal(["src/TestAgent.Core"],tools.Requests.Single().AllowedPaths);
    }
    [Fact] public async Task Multiple_tool_calls_keep_assistant_followed_only_by_matching_tool_results()
    {
        var provider=new TwoToolProvider();var runtime=new AgentRuntime(provider,new MemoryMemories(),new MemorySessions(),new CapturingTools());
        var result=await runtime.RunAsync(AgentRuntime.NewSession(),"inspect",new("custom","http://localhost/v1","fake",SelfReviewEnabled:false),null,new Observer(),CancellationToken.None);
        Assert.Equal(AgentState.Completed,result.State);var messages=provider.Requests[1].Messages;var assistantIndex=messages.ToList().FindIndex(x=>x.Role==ChatRole.Assistant&&x.ToolCalls is {Count:2});Assert.True(assistantIndex>=0);
        Assert.Equal(ChatRole.Tool,messages[assistantIndex+1].Role);Assert.Equal("call-a",messages[assistantIndex+1].ToolCallId);Assert.Equal(ChatRole.Tool,messages[assistantIndex+2].Role);Assert.Equal("call-b",messages[assistantIndex+2].ToolCallId);
    }
    [Fact] public async Task Tool_session_telemetry_failure_does_not_prevent_actual_tool_execution()
    {
        var provider=new ToolCallingProvider();var tools=new CapturingTools();var runtime=new AgentRuntime(provider,new MemoryMemories(),new MemorySessions(),tools,new ThrowingToolSessions());
        var result=await runtime.RunAsync(AgentRuntime.NewSession(),"read it",new("custom","http://localhost/v1","fake",SelfReviewEnabled:false),null,new Observer(),CancellationToken.None);
        Assert.Equal(AgentState.Completed,result.State);Assert.Single(tools.Requests);Assert.Equal("final",result.Content);
    }
    [Fact] public async Task Equivalent_json_argument_order_is_treated_as_the_same_repeated_call()
    {
        var provider=new ReorderedRepeatedToolProvider();var tools=new CapturingTools();var runtime=new AgentRuntime(provider,new MemoryMemories(),new MemorySessions(),tools);
        var result=await runtime.RunAsync(AgentRuntime.NewSession(),"inspect",new("custom","http://localhost/v1","fake",SelfReviewEnabled:false),null,new Observer(),CancellationToken.None);
        Assert.Equal(AgentState.Completed,result.State);Assert.Equal(2,tools.Requests.Count);Assert.Contains(provider.Requests.Last().Messages,x=>x.Role==ChatRole.Tool&&x.Content.Contains("repeated identical",StringComparison.OrdinalIgnoreCase));
    }
    private sealed class FakeProvider:IModelProvider{public async IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest r,[System.Runtime.CompilerServices.EnumeratorCancellation]CancellationToken c){yield return new(StreamEventKind.Reasoning,"think");yield return new(StreamEventKind.Content,"answer");yield return new(StreamEventKind.Usage,Tokens:3);await Task.CompletedTask;}}
    private sealed class RevisingProvider:IModelProvider{private int _calls;public async IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest r,[System.Runtime.CompilerServices.EnumeratorCancellation]CancellationToken c){_calls++;yield return new(StreamEventKind.Content,_calls==1?"draft":"{\"accept\":false,\"revisedAnswer\":\"better answer\"}");await Task.CompletedTask;}}
    private sealed class ToolCallingProvider:IModelProvider{public List<ChatRequest> Requests{get;}=[];public async IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest r,[System.Runtime.CompilerServices.EnumeratorCancellation]CancellationToken c){Requests.Add(r);if(Requests.Count==1)yield return new(StreamEventKind.Completed,ToolCall:new("call-1","read_file","{\"path\":\"README.md\"}"));else yield return new(StreamEventKind.Content,"final");await Task.CompletedTask;}}
    private sealed class TwoToolProvider:IModelProvider{public List<ChatRequest> Requests{get;}=[];public async IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest r,[System.Runtime.CompilerServices.EnumeratorCancellation]CancellationToken c){Requests.Add(r);if(Requests.Count==1){yield return new(StreamEventKind.Completed,ToolCall:new("call-a","read_file","{\"path\":\"README.md\"}"));yield return new(StreamEventKind.Completed,ToolCall:new("call-b","read_file","{\"path\":\"README.md\"}"));}else yield return new(StreamEventKind.Content,"done");await Task.CompletedTask;}}
    private sealed class ReorderedRepeatedToolProvider:IModelProvider{public List<ChatRequest> Requests{get;}=[];public async IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest r,[System.Runtime.CompilerServices.EnumeratorCancellation]CancellationToken c){Requests.Add(r);var call=Requests.Count switch{1=>new ModelToolCall("one","read_file","{\"path\":\"README.md\",\"startLine\":1}"),2=>new ModelToolCall("two","read_file","{\"startLine\":1,\"path\":\"README.md\"}"),3=>new ModelToolCall("three","read_file","{ \"path\": \"README.md\", \"startLine\": 1 }"),_=>null};if(call is not null)yield return new(StreamEventKind.Completed,ToolCall:call);else yield return new(StreamEventKind.Content,"done");await Task.CompletedTask;}}
    private sealed class MemoryMemories:IMemoryStore{public Task DeleteAsync(string id,CancellationToken c=default)=>Task.CompletedTask;public Task<IReadOnlyList<MemoryEntry>> ListAsync(CancellationToken c=default)=>Task.FromResult<IReadOnlyList<MemoryEntry>>([]);public Task SaveAsync(MemoryEntry m,CancellationToken c=default)=>Task.CompletedTask;}
    private sealed class MemorySessions:ISessionStore{public ChatSession? Value;public int SaveCount;public Task DeleteAsync(string id,CancellationToken c=default)=>Task.CompletedTask;public Task<ChatSession?> GetAsync(string id,CancellationToken c=default)=>Task.FromResult(Value);public Task<IReadOnlyList<ChatSession>> ListAsync(CancellationToken c=default)=>Task.FromResult<IReadOnlyList<ChatSession>>(Value is null?[]:[Value]);public Task SaveAsync(ChatSession s,CancellationToken c=default){SaveCount++;Value=s;return Task.CompletedTask;}}
    private sealed class Observer:IAgentObserver{public ValueTask OnEventAsync(StreamEvent v)=>ValueTask.CompletedTask;public ValueTask OnStateAsync(AgentState s)=>ValueTask.CompletedTask;public ValueTask<bool> RequestToolApprovalAsync(ToolApprovalRequest r,CancellationToken c)=>ValueTask.FromResult(true);}
    private sealed class NoTools:IToolExecutionService{public IReadOnlyList<ToolDefinition> GetDefinitions()=>[];public Task<ToolResult> ExecuteAsync(ToolRequest r,IAgentObserver o,CancellationToken c=default)=>Task.FromResult(new ToolResult(r.Id,r.Name,ToolExecutionStatus.Failed,"","No tools."));}
    private sealed class CapturingTools:IToolExecutionService{public List<ToolRequest> Requests{get;}=[];public IReadOnlyList<ToolDefinition> GetDefinitions()=>[new("read_file","read",ToolRiskLevel.ReadOnly,[])];public Task<ToolResult> ExecuteAsync(ToolRequest r,IAgentObserver o,CancellationToken c=default){Requests.Add(r);return Task.FromResult(new ToolResult(r.Id,r.Name,ToolExecutionStatus.Success,"file contents"));}}
    private sealed class ThrowingToolSessions:IToolSessionCoordinator{public Task<IReadOnlyList<ToolSession>> EnsureSessionsAsync(string p,IReadOnlyList<ToolDefinition>d,CancellationToken c=default)=>Task.FromException<IReadOnlyList<ToolSession>>(new IOException("unavailable"));public Task<ToolSession> StartAsync(ToolRequest r,bool a,CancellationToken c=default)=>Task.FromException<ToolSession>(new IOException("unavailable"));public Task<ToolSession> CompleteAsync(ToolSession s,ToolRequest r,ToolResult x,bool a,CancellationToken c=default)=>Task.FromException<ToolSession>(new IOException("unavailable"));public Task<string?> GetStrategyHintAsync(string p,string n,CancellationToken c=default)=>Task.FromException<string?>(new IOException("unavailable"));public Task<IReadOnlyList<ToolSession>> ListAsync(string? p=null,CancellationToken c=default)=>Task.FromException<IReadOnlyList<ToolSession>>(new IOException("unavailable"));public Task<ToolSession>AcknowledgeNeedsReviewAsync(string p,string n,CancellationToken c=default)=>Task.FromException<ToolSession>(new IOException("unavailable"));}
}
