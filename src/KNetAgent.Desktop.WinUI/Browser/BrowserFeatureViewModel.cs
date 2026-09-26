using System.Security.Cryptography;
using System.Text.Json;
using KNetAgent.Desktop.WinUI.Features.Media;
using KNetAgent.Desktop.WinUI.ViewModels;
using TestAgent.Core;

namespace KNetAgent.Desktop.WinUI.Browser;

public sealed class BrowserFeatureViewModel : ObservableObject, IDisposable
{
    private readonly IReadOnlyBrowserSession _browser;
    private readonly MediaAttachmentViewModel _media;
    private readonly IToolExecutionService _toolExecution;
    private IAgentObserver? _observer;
    private Func<string?>? _sessionIdAccessor;
    private string _address = "https://example.com";
    private string _title = "尚未打开网页快照";
    private string _status = "只读取公开 HTTPS 页面；不会运行远程脚本或使用登录态";
    private string _domText = "";
    private bool _busy;
    private bool _disposed;

    public BrowserFeatureViewModel(IReadOnlyBrowserSession browser, MediaAttachmentViewModel media,
        IToolExecutionService toolExecution)
    {
        _browser = browser ?? throw new ArgumentNullException(nameof(browser));
        _media = media ?? throw new ArgumentNullException(nameof(media));
        _toolExecution = toolExecution ?? throw new ArgumentNullException(nameof(toolExecution));
        _browser.Changed += OnBrowserChanged;
    }

    public string Address { get => _address; set => SetProperty(ref _address, value); }
    public string Title { get => _title; private set => SetProperty(ref _title, value); }
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public string DomText { get => _domText; private set => SetProperty(ref _domText, value); }
    public bool Busy { get => _busy; private set => SetProperty(ref _busy, value); }
    public bool HasPage => _browser.Current is not null;

    /// <summary>Connects UI-initiated tools to the same approval and audit observer as the Agent loop.</summary>
    public void Initialize(IAgentObserver observer, Func<string?> sessionIdAccessor)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _observer = observer ?? throw new ArgumentNullException(nameof(observer));
        _sessionIdAccessor = sessionIdAccessor ?? throw new ArgumentNullException(nameof(sessionIdAccessor));
    }

    public async Task OpenAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Busy) return;
        Busy = true;
        Status = "正在通过安全读取器获取网页…";
        try
        {
            var result = await ExecuteToolAsync("open_browser_snapshot",
                JsonSerializer.Serialize(new { url = Address.Trim(), maxChars = 30_000 }),
                cancellationToken);
            RequireSuccess(result);
            var page = _browser.Current ??
                throw new InvalidOperationException("The browser tool completed without a visible snapshot.");
            Address = page.Url;
            Title = page.Title;
            DomText = page.Dom.Text;
            Status = page.Dom.Truncated ? "安全快照已打开（正文已截断）" : "安全快照已打开";
            OnPropertyChanged(nameof(HasPage));
        }
        catch
        {
            Status = "网页快照打开失败";
            throw;
        }
        finally { Busy = false; }
    }

    public async Task ReadDomAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Busy) return;
        Busy = true;
        try
        {
            var result = await ExecuteToolAsync("read_browser_dom", "{\"maxChars\":30000}",
                cancellationToken);
            RequireSuccess(result);
            var dom = _browser.Current?.Dom ??
                throw new InvalidOperationException("The browser tool completed without a DOM snapshot.");
            DomText = dom.Text;
            Status = result.Summary ?? $"已读取净化 DOM：{dom.Elements.Count} 个结构元素";
        }
        catch
        {
            Status = "读取浏览器 DOM 失败";
            throw;
        }
        finally { Busy = false; }
    }

    /// <summary>Captures only the local sanitized snapshot and attaches it once to the next model turn.</summary>
    public async Task CaptureAndAttachAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Busy) return;
        Busy = true;
        try
        {
            var result = await ExecuteToolAsync("capture_browser_viewport", "{}", cancellationToken);
            RequireSuccess(result);
            var capture = _browser.TakeLatestCapture() ??
                throw new InvalidOperationException("The browser capture was not available.");
            try { _media.AttachBrowserCapture(capture); }
            finally
            {
                if (capture.Data.Length > 0) CryptographicOperations.ZeroMemory(capture.Data);
            }
            Status = $"已把 {capture.Width}×{capture.Height} 视口附到下一轮；发送前仍需确认";
        }
        catch
        {
            Status = "浏览器截图失败";
            throw;
        }
        finally { Busy = false; }
    }

    private Task<ToolResult> ExecuteToolAsync(string toolName, string argumentsJson,
        CancellationToken cancellationToken)
    {
        var observer = _observer ?? throw new InvalidOperationException(
            "Attach the browser feature to an Agent observer before using it.");
        var sessionId = _sessionIdAccessor?.Invoke();
        if (string.IsNullOrWhiteSpace(sessionId))
            throw new InvalidOperationException("Select or create a chat before using the browser.");
        var request = new ToolRequest($"UI-BROWSER-{Guid.NewGuid():N}", toolName,
            argumentsJson, sessionId);
        return _toolExecution.ExecuteAsync(request, observer, cancellationToken);
    }

    private static void RequireSuccess(ToolResult result)
    {
        if (result.Status != ToolExecutionStatus.Success)
            throw new InvalidOperationException(result.Error ?? result.Output ??
                                                $"Browser tool ended with {result.Status}.");
    }

    private void OnBrowserChanged()
    {
        var page = _browser.Current;
        if (page is not null)
        {
            Title = page.Title;
            OnPropertyChanged(nameof(HasPage));
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _browser.Changed -= OnBrowserChanged;
        GC.SuppressFinalize(this);
    }
}
