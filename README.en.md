# K.netagentV0.1 — A Windows-Native Single Agent

[简体中文](README.md) | **English**

K.netagent is a Windows-native desktop agent built with **.NET 10**. It focuses on Windows 10/11 and a user-controlled local workspace: streaming conversations, project workspaces, cross-session memory, permission-gated tools, task recovery, and auditable collaboration with external tools.

The long-term direction is a **Windows Incident & Operations Agent**, not just another chat window. This open-source repository documents the features actually implemented, the preview's limitations, and the work still ahead. This is the English documentation; it does not change the application's UI language. Some linked technical documents are currently in Chinese.

## Current status

The published application and the development entry points are different products for testing purposes:

| Entry point | Status | Registered tools | What it includes |
|---|---|---:|---|
| **K.netagent v0.1.0 / WPF** | Published installer | 13 | OpenAI-compatible streaming chat, session recovery, User/Session/Project memory, DPAPI-protected keys, controlled tools, sequential TaskGraph execution, and human-approved code iteration |
| **WPF development entry point** | Development source; not a new release | 24 | Image input, static web retrieval, a visible read-only browser, background commands, VS Code metadata tools, and additional safety controls |
| **WinUI 3 preview** | Source available on the preview branch; installer built locally, not published as a GitHub Release | 30 | Windows Event Center, local incident candidates, read-only service diagnostics, project workspaces, images/browser, TaskGraph and real diffs, VS Code integration, MCP peers, and escalation evaluation; code self-iteration remains disabled |

> The latest formal release is still **WPF v0.1.0**. The current WinUI and other development features are not included in that released installer. The local preview is **0.1.1-preview.20260926**; this does not mean V0.2 has been released.

### What the harness does—and does not do

K.netagent currently provides a lightweight-to-moderate **single-agent harness**, not a multi-agent orchestration platform.

- One central agent uses a bounded tool loop, approvals, audit records, memory, ToolSession statistics, sequential TaskGraphs, and node checkpoints.
- A `ToolSession` records calls, failures, and successful argument strategies. It does not run a model, act as a sub-agent, or automatically rewrite a tool after each collaboration.
- An MCP peer call requires a user-connected peer, an allowed tool, and approval for that particular call. It is not autonomous background collaboration.
- Code iteration follows a controlled proposal, validation, review, approval, and apply-or-rollback process. The application does not silently rewrite itself.
- A temporary source copy is **not an operating-system sandbox**. Build and test steps can still execute code with the current user's privileges, environment, and network access.

### Recorded validation

The local development snapshot was validated on **September 26, 2026**:

- WPF and WinUI Release solution build: **0 warnings, 0 errors**.
- **453/453 automated tests passed**, with no failures or skips; the packaging process checked the actual TRX execution counters.
- Two additional opt-in/native query checks passed, and 56 diagnostic tests passed in 10 repeated runs. Repeated runs are not additional unique tests.
- The installed preview opened both diagnostic windows. Its 486 installed application files matched the publish output by hash.
- Local installation, same-version reinstallation, and uninstallation succeeded; the existing 63 user-data files retained their hashes across uninstallation.

These are dated local results, not a claim that every provider or GUI workflow has been certified. Tests cover Core, Infrastructure, view-model behavior, and WinUI structural checks. Clean Windows machines, real-provider GUI workflows, full visual/keyboard/DPI acceptance, and cross-version data compatibility remain separate gates. See the [local preview validation record](docs/preview-20260926-validation.md) and [WinUI acceptance checklist](docs/winui3-ui-acceptance.md).

## Product direction

The goal is to help users understand local Windows events, services, scheduled tasks, processes, updates, devices, and failure evidence. Repository-oriented coding assistants can remain complementary, explicitly approved analysis peers; K.netagent keeps responsibility for Windows evidence, permission boundaries, and the user's final authorization.

Windows capabilities follow this order: **local inspection, evidence correlation, user confirmation, then narrowly scoped operations**. General administrator PowerShell, automatic elevation, and silent system changes are not the starting point.

## Install the application

### Published WPF release

Download the published installer from [GitHub Releases](https://github.com/kirisue/k-netagent/releases). The filename is:

```text
k-netagent-<version>-win-x64-setup.exe
```

Double-click Setup to install. It installs a multi-file application; it is **not a portable single-file application**.

- Python, the .NET SDK, and a separately installed .NET Runtime are not required to launch the packaged application.
- Installation is per user and does not normally require administrator privileges.
- A Start menu entry is created; a desktop shortcut is optional.
- Normal uninstallation preserves configuration, sessions, memory, and encrypted keys.
- The published WPF application's initial workspace is `Documents\K.netagent Workspace`, not its installation directory.

### Local WinUI preview

The preview installer is generated locally at:

```text
artifacts/installer/winui-preview/k-netagent-0.1.1-preview.20260926-winui-win-x64-setup.exe
```

This file is a build artifact, **not a checked-in file or a currently published GitHub download**. The preview installer requires Windows 10 build **19041** or newer (including Windows 11), and bundles .NET **10.0.12** and the Windows App SDK. Preview has a separate installation directory and uninstall identity from WPF, but both use `%LOCALAPPDATA%\TestAgent` for user data. Separate binaries do not establish complete data isolation or validated concurrent use.

The read-only browser uses the separately maintained **Evergreen WebView2 Runtime**. Its Loader is bundled; the browser runtime itself is not. Git, the .NET SDK, and other development tools are optional dependencies for their respective features, not prerequisites for opening the installed application.

The local preview is unsigned. Windows may show an unknown-publisher or reputation warning. A valid signature identifies the publisher but does not guarantee that a newly built file has enough reputation to avoid warnings; see [Microsoft's SmartScreen explanation](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/smartscreen-reputation). No signing or security-setting changes are performed automatically.

## Run from source

Requirements: Windows 10/11 and .NET SDK **10.0.400 or a compatible newer .NET 10 feature band**. SDK selection is defined in `global.json`.

For the WPF entry point:

```powershell
dotnet restore TestAgent.slnx
.\run-dotnet.ps1
```

Or run its project directly:

```powershell
dotnet run --project .\src\TestAgent.Desktop\TestAgent.Desktop.csproj
```

`run-dotnet.ps1` and `run-dotnet.cmd` still open WPF. To see the WinUI preview and its Windows diagnostics, use the separate launchers:

```powershell
.\run-winui.ps1
.\run-winui.ps1 -Page events -Configuration Release
.\run-winui.ps1 -Page services -Configuration Release
```

You can also double-click `run-winui.cmd`, or use:

```powershell
dotnet run --project .\src\KNetAgent.Desktop.WinUI\KNetAgent.Desktop.WinUI.csproj
```

Source launchers require the SDK and use `dotnet run` to restore/build the project. They do not install dependencies or request elevation. The packaged application has a different dependency boundary.

The WPF source entry point discovers the repository containing `TestAgent.slnx`. WinUI first restores the active project from `%LOCALAPPDATA%\TestAgent\workspaces.json`; without a saved project it falls back to the repository or default Documents workspace. WinUI does not yet replace the formal WPF release.

### Projects and workspaces — WinUI preview

Use the workspace button or the composer's **+** menu to create an empty project folder, open an existing folder, view recent projects, or switch workspaces.

- Creating a project only creates an empty folder. It does not run `git init`, apply a template, or download dependencies.
- The selected folder becomes the root for file tools, development commands, VS Code Bridge, MCP peers, and project memory.
- Switching projects safely relaunches WinUI so every workspace-bound service uses the same new root.
- Switching is blocked during generation, while an unsent draft exists, or while background commands are running.
- Chats and `Project` memory are isolated by stable `WorkspaceId`; `User` memory remains shared across projects.
- Disk roots, the user-profile root, Windows, Program Files, AppData, UNC paths, symlinks, and junction roots are rejected as workspaces.

Choosing a folder does not authorize file writes, process execution, or external agent calls. Per-operation approvals still apply.

## Configure a model

Open Settings and choose a Provider, Endpoint, Model, and API key.

- Provider presets include DeepSeek, OpenAI, OpenRouter, Ollama, and custom OpenAI-compatible endpoints.
- The development source uses editable model hints such as `gpt-5.6-sol`, `gpt-5.6-terra`, and `gpt-5.6-luna`. These are application presets, **not a guarantee of model availability or API compatibility**. Use a model supported by your endpoint and account.
- The development UI offers reasoning levels `none / low / medium / high / xhigh / max`, with `medium` as the recommended application default. Whether a setting is supported depends on the model and provider.
- Development defaults are **32,768 maximum output tokens** and a **300-second timeout**. Settings allow up to 128,000 output tokens and 600 seconds. The output limit is not the context-window size; higher limits can increase latency and cost, and do not force every response to use the full budget.
- When `providerId` is `openai` and the model name starts with `gpt-5.6`, the implementation sends `reasoning_effort` and `max_completion_tokens`. Other compatible providers retain the existing `max_tokens` behavior. This describes request construction in this app, not universal provider support.
- Keys are encrypted with Windows DPAPI for the current Windows user, separately from ordinary configuration. They are not written to the repository or normal application logs.
- Configuration and user data live under `%LOCALAPPDATA%\TestAgent\`.

These defaults describe the **development tree**. The published `v0.1.0` installer retains its original release-time defaults.

## Cross-session memory

Long-term memory and conversation history are separate capabilities:

| Type | Scope | How it is used |
|---|---|---|
| `User` | Shared across chats | Enabled memories can be selected and injected each turn |
| `Project` | Shared across chats in one workspace | Available to normal chat; long tasks narrow access using `RelevantPaths` |
| `Session` | One specific chat | Injected only in its associated session |
| Conversation history | Persisted chats | Retrieved on demand with `search_session_history`, not inserted wholesale into context |

The Memory panel supports creation, editing, enabling, disabling, and deletion. The agent can propose a memory through `save_memory`, but that proposal still requires approval. Conversations are not silently converted into permanent memories.

Each turn injects at most **20 memories / 12,000 characters**, treated as untrusted data. Suspected keys, tokens, passwords, and private keys are rejected from long-term memory. Sessions and memories are restored from `sessions\` and `memory\` under the user-data directory.

## Implemented capabilities

### Windows Event Center — WinUI preview

Open Windows Event Center from the navigation area or the **+** menu:

1. Choose local `Application` or `System`, a time range, level, event ID, or provider, then explicitly query. Opening the window does not read logs.
2. Review up to **200 events** and locally redacted details, then select the records you want to analyze.
3. Preview the selected evidence, limited to **20 events / 16,000 characters**. A selection or result change invalidates the previous preview.
4. Add it to the chat draft, inspect it, and send manually. Querying and monitoring do not themselves call the model.

Monitoring must be explicitly started or stopped. It watches one channel at a time with a 200-event buffer and a visible dropped-event count. **Closing the Event Center stops monitoring.** A bookmark stores the position, isolated by workspace, channel, and filters—not event bodies. Resume is subject to the selected time range and Windows log retention; a crash can cause repeated reads, and stale bookmarks require a fresh start.

`list_windows_event_channels` and approval-gated `query_windows_events` are available to the agent. The query tool returns metadata only. Event bodies reach a model only through selected, previewed evidence handed to chat. Automatic redaction covers common credentials, usernames, machine identity, paths, IP addresses, email addresses, and SIDs, but users must still check for business-sensitive content.

Local deterministic grouping organizes repeated Provider/Event IDs within a **five-minute window** and identifies application-crash or Service Control Manager incident candidates. Each candidate separates observed facts, hypotheses, and read-only next steps. Temporal proximity does not prove the same process, service, or root cause. A candidate's latest 20 records can enter the existing evidence-preview flow.

Custom Windows Event Provider registration, system-wide persistent monitoring, notifications, automatic root-cause confirmation, and repair are not implemented. See [Windows Event Center details](docs/windows-event-center.md).

### Windows service state — WinUI preview

The service window reads local Service Control Manager metadata: name, current state, startup type, and dependencies. It does not query on opening. Queries support an exact service name or a name fragment, at most **100 results**, and a **10-second budget**.

There are **no service start, stop, restart, configuration, or elevation operations**. A stopped service is not automatically a fault. Selecting a service creates a redacted preview that can be manually added to a chat draft.

`query_windows_services` requires approval for each local-environment read and returns bounded, redacted metadata—not service accounts, executable paths, or command lines. Installed shortcuts and `--page events` / `--page services` can open the diagnostic windows without querying automatically.

### Streaming chat and images

- OpenAI-compatible `/chat/completions` streaming, including normal content and optional provider `reasoning_content`.
- Multi-turn sessions, stop generation, clear/new chat, visible errors, and token usage.
- SSE and non-streaming JSON tool-call handling, with bounded network retries.

Both development desktop entry points support local PNG/JPEG attachments. Images are validated against actual format and **20 MP / 10 MB** limits, then re-encoded with metadata removed. They remain in memory for the current turn; sending requires confirmation of the target endpoint and model. Image requests are not automatically retried. Their tool loop allows at most three rounds, so at most four model requests carry the same image.

Image paths, filenames, Base64, and pixels are excluded from persisted sessions, memory, and tool audit bodies. Managed memory is not promised to support forensic-grade erasure. Image capability is off by default and must be enabled for a compatible model. Image endpoints are restricted to HTTPS or loopback HTTP without query parameters. Image turns skip the text-only self-review step.

### Controlled tools — development tree

WPF registers **24** tools and WinUI registers **30**. Both share 21 base tools and three browser tools; WinUI adds three MCP, two event, and one service tool.

| Category | Shared tools |
|---|---|
| Files and history | `list_files`, `read_file`, `search_text`, `search_session_history` |
| Writes and memory | `edit_file`, `apply_patch`, `save_memory` |
| Processes and network | `run_command`, `fetch_web_content`, `start_background_command`, `get_background_command`, `read_background_output`, `stop_background_command` |
| Static VS Code metadata | `get_vscode_workspace_status`, `list_vscode_configured_tasks`, `list_vscode_extension_recommendations`, `get_vscode_docs_link` |
| Live read-only VS Code metadata | `get_vscode_active_editor`, `get_vscode_diagnostics`, `list_vscode_available_tasks`, `list_vscode_installed_extensions` |
| Read-only browser | `open_browser_snapshot`, `read_browser_dom`, `capture_browser_viewport` |

WinUI additionally registers `list_mcp_peers`, `list_mcp_peer_tools`, `call_mcp_peer_tool`, `list_windows_event_channels`, `query_windows_events`, and `query_windows_services`. The published WPF `v0.1.0` installer still has its original **13** tools.

Every registered tool gets a `ToolSession` for the current chat or task. These are statistics containers, not model sessions or sub-agents. When more than 12 tools are registered, all ToolSessions still exist, but each model round exposes at most eight relevant tool schemas.

#### VS Code read-only bridge

Static tools parse redacted workspace metadata without returning or executing `command`, `args`, `env`, or `inputs`. Generic file tools cannot read raw `.vscode` or `*.code-workspace` configuration. Tools do not install, update, or remove extensions.

The live bridge connects to the local extension in `vscode-extension/` over a current-user-only Windows named pipe. Each launch creates a random pipe and a 256-bit in-memory pairing key, with mutual HMAC authentication. Only a trusted, local, single-folder VS Code window that exactly matches the agent workspace is accepted.

Allowed metadata includes the active editor's relative path/selection, diagnostic severity/code/source/location (not diagnostic text), non-executing task metadata, and installed-extension metadata. It does not read file contents or absolute paths, execute tasks, edit files, or manage extensions.

Both development desktop entry points expose temporary pairing; WinUI places it in Project Operations. Copy the pairing JSON and use **K.netagent: Pair Read-Only Bridge** in the extension's command palette. Pairing secrets are not persisted in configuration, logs, or VS Code storage. Clipboard cleanup only removes still-matching pairing text after connection or expiry.

`fetchTasks()` can activate installed task providers. Live tools therefore require `LocalEnvironmentRead` approval on every call, even though the bridge never starts a task.

Build and validate the local VSIX from the repository root, then install it manually with VS Code's **Install from VSIX...** command:

```powershell
powershell -NoProfile -File .\scripts\Package-VsCodeExtension.ps1
```

Output goes to the ignored `artifacts/vscode-extension/` directory. The script does not install or publish the extension automatically.

#### Read-only browser and viewport capture

The visible browser renders **encoded text snapshots**, not a live interactive website. `open_browser_snapshot` retrieves public HTTPS static content, then displays a sanitized local snapshot in an isolated WebView2 surface. Scripts, remote subresources, cookies, login state, navigation, forms, downloads, and new windows are blocked. `read_browser_dom` describes this snapshot's bounded text and elements, not a live page's DOM.

WinUI browser buttons use the same tool execution, approval, ToolSession, and audit path as agent-initiated calls. `capture_browser_viewport` is `SensitiveCapture` and requires explicit approval. Captured pixels stay private to the browser session and do not enter tool results, audit records, chats, or memories. The user must separately attach a capture to the next message before it can be used as a one-turn image attachment; that turn can include up to four model requests as described above. An unattached capture never triggers a model request.

### External agents / MCP peers — WinUI preview

The **+** menu contains External Agent / MCP. Supported integration paths are:

- Codex CLI, launched through the fixed `codex mcp-server` command.
- Claude Code, launched through the fixed `claude mcp serve` command.
- An already running custom Streamable HTTP MCP endpoint on `localhost`, `127.0.0.1`, or `[::1]`.

These describe K.netagent's adapters; compatibility still depends on the installed peer and its available tools. STDIO connections start a dedicated child process only after the user clicks Connect. They do not attach to an existing interactive terminal. Startup, refresh, peer listing, and ordinary chat do not auto-connect. Application exit closes the STDIO processes it started.

Configuration stores absolute executable paths, loopback endpoints, enabled state, and tool allowlists—not tokens. Calls require an already connected peer, explicit permission for the selected tool, and fresh approval for the individual invocation.

The Codex adapter requests the current workspace, a `read-only` sandbox, and `approval-policy=never`, returning analysis, evidence, or patch suggestions. The Claude adapter allows workspace-scoped `Read / View / LS`, not `Edit / Bash / Glob / Grep`. External output is treated as untrusted data: K.netagent neither automatically replays it nor directly applies outside modifications.

#### Escalation evaluator

After a draft answer, a deterministic evaluator scores explicit collaboration requests, tool failures/timeouts, repeated calls, exhausted rounds, missing successful writes or validation, empty answers, and obvious uncertainty. Successful writes and verification lower the score. The default threshold is **50**.

An escalation can be proposed only when exactly one connected, enabled Codex peer permits the `codex` tool and has opted into evaluator-proposed delegation. Rejected operations, cancellation, explicit refusal of outside agents, image turns, or zero/multiple eligible peers suppress automatic proposals. At most one escalation occurs per run. After success, the original model performs one synthesis without tools; only the final answer is persisted.

The score is not authorization and the evaluator does not connect peers or execute tools directly. See [MCP Peer Gateway](docs/mcp-peer-gateway.md).

Optional development commands require trusted installed copies of the .NET SDK, Git, or `rg`. Missing development tools do not prevent ordinary application startup and do not trigger forced downloads.

### Long tasks with one agent

The WPF long-task page and WinUI task/file/change window use the same bounded workflow:

1. Inspect the project and propose a DAG of at most **12 nodes**.
2. Let the user review the plan.
3. Run nodes sequentially with one agent and tools as needed.
4. Persist checkpoints and recover explicitly.

Each node receives the overall goal, its own goal, direct dependency summaries, relevant paths, and acceptance criteria. File tools are additionally restricted by node `RelevantPaths`. State is stored under `%LOCALAPPDATA%\TestAgent\tasks\`.

Completed nodes are not rerun on recovery. Nodes left `Running` after a restart become `NeedsReview`; operations with unknown results are not replayed automatically. A failed node blocks its descendants, while independent nodes can continue. `AcceptanceCriteria` guides the agent and human reviewer—it is not yet an independent machine-verifiable release gate.

This is still one agent, not a worker per node. WinUI plans, checkpoints, file trees, and diffs use real backend data. Without Git, the UI shows only `ModifiedFiles` recorded by approved successful tools instead of inventing a diff.

### Controlled self-iteration

- Text-answer self-review is limited to one round; review failure preserves the original answer. Image turns skip this review.
- WPF code iteration uses allowlisted targets from `iteration-guides/`, proposes changes, builds/tests a temporary copy, and requires approval before changing the real source.
- Successful iteration records go to `iterations/YYYY-MM-DD.md`.

**WinUI code self-iteration is disabled for safety.** Its operations area locates K.netagent's own solution/projects rather than the user's active workspace. However, temporary-copy validation still executes generated build/test code with the logged-in user's privileges before approval. Proposal generation and Apply remain disabled until a genuinely low-privilege, no-network validator exists. Do not approve untrusted code merely because it was tested in a temporary folder.

## Security boundaries

- File tools stay within the workspace and reject sensitive locations, credentials, private keys, `.git`, `bin`, `obj`, `.env`, sensitive history, and symlink/junction escapes.
- Writes, process start/stop, memory saves, and external network access require explicit approval.
- Development commands use constrained recipes for trusted absolute-path `dotnet`, `rg`, and read-only `git`. Builds, tests, and runs can execute workspace code; review its origin before approval.
- Static web retrieval accepts public HTTPS only, with bounded output and safe links. Private-network destinations, credential-bearing URLs, cookies, redirects, JavaScript, and oversized bodies are rejected. Links and forms are not executed.
- Visible browsing uses the same network restrictions and renders encoded text without active remote content. Capture needs per-call approval and explicit attachment before model transmission.
- Tool output, workspace files, pages, history, and memory are untrusted context—not user authorization.
- VS Code live reads require current-user authentication, an exact workspace match, and per-call approval. Pairing credentials remain in memory.
- MCP peers are explicitly connected local STDIO or loopback HTTP services. Tools are individually allowlisted and approved again for each call; delegated prompt bodies are not stored in tool audits or ToolSession bodies.
- The escalation score never replaces approval.
- Redacted tool audits are stored under `%LOCALAPPDATA%\TestAgent\audit\`.

## Build, test, and package

```powershell
dotnet restore TestAgent.slnx
dotnet build TestAgent.slnx -c Release --no-restore
dotnet test TestAgent.slnx -c Release --no-build --no-restore
```

Build a WPF Setup installer from the current source:

```powershell
.\build-installer.ps1 -Version 0.1.0 -InstallBuildTools
```

The version argument labels the build; building today's source as `0.1.0` does not recreate the historical release. The default desktop target remains WPF. `-InstallBuildTools` explicitly allows installation of the maintainer's Inno Setup build tool when missing.

The script runs Release tests, publishes a self-contained `win-x64` multi-file application, packages it into a Setup EXE with Inno Setup, writes a SHA-256 checksum, and cleans its staging directory unless `-KeepStaging` is used. It does not produce a portable single-file app.

```text
artifacts/installer/k-netagent-<version>-win-x64-setup.exe
artifacts/installer/k-netagent-<version>-win-x64-setup.exe.sha256
```

Build the WinUI preview explicitly:

```powershell
.\build-installer.ps1 -Desktop WinUI -Version 0.1.1-preview.20260926 -RuntimeFrameworkVersion 10.0.12
```

The WinUI path checks executed test reports, bundled runtimes, Windows App SDK dependencies, and XBF/PRI resources against the current isolated build output. It creates an EXE, checksum, and local build manifest under `artifacts/installer/winui-preview/`. Local manifests can contain machine paths; review them before any public upload. Inno Setup is a maintainer build dependency, not an end-user runtime requirement.

**The GitHub `v*` tag workflow still defaults to WPF.** Pushing such a tag triggers the release workflow; it has not been switched to publish WinUI previews. A source push is not a new version release.

## Repository layout

- `src/TestAgent.Core` — agent loop, models, task graphs, public interfaces.
- `src/TestAgent.Infrastructure` — model providers, JSON persistence, safe tools, process and Windows services.
- `src/TestAgent.Desktop` — WPF/MVVM desktop entry point and dependency injection.
- `src/KNetAgent.Desktop.WinUI` — preview WinUI 3 shell sharing Core and Infrastructure.
- `tests/TestAgent.Tests` — Core, Infrastructure, view-model behavior, and WinUI structural tests.
- `vscode-extension` — local read-only bridge extension, without third-party runtime dependencies.
- `installer` — Inno Setup definitions.

## Not implemented or not enabled

- Multi-agent/sub-agent orchestration; autonomous creation, modification, or publication of tools across sessions.
- A real low-privilege, no-network process sandbox, and safe WinUI self-iteration validation/transactions.
- Unified run traces, trajectory replay, benchmark tasks, and independent release evaluation gates.
- An interactive shell/PTY.
- Complete clean-machine WinUI acceptance, cross-version data migration validation, and signed distribution.
- Custom Windows event channels, notifications, and full incident evidence correlation beyond local candidate grouping.
- Native scheduled-task, update, device, and reliability diagnostics beyond the implemented local service-state reads.
- VS Code file-body reads, task execution, editor writes, or extension installation/update/removal.
- Interactive browser clicks/forms/login-state automation.
- Arbitrary application/desktop capture, general screen understanding, and GUI automation.

These capabilities need their own identities, permissions, and visible operations. General shell access must not be used to bypass those boundaries.

## Roadmap: Windows Incident & Operations Agent

This roadmap separates implemented foundations from future work. None of the remaining items should be read as a feature of the published WPF release.

### Phase 1: Windows Event Center — basic queries and monitoring implemented

The native .NET event-reader integration supports local Application/System queries, structured filters, explicit window-scoped monitoring, bookmark recovery, and user-selected evidence handoff. The existing agent tools are `list_windows_event_channels` and `query_windows_events`.

Monitoring is currently controlled by Event Center buttons. The following tool names are **planned, not implemented**:

- `start_windows_event_watch`
- `get_windows_event_watch`
- `stop_windows_event_watch`
- `build_windows_incident`

K.netagent's own Windows event channels are not registered. Remote logs, `Security`, arbitrary log files, and log clearing are not exposed.

### Phase 2: Incident correlation — local candidate grouping implemented

Repeated events, application failures, and SCM events can be grouped within five-minute windows. Process/service identity extraction and matching are not yet implemented, so the current grouping cannot confirm a common cause.

The future incident model should distinguish facts directly proved by events, rule-based inferences, and model hypotheses. It should retain Run ID, Tool Call ID, Workspace ID, and event Record ID references. A future report might correlate .NET Runtime 1026, Application Error 1000, and WER 1001, but a claim such as “the same process exited three times” would require identity evidence—not just matching timestamps.

### Phase 3: More native Windows diagnostics — service reads implemented

- Implemented: local service state, startup type, and dependencies. Precise correlation with service exit history is still pending.
- Planned: scheduled-task state, last result, and next run time.
- Planned: process crash/hang and Windows Error Reporting evidence.
- Planned: Windows Update installation/failure history, device/driver problems, and basic disk, memory, network-adapter, and reliability information.

Prefer Windows/.NET APIs or explicit COM interfaces rather than a general shell as a permission bypass.

### Phase 4: Controlled remediation — planned

Only after diagnostics and evidence handling are reliable should narrowly scoped operations be considered: restarting a selected service, stopping a selected process, rerunning an existing scheduled task, opening the relevant Windows Settings page, or exporting a redacted diagnostic bundle.

Every system change must receive separate approval and record its target and outcome. Unknown outcomes must not be automatically replayed. Automatic Defender shutdown, firewall modification, log clearing, arbitrary registry writes, unrestricted administrator PowerShell, and automatic elevation are outside this design.

### K.netagent's own Windows events and notifications — planned

Potential channels include `K.netagent/Admin`, `K.netagent/Operational`, and an opt-in `K.netagent/Debug`. Custom provider registration requires installation-time system integration; it must remain optional so the main application can stay per-user and non-elevated.

Local redacted JSONL auditing remains the default. Future Windows event records should contain only state, stable error codes, timing, token counts, and correlation IDs—not full prompts, answers, reasoning, keys, tool bodies, paths, images, MCP output, or VS Code pairing data.

Local incident notifications are not implemented. Monitoring after application exit would require a separate, low-privilege, uninstallable Windows service, which belongs to a later phase.

## Contributing and security reports

- [Contributing](CONTRIBUTING.md)
- [Code of Conduct](CODE_OF_CONDUCT.md)
- [Security policy](SECURITY.md)
- [Changelog](CHANGELOG.md)
- [Open-source checklist](OPEN_SOURCE_CHECKLIST.md)

## License

MIT. See [LICENSE](LICENSE).
