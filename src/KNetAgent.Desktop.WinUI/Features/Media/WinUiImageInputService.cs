using System.Runtime.InteropServices.WindowsRuntime;
using System.Security.Cryptography;
using TestAgent.Core;
using TestAgent.Infrastructure;
using Windows.Graphics.Imaging;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using WinRT.Interop;

namespace KNetAgent.Desktop.WinUI.Features.Media;

/// <summary>
/// Loads only bounded PNG/JPEG files, validates magic bytes before decoding, and re-encodes
/// visible pixels as PNG. The returned image contains no original filename or metadata.
/// </summary>
public sealed class WinUiImageInputService : IImageInputService, IWinUiImagePicker
{
    public async Task<ImageInput?> PickAsync(nint ownerWindow,
        CancellationToken cancellationToken = default)
    {
        if (ownerWindow == 0)
            throw new ArgumentException("A valid owner window handle is required.", nameof(ownerWindow));

        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.PicturesLibrary };
        picker.FileTypeFilter.Add(".png");
        picker.FileTypeFilter.Add(".jpg");
        picker.FileTypeFilter.Add(".jpeg");
        InitializeWithWindow.Initialize(picker, ownerWindow);
        var file = await picker.PickSingleFileAsync().AsTask(cancellationToken);
        return file is null ? null : await LoadAsync(file.Path, cancellationToken);
    }

    public async Task<ImageInput> LoadAsync(string filePath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            throw new InvalidDataException("Select a PNG or JPEG image.");

        string fullPath;
        try { fullPath = Path.GetFullPath(filePath); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        { throw new InvalidDataException("The selected image path is invalid.", ex); }

        if (!File.Exists(fullPath))
            throw new FileNotFoundException("The selected image no longer exists.", fullPath);
        if ((File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Linked image files are not accepted.");

        byte[]? source = null;
        try
        {
            await using var file = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (file.Length is <= 0 or > ImageInputPreflight.MaxSourceBytes)
                throw new InvalidDataException("The selected image must be between 1 byte and 10 MB.");

            source = new byte[checked((int)file.Length)];
            await file.ReadExactlyAsync(source, cancellationToken);
            if (file.ReadByte() != -1)
                throw new InvalidDataException("The selected image changed while it was being read.");
            return await WinUiImageSanitizer.SanitizeOwnedAsync(source, cancellationToken);
        }
        finally
        {
            Zero(source);
        }
    }

    internal static void Zero(byte[]? bytes)
    {
        if (bytes is { Length: > 0 }) CryptographicOperations.ZeroMemory(bytes);
    }
}

internal static class WinUiImageSanitizer
{
    public static async Task<ImageInput> SanitizeOwnedAsync(byte[] source,
        CancellationToken cancellationToken = default, string? requiredMimeType = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        byte[]? pixels = null;
        byte[]? encoded = null;
        try
        {
            if (source.Length is <= 0 or > ImageInputPreflight.MaxSourceBytes)
                throw new InvalidDataException("The image must be between 1 byte and 10 MB.");
            var header = ImageInputPreflight.Inspect(source);
            if (requiredMimeType is not null &&
                !header.MimeType.Equals(requiredMimeType, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"The image must be encoded as {requiredMimeType}.");

            using var input = new InMemoryRandomAccessStream();
            await input.WriteAsync(source.AsBuffer()).AsTask(cancellationToken);
            input.Seek(0);
            var decoder = await BitmapDecoder.CreateAsync(input).AsTask(cancellationToken);
            if (decoder.FrameCount != 1)
                throw new InvalidDataException("Animated or multi-frame images are not accepted.");
            if (decoder.PixelWidth != (uint)header.Width || decoder.PixelHeight != (uint)header.Height)
                throw new InvalidDataException("Image dimensions do not match the encoded header.");

            var width = checked((int)decoder.OrientedPixelWidth);
            var height = checked((int)decoder.OrientedPixelHeight);
            if (width <= 0 || height <= 0 || (long)width * height > ImageInputPreflight.MaxPixels)
                throw new InvalidDataException("The oriented image dimensions exceed 20 megapixels.");

            var provider = await decoder.GetPixelDataAsync(
                    BitmapPixelFormat.Bgra8,
                    BitmapAlphaMode.Premultiplied,
                    new BitmapTransform(),
                    ExifOrientationMode.RespectExifOrientation,
                    ColorManagementMode.ColorManageToSRgb)
                .AsTask(cancellationToken);
            pixels = provider.DetachPixelData();

            using var output = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, output)
                .AsTask(cancellationToken);
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
                (uint)width, (uint)height,
                double.IsFinite(decoder.DpiX) && decoder.DpiX > 0 ? decoder.DpiX : 96,
                double.IsFinite(decoder.DpiY) && decoder.DpiY > 0 ? decoder.DpiY : 96,
                pixels);
            await encoder.FlushAsync().AsTask(cancellationToken);

            if (output.Size is 0 or > ImageInputPreflight.MaxSourceBytes)
                throw new InvalidDataException("The sanitized image exceeds the 10 MB request limit.");
            output.Seek(0);
            encoded = new byte[checked((int)output.Size)];
            using (var reader = new DataReader(output.GetInputStreamAt(0)))
            {
                await reader.LoadAsync((uint)encoded.Length).AsTask(cancellationToken);
                reader.ReadBytes(encoded);
            }

            var result = new ImageInput("image/png", encoded,
                Convert.ToHexString(SHA256.HashData(encoded)), width, height);
            encoded = null; // ownership transferred to the ImageInput
            return result;
        }
        catch (InvalidDataException) { throw; }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or OverflowException)
        {
            throw new InvalidDataException("The selected file is not a valid supported PNG or JPEG image.", ex);
        }
        finally
        {
            WinUiImageInputService.Zero(source);
            WinUiImageInputService.Zero(pixels);
            WinUiImageInputService.Zero(encoded);
        }
    }
}
