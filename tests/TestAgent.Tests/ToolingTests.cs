using System.Text.Json;
using TestAgent.Core;
using TestAgent.Infrastructure;
using Xunit;
namespace TestAgent.Tests;
public sealed class ToolingTests
{
    [Fact] public async Task Read_only_tool_executes_without_approval()
    {
        var tool=new FakeTool(ToolRiskLevel.ReadOnly);var audit=new MemoryAudit();var service=Service(tool,audit);var observer=new ApprovalObserver(false);
        var result=await service.ExecuteAsync(new("1","fake","{}","s"),observer);Assert.Equal(ToolExecutionStatus.Success,result.Status);Assert.Equal(0,observer.Requests);Assert.Single(audit.Items);
    }
    [Fact] public async Task Write_tool_is_blocked_when_user_rejects()
    {
        var tool=new FakeTool(ToolRiskLevel.WorkspaceWrite);var audit=new MemoryAudit();var observer=new ApprovalObserver(false);var service=Service(tool,audit);
        var result=await service.ExecuteAsync(new("1","fake","{}","s"),observer);Assert.Equal(ToolExecutionStatus.Blocked,result.Status);Assert.Equal(1,observer.Requests);Assert.False(audit.Items[0].Approved);
    }
    [Fact] public async Task Browser_open_approval_reveals_origin_but_redacts_path_and_query()
    {
        var browser=new ApprovalBrowser();var tool=new OpenBrowserSnapshotTool(browser);var observer=new ApprovalObserver(false);var service=Service(tool,new MemoryAudit());
        var result=await service.ExecuteAsync(new("browser-open",tool.Definition.Name,"{\"url\":\"https://example.com/private/account?token=SECRET-SENTINEL\"}","s"),observer);
        Assert.Equal(ToolExecutionStatus.Blocked,result.Status);Assert.NotNull(observer.LastRequest);Assert.Equal(ToolRiskLevel.ExternalNetwork,observer.LastRequest!.RiskLevel);
        Assert.Contains("https://example.com",observer.LastRequest.Summary);Assert.Contains("/<redacted>",observer.LastRequest.Summary);
        Assert.DoesNotContain("private",observer.LastRequest.Summary,StringComparison.OrdinalIgnoreCase);Assert.DoesNotContain("account",observer.LastRequest.Summary,StringComparison.OrdinalIgnoreCase);Assert.DoesNotContain("SECRET-SENTINEL",observer.LastRequest.Summary,StringComparison.Ordinal);
        Assert.Null(browser.Current);
    }
    [Fact] public async Task Browser_capture_approval_names_sensitive_capture_and_private_handoff()
    {
        var browser=new ApprovalBrowser();var tool=new CaptureBrowserViewportTool(browser);var observer=new ApprovalObserver(false);var service=Service(tool,new MemoryAudit());
        var result=await service.ExecuteAsync(new("browser-capture",tool.Definition.Name,"{}","s"),observer);
        Assert.Equal(ToolExecutionStatus.Blocked,result.Status);Assert.NotNull(observer.LastRequest);Assert.Equal(ToolRiskLevel.SensitiveCapture,observer.LastRequest!.RiskLevel);
        Assert.Contains("SensitiveCapture",observer.LastRequest.Summary);Assert.Contains("private information",observer.LastRequest.Summary);Assert.Contains("explicitly attaches",observer.LastRequest.Summary);
        Assert.False(browser.HasLatestCapture);
    }
    [Fact] public async Task Successful_tool_result_survives_telemetry_failures_without_reexecution()
    {
        var tool=new CountingSuccessTool();var service=new ToolExecutionService(new ToolRegistry([tool]),new ThrowingAudit(),new CompleteFailingToolSessions());
        var result=await service.ExecuteAsync(new("req","edit_file","{}","chat"),new ApprovalObserver(true));
        Assert.Equal(ToolExecutionStatus.Success,result.Status);Assert.Equal("real success",result.Output);Assert.Equal(["src/a.cs"],result.ModifiedFiles);Assert.Equal(1,tool.Calls);
    }
    [Fact] public async Task Read_file_rejects_workspace_escape()
    {
        var root=Path.Combine(Path.GetTempPath(),"TestAgent-tool-test-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);await File.WriteAllTextAsync(Path.Combine(root,"TestAgent.slnx"),"<Solution />");var old=Environment.CurrentDirectory;Environment.CurrentDirectory=root;
        try{var tool=new ReadFileTool(new WorkspaceLocator());await Assert.ThrowsAsync<InvalidDataException>(()=>tool.ExecuteAsync(new("1","read_file",JsonSerializer.Serialize(new{path="../secret.txt"}),"s")));}finally{Environment.CurrentDirectory=old;Directory.Delete(root,true);}
    }
    [Theory]
    [InlineData("v4_agent_config.json")]
    [InlineData("V4_AGENT_HISTORY.JSON")]
    [InlineData(".env")]
    [InlineData(".ENV.local")]
    [InlineData("nested/Credentials.Json")]
    [InlineData("nested/client-secret.pem")]
    public async Task Read_and_edit_tools_reject_sensitive_paths_case_insensitively(string relative)
    {
        var root=NewWorkspace("sensitive");var old=Environment.CurrentDirectory;Environment.CurrentDirectory=root;
        try
        {
            var absolute=Path.Combine(root,relative.Replace('/',Path.DirectorySeparatorChar));Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);await File.WriteAllTextAsync(absolute,"secret-value");var locator=new WorkspaceLocator();
            await Assert.ThrowsAsync<InvalidDataException>(()=>new ReadFileTool(locator).ExecuteAsync(new("1","read_file",JsonSerializer.Serialize(new{path=relative}),"s")));
            await Assert.ThrowsAsync<InvalidDataException>(()=>new EditFileTool(locator).ExecuteAsync(new("2","edit_file",JsonSerializer.Serialize(new{action="write",path=relative,content="changed"}),"s")));
            Assert.Equal("secret-value",await File.ReadAllTextAsync(absolute));
        }
        finally{Environment.CurrentDirectory=old;Directory.Delete(root,true);}
    }
    [Fact] public async Task List_and_search_tools_omit_sensitive_files_and_directories()
    {
        var root=NewWorkspace("enumeration");var old=Environment.CurrentDirectory;Environment.CurrentDirectory=root;
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root,"safe.txt"),"needle");await File.WriteAllTextAsync(Path.Combine(root,".ENV"),"needle secret");
            Directory.CreateDirectory(Path.Combine(root,"Secrets"));await File.WriteAllTextAsync(Path.Combine(root,"Secrets","note.txt"),"needle secret");var locator=new WorkspaceLocator();
            var listed=await new ListFilesTool(locator).ExecuteAsync(new("1","list_files","{}","s"));
            var searched=await new SearchTextTool(locator).ExecuteAsync(new("2","search_text",JsonSerializer.Serialize(new{pattern="needle",include="*.txt"}),"s"));
            Assert.Contains("safe.txt",listed.Output);Assert.DoesNotContain(".ENV",listed.Output,StringComparison.OrdinalIgnoreCase);Assert.DoesNotContain("Secrets",listed.Output,StringComparison.OrdinalIgnoreCase);
            Assert.Contains("safe.txt",searched.Output);Assert.DoesNotContain("secret",searched.Output,StringComparison.OrdinalIgnoreCase);
        }
        finally{Environment.CurrentDirectory=old;Directory.Delete(root,true);}
    }
    [Fact] public async Task Read_and_create_reject_reparse_point_ancestors()
    {
        var root=NewWorkspace("links");var outside=Path.Combine(Path.GetTempPath(),"TestAgent-outside-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(outside);await File.WriteAllTextAsync(Path.Combine(outside,"outside.txt"),"outside");var link=Path.Combine(root,"linked");
        try{Directory.CreateSymbolicLink(link,outside);}
        catch(Exception ex) when(ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException){Directory.Delete(root,true);Directory.Delete(outside,true);return;}
        var old=Environment.CurrentDirectory;Environment.CurrentDirectory=root;
        try
        {
            var locator=new WorkspaceLocator();
            await Assert.ThrowsAsync<InvalidDataException>(()=>new ReadFileTool(locator).ExecuteAsync(new("1","read_file",JsonSerializer.Serialize(new{path="linked/outside.txt"}),"s")));
            await Assert.ThrowsAsync<InvalidDataException>(()=>new EditFileTool(locator).ExecuteAsync(new("2","edit_file",JsonSerializer.Serialize(new{action="create",path="linked/new.txt",content="bad"}),"s")));
            Assert.False(File.Exists(Path.Combine(outside,"new.txt")));
        }
        finally{Environment.CurrentDirectory=old;Directory.Delete(root,true);Directory.Delete(outside,true);}
    }
    [Fact] public async Task Tools_enforce_task_allowed_path_scope()
    {
        var root=NewWorkspace("scope");var old=Environment.CurrentDirectory;Environment.CurrentDirectory=root;
        try
        {
            Directory.CreateDirectory(Path.Combine(root,"src"));Directory.CreateDirectory(Path.Combine(root,"docs"));await File.WriteAllTextAsync(Path.Combine(root,"src","allowed.cs"),"needle");await File.WriteAllTextAsync(Path.Combine(root,"docs","blocked.md"),"needle");var locator=new WorkspaceLocator();
            await Assert.ThrowsAsync<InvalidDataException>(()=>new ReadFileTool(locator).ExecuteAsync(new("1","read_file",JsonSerializer.Serialize(new{path="docs/blocked.md"}),"s",["src/"])));
            await Assert.ThrowsAsync<InvalidDataException>(()=>new EditFileTool(locator).ExecuteAsync(new("2","edit_file",JsonSerializer.Serialize(new{action="create",path="docs/new.md",content="bad"}),"s",["src/"])));
            var searched=await new SearchTextTool(locator).ExecuteAsync(new("3","search_text",JsonSerializer.Serialize(new{pattern="needle",path="src"}),"s",["src/"]));Assert.Contains("allowed.cs",searched.Output);Assert.DoesNotContain("blocked.md",searched.Output);
            await Assert.ThrowsAsync<InvalidDataException>(()=>new RunDeveloperCommandTool(locator).ExecuteAsync(new("4","run_command",JsonSerializer.Serialize(new{executable="rg",arguments="needle .",workingDirectory="docs"}),"s",["src/"])));
        }
        finally{Environment.CurrentDirectory=old;Directory.Delete(root,true);}
    }
    [Fact] public async Task File_scope_is_exact_but_directory_scope_allows_new_descendants()
    {
        var root=NewWorkspace("scope-shape");var old=Environment.CurrentDirectory;Environment.CurrentDirectory=root;
        try
        {
            Directory.CreateDirectory(Path.Combine(root,"src"));await File.WriteAllTextAsync(Path.Combine(root,"src","one.cs"),"one");await File.WriteAllTextAsync(Path.Combine(root,"src","two.cs"),"two");var locator=new WorkspaceLocator();
            var exact=await new ReadFileTool(locator).ExecuteAsync(new("1","read_file",JsonSerializer.Serialize(new{path="src/one.cs"}),"s",["src/one.cs"]));Assert.Contains("one",exact.Output);
            await Assert.ThrowsAsync<InvalidDataException>(()=>new ReadFileTool(locator).ExecuteAsync(new("2","read_file",JsonSerializer.Serialize(new{path="src/two.cs"}),"s",["src/one.cs"])));
            var created=await new EditFileTool(locator).ExecuteAsync(new("3","edit_file",JsonSerializer.Serialize(new{action="create",path="src/new/deep.cs",content="ok"}),"s",["src/"]));Assert.Equal(ToolExecutionStatus.Success,created.Status);Assert.True(File.Exists(Path.Combine(root,"src","new","deep.cs")));
        }
        finally{Environment.CurrentDirectory=old;Directory.Delete(root,true);}
    }
    [Fact] public async Task Replace_many_is_atomic_and_reports_modified_file()
    {
        var root=NewWorkspace("replace-many");var old=Environment.CurrentDirectory;Environment.CurrentDirectory=root;try{var path=Path.Combine(root,"sample.txt");await File.WriteAllTextAsync(path,"alpha\nbeta\ngamma");var tool=new EditFileTool(new WorkspaceLocator());var ok=await tool.ExecuteAsync(new("1","edit_file",JsonSerializer.Serialize(new{action="replace_many",path="sample.txt",replacements=new[]{new{oldText="alpha",newText="one"},new{oldText="gamma",newText="three"}}}),"s"));Assert.Equal("one\nbeta\nthree",await File.ReadAllTextAsync(path));Assert.Equal(["sample.txt"],ok.ModifiedFiles);var before=await File.ReadAllTextAsync(path);await Assert.ThrowsAsync<InvalidDataException>(()=>tool.ExecuteAsync(new("2","edit_file",JsonSerializer.Serialize(new{action="replace_many",path="sample.txt",replacements=new[]{new{oldText="one",newText="uno"},new{oldText="missing",newText="bad"}}}),"s")));Assert.Equal(before,await File.ReadAllTextAsync(path));}finally{Environment.CurrentDirectory=old;Directory.Delete(root,true);}
    }
    [Fact] public async Task Read_file_returns_continuation_and_rejects_binary()
    {
        var root=NewWorkspace("read-page");var old=Environment.CurrentDirectory;Environment.CurrentDirectory=root;try{await File.WriteAllLinesAsync(Path.Combine(root,"lines.txt"),["a","b","c"]);await File.WriteAllBytesAsync(Path.Combine(root,"binary.bin"),[1,0,2]);var tool=new ReadFileTool(new WorkspaceLocator());var page=await tool.ExecuteAsync(new("1","read_file",JsonSerializer.Serialize(new{path="lines.txt",startLine=1,endLine=2}),"s"));Assert.True(page.Truncated);Assert.Contains("startLine 3",page.NextAction);await Assert.ThrowsAsync<InvalidDataException>(()=>tool.ExecuteAsync(new("2","read_file",JsonSerializer.Serialize(new{path="binary.bin"}),"s")));}finally{Environment.CurrentDirectory=old;Directory.Delete(root,true);}
    }
    [Fact] public async Task Read_file_bounds_very_long_lines_and_total_output()
    {
        var root=NewWorkspace("read-bound");var old=Environment.CurrentDirectory;Environment.CurrentDirectory=root;try{await File.WriteAllTextAsync(Path.Combine(root,"long.txt"),string.Join("\n",Enumerable.Repeat(new string('x',10_000),20)));var result=await new ReadFileTool(new WorkspaceLocator()).ExecuteAsync(new("1","read_file",JsonSerializer.Serialize(new{path="long.txt",startLine=1,endLine=20}),"s"));Assert.True(result.Truncated);Assert.True(result.Output.Length<=30_100);Assert.Contains("truncated",result.Output,StringComparison.OrdinalIgnoreCase);Assert.NotNull(result.NextAction);}finally{Environment.CurrentDirectory=old;Directory.Delete(root,true);}
    }
    [Fact] public async Task Search_literal_case_and_line_length_are_bounded()
    {
        var root=NewWorkspace("search-literal");var old=Environment.CurrentDirectory;Environment.CurrentDirectory=root;try{Directory.CreateDirectory(Path.Combine(root,"src"));await File.WriteAllTextAsync(Path.Combine(root,"src","a.txt"),"A+B "+new string('x',1000));var tool=new SearchTextTool(new WorkspaceLocator());var found=await tool.ExecuteAsync(new("1","search_text",JsonSerializer.Serialize(new{pattern="A+B",path="src",include="*.txt",literal=true,caseSensitive=true}),"s"));Assert.Contains("a.txt:1",found.Output);Assert.True(found.Output.Length<650);}finally{Environment.CurrentDirectory=old;Directory.Delete(root,true);}
    }
    [Theory]
    [InlineData("rg", "--pre powershell pattern .")]
    [InlineData("git", "diff --ext-diff")]
    [InlineData("dotnet", "tool install something -g")]
    [InlineData("dotnet", "build ../outside/project.csproj")]
    [InlineData("dotnet", "build C:\\outside\\project.csproj")]
    [InlineData("dotnet", "build --output artifacts")]
    [InlineData("dotnet", "test --artifacts-path artifacts")]
    [InlineData("dotnet", "build -p:BaseOutputPath=artifacts")]
    [InlineData("dotnet", "build /p:OutputPath=artifacts")]
    [InlineData("dotnet", "build --property OutputPath=artifacts")]
    [InlineData("dotnet", "build @options.rsp")]
    [InlineData("rg", "needle ../outside")]
    public async Task Command_tool_blocks_execution_escape_hatches(string executable,string arguments)
    {
        var root=NewWorkspace("command");var old=Environment.CurrentDirectory;Environment.CurrentDirectory=root;
        try{var tool=new RunDeveloperCommandTool(new WorkspaceLocator());var json=JsonSerializer.Serialize(new{executable,arguments});await Assert.ThrowsAsync<InvalidDataException>(()=>tool.ExecuteAsync(new("1","run_command",json,"s")));}finally{Environment.CurrentDirectory=old;Directory.Delete(root,true);}
    }
    private static string NewWorkspace(string name){var root=Path.Combine(Path.GetTempPath(),$"TestAgent-{name}-test-{Guid.NewGuid():N}");Directory.CreateDirectory(root);File.WriteAllText(Path.Combine(root,"TestAgent.slnx"),"<Solution />");return root;}
    private static ToolExecutionService Service(IAgentTool tool,IToolAuditStore audit){var store=new MemoryToolSessions();return new(new ToolRegistry([tool]),audit,new ToolSessionCoordinator(store));}
    private sealed class FakeTool(ToolRiskLevel risk):IAgentTool{public ToolDefinition Definition{get;}=new("fake","fake",risk,[]);public Task<ToolResult> ExecuteAsync(ToolRequest r,CancellationToken c=default)=>Task.FromResult(new ToolResult(r.Id,r.Name,ToolExecutionStatus.Success,"ok"));}
    private sealed class CountingSuccessTool:IAgentTool{public int Calls;public ToolDefinition Definition{get;}=new("edit_file","edit",ToolRiskLevel.WorkspaceWrite,[]);public Task<ToolResult> ExecuteAsync(ToolRequest r,CancellationToken c=default){Calls++;return Task.FromResult(new ToolResult(r.Id,r.Name,ToolExecutionStatus.Success,"real success",Summary:"edited",ModifiedFiles:["src/a.cs"]));}}
    private sealed class MemoryAudit:IToolAuditStore{public List<ToolAuditEntry> Items{get;}=[];public Task AppendAsync(ToolAuditEntry e,CancellationToken c=default){Items.Add(e);return Task.CompletedTask;}}
    private sealed class ThrowingAudit:IToolAuditStore{public Task AppendAsync(ToolAuditEntry e,CancellationToken c=default)=>Task.FromException(new IOException("audit unavailable"));}
    private sealed class MemoryToolSessions:IToolSessionStore{private readonly Dictionary<string,ToolSession> _items=new(StringComparer.OrdinalIgnoreCase);public Task<ToolSession?> GetAsync(string parent,string tool,CancellationToken c=default)=>Task.FromResult(_items.Values.FirstOrDefault(x=>x.ParentSessionId==parent&&x.ToolName.Equals(tool,StringComparison.OrdinalIgnoreCase)));public Task<IReadOnlyList<ToolSession>> ListAsync(string? parent=null,CancellationToken c=default)=>Task.FromResult<IReadOnlyList<ToolSession>>(_items.Values.Where(x=>parent is null||x.ParentSessionId==parent).ToArray());public Task SaveAsync(ToolSession s,CancellationToken c=default){_items[s.Id]=s;return Task.CompletedTask;}}
    private sealed class CompleteFailingToolSessions:IToolSessionCoordinator{public Task<IReadOnlyList<ToolSession>> EnsureSessionsAsync(string p,IReadOnlyList<ToolDefinition>d,CancellationToken c=default)=>Task.FromResult<IReadOnlyList<ToolSession>>([]);public Task<ToolSession> StartAsync(ToolRequest r,bool a,CancellationToken c=default)=>Task.FromResult(new ToolSession("tool","chat",r.Name,ToolSessionState.Running,0,0,0,null,[],DateTimeOffset.UtcNow,DateTimeOffset.UtcNow));public Task<ToolSession> CompleteAsync(ToolSession s,ToolRequest r,ToolResult x,bool a,CancellationToken c=default)=>Task.FromException<ToolSession>(new IOException("session unavailable"));public Task<string?> GetStrategyHintAsync(string p,string n,CancellationToken c=default)=>Task.FromResult<string?>(null);public Task<IReadOnlyList<ToolSession>> ListAsync(string? p=null,CancellationToken c=default)=>Task.FromResult<IReadOnlyList<ToolSession>>([]);public Task<ToolSession>AcknowledgeNeedsReviewAsync(string p,string n,CancellationToken c=default)=>Task.FromException<ToolSession>(new IOException("unavailable"));}
    private sealed class ApprovalObserver(bool approve):IAgentObserver{public int Requests;public ToolApprovalRequest? LastRequest;public ValueTask OnEventAsync(StreamEvent v)=>ValueTask.CompletedTask;public ValueTask OnStateAsync(AgentState s)=>ValueTask.CompletedTask;public ValueTask<bool> RequestToolApprovalAsync(ToolApprovalRequest r,CancellationToken c){Requests++;LastRequest=r;return ValueTask.FromResult(approve);}}
    private sealed class ApprovalBrowser:IReadOnlyBrowserSession
    {
        public BrowserPageDocument? Current=>null;public bool HasLatestCapture=>false;public event Action? Changed{add{} remove{}}
        public Task<BrowserPageDocument> OpenSnapshotAsync(string url,int maxChars=30000,CancellationToken ct=default)=>throw new InvalidOperationException("Rejected requests must not navigate.");
        public Task<BrowserDomSnapshot> ReadDomAsync(int maxChars=30000,CancellationToken ct=default)=>throw new InvalidOperationException();
        public Task<BrowserCaptureReceipt> CaptureViewportAsync(CancellationToken ct=default)=>throw new InvalidOperationException("Rejected requests must not capture.");
        public ImageInput? TakeLatestCapture()=>null;
    }
}
