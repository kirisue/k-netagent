# K.netagentV0.1 — Windows 原生桌面 Agent

K.netagentV0.1 是基于 **.NET 10 与 WPF** 开发的 Windows 原生桌面 Agent。许多成熟的 Agent 体验和系统级能力往往优先出现在 macOS，而 Windows 用户在本地工具协作、后台任务、跨会话记忆、长任务恢复与可控自动化等方面，仍缺少一套统一、透明并真正贴近 Windows 的实现。K.netagent 因此从 Windows 原生体验出发重新设计。

我们的目标不是复制一个聊天窗口，而是逐步构建一个能够在 Windows 上长期工作的个人 Agent：理解任务、管理上下文、调用受控工具、保存经过确认的记忆，并在程序中断后继续未完成的工作。

> **当前仓库是 K.netagentV0.1 开源版。** 它只公开并展示 K.netagent 完整路线中的核心能力子集，用于验证 Windows 原生 Agent 的架构、交互与安全边界。后续迭代版本将依次使用 `K.netagentV0.2`、`K.netagentV0.3` 等名称。

> `codex/v0.2-iteration` 是本地开发分支，不代表 V0.2 已发布。没有维护者的明确发布命令，不应推送 V0.2 标签或创建 V0.2 Release。

当前阶段优先把一个 Agent 做到可以日常使用：流式对话、跨会话记忆、受控工具、长任务分治、检查点恢复与人工审批。开源版暂不包含多 Agent 调度。

## 普通用户：双击安装

从仓库的 **Releases** 页面下载：

```text
k-netagent-<版本>-win-x64-setup.exe
```

双击安装即可。安装包包含应用运行所需的 .NET Desktop Runtime：

- 不需要 Python。
- 不需要安装 .NET SDK 或 .NET Runtime。
- 默认按当前用户安装，不要求管理员权限。
- 自动创建开始菜单入口，可选择创建桌面快捷方式。
- 正常卸载不会删除用户的配置、会话、记忆或加密密钥。

未签名的开源测试构建可能显示 Windows SmartScreen“未知发布者”。这是代码签名信誉提示，不是缺少运行依赖；正式消除该提示需要 Authenticode 代码签名证书。

安装版首次使用文件工具时，会创建：

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

源码启动时会自动把包含 `TestAgent.slnx` 的仓库作为工作区。

## 首次配置

在“设置”页选择 Provider、Endpoint、Model 并输入 API Key。

- 支持 DeepSeek、OpenAI、OpenRouter、Ollama 与自定义 OpenAI-compatible 端点。
- API Key 使用 Windows DPAPI、当前用户作用域加密保存。
- Key 不写入仓库、普通配置或日志。
- 配置与用户数据位于 `%LOCALAPPDATA%\TestAgent\`。

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

### 流式单 Agent 对话

- OpenAI-compatible `/chat/completions` 流式响应。
- 普通正文与 Provider 支持时的 `reasoning_content`。
- 多轮会话、停止生成、清空与新建会话、错误展示、token 用量。
- 普通 JSON 与 SSE 工具调用兼容，网络错误有界重试。

V0.2 开发分支增加本地 PNG/JPEG 图片附件：图片先校验真实格式与 20 MP / 10 MB 上限，再由 Windows 图像组件去元数据重编码。图片只保留在本轮内存中，发送前显示完整目标端点和模型并再次确认；含图请求不自动网络重试，工具循环最多三轮，因此最多四次模型请求会携带同一张图片。路径、文件名、Base64 和像素不会写入会话、记忆或工具审计，但托管内存不承诺法证级擦除。图片能力默认关闭，只有确认具体模型支持后才可启用，并且仅允许不含查询参数的 HTTPS 或本机回环 HTTP 模型端点。图片轮次当前跳过纯文本自审，避免无图审阅器改坏结果。

### 17 个受控工具（V0.2 开发分支）

文件与检索：

- `list_files`
- `read_file`
- `search_text`
- `search_session_history`

写入与记忆：

- `edit_file`
- `apply_patch`
- `save_memory`

进程与网络：

- `run_command`
- `fetch_web_content`
- `start_background_command`
- `get_background_command`
- `read_background_output`
- `stop_background_command`

VS Code 工作区静态理解：

- `get_vscode_workspace_status`
- `list_vscode_configured_tasks`
- `list_vscode_extension_recommendations`
- `get_vscode_docs_link`

VS Code 工具只解析脱敏后的工作区元数据，不返回或执行 `command`、`args`、`env`、`inputs`，也不会安装、更新或删除扩展。`.vscode` 与 `*.code-workspace` 原文对通用文件工具不可见，避免绕过专用解析器。

工具采用 `1 个 Agent 会话 + N 个 ToolSession`。注册多少工具，就为当前聊天或任务建立多少个隔离统计会话；ToolSession 不调用模型、不独立规划，也不是子 Agent。工具超过 12 个时仍会建立全部 ToolSession，但每轮只向模型激活最相关的最多 8 个 schema。

安装版的聊天、记忆、文件、网页和基础 Agent 功能不要求额外依赖。`run_command` 与后台开发命令属于可选开发能力：只有电脑已经安装可信的 .NET SDK、Git 或 `rg` 时，相应命令才可用；缺少这些开发工具不会影响应用启动，也不会弹出强制下载提示。

### 单 Agent 长任务分治

“长任务”页实现：

```text
项目扫描 → 最多 12 节点 DAG → 人工审阅 → 单 Agent 顺序执行
→ 工具验证 → 检查点 → 恢复
```

- 每个节点只接收总目标、当前目标、直接依赖摘要、相关路径和验收条件。
- 文件工具同时受节点 `RelevantPaths` 限制。
- 状态写入 `%LOCALAPPDATA%\TestAgent\tasks\`。
- 已完成节点恢复时不会重跑；中断写操作进入 `NeedsReview`，不会自动重放未知副作用。
- 某节点失败只阻塞其后继，独立节点仍可继续。

这仍然是一个 Agent，不会按节点启动多个 Agent 或后台 Worker。

### 受控自迭代

- 纯文本回答自审：最多一轮；审阅失败时保留原回答。图片轮次当前跳过自审。
- 代码自迭代：读取 `iteration-guides/` 白名单提案，在临时副本构建和测试，人工批准后才写入真实源码。
- 成功记录写入 `iterations/YYYY-MM-DD.md`。

代码自迭代主要面向源码工作区。临时副本只提供变更隔离与回滚，不是操作系统进程沙箱；不要批准来源不可信、会在构建或测试阶段执行的代码。

## 安全边界

- 文件工具只能访问当前工作区，拒绝 `.git`、`bin`、`obj`、`.env`、凭据、私钥、敏感历史、符号链接与 junction 逃逸。
- 写文件、启动或停止进程、保存记忆、访问外部网络都需要明确审批。
- `run_command` 与后台命令只允许可信绝对路径的受限 `dotnet`、`rg` 和只读 `git` 配方。
- `dotnet build/test/run` 可能执行工作区代码，批准前必须确认代码来源。
- `fetch_web_content` 只读取公开 HTTPS 静态快照，提取有界标题层级与安全链接；拒绝私网、凭据 URL、Cookie、跳转、JavaScript 与超限正文，不执行链接或表单。
- 工具输出、工作区文件、网页、历史与记忆都按不可信数据回灌，不能替代用户授权。
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

Inno Setup 只属于维护者的构建工具，普通安装用户不需要安装。推送 `v*` 标签后，GitHub Actions 会自动构建安装器并上传 Release。

## 项目结构

- `src/TestAgent.Core`：Agent 循环、模型、任务图与公共接口。
- `src/TestAgent.Infrastructure`：Provider、JSON 存储、安全工具与进程服务。
- `src/TestAgent.Desktop`：WPF/MVVM 与依赖注入。
- `tests/TestAgent.Tests`：Core 和 Infrastructure 自动化测试。
- `installer`：Inno Setup 安装器定义。

## 尚未开放

- 多 Agent / 子 Agent 调度
- 交互式 Shell / PTY
- VS Code 实时桥接、Problems 诊断与扩展安装/更新/删除
- 可点击、可填写表单的浏览器自动化
- 浏览器视口截图、屏幕理解与 GUI 自动化

这些能力需要独立身份、权限和可见操作边界，不能通过通用 Shell 绕过审批。

## 参与贡献与安全问题

- 贡献说明：[CONTRIBUTING.md](CONTRIBUTING.md)
- 行为准则：[CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md)
- 安全报告：[SECURITY.md](SECURITY.md)
- 版本记录：[CHANGELOG.md](CHANGELOG.md)
- GitHub 开源清单：[OPEN_SOURCE_CHECKLIST.md](OPEN_SOURCE_CHECKLIST.md)

## 许可证

MIT — 详见 [LICENSE](LICENSE)。
