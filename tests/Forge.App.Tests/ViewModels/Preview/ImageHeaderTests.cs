using System.Buffers.Binary;
using System.Text;
using Forge.App.ViewModels.Preview;
using Xunit;

namespace Forge.App.Tests.ViewModels.Preview;

/// <summary>
/// The header size reader behind the bounded decode: each supported format's size field at its real offset, and every
/// unknown or truncated header reported as unknown rather than thrown — a wrong answer here either decodes a huge image
/// full size or shrinks a small one.
/// </summary>
public sealed class ImageHeaderTests
{
    [Fact]
    public void A_png_reads_its_size_from_the_ihdr_chunk()
    {
        var data = new byte[33];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(data, 0);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(8), 13);
        "IHDR"u8.CopyTo(data.AsSpan(12));
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(16), 5000);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(20), 300);

        Assert.True(ImageHeader.TryReadSize(data, out var width, out var height));
        Assert.Equal((5000, 300), (width, height));
    }

    [Fact]
    public void A_utf8_encoded_png_signature_is_not_a_png()
    {
        // "\x89" in a C# string is U+0089, which UTF-8 encodes as C2 89 — not the PNG signature byte.
        var data = new byte[40];
        Encoding.UTF8.GetBytes("\x89PNG\r\n\x1A\n").CopyTo(data, 0);

        Assert.False(ImageHeader.TryReadSize(data, out _, out _));
    }

    [Fact]
    public void A_gif_reads_its_little_endian_screen_size()
    {
        var data = new byte[13];
        "GIF89a"u8.CopyTo(data);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(6), 640);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(8), 4800);

        Assert.True(ImageHeader.TryReadSize(data, out var width, out var height));
        Assert.Equal((640, 4800), (width, height));
    }

    [Fact]
    public void A_jpeg_skips_its_app_segments_to_the_frame_header()
    {
        byte[] data =
        [
            0xFF, 0xD8,
            0xFF, 0xE0, 0x00, 0x10, .. new byte[14],
            0xFF, 0xFF, // fill byte before the next marker
            0xFF, 0xC0, 0x00, 0x11, 0x08, 0x0B, 0xB8, 0x0F, 0xA0, 0x03, .. new byte[9],
        ];

        Assert.True(ImageHeader.TryReadSize(data, out var width, out var height));
        Assert.Equal((4000, 3000), (width, height));
    }

    [Fact]
    public void A_jpeg_whose_scan_starts_before_a_frame_header_is_unknown()
    {
        byte[] data = [0xFF, 0xD8, 0xFF, 0xDA, 0x00, 0x08, .. new byte[6]];

        Assert.False(ImageHeader.TryReadSize(data, out _, out _));
    }

    [Fact]
    public void A_webp_vp8x_reads_its_24_bit_canvas_size()
    {
        var data = WebP("VP8X");
        WriteUInt24(data, 24, 9999);
        WriteUInt24(data, 27, 199);

        Assert.True(ImageHeader.TryReadSize(data, out var width, out var height));
        Assert.Equal((10000, 200), (width, height));
    }

    [Fact]
    public void A_webp_vp8l_reads_its_packed_14_bit_size()
    {
        var data = WebP("VP8L");
        data[20] = 0x2F;
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(21), (uint)((1234 - 1) | ((16000 - 1) << 14)));

        Assert.True(ImageHeader.TryReadSize(data, out var width, out var height));
        Assert.Equal((1234, 16000), (width, height));
    }

    [Fact]
    public void A_webp_vp8_reads_its_key_frame_size_without_the_scale_bits()
    {
        var data = WebP("VP8 ");
        data[23] = 0x9D;
        data[24] = 0x01;
        data[25] = 0x2A;
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(26), 0xC000 | 800);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(28), 600);

        Assert.True(ImageHeader.TryReadSize(data, out var width, out var height));
        Assert.Equal((800, 600), (width, height));
    }

    [Theory]
    [InlineData(new byte[0])]
    [InlineData(new byte[] { 0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0x0A })]
    [InlineData(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00 })]
    [InlineData(new byte[] { 0x47, 0x49, 0x46, 0x38, 0x39, 0x61, 0x10 })]
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xC0, 0x00, 0x11, 0x08 })]
    [InlineData(new byte[] { 0xFF, 0xD8, 0x00, 0x00, 0x00 })]
    [InlineData(new byte[] { 0x52, 0x49, 0x46, 0x46, 0x00, 0x00, 0x00, 0x00, 0x57, 0x45, 0x42, 0x50 })]
    public void Garbage_and_truncated_headers_are_unknown_never_an_exception(byte[] data)
    {
        Assert.False(ImageHeader.TryReadSize(data, out var width, out var height));
        Assert.Equal((0, 0), (width, height));
    }

    [Fact]
    public void A_zero_sized_gif_is_unknown()
    {
        var data = new byte[13];
        "GIF87a"u8.CopyTo(data);

        Assert.False(ImageHeader.TryReadSize(data, out _, out _));
    }

    private static byte[] WebP(string chunk)
    {
        var data = new byte[30];
        "RIFF"u8.CopyTo(data);
        "WEBP"u8.CopyTo(data.AsSpan(8));
        Encoding.ASCII.GetBytes(chunk).CopyTo(data, 12);
        return data;
    }

    private static void WriteUInt24(byte[] data, int offset, int value)
    {
        data[offset] = (byte)value;
        data[offset + 1] = (byte)(value >> 8);
        data[offset + 2] = (byte)(value >> 16);
    }
}
