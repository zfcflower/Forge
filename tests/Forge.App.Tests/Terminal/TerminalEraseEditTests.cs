using Forge.App.Terminal.Vt;
using Xunit;

namespace Forge.App.Tests.Terminal;

/// <summary>
/// Covers erasing and editing: ED 0/1/2/3, EL 0/1/2, ECH, ICH, DCH, IL, DL, SU, SD and REP.
/// Erasures clear attributes (checked on a blinking cell) while keeping the current
/// background color as the erase color.
/// </summary>
public sealed class TerminalEraseEditTests
{
    private static TerminalEmulator FilledScreen(out VtTestHost host)
    {
        var emulator = VtHelpers.Create(12, 4, 16, out host);
        VtHelpers.Feed(emulator, "HelloWorld\r\nSecondLine\r\nThirdLine");
        return emulator;
    }

    [Fact]
    public void Ed0_erases_from_cursor_to_end_of_screen()
    {
        var emulator = FilledScreen(out _);
        VtHelpers.Feed(emulator, "\u001b[1;6H\u001b[J");

        Assert.Equal("Hello       ", VtHelpers.RowText(emulator, 0));
        Assert.Equal("            ", VtHelpers.RowText(emulator, 1));
        Assert.Equal("            ", VtHelpers.RowText(emulator, 2));
        Assert.Equal((0, 5), VtHelpers.Cursor(emulator));
    }

    [Fact]
    public void Ed0_clears_flags_but_keeps_erase_background()
    {
        var emulator = VtHelpers.Create(12, 4, 16, out _);
        VtHelpers.Feed(emulator, "\u001b[5;41mHelloWorld");
        VtHelpers.Feed(emulator, "\u001b[1;6H\u001b[J");

        TerminalCell kept = VtHelpers.ScreenCell(emulator, 0, 4);
        Assert.Equal(CellFlags.Blink, kept.Flags);

        TerminalCell erased = VtHelpers.ScreenCell(emulator, 0, 5);
        Assert.Equal(0, erased.Rune);
        Assert.Equal(CellFlags.None, erased.Flags);
        Assert.True(erased.Foreground.IsDefault);
        Assert.Equal(TerminalColor.Palette(1), erased.Background);
    }

    [Fact]
    public void Ed1_erases_from_start_to_cursor()
    {
        var emulator = FilledScreen(out _);
        VtHelpers.Feed(emulator, "\u001b[2;4H\u001b[1J");

        Assert.Equal("            ", VtHelpers.RowText(emulator, 0));
        Assert.Equal("    ndLine  ", VtHelpers.RowText(emulator, 1));
        Assert.Equal("ThirdLine   ", VtHelpers.RowText(emulator, 2));
    }

    [Fact]
    public void Ed2_clears_screen_without_moving_cursor()
    {
        var emulator = FilledScreen(out _);
        VtHelpers.Feed(emulator, "\u001b[2;4H\u001b[2J");

        for (int row = 0; row < 4; row++)
        {
            Assert.Equal("            ", VtHelpers.RowText(emulator, row));
        }

        Assert.Equal((1, 3), VtHelpers.Cursor(emulator));
    }

    [Fact]
    public void El0_erases_from_cursor_to_end_of_line()
    {
        var emulator = FilledScreen(out _);
        VtHelpers.Feed(emulator, "\u001b[1;6H\u001b[K");

        Assert.Equal("Hello       ", VtHelpers.RowText(emulator, 0));
        Assert.Equal("SecondLine  ", VtHelpers.RowText(emulator, 1));
    }

    [Fact]
    public void El1_erases_from_start_of_line_to_cursor()
    {
        var emulator = FilledScreen(out _);
        VtHelpers.Feed(emulator, "\u001b[1;6H\u001b[1K");

        Assert.Equal("      orld  ", VtHelpers.RowText(emulator, 0));
        Assert.Equal("SecondLine  ", VtHelpers.RowText(emulator, 1));
    }

    [Fact]
    public void El2_erases_whole_line_and_keeps_other_rows()
    {
        var emulator = FilledScreen(out _);
        VtHelpers.Feed(emulator, "\u001b[1;6H\u001b[2K");

        Assert.Equal("            ", VtHelpers.RowText(emulator, 0));
        Assert.Equal("SecondLine  ", VtHelpers.RowText(emulator, 1));
        Assert.Equal((0, 5), VtHelpers.Cursor(emulator));
    }

    [Fact]
    public void Ech_erases_n_cells_at_cursor()
    {
        var emulator = FilledScreen(out _);
        VtHelpers.Feed(emulator, "\u001b[1;6H\u001b[3X");

        Assert.Equal("Hello   ld  ", VtHelpers.RowText(emulator, 0));
        Assert.Equal((0, 5), VtHelpers.Cursor(emulator));
    }

    [Fact]
    public void Ich_inserts_blank_cells_and_shifts_right()
    {
        var emulator = FilledScreen(out _);
        VtHelpers.Feed(emulator, "\u001b[1;3H\u001b[2@");

        Assert.Equal("He  lloWorld", VtHelpers.RowText(emulator, 0));
        Assert.Equal("SecondLine  ", VtHelpers.RowText(emulator, 1));
    }

    [Fact]
    public void Dch_deletes_cells_and_shifts_left()
    {
        var emulator = FilledScreen(out _);
        VtHelpers.Feed(emulator, "\u001b[1;3H\u001b[2P");

        Assert.Equal("HeoWorld    ", VtHelpers.RowText(emulator, 0));
        Assert.Equal("SecondLine  ", VtHelpers.RowText(emulator, 1));
    }

    [Fact]
    public void Il_inserts_blank_lines_without_scrollback()
    {
        var emulator = FilledScreen(out _);
        int scrollback = emulator.ScrollbackCount;
        VtHelpers.Feed(emulator, "\u001b[2;1H\u001b[2L");

        Assert.Equal("HelloWorld  ", VtHelpers.RowText(emulator, 0));
        Assert.Equal("            ", VtHelpers.RowText(emulator, 1));
        Assert.Equal("            ", VtHelpers.RowText(emulator, 2));
        Assert.Equal("SecondLine  ", VtHelpers.RowText(emulator, 3));
        Assert.Equal(scrollback, emulator.ScrollbackCount);
    }

    [Fact]
    public void Dl_deletes_lines_and_pulls_content_up()
    {
        var emulator = FilledScreen(out _);
        int scrollback = emulator.ScrollbackCount;
        VtHelpers.Feed(emulator, "\u001b[2;1H\u001b[1M");

        Assert.Equal("HelloWorld  ", VtHelpers.RowText(emulator, 0));
        Assert.Equal("ThirdLine   ", VtHelpers.RowText(emulator, 1));
        Assert.Equal("            ", VtHelpers.RowText(emulator, 2));
        Assert.Equal("            ", VtHelpers.RowText(emulator, 3));
        Assert.Equal(scrollback, emulator.ScrollbackCount);
    }

    [Fact]
    public void Su_scrolls_region_up_into_scrollback()
    {
        var emulator = FilledScreen(out _);
        VtHelpers.Feed(emulator, "\u001b[2S");

        Assert.Equal(2, emulator.ScrollbackCount);
        Assert.Equal("HelloWorld  ", VtHelpers.LineText(emulator.GetLine(0)));
        Assert.Equal("SecondLine  ", VtHelpers.LineText(emulator.GetLine(1)));
        Assert.Equal("ThirdLine   ", VtHelpers.RowText(emulator, 0));
        Assert.Equal("            ", VtHelpers.RowText(emulator, 1));
    }

    [Fact]
    public void Sd_scrolls_region_down_without_scrollback()
    {
        var emulator = FilledScreen(out _);
        VtHelpers.Feed(emulator, "\u001b[2T");

        Assert.Equal(0, emulator.ScrollbackCount);
        Assert.Equal("            ", VtHelpers.RowText(emulator, 0));
        Assert.Equal("            ", VtHelpers.RowText(emulator, 1));
        Assert.Equal("HelloWorld  ", VtHelpers.RowText(emulator, 2));
        Assert.Equal("SecondLine  ", VtHelpers.RowText(emulator, 3));
    }

    [Fact]
    public void Rep_repeats_last_printed_character()
    {
        var emulator = VtHelpers.Create(12, 4, 16, out _);

        VtHelpers.Feed(emulator, "AB\u001b[3b");

        Assert.Equal("ABBBB       ", VtHelpers.RowText(emulator, 0));
        Assert.Equal((0, 5), VtHelpers.Cursor(emulator));
    }

    [Fact]
    public void Rep_with_default_count_repeats_once()
    {
        var emulator = VtHelpers.Create(12, 4, 16, out _);

        VtHelpers.Feed(emulator, "A\u001b[b");

        Assert.Equal("AA          ", VtHelpers.RowText(emulator, 0));
    }
}
