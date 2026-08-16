using System.Text;
using System.Text.Json;
using TestAgent.Core;

namespace TestAgent.Infrastructure;

public sealed class StartBackgroundCommandTool(
    IBackgroundCommandService commands, WorkspaceLocator workspace) : WorkspaceTool(workspace), IAgentTool
{
    public ToolDefinition Definition { get; } = new("start_background_command",
        "Start one approved, non-interactive developer command and return immediately with a persistent job ID. Only bounded dotnet build/test, rg, and git status/branch metadata are allowed.",
        ToolRiskLevel.ProcessExecution,
        [new("executable","string","dotnet, rg, or git.",true,["dotnet","rg","git"]),
         new("arguments","array","Argument tokens; do not include a shell command string.",true),
         new("workingDirectory","string","Workspace-relative directory; default root."),
         new("timeoutSeconds","integer","1-600; default 300."),
         new("displayName","string","Short human-readable job name.")],
        "{\"executable\":\"dotnet\",\"arguments\":[\"test\",\"--nologo\"],\"workingDirectory\":\".\",\"timeoutSeconds\":300}");

    public async Task<ToolResult> ExecuteAsync(ToolRequest request, CancellationToken ct = default)
    {
        using var doc = JsonDocument.Parse(request.ArgumentsJson); var root = doc.RootElement;
        var executable = Text(root, "executable")?.ToLowerInvariant() ?? throw new InvalidDataException("executable is required.");
        var arguments = root.TryGetProperty("arguments", out var array) && array.ValueKind == JsonValueKind.Array
            ? array.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() : null).ToArray()
            : throw new InvalidDataException("arguments must be an array of strings.");
        if (arguments.Any(string.IsNullOrWhiteSpace)) throw new InvalidDataException("arguments may not contain empty or non-string values.");
        var tokens = DeveloperCommandPolicy.Prepare(executable, arguments.Cast<string>().ToArray(), background: true);
        var cwd = Resolve(request, Text(root, "workingDirectory"));
        if (!Directory.Exists(cwd)) throw new DirectoryNotFoundException("Working directory was not found.");
        var trusted = TrustedDeveloperExecutable.Resolve(executable, Root);
        var timeout = root.TryGetProperty("timeoutSeconds", out var timeoutValue) && timeoutValue.TryGetInt32(out var parsed)
            ? Math.Clamp(parsed, 1, 600) : 300;
        var job = await commands.StartAsync(new(request.SessionId, trusted, tokens, cwd, timeout,
            Text(root, "displayName") ?? $"{executable} {tokens.FirstOrDefault()}"), ct);
        return new(request.Id, Definition.Name, ToolExecutionStatus.Success,
            $"jobId={job.Id}\nstate={job.State}\nname={job.DisplayName}",
            Summary:$"Started background command {job.Id}",
            NextAction:"Poll get_background_command, then read_background_output. Do not treat the task as verified until the job reaches a terminal state.");
    }

    private static string? Text(JsonElement root,string name) => root.TryGetProperty(name,out var value)&&value.ValueKind==JsonValueKind.String?value.GetString()?.Trim():null;
}

public sealed class GetBackgroundCommandTool(IBackgroundCommandService commands) : IAgentTool
{
    public ToolDefinition Definition { get; } = new("get_background_command",
        "Get the persisted status for one background command, or list jobs in the current Agent scope.", ToolRiskLevel.ReadOnly,
        [new("jobId","string","Optional BG-* job ID. Omit to list current-scope jobs.")]);
    public async Task<ToolResult> ExecuteAsync(ToolRequest request,CancellationToken ct=default)
    {
        using var doc=JsonDocument.Parse(request.ArgumentsJson);var root=doc.RootElement;
        var id=root.TryGetProperty("jobId",out var value)&&value.ValueKind==JsonValueKind.String?value.GetString():null;
        var jobs=string.IsNullOrWhiteSpace(id)?await commands.ListAsync(request.SessionId,ct):[await BackgroundToolScope.RequireOwnedAsync(commands,request.SessionId,id,ct)];
        var output=jobs.Count==0?"No background commands in this scope.":string.Join("\n",jobs.Select(Format));
        return new(request.Id,Definition.Name,ToolExecutionStatus.Success,output,Summary:$"Returned {jobs.Count} background command state(s)",NextAction:"For a running job, poll later. For a terminal job, read its output before drawing conclusions.");
    }
    private static string Format(BackgroundCommandJob job)=>$"{job.Id} | {job.State} | {job.DisplayName} | exit={job.ExitCode?.ToString()??"-"} | truncated={job.OutputTruncated} | error={job.Error??"-"}";
}

public sealed class ReadBackgroundOutputTool(IBackgroundCommandService commands) : IAgentTool
{
    public ToolDefinition Definition { get; } = new("read_background_output",
        "Read a bounded incremental output chunk from a background command using a byte cursor.",ToolRiskLevel.ReadOnly,
        [new("jobId","string","BG-* job ID.",true),new("cursor","integer","Byte cursor; default 0."),new("maxBytes","integer","256-65536; default 16000.")]);
    public async Task<ToolResult> ExecuteAsync(ToolRequest request,CancellationToken ct=default)
    {
        using var doc=JsonDocument.Parse(request.ArgumentsJson);var root=doc.RootElement;
        var id=root.TryGetProperty("jobId",out var idValue)&&idValue.ValueKind==JsonValueKind.String?idValue.GetString():null;
        if(string.IsNullOrWhiteSpace(id))throw new InvalidDataException("jobId is required.");
        var cursor=root.TryGetProperty("cursor",out var cursorValue)&&cursorValue.TryGetInt64(out var parsedCursor)?parsedCursor:0;
        var max=root.TryGetProperty("maxBytes",out var maxValue)&&maxValue.TryGetInt32(out var parsedMax)?Math.Clamp(parsedMax,256,65_536):16_000;
        await BackgroundToolScope.RequireOwnedAsync(commands,request.SessionId,id,ct);
        var chunk=await commands.ReadOutputAsync(id,cursor,max,ct);
        var header=$"jobId={id}\ncursor={chunk.Cursor}\nnextCursor={chunk.NextCursor}\ncomplete={chunk.IsComplete}\ntruncated={chunk.Truncated}\n\n";
        return new(request.Id,Definition.Name,ToolExecutionStatus.Success,header+chunk.Content,Summary:$"Read background output {chunk.Cursor}-{chunk.NextCursor}",Truncated:chunk.Truncated,NextAction:chunk.IsComplete?"Use the terminal status and output as evidence.":$"Read again with cursor={chunk.NextCursor} after polling status.");
    }
}

public sealed class StopBackgroundCommandTool(IBackgroundCommandService commands) : IAgentTool
{
    public ToolDefinition Definition { get; } = new("stop_background_command",
        "Stop one live background command and its process tree after explicit approval.",ToolRiskLevel.ProcessExecution,
        [new("jobId","string","BG-* job ID.",true)]);
    public async Task<ToolResult> ExecuteAsync(ToolRequest request,CancellationToken ct=default)
    {
        using var doc=JsonDocument.Parse(request.ArgumentsJson);var root=doc.RootElement;
        var id=root.TryGetProperty("jobId",out var value)&&value.ValueKind==JsonValueKind.String?value.GetString():null;
        if(string.IsNullOrWhiteSpace(id))throw new InvalidDataException("jobId is required.");
        await BackgroundToolScope.RequireOwnedAsync(commands,request.SessionId,id,ct);
        var job=await commands.StopAsync(id,ct);
        return new(request.Id,Definition.Name,ToolExecutionStatus.Success,$"jobId={job.Id}\nstate={job.State}",Summary:$"Background command {job.Id} is {job.State}",NextAction:"Read the final output and do not automatically restart a stopped or NeedsReview command.");
    }
}

internal static class BackgroundToolScope
{
    public static async Task<BackgroundCommandJob> RequireOwnedAsync(IBackgroundCommandService commands,
        string scopeId,string jobId,CancellationToken ct)
    {
        var job=await commands.GetAsync(jobId,ct)??throw new InvalidDataException("Background command was not found.");
        if(!job.ScopeId.Equals(scopeId,StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("The background command belongs to a different Agent scope.");
        return job;
    }
}

public static class DeveloperCommandPolicy
{
    private static readonly string[] SensitiveMarkers =
    ["v4_agent_config.json","v4_agent_history.json",".env","credential","secret","password","token","id_rsa","id_ed25519",".pem",".pfx",".p12"];
    private static readonly string[] ExcludedGlobs =
    ["!**/v4_agent_config.json","!**/v4_agent_history.json","!**/.rag_index.json","!**/.env","!**/.env.*",
     "!**/credentials","!**/credentials.*","!**/credential.*","!**/secrets","!**/secrets.*","!**/secret.*",
     "!**/password.*","!**/passwords.*","!**/token.*","!**/tokens.*","!**/history.*","!**/*_history.*",
     "!**/.npmrc","!**/.pypirc","!**/.ssh/**","!**/.aws/**","!**/.azure/**","!**/credentials/**","!**/secrets/**",
     "!**/.git/**","!**/bin/**","!**/obj/**","!**/*.pem","!**/*.key","!**/*.pfx","!**/*.p12","!**/*.keystore","!**/id_rsa","!**/id_ed25519"];

    public static IReadOnlyList<string> Parse(string arguments) =>
        System.Text.RegularExpressions.Regex.Matches(arguments,"(?:[^\\s\\\"]+|\\\"[^\\\"]*\\\")+")
            .Select(match=>match.Value.Trim().Trim('"')).Where(value=>value.Length>0).ToArray();

    public static IReadOnlyList<string> Prepare(string executable,IReadOnlyList<string> args,bool background)
    {
        if(executable is not ("dotnet" or "rg" or "git"))throw new InvalidDataException("Executable is not allowlisted.");
        if(args.Count==0)throw new InvalidDataException("At least one command argument is required.");
        if(args.Any(x=>x.IndexOfAny(['\r','\n','\0'])>=0||Path.IsPathRooted(x)||x.Split(['/', '\\']).Any(p=>p=="..")||x.StartsWith('@')))
            throw new InvalidDataException("Control characters, rooted paths, parent traversal, and response files are blocked.");
        if(SensitiveMarkers.Any(marker=>args.Any(value=>value.Contains(marker,StringComparison.OrdinalIgnoreCase))))
            throw new InvalidDataException("Command arguments reference a sensitive or protected path.");
        if(executable=="dotnet"&&background&&args[0] is not ("build" or "test" or "--info" or "--version"))
            throw new InvalidDataException("Background dotnet allows only build, test, --info, and --version.");
        if(executable=="dotnet"&&!background&&args[0] is not ("build" or "test" or "run" or "restore" or "clean" or "list" or "sln" or "--info" or "--version"))
            throw new InvalidDataException("The dotnet subcommand is not allowlisted.");
        if(executable=="git")ValidateGit(args);
        var joined=string.Join(' ',args);
        if(System.Text.RegularExpressions.Regex.IsMatch(joined,@"(^|\s)(?:-o|--output|--artifacts-path|--results-directory|--diag|-bl|--binarylogger|-p|/p|--property|--ext-diff|--textconv)(?:\s|=|:|$)",System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            throw new InvalidDataException("External execution, output paths, log paths, and build properties are blocked.");
        if(executable=="git")
            return new[]{"-c","core.fsmonitor=false","-c","diff.external=","-c","core.pager=cat"}.Concat(args).ToArray();
        if(executable!="rg")return args.ToArray();
        var forbidden=new[]{"--pre","--pre-glob","--hidden","-u","-uu","-uuu","--follow","-l","--file","-f","--ignore-file","--hostname-bin","--search-zip","-z"};
        if(args.Any(value=>forbidden.Contains(value.Split('=')[0],StringComparer.OrdinalIgnoreCase)||value.StartsWith("--no-ignore",StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("rg external preprocessors, hidden/ignored files, links, and argument files are blocked.");
        var result=args.ToList();result.Add("--glob-case-insensitive");foreach(var glob in ExcludedGlobs){result.Add("--glob");result.Add(glob);}return result;
    }

    private static void ValidateGit(IReadOnlyList<string> args)
    {
        if(args[0]=="status")
        {
            var allowed=new[]{"--short","-s","--branch","-b","--porcelain","--porcelain=v1","--porcelain=v2"};
            if(args.Skip(1).Any(value=>!allowed.Contains(value,StringComparer.OrdinalIgnoreCase)))
                throw new InvalidDataException("Git status accepts only short/porcelain/branch metadata options.");
            return;
        }
        if(args[0]=="branch"&&args.Skip(1).SequenceEqual(["--show-current"]))return;
        throw new InvalidDataException("Only git status metadata and git branch --show-current are allowed. Use bounded workspace tools for file content and history.");
    }
}
