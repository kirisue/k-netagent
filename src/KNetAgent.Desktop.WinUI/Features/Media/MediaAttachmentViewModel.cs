using System.Security.Cryptography;
using KNetAgent.Desktop.WinUI.ViewModels;
using TestAgent.Core;

namespace KNetAgent.Desktop.WinUI.Features.Media;

/// <summary>Owns one sanitized pending image and never exposes its mutable byte array.</summary>
public sealed class MediaAttachmentViewModel : ObservableObject, IDisposable
{
    private readonly IWinUiImagePicker _picker;
    private readonly IImageConfirmationPrompt _confirmation;
    private readonly object _gate = new();
    private ImageInput? _pending;
    private PendingImageInfo? _pendingInfo;
    private string _status = "未附加图片";
    private bool _busy;
    private bool _disposed;

    public MediaAttachmentViewModel(IWinUiImagePicker picker, IImageConfirmationPrompt confirmation)
    {
        _picker = picker ?? throw new ArgumentNullException(nameof(picker));
        _confirmation = confirmation ?? throw new ArgumentNullException(nameof(confirmation));
        PickCommand = new AsyncRelayCommand(() => Task.CompletedTask);
        ClearCommand = new RelayCommand(Clear, () => HasPendingImage && !Busy);
    }

    /// <summary>The root replaces this placeholder with PickAsync(owner HWND), or binds in the control.</summary>
    public AsyncRelayCommand PickCommand { get; }
    public RelayCommand ClearCommand { get; }

    public PendingImageInfo? PendingImage
    {
        get => _pendingInfo;
        private set
        {
            if (SetProperty(ref _pendingInfo, value))
            {
                OnPropertyChanged(nameof(HasPendingImage));
                OnPropertyChanged(nameof(PendingImageSummary));
                ClearCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool HasPendingImage => PendingImage is not null;
    public string PendingImageSummary => PendingImage is { } image
        ? $"{image.Width}×{image.Height} · {image.ByteLength / 1024d:F1} KB · 已清除元数据"
        : "";

    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public bool Busy
    {
        get => _busy;
        private set
        {
            if (SetProperty(ref _busy, value)) ClearCommand.RaiseCanExecuteChanged();
        }
    }

    public async Task PickAsync(nint ownerWindow, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Busy) return;
        Busy = true;
        try
        {
            var image = await _picker.PickAsync(ownerWindow, cancellationToken);
            if (image is null) return;
            Replace(image, "本地图片");
            Status = "图片已净化；发送前仍会再次确认";
        }
        catch
        {
            Status = "图片附加失败";
            throw;
        }
        finally { Busy = false; }
    }

    public void AttachConfirmationRoot(Microsoft.UI.Xaml.XamlRoot xamlRoot) =>
        _confirmation.Attach(xamlRoot);

    public void DetachConfirmationRoot() => _confirmation.Detach();

    /// <summary>Moves a sanitized browser capture into the same explicit-confirmation path.</summary>
    public void AttachBrowserCapture(ImageInput image)
    {
        ArgumentNullException.ThrowIfNull(image);
        ObjectDisposedException.ThrowIf(_disposed, this);
        Replace(Clone(image), "只读浏览器截图");
        Status = "浏览器截图已附加；发送前仍会再次确认";
    }

    public async Task<ImageRunLease?> PrepareRunAsync(ProviderSettings provider,
        AgentRunOptions baseOptions, CancellationToken cancellationToken = default,
        int maxModelRequests = 1)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(baseOptions);
        ObjectDisposedException.ThrowIf(_disposed, this);

        ImageInput? snapshot;
        lock (_gate) snapshot = _pending is null ? null : Clone(_pending);
        if (snapshot is null) return new ImageRunLease(baseOptions, null);

        try
        {
            if (!provider.SupportsImageInput)
                throw new InvalidOperationException("当前模型未启用图片输入。请先在设置中确认模型能力。");
            var target = ValidateAndFormatTarget(provider.Endpoint);
            var approved = await _confirmation.ConfirmAsync(new(
                target, provider.Model, snapshot.Width, snapshot.Height,
                snapshot.Data.Length, Math.Clamp(maxModelRequests, 1, 20)), cancellationToken);
            if (!approved)
            {
                Zero(snapshot);
                return null;
            }

            ImageInput? detached;
            lock (_gate)
            {
                if (_pending is null ||
                    !_pending.Sha256.Equals(snapshot.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("The attached image changed before sending.");
                detached = _pending;
                _pending = null;
            }
            Zero(detached);
            PendingImage = null;
            Status = "图片将在本轮结束后从内存清除";
            return new ImageRunLease(baseOptions with { Images = [snapshot] }, snapshot);
        }
        catch
        {
            Zero(snapshot);
            throw;
        }
    }

    public void Clear()
    {
        ImageInput? image;
        lock (_gate)
        {
            image = _pending;
            _pending = null;
        }
        Zero(image);
        PendingImage = null;
        Status = "已清除图片";
    }

    private void Replace(ImageInput image, string sourceLabel)
    {
        Validate(image);
        ImageInput? replaced;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            replaced = _pending;
            _pending = image;
        }
        Zero(replaced);
        PendingImage = new(image.MimeType, image.Sha256, image.Width, image.Height,
            image.Data.Length, sourceLabel);
    }

    private static ImageInput Clone(ImageInput image) => image with { Data = image.Data.ToArray() };

    private static string ValidateAndFormatTarget(string endpoint)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) ||
            uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0 ||
            !(uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
              uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) && uri.IsLoopback))
            throw new InvalidOperationException(
                "Images may only be sent to an HTTPS model endpoint or a loopback HTTP endpoint without credentials, query, or fragment.");
        return endpoint.TrimEnd('/') + "/chat/completions";
    }

    private static void Validate(ImageInput image)
    {
        if (image.Data is not { Length: > 0 } || image.Data.Length > 10 * 1024 * 1024)
            throw new InvalidDataException("The sanitized image must be between 1 byte and 10 MB.");
        if (image.Width <= 0 || image.Height <= 0 || (long)image.Width * image.Height > 20_000_000)
            throw new InvalidDataException("The sanitized image dimensions are invalid.");
        var hash = Convert.ToHexString(SHA256.HashData(image.Data));
        if (!hash.Equals(image.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The sanitized image hash does not match its pixels.");
    }

    private static void Zero(ImageInput? image)
    {
        if (image?.Data is { Length: > 0 } bytes) CryptographicOperations.ZeroMemory(bytes);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Clear();
        GC.SuppressFinalize(this);
    }
}

public sealed class ImageRunLease : IDisposable
{
    private ImageInput? _ownedImage;
    internal ImageRunLease(AgentRunOptions runOptions, ImageInput? ownedImage)
    {
        RunOptions = runOptions;
        _ownedImage = ownedImage;
    }

    public AgentRunOptions RunOptions { get; }

    public void Dispose()
    {
        var image = Interlocked.Exchange(ref _ownedImage, null);
        if (image?.Data is { Length: > 0 } bytes) CryptographicOperations.ZeroMemory(bytes);
        GC.SuppressFinalize(this);
    }
}
