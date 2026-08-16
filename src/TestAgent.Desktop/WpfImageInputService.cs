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

        byte[] source;
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

        try
        {
            var header = ImageInputPreflight.Inspect(source);
            using var input = new MemoryStream(source, writable: false);
            var decoder = BitmapDecoder.Create(input, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            if (decoder.Frames.Count != 1) throw new InvalidDataException("Animated or multi-frame images are not accepted.");
            var frame = decoder.Frames[0];
            if (frame.PixelWidth != header.Width || frame.PixelHeight != header.Height)
                throw new InvalidDataException("Image dimensions do not match the encoded header.");
            BitmapSource pixels;
            BitmapEncoder encoder;
            if (header.MimeType == "image/jpeg")
            {
                pixels = new FormatConvertedBitmap(frame, PixelFormats.Bgr24, null, 0);
                encoder = new JpegBitmapEncoder { QualityLevel = 90 };
            }
            else
            {
                pixels = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
                encoder = new PngBitmapEncoder();
            }
            pixels.Freeze();
            encoder.Frames.Add(BitmapFrame.Create(pixels, null, null, null));
            using var sanitized = new MemoryStream();encoder.Save(sanitized);
            if (sanitized.Length is <= 0 or > ImageInputPreflight.MaxSourceBytes)
                throw new InvalidDataException("The sanitized image exceeds the 10 MB request limit.");
            var data = sanitized.ToArray();
            return new(header.MimeType,data,Convert.ToHexString(SHA256.HashData(data)),header.Width,header.Height);
        }
        catch(InvalidDataException){throw;}
        catch(Exception ex) when(ex is NotSupportedException or FileFormatException or ArgumentException)
        {throw new InvalidDataException("The selected file is not a valid supported PNG or JPEG image.",ex);}
        finally{CryptographicOperations.ZeroMemory(source);}
    }
}
