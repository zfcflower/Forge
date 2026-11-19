namespace Forge.App.ViewModels;

/// <summary>
/// Which parts of the axis one transcript row draws: the node itself (if it has one), the line from the row's top to the
/// node, and the line from the node to the row's bottom.
/// </summary>
internal readonly record struct TimelineRowLayout(bool ShowNode, bool ShowTopLine, bool ShowBottomLine);

/// <summary>
/// The pure geometry of the timeline, in one place and independent of the visual tree. Since the transcript is one
/// container per round, a row only has to know its place inside its own round: the axis therefore starts at the round's
/// first node, runs unbroken through every row, and ends on the round's closing node. No other round can move it, and no row
/// has to guess whether a node exists above or below it.
/// </summary>
internal static class TimelineLayout
{
    /// <summary>
    /// One row of a round, at <paramref name="index"/> of that round's <paramref name="count"/> rows.
    /// <paramref name="hasNode"/> is false only for the sub-agent turn header, which is metadata rather than an event: the
    /// axis then passes through it, so the first block below still lines up with the round above.
    /// </summary>
    public static TimelineRowLayout Row(int index, int count, bool hasNode) =>
        new(hasNode, index > 0, index < count - 1);
}
