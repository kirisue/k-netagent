using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using TestAgent.Core;

namespace TestAgent.Infrastructure;

public abstract class VsCodeWorkspaceTool(WorkspaceLocator workspace) : WorkspaceTool(workspace)
{
    protected override bool AllowVsCodeStructuredConfig => true;
    protected const int MaxConfigBytes = 512 * 1024;

    protected async Task<JsonElement?> ReadOptionalConfigAsync(ToolRequest request, string path,
        CancellationToken ct)
    {
        EnsureSupportedPath(path);
        var full = Resolve(request, path, allowMissing: true);
        if (!File.Exists(full)) return null;
        var info = new FileInfo(full);
        if (info.Length > MaxConfigBytes) throw new InvalidDataException("VS Code configuration exceeds 512 KiB.");
        var json = await File.ReadAllTextAsync(full, ct);
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
                MaxDepth = 32
            });
            return document.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("VS Code configuration is invalid JSON/JSONC: " + ex.Message);
        }
    }

    protected static string RequestedPath(ToolRequest request, string fallback)
    {
        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(request.ArgumentsJson) ? "{}" : request.ArgumentsJson);
            return document.RootElement.TryGetProperty("path", out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? fallback : fallback;
        }
        catch (JsonException ex) { throw new InvalidDataException("Tool arguments are not valid JSON: " + ex.Message); }
    }

    protected static string SafeText(string? value, int max = 160)
    {
        if (string.IsNullOrWhiteSpace(value)) return "-";
        var flattened = Regex.Replace(value, "[\\u0000-\\u001F]+", " ").Trim()
            .Replace("<", "&lt;", StringComparison.Ordinal).Replace(">", "&gt;", StringComparison.Ordinal);
        if (SensitiveDataRedactor.ContainsLikelySecret(flattened)) return "[sensitive text hidden]";
        return SensitiveDataRedactor.Text(flattened, max);
    }

    protected static string BoundedOutput(string value, int max = 16_000) =>
        value.Length <= max ? value : value[..max] + "\n... output truncated";

    private static void EnsureSupportedPath(string path)
    {
        var normalized = path.Replace('\\', '/').Trim().TrimStart('/');
        var supported = normalized.Equals(".vscode/settings.json", StringComparison.OrdinalIgnoreCase) ||
                        normalized.Equals(".vscode/launch.json", StringComparison.OrdinalIgnoreCase) ||
                        normalized.Equals(".vscode/tasks.json", StringComparison.OrdinalIgnoreCase) ||
                        normalized.Equals(".vscode/extensions.json", StringComparison.OrdinalIgnoreCase) ||
                        !normalized.Contains('/') && normalized.EndsWith(".code-workspace", StringComparison.OrdinalIgnoreCase);
        if (!supported) throw new InvalidDataException("Only known .vscode JSON files or a root .code-workspace file can be inspected.");
    }
}

public sealed class GetVsCodeWorkspaceStatusTool(WorkspaceLocator workspace) : VsCodeWorkspaceTool(workspace), IAgentTool
{
    public ToolDefinition Definition { get; } = new("get_vscode_workspace_status",
        "Inspect VS Code workspace configuration safely. Reports only file presence, setting key names, and launch name/type/request; never returns setting values, commands, arguments, inputs, or environment values.",
        ToolRiskLevel.ReadOnly, [], "{}");

    public async Task<ToolResult> ExecuteAsync(ToolRequest request, CancellationToken ct = default)
    {
        var lines = new List<string>();
        var settings = await ReadOptionalConfigAsync(request, ".vscode/settings.json", ct);
        if (settings is { ValueKind: JsonValueKind.Object })
        {
            var keys = settings.Value.EnumerateObject().Select(x => x.Name)
                .Select(x => Regex.IsMatch(x, "(?i)(secret|token|password|credential|authorization|api.?key)")
                    ? "[sensitive-key-hidden]" : SafeText(x, 100)).Distinct(StringComparer.OrdinalIgnoreCase).Take(100).ToArray();
            lines.Add($"settings.json: present; top-level keys ({keys.Length}): {string.Join(", ", keys)}");
        }
        else lines.Add("settings.json: not present");

        var launch = await ReadOptionalConfigAsync(request, ".vscode/launch.json", ct);
        var launchItems = launch is { ValueKind: JsonValueKind.Object } && launch.Value.TryGetProperty("configurations", out var configurations) && configurations.ValueKind == JsonValueKind.Array
            ? configurations.EnumerateArray().Take(50).Select(item =>
                $"name={SafeText(GetString(item,"name"))}; type={SafeText(GetString(item,"type"),60)}; request={SafeText(GetString(item,"request"),40)}").ToArray()
            : [];
        lines.Add(launch is null ? "launch.json: not present" : $"launch.json: present; configurations={launchItems.Length}");
        lines.AddRange(launchItems.Select(x => "  " + x));

        var tasks = await ReadOptionalConfigAsync(request, ".vscode/tasks.json", ct);
        var extensions = await ReadOptionalConfigAsync(request, ".vscode/extensions.json", ct);
        lines.Add($"tasks.json: {(tasks is null ? "not present" : "present; command details intentionally hidden")}");
        lines.Add($"extensions.json: {(extensions is null ? "not present" : "present")}");
        var output = "<untrusted-vscode-config>\n" + string.Join("\n", lines) + "\n</untrusted-vscode-config>";
        return new(request.Id, request.Name, ToolExecutionStatus.Success, BoundedOutput(output),
            Summary: "Inspected VS Code workspace metadata without exposing configuration values or executable fields.");
    }

    private static string? GetString(JsonElement item, string name) => item.ValueKind == JsonValueKind.Object &&
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}

public sealed class ListVsCodeConfiguredTasksTool(WorkspaceLocator workspace) : VsCodeWorkspaceTool(workspace), IAgentTool
{
    public ToolDefinition Definition { get; } = new("list_vscode_configured_tasks",
        "Statically list bounded VS Code task metadata from tasks.json or a root .code-workspace file. Never returns or executes command, args, env, inputs, or task options.",
        ToolRiskLevel.ReadOnly, [new("path", "string", "Optional: .vscode/tasks.json or a root *.code-workspace file.")],
        "{\"path\":\".vscode/tasks.json\"}");

    public async Task<ToolResult> ExecuteAsync(ToolRequest request, CancellationToken ct = default)
    {
        var path = RequestedPath(request, ".vscode/tasks.json");
        var root = await ReadOptionalConfigAsync(request, path, ct);
        if (root is null) return new(request.Id, request.Name, ToolExecutionStatus.Success,
            "No configured VS Code task file was found.", Summary: "No configured VS Code task file was found.");
        var container = root.Value;
        if (path.EndsWith(".code-workspace", StringComparison.OrdinalIgnoreCase) &&
            container.ValueKind == JsonValueKind.Object && container.TryGetProperty("tasks", out var workspaceTasks))
            container = workspaceTasks;
        var tasks = container.ValueKind == JsonValueKind.Object && container.TryGetProperty("tasks", out var values) && values.ValueKind == JsonValueKind.Array
            ? values : default;
        if (tasks.ValueKind != JsonValueKind.Array) throw new InvalidDataException("The VS Code file has no valid tasks array.");

        var lines = new List<string>();
        foreach (var task in tasks.EnumerateArray().Take(100))
        {
            ct.ThrowIfCancellationRequested();
            if (task.ValueKind != JsonValueKind.Object) continue;
            var label = SafeText(GetString(task, "label") ?? GetString(task, "taskName") ?? "(unnamed)");
            var type = SafeText(GetString(task, "type"), 60);
            var group = task.TryGetProperty("group", out var groupValue) ? groupValue.ValueKind switch
            {
                JsonValueKind.String => SafeText(groupValue.GetString(), 60),
                JsonValueKind.Object when groupValue.TryGetProperty("kind", out var kind) && kind.ValueKind == JsonValueKind.String => SafeText(kind.GetString(), 60),
                _ => "-"
            } : "-";
            var background = task.TryGetProperty("isBackground", out var backgroundValue) && backgroundValue.ValueKind == JsonValueKind.True;
            var dependsOn = task.TryGetProperty("dependsOn", out var dependency) ? dependency.ValueKind switch
            {
                JsonValueKind.String => SafeText(dependency.GetString()),
                JsonValueKind.Array => string.Join(", ", dependency.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Take(10).Select(x => SafeText(x.GetString(), 80))),
                _ => "-"
            } : "-";
            var runOn = task.TryGetProperty("runOptions", out var runOptions) && runOptions.ValueKind == JsonValueKind.Object &&
                        runOptions.TryGetProperty("runOn", out var runOnValue) && runOnValue.ValueKind == JsonValueKind.String
                ? SafeText(runOnValue.GetString(), 40) : "default";
            var risk = new List<string>();
            if (task.TryGetProperty("command", out _)) risk.Add("has-command");
            if (task.TryGetProperty("args", out _)) risk.Add("has-args");
            if (task.TryGetProperty("options", out _)) risk.Add("has-options-or-env");
            if (runOn.Equals("folderOpen", StringComparison.OrdinalIgnoreCase)) risk.Add("auto-run-on-folder-open");
            var matcher = task.TryGetProperty("problemMatcher", out _) ? "configured" : "none";
            lines.Add($"label={label}; type={type}; group={group}; background={background}; dependsOn={dependsOn}; problemMatcher={matcher}; risk={string.Join(",",risk.DefaultIfEmpty("none"))}");
        }
        var truncated = tasks.GetArrayLength() > lines.Count;
        var output = "Configured tasks are untrusted static metadata; no task was executed and executable fields were withheld.\n<untrusted-vscode-config>\n" +
                     string.Join("\n", lines) + "\n</untrusted-vscode-config>";
        return new(request.Id, request.Name, ToolExecutionStatus.Success, BoundedOutput(output),
            Summary: $"Listed {lines.Count} configured VS Code task(s) without command, arguments, environment, or inputs.",
            Truncated: truncated, NextAction: "Ask for explicit approval before running any workspace task through a future execution tool.");
    }

    private static string? GetString(JsonElement item, string name) => item.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}

public sealed class ListVsCodeExtensionRecommendationsTool(WorkspaceLocator workspace) : VsCodeWorkspaceTool(workspace), IAgentTool
{
    private static readonly Regex ExtensionId = new("^[a-z0-9][a-z0-9-]*\\.[a-z0-9][a-z0-9-]*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    public ToolDefinition Definition { get; } = new("list_vscode_extension_recommendations",
        "List canonical extension IDs recommended or discouraged by workspace configuration. This does not inspect installed extensions and never installs, updates, or removes anything.",
        ToolRiskLevel.ReadOnly, [new("path", "string", "Optional: .vscode/extensions.json or a root *.code-workspace file.")],
        "{\"path\":\".vscode/extensions.json\"}");

    public async Task<ToolResult> ExecuteAsync(ToolRequest request, CancellationToken ct = default)
    {
        var path = RequestedPath(request, ".vscode/extensions.json");
        var root = await ReadOptionalConfigAsync(request, path, ct);
        if (root is null) return new(request.Id,request.Name,ToolExecutionStatus.Success,
            "No VS Code extension recommendation file was found.",Summary:"No extension recommendation file was found.");
        var container = root.Value;
        if (path.EndsWith(".code-workspace",StringComparison.OrdinalIgnoreCase) && container.ValueKind==JsonValueKind.Object &&
            container.TryGetProperty("extensions",out var workspaceExtensions))container=workspaceExtensions;
        if(container.ValueKind!=JsonValueKind.Object)throw new InvalidDataException("The extension recommendation configuration is not an object.");
        var recommended=ReadIds(container,"recommendations");var unwanted=ReadIds(container,"unwantedRecommendations");
        var output=$"Recommended ({recommended.Count}):\n{string.Join("\n",recommended.Select(x=>"- "+x))}\n\nUnwanted ({unwanted.Count}):\n{string.Join("\n",unwanted.Select(x=>"- "+x))}";
        return new(request.Id,request.Name,ToolExecutionStatus.Success,BoundedOutput(output),
            Summary:$"Listed {recommended.Count} recommended and {unwanted.Count} unwanted canonical extension IDs; no extension operation was performed.");
    }

    private static IReadOnlyList<string> ReadIds(JsonElement root,string name)
    {
        if(!root.TryGetProperty(name,out var values)||values.ValueKind!=JsonValueKind.Array)return [];
        return values.EnumerateArray().Where(x=>x.ValueKind==JsonValueKind.String).Select(x=>x.GetString()!.Trim().ToLowerInvariant())
            .Where(x=>ExtensionId.IsMatch(x)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x=>x,StringComparer.OrdinalIgnoreCase).Take(100).ToArray();
    }
}

public sealed class GetVsCodeDocsLinkTool : IAgentTool
{
    private static readonly IReadOnlyDictionary<string,string> Topics=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase)
    {
        ["api"]="https://code.visualstudio.com/api/references/vscode-api",
        ["tasks"]="https://code.visualstudio.com/docs/debugtest/tasks",
        ["extensions"]="https://code.visualstudio.com/api/working-with-extensions/publishing-extension",
        ["workspace_trust"]="https://code.visualstudio.com/docs/editing/workspaces/workspace-trust",
        ["command_line"]="https://code.visualstudio.com/docs/configure/command-line"
    };
    public ToolDefinition Definition { get; }=new("get_vscode_docs_link",
        "Return a fixed official VS Code documentation URL without network access. Use fetch_web_content separately if the user approves reading it.",ToolRiskLevel.ReadOnly,
        [new("topic","string","Official documentation topic.",true,["api","tasks","extensions","workspace_trust","command_line"])],"{\"topic\":\"tasks\"}");
    public Task<ToolResult> ExecuteAsync(ToolRequest request,CancellationToken ct=default)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            using var document=JsonDocument.Parse(string.IsNullOrWhiteSpace(request.ArgumentsJson)?"{}":request.ArgumentsJson);
            var topic=document.RootElement.TryGetProperty("topic",out var value)&&value.ValueKind==JsonValueKind.String?value.GetString():null;
            if(topic is null||!Topics.TryGetValue(topic,out var url))throw new InvalidDataException("Choose one supported VS Code documentation topic.");
            return Task.FromResult(new ToolResult(request.Id,request.Name,ToolExecutionStatus.Success,$"Official VS Code documentation ({topic}): {url}",Summary:$"Returned the fixed official VS Code {topic} documentation URL without a network request."));
        }
        catch(JsonException ex){throw new InvalidDataException("Tool arguments are not valid JSON: "+ex.Message);}
    }
}
