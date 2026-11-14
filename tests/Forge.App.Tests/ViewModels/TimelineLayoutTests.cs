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

    // A header draws both segments from one boolean: the axis either passes through it or it does not.

    [Theory]
    [InlineData(true, true, 0, false, false, false, false)]
    [InlineData(true, true, 0, true, false, false, false)]
    [InlineData(true, true, 1, false, false, false, false)]
    [InlineData(true, true, 1, true, false, false, false)]
    [InlineData(true, false, 0, false, false, false, false)]
    [InlineData(true, false, 0, true, false, false, false)]
    [InlineData(true, false, 1, false, false, false, false)]
    [InlineData(true, false, 1, true, false, false, false)]
    [InlineData(false, true, 0, false, false, false, false)]
    [InlineData(false, true, 0, true, false, true, true)]
    [InlineData(false, true, 1, false, false, true, true)]
    [InlineData(false, true, 1, true, false, true, true)]
    [InlineData(false, false, 0, false, false, true, true)]
    [InlineData(false, false, 0, true, false, true, true)]
    [InlineData(false, false, 1, false, false, true, true)]
    [InlineData(false, false, 1, true, false, true, true)]
    public void A_turn_header_has_no_node_and_carries_the_axis_only_between_existing_nodes(
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

    [Theory]
    [InlineData(0, false)]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(2, true)]
    [InlineData(3, false)]
    [InlineData(3, true)]
    public void A_modeled_transcript_draws_exactly_one_contiguous_axis(int visibleBlocks, bool hasFootnote)
    {
        var rows = ModeledRows(visibleBlocks, hasFootnote);

        // The model really is user message, node-less turn header, blocks, optional footnote, closing notice.
        Assert.Equal(3 + visibleBlocks + (hasFootnote ? 1 : 0), rows.Count);
        Assert.True(rows[0].ShowNode);
        Assert.False(rows[1].ShowNode);
        Assert.True(rows[^1].ShowNode);

        AssertSingleContiguousAxis(rows);
    }

    /// <summary>
    /// The rows of one modeled transcript: a user message, a turn header with <paramref name="visibleBlocks"/> visible
    /// blocks and an optional footnote, and a closing notice. The turn is a middle item (a user message before it, a
    /// notice after it), so it is neither the transcript's first nor last row.
    /// </summary>
    private static List<TimelineRowLayout> ModeledRows(int visibleBlocks, bool hasFootnote)
    {
        var rows = new List<TimelineRowLayout>
        {
            TimelineLayout.Item(isFirst: true, isLast: false),
            TimelineLayout.TurnHeader(isFirst: false, isLast: false, visibleBlocks, hasFootnote),
        };
        for (var i = 0; i < visibleBlocks; i++)
        {
            rows.Add(TimelineLayout.TurnBlock(i, visibleBlocks, isFirst: false, isLast: false, hasFootnote));
        }

        if (hasFootnote)
        {
            rows.Add(TimelineLayout.TurnFootnote(visibleBlocks, isFirst: false, isLast: false));
        }

        rows.Add(TimelineLayout.Item(isFirst: false, isLast: true));
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
