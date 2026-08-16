using System.IO;
using System.Security.Cryptography;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TestAgent.Core;
using TestAgent.Infrastructure;

namespace TestAgent.Desktop;

public sealed class WpfImageInputService : IImageInputService
{
    public async Task<ImageInput> LoadAsync(string filePath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(filePath)) throw new InvalidDataException("Select a PNG or JPEG image.");
        string fullPath;
        try { fullPath = Path.GetFullPath(filePath); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        { throw new InvalidDataException("The selected image path is invalid.", ex); }
        if (!File.Exists(fullPath)) throw new FileNotFoundException("The selected image no longer exists.");
        if ((File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Linked image files are not accepted.");

        byte[]? source = null;
        try
        {
            await using (var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                             64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                if (stream.Length is <= 0 or > ImageInputPreflight.MaxSourceBytes)
                    throw new InvalidDataException("The selected image must be between 1 byte and 10 MB.");
                source = new byte[checked((int)stream.Length)];
                var offset = 0;
                while (offset < source.Length)
                {
                    var read = await stream.ReadAsync(source.AsMemory(offset), ct);
                    if (read == 0) throw new EndOfStreamException("The selected image changed while it was being read.");
                    offset += read;
                }
                if (stream.ReadByte() != -1)
                    throw new InvalidDataException("The selected image changed while it was being read.");
            }

            return WpfImageSanitizer.SanitizeOwned(source);
        }
        finally
        {
            if (source is { Length: > 0 }) CryptographicOperations.ZeroMemory(source);
        }
    }
}

internal static class WpfImageSanitizer
{
    public static ImageInput SanitizeOwned(byte[] source, string? requiredMimeType = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        try
        {
            var header = ImageInputPreflight.Inspect(source);
            if (requiredMimeType is not null &&
                !header.MimeType.Equals(requiredMimeType, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"The image must be encoded as {requiredMimeType}.");
            using var input = new MemoryStream(source, writable: false);
            var decoder = BitmapDecoder.Create(input, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            if (decoder.Frames.Count != 1) throw new InvalidDataException("Animated or multi-frame images are not accepted.");
            var frame = decoder.Frames[0];
            if (frame.PixelWidth != header.Width || frame.PixelHeight != header.Height)
                throw new InvalidDataException("Image dimensions do not match the encoded header.");
            var oriented = ApplyExifOrientation(frame);
            BitmapSource pixels;
            BitmapEncoder encoder;
            if (header.MimeType == "image/jpeg")
            {
                pixels = new FormatConvertedBitmap(oriented, PixelFormats.Bgr24, null, 0);
                encoder = new JpegBitmapEncoder { QualityLevel = 90 };
            }
            else
            {
                pixels = new FormatConvertedBitmap(oriented, PixelFormats.Bgra32, null, 0);
                encoder = new PngBitmapEncoder();
            }
            pixels.Freeze();
            encoder.Frames.Add(BitmapFrame.Create(pixels, null, null, null));
            using var sanitized = new MemoryStream();
            try
            {
                encoder.Save(sanitized);
                if (sanitized.Length is <= 0 or > ImageInputPreflight.MaxSourceBytes)
                    throw new InvalidDataException("The sanitized image exceeds the 10 MB request limit.");
                var data = sanitized.ToArray();
                return new(header.MimeType, data, Convert.ToHexString(SHA256.HashData(data)),
                    pixels.PixelWidth, pixels.PixelHeight);
            }
            finally
            {
                if (sanitized.TryGetBuffer(out var buffer) && buffer.Array is { } array && sanitized.Length > 0)
                    CryptographicOperations.ZeroMemory(array.AsSpan(buffer.Offset, checked((int)sanitized.Length)));
            }
        }
        catch (InvalidDataException) { throw; }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or ArgumentException)
        { throw new InvalidDataException("The selected file is not a valid supported PNG or JPEG image.", ex); }
        finally
        {
            if (source.Length > 0) CryptographicOperations.ZeroMemory(source);
        }
    }

    private static BitmapSource ApplyExifOrientation(BitmapFrame frame)
    {
        var orientation = 1;
        try
        {
            if (frame.Metadata is BitmapMetadata metadata &&
                metadata.GetQuery("/app1/ifd/{ushort=274}") is { } value)
                orientation = Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is NotSupportedException or ArgumentException or FormatException or OverflowException)
        {
            orientation = 1;
        }

        Transform? transform = orientation switch
        {
            2 => new ScaleTransform(-1, 1),
            3 => new RotateTransform(180),
            4 => new ScaleTransform(1, -1),
            5 => Group(new ScaleTransform(-1, 1), new RotateTransform(90)),
            6 => new RotateTransform(90),
            7 => Group(new ScaleTransform(-1, 1), new RotateTransform(270)),
            8 => new RotateTransform(270),
            _ => null
        };
        if (transform is null) return frame;
        transform.Freeze();
        var oriented = new TransformedBitmap(frame, transform);
        oriented.Freeze();
        return oriented;
    }

    private static TransformGroup Group(params Transform[] transforms)
    {
        var group = new TransformGroup();
        foreach (var transform in transforms) group.Children.Add(transform);
        return group;
    }
}
