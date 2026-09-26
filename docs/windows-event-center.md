# Windows 事件中心

状态：WinUI 开发预览，未发布。当前实现本机 Application / System 历史查询、窗口内实时监听、位置书签和手工选中证据交给 Agent。

## 操作

从主窗口左侧或 `＋` 菜单打开“Windows 事件中心”。打开窗口不会主动读取日志，也不会发送模型请求。

- 查询默认最近 60 分钟、警告及以上、最多 100 条；上限是 7 天和 200 条。
- 来源与事件 ID 为可选的精确匹配条件，不能填写 XPath、文件路径或远程电脑。
- 点击一条记录查看脱敏详情；只有勾选复选框才会选择该记录作为证据。
- 点击“预览所选证据”，检查实际将加入草稿的文本。每批最多 20 条、16,000 字符，超长内容会明确标注截断。
- 点击“加入聊天草稿”后返回主聊天。它不会自动发送；现有草稿和图片附件也不会被覆盖。
- 最后由用户点击聊天的发送按钮，将草稿交给当前模型 Provider。送出的脱敏文字会像普通对话一样进入会话历史。

## 监听与恢复

点击“开始监听新事件”后只接收未来匹配事件。关闭事件中心窗口或点击“停止监控”会停止监听；应用启动不会自动恢复监听。

窗口最多缓存 200 条，超出时淘汰较旧事件并显示丢弃数量。停止时将位置书签保存到 `%LOCALAPPDATA%\TestAgent\windows-events\`，书签不含事件正文。书签按 WorkspaceId、频道、时间范围和筛选条件隔离。

“从上次位置继续”仅用于相同条件，并仍受当前时间范围限制。日志轮转、删除或损坏的书签可能导致无法恢复，界面会提示重新开始；该机制不是持久消息队列，也不保证不丢不重。进程崩溃可能导致重读上次保存位置之后的事件。

## Agent 工具

`list_windows_event_channels` 返回固定的允许频道清单，不触碰系统日志。

`query_windows_events` 每次通过现有 LocalEnvironmentRead 审批。只返回 Channel、RecordId、EventId、Provider、Level、Timestamp 和分页位置，不返回 Message、Raw XML、机器名、用户名或 SID。元数据本身仍可能反映软件和运行状态，因此审批会说明结果交给当前 Provider。工具调用进入现有审计与 ToolSession。

## 本地故障线索

查询后展开“故障线索 · 本地分组”，点击整理当前事件。最多处理 200 条，使用锚定的 5 分钟窗口，避免相邻事件无限串联；频道、RecordId 和 UTC 时间共同标识证据，每条唯一记录只归属一组，缺时间的记录单独保留。

Provider 与 Event ID 一起匹配常见的 Application Error / WER / .NET Runtime 以及 Service Control Manager 模式。分组只说明重复或时间邻近，不证明同一进程、同一服务或根因。规则不会解析并执行事件 Message，不调用模型、不修复系统。依据参考微软[应用与服务崩溃排障](https://learn.microsoft.com/en-us/troubleshoot/windows-server/performance/troubleshoot-application-service-crashing-behavior)；具体分组仍是启发式，不能替代对原始证据和身份的核对。

选择一组后可一键勾选最新 20 条对应记录，仍需预览后才能加入聊天草稿。重新查询或实时列表刷新会使分组与证据预览失效，需要重新整理。

## 数据与权限

原生日志接口只读取本机 Application/System。当前没有事件 Provider 注册、清日志、写入事件、远程连接、Security 访问、提权或系统修复代码。

事件正文在本机经过常见凭据、路径、邮箱、IP、SID 和身份字段脱敏；自动规则不能识别所有业务秘密，发送前仍需检查预览。工具输出、原生日志和事件描述均按不可信数据处理。事件级别或时间相近不能证明根因。

界面列表、详情、线索分组和实时缓冲仅驻内存。自定义 `K.netagent/Admin`、`Operational`、`Debug` Windows 频道、本地通知、计划任务/驱动关联以及自动根因诊断留待后续阶段。当前另有 Windows 服务状态窗口，提供本机只读元数据查询与脱敏草稿交接。

## 实现与验证

底层采用微软的 [EventLogReader](https://learn.microsoft.com/en-us/dotnet/api/system.diagnostics.eventing.reader.eventlogreader?view=windowsdesktop-10.0) 与 [EventLogWatcher](https://learn.microsoft.com/en-us/dotnet/api/system.diagnostics.eventing.reader.eventlogwatcher?view=windowsdesktop-10.0)。读取有取消与 10 秒预算；原生句柄及时释放，监控缓冲和证据长度受限。

自动测试使用可注入的原生日志适配器，覆盖结构过滤、权限/取消、队列限额、停止、书签隔离、损坏拒绝、selected-only 证据、脱敏与实际 WinUI ViewModel 行为。可选本机 smoke 测试只读取最多一条 Application 记录，不创建日志，也不输出正文。
