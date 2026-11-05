using Forge.App.Terminal.Vt;
using Xunit;

namespace Forge.App.Tests.Terminal;

/// <summary>
/// Covers UTF-8 decoding across <see cref="TerminalEmulator.Feed"/> chunk boundaries:
/// a multi-byte sequence split byte-by-byte still lands as one cell, and illegal bytes
/// become U+FFFD without corrupting the parser state that follows.
/// </summary>
public sealed class TerminalUtf8Tests
{
    [Fact]
    public void Multibyte_character_split_one_byte_per_feed_reassembles()
    {
        var emulator = VtHelpers.Create(10, 4, 16, out _);
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes("A\u4e2dB");

        foreach (byte b in bytes)
        {
            VtHelpers.FeedBytes(emulator, b);
        }

        Assert.Equal(0x4E2D, VtHelpers.ScreenCell(emulator, 0, 1).Rune);
        Assert.True(VtHelpers.ScreenCell(emulator, 0, 2).IsWideContinuation);
        Assert.Equal('A', (char)VtHelpers.ScreenCell(emulator, 0, 0).Rune);
        Assert.Equal('B', (char)VtHelpers.ScreenCell(emulator, 0, 3).Rune);
        Assert.Equal((0, 4), VtHelpers.Cursor(emulator));
    }

    [Fact]
    public void Four_byte_emoji_split_across_feeds_reassembles()
    {
        var emulator = VtHelpers.Create(10, 4, 16, out _);
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes("\U0001F600");
        VtHelpers.FeedBytes(emulator, bytes[0], bytes[1]);
        VtHelpers.FeedBytes(emulator, bytes[2], bytes[3]);

        Assert.Equal(0x1F600, VtHelpers.ScreenCell(emulator, 0, 0).Rune);
        Assert.True(VtHelpers.ScreenCell(emulator, 0, 1).IsWideContinuation);
        Assert.Equal((0, 2), VtHelpers.Cursor(emulator));
    }

    [Fact]
    public void Invalid_byte_becomes_replacement_character_and_parsing_continues()
    {
        var emulator = VtHelpers.Create(10, 4, 16, out _);

        VtHelpers.FeedBytes(emulator, 0xFF);
        VtHelpers.Feed(emulator, "A");

        Assert.Equal(0xFFFD, VtHelpers.ScreenCell(emulator, 0, 0).Rune);
        Assert.Equal('A', (char)VtHelpers.ScreenCell(emulator, 0, 1).Rune);
        Assert.Equal((0, 2), VtHelpers.Cursor(emulator));
    }

    [Fact]
    public void Truncated_sequence_followed_by_ascii_recovers()
    {
        var emulator = VtHelpers.Create(10, 4, 16, out _);

        // 0xE4 starts a 3-byte sequence; 0x41 ('A') is not a valid continuation.
        VtHelpers.FeedBytes(emulator, 0xE4, 0x41);

        Assert.Equal(0xFFFD, VtHelpers.ScreenCell(emulator, 0, 0).Rune);
        Assert.Equal('A', (char)VtHelpers.ScreenCell(emulator, 0, 1).Rune);
        Assert.Equal((0, 2), VtHelpers.Cursor(emulator));
    }

    [Fact]
    public void Split_sequence_completed_by_later_feed_prints_once()
    {
        var emulator = VtHelpers.Create(10, 4, 16, out _);
        byte[] full = System.Text.Encoding.UTF8.GetBytes("\u4e2d");

        VtHelpers.FeedBytes(emulator, full[0], full[1]);

        // Nothing printable has arrived yet: the cell is still blank (rune 0).
        Assert.Equal(0, VtHelpers.ScreenCell(emulator, 0, 0).Rune);
        Assert.Equal((0, 0), VtHelpers.Cursor(emulator));

        VtHelpers.FeedBytes(emulator, full[2]);

        Assert.Equal(0x4E2D, VtHelpers.ScreenCell(emulator, 0, 0).Rune);
        Assert.Equal((0, 2), VtHelpers.Cursor(emulator));
    }
}
