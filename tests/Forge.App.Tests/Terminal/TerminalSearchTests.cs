using Forge.App.Terminal;
using Forge.App.Terminal.Vt;
using Xunit;

namespace Forge.App.Tests.Terminal;

/// <summary>
/// Covers <see cref="TerminalSearch"/>: per-line matching, case sensitivity, empty query, wide-character column mapping,
/// and the scrollback anchor (<c>LineBase - dropped</c>) that keeps a match on its text when lines are evicted.
/// </summary>
public sealed class TerminalSearchTests
{
    private static TerminalSearchLine[] Snapshot(TerminalEmulator emulator)
    {
        var lines = new TerminalSearchLine[emulator.TotalLines];
        for (var i = 0; i < lines.Length; i++)
        {
            lines[i] = TerminalSearch.SnapshotLine(emulator.GetLine(i));
        }

        return lines;
    }

    [Fact]
    public void Find_matches_every_occurrence_and_maps_columns()
    {
        var emulator = VtHelpers.Create(20, 4, 8, out _);
        VtHelpers.Feed(emulator, "hello hello");

        var matches = TerminalSearch.Find(Snapshot(emulator), "hello", caseSensitive: true, baseDropped: 0);

        Assert.Equal(2, matches.Count);
        Assert.Equal(0, matches[0].LineBase);
        Assert.Equal(0, matches[0].StartColumn);
        Assert.Equal(4, matches[0].EndColumn);
        Assert.Equal(6, matches[1].StartColumn);
        Assert.Equal(10, matches[1].EndColumn);
    }

    [Fact]
    public void Find_respects_the_case_toggle()
    {
        var emulator = VtHelpers.Create(20, 4, 8, out _);
        VtHelpers.Feed(emulator, "Hello hello");

        Assert.Single(TerminalSearch.Find(Snapshot(emulator), "hello", caseSensitive: true, baseDropped: 0));
        Assert.Equal(2, TerminalSearch.Find(Snapshot(emulator), "hello", caseSensitive: false, baseDropped: 0).Count);
    }

    [Fact]
    public void Find_returns_nothing_for_an_empty_query()
    {
        var emulator = VtHelpers.Create(20, 4, 8, out _);
        VtHelpers.Feed(emulator, "hello");

        Assert.Empty(TerminalSearch.Find(Snapshot(emulator), string.Empty, caseSensitive: false, baseDropped: 0));
    }

    [Fact]
    public void Find_covers_both_cells_of_a_wide_character()
    {
        var emulator = VtHelpers.Create(20, 4, 8, out _);
        VtHelpers.Feed(emulator, "A\u4e2dB");

        var match = Assert.Single(TerminalSearch.Find(Snapshot(emulator), "\u4e2d", caseSensitive: true, baseDropped: 0));

        Assert.Equal(1, match.StartColumn);
        Assert.Equal(2, match.EndColumn);
    }

    [Fact]
    public void LineBase_keeps_a_match_on_its_line_until_that_line_is_dropped()
    {
        var emulator = VtHelpers.Create(10, 1, 8, out _);
        VtHelpers.Feed(emulator, "old\r\nnew");

        Assert.Equal(1, emulator.ScrollbackCount);
        var old = Assert.Single(TerminalSearch.Find(Snapshot(emulator), "old", caseSensitive: true, baseDropped: 0));
        var fresh = Assert.Single(TerminalSearch.Find(Snapshot(emulator), "new", caseSensitive: true, baseDropped: 0));

        Assert.Equal(0, old.AbsoluteLine(emulator.ScrollbackDropped));
        Assert.Equal(1, fresh.AbsoluteLine(emulator.ScrollbackDropped));

        emulator.ClearScrollback();
        Assert.True(old.AbsoluteLine(emulator.ScrollbackDropped) < 0, "an evicted scrollback line is no longer addressable");
        Assert.Equal(0, fresh.AbsoluteLine(emulator.ScrollbackDropped));
    }
}
