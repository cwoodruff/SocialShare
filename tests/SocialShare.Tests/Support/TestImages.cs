using System.Buffers.Binary;
using System.Text;

namespace SocialShare.Tests;

/// <summary>Byte level fixtures, just enough header for the inspector to read.</summary>
public static class TestImages
{
    public static byte[] Png(int width, int height)
    {
        var bytes = new byte[24];
        ReadOnlySpan<byte> signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        signature.CopyTo(bytes);

        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(8, 4), 13);
        "IHDR"u8.CopyTo(bytes.AsSpan(12, 4));
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(16, 4), width);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(20, 4), height);

        return bytes;
    }

    public static byte[] Jpeg(int width, int height)
    {
        var bytes = new List<byte> { 0xFF, 0xD8, 0xFF };

        // An APP0 segment that has to be walked past before the frame header.
        bytes.AddRange([0xE0, 0x00, 0x10]);
        bytes.AddRange(Encoding.ASCII.GetBytes("JFIF\0"));
        bytes.AddRange(new byte[9]);

        // SOF0 carries the real dimensions.
        bytes.AddRange([0xFF, 0xC0, 0x00, 0x11, 0x08]);
        bytes.AddRange([(byte)(height >> 8), (byte)(height & 0xFF)]);
        bytes.AddRange([(byte)(width >> 8), (byte)(width & 0xFF)]);
        bytes.AddRange(new byte[10]);

        return [.. bytes];
    }

    public static byte[] WebPLossy(int width, int height)
    {
        var bytes = new byte[30];
        "RIFF"u8.CopyTo(bytes.AsSpan(0, 4));
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4, 4), bytes.Length - 8);
        "WEBP"u8.CopyTo(bytes.AsSpan(8, 4));
        "VP8 "u8.CopyTo(bytes.AsSpan(12, 4));
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(26, 2), (ushort)width);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(28, 2), (ushort)height);

        return bytes;
    }
}
