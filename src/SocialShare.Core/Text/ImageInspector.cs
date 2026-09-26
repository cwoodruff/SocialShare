using System.Buffers.Binary;

namespace SocialShare.Core.Text;

public sealed record ImageInfo(string ContentType, int Width, int Height);

/// <summary>
/// Reads the first bytes of an upload to work out what it really is and how big it is.
/// This is deliberately hand rolled rather than pulling in an imaging library: all we need
/// is to reject anything that is not a real JPEG, PNG or WebP and to read the dimensions.
/// </summary>
public static class ImageInspector
{
    public const string Jpeg = "image/jpeg";
    public const string Png = "image/png";
    public const string WebP = "image/webp";

    public static readonly IReadOnlyList<string> AllowedContentTypes = [Jpeg, Png, WebP];

    /// <summary>
    /// Returns the real content type and dimensions, or null when the bytes are not one of
    /// the three formats we accept. The extension and the browser supplied type are ignored.
    /// </summary>
    public static ImageInfo? Inspect(ReadOnlySpan<byte> bytes)
    {
        if (IsPng(bytes))
        {
            return ReadPng(bytes);
        }

        if (IsJpeg(bytes))
        {
            return ReadJpeg(bytes);
        }

        if (IsWebP(bytes))
        {
            return ReadWebP(bytes);
        }

        return null;
    }

    private static bool IsPng(ReadOnlySpan<byte> b) =>
        b.Length >= 8 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47
        && b[4] == 0x0D && b[5] == 0x0A && b[6] == 0x1A && b[7] == 0x0A;

    private static bool IsJpeg(ReadOnlySpan<byte> b) =>
        b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF;

    private static bool IsWebP(ReadOnlySpan<byte> b) =>
        b.Length >= 12
        && b[0] == (byte)'R' && b[1] == (byte)'I' && b[2] == (byte)'F' && b[3] == (byte)'F'
        && b[8] == (byte)'W' && b[9] == (byte)'E' && b[10] == (byte)'B' && b[11] == (byte)'P';

    private static ImageInfo? ReadPng(ReadOnlySpan<byte> b)
    {
        // IHDR is always the first chunk: 8 byte signature, 4 byte length, 4 byte type, then w and h.
        if (b.Length < 24)
        {
            return null;
        }

        var width = BinaryPrimitives.ReadInt32BigEndian(b.Slice(16, 4));
        var height = BinaryPrimitives.ReadInt32BigEndian(b.Slice(20, 4));
        return width > 0 && height > 0 ? new ImageInfo(Png, width, height) : null;
    }

    private static ImageInfo? ReadJpeg(ReadOnlySpan<byte> b)
    {
        // Walk the marker segments until one of the Start Of Frame markers, which carries the size.
        var i = 2;
        while (i + 9 < b.Length)
        {
            if (b[i] != 0xFF)
            {
                i++;
                continue;
            }

            var marker = b[i + 1];
            if (marker is 0xD8 or 0x01 || (marker >= 0xD0 && marker <= 0xD7))
            {
                i += 2;
                continue;
            }

            var length = BinaryPrimitives.ReadUInt16BigEndian(b.Slice(i + 2, 2));
            var isStartOfFrame = marker is >= 0xC0 and <= 0xCF
                && marker is not 0xC4 and not 0xC8 and not 0xCC;

            if (isStartOfFrame)
            {
                var height = BinaryPrimitives.ReadUInt16BigEndian(b.Slice(i + 5, 2));
                var width = BinaryPrimitives.ReadUInt16BigEndian(b.Slice(i + 7, 2));
                return width > 0 && height > 0 ? new ImageInfo(Jpeg, width, height) : null;
            }

            i += 2 + length;
        }

        return null;
    }

    private static ImageInfo? ReadWebP(ReadOnlySpan<byte> b)
    {
        if (b.Length < 30)
        {
            return null;
        }

        var format = System.Text.Encoding.ASCII.GetString(b.Slice(12, 4));
        switch (format)
        {
            case "VP8 ":
            {
                // Lossy: 3 byte frame tag, 3 byte start code, then 14 bit width and height.
                var width = BinaryPrimitives.ReadUInt16LittleEndian(b.Slice(26, 2)) & 0x3FFF;
                var height = BinaryPrimitives.ReadUInt16LittleEndian(b.Slice(28, 2)) & 0x3FFF;
                return width > 0 && height > 0 ? new ImageInfo(WebP, width, height) : null;
            }

            case "VP8L":
            {
                // Lossless: 1 byte signature then 14 bits width and 14 bits height, minus one.
                var bits = BinaryPrimitives.ReadUInt32LittleEndian(b.Slice(21, 4));
                var width = (int)(bits & 0x3FFF) + 1;
                var height = (int)((bits >> 14) & 0x3FFF) + 1;
                return new ImageInfo(WebP, width, height);
            }

            case "VP8X":
            {
                // Extended: 4 byte flags then 24 bit width minus one and 24 bit height minus one.
                var width = (b[24] | (b[25] << 8) | (b[26] << 16)) + 1;
                var height = (b[27] | (b[28] << 8) | (b[29] << 16)) + 1;
                return new ImageInfo(WebP, width, height);
            }

            default:
                return null;
        }
    }
}
