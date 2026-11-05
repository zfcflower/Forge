namespace Forge.App.Terminal.Vt;

/// <summary>Cursor rendering styles selected by DECSCUSR (<c>CSI Ps SP q</c>).</summary>
internal enum CursorShape
{
    Block,

    Underline,

    Bar,
}

/// <summary>Where the cursor is and how it should be drawn; a snapshot, taken while the caller holds the emulator lock.</summary>
internal readonly struct TerminalCursor : IEquatable<TerminalCursor>
{
    internal TerminalCursor(int row, int column, bool visible, CursorShape shape, bool blinking)
    {
        Row = row;
        Column = column;
        Visible = visible;
        Shape = shape;
        Blinking = blinking;
    }

    /// <summary>Row inside the current screen, 0-based; scrollback rows are never addressed by the cursor.</summary>
    public int Row { get; }

    /// <summary>Column, 0-based.</summary>
    public int Column { get; }

    public bool Visible { get; }

    public CursorShape Shape { get; }

    public bool Blinking { get; }

    public bool Equals(TerminalCursor other) =>
        Row == other.Row && Column == other.Column && Visible == other.Visible && Shape == other.Shape &&
        Blinking == other.Blinking;

    public override bool Equals(object? obj) => obj is TerminalCursor other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Row, Column, Visible, Shape, Blinking);

    public static bool operator ==(TerminalCursor left, TerminalCursor right) => left.Equals(right);

    public static bool operator !=(TerminalCursor left, TerminalCursor right) => !left.Equals(right);
}
