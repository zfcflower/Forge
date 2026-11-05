using Forge.App.Terminal.Vt;
using Xunit;

namespace Forge.App.Tests.Terminal;

/// <summary>
/// Covers the DEC line-drawing charset: <c>ESC ( 0</c> maps printable ASCII onto the
/// box-drawing Unicode range, <c>ESC ( B</c> restores plain ASCII, and <c>SO</c>/<c>SI</c>
/// switch to/from the G1 set.
/// </summary>
public sealed class TerminalCharsetTests
{
    [Fact]
    public void Graphics_charset_maps_q_to_box_drawing()
    {
        var emulator = VtHelpers.Create(10, 4, 16, out _);

        VtHelpers.Feed(emulator, "\u001b(0q");

        Assert.Equal(0x2500, VtHelpers.ScreenCell(emulator, 0, 0).Rune);
    }

    [Fact]
    public void Graphics_charset_maps_corners_and_junctions()
    {
        var emulator = VtHelpers.Create(10, 4, 16, out _);

        VtHelpers.Feed(emulator, "\u001b(0jklmn");

        Assert.Equal(0x2518, VtHelpers.ScreenCell(emulator, 0, 0).Rune);
        Assert.Equal(0x2510, VtHelpers.ScreenCell(emulator, 0, 1).Rune);
        Assert.Equal(0x250C, VtHelpers.ScreenCell(emulator, 0, 2).Rune);
        Assert.Equal(0x2514, VtHelpers.ScreenCell(emulator, 0, 3).Rune);
        Assert.Equal(0x253C, VtHelpers.ScreenCell(emulator, 0, 4).Rune);
    }

    [Fact]
    public void Ascii_charset_restores_plain_letters()
    {
        var emulator = VtHelpers.Create(10, 4, 16, out _);

        VtHelpers.Feed(emulator, "\u001b(0q\u001b(Bq");

        Assert.Equal(0x2500, VtHelpers.ScreenCell(emulator, 0, 0).Rune);
        Assert.Equal('q', (char)VtHelpers.ScreenCell(emulator, 0, 1).Rune);
    }

    [Fact]
    public void G1_charset_selected_by_so_maps_the_same_glyphs()
    {
        var emulator = VtHelpers.Create(10, 4, 16, out _);

        VtHelpers.Feed(emulator, "\u001b)0\u000Eq\u000Fq");

        Assert.Equal(0x2500, VtHelpers.ScreenCell(emulator, 0, 0).Rune);
        Assert.Equal('q', (char)VtHelpers.ScreenCell(emulator, 0, 1).Rune);
    }

    [Fact]
    public void Attributes_apply_to_graphics_cells()
    {
        var emulator = VtHelpers.Create(10, 4, 16, out _);

        VtHelpers.Feed(emulator, "\u001b[1m\u001b(0q");

        TerminalCell cell = VtHelpers.ScreenCell(emulator, 0, 0);
        Assert.Equal(0x2500, cell.Rune);
        Assert.Equal(CellFlags.Bold, cell.Flags);
    }
}
