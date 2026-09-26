using System.Net;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Security.Cryptography;
using System.Text;
using KNetAgent.Desktop.WinUI.Features.Media;
using KNetAgent.Desktop.WinUI.Services;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using TestAgent.Core;
using TestAgent.Infrastructure;
using Windows.Storage.Streams;

namespace KNetAgent.Desktop.WinUI.Browser;

/// <summary>
/// Visible, read-only browser for sanitized snapshots. Remote HTML never reaches WebView2;
/// only text returned by ISafeWebContentReader is encoded into a local about:blank page.
/// </summary>
public sealed class WinUiReadOnlyBrowserSession : IReadOnlyBrowserSession, IDisposable
{
    private readonly ISafeWebContentReader _reader;
    private readonly AppPaths _paths;
    private readonly IUiDispatcher _dispatcher;
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

    public WinUiReadOnlyBrowserSession(ISafeWebContentReader reader, AppPaths paths,
        IUiDispatcher dispatcher)
    {
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
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

    public void Attach(WebView2 webView)
    {
        ArgumentNullException.ThrowIfNull(webView);
        if (!_dispatcher.HasThreadAccess)
            throw new InvalidOperationException("Attach the browser surface on the WinUI thread.");
        lock (_stateGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_webView is not null && !ReferenceEquals(_webView, webView))
                throw new InvalidOperationException("A browser surface is already attached.");
            _webView = webView;
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
            var page = await _reader.ReadAsync(url, maxChars, ct);
            var document = BrowserSnapshotFormatter.CreateDocument(page);
            var html = BrowserSnapshotFormatter.CreateHtml(document);
            InvalidateDisplayedPage();
            try
            {
                await OnUiAsync(async webView =>
                {
                    await EnsureReadyOnUiAsync(webView);
                    await NavigateLocalSnapshotOnUiAsync(webView, html, ct);
                    return true;
                }, ct);
            }
            catch
            {
                InvalidateDisplayedPage();
                throw;
            }
            lock (_stateGate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                _current = document;
                _displayReady = true;
            }
            RaiseChanged();
            return document;
        }
        finally { _operationGate.Release(); }
    }

    public Task<BrowserDomSnapshot> ReadDomAsync(int maxChars = 30_000,
        CancellationToken ct = default)
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

            var rawPng = await OnUiAsync(async webView =>
            {
                await EnsureReadyOnUiAsync(webView);
                using var output = new InMemoryRandomAccessStream();
                await webView.CoreWebView2.CapturePreviewAsync(
                    CoreWebView2CapturePreviewImageFormat.Png, output).AsTask(ct);
                if (output.Size is 0 or > ImageInputPreflight.MaxSourceBytes)
                    throw new InvalidDataException("The browser viewport capture must be between 1 byte and 10 MB.");
                var bytes = new byte[checked((int)output.Size)];
                using var reader = new DataReader(output.GetInputStreamAt(0));
                await reader.LoadAsync((uint)bytes.Length).AsTask(ct);
                reader.ReadBytes(bytes);
                return bytes;
            }, ct);

            ImageInput sanitized;
            try { sanitized = await WinUiImageSanitizer.SanitizeOwnedAsync(rawPng, ct, "image/png"); }
            finally { WinUiImageInputService.Zero(rawPng); }

            ImageInput? replaced;
            try
            {
                lock (_stateGate)
                {
                    ObjectDisposedException.ThrowIf(_disposed, this);
                    if (!_displayReady || !ReferenceEquals(_current, document))
                        throw new InvalidOperationException("The displayed snapshot changed before capture completed.");
                    replaced = _latestCapture;
                    _latestCapture = sanitized;
                }
            }
            catch { Zero(sanitized); throw; }
            Zero(replaced);
            RaiseChanged();
            return new(document.Url, document.Title, sanitized.Width, sanitized.Height,
                DateTimeOffset.UtcNow);
        }
        finally { _operationGate.Release(); }
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

    private async Task EnsureReadyOnUiAsync(WebView2 webView)
    {
        if (_configured) return;
        if (!_dispatcher.HasThreadAccess)
            throw new InvalidOperationException("WebView2 initialization requires the WinUI thread.");

        var profileRoot = Path.GetFullPath(_paths.BrowserProfile);
        Directory.CreateDirectory(profileRoot);
        if ((File.GetAttributes(profileRoot) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("The browser profile root cannot be a linked directory.");
        _profilePath ??= Path.Combine(profileRoot,
            $"winui-ephemeral-{Environment.ProcessId}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_profilePath);
        if ((File.GetAttributes(_profilePath) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("The browser profile directory cannot be linked.");

        _environment ??= await CoreWebView2Environment.CreateWithOptionsAsync(
            null, _profilePath, new CoreWebView2EnvironmentOptions());
        await webView.EnsureCoreWebView2Async(_environment);

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
        void OnCompleted(CoreWebView2? sender, CoreWebView2NavigationCompletedEventArgs args)
        {
            if (args.IsSuccess) completed.TrySetResult();
            else completed.TrySetException(new InvalidOperationException(
                $"Local snapshot rendering failed ({args.WebErrorStatus})."));
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
            webView = _webView ?? throw new InvalidOperationException(
                "Attach a visible WebView2 surface before opening or capturing a snapshot.");
        if (_dispatcher.HasThreadAccess) return action(webView);

        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _dispatcher.Post(async () =>
        {
            try
            {
                ct.ThrowIfCancellationRequested();
                completion.TrySetResult(await action(webView));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            { completion.TrySetCanceled(ct); }
            catch (Exception ex) { completion.TrySetException(ex); }
        });
        return completion.Task;
    }

    private void BlockExternalResource(CoreWebView2? sender, CoreWebView2WebResourceRequestedEventArgs args)
    {
        if (_environment is null) return;
        args.Response = _environment.CreateWebResourceResponse(
            new InMemoryRandomAccessStream(), 403, "Forbidden",
            "Content-Type: text/plain; charset=utf-8");
    }

    private void BlockNonSnapshotNavigation(CoreWebView2? sender, CoreWebView2NavigationStartingEventArgs args)
    {
        if (!_allowLocalNavigation ||
            !args.Uri.Equals("about:blank", StringComparison.OrdinalIgnoreCase))
            args.Cancel = true;
    }

    private static void BlockNewWindow(CoreWebView2? sender, CoreWebView2NewWindowRequestedEventArgs args) =>
        args.Handled = true;
    private static void DenyPermission(CoreWebView2? sender, CoreWebView2PermissionRequestedEventArgs args) =>
        args.State = CoreWebView2PermissionState.Deny;
    private static void CancelDownload(CoreWebView2? sender, CoreWebView2DownloadStartingEventArgs args)
    { args.Cancel = true; args.Handled = true; }
    private static void CancelBasicAuthentication(CoreWebView2? sender,
        CoreWebView2BasicAuthenticationRequestedEventArgs args) => args.Cancel = true;
    private static void CancelClientCertificate(CoreWebView2? sender,
        CoreWebView2ClientCertificateRequestedEventArgs args)
    { args.Handled = true; args.SelectedCertificate = null; }

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

    private void RaiseChanged()
    {
        var changed = Changed;
        if (changed is null) return;
        if (_dispatcher.HasThreadAccess) changed();
        else _dispatcher.Post(changed);
    }

    public void Dispose()
    {
        WebView2? webView;
        ImageInput? capture;
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
        }
        Zero(capture);
        if (webView is not null)
        {
            void CloseAndClean()
            {
                try
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
                    webView.Close();
                }
                catch (InvalidOperationException) { }
                DeleteEphemeralProfile(_profilePath);
            }
            if (_dispatcher.HasThreadAccess) CloseAndClean();
            else _dispatcher.Post(CloseAndClean);
        }
        else DeleteEphemeralProfile(_profilePath);
        GC.SuppressFinalize(this);
    }

    private void DeleteEphemeralProfile(string? profilePath)
    {
        if (string.IsNullOrWhiteSpace(profilePath)) return;
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_paths.BrowserProfile));
        var candidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(profilePath));
        if (!candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(candidate).StartsWith("winui-ephemeral-", StringComparison.Ordinal) ||
            !Directory.Exists(candidate)) return;
        try
        {
            if ((File.GetAttributes(candidate) & FileAttributes.ReparsePoint) == 0)
                Directory.Delete(candidate, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static void Zero(ImageInput? image)
    {
        if (image?.Data is { Length: > 0 } bytes) CryptographicOperations.ZeroMemory(bytes);
    }
}

internal static class BrowserSnapshotFormatter
{
    public static BrowserPageDocument CreateDocument(WebContentResult page)
    {
        var capturedAt = DateTimeOffset.UtcNow;
        var elements = new List<BrowserDomElement>();
        foreach (var heading in page.Headings?.Take(40) ?? [])
        {
            var separator = heading.IndexOf(':');
            var kind = separator is > 0 and <= 2 ? heading[..separator] : "heading";
            var text = separator is > 0 and <= 2 ? heading[(separator + 1)..].Trim() : heading;
            elements.Add(new(kind, text));
        }
        if (page.Links is not null)
            elements.AddRange(page.Links.Take(40).Select(link =>
                new BrowserDomElement("link", link.Text, link.Url)));
        var title = string.IsNullOrWhiteSpace(page.Title) ? "Safe web snapshot" : page.Title;
        var dom = new BrowserDomSnapshot(page.FinalUrl, title, page.Text, elements,
            page.Truncated, capturedAt);
        return new(page.FinalUrl, title, dom, capturedAt);
    }

    public static string CreateHtml(BrowserPageDocument page)
    {
        static string E(string value) => WebUtility.HtmlEncode(value);
        var html = new StringBuilder(checked(page.Dom.Text.Length + 8_192));
        html.Append("<!doctype html><html><head><meta charset=\"utf-8\">")
            .Append("<meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; script-src 'none'; style-src 'unsafe-inline'; img-src data:; font-src 'none'; connect-src 'none'; media-src 'none'; object-src 'none'; frame-src 'none'; worker-src 'none'; manifest-src 'none'; base-uri 'none'; form-action 'none'\">")
            .Append("<meta name=\"referrer\" content=\"no-referrer\"><title>").Append(E(page.Title))
            .Append("</title><style>html{color-scheme:dark}body{margin:0 auto;max-width:1050px;padding:28px;font:15px/1.65 'Segoe UI',sans-serif;background:#17191c;color:#e7e9ec}header{border-bottom:1px solid #3a3d43;margin-bottom:24px;padding-bottom:16px}h1{font-size:22px}.url,.notice{color:#9ca7b5;overflow-wrap:anywhere}.notice{font-size:13px}pre{white-space:pre-wrap;overflow-wrap:anywhere;font:14px/1.65 'Segoe UI',sans-serif}.structure{margin-top:28px;border-top:1px solid #3a3d43;padding-top:16px}.target{display:block;color:#8ab4f8;font:12px/1.4 Consolas,monospace;overflow-wrap:anywhere}</style></head><body><header><h1>")
            .Append(E(page.Title)).Append("</h1><div class=\"url\">").Append(E(page.Url))
            .Append("</div><div class=\"notice\">Read-only local snapshot. Remote HTML, scripts, forms, cookies, and external resources are not loaded.</div></header><main><pre>")
            .Append(E(page.Dom.Text)).Append("</pre>");
        if (page.Dom.Elements.Count > 0)
        {
            html.Append("<section class=\"structure\"><h2>Page structure</h2>");
            foreach (var element in page.Dom.Elements.Take(80))
            {
                html.Append("<div><strong>").Append(E(element.Kind)).Append(":</strong> ")
                    .Append(E(element.Text));
                if (!string.IsNullOrWhiteSpace(element.Target))
                    html.Append("<span class=\"target\">").Append(E(element.Target)).Append("</span>");
                html.Append("</div>");
            }
            html.Append("</section>");
        }
        return html.Append("</main></body></html>").ToString();
    }
}
