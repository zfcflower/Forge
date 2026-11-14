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
    /// The turn header: turn metadata is not an event, so it has no node; it carries the axis through only when there is a
    /// node above it and one below it (a visible block, a footnote, or a later item).
    /// </summary>
    public static TimelineRowLayout TurnHeader(bool isFirst, bool isLast, int visibleBlocks, bool hasFootnote)
    {
        var line = !isFirst && (visibleBlocks > 0 || hasFootnote || !isLast);
        return new(false, line, line);
    }

    /// <summary>One block of an expanded turn, at <paramref name="index"/> of <paramref name="visibleBlocks"/>.</summary>
    public static TimelineRowLayout TurnBlock(int index, int visibleBlocks, bool isFirst, bool isLast, bool hasFootnote) =>
        new(true, index > 0 || !isFirst, index < visibleBlocks - 1 || hasFootnote || !isLast);

    /// <summary>The turn's footnote, when it has one: it is the turn's closing error node.</summary>
    public static TimelineRowLayout TurnFootnote(int visibleBlocks, bool isFirst, bool isLast) =>
        new(true, visibleBlocks > 0 || !isFirst, !isLast);
}