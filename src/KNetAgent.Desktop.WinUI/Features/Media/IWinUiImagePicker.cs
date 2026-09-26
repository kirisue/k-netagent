using TestAgent.Core;

namespace KNetAgent.Desktop.WinUI.Features.Media;

/// <summary>WinUI image picker boundary. The owner HWND is mandatory for unpackaged desktop apps.</summary>
public interface IWinUiImagePicker
{
    Task<ImageInput?> PickAsync(nint ownerWindow, CancellationToken cancellationToken = default);
}

public sealed record PendingImageInfo(
    string MimeType,
    string Sha256,
    int Width,
    int Height,
    int ByteLength,
    string SourceLabel);

public sealed record ImageSendConfirmationRequest(
    string Target,
    string Model,
    int Width,
    int Height,
    int ByteLength,
    int MaxModelRequests);

public interface IImageConfirmationPrompt
{
    void Attach(Microsoft.UI.Xaml.XamlRoot xamlRoot);
    void Detach();
    Task<bool> ConfirmAsync(ImageSendConfirmationRequest request,
        CancellationToken cancellationToken = default);
}
