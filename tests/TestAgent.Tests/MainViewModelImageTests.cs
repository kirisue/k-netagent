using System.Windows.Threading;
using TestAgent.Core;
using TestAgent.Desktop;
using Xunit;

namespace TestAgent.Tests;

public sealed class MainViewModelImageTests
{
    [Fact]
    public Task Confirmed_image_send_uses_the_immutable_provider_snapshot_shown_to_the_user() => RunStaAsync(async () =>
    {
        var bytes = OnePixelPng();
        var runtime = new CapturingRuntime();
        var confirmation = new CapturingConfirmation();
        var harness = new ViewModelHarness(runtime, new FixedImageInput(bytes), confirmation);
        var vm = harness.Create();
        await vm.InitializeAsync();
        await vm.AttachImageAsync("explicit-user-selection.png");

        confirmation.OnConfirm = request =>
        {
            Assert.Equal("https://vision.example/v1/chat/completions", request.Target);
            Assert.Equal("vision-model-a", request.Model);
            vm.ProviderId = "openai";
            return true;
        };

        await ((AsyncCommand)vm.SendCommand).ExecuteAsync();

        Assert.Equal(1, runtime.CallCount);
        Assert.NotNull(runtime.Settings);
        Assert.Equal("custom", runtime.Settings.ProviderId);
        Assert.Equal("https://vision.example/v1", runtime.Settings.Endpoint);
        Assert.Equal("vision-model-a", runtime.Settings.Model);
        Assert.True(runtime.Settings.SupportsImageInput);
        Assert.Equal("custom", harness.Secrets.LastRequestedProviderId);
    });

    [Fact]
    public Task Rejecting_image_confirmation_does_not_call_runtime_or_consume_the_attachment() => RunStaAsync(async () =>
    {
        var bytes = OnePixelPng();
        var original = bytes.ToArray();
        var runtime = new CapturingRuntime();
        var confirmation = new CapturingConfirmation { OnConfirm = _ => false };
        var vm = new ViewModelHarness(runtime, new FixedImageInput(bytes), confirmation).Create();
        await vm.InitializeAsync();
        vm.Input = "describe it";
        await vm.AttachImageAsync("explicit-user-selection.png");

        await ((AsyncCommand)vm.SendCommand).ExecuteAsync();

        Assert.Equal(0, runtime.CallCount);
        Assert.True(vm.HasAttachedImage);
        Assert.Equal("describe it", vm.Input);
        Assert.Equal(original, bytes);
        Assert.Equal(1, confirmation.CallCount);
    });

    [Fact]
    public Task Completed_image_send_detaches_and_zeroes_the_owned_pixel_buffer() => RunStaAsync(async () =>
    {
        var bytes = OnePixelPng();
        var runtime = new CapturingRuntime();
        var vm = new ViewModelHarness(runtime, new FixedImageInput(bytes),
            new CapturingConfirmation { OnConfirm = _ => true }).Create();
        await vm.InitializeAsync();
        await vm.AttachImageAsync("explicit-user-selection.png");

        await ((AsyncCommand)vm.SendCommand).ExecuteAsync();

        Assert.True(runtime.SawNonZeroImageDuringRun);
        Assert.False(vm.HasAttachedImage);
        Assert.Null(vm.AttachedImagePreview);
        Assert.Empty(vm.AttachedImageSummary);
        Assert.All(bytes, value => Assert.Equal((byte)0, value));
    });

    [Fact]
    public Task Browser_capture_is_taken_once_attached_once_and_never_written_into_chat_messages() => RunStaAsync(async () =>
    {
        var bytes = OnePixelPng();
        var encodedPixels = Convert.ToBase64String(bytes);
        var runtime = new CapturingRuntime();
        var browser = new CapturingBrowser(new ImageInput("image/png", bytes, "browser-sha", 1, 1));
        var harness = new ViewModelHarness(runtime, new FixedImageInput(OnePixelPng()),
            new CapturingConfirmation { OnConfirm = _ => true }, browser);
        var vm = harness.Create();
        await vm.InitializeAsync();

        Assert.True(vm.AttachBrowserCaptureCommand.CanExecute(null));
        vm.AttachBrowserCaptureCommand.Execute(null);

        Assert.Equal(1, browser.TakeCalls);
        Assert.False(browser.HasLatestCapture);
        Assert.True(vm.HasAttachedImage);
        Assert.False(vm.AttachBrowserCaptureCommand.CanExecute(null));
        Assert.Contains("一次性转移", vm.BrowserStatus);

        vm.Input = "summarize the visible snapshot";
        await ((AsyncCommand)vm.SendCommand).ExecuteAsync();

        Assert.Equal(1, browser.TakeCalls);
        Assert.Equal(1, runtime.CallCount);
        Assert.NotNull(runtime.ReturnedSession);
        Assert.All(runtime.ReturnedSession.Messages,
            message => Assert.DoesNotContain(encodedPixels, message.Content, StringComparison.Ordinal));
        Assert.DoesNotContain(encodedPixels, runtime.LastUserMessage, StringComparison.Ordinal);
        Assert.All(bytes, value => Assert.Equal((byte)0, value));
    });

    private static byte[] OnePixelPng() => Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");

    private static Task RunStaAsync(Func<Task> action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(async () =>
            {
                try
                {
                    await action();
                    completion.SetResult();
                }
                catch (Exception ex)
                {
                    completion.SetException(ex);
                }
                finally
                {
                    dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                }
            });
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        return completion.Task;
    }

    private sealed class ViewModelHarness(
        CapturingRuntime runtime,
        IImageInputService imageInputs,
        IImageSendConfirmationService confirmation,
        CapturingBrowser? browser = null)
    {
        private readonly MemorySessions _sessions = new();
        private readonly CapturingBrowser _browser = browser ?? new CapturingBrowser();
        public CapturingSecrets Secrets { get; } = new();

        public MainViewModel Create() => new(runtime, _sessions, new EmptyMemories(),
            new FixedSettings(), Secrets, new EmptyIterationGuides(), new UnusedIterationService(),
            new EmptyTaskWorkflow(), new EmptyToolSessions(), new EmptyToolRegistry(),
            new EmptyBackgroundCommands(), imageInputs, confirmation, _browser, new UnusedToolExecution());
    }

    private sealed class CapturingRuntime : IAgentRuntime
    {
        public int CallCount { get; private set; }
        public ProviderSettings? Settings { get; private set; }
        public bool SawNonZeroImageDuringRun { get; private set; }
        public string LastUserMessage { get; private set; } = "";
        public ChatSession? ReturnedSession { get; private set; }

        public Task<AgentRunResult> RunAsync(ChatSession session, string userMessage, ProviderSettings settings,
            string? apiKey, IAgentObserver observer, CancellationToken cancellationToken, AgentRunOptions? options = null)
        {
            CallCount++;
            Settings = settings;
            LastUserMessage = userMessage;
            var image = Assert.Single(options?.Images ?? []);
            SawNonZeroImageDuringRun = image.Data.Any(value => value != 0);
            var updated = session with
            {
                Messages = [new(ChatRole.User, userMessage, DateTimeOffset.UtcNow)],
                UpdatedAt = DateTimeOffset.UtcNow
            };
            ReturnedSession = updated;
            return Task.FromResult(new AgentRunResult(updated, AgentState.Completed, "done", "", 1));
        }
    }

    private sealed class CapturingConfirmation : IImageSendConfirmationService
    {
        public Func<ImageSendConfirmation, bool>? OnConfirm { get; set; }
        public int CallCount { get; private set; }
        public bool Confirm(ImageSendConfirmation request)
        {
            CallCount++;
            return OnConfirm?.Invoke(request) ?? false;
        }
    }

    private sealed class FixedImageInput(byte[] data) : IImageInputService
    {
        private readonly ImageInput _image = new("image/png", data, "test-sha", 1, 1);
        public Task<ImageInput> LoadAsync(string filePath, CancellationToken ct = default) => Task.FromResult(_image);
    }

    private sealed class CapturingBrowser(ImageInput? capture = null) : IReadOnlyBrowserSession
    {
        private ImageInput? _capture = capture;
        public int TakeCalls { get; private set; }
        public BrowserPageDocument? Current => null;
        public bool HasLatestCapture => _capture is not null;
        public event Action? Changed;
        public Task<BrowserPageDocument> OpenSnapshotAsync(string url, int maxChars = 30_000,
            CancellationToken ct = default) => throw new NotSupportedException();
        public Task<BrowserDomSnapshot> ReadDomAsync(int maxChars = 30_000,
            CancellationToken ct = default) => throw new NotSupportedException();
        public Task<BrowserCaptureReceipt> CaptureViewportAsync(CancellationToken ct = default) =>
            throw new NotSupportedException();
        public ImageInput? TakeLatestCapture()
        {
            TakeCalls++;
            var result = _capture;
            _capture = null;
            Changed?.Invoke();
            return result;
        }
    }

    private sealed class UnusedToolExecution : IToolExecutionService
    {
        public IReadOnlyList<ToolDefinition> GetDefinitions() => [];
        public Task<ToolResult> ExecuteAsync(ToolRequest request, IAgentObserver observer,
            CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class MemorySessions : ISessionStore
    {
        private readonly List<ChatSession> _items = [];
        public Task<IReadOnlyList<ChatSession>> ListAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ChatSession>>(_items.ToArray());
        public Task<ChatSession?> GetAsync(string id, CancellationToken ct = default) =>
            Task.FromResult(_items.FirstOrDefault(item => item.Id == id));
        public Task SaveAsync(ChatSession session, CancellationToken ct = default)
        {
            _items.RemoveAll(item => item.Id == session.Id);
            _items.Add(session);
            return Task.CompletedTask;
        }
        public Task DeleteAsync(string id, CancellationToken ct = default)
        {
            _items.RemoveAll(item => item.Id == id);
            return Task.CompletedTask;
        }
    }

    private sealed class EmptyMemories : IMemoryStore
    {
        public Task<IReadOnlyList<MemoryEntry>> ListAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<MemoryEntry>>([]);
        public Task SaveAsync(MemoryEntry memory, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeleteAsync(string id, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class FixedSettings : ISettingsStore
    {
        private readonly AppSettings _settings = new(new("custom", "https://vision.example/v1", "vision-model-a",
            SelfReviewEnabled: false, SupportsImageInput: true), "system-test");
        public Task<AppSettings> LoadAsync(CancellationToken ct = default) => Task.FromResult(_settings);
        public Task SaveAsync(AppSettings settings, CancellationToken ct = default) => Task.CompletedTask;
    }

    public sealed class CapturingSecrets : ISecureSecretStore
    {
        public string? LastRequestedProviderId { get; private set; }
        public Task<string?> GetAsync(string providerId, CancellationToken ct = default)
        {
            LastRequestedProviderId = providerId;
            return Task.FromResult<string?>("test-key");
        }
        public Task SetAsync(string providerId, string secret, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class EmptyIterationGuides : IIterationGuideStore
    {
        public Task<IReadOnlyList<IterationGuide>> ListAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<IterationGuide>>([]);
    }

    private sealed class UnusedIterationService : ICodeIterationService
    {
        public Task<IterationProposal> GenerateAsync(IterationGuide guide, ProviderSettings settings, string? apiKey,
            CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IterationApplyResult> ApplyAsync(IterationProposal proposal, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class EmptyTaskWorkflow : ITaskWorkflowService
    {
        public Task<TaskGraphPlan> CreatePlanAsync(string goal, ProviderSettings settings, string? apiKey,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<TaskWorkflowItem>> ListAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<TaskWorkflowItem>>([]);
        public Task<TaskGraphCheckpoint> RunAsync(TaskGraphPlan plan, ProviderSettings settings, string? apiKey,
            IAgentObserver observer, CancellationToken cancellationToken,
            IProgress<TaskGraphCheckpoint>? progress = null, string? systemPrompt = null) => throw new NotSupportedException();
        public Task<TaskGraphCheckpoint> AcknowledgeNeedsReviewAsync(TaskGraphPlan plan,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class EmptyToolSessions : IToolSessionCoordinator
    {
        public Task<IReadOnlyList<ToolSession>> EnsureSessionsAsync(string parentSessionId,
            IReadOnlyList<ToolDefinition> definitions, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ToolSession>>([]);
        public Task<ToolSession> StartAsync(ToolRequest request, bool approved, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<ToolSession> CompleteAsync(ToolSession session, ToolRequest request, ToolResult result,
            bool approved, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string?> GetStrategyHintAsync(string parentSessionId, string toolName,
            CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task<IReadOnlyList<ToolSession>> ListAsync(string? parentSessionId = null,
            CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ToolSession>>([]);
        public Task<ToolSession> AcknowledgeNeedsReviewAsync(string parentSessionId, string toolName,
            CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class EmptyToolRegistry : IToolRegistry
    {
        public IReadOnlyList<ToolDefinition> GetDefinitions() => [];
        public IAgentTool? Get(string name) => null;
    }

    private sealed class EmptyBackgroundCommands : IBackgroundCommandService
    {
        public Task<BackgroundCommandJob> StartAsync(BackgroundCommandSpec spec,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<BackgroundCommandJob?> GetAsync(string jobId, CancellationToken cancellationToken = default) =>
            Task.FromResult<BackgroundCommandJob?>(null);
        public Task<IReadOnlyList<BackgroundCommandJob>> ListAsync(string? scopeId = null,
            CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<BackgroundCommandJob>>([]);
        public Task<BackgroundCommandOutputChunk> ReadOutputAsync(string jobId, long cursor = 0,
            int maxBytes = 64 * 1024, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<BackgroundCommandJob> StopAsync(string jobId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<BackgroundCommandJob>> ReconcileAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<BackgroundCommandJob>>([]);
    }
}
