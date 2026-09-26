using System.Xml.Linq;
using Xunit;

namespace TestAgent.Tests;

/// <summary>
/// Headless source gates for the WinUI feature slices. The WinUI project build remains
/// the authoritative XAML and WinRT projection check.
/// </summary>
public sealed class WinUiMediaBrowserStructureTests
{
    private static readonly string Root = FindRepositoryRoot();

    [Fact]
    public void Image_picker_uses_hwnd_and_sanitizes_bounded_magic_checked_pixels()
    {
        var picker = Read("src", "KNetAgent.Desktop.WinUI", "Features", "Media",
            "WinUiImageInputService.cs");

        Assert.Contains("InitializeWithWindow.Initialize(picker, ownerWindow)", picker,
            StringComparison.Ordinal);
        Assert.Contains("ImageInputPreflight.MaxSourceBytes", picker, StringComparison.Ordinal);
        Assert.Contains("ImageInputPreflight.Inspect(source)", picker, StringComparison.Ordinal);
        Assert.Contains("ImageInputPreflight.MaxPixels", picker, StringComparison.Ordinal);
        Assert.Contains("ExifOrientationMode.RespectExifOrientation", picker, StringComparison.Ordinal);
        Assert.Contains("BitmapEncoder.PngEncoderId", picker, StringComparison.Ordinal);
        Assert.Contains("CryptographicOperations.ZeroMemory", picker, StringComparison.Ordinal);
        Assert.DoesNotContain("Path.GetFileName(fullPath)", picker, StringComparison.Ordinal);
    }

    [Fact]
    public void Pending_image_requires_confirmation_and_is_zeroed_on_reject_or_lease_dispose()
    {
        var media = Read("src", "KNetAgent.Desktop.WinUI", "Features", "Media",
            "MediaAttachmentViewModel.cs");
        var prompt = Read("src", "KNetAgent.Desktop.WinUI", "Features", "Media",
            "WinUiImageConfirmationPrompt.cs");

        Assert.Contains("ConfirmAsync", media, StringComparison.Ordinal);
        Assert.Contains("if (!approved)", media, StringComparison.Ordinal);
        Assert.Contains("Zero(snapshot);", media, StringComparison.Ordinal);
        Assert.Contains("baseOptions with { Images = [snapshot] }", media, StringComparison.Ordinal);
        Assert.Contains("sealed class ImageRunLease : IDisposable", media, StringComparison.Ordinal);
        Assert.Contains("Interlocked.Exchange(ref _ownedImage, null)", media, StringComparison.Ordinal);
        Assert.Contains("uri.UserInfo.Length > 0", media, StringComparison.Ordinal);
        Assert.Contains("uri.Query.Length > 0", media, StringComparison.Ordinal);
        Assert.Contains("uri.Fragment.Length > 0", media, StringComparison.Ordinal);
        Assert.Contains("/chat/completions", media, StringComparison.Ordinal);
        Assert.Contains("DefaultButton = ContentDialogButton.Close", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void Visible_browser_renders_only_safe_reader_text_and_blocks_active_capabilities()
    {
        var browser = Read("src", "KNetAgent.Desktop.WinUI", "Browser",
            "WinUiReadOnlyBrowserSession.cs");

        Assert.Contains("ISafeWebContentReader", browser, StringComparison.Ordinal);
        Assert.Contains("_reader.ReadAsync", browser, StringComparison.Ordinal);
        Assert.Contains("WebUtility.HtmlEncode", browser, StringComparison.Ordinal);
        Assert.Contains("NavigateToString(html)", browser, StringComparison.Ordinal);
        Assert.Contains("default-src 'none'", browser, StringComparison.Ordinal);
        Assert.Contains("settings.IsScriptEnabled = false", browser, StringComparison.Ordinal);
        Assert.Contains("WebResourceRequested += BlockExternalResource", browser, StringComparison.Ordinal);
        Assert.Contains("NavigationStarting += BlockNonSnapshotNavigation", browser,
            StringComparison.Ordinal);
        Assert.Contains("PermissionRequested += DenyPermission", browser, StringComparison.Ordinal);
        Assert.Contains("DownloadStarting += CancelDownload", browser, StringComparison.Ordinal);
        Assert.DoesNotContain("webView.Source =", browser, StringComparison.Ordinal);
    }

    [Fact]
    public void Browser_ui_routes_open_dom_and_capture_through_approved_tool_execution()
    {
        var viewModel = Read("src", "KNetAgent.Desktop.WinUI", "Browser",
            "BrowserFeatureViewModel.cs");
        var control = Read("src", "KNetAgent.Desktop.WinUI", "Browser",
            "ReadOnlyBrowserControl.xaml.cs");

        Assert.Contains("IToolExecutionService", viewModel, StringComparison.Ordinal);
        Assert.Contains("_toolExecution.ExecuteAsync(request, observer", viewModel,
            StringComparison.Ordinal);
        Assert.Contains("\"open_browser_snapshot\"", viewModel, StringComparison.Ordinal);
        Assert.Contains("\"read_browser_dom\"", viewModel, StringComparison.Ordinal);
        Assert.Contains("\"capture_browser_viewport\"", viewModel, StringComparison.Ordinal);
        Assert.DoesNotContain("_browser.OpenSnapshotAsync", viewModel, StringComparison.Ordinal);
        Assert.DoesNotContain("_browser.ReadDomAsync", viewModel, StringComparison.Ordinal);
        Assert.DoesNotContain("_browser.CaptureViewportAsync", viewModel, StringComparison.Ordinal);
        Assert.Contains("IAgentObserver observer", control, StringComparison.Ordinal);
        Assert.Contains("viewModel.Initialize(observer, sessionIdAccessor)", control,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Media_and_browser_user_controls_are_embeddable_real_surfaces()
    {
        var media = XDocument.Parse(Read("src", "KNetAgent.Desktop.WinUI", "Features", "Media",
            "MediaAttachmentControl.xaml"));
        var browser = XDocument.Parse(Read("src", "KNetAgent.Desktop.WinUI", "Browser",
            "ReadOnlyBrowserControl.xaml"));

        Assert.Equal("UserControl", media.Root?.Name.LocalName);
        Assert.Contains(media.Descendants(), element => element.Name.LocalName == "Button" &&
            string.Equals((string?)element.Attribute("Content"), "选择图片", StringComparison.Ordinal));
        Assert.Equal("UserControl", browser.Root?.Name.LocalName);
        Assert.Contains(browser.Descendants(), element => element.Name.LocalName == "WebView2");
        Assert.Contains(browser.Descendants(), element => element.Name.LocalName == "Button" &&
            string.Equals((string?)element.Attribute("Content"), "截取并附加", StringComparison.Ordinal));
    }

    [Fact]
    public void Feature_has_one_composition_root_registration_entrypoint()
    {
        var source = Read("src", "KNetAgent.Desktop.WinUI", "Features", "Media",
            "MediaBrowserServiceCollectionExtensions.cs");
        Assert.Contains("AddWinUiMediaAndBrowser", source, StringComparison.Ordinal);
        Assert.Contains("IImageInputService", source, StringComparison.Ordinal);
        Assert.Contains("IReadOnlyBrowserSession", source, StringComparison.Ordinal);
        Assert.Contains("MediaAttachmentViewModel", source, StringComparison.Ordinal);
        Assert.Contains("BrowserFeatureViewModel", source, StringComparison.Ordinal);
        Assert.Contains("OpenBrowserSnapshotTool", source, StringComparison.Ordinal);
        Assert.Contains("ReadBrowserDomTool", source, StringComparison.Ordinal);
        Assert.Contains("CaptureBrowserViewportTool", source, StringComparison.Ordinal);
    }

    private static string Read(params string[] relativeParts) =>
        File.ReadAllText(Path.Combine([Root, .. relativeParts]));

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "TestAgent.slnx")))
                return directory.FullName;
        }
        throw new DirectoryNotFoundException("Could not locate TestAgent.slnx.");
    }
}
