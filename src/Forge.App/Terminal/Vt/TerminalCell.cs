namespace Forge.App.Terminal.Vt;

/// <summary>
/// One screen cell: the rune drawn in it, resolved colours, SGR attributes and the OSC 8 hyperlink it belongs to.
/// Everything is inline (no references), so a row is a plain value-type array and scrolling moves pointers only.
/// <see cref="Rune"/> is 0 for "blank / never written".
/// </summary>
internal struct TerminalCell : IEquatable<TerminalCell>
{
    /// <summary>0 means blank; a real space keeps its own value (0x20) so trailing blanks stay distinguishable.</summary>
    public int Rune;

    public TerminalColor Foreground;

    public TerminalColor Background;

    public CellFlags Flags;

    /// <summary>OSC 8 link id, 0 for none; resolve through <see cref="TerminalEmulator.GetHyperlink"/>.</summary>
    public int HyperlinkId;

    /// <summary>True when this is exactly the default cell, i.e. a blank with no attributes or colours.</summary>
    public readonly bool IsBlank =>
        Rune == 0 && Flags == 0 && HyperlinkId == 0 && Foreground.IsDefault && Background.IsDefault;

    /// <summary>True when this cell is half of a double-width character.</summary>
    public readonly bool IsWideContinuation => (Flags & CellFlags.WideContinuation) != 0;

    public readonly bool Equals(TerminalCell other) =>
        Rune == other.Rune &&
        Flags == other.Flags &&
        HyperlinkId == other.HyperlinkId &&
        Foreground == other.Foreground &&
        Background == other.Background;

    public override readonly bool Equals(object? obj) => obj is TerminalCell other && Equals(other);

    public override readonly int GetHashCode() => HashCode.Combine(Rune, (int)Flags, HyperlinkId, Foreground, Background);

    public static bool operator ==(TerminalCell left, TerminalCell right) => left.Equals(right);

    public static bool operator !=(TerminalCell left, TerminalCell right) => !left.Equals(right);
}
