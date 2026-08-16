using System.IO;
using System.Net;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using TestAgent.Core;
using TestAgent.Infrastructure;

[assembly: InternalsVisibleTo("TestAgent.Tests")]

namespace TestAgent.Desktop;

internal sealed record BrowserSessionTestHooks(
    Func<string, CancellationToken, Task> NavigateAsync,
    Func<CancellationToken, Task<byte[]>> CaptureAsync);

/// <summary>
/// Displays the already-sanitized text returned by <see cref="ISafeWebContentReader"/>.
/// The WebView never receives the remote HTML and is not allowed to make network requests.
/// </summary>
public sealed class WpfReadOnlyBrowserSession : IReadOnlyBrowserSession, IDisposable
{
    private readonly ISafeWebContentReader _reader;
    private readonly AppPaths _paths;
    private readonly BrowserSessionTestHooks? _testHooks;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly object _stateGate = new();
    private WebView2? _webView;
    private CoreWebView2Environment? _environment;
    private BrowserPageDocument? _current;
    private ImageInput? _latestCapture;
    private string? _profilePath;
    private bool _configured;
    private bool _allowLocalNavigation;
    private bool _displayReady;
    private bool _disposed;

    public WpfReadOnlyBrowserSession(ISafeWebContentReader reader, AppPaths paths)
    {
        _reader = reader;
        _paths = paths;
    }

    internal WpfReadOnlyBrowserSession(ISafeWebContentReader reader, AppPaths paths,
        BrowserSessionTestHooks testHooks)
    {
        _reader = reader;
        _paths = paths;
        _testHooks = testHooks ?? throw new ArgumentNullException(nameof(testHooks));
    }

    public BrowserPageDocument? Current
    {
        get { lock (_stateGate) return _displayReady ? _current : null; }
    }

    public bool HasLatestCapture
    {
        get { lock (_stateGate) return _displayReady && _latestCapture is not null; }
    }

    public event Action? Changed;

    /// <summary>Attaches the one visible WebView2 surface owned by the main window.</summary>
    public void Attach(WebView2 webView)
    {
        ArgumentNullException.ThrowIfNull(webView);
        if (!webView.Dispatcher.CheckAccess())
            throw new InvalidOperationException("The browser surface must be attached on the WPF UI thread.");
        lock (_stateGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_webView is not null && !ReferenceEquals(_webView, webView))
                throw new InvalidOperationException("A browser surface is already attached to this session.");
            _webView = webView;
            _webView.AllowDrop = false;
        }
    }

    public async Task<BrowserPageDocument> OpenSnapshotAsync(string url, int maxChars = 30_000,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        maxChars = Math.Clamp(maxChars, 1_000, 80_000);
        await _operationGate.WaitAsync(ct);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var page = await _reader.ReadAsync(url, maxChars, ct);
            var document = CreateDocument(page);
            var html = CreateSnapshotHtml(document);
            InvalidateDisplayedPage();
            try
            {
                await RenderSnapshotAsync(html, ct);
            }
            catch
            {
                StopPendingNavigation();
                // The visible surface may have partially changed. Keep the session explicitly
                // non-readable and non-capturable instead of pairing it with stale metadata.
                InvalidateDisplayedPage();
                throw;
            }

            CommitDisplayedPage(document);
            return document;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public Task<BrowserDomSnapshot> ReadDomAsync(int maxChars = 30_000, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);
        maxChars = Math.Clamp(maxChars, 1_000, 80_000);
        BrowserDomSnapshot source;
        lock (_stateGate)
            source = _displayReady && _current is not null
                ? _current.Dom
                : throw new InvalidOperationException("Open a safe browser snapshot first.");

        var truncated = source.Truncated || source.Text.Length > maxChars;
        var text = source.Text.Length <= maxChars
            ? source.Text
            : source.Text[..maxChars] + "\n… [browser snapshot truncated]";
        return Task.FromResult(source with
        {
            Text = text,
            Truncated = truncated,
            CapturedAt = DateTimeOffset.UtcNow
        });
    }

    public async Task<BrowserCaptureReceipt> CaptureViewportAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _operationGate.WaitAsync(ct);
        try
        {
            BrowserPageDocument document;
            lock (_stateGate)
                document = _displayReady && _current is not null
                    ? _current
                    : throw new InvalidOperationException("Open a safe browser snapshot first.");

            var rawPng = await CaptureViewportBytesAsync(ct);

            var sanitized = WpfImageSanitizer.SanitizeOwned(rawPng, "image/png");
            ImageInput? replaced;
            try
            {
                lock (_stateGate)
                {
                    ObjectDisposedException.ThrowIf(_disposed, this);
                    if (!_displayReady || !ReferenceEquals(_current, document))
                        throw new InvalidOperationException(
                            "The displayed browser snapshot changed before capture completed.");
                    replaced = _latestCapture;
                    _latestCapture = sanitized;
                }
            }
            catch { Zero(sanitized); throw; }
            Zero(replaced);
            var capturedAt = DateTimeOffset.UtcNow;
            RaiseChanged();
            return new(document.Url, document.Title, sanitized.Width, sanitized.Height, capturedAt);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public ImageInput? TakeLatestCapture()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ImageInput? capture;
        ImageInput? stale = null;
        lock (_stateGate)
        {
            capture = _displayReady ? _latestCapture : null;
            if (!_displayReady) stale = _latestCapture;
            _latestCapture = null;
        }
        Zero(stale);
        if (capture is not null) RaiseChanged();
        return capture;
    }

    private async Task RenderSnapshotAsync(string html, CancellationToken ct)
    {
        if (_testHooks is not null)
        {
            await _testHooks.NavigateAsync(html, ct);
            return;
        }

        await OnUiAsync(async webView =>
        {
            await EnsureReadyOnUiAsync(webView);
            await NavigateLocalSnapshotOnUiAsync(webView, html, ct);
            return true;
        }, ct);
    }

    private async Task<byte[]> CaptureViewportBytesAsync(CancellationToken ct)
    {
        byte[] rawPng;
        if (_testHooks is not null)
        {
            ct.ThrowIfCancellationRequested();
            rawPng = await _testHooks.CaptureAsync(ct);
            ct.ThrowIfCancellationRequested();
        }
        else
        {
            rawPng = await OnUiAsync(async webView =>
            {
                await EnsureReadyOnUiAsync(webView);
                ct.ThrowIfCancellationRequested();
                using var encoded = new MemoryStream();
                try
                {
                    await webView.CoreWebView2.CapturePreviewAsync(
                        CoreWebView2CapturePreviewImageFormat.Png, encoded);
                    ct.ThrowIfCancellationRequested();
                    return encoded.ToArray();
                }
                finally
                {
                    ZeroMemoryStreamBuffer(encoded);
                }
            }, ct);
        }

        if (rawPng.Length is > 0 and <= ImageInputPreflight.MaxSourceBytes) return rawPng;
        if (rawPng.Length > 0) CryptographicOperations.ZeroMemory(rawPng);
        throw new InvalidDataException("The browser viewport capture must be between 1 byte and 10 MB.");
    }

    private void InvalidateDisplayedPage()
    {
        ImageInput? capture;
        lock (_stateGate)
        {
            _displayReady = false;
            _current = null;
            capture = _latestCapture;
            _latestCapture = null;
        }
        Zero(capture);
        RaiseChanged();
    }

    private void CommitDisplayedPage(BrowserPageDocument document)
    {
        lock (_stateGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _current = document;
            _displayReady = true;
        }
        RaiseChanged();
    }

    private void StopPendingNavigation()
    {
        if (_testHooks is not null) return;
        WebView2? webView;
        lock (_stateGate) webView = _webView;
        if (webView is null || webView.Dispatcher.HasShutdownStarted) return;

        void Stop()
        {
            try { webView.CoreWebView2?.Stop(); }
            catch (InvalidOperationException) { }
        }

        if (webView.Dispatcher.CheckAccess()) Stop();
        else webView.Dispatcher.BeginInvoke(Stop, DispatcherPriority.Send);
    }

    private async Task EnsureReadyOnUiAsync(WebView2 webView)
    {
        if (_configured) return;
        if (!webView.Dispatcher.CheckAccess())
            throw new InvalidOperationException("WebView2 initialization must run on the WPF UI thread.");

        var profileRoot = Path.GetFullPath(_paths.BrowserProfile);
        Directory.CreateDirectory(profileRoot);
        if ((File.GetAttributes(profileRoot) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("The browser profile root cannot be a linked directory.");
        _profilePath ??= Path.Combine(profileRoot,
            $"ephemeral-{Environment.ProcessId}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_profilePath);
        if ((File.GetAttributes(_profilePath) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("The browser profile directory cannot be a linked directory.");

        try
        {
            _environment ??= await CoreWebView2Environment.CreateAsync(null, _profilePath);
            await webView.EnsureCoreWebView2Async(_environment);
        }
        catch (WebView2RuntimeNotFoundException ex)
        {
            throw new InvalidOperationException(
                "The Microsoft Edge WebView2 Runtime is required for the visible safe browser.", ex);
        }

        var settings = webView.CoreWebView2.Settings;
        settings.AreBrowserAcceleratorKeysEnabled = false;
        settings.AreDefaultContextMenusEnabled = false;
        settings.AreDefaultScriptDialogsEnabled = false;
        settings.AreDevToolsEnabled = false;
        settings.AreHostObjectsAllowed = false;
        settings.IsBuiltInErrorPageEnabled = false;
        settings.IsGeneralAutofillEnabled = false;
        settings.IsPasswordAutosaveEnabled = false;
        settings.IsScriptEnabled = false;
        settings.IsStatusBarEnabled = false;
        settings.IsWebMessageEnabled = false;
        settings.IsZoomControlEnabled = false;

        webView.CoreWebView2.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
        webView.CoreWebView2.WebResourceRequested += BlockExternalResource;
        webView.CoreWebView2.NavigationStarting += BlockNonSnapshotNavigation;
        webView.CoreWebView2.NewWindowRequested += BlockNewWindow;
        webView.CoreWebView2.PermissionRequested += DenyPermission;
        webView.CoreWebView2.DownloadStarting += CancelDownload;
        webView.CoreWebView2.BasicAuthenticationRequested += CancelBasicAuthentication;
        webView.CoreWebView2.ClientCertificateRequested += CancelClientCertificate;
        _configured = true;
    }

    private async Task NavigateLocalSnapshotOnUiAsync(WebView2 webView, string html, CancellationToken ct)
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs args)
        {
            if (args.IsSuccess) completed.TrySetResult();
            else completed.TrySetException(new InvalidOperationException(
                $"The local browser snapshot could not be rendered ({args.WebErrorStatus})."));
        }

        webView.CoreWebView2.NavigationCompleted += OnCompleted;
        _allowLocalNavigation = true;
        try
        {
            webView.NavigateToString(html);
            await completed.Task.WaitAsync(ct);
        }
        finally
        {
            _allowLocalNavigation = false;
            webView.CoreWebView2.NavigationCompleted -= OnCompleted;
        }
    }

    private Task<T> OnUiAsync<T>(Func<WebView2, Task<T>> action, CancellationToken ct)
    {
        WebView2 webView;
        lock (_stateGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            webView = _webView ?? throw new InvalidOperationException(
                "Attach a visible WebView2 browser surface before opening or capturing a snapshot.");
        }
        if (webView.Dispatcher.CheckAccess()) return action(webView);
        return webView.Dispatcher
            .InvokeAsync(() => action(webView), DispatcherPriority.Normal, ct)
            .Task.Unwrap();
    }

    private static BrowserPageDocument CreateDocument(WebContentResult page)
    {
        var capturedAt = DateTimeOffset.UtcNow;
        var elements = new List<BrowserDomElement>();
        if (page.Headings is not null)
        {
            foreach (var heading in page.Headings.Take(40))
            {
                var separator = heading.IndexOf(':');
                var kind = separator is > 0 and <= 2 ? heading[..separator] : "heading";
                var text = separator is > 0 and <= 2 ? heading[(separator + 1)..].Trim() : heading;
                elements.Add(new(kind, text));
            }
        }
        if (page.Links is not null)
            elements.AddRange(page.Links.Take(40).Select(link =>
                new BrowserDomElement("link", link.Text, link.Url)));
        var title = string.IsNullOrWhiteSpace(page.Title) ? "Safe web snapshot" : page.Title;
        var dom = new BrowserDomSnapshot(page.FinalUrl, title, page.Text, elements, page.Truncated, capturedAt);
        return new(page.FinalUrl, title, dom, capturedAt);
    }

    private static string CreateSnapshotHtml(BrowserPageDocument page)
    {
        static string E(string value) => WebUtility.HtmlEncode(value);
        var html = new StringBuilder(checked(page.Dom.Text.Length + 8_192));
        html.Append("<!doctype html><html><head><meta charset=\"utf-8\">")
            .Append("<meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; script-src 'none'; style-src 'unsafe-inline'; img-src data:; font-src 'none'; connect-src 'none'; media-src 'none'; object-src 'none'; frame-src 'none'; worker-src 'none'; manifest-src 'none'; base-uri 'none'; form-action 'none'\">")
            .Append("<meta name=\"referrer\" content=\"no-referrer\">")
            .Append("<title>").Append(E(page.Title)).Append("</title>")
            .Append("<style>html{color-scheme:dark}body{margin:0 auto;max-width:1050px;padding:28px;font:15px/1.65 'Segoe UI',sans-serif;background:#17191c;color:#e7e9ec}header{border-bottom:1px solid #3a3d43;margin-bottom:24px;padding-bottom:16px}h1{font-size:22px;margin:0 0 8px}.url,.notice{color:#9ca7b5;overflow-wrap:anywhere}.notice{font-size:13px}pre{white-space:pre-wrap;overflow-wrap:anywhere;font:14px/1.65 'Segoe UI',sans-serif}.structure{margin-top:28px;border-top:1px solid #3a3d43;padding-top:16px}.item{margin:8px 0}.target{display:block;color:#8ab4f8;font:12px/1.4 Consolas,monospace;overflow-wrap:anywhere}</style>")
            .Append("</head><body><header><h1>").Append(E(page.Title)).Append("</h1><div class=\"url\">")
            .Append(E(page.Url)).Append("</div><div class=\"notice\">Read-only local snapshot. Remote HTML, scripts, forms, cookies, and external resources are not loaded.</div></header>")
            .Append("<main><pre>").Append(E(page.Dom.Text)).Append("</pre>");
        if (page.Dom.Elements.Count > 0)
        {
            html.Append("<section class=\"structure\"><h2>Page structure</h2>");
            foreach (var element in page.Dom.Elements.Take(80))
            {
                html.Append("<div class=\"item\"><strong>").Append(E(element.Kind)).Append(":</strong> ")
                    .Append(E(element.Text));
                if (!string.IsNullOrWhiteSpace(element.Target))
                    html.Append("<span class=\"target\">").Append(E(element.Target)).Append("</span>");
                html.Append("</div>");
            }
            html.Append("</section>");
        }
        return html.Append("</main></body></html>").ToString();
    }

    private void BlockExternalResource(object? sender, CoreWebView2WebResourceRequestedEventArgs args)
    {
        if (_environment is null) return;
        args.Response = _environment.CreateWebResourceResponse(
            new MemoryStream([], writable: false), 403, "Forbidden", "Content-Type: text/plain; charset=utf-8");
    }

    private void BlockNonSnapshotNavigation(object? sender, CoreWebView2NavigationStartingEventArgs args)
    {
        if (!_allowLocalNavigation ||
            !args.Uri.Equals("about:blank", StringComparison.OrdinalIgnoreCase))
            args.Cancel = true;
    }

    private static void BlockNewWindow(object? sender, CoreWebView2NewWindowRequestedEventArgs args) =>
        args.Handled = true;

    private static void DenyPermission(object? sender, CoreWebView2PermissionRequestedEventArgs args) =>
        args.State = CoreWebView2PermissionState.Deny;

    private static void CancelDownload(object? sender, CoreWebView2DownloadStartingEventArgs args)
    {
        args.Cancel = true;
        args.Handled = true;
    }

    private static void CancelBasicAuthentication(object? sender,
        CoreWebView2BasicAuthenticationRequestedEventArgs args) => args.Cancel = true;

    private static void CancelClientCertificate(object? sender,
        CoreWebView2ClientCertificateRequestedEventArgs args)
    {
        args.Handled = true;
        args.SelectedCertificate = null;
    }

    private void RaiseChanged()
    {
        var handler = Changed;
        if (handler is null) return;
        WebView2? webView;
        lock (_stateGate) webView = _webView;
        if (webView is null || webView.Dispatcher.CheckAccess()) handler();
        else if (!webView.Dispatcher.HasShutdownStarted) webView.Dispatcher.BeginInvoke(handler);
    }

    public void Dispose()
    {
        WebView2? webView;
        ImageInput? capture;
        string? profilePath;
        lock (_stateGate)
        {
            if (_disposed) return;
            _disposed = true;
            webView = _webView;
            _webView = null;
            capture = _latestCapture;
            _latestCapture = null;
            _current = null;
            _displayReady = false;
            profilePath = _profilePath;
            _profilePath = null;
        }
        Zero(capture);

        if (webView is not null)
        {
            void Close()
            {
                if (_configured && webView.CoreWebView2 is not null)
                {
                    webView.CoreWebView2.WebResourceRequested -= BlockExternalResource;
                    webView.CoreWebView2.NavigationStarting -= BlockNonSnapshotNavigation;
                    webView.CoreWebView2.NewWindowRequested -= BlockNewWindow;
                    webView.CoreWebView2.PermissionRequested -= DenyPermission;
                    webView.CoreWebView2.DownloadStarting -= CancelDownload;
                    webView.CoreWebView2.BasicAuthenticationRequested -= CancelBasicAuthentication;
                    webView.CoreWebView2.ClientCertificateRequested -= CancelClientCertificate;
                }
                webView.Dispose();
            }
            try
            {
                if (webView.Dispatcher.CheckAccess()) Close();
                else if (!webView.Dispatcher.HasShutdownStarted) webView.Dispatcher.Invoke(Close);
            }
            catch (InvalidOperationException) { }
        }

        DeleteEphemeralProfile(profilePath);
        GC.SuppressFinalize(this);
    }

    private void DeleteEphemeralProfile(string? profilePath)
    {
        if (string.IsNullOrWhiteSpace(profilePath)) return;
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_paths.BrowserProfile));
        var candidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(profilePath));
        var prefix = root + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(candidate).StartsWith("ephemeral-", StringComparison.Ordinal) ||
            !Directory.Exists(candidate)) return;
        try
        {
            if ((File.GetAttributes(candidate) & FileAttributes.ReparsePoint) != 0) return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; }

        foreach (var delay in new[] { 0, 75, 150, 300 })
        {
            if (delay > 0) Thread.Sleep(delay);
            try
            {
                if (Directory.Exists(candidate)) Directory.Delete(candidate, recursive: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private static void Zero(ImageInput? image)
    {
        if (image?.Data is { Length: > 0 } data) CryptographicOperations.ZeroMemory(data);
    }

    private static void ZeroMemoryStreamBuffer(MemoryStream stream)
    {
        if (stream.TryGetBuffer(out var buffer) && buffer.Array is { } array && stream.Length > 0)
            CryptographicOperations.ZeroMemory(array.AsSpan(buffer.Offset, checked((int)stream.Length)));
    }
}
