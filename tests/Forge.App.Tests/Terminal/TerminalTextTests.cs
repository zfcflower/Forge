using Forge.App.Terminal.Vt;
using Xunit;

namespace Forge.App.Tests.Terminal;

/// <summary>
/// Covers <see cref="TerminalText"/>: linear extraction on one line, soft-wrapped lines joined
/// without newlines (with trailing blanks trimmed), extraction across scrollback and screen,
/// wide characters counted once, combining marks kept, rectangular block selection,
/// and <see cref="TerminalText.WordAt"/> boundaries.
/// </summary>
public sealed class TerminalTextTests
{
    [Fact]
    public void GetText_reads_a_range_on_one_line()
    {
        var emulator = VtHelpers.Create(20, 4, 8, out _);
        VtHelpers.Feed(emulator, "hello world");

        Assert.Equal("hello", TerminalText.GetText(emulator, (0, 0), (0, 4)));
        Assert.Equal("world", TerminalText.GetText(emulator, (0, 6), (0, 10)));
        Assert.Equal("hello world", TerminalText.GetText(emulator, (0, 0), (0, 19)));
    }

    [Fact]
    public void GetText_joins_soft_wrapped_lines_without_newline()
    {
        var emulator = VtHelpers.Create(10, 4, 8, out _);
        VtHelpers.Feed(emulator, "0123456789AB");

        Assert.Equal("0123456789AB", TerminalText.GetText(emulator, (0, 0), (1, 1)));
    }

    [Fact]
    public void GetText_trims_trailing_blanks_and_uses_newline_for_hard_breaks()
    {
        var emulator = VtHelpers.Create(10, 4, 8, out _);
        VtHelpers.Feed(emulator, "Hi\r\nBye");

        Assert.Equal("Hi", TerminalText.GetText(emulator, (0, 0), (0, 9)));
        Assert.Equal("Hi\nBye", TerminalText.GetText(emulator, (0, 0), (1, 2)));
    }

    [Fact]
    public void GetText_spans_scrollback_and_screen()
    {
        var emulator = VtHelpers.Create(10, 1, 8, out _);
        VtHelpers.Feed(emulator, "old\r\nnew");

        Assert.Equal(1, emulator.ScrollbackCount);
        Assert.Equal("old\nnew", TerminalText.GetText(emulator, (0, 0), (1, 2)));
    }

    [Fact]
    public void GetText_counts_wide_characters_once()
    {
        var emulator = VtHelpers.Create(10, 4, 8, out _);
        VtHelpers.Feed(emulator, "A\u4e2dB");

        Assert.Equal("A\u4e2dB", TerminalText.GetText(emulator, (0, 0), (0, 9)));
    }

    [Fact]
    public void GetText_keeps_combining_marks_with_their_cell()
    {
        var emulator = VtHelpers.Create(10, 4, 8, out _);
        VtHelpers.Feed(emulator, "e\u0301x");

        Assert.Equal("e\u0301x", TerminalText.GetText(emulator, (0, 0), (0, 9)));
    }

    [Fact]
    public void GetText_block_selects_a_rectangle()
    {
        var emulator = VtHelpers.Create(10, 4, 8, out _);
        VtHelpers.Feed(emulator, "abcdef\r\nghijkl\r\nmnopqr");

        Assert.Equal("bcd\nhij\nnop", TerminalText.GetText(emulator, (0, 1), (2, 3), block: true));
    }

    [Fact]
    public void GetText_accepts_reversed_endpoints()
    {
        var emulator = VtHelpers.Create(20, 4, 8, out _);
        VtHelpers.Feed(emulator, "hello world");

        Assert.Equal("hello", TerminalText.GetText(emulator, (0, 4), (0, 0)));
    }

    [Fact]
    public void WordAt_selects_the_word_around_the_cursor()
    {
        var emulator = VtHelpers.Create(20, 4, 8, out _);
        VtHelpers.Feed(emulator, "foo bar");

        Assert.Equal((0, 2), TerminalText.WordAt(emulator, 0, 1));
        Assert.Equal((4, 6), TerminalText.WordAt(emulator, 0, 6));
    }

    [Fact]
    public void WordAt_on_whitespace_returns_the_point_itself()
    {
        var emulator = VtHelpers.Create(20, 4, 8, out _);
        VtHelpers.Feed(emulator, "foo bar");

        Assert.Equal((3, 3), TerminalText.WordAt(emulator, 0, 3));
    }

    [Fact]
    public void WordAt_clamps_out_of_range_positions()
    {
        var emulator = VtHelpers.Create(20, 4, 8, out _);
        VtHelpers.Feed(emulator, "foo bar");

        Assert.Equal((0, 2), TerminalText.WordAt(emulator, 0, 0));
        Assert.Equal((4, 6), TerminalText.WordAt(emulator, 0, 6));
        Assert.Equal((19, 19), TerminalText.WordAt(emulator, 0, 100));
    }

    [Fact]
    public void WordAt_stops_at_punctuation_delimiters()
    {
        var emulator = VtHelpers.Create(20, 4, 8, out _);
        VtHelpers.Feed(emulator, "a(b)c");

        Assert.Equal((0, 0), TerminalText.WordAt(emulator, 0, 0));
        Assert.Equal((2, 2), TerminalText.WordAt(emulator, 0, 2));
        Assert.Equal((4, 4), TerminalText.WordAt(emulator, 0, 4));
    }
}
