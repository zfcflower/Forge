using Forge.App.Terminal.Vt;
using Xunit;

namespace Forge.App.Tests.Terminal;

/// <summary>
/// Covers <see cref="TerminalEmulator.Resize"/>: dimensions update, the cursor is clamped back
/// on screen, shrinking rows keeps the bottom content visible by pushing top lines into the
/// scrollback, the version bumps, same-size resizes are free, and widening never reflows.
/// </summary>
public sealed class TerminalResizeTests
{
    [Fact]
    public void Resize_updates_dimensions_and_clamps_cursor()
    {
        var emulator = VtHelpers.Create(10, 4, 16, out _);
        VtHelpers.Feed(emulator, "\u001b[4;10H");

        emulator.Resize(6, 2);

        Assert.Equal(6, emulator.Columns);
        Assert.Equal(2, emulator.Rows);
        Assert.Equal((1, 5), VtHelpers.Cursor(emulator));
    }

    [Fact]
    public void Shrinking_rows_pushes_top_lines_into_scrollback_bottom_stays_visible()
    {
        var emulator = VtHelpers.Create(10, 4, 16, out _);
        VtHelpers.Feed(emulator, "R0\r\nR1\r\nR2\r\nR3");
        long version = emulator.Version;

        emulator.Resize(10, 2);

        Assert.True(emulator.Version > version);
        Assert.Equal(2, emulator.ScrollbackCount);
        Assert.Equal("R0        ", VtHelpers.LineText(emulator.GetLine(0)));
        Assert.Equal("R1        ", VtHelpers.LineText(emulator.GetLine(1)));
        Assert.Equal("R2        ", VtHelpers.RowText(emulator, 0));
        Assert.Equal("R3        ", VtHelpers.RowText(emulator, 1));
        Assert.Equal(4, emulator.TotalLines);
    }

    [Fact]
    public void Growing_rows_keeps_content_and_moves_cursor_with_it()
    {
        var emulator = VtHelpers.Create(10, 2, 16, out _);
        VtHelpers.Feed(emulator, "R0\r\nR1");
        Assert.Equal(0, emulator.ScrollbackCount);

        emulator.Resize(10, 4);

        Assert.Equal("R0        ", VtHelpers.RowText(emulator, 0));
        Assert.Equal("R1        ", VtHelpers.RowText(emulator, 1));
        Assert.Equal("          ", VtHelpers.RowText(emulator, 2));
        Assert.Equal("          ", VtHelpers.RowText(emulator, 3));
        Assert.Equal(0, emulator.ScrollbackCount);
    }

    [Fact]
    public void Narrowing_truncates_right_cells_and_resets_margins()
    {
        var emulator = VtHelpers.Create(10, 4, 16, out _);
        VtHelpers.Feed(emulator, "0123456789\u001b[2;4r\u001b[1;1H");

        emulator.Resize(6, 4);

        Assert.Equal("012345", VtHelpers.RowText(emulator, 0));
        Assert.Equal((0, 0), VtHelpers.Cursor(emulator));

        // The scroll region was reset to full height: the cursor reaches the last row.
        VtHelpers.Feed(emulator, "\u001b[4;1H");
        Assert.Equal((3, 0), VtHelpers.Cursor(emulator));
    }

    [Fact]
    public void Widening_pads_without_reflowing_wrapped_lines()
    {
        var emulator = VtHelpers.Create(6, 4, 16, out _);
        VtHelpers.Feed(emulator, "012345ABCDEF");

        emulator.Resize(12, 4);

        Assert.Equal("012345      ", VtHelpers.RowText(emulator, 0));
        Assert.Equal("ABCDEF      ", VtHelpers.RowText(emulator, 1));

        emulator.Resize(6, 4);

        Assert.Equal("012345", VtHelpers.RowText(emulator, 0));
        Assert.Equal("ABCDEF", VtHelpers.RowText(emulator, 1));
    }

    [Fact]
    public void Same_size_resize_does_not_bump_version()
    {
        var emulator = VtHelpers.Create(10, 4, 16, out _);
        VtHelpers.Feed(emulator, "hi");
        long version = emulator.Version;

        emulator.Resize(10, 4);

        Assert.Equal(version, emulator.Version);
        Assert.Equal("hi        ", VtHelpers.RowText(emulator, 0));
    }

    [Fact]
    public void Feed_after_resize_writes_within_new_bounds()
    {
        var emulator = VtHelpers.Create(10, 4, 16, out _);

        emulator.Resize(4, 2);
        VtHelpers.Feed(emulator, "ABCDEFGH");

        Assert.Equal("ABCD", VtHelpers.RowText(emulator, 0));
        Assert.Equal("EFGH", VtHelpers.RowText(emulator, 1));
        Assert.Equal((1, 3), VtHelpers.Cursor(emulator));
    }
}
