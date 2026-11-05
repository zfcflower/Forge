namespace Forge.App.Terminal.Vt;

/// <summary>Cursor state saved by DECSC (ESC 7) and restored by DECRC (ESC 8).</summary>
internal struct SavedCursor
{
    public bool Valid;
    public int Row;
    public int Column;
    public bool PendingWrap;
    public CellFlags Attributes;
    public TerminalColor Foreground;
    public TerminalColor Background;
    public bool OriginMode;
    public bool AutoWrap;
    public bool G0Graphics;
    public bool G1Graphics;
    public int ActiveCharset;
    public int ScrollTop;
    public int ScrollBottom;
}

/// <summary>
/// One screen buffer (primary or alternate): its rows, its own cursor, its own scrolling region and its DECSC slot.
/// Both screens are fixed-size arrays of reused <see cref="TerminalLine"/> objects, so switching screens or scrolling
/// moves references only.
/// </summary>
internal sealed class TerminalScreen
{
    internal TerminalScreen(int columns, int rows)
    {
        Columns = columns;
        Rows = rows;
        Lines = new TerminalLine[rows];
        for (int i = 0; i < rows; i++)
        {
            Lines[i] = new TerminalLine(columns);
        }

        ScrollBottom = rows - 1;
    }

    public TerminalLine[] Lines { get; private set; }

    public int Rows { get; private set; }

    public int Columns { get; private set; }

    public int CursorRow;

    public int CursorColumn;

    public bool PendingWrap;

    public int ScrollTop;

    public int ScrollBottom;

    public SavedCursor Saved;

    internal void SetLines(TerminalLine[] lines, int columns, int rows)
    {
        Lines = lines;
        Columns = columns;
        Rows = rows;
    }

    internal void SetSize(int columns, int rows)
    {
        Columns = columns;
        Rows = rows;
    }
}
