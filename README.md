# K.netagentV0.1 — Windows 原生单 Agent

K.netagent 是使用 **.NET 10** 构建的 Windows 原生桌面 Agent。项目优先服务 Windows 10/11，目标不是复制一个聊天窗口，而是逐步形成一套由用户掌控的本地 Agent 工作环境：流式对话、项目工作区、跨会话记忆、受控工具、任务恢复和可审计的外部能力协作。

## 当前成果一览

本仓库同时包含已发布版本和未发布的开发入口，必须明确区分：

| 入口 | 状态 | 工具数 | 当前成果 |
|---|---|---:|---|
| **K.netagent v0.1.0 / WPF** | 已发布，可双击安装 | 13 | OpenAI-compatible 流式对话、会话恢复、User/Session/Project 记忆、DPAPI 密钥保护、受控工具、顺序 TaskGraph 与人工审批的代码迭代 |
| **当前 WPF 开发入口** | 未发布源码快照 | 24 | 在 V0.1 基础上增加图片输入、静态网页读取、可见只读浏览器、后台命令、VS Code 静态/实时只读工具及更多安全限制 |
| **WinUI 3 预览入口** | 本地预览安装包，尚未发布到 GitHub | 30 | Windows 事件中心与本地故障线索分组、Windows 服务只读诊断、项目工作区、图片/只读浏览器、TaskGraph/真实 Diff、VS Code、MCP 与 Escalation；自迭代执行仍安全禁用 |

> 最新正式 Release 仍是 **WPF v0.1.0**。当前分支中的 WinUI、工作区、MCP、图片和浏览器等开发内容不属于现有正式 WPF 安装包；本地 WinUI Preview 包另行说明，也不代表 V0.2 已发布。

### 当前 Harness 的真实定位

K.netagent 当前是一个 **轻中量、单 Agent Harness**，不是多 Agent 调度平台：

- 中央 Agent 具有有界工具循环、审批、审计、记忆、ToolSession、顺序 TaskGraph 和节点检查点。
- `ToolSession` 只记录调用次数、错误和成功参数策略，不运行模型，不是子 Agent，也不会在每次协作后自动修改工具。
- MCP Peer 是用户先连接、逐项允许工具并再次批准后的一次外部工具调用，不是后台自主协作的多 Agent 系统。
- 代码迭代仍遵循“限定目标 → 生成提案 → 构建测试 → 人工批准 → 应用或回滚”，不会静默改造自身。
- 迭代使用的临时源码副本只提供变更隔离，不是低权限、无网络的操作系统沙箱，不能安全执行来源不可信的代码。

### 最近验证

截至 **2026-09-26**，当前开发树：

- Release 解决方案构建：`0` 警告、`0` 错误。
- .NET 自动化测试：`453/453` 通过，`0` 失败、`0` 跳过；打包流程中的 TRX 确认实际执行了 453 项，包含故障分组、记录身份、服务诊断行为与原生缓冲区边界测试。
- WinUI 使用显式 `--workspace` 参数完成真实进程启动冒烟。

这些测试覆盖 Core、Infrastructure、WPF ViewModel、WinUI 事件中心实际 ViewModel 行为，以及其他 WinUI XAML/结构门禁；它们不能替代真实 Provider、真实 Codex/Claude MCP、安装器和完整 GUI 的人工验收。

## 产品方向

K.netagent 的长期目标不是成为另一个 Codex 外壳，而是成为 **Windows Incident & Operations Agent**：

- Codex 主要理解和修改代码仓库。
- K.netagent 负责理解 Windows 本机事件、服务、计划任务、进程、更新、设备和故障证据。
- Codex、Claude Code 等仍可作为经用户连接和审批的外部分析 Peer；Windows 状态、证据、权限和最终操作授权由 K.netagent 自己掌握。

未来 Windows 能力必须继续遵守“本地读取 → 证据关联 → 用户确认 → 受控操作”的顺序，不从通用管理员 PowerShell、自动提权或静默系统修改起步。

## 普通用户：双击安装

本轮本地 WinUI 预览安装包为 `0.1.1-preview.20260926`，位于：

```text
artifacts/installer/winui-preview/k-netagent-0.1.1-preview.20260926-winui-win-x64-setup.exe
```

这是双击安装的 Setup.exe，多文件应用由安装器展开；内置 .NET 10.0.12 与 Windows App SDK。Preview 使用独立安装目录和卸载标识，可与正式 WPF 版并存；用户数据仍使用 `%LOCALAPPDATA%\TestAgent`。只读浏览器需要系统的 Evergreen WebView2 Runtime，包中包含 Loader 而非浏览器运行时。Git、.NET SDK 等开发工具仍为可选能力。该预览未创建 GitHub 标签或 Release。

本机安装、同版本覆盖安装、两个诊断窗口启动和卸载检查已完成；安装文件逐一匹配本次 publish，卸载前后现有用户数据哈希一致。完整证据与 GUI、干净机器、跨版本兼容性的未验收项见 [本地预览验收记录](docs/preview-20260926-validation.md)。这些结果不代表稳定版已完成验收。

GitHub 上现有正式 WPF 版本仍从仓库的 **Releases** 页面下载：


```text
k-netagent-<版本>-win-x64-setup.exe
```

双击安装即可。安装包包含应用运行所需的 .NET Desktop Runtime：

- 不需要 Python。
- 不需要安装 .NET SDK 或 .NET Runtime。
- 默认按当前用户安装，不要求管理员权限。
- 自动创建开始菜单入口，可选择创建桌面快捷方式。
- 正常卸载不会删除用户的配置、会话、记忆或加密密钥。

未签名的开源测试构建可能显示 Windows SmartScreen“未知发布者”。这是签名与信誉提示，不是缺少运行依赖；有效代码签名可以标识发布者，但并不保证新安装包立即不再告警，文件和发布者的信誉仍需积累。详见 [Microsoft SmartScreen 说明](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/smartscreen-reputation)。

正式 WPF 安装版首次启动时会准备默认工作区：

```text
文档\K.netagent Workspace
```

文件、长任务和受控命令只在该工作区内运行，不会把安装目录当成用户项目。

## 开发者：从源码启动

要求：Windows 10/11、.NET 10.0.400 或更新的 .NET 10 SDK。仓库通过 `global.json` 统一选择兼容的 .NET 10 feature band。

```powershell
dotnet restore TestAgent.slnx
.\run-dotnet.ps1
```

也可以直接运行：

```powershell
dotnet run --project .\src\TestAgent.Desktop\TestAgent.Desktop.csproj
```

原来的 `run-dotnet.ps1` / `run-dotnet.cmd` 仍启动 WPF，不会显示 WinUI 的 Windows 诊断界面。正在开发的 WinUI 3 界面与稳定 WPF V0.1 并行存在，可用单独的源码入口：

```powershell
.\run-winui.ps1
.\run-winui.ps1 -Page events -Configuration Release
.\run-winui.ps1 -Page services -Configuration Release
```

也可以双击 `run-winui.cmd`；这些源码入口需要 .NET SDK，会通过 `dotnet run` 还原并编译项目，不是免依赖安装版，不会自动安装依赖或请求提权。直接命令仍然可用：

```powershell
dotnet run --project .\src\KNetAgent.Desktop.WinUI\KNetAgent.Desktop.WinUI.csproj
```

WinUI 3 入口当前用于界面迁移和功能对齐；正式安装包仍指向 WPF V0.1，直到聊天、工具、任务、浏览器、图片、自迭代和安装验收全部完成，且维护者明确下达发布命令。

WPF 源码入口会自动发现包含 `TestAgent.slnx` 的仓库。WinUI 3 入口会优先恢复 `%LOCALAPPDATA%\TestAgent\workspaces.json` 中的活动项目；没有记录时才回退到当前仓库或默认文档工作区。

### 项目与工作区（WinUI 3 预览）

WinUI 3 入口支持像 Codex 一样管理本地项目：点击会话顶部的工作区按钮，或打开输入区 `＋` 菜单，即可创建新项目、打开已有文件夹、查看最近项目并切换当前工作区。该界面尚未进入正式 V0.1 安装包。

- “创建新项目”只创建一个空文件夹，不会自动执行 `git init`、套用代码模板或下载依赖。
- 选中的目录会成为文件工具、开发命令、VS Code Bridge、MCP Peer 和项目记忆的工作根目录。
- 切换项目会安全重启 WinUI 进程，使所有工作区绑定服务从同一个新根目录重新建立，避免一次工具调用混用两个项目。
- Agent 正在生成、输入框仍有未发送内容或后台命令仍在运行时，程序会拒绝切换。
- 聊天会话与 `Project` 记忆按稳定的 `WorkspaceId` 隔离；`User` 记忆仍可跨项目共享。
- 最近项目保存在 `%LOCALAPPDATA%\TestAgent\workspaces.json`。程序拒绝把磁盘根目录、用户主目录根、Windows、Program Files、AppData、UNC、符号链接或 junction 当作工作区。

选择目录本身不会批准写入、执行命令或调用外部 Agent；原有的逐次审批规则保持不变。

## 首次配置

在“设置”页选择 Provider、Endpoint、Model 并输入 API Key。

- 支持 DeepSeek、OpenAI、OpenRouter、Ollama 与自定义 OpenAI-compatible 端点。
- OpenAI 预设使用 `gpt-5.6-sol`；模型输入仍可自由编辑，并提供 `gpt-5.6-terra` 与 `gpt-5.6-luna` 建议。
- GPT-5.6 推理强度可选 `none / low / medium / high / xhigh / max`，推荐默认值为 `medium`。
- 推荐默认最大输出为 32,768 tokens、超时 300 秒；设置页可调整到 128,000 tokens 和 600 秒。Tokens 是单次回答输出上限，不是上下文窗口，调高会增加潜在延迟和费用，并不表示每次都会用满。
- `reasoning_effort` 与 `max_completion_tokens` 只对官方 OpenAI 的 GPT-5.6 Chat Completions 请求启用；其他兼容 Provider 继续使用原有 `max_tokens`，避免不兼容字段导致请求失败。
- API Key 使用 Windows DPAPI、当前用户作用域加密保存。
- Key 不写入仓库、普通配置或日志。
- 配置与用户数据位于 `%LOCALAPPDATA%\TestAgent\`。

以上 GPT-5.6 建议、推理强度与 32K/300 秒默认值描述的是**当前开发树**。正式 `v0.1.0` 安装包仍使用发布时的旧默认配置。当 `providerId=openai` 且模型名以 `gpt-5.6` 开头时，当前实现发送 `reasoning_effort` 与 `max_completion_tokens`；模型名称是否真实可用仍取决于所选 Provider、Endpoint 和账户权限。

## 跨会话记忆

长期记忆和聊天历史是两套能力：

| 类型 | 范围 | 使用方式 |
|---|---|---|
| `User` | 所有聊天共享 | 每轮自动选择并注入已启用记忆 |
| `Project` | 当前工作区内跨聊天共享 | 普通聊天自动可用；长任务按 `RelevantPaths` 收窄 |
| `Session` | 只属于指定聊天 | 仅在对应会话自动注入 |
| 会话历史 | 已持久化的聊天 | 由 `search_session_history` 按需检索，不整库塞入上下文 |

记忆可在“记忆”页新增、编辑、启用、停用和删除，也可以由 Agent 调用 `save_memory` 提议保存；模型提议仍需人工批准。程序不会把每段聊天静默总结成永久记忆。

每轮最多注入 20 条、总计 12,000 字符，并把记忆作为不可信用户数据隔离。疑似 API Key、Token、密码或私钥不会被保存为长期记忆。

重启后，会话从 `%LOCALAPPDATA%\TestAgent\sessions\` 恢复，长期记忆从 `%LOCALAPPDATA%\TestAgent\memory\` 恢复。

## 当前能力

### Windows 事件中心（WinUI 3 预览）

路线图第一阶段已开始落地。点击左侧或输入区 `＋` 菜单中的“Windows 事件中心”：

1. 选择本机 `Application` / `System`、最近分钟数、级别、事件 ID 或来源，再查询；打开窗口不会自动读取日志。
2. 查看最多 200 条事件及本地脱敏详情，勾选需要分析的记录。
3. 生成证据预览，最多 20 条、16,000 字符；选择变化后需要重新预览。
4. 点击“加入聊天草稿”，返回主窗口检查并手动发送。查询和监控本身不调用模型。

支持显式开始监听、停止、按上次书签继续。每次只监听一个频道，缓冲上限 200 条，溢出数量可见。**当前监听仅在事件中心窗口打开期间运行，关闭窗口即停止。** 停止时保存按工作区、频道和筛选条件隔离的位置书签，不保存事件正文；崩溃后可能重复读取，自上次位置恢复仍受所选时间范围和 Windows 日志保留情况限制。陈旧书签会报错，需要用户重新开始。

Agent 新增 `list_windows_event_channels` 和 `query_windows_events`。后者每次需要审批，仅向模型返回事件元数据；正文需要用户在事件中心选中、预览后再交给模型。自动脱敏会隐藏常见凭据、用户名字段、本机身份、路径、IP、邮箱与 SID，但仍需人工检查业务敏感信息。

事件中心还支持“整理当前事件”：对最多 200 条记录做本地确定性分组，识别 5 分钟内重复的 Provider/Event ID，以及应用异常与服务控制管理器的候选关联。每组显示事实、待验证假设与只读检查建议；不从时间接近推断同一进程、同一服务或根因。可以选择一组的最新 20 条记录继续原有证据预览流程。

尚未实现自定义 Windows Event Provider 注册、系统级常驻监控、Windows 通知、自动根因确认或系统修复。详细说明见 [Windows 事件中心](docs/windows-event-center.md)。

### Windows 服务状态（WinUI 3 预览）

左侧或 `＋` 菜单中的“Windows 服务状态”通过本机服务控制管理器读取名称、当前状态、启动类型和依赖项。打开窗口不会查询；支持精确服务名或名称片段筛选、最多 100 项、10 秒预算。没有服务启动、停止、重启或修改操作；“已停止”本身不代表故障。

选中服务后显示脱敏证据预览，用户可加入聊天草稿后手动发送。Agent 工具 `query_windows_services` 每次走本机环境读取审批，只返回有界、脱敏元数据，不包含账户、可执行路径或命令行。命令行也可用 `--page events` 或 `--page services` 直接打开对应诊断窗口，但不会自动读取。

### 流式单 Agent 对话

- OpenAI-compatible `/chat/completions` 流式响应。
- 普通正文与 Provider 支持时的 `reasoning_content`。
- 多轮会话、停止生成、清空与新建会话、错误展示、token 用量。
- 普通 JSON 与 SSE 工具调用兼容，网络错误有界重试。

两个当前开发入口均支持本地 PNG/JPEG 图片附件：图片先校验真实格式与 20 MP / 10 MB 上限，再由 Windows 图像组件去元数据重编码。图片只保留在本轮内存中，发送前显示完整目标端点和模型并再次确认；含图请求不自动网络重试，工具循环最多三轮，因此最多四次模型请求会携带同一张图片。路径、文件名、Base64 和像素不会写入会话、记忆或工具审计，但托管内存不承诺法证级擦除。图片能力默认关闭，只有确认具体模型支持后才可启用，并且仅允许不含查询参数的 HTTPS 或本机回环 HTTP 模型端点。图片轮次当前跳过纯文本自审，避免无图审阅器改坏结果。

### 受控工具（当前开发树）

当前 WPF 开发入口注册 24 个工具，WinUI 3 预览注册 30 个。两者共享 21 个基础工具和 3 个只读浏览器工具；WinUI 另外增加 3 个 MCP Peer 工具、2 个 Windows 事件工具和 1 个 Windows 服务查询工具。

| 类别 | 两个开发入口共同提供 |
|---|---|
| 文件与历史 | `list_files`、`read_file`、`search_text`、`search_session_history` |
| 写入与记忆 | `edit_file`、`apply_patch`、`save_memory` |
| 进程与网络 | `run_command`、`fetch_web_content`、`start_background_command`、`get_background_command`、`read_background_output`、`stop_background_command` |
| VS Code 静态元数据 | `get_vscode_workspace_status`、`list_vscode_configured_tasks`、`list_vscode_extension_recommendations`、`get_vscode_docs_link` |
| VS Code 实时只读元数据 | `get_vscode_active_editor`、`get_vscode_diagnostics`、`list_vscode_available_tasks`、`list_vscode_installed_extensions` |

两个开发入口共同注册的浏览器工具：`open_browser_snapshot`、`read_browser_dom`、`capture_browser_viewport`。WinUI 3 预览另外注册：`list_mcp_peers`、`list_mcp_peer_tools`、`call_mcp_peer_tool`、`list_windows_event_channels`、`query_windows_events`、`query_windows_services`。

正式 `v0.1.0` WPF 安装包注册 13 个发布时工具；上述 24 工具配置属于当前未发布开发树。

VS Code 工具只解析脱敏后的工作区元数据，不返回或执行 `command`、`args`、`env`、`inputs`，也不会安装、更新或删除扩展。`.vscode` 与 `*.code-workspace` 原文对通用文件工具不可见，避免绕过专用解析器。

实时桥仍然只有一个中央 Agent。K.netagent 与 `vscode-extension/` 中的本地扩展通过当前 Windows 用户专属的命名管道连接；每次启动生成随机 Pipe 与 256-bit 内存配对密钥，并进行双向 HMAC 认证。扩展只接受可信、本地、单文件夹且与 Agent 工作区完全相同的 VS Code 窗口。配对后可以读取活动编辑器的相对路径/选区、Problems 的严重级别、代码、来源和位置（不传诊断正文）、当前配对工作区 `fetchTasks()` 的非执行元数据，以及已安装扩展清单；不会读取文件正文或绝对路径，也不能执行任务、命令或扩展操作。

两个当前开发入口都提供 VS Code 临时配对入口；WinUI 位于“项目运维”窗口。复制配对 JSON 后，在扩展命令面板执行 `K.netagent: Pair Read-Only Bridge` 并粘贴。配对码不写入配置、日志或 VS Code storage，并在连接成功或倒计时结束时只清理仍然匹配的剪贴板内容。`fetchTasks()` 可能唤醒已安装扩展提供的 Task Provider，所以实时工具统一归类为 `LocalEnvironmentRead`，每次调用仍需人工审批，桥本身绝不会启动任务。

本地扩展可在仓库根目录运行 `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Package-VsCodeExtension.ps1` 生成并校验 VSIX，然后在 VS Code 的扩展菜单中选择 **Install from VSIX...** 安装。产物写入被 Git 忽略的 `artifacts/vscode-extension/`；脚本不会自动安装或发布扩展。

两个当前开发入口的浏览器工具都提供用户可见的只读快照窗口。`open_browser_snapshot` 只获取公开 HTTPS 静态内容，再把经过 HTML 编码的文本快照显示到隔离 WebView2 中；窗口不会加载远端页面资源，不运行 JavaScript，不使用 Cookie 或登录状态，也不能点击、输入、提交表单、下载文件或打开新窗口。`read_browser_dom` 读取的是这份静态快照的有界文本与元素摘要，不是真实网页的活动 DOM。WinUI 中三个浏览器按钮同样经过 ToolExecution、逐次审批、ToolSession 和审计。

`capture_browser_viewport` 属于 `SensitiveCapture`，每次都需要明确审批。截图像素由浏览器会话私有保留，不进入工具结果、审计、会话或记忆；用户还必须将它明确附加到下一轮消息，它才会作为一次性图片输入发送给模型。未附加的截图不会自动触发模型请求。

工具采用 `1 个 Agent 会话 + N 个 ToolSession`。注册多少工具，就为当前聊天或任务建立多少个隔离统计会话；ToolSession 不调用模型、不独立规划，也不是子 Agent。工具超过 12 个时仍会建立全部 ToolSession，但每轮只向模型激活最相关的最多 8 个 schema。

### 外部 Agent / MCP Peer（WinUI 3 预览）

WinUI 3 输入区的 `＋` 菜单提供“外部 Agent / MCP”。用户可以显式添加并连接：

- Codex CLI：K.netagent 固定启动 `codex mcp-server`。
- Claude Code：K.netagent 固定启动 `claude mcp serve`。
- 自定义本机服务：仅接受已经运行在 `localhost / 127.0.0.1 / [::1]` 的 Streamable HTTP MCP endpoint。

STDIO 连接不是附着到已经打开的交互式终端，而是用户点击“连接”后由 K.netagent 启动一个专属 MCP 子进程。程序不会在启动、刷新、列举 Peer 或普通聊天时自动连接；应用退出会关闭它启动的 STDIO 子进程。配置只保存绝对可执行文件路径、回环地址、启用状态和工具白名单，不保存 Token。

`call_mcp_peer_tool` 会保持在 Agent 的候选工具中，使当前 Agent 在能力不足或验证失败时可以提出外部协作。但执行仍要求：Peer 已由用户连接、工具已逐项允许、当前调用再次人工批准。Codex 被强制使用当前工作区、`read-only` sandbox 和 `approval-policy=never`，只返回分析、证据或补丁建议；Claude 首版只允许工作区内的 `Read / View / LS`，不开放 `Edit / Bash / Glob / Grep`。外部输出始终作为不可信数据隔离，K.netagent 不自动重放，也不会直接应用外部修改。

确定性 Escalation Evaluator 会在初稿后根据显式协作请求、工具失败/超时、重复调用、工具轮次耗尽、要求实现但没有成功写入、要求验证但没有成功验证、空答案或明显不确定答案计算分数；成功写入和成功验证会降低分数。默认阈值为 50。只有唯一一个已连接、启用、允许 `codex` 且打开“允许评估不足时提出委托”的 Codex Peer 才有资格被建议。用户拒绝过操作、取消任务、明确不要外部 Agent、含图轮次、无合格 Peer 或多个合格 Peer都会抑制自动委托。一次运行最多升级一次，成功后由原模型做一次无工具综合并只持久化最终答案。

详细操作与边界见 [MCP Peer Gateway](docs/mcp-peer-gateway.md)。

安装版的聊天、记忆、文件、网页和基础 Agent 功能不要求额外依赖。`run_command` 与后台开发命令属于可选开发能力：只有电脑已经安装可信的 .NET SDK、Git 或 `rg` 时，相应命令才可用；缺少这些开发工具不会影响应用启动，也不会弹出强制下载提示。

### 单 Agent 长任务分治（当前开发入口）

WPF“长任务”页和 WinUI“任务、文件与真实变更”窗口均已接入：

```text
项目扫描 → 最多 12 节点 DAG → 人工审阅
→ 单 Agent 顺序执行并按需调用工具 → 持久化检查点 → 恢复
```

- 每个节点只接收总目标、当前目标、直接依赖摘要、相关路径和验收条件。
- 文件工具同时受节点 `RelevantPaths` 限制。
- 状态写入 `%LOCALAPPDATA%\TestAgent\tasks\`。
- 已完成节点恢复时不会重跑；进程重启后遗留为 `Running` 的节点进入 `NeedsReview`，不会自动重放结果未知的操作。
- 某节点失败只阻塞其后继，独立节点仍可继续。
- `AcceptanceCriteria` 当前用于约束 Agent 和帮助人工审阅，尚不是独立、确定性的机器验收或发布门禁。

这仍然是一个 Agent，不会按节点启动多个 Agent 或后台 Worker。WinUI 的计划、检查点、文件树与变更全部来自真实后端数据；Git 不可用时，只展示已批准成功工具记录的 `ModifiedFiles`，不会生成假 Diff。

### 受控自迭代（WPF 可用；WinUI 安全禁用）

- 纯文本回答自审：最多一轮；审阅失败时保留原回答。图片轮次当前跳过自审。
- 代码自迭代：读取 `iteration-guides/` 白名单提案，在临时副本构建和测试，人工批准后才写入真实源码。
- 成功记录写入 `iterations/YYYY-MM-DD.md`。

代码自迭代主要面向源码工作区。临时副本只提供变更隔离与回滚，不是操作系统进程沙箱；不要批准来源不可信、会在构建或测试阶段执行的代码。

WinUI 运维中心只从运行目录向上验证 K.netagent 自身 solution、Core、Infrastructure 和 WinUI 项目，不使用用户当前 Active Workspace。但当前临时副本验证仍会在审批前以登录用户权限执行模型生成的构建/测试，因此 WinUI 已明确禁用生成与 Apply，直到低权限、无网络隔离验证器完成。

## 安全边界

- 文件工具只能访问当前工作区，拒绝 `.git`、`bin`、`obj`、`.env`、凭据、私钥、敏感历史、符号链接与 junction 逃逸。
- 写文件、启动或停止进程、保存记忆、访问外部网络都需要明确审批。
- `run_command` 与后台命令只允许可信绝对路径的受限 `dotnet`、`rg` 和只读 `git` 配方。
- `dotnet build/test/run` 可能执行工作区代码，批准前必须确认代码来源。
- `fetch_web_content` 只读取公开 HTTPS 静态快照，提取有界标题层级与安全链接；拒绝私网、凭据 URL、Cookie、跳转、JavaScript 与超限正文，不执行链接或表单。
- 可见浏览器使用同一套公开 HTTPS、DNS 与下载限制；远端正文只以编码后的静态文本显示，所有脚本、导航、登录态、权限请求和子资源访问均被阻断。
- 浏览器视口截图必须单次审批并由用户显式附加到下一轮；像素只允许一次性转移，不会出现在工具输出、工具审计、会话或长期记忆中。
- 工具输出、工作区文件、网页、历史与记忆都按不可信数据回灌，不能替代用户授权。
- VS Code 实时桥只接受当前用户、双向认证且工作区精确匹配的连接；配对密钥仅驻内存，实时元数据每次读取都需审批。
- MCP Peer 只允许用户显式连接的本机 STDIO 或回环 HTTP 服务；每个外部工具逐项 allowlist、每次调用再次审批，委托提示不会写入工具审计或 ToolSession 正文。
- Escalation Evaluator 不连接 Peer、不直接执行工具，也不把评分当作授权；它只能触发原有的单次审批流程。
- 工具审计位于 `%LOCALAPPDATA%\TestAgent\audit\`，敏感字段会被脱敏。

## 构建、测试与安装包

```powershell
dotnet restore TestAgent.slnx
dotnet build TestAgent.slnx -c Release --no-restore
dotnet test TestAgent.slnx -c Release --no-build --no-restore
```

生成双击安装的自包含 Setup：

```powershell
.\build-installer.ps1 -Version 0.1.0 -InstallBuildTools
```

该脚本会：

1. 运行 Release 测试。
2. 发布 `win-x64` self-contained 多文件应用。
3. 使用 Inno Setup 把应用与 .NET Runtime 打包成单个 Setup EXE。
4. 输出 SHA-256 校验文件。
5. 删除临时 publish staging，不生成便携版发布物。

最终文件位于：

```text
artifacts\installer\k-netagent-<版本>-win-x64-setup.exe
artifacts\installer\k-netagent-<版本>-win-x64-setup.exe.sha256
```

构建当前 WinUI 本地预览包：

```powershell
.\build-installer.ps1 -Desktop WinUI -Version 0.1.1-preview.20260926 -RuntimeFrameworkVersion 10.0.12
```

脚本检查实际测试报告，校验随包运行时、Windows App SDK 与本次构建的 XBF/PRI 资源，然后生成 EXE、SHA-256 和本地构建清单。`-Desktop Wpf` 保留旧入口。Inno Setup 只属于维护者构建工具。

当前 GitHub 标签发布流程仍默认选择 WPF。推送任何 `v*` 标签都会触发 Release 工作流；WinUI 预览包目前仅本地生成，未切换公开发布目标。

## 项目结构

- `src/TestAgent.Core`：Agent 循环、模型、任务图与公共接口。
- `src/TestAgent.Infrastructure`：Provider、JSON 存储、安全工具与进程服务。
- `src/TestAgent.Desktop`：WPF/MVVM 与依赖注入。
- `src/KNetAgent.Desktop.WinUI`：正在开发的 Codex 风格 WinUI 3 桌面壳；复用同一 Core 和 Infrastructure。
- `tests/TestAgent.Tests`：Core、Infrastructure、WPF ViewModel 与 WinUI XAML/结构自动化测试。
- `vscode-extension`：无第三方运行依赖的 VS Code 实时只读桥扩展源码。
- `installer`：Inno Setup 安装器定义。

## 尚未完成或开放

- 多 Agent / 子 Agent 调度
- 自动创造、自动修改或跨会话自主发布工具
- 低权限、无网络的真实进程沙箱
- WinUI 自迭代所需的审批前隔离验证器与崩溃安全事务
- 统一 Run Trace、轨迹回放、基准任务集和发布评测门禁
- 交互式 Shell / PTY
- WinUI 干净 Windows 机器验收、跨版本数据迁移及正式签名发布（本地 Preview 安装包已生成）
- Windows 自定义事件频道注册、本地通知和自动 Incident 证据关联（事件查询与窗口内监听已实现）
- 计划任务、更新、设备与可靠性记录的原生诊断（本机服务只读状态已实现）
- VS Code 文件正文读取、任务执行、编辑器写入与扩展安装/更新/删除
- 可点击、可填写表单或使用登录态的浏览器自动化
- 任意应用/桌面截图、通用屏幕理解与 GUI 自动化

这些能力需要独立身份、权限和可见操作边界，不能通过通用 Shell 绕过审批。

## 未来路线图：Windows Incident & Operations Agent

> 路线图分阶段推进；事件查询、窗口内监听、书签和选中证据交接已在 WinUI 开发版实现。其余未完成项仍是规划，不属于当前正式发布版。

### 阶段 1：Windows 事件中心（基础查询与监听已实现）

使用 .NET 原生 [`System.Diagnostics.Eventing.Reader`](https://learn.microsoft.com/en-us/dotnet/api/system.diagnostics.eventing.reader?view=windowsdesktop-10.0) 接口：

- 结构化查询 `Application` 和 `System` 日志。
- 按时间、等级、Provider 和 Event ID 过滤。
- 使用 `EventLogWatcher` 在 K.netagent 运行期间监听新事件。
- 使用 `EventBookmark` 保存读取位置，重启后继续。
- WinUI 提供频道树、事件列表、常规/详细信息、关联证据和 Agent 分析区域。
- 用户选择具体证据并确认后，才把有界、脱敏内容交给模型。

已实现的 Agent 工具：

- `list_windows_event_channels`
- `query_windows_events`

监听目前由事件中心按钮显式控制。以下 Agent 工具仍待后续设计与实现：
- `start_windows_event_watch`
- `get_windows_event_watch`
- `stop_windows_event_watch`
- `build_windows_incident`

当前只允许本机 `Application` 和 `System`；K.netagent 自身的 Windows 事件频道尚未注册。不开放远程日志、`Security` 或日志文件，也不提供清除事件日志的工具。

### 阶段 2：故障事件关联（本地线索分组已实现）

当前已把重复事件、应用异常和 SCM 事件按 5 分钟窗口整理成候选 `Incident`。尚未提取并匹配进程或服务身份，不能确认共同原因。完整诊断证据关联仍在规划，下面仅展示目标格式：

```text
INC-20260819-001
主题：应用连续崩溃
证据：.NET Runtime 1026 + Application Error 1000 + WER 1001
已确认事实：同一进程在 5 分钟内退出 3 次
待验证推测：可能与最近更新或依赖缺失有关
下一步：检查版本、更新历史和故障模块
```

Incident 必须区分事件直接证明的事实、规则推断和模型推测，并保存关联的 Run ID、Tool Call ID、Workspace ID 与事件 Record ID。

### 阶段 3：更多 Windows 原生诊断（服务只读查询已实现）

事件中心稳定后逐步接入：

- Windows 服务当前状态、启动类型及依赖已实现；与退出历史的精确关联待完成。
- 计划任务状态、最后结果和下次执行时间。
- 进程崩溃、挂起和 Windows Error Reporting 记录。
- Windows Update 安装与失败历史。
- 设备和驱动异常。
- 磁盘、内存、网络适配器和可靠性基础信息。

这些功能优先使用 Windows/.NET 原生 API 或明确的 COM 接口，不把通用 Shell 当作权限绕过层。

### 阶段 4：受控修复

只有诊断和证据闭环稳定后，才考虑加入：

- 重启明确选中的服务。
- 停止明确选中的进程。
- 重新运行已存在的计划任务。
- 打开对应 Windows 设置页面。
- 导出脱敏诊断包。

每次系统修改必须单独审批、记录目标和结果；未知结果不得自动重放。不会提供自动关闭 Defender、防火墙修改、日志清除、任意注册表写入、通用管理员 PowerShell 或自动提权。

### K.netagent 自身事件

未来可规划 `K.netagent/Admin`、`K.netagent/Operational` 和默认关闭的 `K.netagent/Debug` 频道，但自定义 Windows Event Provider 需要安装阶段注册。为保持当前用户安装和非管理员运行：

- 默认仍使用本地脱敏 JSONL 审计。
- 自定义“应用程序和服务日志”频道作为可选系统集成，不让主程序长期提升权限。
- Windows 事件日志只记录状态、稳定错误码、耗时、token 数和关联 ID。
- 永不记录完整问题、回答、推理、API Key、工具正文、完整路径、图片、MCP 输出或 VS Code 配对信息。

未来高优先级 Incident 可通过 Windows App SDK 的 [`AppNotificationManager`](https://learn.microsoft.com/en-us/windows/apps/develop/notifications/) 发送本地通知；通知尚未实现。当前监听随事件中心窗口关闭而停止。应用退出后继续监控需要独立、低权限且可卸载的 Windows 服务，属于更后期能力。

## 参与贡献与安全问题

- 贡献说明：[CONTRIBUTING.md](CONTRIBUTING.md)
- 行为准则：[CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md)
- 安全报告：[SECURITY.md](SECURITY.md)
- 版本记录：[CHANGELOG.md](CHANGELOG.md)
- GitHub 开源清单：[OPEN_SOURCE_CHECKLIST.md](OPEN_SOURCE_CHECKLIST.md)

## 许可证

MIT — 详见 [LICENSE](LICENSE)。
