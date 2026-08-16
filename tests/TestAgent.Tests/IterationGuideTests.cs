using TestAgent.Infrastructure;
using TestAgent.Core;
using Xunit;
namespace TestAgent.Tests;
public sealed class IterationGuideTests
{
    [Fact] public async Task Guide_store_reads_title_goal_and_explicit_targets()
    {
        var root=Path.Combine(Path.GetTempPath(),"TestAgent-guide-test-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(Path.Combine(root,"iteration-guides"));
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root,"TestAgent.slnx"),"<Solution />");
            await File.WriteAllTextAsync(Path.Combine(root,"iteration-guides","one.md"),"# Improve thing\nGoal: measurable goal\nTargets:\n- src/A.cs\n- tests/A.cs\n\n## Acceptance\n- passes\n");
            var old=Environment.CurrentDirectory;Environment.CurrentDirectory=root;try{var list=await new MarkdownIterationGuideStore(new WorkspaceLocator()).ListAsync();var guide=Assert.Single(list);Assert.Equal("Improve thing",guide.Title);Assert.Equal("measurable goal",guide.Goal);Assert.Equal(["src/A.cs","tests/A.cs"],guide.Targets);}finally{Environment.CurrentDirectory=old;}
        }
        finally{Directory.Delete(root,true);}
    }
    [Fact] public async Task Iteration_rejects_model_change_outside_explicit_targets()
    {
        var root=Path.Combine(Path.GetTempPath(),"TestAgent-guard-test-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(Path.Combine(root,"src"));
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root,"TestAgent.slnx"),"<Solution />");await File.WriteAllTextAsync(Path.Combine(root,"src","A.cs"),"class A {}");
            var old=Environment.CurrentDirectory;Environment.CurrentDirectory=root;
            try
            {
                var service=new CodeIterationService(new MaliciousProvider(),new WorkspaceLocator());var guide=new IterationGuide("g","g","g",["src/A.cs"],"# g\nGoal: g\nTargets:\n- src/A.cs\n");
                var error=await Assert.ThrowsAsync<InvalidDataException>(()=>service.GenerateAsync(guide,new("custom","http://fake/v1","m"),null));Assert.Contains("non-whitelisted",error.Message);
            }
            finally{Environment.CurrentDirectory=old;}
        }
        finally{Directory.Delete(root,true);}
    }
    private sealed class MaliciousProvider:IModelProvider{public async IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest r,[System.Runtime.CompilerServices.EnumeratorCancellation]CancellationToken c){yield return new(StreamEventKind.Content,"{\"summary\":\"x\",\"changes\":[{\"path\":\"README.md\",\"content\":\"bad\"}]}");await Task.CompletedTask;}}
}
