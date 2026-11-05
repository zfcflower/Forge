using Forge.App.Terminal.Vt;
using Xunit;

namespace Forge.App.Tests.Terminal;

/// <summary>
/// Covers SGR: the 16 basic colors, bright variants, 256-color and truecolor forms
/// (semicolon and colon style), underline sub-styles, every attribute on/off pair,
/// and the 39/49 default-color resets. Each case pins the exact cell colors and flags.
/// </summary>
public sealed class TerminalSgrTests
{
    private static TerminalCell PrintWithSgr(string sgr, char letter = 'X')
    {
        var emulator = VtHelpers.Create(10, 4, 16, out _);
        VtHelpers.Feed(emulator, "\u001b[" + sgr + "m" + letter);
        return VtHelpers.ScreenCell(emulator, 0, 0);
    }

    public static IEnumerable<object[]> BasicForegroundCases()
    {
        for (int i = 0; i < 8; i++)
        {
            yield return new object[] { 30 + i, i };
        }
    }

    public static IEnumerable<object[]> BasicBackgroundCases()
    {
        for (int i = 0; i < 8; i++)
        {
            yield return new object[] { 40 + i, i };
        }
    }

    public static IEnumerable<object[]> BrightForegroundCases()
    {
        for (int i = 0; i < 8; i++)
        {
            yield return new object[] { 90 + i, 8 + i };
        }
    }

    public static IEnumerable<object[]> BrightBackgroundCases()
    {
        for (int i = 0; i < 8; i++)
        {
            yield return new object[] { 100 + i, 8 + i };
        }
    }

    [Theory]
    [MemberData(nameof(BasicForegroundCases))]
    public void Basic_foreground_sets_palette_index(int code, int expected)
    {
        TerminalCell cell = PrintWithSgr(code.ToString());

        Assert.Equal(TerminalColor.Palette(expected), cell.Foreground);
        Assert.True(cell.Background.IsDefault);
        Assert.Equal('X', (char)cell.Rune);
    }

    [Theory]
    [MemberData(nameof(BasicBackgroundCases))]
    public void Basic_background_sets_palette_index(int code, int expected)
    {
        TerminalCell cell = PrintWithSgr(code.ToString());

        Assert.Equal(TerminalColor.Palette(expected), cell.Background);
        Assert.True(cell.Foreground.IsDefault);
    }

    [Theory]
    [MemberData(nameof(BrightForegroundCases))]
    public void Bright_foreground_sets_high_palette_index(int code, int expected)
    {
        TerminalCell cell = PrintWithSgr(code.ToString());

        Assert.Equal(TerminalColor.Palette(expected), cell.Foreground);
    }

    [Theory]
    [MemberData(nameof(BrightBackgroundCases))]
    public void Bright_background_sets_high_palette_index(int code, int expected)
    {
        TerminalCell cell = PrintWithSgr(code.ToString());

        Assert.Equal(TerminalColor.Palette(expected), cell.Background);
    }

    [Theory]
    [InlineData("38;5;200", 200)]
    [InlineData("38;5;0", 0)]
    [InlineData("38;5;255", 255)]
    public void Palette_256_foreground_form(string sgr, int expected)
    {
        TerminalCell cell = PrintWithSgr(sgr);

        Assert.Equal(TerminalColor.Palette(expected), cell.Foreground);
        Assert.True(cell.Background.IsDefault);
    }

    [Theory]
    [InlineData("48;5;200", 200)]
    [InlineData("48;5;0", 0)]
    [InlineData("48;5;255", 255)]
    public void Palette_256_background_form(string sgr, int expected)
    {
        TerminalCell cell = PrintWithSgr(sgr);

        Assert.Equal(TerminalColor.Palette(expected), cell.Background);
        Assert.True(cell.Foreground.IsDefault);
    }

    [Fact]
    public void Truecolor_semicolon_form()
    {
        TerminalCell cell = PrintWithSgr("38;2;10;20;30");

        Assert.Equal(TerminalColor.Rgb(10, 20, 30), cell.Foreground);
    }

    [Fact]
    public void Truecolor_background_semicolon_form()
    {
        TerminalCell cell = PrintWithSgr("48;2;200;100;50");

        Assert.Equal(TerminalColor.Rgb(200, 100, 50), cell.Background);
    }

    [Fact]
    public void Truecolor_colon_form_with_empty_second_field()
    {
        TerminalCell cell = PrintWithSgr("38:2::10:20:30");

        Assert.Equal(TerminalColor.Rgb(10, 20, 30), cell.Foreground);
    }

    [Fact]
    public void Truecolor_colon_form_without_empty_field()
    {
        TerminalCell cell = PrintWithSgr("38:2:10:20:30");

        Assert.Equal(TerminalColor.Rgb(10, 20, 30), cell.Foreground);
    }

    [Fact]
    public void Palette_256_colon_form()
    {
        TerminalCell cell = PrintWithSgr("38:5:77");

        Assert.Equal(TerminalColor.Palette(77), cell.Foreground);
    }

    [Theory]
    [InlineData("1", (int)CellFlags.Bold)]
    [InlineData("2", (int)CellFlags.Dim)]
    [InlineData("3", (int)CellFlags.Italic)]
    [InlineData("4", (int)CellFlags.Underline)]
    [InlineData("5", (int)CellFlags.Blink)]
    [InlineData("7", (int)CellFlags.Inverse)]
    [InlineData("8", (int)CellFlags.Hidden)]
    [InlineData("9", (int)CellFlags.Strikethrough)]
    public void Single_attributes_set_their_flag(string sgr, int expected)
    {
        TerminalCell cell = PrintWithSgr(sgr);

        Assert.Equal((CellFlags)expected, cell.Flags);
    }

    [Fact]
    public void Underline_substyles_select_single_or_double()
    {
        Assert.Equal(CellFlags.Underline, PrintWithSgr("4").Flags);
        Assert.Equal(CellFlags.Underline, PrintWithSgr("4:1").Flags);
        Assert.Equal(CellFlags.DoubleUnderline, PrintWithSgr("4:2").Flags);
        Assert.Equal(CellFlags.None, PrintWithSgr("4:0").Flags);
        Assert.Equal(CellFlags.DoubleUnderline, PrintWithSgr("21").Flags);
    }

    [Fact]
    public void Attribute_off_codes_clear_only_their_flag()
    {
        var emulator = VtHelpers.Create(10, 4, 16, out _);
        VtHelpers.Feed(emulator, "\u001b[1;3;4;5;7;8;9mX");
        TerminalCell before = VtHelpers.ScreenCell(emulator, 0, 0);
        Assert.Equal(
            CellFlags.Bold | CellFlags.Italic | CellFlags.Underline | CellFlags.Blink |
            CellFlags.Inverse | CellFlags.Hidden | CellFlags.Strikethrough,
            before.Flags);

        VtHelpers.Feed(emulator, "\u001b[22;23;24;25;27;28;29mY");
        TerminalCell after = VtHelpers.ScreenCell(emulator, 0, 1);
        Assert.Equal(CellFlags.None, after.Flags);
        Assert.Equal('Y', (char)after.Rune);
    }

    [Fact]
    public void Sgr_0_resets_everything()
    {
        var emulator = VtHelpers.Create(10, 4, 16, out _);
        VtHelpers.Feed(emulator, "\u001b[1;31;42mX\u001b[0mY");

        TerminalCell styled = VtHelpers.ScreenCell(emulator, 0, 0);
        Assert.Equal(CellFlags.Bold, styled.Flags);
        Assert.Equal(TerminalColor.Palette(1), styled.Foreground);
        Assert.Equal(TerminalColor.Palette(2), styled.Background);

        TerminalCell plain = VtHelpers.ScreenCell(emulator, 0, 1);
        Assert.Equal(CellFlags.None, plain.Flags);
        Assert.True(plain.Foreground.IsDefault);
        Assert.True(plain.Background.IsDefault);
    }

    [Fact]
    public void Foreground_and_background_reset_codes_restore_defaults()
    {
        var emulator = VtHelpers.Create(10, 4, 16, out _);
        VtHelpers.Feed(emulator, "\u001b[31;44mX\u001b[39mY\u001b[49mZ");

        Assert.Equal(TerminalColor.Palette(1), VtHelpers.ScreenCell(emulator, 0, 0).Foreground);
        Assert.Equal(TerminalColor.Palette(4), VtHelpers.ScreenCell(emulator, 0, 0).Background);
        Assert.True(VtHelpers.ScreenCell(emulator, 0, 1).Foreground.IsDefault);
        Assert.Equal(TerminalColor.Palette(4), VtHelpers.ScreenCell(emulator, 0, 1).Background);
        Assert.True(VtHelpers.ScreenCell(emulator, 0, 2).Foreground.IsDefault);
        Assert.True(VtHelpers.ScreenCell(emulator, 0, 2).Background.IsDefault);
    }

    [Fact]
    public void Empty_sgr_resets_like_zero()
    {
        TerminalCell cell = PrintWithSgr(string.Empty);

        Assert.Equal(CellFlags.None, cell.Flags);
        Assert.True(cell.Foreground.IsDefault);
        Assert.True(cell.Background.IsDefault);
    }
}
