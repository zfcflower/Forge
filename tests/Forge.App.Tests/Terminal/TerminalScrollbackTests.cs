using Forge.App.Terminal.Vt;
using Xunit;

namespace Forge.App.Tests.Terminal;

/// <summary>
/// Covers scrolling regions (DECSTBM), full-screen scrolling into the scrollback ring,
/// the scrollback limit (growth, trimming, shrinking), <c>CSI 3 J</c> / ClearScrollback,
/// and the <see cref="TerminalEmulator.GetLine"/> index rules.
/// </summary>
public sealed class TerminalScrollbackTests
{
    private static TerminalEmulator ScreenWithLines(int columns, int rows, int scrollback, params string[] lines)
    {
        var emulator = VtHelpers.Create(columns, rows, scrollback, out _);
        foreach (string line in lines)
        {
            VtHelpers.Feed(emulator, line + "\r\n");
        }

        return emulator;
    }

    [Fact]
    public void Decstbm_scrolls_only_inside_the_region()
    {
        var emulator = VtHelpers.Create(10, 5, 8, out _);
        VtHelpers.Feed(emulator, "L0\r\nL1\r\nL2\r\nL3\r\nL4");
        VtHelpers.Feed(emulator, "\u001b[2;4r\u001b[4;1H\n");

        // Rows are 1-based in the sequence: region covers screen rows 1..3, cursor was on row 3.
        Assert.Equal("L0        ", VtHelpers.RowText(emulator, 0));
        Assert.Equal("L2        ", VtHelpers.RowText(emulator, 1));
        Assert.Equal("L3        ", VtHelpers.RowText(emulator, 2));
        Assert.Equal("          ", VtHelpers.RowText(emulator, 3));
        Assert.Equal("L4        ", VtHelpers.RowText(emulator, 4));
        Assert.Equal(0, emulator.ScrollbackCount);
    }

    [Fact]
    public void Linefeed_at_region_bottom_does_not_touch_scrollback()
    {
        var emulator = VtHelpers.Create(10, 5, 8, out _);
        VtHelpers.Feed(emulator, "L0\r\nL1\r\nL2\r\nL3\r\nL4");
        VtHelpers.Feed(emulator, "\u001b[2;4r\u001b[4;1H\n\n");

        Assert.Equal("L3        ", VtHelpers.RowText(emulator, 1));
        Assert.Equal("          ", VtHelpers.RowText(emulator, 2));
        Assert.Equal("          ", VtHelpers.RowText(emulator, 3));
        Assert.Equal(0, emulator.ScrollbackCount);
    }

    [Fact]
    public void Full_screen_scroll_pushes_oldest_line_into_scrollback()
    {
        var emulator = ScreenWithLines(10, 4, 8, "1", "2", "3", "4", "5");

        Assert.Equal(2, emulator.ScrollbackCount);
        Assert.Equal(6, emulator.TotalLines);
        Assert.Equal("1         ", VtHelpers.LineText(emulator.GetLine(0)));
        Assert.Equal("2         ", VtHelpers.LineText(emulator.GetLine(1)));
        Assert.Equal("3         ", VtHelpers.LineText(emulator.GetLine(2)));
        Assert.Equal("4         ", VtHelpers.LineText(emulator.GetLine(3)));
        Assert.Equal("5         ", VtHelpers.LineText(emulator.GetLine(4)));
        Assert.Equal("          ", VtHelpers.LineText(emulator.GetLine(5)));
    }

    [Fact]
    public void Scrollback_ring_keeps_newest_lines_when_full()
    {
        var emulator = ScreenWithLines(10, 4, 3, "1", "2", "3", "4", "5", "6", "7");

        Assert.Equal(3, emulator.ScrollbackCount);
        Assert.Equal("2         ", VtHelpers.LineText(emulator.GetLine(0)));
        Assert.Equal("3         ", VtHelpers.LineText(emulator.GetLine(1)));
        Assert.Equal("4         ", VtHelpers.LineText(emulator.GetLine(2)));
    }

    [Fact]
    public void Lowering_scrollback_limit_trims_oldest_immediately()
    {
        var emulator = ScreenWithLines(10, 4, 8, "1", "2", "3", "4", "5");
        Assert.Equal(2, emulator.ScrollbackCount);
        long version = emulator.Version;

        emulator.ScrollbackLimit = 1;

        Assert.Equal(1, emulator.ScrollbackCount);
        Assert.Equal("2         ", VtHelpers.LineText(emulator.GetLine(0)));
        Assert.True(emulator.Version > version);
    }

    [Fact]
    public void Scrollback_limit_zero_discards_scrolled_lines()
    {
        var emulator = ScreenWithLines(10, 4, 0, "1", "2", "3");

        Assert.Equal(0, emulator.ScrollbackCount);
        Assert.Equal(4, emulator.TotalLines);
    }

    [Fact]
    public void Csi_3j_clears_scrollback_and_keeps_screen()
    {
        var emulator = ScreenWithLines(10, 4, 8, "1", "2", "3", "4", "5");
        Assert.Equal(2, emulator.ScrollbackCount);

        VtHelpers.Feed(emulator, "\u001b[3J");

        Assert.Equal(0, emulator.ScrollbackCount);
        Assert.Equal("5         ", VtHelpers.RowText(emulator, 2));
    }

    [Fact]
    public void ClearScrollback_drops_lines_and_bumps_version()
    {
        var emulator = ScreenWithLines(10, 4, 8, "1", "2", "3", "4", "5");
        long version = emulator.Version;

        emulator.ClearScrollback();

        Assert.Equal(0, emulator.ScrollbackCount);
        Assert.True(emulator.Version > version);
    }

    [Fact]
    public void GetLine_rejects_out_of_range_indices()
    {
        var emulator = ScreenWithLines(10, 4, 8, "1", "2");

        Assert.Throws<ArgumentOutOfRangeException>(() => emulator.GetLine(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => emulator.GetLine(emulator.TotalLines));
    }

    [Fact]
    public void Reverse_index_at_region_top_scrolls_region_down()
    {
        var emulator = VtHelpers.Create(10, 5, 8, out _);
        VtHelpers.Feed(emulator, "L0\r\nL1\r\nL2\r\nL3\r\nL4");
        // Region rows 2..4 (1-based), cursor on the top margin, then RI (ESC M).
        VtHelpers.Feed(emulator, "\u001b[2;4r\u001b[2;1H\u001bM");

        Assert.Equal("L0        ", VtHelpers.RowText(emulator, 0));
        Assert.Equal("          ", VtHelpers.RowText(emulator, 1));
        Assert.Equal("L1        ", VtHelpers.RowText(emulator, 2));
        Assert.Equal("L2        ", VtHelpers.RowText(emulator, 3));
        Assert.Equal("L4        ", VtHelpers.RowText(emulator, 4));
        Assert.Equal(0, emulator.ScrollbackCount);
    }
}
