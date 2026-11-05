using Forge.App.Terminal.Vt;
using Xunit;

namespace Forge.App.Tests.Terminal;

/// <summary>
/// Covers the mode switches: DECSET/DECRST for 1 (DECCKM), 6 (origin), 7 (autowrap), 12, 25,
/// 1000/1002/1003 (mouse tracking), 1006 (SGR mouse), 2004 (bracketed paste) and 2026
/// (synchronized output); DECSTR (<c>CSI ! p</c>) resetting to a known state; the insert-mode
/// (CSI 4h/l) editing behavior; and LNM (CSI 20h/l).
/// </summary>
public sealed class TerminalModesTests
{
    public static IEnumerable<object[]> DecModes()
    {
        yield return new object[] { 1, (int)TerminalModes.ApplicationCursorKeys };
        yield return new object[] { 5, (int)TerminalModes.ReverseVideo };
        yield return new object[] { 6, (int)TerminalModes.Origin };
        yield return new object[] { 7, (int)TerminalModes.AutoWrap };
        yield return new object[] { 1004, (int)TerminalModes.FocusEvents };
        yield return new object[] { 2004, (int)TerminalModes.BracketedPaste };
        yield return new object[] { 2026, (int)TerminalModes.SynchronizedOutput };
    }

    [Theory]
    [MemberData(nameof(DecModes))]
    public void Decset_sets_and_decrst_clears_flag_modes(int mode, int flag)
    {
        var emulator = VtHelpers.Create(10, 4, 16, out _);
        var flagMode = (TerminalModes)flag;

        VtHelpers.Feed(emulator, $"\u001b[?{mode}h");
        Assert.True((emulator.Modes & flagMode) != 0);

        // Reset always clears the flag, including AutoWrap (mode 7) which starts out set.
        VtHelpers.Feed(emulator, $"\u001b[?{mode}l");
        Assert.Equal(TerminalModes.None, emulator.Modes & flagMode);
    }

    [Theory]
    [InlineData(1000, (int)MouseTracking.Normal)]
    [InlineData(1002, (int)MouseTracking.ButtonEvent)]
    [InlineData(1003, (int)MouseTracking.AnyEvent)]
    public void Decset_selects_mouse_tracking_mode(int mode, int expected)
    {
        var emulator = VtHelpers.Create(10, 4, 16, out _);

        VtHelpers.Feed(emulator, $"\u001b[?{mode}h");
        Assert.Equal((MouseTracking)expected, emulator.MouseMode);

        VtHelpers.Feed(emulator, $"\u001b[?{mode}l");
        Assert.Equal(MouseTracking.None, emulator.MouseMode);
    }

    [Fact]
    public void Decset_1006_toggles_sgr_mouse()
    {
        var emulator = VtHelpers.Create(10, 4, 16, out _);

        VtHelpers.Feed(emulator, "\u001b[?1006h");
        Assert.True(emulator.MouseSgr);

        VtHelpers.Feed(emulator, "\u001b[?1006l");
        Assert.False(emulator.MouseSgr);
    }

    [Fact]
    public void Decset_12_and_25_toggle_blink_and_visibility()
    {
        var emulator = VtHelpers.Create(10, 4, 16, out _);

        VtHelpers.Feed(emulator, "\u001b[?12l");
        Assert.False(emulator.Cursor.Blinking);
        VtHelpers.Feed(emulator, "\u001b[?12h");
        Assert.True(emulator.Cursor.Blinking);

        VtHelpers.Feed(emulator, "\u001b[?25l");
        Assert.False(emulator.Cursor.Visible);
        VtHelpers.Feed(emulator, "\u001b[?25h");
        Assert.True(emulator.Cursor.Visible);
    }

    [Fact]
    public void Origin_mode_makes_cursor_addressing_region_relative()
    {
        var emulator = VtHelpers.Create(10, 5, 16, out _);
        VtHelpers.Feed(emulator, "\u001b[2;4r\u001b[?6h\u001b[1;1H");

        Assert.True((emulator.Modes & TerminalModes.Origin) != 0);
        Assert.Equal((1, 0), VtHelpers.Cursor(emulator));

        VtHelpers.Feed(emulator, "\u001b[?6l\u001b[1;1H");
        Assert.Equal((0, 0), VtHelpers.Cursor(emulator));
    }

    [Fact]
    public void Decset_9_selects_x10_mouse_tracking()
    {
        var emulator = VtHelpers.Create(10, 4, 16, out _);

        VtHelpers.Feed(emulator, "\u001b[?9h");
        Assert.Equal(MouseTracking.X10, emulator.MouseMode);

        VtHelpers.Feed(emulator, "\u001b[?9l");
        Assert.Equal(MouseTracking.None, emulator.MouseMode);
    }

    [Fact]
    public void Decscusc_sets_cursor_shape_and_blink()
    {
        var emulator = VtHelpers.Create(10, 4, 16, out _);

        VtHelpers.Feed(emulator, "\u001b[2 q");
        Assert.Equal(CursorShape.Block, emulator.Cursor.Shape);
        Assert.False(emulator.Cursor.Blinking);

        VtHelpers.Feed(emulator, "\u001b[4 q");
        Assert.Equal(CursorShape.Underline, emulator.Cursor.Shape);
        Assert.False(emulator.Cursor.Blinking);

        VtHelpers.Feed(emulator, "\u001b[5 q");
        Assert.Equal(CursorShape.Bar, emulator.Cursor.Shape);
        Assert.True(emulator.Cursor.Blinking);
    }

    [Fact]
    public void Escape_7_and_8_save_and_restore_cursor()
    {
        var emulator = VtHelpers.Create(10, 4, 16, out _);
        VtHelpers.Feed(emulator, "\u001b[2;3H");

        VtHelpers.Feed(emulator, "\u001b7\u001b[1;1HX\u001b8");

        Assert.Equal("X         ", VtHelpers.RowText(emulator, 0));
        Assert.Equal((1, 2), VtHelpers.Cursor(emulator));
    }

    [Fact]
    public void Ris_full_reset_restores_initial_state()
    {
        var emulator = VtHelpers.Create(10, 4, 16, out _);
        VtHelpers.Feed(emulator, "junk\r\n\u001b[?1h\u001b[31m\u001b[1;1H");

        VtHelpers.Feed(emulator, "\u001bc");

        Assert.Equal(TerminalModes.AutoWrap, emulator.Modes);
        Assert.Equal((0, 0), VtHelpers.Cursor(emulator));
        Assert.Equal(0, emulator.ScrollbackCount);
        Assert.Equal("          ", VtHelpers.RowText(emulator, 0));
        Assert.Equal(MouseTracking.None, emulator.MouseMode);
    }

    [Fact]
    public void Decstr_resets_to_a_known_state()
    {
        var emulator = VtHelpers.Create(10, 5, 16, out _);
        VtHelpers.Feed(emulator, "\u001b[?1h\u001b[?6h\u001b[4h\u001b[20h\u001b[2;3r\u001b[3;5H\u001b[1;31m");

        VtHelpers.Feed(emulator, "\u001b[!p");

        Assert.Equal(TerminalModes.AutoWrap, emulator.Modes);
        Assert.Equal((0, 0), VtHelpers.Cursor(emulator));

        // Scroll region is full again: the cursor can reach the last row.
        VtHelpers.Feed(emulator, "\u001b[5;1H");
        Assert.Equal((4, 0), VtHelpers.Cursor(emulator));

        // Insert mode is off: printing overwrites instead of shifting.
        VtHelpers.Feed(emulator, "abc\rX");
        Assert.Equal("Xbc       ", VtHelpers.RowText(emulator, 4));

        // LNM is off: a line feed keeps the column.
        VtHelpers.Feed(emulator, "\n");
        Assert.Equal((4, 1), VtHelpers.Cursor(emulator));
    }

    [Fact]
    public void Insert_mode_shifts_cells_right_and_reset_overwrites()
    {
        var emulator = VtHelpers.Create(12, 4, 16, out _);
        VtHelpers.Feed(emulator, "abc");

        VtHelpers.Feed(emulator, "\u001b[1;2H\u001b[4hX");
        Assert.Equal("aXbc        ", VtHelpers.RowText(emulator, 0));

        VtHelpers.Feed(emulator, "\u001b[4lY");
        Assert.Equal("aXYc        ", VtHelpers.RowText(emulator, 0));
    }

    [Fact]
    public void Lnm_makes_linefeed_also_return_the_carriage()
    {
        var emulator = VtHelpers.Create(10, 4, 16, out _);
        VtHelpers.Feed(emulator, "\u001b[1;6H");

        VtHelpers.Feed(emulator, "\u001b[20h\n");
        Assert.Equal((1, 0), VtHelpers.Cursor(emulator));

        VtHelpers.Feed(emulator, "\u001b[20l\u001b[1;6H\n");
        Assert.Equal((1, 5), VtHelpers.Cursor(emulator));
    }
}
