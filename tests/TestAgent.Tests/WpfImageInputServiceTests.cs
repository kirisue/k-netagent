using System.Security.Cryptography;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using TestAgent.Desktop;
using Xunit;

namespace TestAgent.Tests;

public sealed class WpfImageInputServiceTests
{
    [Fact]
    public Task Png_is_reencoded_without_metadata_and_preserves_pixels_and_hash() => RunStaAsync(async () =>
    {
        const string marker = "KNET_PRIVATE_PNG_METADATA_72F0";
        var pixels = new byte[]
        {
            0, 0, 255, 255, 0, 255, 0, 255,
            255, 0, 0, 255, 255, 255, 255, 128
        };
        var path = CreateTempPath(".png");
        try
        {
            var metadata = new BitmapMetadata("png");
            metadata.SetQuery("/tEXt/{str=Description}", marker);
            Save(path, new PngBitmapEncoder(), CreateBitmap(2, 2, PixelFormats.Bgra32, pixels), metadata);
            Assert.Equal(marker, ReadMetadata(path, "/tEXt/{str=Description}"));

            var image = await new WpfImageInputService().LoadAsync(path);

            Assert.Equal("image/png", image.MimeType);
            Assert.Equal(2, image.Width);
            Assert.Equal(2, image.Height);
            Assert.Equal(Convert.ToHexString(SHA256.HashData(image.Data)), image.Sha256);
            Assert.DoesNotContain(marker, Encoding.Latin1.GetString(image.Data), StringComparison.Ordinal);
            Assert.Null(ReadMetadata(image.Data, "/tEXt/{str=Description}"));

            var decoded = DecodePixels(image.Data, PixelFormats.Bgra32, out var width, out var height);
            Assert.Equal(2, width);
            Assert.Equal(2, height);
            Assert.Equal(pixels, decoded);
        }
        finally
        {
            File.Delete(path);
        }
    });

    [Fact]
    public Task Jpeg_is_reencoded_without_exif_metadata_and_remains_decodable() => RunStaAsync(async () =>
    {
        const string marker = "KNET_PRIVATE_JPEG_METADATA_4B11";
        var path = CreateTempPath(".jpg");
        try
        {
            var metadata = new BitmapMetadata("jpg");
            metadata.SetQuery("/app1/ifd/{ushort=315}", marker);
            var encoder = new JpegBitmapEncoder { QualityLevel = 100 };
            Save(path, encoder, CreateGrayscaleBands(40, 20), metadata);
            Assert.Equal(marker, ReadMetadata(path, "/app1/ifd/{ushort=315}"));

            var image = await new WpfImageInputService().LoadAsync(path);

            Assert.Equal("image/jpeg", image.MimeType);
            Assert.Equal(40, image.Width);
            Assert.Equal(20, image.Height);
            Assert.Equal(Convert.ToHexString(SHA256.HashData(image.Data)), image.Sha256);
            Assert.Null(ReadMetadata(image.Data, "/app1/ifd/{ushort=315}"));
            var decoded = DecodePixels(image.Data, PixelFormats.Bgr24, out var width, out var height);
            Assert.Equal(40, width);
            Assert.Equal(20, height);
            Assert.Equal(40 * 20 * 3, decoded.Length);
        }
        finally
        {
            File.Delete(path);
        }
    });

    [Fact]
    public Task Jpeg_exif_orientation_six_is_applied_before_metadata_is_removed() => RunStaAsync(async () =>
    {
        var path = CreateTempPath(".jpg");
        try
        {
            var metadata = new BitmapMetadata("jpg");
            metadata.SetQuery("/app1/ifd/{ushort=274}", (ushort)6);
            var encoder = new JpegBitmapEncoder { QualityLevel = 100 };
            Save(path, encoder, CreateGrayscaleBands(40, 20), metadata);
            Assert.Equal((ushort)6, ReadMetadata(path, "/app1/ifd/{ushort=274}"));

            var image = await new WpfImageInputService().LoadAsync(path);

            Assert.Equal(20, image.Width);
            Assert.Equal(40, image.Height);
            Assert.Null(ReadMetadata(image.Data, "/app1/ifd/{ushort=274}"));
            var pixels = DecodePixels(image.Data, PixelFormats.Bgr24, out var width, out var height);
            Assert.Equal(20, width);
            Assert.Equal(40, height);
            Assert.True(SampleGray(pixels, width, 10, 10) < 80, "The original left band should rotate to the top.");
            Assert.True(SampleGray(pixels, width, 10, 30) > 170, "The original right band should rotate to the bottom.");
        }
        finally
        {
            File.Delete(path);
        }
    });

    private static BitmapSource CreateGrayscaleBands(int width, int height)
    {
        var pixels = new byte[width * height * 3];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var value = x < width / 2 ? (byte)20 : (byte)230;
            var offset = (y * width + x) * 3;
            pixels[offset] = value;
            pixels[offset + 1] = value;
            pixels[offset + 2] = value;
        }
        return CreateBitmap(width, height, PixelFormats.Bgr24, pixels);
    }

    private static BitmapSource CreateBitmap(int width, int height, PixelFormat format, byte[] pixels)
    {
        var bitmap = BitmapSource.Create(width, height, 96, 96, format, null, pixels,
            checked(width * (format.BitsPerPixel / 8)));
        bitmap.Freeze();
        return bitmap;
    }

    private static void Save(string path, BitmapEncoder encoder, BitmapSource pixels, BitmapMetadata metadata)
    {
        encoder.Frames.Add(BitmapFrame.Create(pixels, null, metadata, null));
        using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        encoder.Save(output);
    }

    private static object? ReadMetadata(string path, string query) => ReadMetadata(File.ReadAllBytes(path), query);

    private static object? ReadMetadata(byte[] data, string query)
    {
        using var input = new MemoryStream(data, writable: false);
        var decoder = BitmapDecoder.Create(input, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        return (decoder.Frames[0].Metadata as BitmapMetadata)?.GetQuery(query);
    }

    private static byte[] DecodePixels(byte[] data, PixelFormat format, out int width, out int height)
    {
        using var input = new MemoryStream(data, writable: false);
        var decoder = BitmapDecoder.Create(input, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var converted = new FormatConvertedBitmap(decoder.Frames[0], format, null, 0);
        width = converted.PixelWidth;
        height = converted.PixelHeight;
        var stride = checked(width * (format.BitsPerPixel / 8));
        var pixels = new byte[checked(stride * height)];
        converted.CopyPixels(pixels, stride, 0);
        return pixels;
    }

    private static byte SampleGray(byte[] bgr24, int width, int x, int y) => bgr24[(y * width + x) * 3];

    private static string CreateTempPath(string extension) =>
        Path.Combine(Path.GetTempPath(), $"KNetAgent-WpfImage-{Guid.NewGuid():N}{extension}");

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
}
