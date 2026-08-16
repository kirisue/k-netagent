using System.Buffers.Binary;
using TestAgent.Infrastructure;
using Xunit;

namespace TestAgent.Tests;

public sealed class ImageInputServiceTests
{
    [Fact]
    public void Reads_real_png_header_without_decoding_pixels()
    {
        var source=Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
        var header=ImageInputPreflight.Inspect(source);
        Assert.Equal("image/png",header.MimeType);Assert.Equal(1,header.Width);Assert.Equal(1,header.Height);
    }

    [Fact]
    public void Rejects_fake_format_and_oversized_dimensions_before_decode()
    {
        Assert.Throws<InvalidDataException>(()=>ImageInputPreflight.Inspect("not an image"u8));
        var bomb=new byte[24];new byte[]{137,80,78,71,13,10,26,10}.CopyTo(bomb,0);BinaryPrimitives.WriteInt32BigEndian(bomb.AsSpan(16,4),5000);BinaryPrimitives.WriteInt32BigEndian(bomb.AsSpan(20,4),5000);
        Assert.Throws<InvalidDataException>(()=>ImageInputPreflight.Inspect(bomb));
    }
}
