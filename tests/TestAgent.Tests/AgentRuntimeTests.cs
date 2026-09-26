using TestAgent.Core;
using System.Text.Json;
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
    [Fact] public async Task Runtime_rejects_a_session_from_another_workspace_before_mutation()
    {
        var provider=new CapturingImageProvider();var sessions=new MemorySessions();var source=AgentRuntime.NewSession("workspace-a");
        var runtime=new AgentRuntime(provider,new MemoryMemories(),sessions,new NoTools());

        await Assert.ThrowsAsync<InvalidOperationException>(()=>runtime.RunAsync(source,"hello",
            new("custom","https://model.example/v1","fake",SelfReviewEnabled:false),null,new Observer(),CancellationToken.None,
            new AgentRunOptions(WorkspaceId:"workspace-b")));

        Assert.Empty(source.Messages);Assert.Equal(0,sessions.SaveCount);Assert.Empty(provider.Requests);
    }
    [Fact] public async Task Runtime_binds_a_legacy_session_and_injects_only_matching_workspace_memory()
    {
        var now=DateTimeOffset.UtcNow;var provider=new CapturingImageProvider();var sessions=new MemorySessions();
        var memories=new StaticMemories([
            new("user","user","USER-MARKER",true,now,MemoryScope.User),
            new("project-a","project a","PROJECT-A-MARKER",true,now,MemoryScope.Project,WorkspaceId:"workspace-a"),
            new("project-b","project b","PROJECT-B-MARKER",true,now,MemoryScope.Project,WorkspaceId:"workspace-b")]);
        var runtime=new AgentRuntime(provider,memories,sessions,new NoTools());var legacy=AgentRuntime.NewSession();

        var result=await runtime.RunAsync(legacy,"hello",
            new("custom","https://model.example/v1","fake",SelfReviewEnabled:false),null,new Observer(),CancellationToken.None,
            new AgentRunOptions(WorkspaceId:"workspace-a"));

        Assert.Equal("workspace-a",result.Session.WorkspaceId);Assert.Equal(2,result.Session.Version);
        Assert.Equal("workspace-a",sessions.Value!.WorkspaceId);
        var context=string.Join("\n",Assert.Single(provider.Requests).Messages.Select(message=>message.Content));
        Assert.Contains("USER-MARKER",context);Assert.Contains("PROJECT-A-MARKER",context);Assert.DoesNotContain("PROJECT-B-MARKER",context);
    }
    [Fact] public async Task Runtime_forwards_image_only_to_provider_and_never_persists_image_bytes()
    {
        var bytes=new byte[]{9,8,7,6};var image=new ImageInput("image/png",bytes,"HASH",1,1);var provider=new CapturingImageProvider();var sessions=new MemorySessions();
        var runtime=new AgentRuntime(provider,new MemoryMemories(),sessions,new NoTools());
        var result=await runtime.RunAsync(AgentRuntime.NewSession(),"analyze",new("custom","https://model.example/v1","vision",SelfReviewEnabled:true,SupportsImageInput:true),null,new Observer(),CancellationToken.None,new AgentRunOptions(Images:[image]));
        Assert.Equal(AgentState.Completed,result.State);var request=Assert.Single(provider.Requests);Assert.Same(image,Assert.Single(request.Images!));
        var persisted=JsonSerializer.Serialize(sessions.Value);Assert.DoesNotContain(Convert.ToBase64String(bytes),persisted);Assert.DoesNotContain("image/png",persisted);
    }
    [Fact] public async Task Unsupported_image_is_rejected_before_session_mutation_or_persistence()
    {
        var sessions=new MemorySessions();var source=AgentRuntime.NewSession();var runtime=new AgentRuntime(new CapturingImageProvider(),new MemoryMemories(),sessions,new NoTools());
        await Assert.ThrowsAsync<InvalidOperationException>(()=>runtime.RunAsync(source,"analyze",new("custom","https://model.example/v1","text-only",SupportsImageInput:false),null,new Observer(),CancellationToken.None,new AgentRunOptions(Images:[new("image/png",[1],"HASH",1,1)])));
        Assert.Empty(source.Messages);Assert.Equal(0,sessions.SaveCount);
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
    [Fact] public async Task Runtime_keeps_current_image_available_across_the_bounded_tool_loop()
    {
        var image=new ImageInput("image/png",[1],"HASH",1,1);var provider=new ToolCallingProvider();var runtime=new AgentRuntime(provider,new MemoryMemories(),new MemorySessions(),new CapturingTools());
        var result=await runtime.RunAsync(AgentRuntime.NewSession(),"inspect image",new("custom","http://localhost/v1","vision",SelfReviewEnabled:false,SupportsImageInput:true),null,new Observer(),CancellationToken.None,new AgentRunOptions(Images:[image]));
        Assert.Equal(AgentState.Completed,result.State);Assert.Equal(2,provider.Requests.Count);Assert.All(provider.Requests,request=>Assert.Same(image,Assert.Single(request.Images!)));
    }
    [Fact] public async Task Image_tool_loop_is_bounded_to_four_model_requests()
    {
        var image=new ImageInput("image/png",[1],"HASH",1,1);var provider=new AlwaysToolProvider();var runtime=new AgentRuntime(provider,new MemoryMemories(),new MemorySessions(),new CapturingTools());
        var result=await runtime.RunAsync(AgentRuntime.NewSession(),"inspect image",new("custom","http://localhost/v1","vision",SelfReviewEnabled:false,SupportsImageInput:true),null,new Observer(),CancellationToken.None,new AgentRunOptions(Images:[image]));
        Assert.Equal(AgentState.Completed,result.State);Assert.Equal("bounded final",result.Content);Assert.Equal(4,provider.Requests.Count);Assert.All(provider.Requests,request=>Assert.Same(image,Assert.Single(request.Images!)));
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
    [Fact] public async Task Escalation_success_synthesizes_silently_and_does_not_persist_raw_peer_output()
    {
        const string rawPeerOutput="RAW UNTRUSTED PEER EVIDENCE";var provider=new EscalationProvider();
        var tools=new EscalationTools(new("ignored","ignored",ToolExecutionStatus.Success,rawPeerOutput));
        var evaluator=new FakeEscalationEvaluator(new(true,85,["verification failed"],"codex-local","Review the draft and provide read-only evidence."));
        var observer=new CapturingObserver();var runtime=new AgentRuntime(provider,new MemoryMemories(),new MemorySessions(),tools,escalationEvaluator:evaluator);
        var result=await runtime.RunAsync(AgentRuntime.NewSession(),"finish the task",new("custom","http://localhost/v1","fake",SelfReviewEnabled:false),null,observer,CancellationToken.None);
        Assert.Equal("synthesized answer",result.Content);Assert.Equal(1,evaluator.CallCount);Assert.Single(tools.Requests);
        Assert.Equal("call_mcp_peer_tool",tools.Requests[0].Name);Assert.Equal(2,provider.Requests.Count);Assert.Empty(provider.Requests[1].Tools!);
        Assert.Contains(rawPeerOutput,provider.Requests[1].Messages.Last().Content);Assert.DoesNotContain(rawPeerOutput,JsonSerializer.Serialize(result.Session));
        Assert.Contains(observer.Events,x=>x.Kind==StreamEventKind.Escalation&&x.Escalation?.Score==85);
        Assert.Contains(observer.Events,x=>x.Kind==StreamEventKind.Revision&&x.Text=="synthesized answer");
        using var arguments=JsonDocument.Parse(tools.Requests[0].ArgumentsJson);
        Assert.Equal("codex-local",arguments.RootElement.GetProperty("peerId").GetString());Assert.Equal("codex",arguments.RootElement.GetProperty("toolName").GetString());
    }
    [Fact] public async Task Rejected_escalation_keeps_draft_and_does_not_request_synthesis()
    {
        var provider=new EscalationProvider();var tools=new EscalationTools(new("ignored","ignored",ToolExecutionStatus.Blocked,"","User rejected.",ErrorCode:"user_rejected"));
        var evaluator=new FakeEscalationEvaluator(new(true,90,["needs specialist"],"codex-local","Review it."));
        var runtime=new AgentRuntime(provider,new MemoryMemories(),new MemorySessions(),tools,escalationEvaluator:evaluator);
        var result=await runtime.RunAsync(AgentRuntime.NewSession(),"finish the task",new("custom","http://localhost/v1","fake",SelfReviewEnabled:false),null,new Observer(),CancellationToken.None);
        Assert.Equal("draft answer",result.Content);Assert.Single(provider.Requests);Assert.Single(tools.Requests);
        Assert.False(result.Escalation!.ShouldEscalate);Assert.Equal("user_rejected",result.Escalation.SuppressedReason);
    }
    [Fact] public async Task Escalation_is_suppressed_when_gateway_tool_is_unavailable()
    {
        var evaluator=new FakeEscalationEvaluator(new(true,90,["needs specialist"],"codex-local","Review it."));
        var runtime=new AgentRuntime(new FakeProvider(),new MemoryMemories(),new MemorySessions(),new NoTools(),escalationEvaluator:evaluator);
        var result=await runtime.RunAsync(AgentRuntime.NewSession(),"finish the task",new("custom","http://localhost/v1","fake",SelfReviewEnabled:false),null,new Observer(),CancellationToken.None);
        Assert.Equal("answer",result.Content);Assert.Equal(1,evaluator.CallCount);Assert.False(result.Escalation!.ShouldEscalate);
        Assert.Equal("escalation_tool_unavailable",result.Escalation.SuppressedReason);
    }
    [Fact] public async Task Image_run_does_not_add_an_unbounded_fifth_model_request_for_escalation()
    {
        var provider=new EscalationProvider();var tools=new EscalationTools(new("ignored","ignored",ToolExecutionStatus.Success,"peer"));
        var evaluator=new FakeEscalationEvaluator(new(true,100,["needs specialist"],"codex-local","Review it."));
        var runtime=new AgentRuntime(provider,new MemoryMemories(),new MemorySessions(),tools,escalationEvaluator:evaluator);
        var image=new ImageInput("image/png",[1],"HASH",1,1);
        var result=await runtime.RunAsync(AgentRuntime.NewSession(),"inspect",new("custom","http://localhost/v1","vision",SelfReviewEnabled:false,SupportsImageInput:true),null,new Observer(),CancellationToken.None,new AgentRunOptions(Images:[image]));
        Assert.Equal("draft answer",result.Content);Assert.Single(provider.Requests);Assert.Empty(tools.Requests);
        Assert.False(result.Escalation!.ShouldEscalate);Assert.Equal("image_escalation_not_supported",result.Escalation.SuppressedReason);
    }
    [Fact] public async Task Failed_escalation_is_attempted_only_once_and_keeps_draft()
    {
        var provider=new EscalationProvider();var tools=new EscalationTools(new("ignored","ignored",ToolExecutionStatus.Failed,"","peer failed",ErrorCode:"peer_call_failed"));
        var evaluator=new FakeEscalationEvaluator(new(true,90,["verification failed"],"codex-local","Review it."));
        var runtime=new AgentRuntime(provider,new MemoryMemories(),new MemorySessions(),tools,escalationEvaluator:evaluator);
        var result=await runtime.RunAsync(AgentRuntime.NewSession(),"finish the task",new("custom","http://localhost/v1","fake",SelfReviewEnabled:false),null,new Observer(),CancellationToken.None);
        Assert.Equal("draft answer",result.Content);Assert.Equal(1,evaluator.CallCount);Assert.Single(tools.Requests);Assert.Single(provider.Requests);
        Assert.False(result.Escalation!.ShouldEscalate);Assert.Equal("peer_call_failed",result.Escalation.SuppressedReason);
    }
    [Fact] public async Task Cancelled_run_never_evaluates_or_calls_escalation()
    {
        var tools=new EscalationTools(new("ignored","ignored",ToolExecutionStatus.Success,"peer"));
        var evaluator=new FakeEscalationEvaluator(new(true,100,["requested"],"codex-local","Review it."));
        var runtime=new AgentRuntime(new FakeProvider(),new MemoryMemories(),new MemorySessions(),tools,escalationEvaluator:evaluator);
        using var cancellation=new CancellationTokenSource();cancellation.Cancel();
        var result=await runtime.RunAsync(AgentRuntime.NewSession(),"finish it",new("custom","http://localhost/v1","fake",SelfReviewEnabled:false),null,new Observer(),cancellation.Token);
        Assert.Equal(AgentState.Cancelled,result.State);Assert.Equal(0,evaluator.CallCount);Assert.Empty(tools.Requests);
    }
    [Fact] public async Task Earlier_user_rejection_hard_suppresses_automatic_escalation()
    {
        var provider=new ToolCallingProvider();var tools=new RejectingInitialTools();
        var evaluator=new FakeEscalationEvaluator(new(true,100,["tool failed"],"codex-local","Ask another Agent."));
        var runtime=new AgentRuntime(provider,new MemoryMemories(),new MemorySessions(),tools,escalationEvaluator:evaluator);
        var result=await runtime.RunAsync(AgentRuntime.NewSession(),"read it",new("custom","http://localhost/v1","fake",SelfReviewEnabled:false),null,new Observer(),CancellationToken.None);
        Assert.Equal("final",result.Content);Assert.Single(tools.Requests);Assert.Equal("read_file",tools.Requests[0].Name);
        Assert.False(result.Escalation!.ShouldEscalate);Assert.Equal("user_rejected",result.Escalation.SuppressedReason);
        Assert.Contains(evaluator.Context!.Signals,x=>x.Kind==EscalationSignalKind.UserRejected);
    }
    [Fact] public async Task Runtime_never_auto_repeats_a_peer_call_already_attempted_by_the_model()
    {
        var provider=new PeerToolCallingProvider();var tools=new EscalationTools(new("ignored","ignored",ToolExecutionStatus.Success,"peer evidence"));
        var evaluator=new FakeEscalationEvaluator(new(true,100,["explicit"],"codex-local","Review again."));
        var runtime=new AgentRuntime(provider,new MemoryMemories(),new MemorySessions(),tools,escalationEvaluator:evaluator);
        var result=await runtime.RunAsync(AgentRuntime.NewSession(),"ask Codex",new("custom","http://localhost/v1","fake",SelfReviewEnabled:false),null,new Observer(),CancellationToken.None);
        Assert.Equal("model final",result.Content);Assert.Single(tools.Requests);Assert.Equal("peer_already_called",result.Escalation!.SuppressedReason);
    }
    private sealed class FakeProvider:IModelProvider{public async IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest r,[System.Runtime.CompilerServices.EnumeratorCancellation]CancellationToken c){yield return new(StreamEventKind.Reasoning,"think");yield return new(StreamEventKind.Content,"answer");yield return new(StreamEventKind.Usage,Tokens:3);await Task.CompletedTask;}}
    private sealed class CapturingImageProvider:IModelProvider{public List<ChatRequest> Requests{get;}=[];public async IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest r,[System.Runtime.CompilerServices.EnumeratorCancellation]CancellationToken c){Requests.Add(r);yield return new(StreamEventKind.Content,"image answer");await Task.CompletedTask;}}
    private sealed class RevisingProvider:IModelProvider{private int _calls;public async IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest r,[System.Runtime.CompilerServices.EnumeratorCancellation]CancellationToken c){_calls++;yield return new(StreamEventKind.Content,_calls==1?"draft":"{\"accept\":false,\"revisedAnswer\":\"better answer\"}");await Task.CompletedTask;}}
    private sealed class ToolCallingProvider:IModelProvider{public List<ChatRequest> Requests{get;}=[];public async IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest r,[System.Runtime.CompilerServices.EnumeratorCancellation]CancellationToken c){Requests.Add(r);if(Requests.Count==1)yield return new(StreamEventKind.Completed,ToolCall:new("call-1","read_file","{\"path\":\"README.md\"}"));else yield return new(StreamEventKind.Content,"final");await Task.CompletedTask;}}
    private sealed class AlwaysToolProvider:IModelProvider{public List<ChatRequest> Requests{get;}=[];public async IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest r,[System.Runtime.CompilerServices.EnumeratorCancellation]CancellationToken c){Requests.Add(r);if(r.Tools is {Count:>0})yield return new(StreamEventKind.Completed,ToolCall:new($"call-{Requests.Count}","read_file","{\"path\":\"README.md\"}"));else yield return new(StreamEventKind.Content,"bounded final");await Task.CompletedTask;}}
    private sealed class TwoToolProvider:IModelProvider{public List<ChatRequest> Requests{get;}=[];public async IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest r,[System.Runtime.CompilerServices.EnumeratorCancellation]CancellationToken c){Requests.Add(r);if(Requests.Count==1){yield return new(StreamEventKind.Completed,ToolCall:new("call-a","read_file","{\"path\":\"README.md\"}"));yield return new(StreamEventKind.Completed,ToolCall:new("call-b","read_file","{\"path\":\"README.md\"}"));}else yield return new(StreamEventKind.Content,"done");await Task.CompletedTask;}}
    private sealed class ReorderedRepeatedToolProvider:IModelProvider{public List<ChatRequest> Requests{get;}=[];public async IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest r,[System.Runtime.CompilerServices.EnumeratorCancellation]CancellationToken c){Requests.Add(r);var call=Requests.Count switch{1=>new ModelToolCall("one","read_file","{\"path\":\"README.md\",\"startLine\":1}"),2=>new ModelToolCall("two","read_file","{\"startLine\":1,\"path\":\"README.md\"}"),3=>new ModelToolCall("three","read_file","{ \"path\": \"README.md\", \"startLine\": 1 }"),_=>null};if(call is not null)yield return new(StreamEventKind.Completed,ToolCall:call);else yield return new(StreamEventKind.Content,"done");await Task.CompletedTask;}}
    private sealed class EscalationProvider:IModelProvider{public List<ChatRequest> Requests{get;}=[];public async IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest r,[System.Runtime.CompilerServices.EnumeratorCancellation]CancellationToken c){Requests.Add(r);yield return new(StreamEventKind.Content,Requests.Count==1?"draft answer":"synthesized answer");await Task.CompletedTask;}}
    private sealed class PeerToolCallingProvider:IModelProvider{private int calls;public async IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest r,[System.Runtime.CompilerServices.EnumeratorCancellation]CancellationToken c){calls++;if(calls==1)yield return new(StreamEventKind.Completed,ToolCall:new("peer-1","call_mcp_peer_tool","{\"peerId\":\"codex-local\",\"toolName\":\"codex\",\"task\":\"Review\"}"));else yield return new(StreamEventKind.Content,"model final");await Task.CompletedTask;}}
    private sealed class FakeEscalationEvaluator(EscalationDecision decision):IEscalationEvaluator{public int CallCount{get;private set;}public EscalationContext? Context{get;private set;}public Task<EscalationDecision> EvaluateAsync(EscalationContext context,CancellationToken c=default){CallCount++;Context=context;return Task.FromResult(decision);}}
    private sealed class EscalationTools(ToolResult result):IToolExecutionService{public List<ToolRequest> Requests{get;}=[];public IReadOnlyList<ToolDefinition> GetDefinitions()=>[new("call_mcp_peer_tool","peer",ToolRiskLevel.ProcessExecution,[])];public Task<ToolResult> ExecuteAsync(ToolRequest r,IAgentObserver o,CancellationToken c=default){Requests.Add(r);return Task.FromResult(result with{RequestId=r.Id,ToolName=r.Name});}}
    private sealed class RejectingInitialTools:IToolExecutionService{public List<ToolRequest> Requests{get;}=[];public IReadOnlyList<ToolDefinition> GetDefinitions()=>[new("read_file","read",ToolRiskLevel.ReadOnly,[]),new("call_mcp_peer_tool","peer",ToolRiskLevel.ProcessExecution,[])];public Task<ToolResult> ExecuteAsync(ToolRequest r,IAgentObserver o,CancellationToken c=default){Requests.Add(r);return Task.FromResult(new ToolResult(r.Id,r.Name,ToolExecutionStatus.Blocked,"","User rejected.",ErrorCode:"user_rejected"));}}
    private sealed class MemoryMemories:IMemoryStore{public Task DeleteAsync(string id,CancellationToken c=default)=>Task.CompletedTask;public Task<IReadOnlyList<MemoryEntry>> ListAsync(CancellationToken c=default)=>Task.FromResult<IReadOnlyList<MemoryEntry>>([]);public Task SaveAsync(MemoryEntry m,CancellationToken c=default)=>Task.CompletedTask;}
    private sealed class StaticMemories(IReadOnlyList<MemoryEntry> values):IMemoryStore{public Task DeleteAsync(string id,CancellationToken c=default)=>Task.CompletedTask;public Task<IReadOnlyList<MemoryEntry>> ListAsync(CancellationToken c=default)=>Task.FromResult(values);public Task SaveAsync(MemoryEntry m,CancellationToken c=default)=>Task.CompletedTask;}
    private sealed class MemorySessions:ISessionStore{public ChatSession? Value;public int SaveCount;public Task DeleteAsync(string id,CancellationToken c=default)=>Task.CompletedTask;public Task<ChatSession?> GetAsync(string id,CancellationToken c=default)=>Task.FromResult(Value);public Task<IReadOnlyList<ChatSession>> ListAsync(CancellationToken c=default)=>Task.FromResult<IReadOnlyList<ChatSession>>(Value is null?[]:[Value]);public Task SaveAsync(ChatSession s,CancellationToken c=default){SaveCount++;Value=s;return Task.CompletedTask;}}
    private sealed class Observer:IAgentObserver{public ValueTask OnEventAsync(StreamEvent v)=>ValueTask.CompletedTask;public ValueTask OnStateAsync(AgentState s)=>ValueTask.CompletedTask;public ValueTask<bool> RequestToolApprovalAsync(ToolApprovalRequest r,CancellationToken c)=>ValueTask.FromResult(true);}
    private sealed class CapturingObserver:IAgentObserver{public List<StreamEvent> Events{get;}=[];public ValueTask OnEventAsync(StreamEvent v){Events.Add(v);return ValueTask.CompletedTask;}public ValueTask OnStateAsync(AgentState s)=>ValueTask.CompletedTask;public ValueTask<bool> RequestToolApprovalAsync(ToolApprovalRequest r,CancellationToken c)=>ValueTask.FromResult(true);}
    private sealed class NoTools:IToolExecutionService{public IReadOnlyList<ToolDefinition> GetDefinitions()=>[];public Task<ToolResult> ExecuteAsync(ToolRequest r,IAgentObserver o,CancellationToken c=default)=>Task.FromResult(new ToolResult(r.Id,r.Name,ToolExecutionStatus.Failed,"","No tools."));}
    private sealed class CapturingTools:IToolExecutionService{public List<ToolRequest> Requests{get;}=[];public IReadOnlyList<ToolDefinition> GetDefinitions()=>[new("read_file","read",ToolRiskLevel.ReadOnly,[])];public Task<ToolResult> ExecuteAsync(ToolRequest r,IAgentObserver o,CancellationToken c=default){Requests.Add(r);return Task.FromResult(new ToolResult(r.Id,r.Name,ToolExecutionStatus.Success,"file contents"));}}
    private sealed class ThrowingToolSessions:IToolSessionCoordinator{public Task<IReadOnlyList<ToolSession>> EnsureSessionsAsync(string p,IReadOnlyList<ToolDefinition>d,CancellationToken c=default)=>Task.FromException<IReadOnlyList<ToolSession>>(new IOException("unavailable"));public Task<ToolSession> StartAsync(ToolRequest r,bool a,CancellationToken c=default)=>Task.FromException<ToolSession>(new IOException("unavailable"));public Task<ToolSession> CompleteAsync(ToolSession s,ToolRequest r,ToolResult x,bool a,CancellationToken c=default)=>Task.FromException<ToolSession>(new IOException("unavailable"));public Task<string?> GetStrategyHintAsync(string p,string n,CancellationToken c=default)=>Task.FromException<string?>(new IOException("unavailable"));public Task<IReadOnlyList<ToolSession>> ListAsync(string? p=null,CancellationToken c=default)=>Task.FromException<IReadOnlyList<ToolSession>>(new IOException("unavailable"));public Task<ToolSession>AcknowledgeNeedsReviewAsync(string p,string n,CancellationToken c=default)=>Task.FromException<ToolSession>(new IOException("unavailable"));}
}
