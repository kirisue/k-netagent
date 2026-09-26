# WinUI 本地预览迭代验收（2026-09-26）

这是一小时迭代窗口内的本地交付记录，不是 GitHub 发布公告，也不是稳定版认证。
项目保持单 Agent；没有提交、推送、创建标签或发布 V0.2。

## 本轮交付

- 事件中心支持本地故障线索分组：重复事件、应用异常与 SCM 候选关联；分别展示事实、假设和只读检查建议，不把时间接近当作同一根因。
- 分组证据可选择最新 20 条进入已有脱敏预览流程。事件身份统一为频道、Record ID、时间戳，避免 ID 重用时错误继承勾选或丢失证据。
- Windows 服务状态窗口读取本机状态、启动类型和依赖；新增逐次审批的 `query_windows_services` 工具。没有启动、停止、修改服务或提权能力。
- 服务原生数据解码验证缓冲区范围、UTF-16 对齐、终止符与编码；不读取服务账号、可执行路径或命令行。
- WinUI Preview 双击安装器、独立产品标识、版本显示，以及事件和服务的开始菜单快捷方式。
- 单独的 `run-winui.ps1` / `run-winui.cmd` 源码启动入口，支持 `-Page events/services`；原 WPF 启动脚本保持不变。源码启动需要 .NET SDK，与安装版依赖边界不同。

## 最终安装包

```text
artifacts/installer/winui-preview/k-netagent-0.1.1-preview.20260926-winui-win-x64-setup.exe
SHA256: 5ede2de361b68ea093084f285df8cafb17329e3b96a48b19016a88fa66604cff
Size: 47,349,476 bytes
```

这是安装器 EXE，不是免安装单文件应用。安装后展开 self-contained 多文件程序，
内置 .NET 10.0.12 与 Windows App SDK。签名状态为未签名；浏览器功能另需系统
Evergreen WebView2 Runtime，包内只有 Loader。Git 和 .NET SDK 也不随安装包提供。

最终构建命令未跳过测试：

```powershell
.\build-installer.ps1 -Desktop WinUI -Version 0.1.1-preview.20260926 -RuntimeFrameworkVersion 10.0.12 -KeepStaging
```

## 已验证证据

| 范围 | 结果与证据 |
|---|---|
| 自动化回归 | 最终打包流程实际执行 453 项，全部通过，0 失败、0 跳过；原始 TRX 在本次 staging 的 `tests/`，相同哈希副本为 `artifacts/validation/hour-20260926/hour-package-final.trx` |
| 双入口构建 | 冻结源码完成完整 `TestAgent.slnx` Release 构建，WPF 与 WinUI 均成功，0 警告、0 错误 |
| 诊断重复回归 | 56 个故障选择、服务状态及读取器测试连续运行 10 轮，560 次执行全部通过；这是同一组测试的重复运行，不是新增 560 个测试；TRX 在 `artifacts/validation/hour-20260926/stress/` |
| Windows 原生读取 | 冻结源码编译后，显式开启只读 smoke 的 2 项测试通过；`artifacts/validation/hour-20260926/hour-native-final.trx` |
| 打包与依赖 | WinUI 编译和安装器编译成功；20 个必需文件、13 个 XBF/PRI 资源与 .NET 原生运行时源包哈希检查通过 |
| 本机安装与覆盖安装 | 安装及同版本覆盖安装退出码均为 0；最终安装的 486 个发布文件与本次 publish 逐一核对 SHA-256，0 个不一致；主程序、事件、服务三个快捷方式的目标和参数正确 |
| 最终安装包启动 | 最终 EXE 安装后，事件中心和服务窗口分别成功创建并返回真实可访问性控件；版本为 `0.1.1-preview.20260926`，打开时均未自动查询 |
| 本机卸载 | 退出码 0；测试安装的 EXE、Preview 卸载登记和三个快捷方式已清除；卸载前后现有 63 个用户数据文件的路径和 SHA-256 完全一致，WPF 卸载登记存在状态未变 |
| 界面范围 | 上述窗口与控件证据不等于完整视觉和交互验收，自动点击限制见下文 |
| 静态检查 | `git diff --check` 和打包 PowerShell 语法检查通过 |

测试覆盖包含上下文、Provider、存储、工具、审批、MCP、故障分组、服务状态和
链接到测试项目的 ViewModel 行为；并不等于所有第三方 Provider 和所有 GUI 操作
都已在真实环境逐一验证。原生 smoke 最多读取一条事件和一条服务元数据，不输出正文。

安装生命周期日志保留在 `artifacts/validation/installer-20260926/`，包括
`install.log`、`reinstall.log`、`final-install.log` 和 `final-uninstall.log`。
测试只安装到该目录下的 `app/`，没有替换正式 WPF 产品；卸载检查结束后不保留
测试安装，但保留上面的最终 Setup.exe、校验和、构建清单和测试证据。
不含用户文件名、内容或本机绝对路径的检查摘要保存在
`artifacts/validation/hour-20260926/final-delivery-summary.json`；它记录本次工具实测结果，
不是可替代原始日志、TRX 或重新执行验收的独立验证器。

## 明确没有完成的验收

- 干净 Windows 10/11、缺少 WebView2 的恢复提示、不同 DPI、键盘与读屏的完整 GUI 验收。
- 真实 Provider 的安装后多轮对话、停止、重启恢复端到端验收。
- 从旧版本升级、共享用户数据的兼容性与两个桌面入口同时运行的行为。
- 正式签名和发布流水线切换。当前 `v*` 标签工作流仍默认打 WPF，不可直接用于发布此 WinUI 包。

本机自动界面操作遇到 `coordinate input geometry is unavailable` / 无缓存坐标；
此前截图遇到 `FrameArrived timed out`。按 computer-use 验证流程重新观察后，
可以读取控件，但未取得完整点击与视觉验收证据；没有用进程存活或测试通过替代这项结论。

Preview 与 WPF 的安装目录和卸载标识分开，但仍共享 `%LOCALAPPDATA%\TestAgent`。
尚不宣称完全隔离或稳定版替换。WinUI 自迭代执行继续禁用，直到真正的低权限、
无网络验证器完成；临时源码副本不是安全沙箱。

最终安装器生成后，仅补充文档、验收摘要和源码启动脚本；没有再修改安装包内的
应用编译输入。源码启动脚本不属于 Setup.exe 内的运行文件。

## 后续验收入口

双击安装后，从开始菜单打开 `K.netagent WinUI Preview`，或使用
`Windows Events` / `Windows Services` 快捷方式。诊断页面必须由用户点击查询；
把证据加入聊天只准备草稿，不会自动发送。完整清单见
[WinUI 验收门禁](winui3-ui-acceptance.md)。
