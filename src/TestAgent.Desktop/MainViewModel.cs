using System.Collections.ObjectModel; using System.ComponentModel; using System.IO; using System.Runtime.CompilerServices; using System.Security.Cryptography; using System.Text.Json; using System.Windows; using System.Windows.Input; using System.Windows.Media; using System.Windows.Media.Imaging; using TestAgent.Core; using TestAgent.Infrastructure;
namespace TestAgent.Desktop;
public sealed class MainViewModel : NotifyBase, IAgentObserver, IDisposable
{
    private readonly IAgentRuntime _agent; private readonly ISessionStore _sessions; private readonly IMemoryStore _memories; private readonly ISettingsStore _settings; private readonly ISecureSecretStore _secrets; private readonly IIterationGuideStore _iterationGuideStore; private readonly ICodeIterationService _iterationService; private readonly ITaskWorkflowService _taskWorkflow; private readonly IToolSessionCoordinator _toolSessions; private readonly IToolRegistry _toolRegistry; private readonly IToolExecutionService _toolExecution; private readonly IBackgroundCommandService _backgroundCommands; private readonly IImageInputService _imageInputs; private readonly IImageSendConfirmationService _imageConfirmation; private readonly IReadOnlyBrowserSession _browser; private CancellationTokenSource? _runCts; private AppSettings _appSettings = null!; private ImageInput? _pendingImage; private bool _disposed;
    public ObservableCollection<ChatSession> Sessions { get; }=[]; public ObservableCollection<ChatMessage> Messages { get; }=[]; public ObservableCollection<MemoryEntry> Memories { get; }=[];
    public ObservableCollection<IterationGuide> IterationGuides { get; }=[];
    public ObservableCollection<TaskWorkflowItem> TaskWorkflows { get; }=[]; public ObservableCollection<TaskNodeDisplay> TaskNodes { get; }=[];
    public ObservableCollection<ToolSessionDisplay> ToolSessions{get;}=[];
    public ObservableCollection<BackgroundCommandJob> BackgroundCommands{get;}=[];
    public string[] ProviderIds { get; }=["deepseek","openai","openrouter","ollama","custom"];
    private ChatSession? _selectedSession; public ChatSession? SelectedSession { get=>_selectedSession; set { if(Set(ref _selectedSession,value)){SyncMessages();_ = RefreshToolSessionsAsync();CommandsChanged();} } }
    private MemoryEntry? _selectedMemory; public MemoryEntry? SelectedMemory { get=>_selectedMemory; set { Set(ref _selectedMemory,value); if(value is not null){MemoryName=value.Name;MemoryContent=value.Content;MemoryEnabled=value.Enabled;MemoryScope=value.Scope;MemoryScopeId=value.ScopeId??"";} } }
    private string _input=""; public string Input { get=>_input;set {if(Set(ref _input,value))CommandsChanged();} } private string _reasoning=""; public string Reasoning { get=>_reasoning;set {Set(ref _reasoning,value); OnPropertyChanged(nameof(HasReasoning));} } public bool HasReasoning=>!string.IsNullOrWhiteSpace(Reasoning);
    private string _attachedImageSummary=""; private ImageSource? _attachedImagePreview; private bool _imageLoading; public string AttachedImageSummary{get=>_attachedImageSummary;private set=>Set(ref _attachedImageSummary,value);} public ImageSource? AttachedImagePreview{get=>_attachedImagePreview;private set=>Set(ref _attachedImagePreview,value);} public bool HasAttachedImage=>_pendingImage is not null; public bool CanAttachImage=>!Busy&&!_imageLoading&&!_disposed;
    private string _streamingContent=""; public string StreamingContent { get=>_streamingContent; set { Set(ref _streamingContent,value); OnPropertyChanged(nameof(HasStreamingContent)); } } public bool HasStreamingContent=>!string.IsNullOrWhiteSpace(StreamingContent);
    private string _status="就绪"; public string Status {get=>_status;set=>Set(ref _status,value);} private string _toolSessionStatus="工具会话尚未初始化"; public string ToolSessionStatus{get=>_toolSessionStatus;set=>Set(ref _toolSessionStatus,value);} private bool _busy; public bool Busy {get=>_busy;set {if(Set(ref _busy,value)){OnPropertyChanged(nameof(CanAttachImage));CommandsChanged();}}}
    private BackgroundCommandJob? _selectedBackgroundCommand; private string _backgroundOutput="",_backgroundStatus="后台命令尚未刷新"; private long _backgroundCursor; public BackgroundCommandJob? SelectedBackgroundCommand{get=>_selectedBackgroundCommand;set{var changedId=!string.Equals(_selectedBackgroundCommand?.Id,value?.Id,StringComparison.OrdinalIgnoreCase);if(Set(ref _selectedBackgroundCommand,value)){if(changedId){BackgroundOutput="";_backgroundCursor=0;}CommandsChanged();}}} public string BackgroundOutput{get=>_backgroundOutput;set=>Set(ref _backgroundOutput,value);} public string BackgroundStatus{get=>_backgroundStatus;set=>Set(ref _backgroundStatus,value);}
    private string _browserAddress="https://example.com/",_browserStatus="只读浏览器尚未打开页面",_browserDomPreview=""; private int _centerTabIndex;
    public string BrowserAddress{get=>_browserAddress;set{if(Set(ref _browserAddress,value))CommandsChanged();}} public string BrowserStatus{get=>_browserStatus;private set=>Set(ref _browserStatus,value);} public string BrowserDomPreview{get=>_browserDomPreview;private set{Set(ref _browserDomPreview,value);OnPropertyChanged(nameof(HasBrowserDomPreview));}} public bool HasBrowserDomPreview=>!string.IsNullOrWhiteSpace(BrowserDomPreview); public bool BrowserHasPage=>_browser.Current is not null; public bool BrowserHasCapture=>_browser.HasLatestCapture; public string BrowserPageSummary=>_browser.Current is { } page?$"{page.Title}\n{page.Url}":"公开 HTTPS 页面会先被净化为本地只读快照；不会加载远程 HTML、Cookie 或脚本。"; public int CenterTabIndex{get=>_centerTabIndex;set=>Set(ref _centerTabIndex,value);}
    private string _providerId="deepseek",_endpoint="",_model=""; private int _maxTokens=4096,_timeoutSeconds=120; private bool _selfReviewEnabled=true,_supportsImageInput; public string ProviderId {get=>_providerId;set {if(Set(ref _providerId,value)){SupportsImageInput=false;ApplyPreset();}}} public string Endpoint {get=>_endpoint;set{if(Set(ref _endpoint,value))SupportsImageInput=false;}} public string Model {get=>_model;set{if(Set(ref _model,value))SupportsImageInput=false;}} public int MaxTokens {get=>_maxTokens;set=>Set(ref _maxTokens,value);} public int TimeoutSeconds {get=>_timeoutSeconds;set=>Set(ref _timeoutSeconds,value);} public bool SelfReviewEnabled {get=>_selfReviewEnabled;set=>Set(ref _selfReviewEnabled,value);} public bool SupportsImageInput{get=>_supportsImageInput;set=>Set(ref _supportsImageInput,value);}
    private string _memoryName="",_memoryContent=""; private bool _memoryEnabled=true; private MemoryScope _memoryScope=TestAgent.Core.MemoryScope.User; private string _memoryScopeId=""; public MemoryScope[] MemoryScopes{get;}=Enum.GetValues<MemoryScope>(); public string MemoryName{get=>_memoryName;set=>Set(ref _memoryName,value);} public string MemoryContent{get=>_memoryContent;set=>Set(ref _memoryContent,value);} public bool MemoryEnabled{get=>_memoryEnabled;set=>Set(ref _memoryEnabled,value);} public MemoryScope MemoryScope{get=>_memoryScope;set=>Set(ref _memoryScope,value);} public string MemoryScopeId{get=>_memoryScopeId;set=>Set(ref _memoryScopeId,value);}
    private IterationGuide? _selectedIterationGuide; private IterationProposal? _iterationProposal; private string _iterationSummary="",_iterationFiles="",_iterationValidation="",_iterationPreview=""; private bool _iterationApproved;
    private TaskWorkflowItem? _selectedTaskWorkflow; private string _taskGoal="",_taskSummary=""; private bool _taskPlanApproved;
    public TaskWorkflowItem? SelectedTaskWorkflow{get=>_selectedTaskWorkflow;set{if(Set(ref _selectedTaskWorkflow,value)){TaskPlanApproved=false;SyncTaskNodes();CommandsChanged();}}} public string TaskGoal{get=>_taskGoal;set{Set(ref _taskGoal,value);CommandsChanged();}} public string TaskSummary{get=>_taskSummary;set=>Set(ref _taskSummary,value);} public bool TaskPlanApproved{get=>_taskPlanApproved;set{Set(ref _taskPlanApproved,value);CommandsChanged();}}
    public IterationGuide? SelectedIterationGuide {get=>_selectedIterationGuide;set {Set(ref _selectedIterationGuide,value);IterationApproved=false;CommandsChanged();}} public string IterationSummary {get=>_iterationSummary;set=>Set(ref _iterationSummary,value);} public string IterationFiles {get=>_iterationFiles;set=>Set(ref _iterationFiles,value);} public string IterationValidation {get=>_iterationValidation;set=>Set(ref _iterationValidation,value);} public string IterationPreview {get=>_iterationPreview;set=>Set(ref _iterationPreview,value);} public bool IterationApproved {get=>_iterationApproved;set {Set(ref _iterationApproved,value);CommandsChanged();}}
    public ICommand SendCommand {get;} public ICommand StopCommand {get;} public ICommand NewSessionCommand {get;} public ICommand ClearSessionCommand {get;} public ICommand ClearImageCommand{get;} public ICommand OpenBrowserSnapshotCommand{get;} public ICommand ReadBrowserDomCommand{get;} public ICommand CaptureBrowserViewportCommand{get;} public ICommand AttachBrowserCaptureCommand{get;} public ICommand SaveMemoryCommand {get;} public ICommand DeleteMemoryCommand {get;} public ICommand GenerateIterationCommand {get;} public ICommand ApplyIterationCommand {get;} public ICommand CreateTaskPlanCommand{get;} public ICommand RunTaskPlanCommand{get;} public ICommand RefreshTaskPlansCommand{get;} public ICommand ReviewAndRetryTaskCommand{get;} public ICommand RefreshToolSessionsCommand{get;} public ICommand AcknowledgeToolSessionsCommand{get;} public ICommand RefreshBackgroundCommandsCommand{get;} public ICommand ReadBackgroundOutputCommand{get;} public ICommand StopBackgroundCommandCommand{get;}
    public MainViewModel(IAgentRuntime agent,ISessionStore sessions,IMemoryStore memories,ISettingsStore settings,ISecureSecretStore secrets,IIterationGuideStore iterationGuideStore,ICodeIterationService iterationService,ITaskWorkflowService taskWorkflow,IToolSessionCoordinator toolSessions,IToolRegistry toolRegistry,IBackgroundCommandService backgroundCommands,IImageInputService imageInputs,IImageSendConfirmationService imageConfirmation,IReadOnlyBrowserSession browser,IToolExecutionService toolExecution)
    {
        _agent=agent;_sessions=sessions;_memories=memories;_settings=settings;_secrets=secrets;_iterationGuideStore=iterationGuideStore;_iterationService=iterationService;_taskWorkflow=taskWorkflow;_toolSessions=toolSessions;_toolRegistry=toolRegistry;_backgroundCommands=backgroundCommands;_imageInputs=imageInputs;_imageConfirmation=imageConfirmation;_browser=browser;_toolExecution=toolExecution;
        _browser.Changed+=OnBrowserChanged;
        SendCommand=new AsyncCommand(SendAsync,()=>!_disposed&&!Busy&&!_imageLoading&&SelectedSession is not null&&(!string.IsNullOrWhiteSpace(Input)||HasAttachedImage));StopCommand=new RelayCommand(()=>_runCts?.Cancel(),()=>!_disposed&&Busy);NewSessionCommand=new AsyncCommand(NewSessionAsync,()=>!_disposed&&!Busy);ClearSessionCommand=new AsyncCommand(ClearSessionAsync,()=>!_disposed&&!Busy&&SelectedSession is not null);ClearImageCommand=new RelayCommand(ClearAttachedImage,()=>!_disposed&&!Busy&&!_imageLoading&&HasAttachedImage);
        OpenBrowserSnapshotCommand=new AsyncCommand(OpenBrowserSnapshotAsync,()=>!_disposed&&!Busy&&!_imageLoading&&SelectedSession is not null&&!string.IsNullOrWhiteSpace(BrowserAddress));ReadBrowserDomCommand=new AsyncCommand(ReadBrowserDomAsync,()=>!_disposed&&!Busy&&!_imageLoading&&SelectedSession is not null&&BrowserHasPage);CaptureBrowserViewportCommand=new AsyncCommand(CaptureBrowserViewportAsync,()=>!_disposed&&!Busy&&!_imageLoading&&SelectedSession is not null&&BrowserHasPage);AttachBrowserCaptureCommand=new RelayCommand(AttachBrowserCapture,()=>!_disposed&&!Busy&&!_imageLoading&&BrowserHasCapture);
        SaveMemoryCommand=new AsyncCommand(SaveMemoryAsync);DeleteMemoryCommand=new AsyncCommand(DeleteMemoryAsync,()=>SelectedMemory is not null);GenerateIterationCommand=new AsyncCommand(GenerateIterationAsync,()=>!Busy&&SelectedIterationGuide is not null);ApplyIterationCommand=new AsyncCommand(ApplyIterationAsync,()=>!Busy&&IterationApproved&&_iterationProposal?.Validation.Success==true);CreateTaskPlanCommand=new AsyncCommand(CreateTaskPlanAsync,()=>!Busy&&!string.IsNullOrWhiteSpace(TaskGoal));RunTaskPlanCommand=new AsyncCommand(RunTaskPlanAsync,()=>!Busy&&TaskPlanApproved&&SelectedTaskWorkflow is not null&&SelectedTaskWorkflow.Status is not (TaskGraphStatus.Completed or TaskGraphStatus.NeedsReview));RefreshTaskPlansCommand=new AsyncCommand(()=>RefreshTaskPlansAsync(),()=>!Busy);ReviewAndRetryTaskCommand=new AsyncCommand(ReviewAndRetryTaskAsync,()=>!Busy&&TaskPlanApproved&&SelectedTaskWorkflow?.Status==TaskGraphStatus.NeedsReview);RefreshToolSessionsCommand=new AsyncCommand(RefreshToolSessionsAsync,()=>!Busy);AcknowledgeToolSessionsCommand=new AsyncCommand(AcknowledgeToolSessionsAsync,()=>!Busy&&ToolSessions.Any(x=>x.State==ToolSessionState.NeedsReview));RefreshBackgroundCommandsCommand=new AsyncCommand(RefreshBackgroundCommandsAsync);ReadBackgroundOutputCommand=new AsyncCommand(ReadBackgroundOutputAsync,()=>SelectedBackgroundCommand is not null);StopBackgroundCommandCommand=new AsyncCommand(StopBackgroundCommandAsync,()=>SelectedBackgroundCommand?.State is BackgroundCommandState.Running or BackgroundCommandState.Starting or BackgroundCommandState.Stopping or BackgroundCommandState.NeedsReview);
    }
    public async Task InitializeAsync(){_appSettings=await _settings.LoadAsync();ProviderId=_appSettings.Provider.ProviderId;Endpoint=_appSettings.Provider.Endpoint;Model=_appSettings.Provider.Model;MaxTokens=_appSettings.Provider.MaxOutputTokens;TimeoutSeconds=_appSettings.Provider.TimeoutSeconds;SelfReviewEnabled=_appSettings.Provider.SelfReviewEnabled;SupportsImageInput=_appSettings.Provider.SupportsImageInput;await RefreshSessions();await RefreshMemories();await RefreshIterationGuides();await RefreshTaskPlansAsync();if(SelectedSession is null)await NewSessionAsync();await RefreshToolSessionsAsync();await RefreshBackgroundCommandsAsync();}
    public async Task SaveSettingsAsync(string key){_appSettings=new(new ProviderSettings(ProviderId,Endpoint,Model,MaxTokens,TimeoutSeconds,30,SelfReviewEnabled,SupportsImageInput:SupportsImageInput),_appSettings.SystemPrompt);await _settings.SaveAsync(_appSettings);if(!string.IsNullOrWhiteSpace(key))await _secrets.SetAsync(ProviderId,key);Status="设置已安全保存";}
    private async Task SendAsync()
    {
        var session=SelectedSession;
        if(session is null||_imageLoading)return;
        var providerSettings=new ProviderSettings(ProviderId,Endpoint,Model,MaxTokens,TimeoutSeconds,30,SelfReviewEnabled,SupportsImageInput:SupportsImageInput);
        var image=_pendingImage;
        if(image is not null)
        {
            if(!providerSettings.SupportsImageInput){Status="当前模型未启用图片输入；确认模型支持后请先在设置中勾选。";return;}
            if(!Uri.TryCreate(providerSettings.Endpoint,UriKind.Absolute,out var endpoint)||endpoint.UserInfo.Length>0||endpoint.Query.Length>0||endpoint.Fragment.Length>0||
               !(endpoint.Scheme.Equals(Uri.UriSchemeHttps,StringComparison.OrdinalIgnoreCase)||
                 endpoint.Scheme.Equals(Uri.UriSchemeHttp,StringComparison.OrdinalIgnoreCase)&&endpoint.IsLoopback))
            {Status="图片只能发送到不含查询参数的 HTTPS 模型端点或本机回环 HTTP 端点。";return;}
            var target=endpoint.GetLeftPart(UriPartial.Path).TrimEnd('/')+"/chat/completions";
            if(!_imageConfirmation.Confirm(new(target,providerSettings.Model,image.Width,image.Height,image.Data.Length,4)))return;
        }
        var text=string.IsNullOrWhiteSpace(Input)?"请分析这张图片并说明你看到的重要信息。":Input.Trim();
        if(image is not null)text+="\n\n[本轮附带 1 张图片；图片像素不会保存到本地会话。]";
        Input="";Reasoning="";StreamingContent="";Busy=true;if(image is not null)DetachAttachedImage(image);Status=image is null?"生成中…":"正在发送已确认的图片并生成…";_runCts=new();
        try
        {
            var key=await _secrets.GetAsync(providerSettings.ProviderId,_runCts.Token);
            var result=await _agent.RunAsync(session,text,providerSettings,key,this,_runCts.Token,
                new AgentRunOptions(SystemPrompt:_appSettings.SystemPrompt,Images:image is null?null:[image]));
            SelectedSession=result.Session;SyncMessages();StreamingContent="";
            Status=result.State==AgentState.Failed?$"错误：{result.Error}":result.State==AgentState.Cancelled?"已停止":$"完成 · Tokens {result.Tokens?.ToString()??"未知"}";
            await RefreshSessions(result.Session.Id);
        }
        catch(OperationCanceledException){Status="已停止";}
        catch(Exception ex){Status="错误："+ex.Message;}
        finally{if(image is not null)CryptographicOperations.ZeroMemory(image.Data);Busy=false;CommandsChanged();}
    }
    private Task OpenBrowserSnapshotAsync()=>ExecuteBrowserToolAsync("open_browser_snapshot",JsonSerializer.Serialize(new{url=BrowserAddress.Trim(),maxChars=30_000}),true,"正在读取并生成本地安全快照…");
    private Task ReadBrowserDomAsync()=>ExecuteBrowserToolAsync("read_browser_dom","{\"maxChars\":30000}",true,"正在读取当前安全 DOM 快照…");
    private Task CaptureBrowserViewportAsync()=>ExecuteBrowserToolAsync("capture_browser_viewport","{}",false,"等待批准并截取可见视口…");
    private async Task ExecuteBrowserToolAsync(string toolName,string argumentsJson,bool showOutput,string progress)
    {
        var session=SelectedSession;
        if(session is null||Busy||_disposed)return;
        CenterTabIndex=1;BrowserStatus=progress;Status=progress;Busy=true;var operationCts=new CancellationTokenSource();_runCts=operationCts;
        try
        {
            var request=new ToolRequest($"UI-BROWSER-{Guid.NewGuid():N}",toolName,argumentsJson,session.Id);
            var result=await _toolExecution.ExecuteAsync(request,this,operationCts.Token);
            if(showOutput&&result.Status==ToolExecutionStatus.Success)BrowserDomPreview=result.Output;
            BrowserStatus=result.Status==ToolExecutionStatus.Success
                ?result.Summary??$"工具 {toolName} 已完成。"
                :$"{result.Status}：{result.Error??result.Output}";
            Status="安全浏览器："+BrowserStatus;
        }
        catch(OperationCanceledException){BrowserStatus="安全浏览器操作已停止。";Status=BrowserStatus;}
        catch(Exception ex){BrowserStatus="安全浏览器错误："+ex.Message;Status=BrowserStatus;}
        finally
        {
            if(ReferenceEquals(_runCts,operationCts))_runCts=null;operationCts.Dispose();Busy=false;CommandsChanged();await RefreshToolSessionsAsync();
        }
    }
    private void AttachBrowserCapture()
    {
        if(Busy||_imageLoading||_disposed)return;
        ImageInput? capture=_browser.TakeLatestCapture();
        if(capture is null){BrowserStatus="没有可转移的视口截图；请先执行“截取视口”并批准。";return;}
        try
        {
            var preview=CreateImagePreview(capture);
            var previous=_pendingImage;
            _pendingImage=capture;capture=null;
            if(previous is not null)CryptographicOperations.ZeroMemory(previous.Data);
            AttachedImageSummary=$"安全浏览器视口 · {_pendingImage.Width}×{_pendingImage.Height} · {_pendingImage.Data.Length/1024d:F1} KB · 仅内存";
            AttachedImagePreview=preview;OnPropertyChanged(nameof(HasAttachedImage));CenterTabIndex=0;
            BrowserStatus="视口截图已一次性转移到下一轮聊天；发送、替换、移除或退出时会清零像素。";
            Status="浏览器截图已附到下一轮";CommandsChanged();
        }
        finally
        {
            if(capture is not null)CryptographicOperations.ZeroMemory(capture.Data);
        }
    }
    public async Task AttachImageAsync(string filePath)
    {
        if(!CanAttachImage)return;
        _imageLoading=true;OnPropertyChanged(nameof(CanAttachImage));CommandsChanged();Status="正在安全处理图片…";
        ImageInput? loaded=null;
        try
        {
            loaded=await _imageInputs.LoadAsync(filePath);
            var preview=CreateImagePreview(loaded);
            if(Busy||_disposed){Status="当前操作已开始或应用正在退出，刚选择的图片未附加。";return;}
            var previous=_pendingImage;
            _pendingImage=loaded;loaded=null;
            if(previous is not null)CryptographicOperations.ZeroMemory(previous.Data);
            AttachedImageSummary=$"图片 · {_pendingImage.Width}×{_pendingImage.Height} · {_pendingImage.Data.Length/1024d:F1} KB · 已移除元数据";
            AttachedImagePreview=preview;OnPropertyChanged(nameof(HasAttachedImage));Status="图片已在内存中准备；发送前仍会再次确认目标 Provider。";
        }
        finally
        {
            if(loaded is not null)CryptographicOperations.ZeroMemory(loaded.Data);
            _imageLoading=false;OnPropertyChanged(nameof(CanAttachImage));CommandsChanged();
        }
    }
    private static BitmapImage CreateImagePreview(ImageInput image)
    {
        using var stream=new MemoryStream(image.Data,writable:false);
        var preview=new BitmapImage();preview.BeginInit();preview.CacheOption=BitmapCacheOption.OnLoad;preview.StreamSource=stream;preview.EndInit();preview.Freeze();return preview;
    }
    private void DetachAttachedImage(ImageInput expected)
    {
        if(!ReferenceEquals(_pendingImage,expected))throw new InvalidOperationException("The attached image changed before sending.");
        _pendingImage=null;AttachedImageSummary="";AttachedImagePreview=null;OnPropertyChanged(nameof(HasAttachedImage));CommandsChanged();
    }
    private void ClearAttachedImage()
    {
        if(_pendingImage is not null)CryptographicOperations.ZeroMemory(_pendingImage.Data);
        _pendingImage=null;AttachedImageSummary="";AttachedImagePreview=null;OnPropertyChanged(nameof(HasAttachedImage));CommandsChanged();
    }
    private async Task NewSessionAsync(){var s=AgentRuntime.NewSession();await _sessions.SaveAsync(s);Sessions.Insert(0,s);SelectedSession=s;}
    private async Task ClearSessionAsync(){if(SelectedSession is null)return;var cleared=SelectedSession with{Title="New chat",Messages=[],UpdatedAt=DateTimeOffset.UtcNow};await _sessions.SaveAsync(cleared);SelectedSession=cleared;SyncMessages();await RefreshSessions(cleared.Id);Status="当前会话已清空";}
    private async Task SaveMemoryAsync(){if(string.IsNullOrWhiteSpace(MemoryName)||string.IsNullOrWhiteSpace(MemoryContent))return;if(MemoryName.Length>120||MemoryContent.Length>12_000){Status="记忆名称最多 120 字，内容最多 12,000 字";return;}if(SensitiveDataRedactor.ContainsLikelySecret(MemoryContent)){Status="检测到疑似密钥、Token、密码或私钥，记忆未保存";return;}if(MemoryScope==TestAgent.Core.MemoryScope.Project&&string.IsNullOrWhiteSpace(MemoryScopeId)){Status="项目记忆需要相对路径范围";return;}var scopeId=MemoryScope switch{TestAgent.Core.MemoryScope.User=>null,TestAgent.Core.MemoryScope.Session=>string.IsNullOrWhiteSpace(MemoryScopeId)?SelectedSession?.Id:MemoryScopeId.Trim(),_=>MemoryScopeId.Trim()};var item=new MemoryEntry(SelectedMemory?.Id??$"MEM-{Guid.NewGuid():N}",MemoryName.Trim(),MemoryContent.Trim(),MemoryEnabled,DateTimeOffset.UtcNow,MemoryScope,scopeId);await _memories.SaveAsync(item);await RefreshMemories();SelectedMemory=item;Status="记忆已保存";}
    private async Task DeleteMemoryAsync(){if(SelectedMemory is null)return;await _memories.DeleteAsync(SelectedMemory.Id);SelectedMemory=null;MemoryName=MemoryContent="";await RefreshMemories();}
    private async Task RefreshSessions(string? select=null){var items=await _sessions.ListAsync();Sessions.Clear();foreach(var x in items)Sessions.Add(x);SelectedSession=Sessions.FirstOrDefault(x=>x.Id==(select??SelectedSession?.Id))??Sessions.FirstOrDefault();}
    private async Task RefreshMemories(){var items=await _memories.ListAsync();Memories.Clear();foreach(var x in items)Memories.Add(x);}
    private async Task RefreshIterationGuides(){var items=await _iterationGuideStore.ListAsync();IterationGuides.Clear();foreach(var x in items)IterationGuides.Add(x);SelectedIterationGuide=IterationGuides.FirstOrDefault();}
    private async Task GenerateIterationAsync(){if(SelectedIterationGuide is null)return;Busy=true;_runCts=new();IterationApproved=false;IterationSummary="正在生成并在隔离副本验证…";IterationFiles=IterationValidation=IterationPreview="";try{var key=await _secrets.GetAsync(ProviderId);_iterationProposal=await _iterationService.GenerateAsync(SelectedIterationGuide,new(ProviderId,Endpoint,Model,MaxTokens,TimeoutSeconds,30,false),key,_runCts.Token);IterationSummary=_iterationProposal.Summary;IterationFiles="文件：\n"+string.Join("\n",_iterationProposal.Changes.Select(x=>"• "+x.Path));IterationPreview=string.Join("\n\n",_iterationProposal.Changes.Select(x=>$"===== {x.Path} · ORIGINAL =====\n{x.OriginalContent}\n===== {x.Path} · PROPOSED =====\n{x.NewContent}"));IterationValidation=_iterationProposal.Validation.Success?"✓ 隔离构建与测试通过，可在审阅后批准。":"✗ 验证失败，禁止应用。\n"+_iterationProposal.Validation.BuildOutput+"\n"+_iterationProposal.Validation.TestOutput;}catch(OperationCanceledException){IterationSummary="已停止生成迭代提案。";}finally{Busy=false;CommandsChanged();}}
    private async Task ApplyIterationAsync(){if(_iterationProposal is null||!IterationApproved)return;Busy=true;_runCts=new();try{var result=await _iterationService.ApplyAsync(_iterationProposal,_runCts.Token);IterationValidation=result.Success?"✓ 已应用并再次验证。":$"✗ {result.Message}\n{result.ValidationOutput}";Status=result.Message;IterationApproved=false;}catch(OperationCanceledException){IterationValidation="已停止；如果正在应用文件，引擎会执行回滚。";}finally{Busy=false;CommandsChanged();}}
    private ProviderSettings CurrentProvider(bool selfReview=false)=>new(ProviderId,Endpoint,Model,MaxTokens,TimeoutSeconds,30,selfReview);
    private async Task CreateTaskPlanAsync(){Busy=true;_runCts=new();TaskPlanApproved=false;TaskSummary="正在扫描项目并生成最小上下文计划…";try{var key=await _secrets.GetAsync(ProviderId,_runCts.Token);var plan=await _taskWorkflow.CreatePlanAsync(TaskGoal,CurrentProvider(),key,_runCts.Token);await RefreshTaskPlansAsync(plan.Id);TaskSummary=$"计划已生成：{plan.Nodes.Count} 个节点。请审阅模式、依赖、路径和验收条件，然后勾选批准。";Status="任务计划等待批准";}catch(OperationCanceledException){TaskSummary="已停止生成计划。";}catch(Exception ex){TaskSummary="计划失败："+ex.Message;Status="计划失败";}finally{Busy=false;CommandsChanged();}}
    private async Task RunTaskPlanAsync(){if(SelectedTaskWorkflow is null)return;Busy=true;_runCts=new();var id=SelectedTaskWorkflow.Plan.Id;TaskSummary="同一个 Agent 正在按任务图顺序执行…";Status="任务执行中";try{var key=await _secrets.GetAsync(ProviderId,_runCts.Token);var progress=new Progress<TaskGraphCheckpoint>(cp=>ApplyTaskCheckpoint(cp));var checkpoint=await _taskWorkflow.RunAsync(SelectedTaskWorkflow.Plan,CurrentProvider(),key,this,_runCts.Token,progress,_appSettings.SystemPrompt);await RefreshTaskPlansAsync(id);TaskSummary=checkpoint.Status switch{TaskGraphStatus.Completed=>"任务已完成，所有节点均有持久化检查点。",TaskGraphStatus.Interrupted=>"任务已停止。再次审阅并批准即可从未完成节点恢复。",TaskGraphStatus.NeedsReview=>"上次进程中断时节点结果未知。请先检查工作区，再使用“已检查并允许重试”。",_=>"任务结束但存在失败或阻塞节点，请查看节点错误后再决定是否重新规划。"};Status="任务 "+checkpoint.Status;}catch(OperationCanceledException){TaskSummary="任务停止请求已提交，可稍后恢复。";}catch(Exception ex){TaskSummary="任务运行错误："+ex.Message;Status="任务错误";}finally{Busy=false;TaskPlanApproved=false;CommandsChanged();}}
    private async Task ReviewAndRetryTaskAsync(){if(SelectedTaskWorkflow is null)return;var id=SelectedTaskWorkflow.Plan.Id;Busy=true;try{await _taskWorkflow.AcknowledgeNeedsReviewAsync(SelectedTaskWorkflow.Plan);await RefreshTaskPlansAsync(id);TaskSummary="已记录人工检查。请再次审阅并批准，然后执行 / 恢复。";}finally{Busy=false;TaskPlanApproved=false;CommandsChanged();}}
    private async Task RefreshTaskPlansAsync(string? select=null){var items=await _taskWorkflow.ListAsync();var wanted=select??SelectedTaskWorkflow?.Plan.Id;TaskWorkflows.Clear();foreach(var x in items)TaskWorkflows.Add(x);SelectedTaskWorkflow=TaskWorkflows.FirstOrDefault(x=>x.Plan.Id==wanted)??TaskWorkflows.FirstOrDefault();SyncTaskNodes();}
    private void ApplyTaskCheckpoint(TaskGraphCheckpoint checkpoint){if(SelectedTaskWorkflow?.Plan.Id!=checkpoint.GraphId)return;SelectedTaskWorkflow=new TaskWorkflowItem(SelectedTaskWorkflow.Plan,checkpoint);SyncTaskNodes();TaskSummary=$"状态：{checkpoint.Status} · 已完成 {checkpoint.Nodes.Count(x=>x.Status==TaskNodeStatus.Completed)}/{checkpoint.Nodes.Count}";}
    private void SyncTaskNodes(){TaskNodes.Clear();if(SelectedTaskWorkflow is null)return;var states=SelectedTaskWorkflow.Checkpoint?.Nodes.ToDictionary(x=>x.NodeId,StringComparer.OrdinalIgnoreCase);foreach(var node in SelectedTaskWorkflow.Plan.Nodes){TaskNodeCheckpoint? state=null;if(states is not null)states.TryGetValue(node.Id,out state);TaskNodes.Add(new(node.Id,node.Title,node.Mode,state?.Status??TaskNodeStatus.Pending,string.Join(", ",node.DependsOn),string.Join("\n",node.RelevantPaths??[]),string.Join("\n",node.AcceptanceCriteria??[]),state?.Error??""));}TaskSummary=$"目标：{SelectedTaskWorkflow.Plan.Goal}\n状态：{SelectedTaskWorkflow.Status} · 节点：{SelectedTaskWorkflow.Plan.Nodes.Count}";}
    private async Task RefreshToolSessionsAsync(){var parent=SelectedSession?.Id;if(parent is null){ToolSessions.Clear();ToolSessionStatus="没有选中的聊天";return;}try{var definitions=_toolRegistry.GetDefinitions();await _toolSessions.EnsureSessionsAsync(parent,definitions);var definitionByName=definitions.ToDictionary(x=>x.Name,StringComparer.OrdinalIgnoreCase);var registered=definitionByName.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);var items=(await _toolSessions.ListAsync(parent)).Where(x=>registered.Contains(x.ToolName)).OrderBy(x=>x.ToolName,StringComparer.OrdinalIgnoreCase).ToArray();ToolSessions.Clear();foreach(var x in items){var last=x.RecentInvocations.LastOrDefault();var definition=definitionByName[x.ToolName];ToolSessions.Add(new(x.ToolName,definition.Description,definition.RiskLevel,x.State,x.TotalCalls,x.SuccessCount,x.FailureCount,x.TotalCalls==0?"-":$"{100d*x.SuccessCount/x.TotalCalls:F0}%",last?.ResultSummary??"尚未调用",last?.Error??"",x.LastSuccessfulStrategy??""));}ToolSessionStatus=$"当前聊天已建立 {items.Length}/{definitions.Count} 个工具会话；模型每轮最多激活 8 个相关工具。";}catch{ToolSessionStatus="工具会话统计暂不可用；Agent 仍可执行工具。";}}
    private async Task AcknowledgeToolSessionsAsync(){var parent=SelectedSession?.Id;if(parent is null)return;Busy=true;try{var pending=await _toolSessions.ListAsync(parent);foreach(var item in pending.Where(x=>x.State==ToolSessionState.NeedsReview))await _toolSessions.AcknowledgeNeedsReviewAsync(parent,item.ToolName);await RefreshToolSessionsAsync();ToolSessionStatus="已记录人工检查；遗留工具会话可接受新的请求。";}finally{Busy=false;CommandsChanged();}}
    private async Task RefreshBackgroundCommandsAsync(){var selected=SelectedBackgroundCommand?.Id;var items=await _backgroundCommands.ListAsync();BackgroundCommands.Clear();foreach(var item in items)BackgroundCommands.Add(item);SelectedBackgroundCommand=BackgroundCommands.FirstOrDefault(x=>x.Id==selected)??BackgroundCommands.FirstOrDefault();BackgroundStatus=$"后台任务 {items.Count} 个；Running {items.Count(x=>x.State==BackgroundCommandState.Running)} 个。Agent 的停止按钮不会自动终止它们。";}
    private async Task ReadBackgroundOutputAsync(){if(SelectedBackgroundCommand is null)return;var chunk=await _backgroundCommands.ReadOutputAsync(SelectedBackgroundCommand.Id,_backgroundCursor,64*1024);if(chunk.Content.Length>0)BackgroundOutput+=chunk.Content;_backgroundCursor=chunk.NextCursor;if(chunk.Truncated&&!BackgroundOutput.EndsWith("[输出已达到 1 MB 上限]",StringComparison.Ordinal))BackgroundOutput+="\n[输出已达到 1 MB 上限]";BackgroundStatus=chunk.IsComplete?"已读到任务输出末尾。":$"已读取到 byte {chunk.NextCursor}；仍有输出时再次点击“读取输出”。";await RefreshBackgroundCommandsAsync();}
    private async Task StopBackgroundCommandAsync(){if(SelectedBackgroundCommand is null)return;var result=MessageBox.Show($"将终止后台任务及其进程树：\n{SelectedBackgroundCommand.Id}\n{SelectedBackgroundCommand.DisplayName}\n\n继续？","停止后台任务",MessageBoxButton.YesNo,MessageBoxImage.Warning,MessageBoxResult.No);if(result!=MessageBoxResult.Yes)return;await _backgroundCommands.StopAsync(SelectedBackgroundCommand.Id);await RefreshBackgroundCommandsAsync();await ReadBackgroundOutputAsync();}
    private void SyncMessages(){Messages.Clear();if(SelectedSession is not null)foreach(var x in SelectedSession.Messages)Messages.Add(x);}
    private void ApplyPreset(){var p=ProviderId switch{"deepseek"=>("https://api.deepseek.com/v1","deepseek-chat"),"openai"=>("https://api.openai.com/v1","gpt-4.1-mini"),"openrouter"=>("https://openrouter.ai/api/v1","openai/gpt-4.1-mini"),"ollama"=>("http://localhost:11434/v1","qwen2.5:7b"),_=>(Endpoint,Model)};Endpoint=p.Item1;Model=p.Item2;}
    private void OnBrowserChanged()
    {
        var dispatcher=Application.Current?.Dispatcher;
        if(dispatcher is not null&&!dispatcher.CheckAccess()){dispatcher.BeginInvoke(OnBrowserChanged);return;}
        OnPropertyChanged(nameof(BrowserHasPage));OnPropertyChanged(nameof(BrowserHasCapture));OnPropertyChanged(nameof(BrowserPageSummary));
        if(BrowserHasPage){CenterTabIndex=1;BrowserStatus=BrowserHasCapture?"已在内存中保留一张经净化的视口截图；可一次性附到下一轮。":"正在显示编码后的本地只读快照。";}CommandsChanged();
    }
    public ValueTask OnStateAsync(AgentState state)=>ValueTask.CompletedTask; public ValueTask OnEventAsync(StreamEvent value){Application.Current.Dispatcher.Invoke(()=>{if(value.Kind==StreamEventKind.Reasoning)Reasoning+=value.Text;if(value.Kind==StreamEventKind.Content)StreamingContent+=value.Text;if(value.Kind==StreamEventKind.Revision)StreamingContent=value.Text;if(value.Kind==StreamEventKind.ToolStarted)Status="工具请求："+value.Text;if(value.Kind==StreamEventKind.ToolCompleted)Status=value.ToolResult?.Status==ToolExecutionStatus.Success?"工具完成："+value.ToolResult.ToolName:"工具未完成："+(value.ToolResult?.Error??value.Text);if(value.Kind==StreamEventKind.ToolSessionUpdated)_=RefreshToolSessionsAsync();});return ValueTask.CompletedTask;}
    public async ValueTask<bool> RequestToolApprovalAsync(ToolApprovalRequest request,CancellationToken ct){ct.ThrowIfCancellationRequested();var result=await Application.Current.Dispatcher.InvokeAsync(()=>MessageBox.Show($"工具：{request.ToolName}\n风险：{request.RiskLevel}\n\n参数：\n{request.Summary}\n\n是否允许本次操作？","K.netagentV0.1 工具审批",MessageBoxButton.YesNo,MessageBoxImage.Warning,MessageBoxResult.No)).Task;return result==MessageBoxResult.Yes;}
    private void CommandsChanged(){(SendCommand as CommandBase)?.Raise();(StopCommand as CommandBase)?.Raise();(NewSessionCommand as CommandBase)?.Raise();(ClearSessionCommand as CommandBase)?.Raise();(ClearImageCommand as CommandBase)?.Raise();(OpenBrowserSnapshotCommand as CommandBase)?.Raise();(ReadBrowserDomCommand as CommandBase)?.Raise();(CaptureBrowserViewportCommand as CommandBase)?.Raise();(AttachBrowserCaptureCommand as CommandBase)?.Raise();(GenerateIterationCommand as CommandBase)?.Raise();(ApplyIterationCommand as CommandBase)?.Raise();(CreateTaskPlanCommand as CommandBase)?.Raise();(RunTaskPlanCommand as CommandBase)?.Raise();(RefreshTaskPlansCommand as CommandBase)?.Raise();(ReviewAndRetryTaskCommand as CommandBase)?.Raise();(RefreshToolSessionsCommand as CommandBase)?.Raise();(AcknowledgeToolSessionsCommand as CommandBase)?.Raise();(RefreshBackgroundCommandsCommand as CommandBase)?.Raise();(ReadBackgroundOutputCommand as CommandBase)?.Raise();(StopBackgroundCommandCommand as CommandBase)?.Raise();}
    public void Dispose()
    {
        if(_disposed)return;_disposed=true;_browser.Changed-=OnBrowserChanged;_runCts?.Cancel();
        if(_pendingImage is not null)CryptographicOperations.ZeroMemory(_pendingImage.Data);
        _pendingImage=null;AttachedImagePreview=null;AttachedImageSummary="";GC.SuppressFinalize(this);
    }
}
public sealed record TaskNodeDisplay(string Id,string Title,TaskWorkMode Mode,TaskNodeStatus Status,string Dependencies,string RelevantPaths,string AcceptanceCriteria,string Error);
public sealed record ToolSessionDisplay(string ToolName,string Description,ToolRiskLevel RiskLevel,ToolSessionState State,int TotalCalls,int SuccessCount,int FailureCount,string SuccessRate,string LastSummary,string LastError,string LastSuccessfulStrategy);
public abstract class NotifyBase:INotifyPropertyChanged{public event PropertyChangedEventHandler? PropertyChanged;protected bool Set<T>(ref T field,T value,[CallerMemberName]string? name=null){if(EqualityComparer<T>.Default.Equals(field,value))return false;field=value;OnPropertyChanged(name);return true;}protected void OnPropertyChanged(string? n)=>PropertyChanged?.Invoke(this,new(n));}
public abstract class CommandBase:ICommand{public event EventHandler? CanExecuteChanged;public abstract bool CanExecute(object? p);public abstract void Execute(object? p);public void Raise()=>CanExecuteChanged?.Invoke(this,EventArgs.Empty);}
public sealed class RelayCommand(Action action,Func<bool>? can=null):CommandBase{public override bool CanExecute(object? p)=>can?.Invoke()??true;public override void Execute(object? p)=>action();}
public sealed class AsyncCommand(Func<Task> action,Func<bool>? can=null):CommandBase{public override bool CanExecute(object? p)=>can?.Invoke()??true;public Task ExecuteAsync()=>action();public override async void Execute(object? p){try{await ExecuteAsync();}catch(Exception ex){System.Windows.MessageBox.Show(ex.Message,"K.netagentV0.1");}}}
