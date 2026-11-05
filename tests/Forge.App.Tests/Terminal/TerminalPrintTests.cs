using Forge.App.Terminal.Vt;
using Xunit;

namespace Forge.App.Tests.Terminal;

/// <summary>
/// Covers plain printing and cursor motion: text, CR/LF, tab stops (HTS/TBC), backspace,
/// pending wrap (the cursor only wraps when the next printable character arrives),
/// double-width cells, combining marks and zero-width runes.
/// </summary>
public sealed class TerminalPrintTests
{
    [Fact]
    public void Plain_text_lands_in_cells_and_advances_cursor()
    {
        var emulator = VtHelpers.Create(10, 4, 16, out _);

        VtHelpers.Feed(emulator, "Hello");

        Assert.Equal("Hello     ", VtHelpers.RowText(emulator, 0));
        Assert.Equal((0, 5), VtHelpers.Cursor(emulator));
    }

    [Fact]
    public void Crlf_moves_to_start_of_next_line()
    {
        var emulator = VtHelpers.Create(10, 4, 16, out _);

        VtHelpers.Feed(emulator, "Hello\r\nWorld");

        Assert.Equal("Hello     ", VtHelpers.RowText(emulator, 0));
        Assert.Equal("World     ", VtHelpers.RowText(emulator, 1));
        Assert.Equal((1, 5), VtHelpers.Cursor(emulator));
    }

    [Fact]
    public void Lone_linefeed_keeps_column_without_lnm()
    {
        var emulator = VtHelpers.Create(10, 4, 16, out _);
        VtHelpers.Feed(emulator, "Hello");

        VtHelpers.Feed(emulator, "\n");

        Assert.Equal((1, 5), VtHelpers.Cursor(emulator));
        Assert.Equal("Hello     ", VtHelpers.RowText(emulator, 0));
    }

    [Fact]
    public void Carriage_return_returns_to_column_zero()
    {
        var emulator = VtHelpers.Create(10, 4, 16, out _);
        VtHelpers.Feed(emulator, "Hello");

        VtHelpers.Feed(emulator, "\rX");

        Assert.Equal("Xello     ", VtHelpers.RowText(emulator, 0));
        Assert.Equal((0, 1), VtHelpers.Cursor(emulator));
    }

    [Theory]
    [InlineData(0, 8)]
    [InlineData(1, 8)]
    [InlineData(8, 16)]
    [InlineData(9, 16)]
    public void Tab_advances_to_default_8_column_stops(int from, int expected)
    {
        var emulator = VtHelpers.Create(20, 4, 16, out _);
        VtHelpers.Feed(emulator, $"\u001b[{from + 1}G");

        VtHelpers.Feed(emulator, "\t");

        Assert.Equal((0, expected), VtHelpers.Cursor(emulator));
    }

    [Fact]
    public void Hts_sets_a_custom_stop_and_tbc_clears_it()
    {
        var emulator = VtHelpers.Create(20, 4, 16, out _);

        // Custom stop at column 5.
        VtHelpers.Feed(emulator, "\u001b[6G\u001bH\u001b[1GX\t");
        Assert.Equal((0, 5), VtHelpers.Cursor(emulator));

        // TBC 0 clears the stop under the cursor; the tab falls through to 8.
        VtHelpers.Feed(emulator, "\u001b[g\r\t");
        Assert.Equal((0, 8), VtHelpers.Cursor(emulator));
    }

    [Fact]
    public void Tbc_3_clears_all_stops_so_tab_runs_to_last_column()
    {
        var emulator = VtHelpers.Create(10, 4, 16, out _);

        VtHelpers.Feed(emulator, "\u001b[3g\t");

        Assert.Equal((0, 9), VtHelpers.Cursor(emulator));
    }

    [Fact]
    public void Backspace_moves_left_and_overwrites()
    {
        var emulator = VtHelpers.Create(10, 4, 16, out _);

        VtHelpers.Feed(emulator, "AB\bC");

        Assert.Equal("AC        ", VtHelpers.RowText(emulator, 0));
        Assert.Equal((0, 2), VtHelpers.Cursor(emulator));
    }

    [Fact]
    public void Backspace_at_column_zero_stays_put()
    {
        var emulator = VtHelpers.Create(10, 4, 16, out _);

        VtHelpers.Feed(emulator, "\bX");

        Assert.Equal("X         ", VtHelpers.RowText(emulator, 0));
        Assert.Equal((0, 1), VtHelpers.Cursor(emulator));
    }

    [Fact]
    public void Cursor_stays_on_last_column_until_next_printable_char_wraps()
    {
        var emulator = VtHelpers.Create(10, 4, 16, out _);
        VtHelpers.Feed(emulator, "0123456789");

        // The wrap is still pending: the cursor has not moved to the next row yet.
        Assert.Equal((0, 9), VtHelpers.Cursor(emulator));
        Assert.Equal("0123456789", VtHelpers.RowText(emulator, 0));

        VtHelpers.Feed(emulator, "X");

        Assert.Equal("0123456789", VtHelpers.RowText(emulator, 0));
        Assert.Equal("X         ", VtHelpers.RowText(emulator, 1));
        Assert.Equal((1, 1), VtHelpers.Cursor(emulator));
    }

    [Fact]
    public void Cursor_motion_cancels_pending_wrap_without_scrolling()
    {
        var emulator = VtHelpers.Create(10, 4, 16, out _);
        VtHelpers.Feed(emulator, "0123456789");

        VtHelpers.Feed(emulator, "\u001b[DY");

        Assert.Equal("01234567Y9", VtHelpers.RowText(emulator, 0));
        Assert.Equal("          ", VtHelpers.RowText(emulator, 1));
        Assert.Equal((0, 9), VtHelpers.Cursor(emulator));
    }

    [Fact]
    public void Carriage_return_cancels_pending_wrap()
    {
        var emulator = VtHelpers.Create(10, 4, 16, out _);
        VtHelpers.Feed(emulator, "0123456789");

        VtHelpers.Feed(emulator, "\rZ");

        Assert.Equal("Z123456789", VtHelpers.RowText(emulator, 0));
        Assert.Equal((0, 1), VtHelpers.Cursor(emulator));
    }

    [Fact]
    public void Without_autowrap_last_column_overwrites_in_place()
    {
        var emulator = VtHelpers.Create(10, 4, 16, out _);
        VtHelpers.Feed(emulator, "\u001b[?7l0123456789XY");

        Assert.Equal("012345678Y", VtHelpers.RowText(emulator, 0));
        Assert.Equal("          ", VtHelpers.RowText(emulator, 1));
        Assert.Equal((0, 9), VtHelpers.Cursor(emulator));

        VtHelpers.Feed(emulator, "\u001b[?7h");
        Assert.Equal(TerminalModes.AutoWrap, emulator.Modes & TerminalModes.AutoWrap);
    }

    [Fact]
    public void Wide_character_occupies_two_cells_with_continuation()
    {
        var emulator = VtHelpers.Create(10, 4, 16, out _);

        VtHelpers.Feed(emulator, "A\u4e2dB");

        TerminalCell first = VtHelpers.ScreenCell(emulator, 0, 1);
        TerminalCell second = VtHelpers.ScreenCell(emulator, 0, 2);
        Assert.Equal(0x4E2D, first.Rune);
        Assert.False(first.IsWideContinuation);
        Assert.True(second.IsWideContinuation);
        Assert.Equal('A', (char)VtHelpers.ScreenCell(emulator, 0, 0).Rune);
        Assert.Equal('B', (char)VtHelpers.ScreenCell(emulator, 0, 3).Rune);
        Assert.Equal((0, 4), VtHelpers.Cursor(emulator));
    }

    [Fact]
    public void Wide_character_at_last_column_wraps_first()
    {
        var emulator = VtHelpers.Create(4, 4, 16, out _);
        VtHelpers.Feed(emulator, "\u001b[4G");

        VtHelpers.Feed(emulator, "\u4e2d");

        Assert.Equal("    ", VtHelpers.RowText(emulator, 0));
        Assert.Equal(0x4E2D, VtHelpers.ScreenCell(emulator, 1, 0).Rune);
        Assert.True(VtHelpers.ScreenCell(emulator, 1, 1).IsWideContinuation);
        Assert.Equal((1, 2), VtHelpers.Cursor(emulator));
    }

    [Fact]
    public void Wide_character_at_last_column_is_dropped_without_autowrap()
    {
        var emulator = VtHelpers.Create(4, 4, 16, out _);
        VtHelpers.Feed(emulator, "\u001b[?7l\u001b[4G");

        VtHelpers.Feed(emulator, "\u4e2d");

        Assert.Equal("    ", VtHelpers.RowText(emulator, 0));
        Assert.Equal("    ", VtHelpers.RowText(emulator, 1));
        Assert.Equal((0, 3), VtHelpers.Cursor(emulator));
    }

    [Fact]
    public void Combining_mark_attaches_to_previous_cell_without_advancing()
    {
        var emulator = VtHelpers.Create(10, 4, 16, out _);

        VtHelpers.Feed(emulator, "e\u0301");

        Assert.Equal('e', (char)VtHelpers.ScreenCell(emulator, 0, 0).Rune);
        Assert.Equal("\u0301", emulator.GetLine(emulator.ScrollbackCount).GetCombining(0));
        Assert.Equal((0, 1), VtHelpers.Cursor(emulator));
    }

    [Fact]
    public void Zero_width_joiner_does_not_consume_a_cell()
    {
        var emulator = VtHelpers.Create(10, 4, 16, out _);

        VtHelpers.Feed(emulator, "a\u200db");

        Assert.Equal('a', (char)VtHelpers.ScreenCell(emulator, 0, 0).Rune);
        Assert.Equal("\u200d", emulator.GetLine(emulator.ScrollbackCount).GetCombining(0));
        Assert.Equal('b', (char)VtHelpers.ScreenCell(emulator, 0, 1).Rune);
        Assert.Equal((0, 2), VtHelpers.Cursor(emulator));
    }

    [Fact]
    public void Nel_moves_to_start_of_next_line()
    {
        var emulator = VtHelpers.Create(10, 4, 16, out _);

        VtHelpers.Feed(emulator, "AB\u001bEC");

        Assert.Equal("AB        ", VtHelpers.RowText(emulator, 0));
        Assert.Equal("C         ", VtHelpers.RowText(emulator, 1));
        Assert.Equal((1, 1), VtHelpers.Cursor(emulator));
    }

    [Fact]
    public void Feeding_text_bumps_version_while_empty_feed_does_not()
    {
        var emulator = VtHelpers.Create(10, 4, 16, out _);
        long before = emulator.Version;

        VtHelpers.Feed(emulator, "hi");
        Assert.True(emulator.Version > before);

        long afterText = emulator.Version;
        emulator.Feed(ReadOnlySpan<byte>.Empty);
        Assert.Equal(afterText, emulator.Version);
    }

    [Fact]
    public void Combining_mark_with_no_base_cell_is_dropped()
    {
        var emulator = VtHelpers.Create(10, 4, 16, out _);

        VtHelpers.Feed(emulator, "\u0301b");

        Assert.Equal('b', (char)VtHelpers.ScreenCell(emulator, 0, 0).Rune);
        Assert.Equal((0, 1), VtHelpers.Cursor(emulator));
    }
}
