using System.Buffers.Binary;
using Avalonia;
using Avalonia.Media.Imaging;

namespace Forge.App.ViewModels.Preview;

/// <summary>
/// The pixel size of an encoded image read from its header, without decoding it: PNG, GIF, JPEG and WebP, the four
/// formats the previews display. This is what lets a large image be decoded straight to a bounded size — a 20 MB
/// screenshot can hold hundreds of megabytes of pixels, and decoding it full size only to scale it down is the
/// allocation spike the bounded decode exists to avoid. Pure and allocation-free; an unknown or truncated header is
/// "unknown", never an exception.
/// </summary>
internal static class ImageHeader
{
    private static ReadOnlySpan<byte> PngSignature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    public static bool TryReadSize(ReadOnlySpan<byte> data, out int width, out int height)
    {
        width = 0;
        height = 0;
        if (data.Length >= 24 && data.StartsWith(PngSignature) && data.Slice(12, 4).SequenceEqual("IHDR"u8))
        {
            width = (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(data[16..]), int.MaxValue);
            height = (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(data[20..]), int.MaxValue);
            return Valid(width, height);
        }

        if (data.Length >= 10 && (data.StartsWith("GIF87a"u8) || data.StartsWith("GIF89a"u8)))
        {
            width = BinaryPrimitives.ReadUInt16LittleEndian(data[6..]);
            height = BinaryPrimitives.ReadUInt16LittleEndian(data[8..]);
            return Valid(width, height);
        }

        if (data.Length >= 4 && data[0] == 0xFF && data[1] == 0xD8)
        {
            return TryReadJpeg(data, out width, out height);
        }

        if (data.Length >= 30 && data.StartsWith("RIFF"u8) && data.Slice(8, 4).SequenceEqual("WEBP"u8))
        {
            return TryReadWebP(data, out width, out height);
        }

        return false;
    }

    /// <summary>
    /// Decodes <paramref name="data"/> with its long edge at most <paramref name="maxLongEdge"/> pixels: an image the header
    /// says is larger is decoded straight to the bound, one within it (or with an unreadable header) at its own size, so a
    /// small image is never blown up. Throws what the decoder throws; the callers turn that into their own notice.
    /// </summary>
    public static Bitmap DecodeBounded(byte[] data, int maxLongEdge, BitmapInterpolationMode quality)
    {
        using var input = new MemoryStream(data, writable: false);
        if (maxLongEdge > 0 && TryReadSize(data, out var width, out var height) && Math.Max(width, height) > maxLongEdge)
        {
            return width >= height
                ? Bitmap.DecodeToWidth(input, maxLongEdge, quality)
                : Bitmap.DecodeToHeight(input, maxLongEdge, quality);
        }

        var decoded = new Bitmap(input);
        var size = decoded.PixelSize;
        var longEdge = Math.Max(size.Width, size.Height);
        if (maxLongEdge <= 0 || longEdge <= maxLongEdge)
        {
            return decoded;
        }

        // The header did not say (an exotic JPEG layout, say): scale after the fact and drop the full-size bitmap.
        var scale = (double)maxLongEdge / longEdge;
        var target = new PixelSize(Math.Max(1, (int)(size.Width * scale)), Math.Max(1, (int)(size.Height * scale)));
        using (decoded)
        {
            return decoded.CreateScaledBitmap(target, quality);
        }
    }

    private static bool TryReadJpeg(ReadOnlySpan<byte> data, out int width, out int height)
    {
        width = 0;
        height = 0;
        var index = 2;
        while (index + 4 <= data.Length)
        {
            if (data[index] != 0xFF)
            {
                return false;
            }

            var marker = data[index + 1];
            if (marker == 0xFF)
            {
                // Fill bytes before a marker.
                index++;
                continue;
            }

            if (marker is 0xD8 or 0x01 or (>= 0xD0 and <= 0xD7))
            {
                // Markers without a length segment.
                index += 2;
                continue;
            }

            var length = BinaryPrimitives.ReadUInt16BigEndian(data[(index + 2)..]);
            if (length < 2)
            {
                return false;
            }

            // Start-of-frame markers, excluding DHT (C4), JPG (C8) and DAC (CC), carry the frame size.
            if (marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC)
            {
                if (index + 9 > data.Length)
                {
                    return false;
                }

                height = BinaryPrimitives.ReadUInt16BigEndian(data[(index + 5)..]);
                width = BinaryPrimitives.ReadUInt16BigEndian(data[(index + 7)..]);
                return Valid(width, height);
            }

            if (marker is 0xD9 or 0xDA)
            {
                // End of image, or the scan started before any frame header: no size to read.
                return false;
            }

            index += 2 + length;
        }

        return false;
    }

    private static bool TryReadWebP(ReadOnlySpan<byte> data, out int width, out int height)
    {
        width = 0;
        height = 0;
        var chunk = data.Slice(12, 4);
        if (chunk.SequenceEqual("VP8X"u8))
        {
            width = 1 + (data[24] | (data[25] << 8) | (data[26] << 16));
            height = 1 + (data[27] | (data[28] << 8) | (data[29] << 16));
            return Valid(width, height);
        }

        if (chunk.SequenceEqual("VP8L"u8) && data[20] == 0x2F)
        {
            width = 1 + (data[21] | ((data[22] & 0x3F) << 8));
            height = 1 + ((data[22] >> 6) | (data[23] << 2) | ((data[24] & 0x0F) << 10));
            return Valid(width, height);
        }

        if (chunk.SequenceEqual("VP8 "u8) && data[23] == 0x9D && data[24] == 0x01 && data[25] == 0x2A)
        {
            width = BinaryPrimitives.ReadUInt16LittleEndian(data[26..]) & 0x3FFF;
            height = BinaryPrimitives.ReadUInt16LittleEndian(data[28..]) & 0x3FFF;
            return Valid(width, height);
        }

        return false;
    }

    private static bool Valid(int width, int height) => width > 0 && height > 0;
}
