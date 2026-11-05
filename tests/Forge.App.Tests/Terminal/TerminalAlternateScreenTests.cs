using Forge.App.Terminal.Vt;
using Xunit;

namespace Forge.App.Tests.Terminal;

/// <summary>
/// Covers the alternate screen: 1049 (also 47 and 1047) switches to a blank buffer with no
/// scrollback, preserves the primary screen and its scrollback, and restores the entry cursor
/// on exit. 1048 only saves/restores the cursor. The three "enter" modes differ exactly in
/// which side they clear.
/// </summary>
public sealed class TerminalAlternateScreenTests
{
    private static TerminalEmulator MainWithScrollback(out VtTestHost host)
    {
        var emulator = VtHelpers.Create(10, 4, 8, out host);
        VtHelpers.Feed(emulator, "row1\r\nrow2\r\nrow3\r\nrow4\r\nrow5\r\nrow6");
        // Screen now shows row3..row6; scrollback holds row1, row2.
        VtHelpers.Feed(emulator, "\u001b[3;4H");
        return emulator;
    }

    [Fact]
    public void Enter_1049_shows_blank_screen_with_no_scrollback()
    {
        var emulator = MainWithScrollback(out _);

        VtHelpers.Feed(emulator, "\u001b[?1049h");

        Assert.True(emulator.IsAlternateScreen);
        Assert.True((emulator.Modes & TerminalModes.AlternateScreen) != 0);
        Assert.Equal(0, emulator.ScrollbackCount);
        for (int row = 0; row < 4; row++)
        {
            Assert.Equal("          ", VtHelpers.RowText(emulator, row));
        }

        Assert.Equal((0, 0), VtHelpers.Cursor(emulator));
    }

    [Fact]
    public void Exit_1049_restores_content_scrollback_and_cursor()
    {
        var emulator = MainWithScrollback(out _);

        VtHelpers.Feed(emulator, "\u001b[?1049h");
        VtHelpers.Feed(emulator, "ALT\r\nsecond");
        VtHelpers.Feed(emulator, "\u001b[?1049l");

        Assert.False(emulator.IsAlternateScreen);
        Assert.True((emulator.Modes & TerminalModes.AlternateScreen) == 0);
        Assert.Equal(2, emulator.ScrollbackCount);
        Assert.Equal("row1      ", VtHelpers.LineText(emulator.GetLine(0)));
        Assert.Equal("row2      ", VtHelpers.LineText(emulator.GetLine(1)));
        Assert.Equal("row5      ", VtHelpers.RowText(emulator, 2));
        Assert.Equal((2, 3), VtHelpers.Cursor(emulator));
    }

    [Fact]
    public void Alternate_screen_scrolling_never_reaches_scrollback()
    {
        var emulator = MainWithScrollback(out _);
        VtHelpers.Feed(emulator, "\u001b[?1049h");
        VtHelpers.Feed(emulator, "a\r\nb\r\nc\r\nd\r\ne\r\nf");

        Assert.Equal(0, emulator.ScrollbackCount);
        Assert.Equal("e         ", VtHelpers.RowText(emulator, 2));
        Assert.Equal("f         ", VtHelpers.RowText(emulator, 3));

        VtHelpers.Feed(emulator, "\u001b[?1049l");
        Assert.Equal(2, emulator.ScrollbackCount);
    }

    [Fact]
    public void Mode_47_preserves_alternate_content_across_switches()
    {
        var emulator = MainWithScrollback(out _);

        VtHelpers.Feed(emulator, "\u001b[?47h");
        Assert.True(emulator.IsAlternateScreen);
        VtHelpers.Feed(emulator, "kept");
        VtHelpers.Feed(emulator, "\u001b[?47l");
        Assert.False(emulator.IsAlternateScreen);
        Assert.Equal("row5      ", VtHelpers.RowText(emulator, 2));

        VtHelpers.Feed(emulator, "\u001b[?47h");
        Assert.Equal("kept      ", VtHelpers.RowText(emulator, 0));

        VtHelpers.Feed(emulator, "\u001b[?47l");
    }

    [Fact]
    public void Mode_1047_clears_alternate_content_on_exit()
    {
        var emulator = MainWithScrollback(out _);

        VtHelpers.Feed(emulator, "\u001b[?1047h");
        VtHelpers.Feed(emulator, "doomed");
        VtHelpers.Feed(emulator, "\u001b[?1047l");
        Assert.Equal("row5      ", VtHelpers.RowText(emulator, 2));

        VtHelpers.Feed(emulator, "\u001b[?1047h");
        Assert.Equal("          ", VtHelpers.RowText(emulator, 0));

        VtHelpers.Feed(emulator, "\u001b[?1047l");
    }

    [Fact]
    public void Mode_1049_clears_alternate_content_on_enter()
    {
        var emulator = MainWithScrollback(out _);

        VtHelpers.Feed(emulator, "\u001b[?1047h");
        VtHelpers.Feed(emulator, "stale");
        VtHelpers.Feed(emulator, "\u001b[?1047l");

        VtHelpers.Feed(emulator, "\u001b[?1049h");
        Assert.Equal("          ", VtHelpers.RowText(emulator, 0));

        VtHelpers.Feed(emulator, "\u001b[?1049l");
    }

    [Fact]
    public void Mode_1048_saves_and_restores_cursor_without_switching_screen()
    {
        var emulator = MainWithScrollback(out _);

        VtHelpers.Feed(emulator, "\u001b[?1048h");
        VtHelpers.Feed(emulator, "\u001b[1;1H");
        Assert.Equal((0, 0), VtHelpers.Cursor(emulator));
        VtHelpers.Feed(emulator, "\u001b[?1048l");

        Assert.False(emulator.IsAlternateScreen);
        Assert.Equal((2, 3), VtHelpers.Cursor(emulator));
        Assert.Equal("row5      ", VtHelpers.RowText(emulator, 2));
    }
}
