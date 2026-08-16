using System.Buffers.Binary;

namespace TestAgent.Infrastructure;

public sealed record ImageHeader(string MimeType, int Width, int Height);

/// <summary>Reads only bounded image headers so oversized pixel dimensions are rejected before decoding.</summary>
public static class ImageInputPreflight
{
    public const int MaxSourceBytes = 10 * 1024 * 1024;
    public const long MaxPixels = 20_000_000;

    public static ImageHeader Inspect(ReadOnlySpan<byte> data)
    {
        ReadOnlySpan<byte> png = [137, 80, 78, 71, 13, 10, 26, 10];
        ImageHeader header;
        if (data.Length >= 24 && data[..8].SequenceEqual(png))
            header = new("image/png", BinaryPrimitives.ReadInt32BigEndian(data[16..20]),
                BinaryPrimitives.ReadInt32BigEndian(data[20..24]));
        else if (data.Length >= 4 && data[0] == 0xFF && data[1] == 0xD8)
            header = ReadJpeg(data);
        else
            throw new InvalidDataException("Only PNG and JPEG byte formats are supported.");
        if (header.Width <= 0 || header.Height <= 0 || (long)header.Width * header.Height > MaxPixels)
            throw new InvalidDataException("Image dimensions are invalid or exceed 20 megapixels.");
        return header;
    }

    private static ImageHeader ReadJpeg(ReadOnlySpan<byte> data)
    {
        var offset = 2;
        while (offset + 3 < data.Length)
        {
            while (offset < data.Length && data[offset] != 0xFF) offset++;
            while (offset < data.Length && data[offset] == 0xFF) offset++;
            if (offset >= data.Length) break;
            var marker = data[offset++];
            if (marker is 0xD8 or 0x01 || marker is >= 0xD0 and <= 0xD7) continue;
            if (marker is 0xD9 or 0xDA || offset + 1 >= data.Length) break;
            var length = BinaryPrimitives.ReadUInt16BigEndian(data[offset..(offset + 2)]);
            if (length < 2 || offset + length > data.Length)
                throw new InvalidDataException("JPEG segment length is invalid.");
            if (IsStartOfFrame(marker))
            {
                if (length < 7) throw new InvalidDataException("JPEG dimensions are missing.");
                var height = BinaryPrimitives.ReadUInt16BigEndian(data[(offset + 3)..(offset + 5)]);
                var width = BinaryPrimitives.ReadUInt16BigEndian(data[(offset + 5)..(offset + 7)]);
                return new("image/jpeg", width, height);
            }
            offset += length;
        }
        throw new InvalidDataException("JPEG dimensions could not be read.");
    }

    private static bool IsStartOfFrame(byte marker) => marker is
        0xC0 or 0xC1 or 0xC2 or 0xC3 or 0xC5 or 0xC6 or 0xC7 or
        0xC9 or 0xCA or 0xCB or 0xCD or 0xCE or 0xCF;
}
