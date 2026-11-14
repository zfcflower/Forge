using Forge.App.ViewModels;
using Xunit;

namespace Forge.App.Tests.ViewModels;

/// <summary>
/// Pins the pure geometry of the timeline axis (forge-app spec, "聊天时间轴(W13)"): every combination the row controls can
/// ask about is enumerated as a literal table, and one continuity invariant asserts that a modeled transcript (user
/// message, turn header + blocks + optional footnote, closing notice) draws exactly one contiguous axis run that starts
/// at the first node, ends under the last one, has no internal gap and no stub beyond either end.
/// </summary>
public sealed class TimelineLayoutTests
{
    // ---------------------------------------------------------------- Item

    [Theory]
    [InlineData(true, true, true, false, false)]
    [InlineData(true, false, true, false, true)]
    [InlineData(false, true, true, true, false)]
    [InlineData(false, false, true, true, true)]
    public void A_top_level_item_always_has_a_node_and_lines_only_towards_other_nodes(
        bool isFirst, bool isLast, bool showNode, bool showTopLine, bool showBottomLine)
    {
        var layout = TimelineLayout.Item(isFirst, isLast);

        Assert.Equal(showNode, layout.ShowNode);
        Assert.Equal(showTopLine, layout.ShowTopLine);
        Assert.Equal(showBottomLine, layout.ShowBottomLine);
    }

    // ---------------------------------------------------------------- TurnHeader

    // A header normally draws both segments from one boolean: the axis either passes through it or it does not. The one
    // exception is a header with nothing visible of its own turn at an end of the transcript (the rows with 0 blocks and
    // no footnote that are first or last): it becomes the end node, so no neighbour's line hangs into nothing.

    [Theory]
    [InlineData(true, true, 0, false, true, false, false)]
    [InlineData(true, true, 0, true, false, false, false)]
    [InlineData(true, true, 1, false, false, false, false)]
    [InlineData(true, true, 1, true, false, false, false)]
    [InlineData(true, false, 0, false, true, false, true)]
    [InlineData(true, false, 0, true, false, false, false)]
    [InlineData(true, false, 1, false, false, false, false)]
    [InlineData(true, false, 1, true, false, false, false)]
    [InlineData(false, true, 0, false, true, true, false)]
    [InlineData(false, true, 0, true, false, true, true)]
    [InlineData(false, true, 1, false, false, true, true)]
    [InlineData(false, true, 1, true, false, true, true)]
    [InlineData(false, false, 0, false, false, true, true)]
    [InlineData(false, false, 0, true, false, true, true)]
    [InlineData(false, false, 1, false, false, true, true)]
    [InlineData(false, false, 1, true, false, true, true)]
    public void A_turn_header_has_no_node_unless_it_is_an_empty_end_of_the_transcript(
        bool isFirst, bool isLast, int visibleBlocks, bool hasFootnote,
        bool showNode, bool showTopLine, bool showBottomLine)
    {
        var layout = TimelineLayout.TurnHeader(isFirst, isLast, visibleBlocks, hasFootnote);

        Assert.Equal(showNode, layout.ShowNode);
        Assert.Equal(showTopLine, layout.ShowTopLine);
        Assert.Equal(showBottomLine, layout.ShowBottomLine);
    }

    // ---------------------------------------------------------------- TurnBlock

    [Theory]
    [InlineData(0, 1, false, false, false, true, true, true)]
    [InlineData(0, 1, false, false, true, true, true, true)]
    [InlineData(0, 1, false, true, false, true, true, false)]
    [InlineData(0, 1, false, true, true, true, true, true)]
    [InlineData(0, 1, true, false, false, true, false, true)]
    [InlineData(0, 1, true, false, true, true, false, true)]
    [InlineData(0, 1, true, true, false, true, false, false)]
    [InlineData(0, 1, true, true, true, true, false, true)]
    [InlineData(0, 2, false, false, false, true, true, true)]
    [InlineData(0, 2, false, false, true, true, true, true)]
    [InlineData(0, 2, false, true, false, true, true, true)]
    [InlineData(0, 2, false, true, true, true, true, true)]
    [InlineData(0, 2, true, false, false, true, false, true)]
    [InlineData(0, 2, true, false, true, true, false, true)]
    [InlineData(0, 2, true, true, false, true, false, true)]
    [InlineData(0, 2, true, true, true, true, false, true)]
    [InlineData(1, 2, false, false, false, true, true, true)]
    [InlineData(1, 2, false, false, true, true, true, true)]
    [InlineData(1, 2, false, true, false, true, true, false)]
    [InlineData(1, 2, false, true, true, true, true, true)]
    [InlineData(1, 2, true, false, false, true, true, true)]
    [InlineData(1, 2, true, false, true, true, true, true)]
    [InlineData(1, 2, true, true, false, true, true, false)]
    [InlineData(1, 2, true, true, true, true, true, true)]
    [InlineData(0, 3, false, false, false, true, true, true)]
    [InlineData(0, 3, false, false, true, true, true, true)]
    [InlineData(0, 3, false, true, false, true, true, true)]
    [InlineData(0, 3, false, true, true, true, true, true)]
    [InlineData(0, 3, true, false, false, true, false, true)]
    [InlineData(0, 3, true, false, true, true, false, true)]
    [InlineData(0, 3, true, true, false, true, false, true)]
    [InlineData(0, 3, true, true, true, true, false, true)]
    [InlineData(1, 3, false, false, false, true, true, true)]
    [InlineData(1, 3, false, false, true, true, true, true)]
    [InlineData(1, 3, false, true, false, true, true, true)]
    [InlineData(1, 3, false, true, true, true, true, true)]
    [InlineData(1, 3, true, false, false, true, true, true)]
    [InlineData(1, 3, true, false, true, true, true, true)]
    [InlineData(1, 3, true, true, false, true, true, true)]
    [InlineData(1, 3, true, true, true, true, true, true)]
    [InlineData(2, 3, false, false, false, true, true, true)]
    [InlineData(2, 3, false, false, true, true, true, true)]
    [InlineData(2, 3, false, true, false, true, true, false)]
    [InlineData(2, 3, false, true, true, true, true, true)]
    [InlineData(2, 3, true, false, false, true, true, true)]
    [InlineData(2, 3, true, false, true, true, true, true)]
    [InlineData(2, 3, true, true, false, true, true, false)]
    [InlineData(2, 3, true, true, true, true, true, true)]
    public void A_block_always_has_a_node_and_lines_towards_its_neighbours(
        int index, int visibleBlocks, bool turnFirst, bool turnLast, bool hasFootnote,
        bool showNode, bool showTopLine, bool showBottomLine)
    {
        var layout = TimelineLayout.TurnBlock(index, visibleBlocks, turnFirst, turnLast, hasFootnote);

        Assert.Equal(showNode, layout.ShowNode);
        Assert.Equal(showTopLine, layout.ShowTopLine);
        Assert.Equal(showBottomLine, layout.ShowBottomLine);
    }

    // ---------------------------------------------------------------- TurnFootnote

    [Theory]
    [InlineData(0, false, false, true, true, true)]
    [InlineData(0, false, true, true, true, false)]
    [InlineData(0, true, false, true, false, true)]
    [InlineData(0, true, true, true, false, false)]
    [InlineData(1, false, false, true, true, true)]
    [InlineData(1, false, true, true, true, false)]
    [InlineData(1, true, false, true, true, true)]
    [InlineData(1, true, true, true, true, false)]
    public void A_footnote_is_the_turns_closing_error_node(
        int visibleBlocks, bool isFirst, bool isLast, bool showNode, bool showTopLine, bool showBottomLine)
    {
        var layout = TimelineLayout.TurnFootnote(visibleBlocks, isFirst, isLast);

        Assert.Equal(showNode, layout.ShowNode);
        Assert.Equal(showTopLine, layout.ShowTopLine);
        Assert.Equal(showBottomLine, layout.ShowBottomLine);
    }

    // ---------------------------------------------------------------- the continuity invariant

    /// <summary>Where the modeled turn sits in the transcript.</summary>
    public enum TurnPlace
    {
        First,
        Middle,
        Last,
    }

    public static TheoryData<TurnPlace, int, bool, bool> ModeledTurns()
    {
        var data = new TheoryData<TurnPlace, int, bool, bool>();
        foreach (var place in Enum.GetValues<TurnPlace>())
        {
            for (var blocks = 0; blocks <= 3; blocks++)
            {
                foreach (var footnote in new[] { false, true })
                {
                    foreach (var collapsed in new[] { false, true })
                    {
                        data.Add(place, blocks, footnote, collapsed);
                    }
                }
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(ModeledTurns))]
    public void A_modeled_transcript_draws_exactly_one_contiguous_axis(TurnPlace place, int blocks, bool hasFootnote, bool collapsed)
    {
        var rows = ModeledRows(place, blocks, hasFootnote, collapsed);

        // Whatever the turn shows, the transcript has at least one node (the user message, notice or the turn's own).
        AssertSingleContiguousAxis(rows);
    }

    /// <summary>
    /// The H3 regression, spelled out: a user message followed by a still-empty running turn must not leave the user row's
    /// line hanging; the turn's header is the end node.
    /// </summary>
    [Fact]
    public void A_user_message_above_an_empty_last_turn_ends_on_the_turns_header_node()
    {
        var rows = ModeledRows(TurnPlace.Last, 0, false, collapsed: false);

        Assert.Equal(2, rows.Count);
        Assert.True(rows[0].ShowBottomLine);
        Assert.True(rows[1].ShowNode);
        Assert.True(rows[1].ShowTopLine);
        Assert.False(rows[1].ShowBottomLine);
    }

    /// <summary>The M1 regression: a collapsed first turn above a second turn is the axis start, not a hanging line.</summary>
    [Fact]
    public void A_collapsed_first_turn_starts_the_axis_for_the_turn_after_it()
    {
        var first = TimelineLayout.TurnHeader(isFirst: true, isLast: false, visibleBlocks: 0, hasFootnote: false);
        var second = TimelineLayout.TurnHeader(isFirst: false, isLast: true, visibleBlocks: 0, hasFootnote: false);

        Assert.True(first.ShowNode);
        Assert.False(first.ShowTopLine);
        Assert.True(first.ShowBottomLine);
        Assert.True(second.ShowNode);
        Assert.True(second.ShowTopLine);
        Assert.False(second.ShowBottomLine);
    }

    /// <summary>
    /// The rows of one modeled transcript. The turn (header, its visible blocks unless collapsed, optional footnote) sits
    /// <paramref name="place"/>: a user message before it unless it is first, a closing notice after it unless it is last.
    /// </summary>
    private static List<TimelineRowLayout> ModeledRows(TurnPlace place, int blocks, bool hasFootnote, bool collapsed)
    {
        var visibleBlocks = collapsed ? 0 : blocks;
        var hasBefore = place != TurnPlace.First;
        var hasAfter = place != TurnPlace.Last;
        var rows = new List<TimelineRowLayout>();
        if (hasBefore)
        {
            rows.Add(TimelineLayout.Item(isFirst: true, isLast: false));
        }

        var isFirst = !hasBefore;
        var isLast = !hasAfter;
        rows.Add(TimelineLayout.TurnHeader(isFirst, isLast, visibleBlocks, hasFootnote));
        for (var i = 0; i < visibleBlocks; i++)
        {
            rows.Add(TimelineLayout.TurnBlock(i, visibleBlocks, isFirst, isLast, hasFootnote));
        }

        if (hasFootnote)
        {
            rows.Add(TimelineLayout.TurnFootnote(visibleBlocks, isFirst, isLast));
        }

        if (hasAfter)
        {
            rows.Add(TimelineLayout.Item(isFirst: false, isLast: true));
        }

        return rows;
    }
    /// <summary>
    /// The axis is one contiguous run: it starts at the first node (no line above it, no stub before it), ends at the
    /// last node (no line below it, no stub after it), and every consecutive pair of rows inside the run is connected -
    /// a bottom line is always met by the next row's top line, so there is no internal gap.
    /// </summary>
    private static void AssertSingleContiguousAxis(List<TimelineRowLayout> rows)
    {
        var firstNode = rows.FindIndex(static row => row.ShowNode);
        var lastNode = rows.FindLastIndex(static row => row.ShowNode);
        Assert.True(firstNode >= 0, "the modeled transcript has no node at all");

        for (var i = 0; i < firstNode; i++)
        {
            Assert.False(rows[i].ShowTopLine || rows[i].ShowBottomLine, $"row {i} draws a stub above the axis start");
        }

        for (var i = lastNode + 1; i < rows.Count; i++)
        {
            Assert.False(rows[i].ShowTopLine || rows[i].ShowBottomLine, $"row {i} draws a stub below the axis end");
        }

        Assert.False(rows[firstNode].ShowTopLine, "the first node draws a line above itself");
        Assert.False(rows[lastNode].ShowBottomLine, "the last node draws a line below itself");

        for (var i = firstNode; i < lastNode; i++)
        {
            Assert.True(rows[i].ShowBottomLine, $"row {i} does not continue the axis downwards");
            Assert.True(rows[i + 1].ShowTopLine, $"row {i + 1} does not continue the axis upwards");
        }
    }
}
