namespace Forge.App.ViewModels;

/// <summary>
/// Which parts of the axis one transcript row draws: the node itself (if it has one), the line from the row's top to the
/// node, and the line from the node to the row's bottom. A row only ever asks "is there a node above me" and "is there a
/// node below me"; the axis therefore starts at the first node, ends under the last one, and passes through every node in
/// between without a gap.
/// </summary>
internal readonly record struct TimelineRowLayout(bool ShowNode, bool ShowTopLine, bool ShowBottomLine);

/// <summary>
/// The pure geometry of the timeline, in one place and independent of the visual tree. The row control only has to supply
/// the row's position (first/last in its collection, the block's index, whether the turn carries a footnote) and whether
/// the turn is expanded; every decision about the axis lives here, so a combination can be asserted without a window.
/// </summary>
internal static class TimelineLayout
{
    /// <summary>A top-level row that always has a node: a user message or a transcript notice.</summary>
    public static TimelineRowLayout Item(bool isFirst, bool isLast) => new(true, !isFirst, !isLast);

    /// <summary>
    /// The turn header: turn metadata is not an event, so it normally has no node; it carries the axis through only when
    /// there is a node above it and one below it (a visible block, a footnote, or a later item).
    /// <para>The exception is a header with nothing visible of its own turn (no visible block, no footnote: still empty and
    /// running, finished empty, or collapsed) that sits at an end of the transcript. Left node-less it would leave the
    /// neighbouring node's line hanging into nothing (a user message above a turn that has produced no output yet; a
    /// collapsed first turn above the second turn), so it becomes the end node of the axis itself.</para>
    /// </summary>
    public static TimelineRowLayout TurnHeader(bool isFirst, bool isLast, int visibleBlocks, bool hasFootnote)
    {
        if (visibleBlocks == 0 && !hasFootnote && (isFirst || isLast))
        {
            return new(true, !isFirst, !isLast);
        }

        var line = !isFirst && (visibleBlocks > 0 || hasFootnote || !isLast);
        return new(false, line, line);
    }

    /// <summary>
    /// A block or the footnote of a turn, at <paramref name="index"/> of <paramref name="count"/> rows of the flat transcript
    /// list. The row above the first block (or the footnote of a turn with no block) is the turn's header; when that header
    /// is the very first row it draws no line, so the axis starts at the first node, not at the header. Every other row has
    /// a node above it, and a node below it unless it is the last row.
    /// </summary>
    public static TimelineRowLayout TurnRow(int index, int count) => new(true, index > 1, index < count - 1);
}
