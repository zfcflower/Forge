using System.Text;

namespace Forge.App.Terminal.Vt;

/// <summary>
/// A xterm-compatible VT terminal: it turns a UTF-8 byte stream into a screen of <see cref="TerminalLine"/>s plus a
/// scrollback ring, and never touches a UI framework. Not thread safe: the caller owns the lock (the app has one
/// background pump per terminal that feeds this, and the renderer briefly takes the same lock to read lines).
/// <para>
/// Reading is index based: <see cref="GetLine"/> addresses <c>[0, ScrollbackCount + Rows)</c>, oldest scrollback line
/// first, current screen rows last. Scrolling moves line references only; a full scrollback recycles the oldest line
/// object instead of allocating. Parsing is a DEC ANSI state machine
/// (see <c>TerminalEmulator.Parser.cs</c>) and unknown or malformed sequences are ignored without changing state.
/// </para>
/// </summary>
internal sealed partial class TerminalEmulator
{
    internal const int MaxColumns = 1000;
    internal const int MaxRows = 500;
    internal const int MaxScrollback = 100000;

    private readonly ITerminalEmulatorHost host;
    private readonly ScrollbackBuffer scrollback = new();
    private readonly TerminalScreen primary;
    private readonly TerminalScreen alternate;
    private TerminalScreen screen;
    private TerminalLine? spare;
    private int columns;
    private int rows;

    private CellFlags attributes;
    private TerminalColor foreground;
    private TerminalColor background;
    private int hyperlink;

    private TerminalModes modes = TerminalModes.AutoWrap;
    private MouseTracking mouseTracking;
    private bool mouseSgr;
    private bool insertMode;
    private bool lineFeedNewLine;
    private bool alternateActive;

    private bool cursorVisible = true;
    private CursorShape cursorShape = CursorShape.Block;
    private bool cursorBlinking = true;

    private bool g0Graphics;
    private bool g1Graphics;
    private int activeCharset;

    private bool[] tabStops;
    private string title = string.Empty;
    private long version;
    private long scrollbackPushed;
    private long scrollbackDropped;

    public TerminalEmulator(int columns, int rows, int scrollbackLimit, ITerminalEmulatorHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        this.host = host;
        this.columns = Math.Clamp(columns, 1, MaxColumns);
        this.rows = Math.Clamp(rows, 1, MaxRows);
        primary = new TerminalScreen(this.columns, this.rows);
        alternate = new TerminalScreen(this.columns, this.rows);
        screen = primary;
        scrollback.SetLimit(Math.Clamp(scrollbackLimit, 0, MaxScrollback));
        tabStops = new bool[this.columns];
        ResetTabStops();
        ResetParserState();
    }

    /// <summary>Screen width in cells; scrollback lines always have this width.</summary>
    public int Columns => columns;

    /// <summary>Screen height in rows.</summary>
    public int Rows => rows;

    /// <summary>Lines available above the screen; always 0 while the alternate screen is active.</summary>
    public int ScrollbackCount => alternateActive ? 0 : scrollback.Count;

    /// <summary>Lines <see cref="GetLine"/> can address: scrollback plus screen rows.</summary>
    public int TotalLines => ScrollbackCount + rows;

    /// <summary>Maximum number of scrollback lines; lowering it trims the oldest lines immediately.</summary>
    public int ScrollbackLimit
    {
        get => scrollback.Capacity;
        set
        {
            int limit = Math.Clamp(value, 0, MaxScrollback);
            if (limit == scrollback.Capacity)
            {
                return;
            }

            int kept = scrollback.Count;
            scrollback.SetLimit(limit);
            scrollbackDropped += kept - scrollback.Count;
            version++;
        }
    }

    /// <summary>
    /// Monotonic count of lines pushed into the scrollback. A push does not move the text a line index already points
    /// at, but it does move the viewport relative to a fixed distance from the bottom, so a viewer that is scrolled
    /// back adds the push delta to that distance to stay on the same text.
    /// </summary>
    public long ScrollbackPushed => scrollbackPushed;

    /// <summary>
    /// Monotonic count of lines discarded from the oldest end of the scrollback: evicted by a full ring, trimmed by a
    /// smaller <see cref="ScrollbackLimit"/>, or dropped by <see cref="ClearScrollback"/>. After a discard of
    /// <c>d</c> lines the text a line index addressed is at <c>index - d</c>, which is how a viewer keeps a selection on
    /// the text it was dragged over.
    /// </summary>
    public long ScrollbackDropped => scrollbackDropped;

    public TerminalCursor Cursor => new(screen.CursorRow, screen.CursorColumn, cursorVisible, cursorShape, cursorBlinking);

    public TerminalModes Modes => modes;

    public MouseTracking MouseMode => mouseTracking;

    /// <summary>DECSET 1006: mouse events are reported in SGR form.</summary>
    public bool MouseSgr => mouseSgr;

    /// <summary>True while the alternate screen buffer (DECSET 47/1047/1049) is in use.</summary>
    public bool IsAlternateScreen => alternateActive;

    /// <summary>The OSC 0/1/2 window title; empty until the application sets one.</summary>
    public string Title => title;

    /// <summary>Incremented on every change that a renderer can see, cursor-only changes included.</summary>
    public long Version => version;

    /// <summary>Resolves an OSC 8 link id from <see cref="TerminalCell.HyperlinkId"/>; ids are reused per URI.</summary>
    public string? GetHyperlink(int id) => id > 0 && id <= hyperlinks.Count ? hyperlinks[id - 1] : null;

    /// <summary>
    /// Addresses one row: 0 is the oldest scrollback line, <c>ScrollbackCount + Rows - 1</c> the bottom screen row.
    /// The returned object is reused by the emulator (a scrolled-out line is recycled later), so a caller must not
    /// keep it across calls without also comparing <see cref="TerminalLine.Version"/>.
    /// </summary>
    public TerminalLine GetLine(int index)
    {
        int scrollbackCount = ScrollbackCount;
        if ((uint)index >= (uint)(scrollbackCount + rows))
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        return index < scrollbackCount ? scrollback.Get(index) : screen.Lines[index - scrollbackCount];
    }

    /// <summary>
    /// Decodes and parses UTF-8 terminal output. A multi-byte sequence cut at the end of a chunk is kept for the next
    /// call; invalid bytes become U+FFFD instead of being dropped.
    /// </summary>
    public void Feed(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty && pendingCount == 0)
        {
            return;
        }

        if (pendingCount > 0)
        {
            Span<byte> head = stackalloc byte[4];
            pending.AsSpan(0, pendingCount).CopyTo(head);
            int needed = 4 - pendingCount;
            int take = Math.Min(needed, data.Length);
            data[..take].CopyTo(head[pendingCount..]);
            int total = pendingCount + take;
            pendingCount = 0;
            data = data[take..];
            Parse(head[..total]);
        }

        if (!data.IsEmpty)
        {
            Parse(data);
        }

        version++;
    }

    /// <summary>
    /// Changes the screen size. No reflow: lines are truncated or padded on the right. Shrinking rows pushes the top
    /// lines into the scrollback so the bottom content (where the prompt is) stays visible; the scrolling region and
    /// any pending wrap are reset, and the cursor is clamped back inside the screen.
    /// </summary>
    public void Resize(int columns, int rows)
    {
        int newColumns = Math.Clamp(columns, 1, MaxColumns);
        int newRows = Math.Clamp(rows, 1, MaxRows);
        if (newColumns == this.columns && newRows == this.rows)
        {
            return;
        }

        if (newRows != this.rows)
        {
            ResizeRows(newRows);
        }

        if (newColumns != this.columns)
        {
            ResizeColumns(newColumns);
        }

        this.columns = newColumns;
        this.rows = newRows;
        primary.SetSize(newColumns, newRows);
        alternate.SetSize(newColumns, newRows);
        ClampScreen(primary, newColumns, newRows);
        ClampScreen(alternate, newColumns, newRows);

        version++;
    }

    /// <summary>Drops the whole scrollback (ED 3). The screen is untouched.</summary>
    public void ClearScrollback()
    {
        if (scrollback.Count == 0)
        {
            return;
        }

        scrollbackDropped += scrollback.Count;
        scrollback.Clear();
        version++;
    }

    // ---- printing -----------------------------------------------------------------------------------------------

    /// <summary>Fast path for a run of printable ASCII (0x20–0x7E): one pass, one line version bump.</summary>
    private void PrintAscii(ReadOnlySpan<byte> ascii)
    {
        if (activeCharset == 0 ? g0Graphics : g1Graphics)
        {
            for (int i = 0; i < ascii.Length; i++)
            {
                PrintRune(DecGraphicsTable[ascii[i] - 0x20]);
            }

            return;
        }

        TerminalScreen target = screen;
        int index = 0;
        while (index < ascii.Length)
        {
            if (target.PendingWrap)
            {
                target.PendingWrap = false;
                if ((modes & TerminalModes.AutoWrap) != 0)
                {
                    WrapLine();
                }
            }

            if (insertMode)
            {
                InsertCells(Math.Min(columns - target.CursorColumn, ascii.Length - index));
            }

            int column = target.CursorColumn;
            int take = Math.Min(columns - column, ascii.Length - index);
            target.Lines[target.CursorRow].WriteAsciiRun(
                column,
                ascii.Slice(index, take),
                attributes,
                foreground,
                background,
                hyperlink);
            index += take;
            target.CursorColumn = column + take;
            if (target.CursorColumn >= columns)
            {
                target.CursorColumn = columns - 1;
                target.PendingWrap = true;
            }
        }

        lastPrintedRune = ascii[^1];
    }

    private void PrintRune(int rune)
    {
        int width = CellWidth.Of(rune);
        if (width == 0)
        {
            AppendCombining(rune);
            return;
        }

        if (width == 2 && columns < 2)
        {
            return;
        }

        TerminalScreen target = screen;
        if (target.PendingWrap)
        {
            target.PendingWrap = false;
            if ((modes & TerminalModes.AutoWrap) != 0)
            {
                WrapLine();
            }
        }

        if (width == 2 && target.CursorColumn >= columns - 1)
        {
            if ((modes & TerminalModes.AutoWrap) == 0)
            {
                return;
            }

            WrapLine();
        }

        if (insertMode)
        {
            InsertCells(width);
        }

        PutCell(rune, width);
    }

    private void PutCell(int rune, int width)
    {
        lastPrintedRune = rune;
        TerminalScreen target = screen;
        TerminalLine line = target.Lines[target.CursorRow];
        int column = target.CursorColumn;
        CellFlags flags = attributes;
        TerminalColor cellForeground = foreground;
        TerminalColor cellBackground = background;
        int link = hyperlink;
        RepairWide(line, column, width);
        line.SetCell(column, rune, flags, cellForeground, cellBackground, link);
        if (width == 2)
        {
            line.SetContinuation(column + 1, flags, cellForeground, cellBackground, link);
        }

        target.CursorColumn = column + width;
        if (target.CursorColumn >= columns)
        {
            target.CursorColumn = columns - 1;
            target.PendingWrap = true;
        }
    }

    /// <summary>Blanks the far half of a double-width character that the write at <paramref name="column"/> orphans.</summary>
    private static void RepairWide(TerminalLine line, int column, int width)
    {
        if (line[column].IsWideContinuation && column > 0)
        {
            line.Clear(column - 1, default);
        }

        int after = column + width;
        if (after < line.Count && line[after].IsWideContinuation)
        {
            line.Clear(after, default);
        }
    }

    /// <summary>Attaches a zero-width rune (combining mark, ZWJ, variation selector) to the previously written cell.</summary>
    private void AppendCombining(int rune)
    {
        TerminalScreen target = screen;
        int column = target.PendingWrap ? columns - 1 : target.CursorColumn - 1;
        if (column < 0)
        {
            return;
        }

        TerminalLine line = target.Lines[target.CursorRow];
        if (line[column].IsWideContinuation)
        {
            column--;
        }

        if (column >= 0)
        {
            line.AppendCombining(column, rune);
        }
    }

    private void WrapLine()
    {
        TerminalScreen target = screen;
        target.Lines[target.CursorRow].SetWrapped(true);
        target.CursorColumn = 0;
        target.PendingWrap = false;
        Index();
    }

    // ---- scrolling ----------------------------------------------------------------------------------------------

    /// <summary>LF / IND: down one row inside the scrolling region, scrolling when already on the bottom margin.</summary>
    private void Index()
    {
        TerminalScreen target = screen;
        if (target.CursorRow == target.ScrollBottom)
        {
            ScrollUp(target.ScrollTop, target.ScrollBottom, 1);
        }
        else if (target.CursorRow < rows - 1)
        {
            target.CursorRow++;
        }
    }

    /// <summary>RI: up one row inside the scrolling region, scrolling when already on the top margin.</summary>
    private void ReverseIndex()
    {
        TerminalScreen target = screen;
        if (target.CursorRow == target.ScrollTop)
        {
            ScrollDown(target.ScrollTop, target.ScrollBottom, 1);
        }
        else if (target.CursorRow > 0)
        {
            target.CursorRow--;
        }
    }

    /// <summary>
    /// Scrolls <c>[top, bottom]</c> up by <paramref name="count"/> rows: only line references move. Lines leaving a
    /// full-height primary screen go to the scrollback; everything else is recycled for the rows entering at the bottom.
    /// </summary>
    private void ScrollUp(int top, int bottom, int count)
    {
        TerminalLine[] lines = screen.Lines;
        bool toScrollback = !alternateActive && top == 0 && bottom == rows - 1;
        for (int step = 0; step < count; step++)
        {
            TerminalLine evicted = lines[top];
            int move = bottom - top;
            if (move > 0)
            {
                Array.Copy(lines, top + 1, lines, top, move);
            }

            lines[bottom] = AcquireLine();
            Recycle(evicted, toScrollback);
        }
    }

    private void ScrollDown(int top, int bottom, int count)
    {
        TerminalLine[] lines = screen.Lines;
        for (int step = 0; step < count; step++)
        {
            TerminalLine evicted = lines[bottom];
            int move = bottom - top;
            if (move > 0)
            {
                Array.Copy(lines, top, lines, top + 1, move);
            }

            lines[top] = AcquireLine();
            Recycle(evicted, false);
        }
    }

    private TerminalLine AcquireLine()
    {
        TerminalLine line = spare ?? new TerminalLine(columns);
        spare = null;
        line.Reset(columns, BlankCell);
        return line;
    }

    private void Recycle(TerminalLine line, bool toScrollback)
    {
        if (toScrollback)
        {
            PushScrollback(line);
        }
        else
        {
            spare = line;
        }
    }

    /// <summary>Appends to the scrollback and counts the push and, when the ring is full, the evicted oldest line.</summary>
    private void PushScrollback(TerminalLine line)
    {
        TerminalLine? victim = scrollback.Push(line);
        scrollbackPushed++;
        if (victim is not null)
        {
            scrollbackDropped++;
            spare = victim;
        }
    }

    // ---- editing ------------------------------------------------------------------------------------------------

    private void InsertCells(int count)
    {
        TerminalScreen target = screen;
        if (count <= 0)
        {
            return;
        }

        target.Lines[target.CursorRow].InsertCells(target.CursorColumn, Math.Min(count, columns - target.CursorColumn), BlankCell);
    }

    private void DeleteCells(int count)
    {
        TerminalScreen target = screen;
        if (count <= 0)
        {
            return;
        }

        target.Lines[target.CursorRow].DeleteCells(target.CursorColumn, Math.Min(count, columns - target.CursorColumn), BlankCell);
    }

    private void InsertLines(int count)
    {
        TerminalScreen target = screen;
        if (target.CursorRow < target.ScrollTop || target.CursorRow > target.ScrollBottom)
        {
            return;
        }

        int available = target.ScrollBottom - target.CursorRow + 1;
        ScrollDown(target.CursorRow, target.ScrollBottom, Math.Min(count, available));
    }

    private void DeleteLines(int count)
    {
        TerminalScreen target = screen;
        if (target.CursorRow < target.ScrollTop || target.CursorRow > target.ScrollBottom)
        {
            return;
        }

        int available = target.ScrollBottom - target.CursorRow + 1;
        ScrollUp(target.CursorRow, target.ScrollBottom, Math.Min(count, available));
    }

    private void EraseInLine(int from, int to)
    {
        TerminalScreen target = screen;
        from = Math.Max(from, 0);
        to = Math.Min(to, columns - 1);
        if (from > to)
        {
            return;
        }

        target.Lines[target.CursorRow].Fill(from, to + 1, BlankCell);
    }

    private void EraseLineFully(int row)
    {
        screen.Lines[row].Reset(columns, BlankCell);
    }

    private TerminalCell BlankCell => new() { Background = background };

    // ---- cursor & tabs ------------------------------------------------------------------------------------------

    private void MoveCursorTo(int row, int column)
    {
        TerminalScreen target = screen;
        target.CursorRow = Math.Clamp(row, 0, rows - 1);
        target.CursorColumn = Math.Clamp(column, 0, columns - 1);
        target.PendingWrap = false;
    }

    private void ResetTabStops()
    {
        bool[] stops = tabStops;
        for (int i = 0; i < stops.Length; i++)
        {
            stops[i] = i % 8 == 0 && i != 0;
        }
    }

    private void SetTabStop()
    {
        TerminalScreen target = screen;
        if ((uint)target.CursorColumn < (uint)tabStops.Length)
        {
            tabStops[target.CursorColumn] = true;
        }
    }

    private void ClearTabStop(int mode)
    {
        TerminalScreen target = screen;
        if (mode == 0)
        {
            if ((uint)target.CursorColumn < (uint)tabStops.Length)
            {
                tabStops[target.CursorColumn] = false;
            }
        }
        else if (mode == 3)
        {
            Array.Clear(tabStops);
        }
    }

    private void HorizontalTab(int count)
    {
        TerminalScreen target = screen;
        for (int step = 0; step < count; step++)
        {
            int column = target.CursorColumn + 1;
            while (column < columns - 1 && !tabStops[column])
            {
                column++;
            }

            target.CursorColumn = Math.Min(column, columns - 1);
            if (target.CursorColumn >= columns - 1)
            {
                break;
            }
        }

        target.PendingWrap = false;
    }

    private void BackTab(int count)
    {
        TerminalScreen target = screen;
        for (int step = 0; step < count; step++)
        {
            int column = target.CursorColumn - 1;
            while (column > 0 && !tabStops[column])
            {
                column--;
            }

            target.CursorColumn = Math.Max(column, 0);
            if (target.CursorColumn == 0)
            {
                break;
            }
        }

        target.PendingWrap = false;
    }

    // ---- resize -------------------------------------------------------------------------------------------------

    private void ResizeRows(int newRows)
    {
        int oldRows = rows;
        if (newRows < oldRows)
        {
            int drop = oldRows - newRows;
            TerminalLine[] primaryLines = primary.Lines;
            for (int i = 0; i < drop; i++)
            {
                PushScrollback(primaryLines[i]);
                primaryLines[i] = null!;
            }

            var keptPrimary = new TerminalLine[newRows];
            Array.Copy(primaryLines, drop, keptPrimary, 0, newRows);
            primary.SetLines(keptPrimary, columns, newRows);
            primary.CursorRow = Math.Max(primary.CursorRow - drop, 0);

            TerminalLine[] alternateLines = alternate.Lines;
            for (int i = 0; i < drop; i++)
            {
                spare = alternateLines[i];
                alternateLines[i] = null!;
            }

            var keptAlternate = new TerminalLine[newRows];
            Array.Copy(alternateLines, drop, keptAlternate, 0, newRows);
            alternate.SetLines(keptAlternate, columns, newRows);
            alternate.CursorRow = Math.Max(alternate.CursorRow - drop, 0);
            return;
        }

        GrowScreen(primary, newRows);
        GrowScreen(alternate, newRows);
    }

    private static void ClampScreen(TerminalScreen target, int newColumns, int newRows)
    {
        target.CursorRow = Math.Clamp(target.CursorRow, 0, newRows - 1);
        target.CursorColumn = Math.Clamp(target.CursorColumn, 0, newColumns - 1);
        target.PendingWrap = false;
        target.ScrollTop = 0;
        target.ScrollBottom = newRows - 1;
        target.Saved = default;
    }

    private static void ResizeScreenWidth(TerminalScreen target, int newColumns)
    {
        TerminalLine[] lines = target.Lines;
        for (int i = 0; i < lines.Length; i++)
        {
            lines[i].Resize(newColumns);
        }
    }

    private void GrowScreen(TerminalScreen target, int newRows)
    {
        TerminalLine[] existing = target.Lines;
        var grown = new TerminalLine[newRows];
        Array.Copy(existing, grown, existing.Length);
        for (int i = existing.Length; i < newRows; i++)
        {
            grown[i] = AcquireLine();
        }

        target.SetLines(grown, columns, newRows);
    }

    private void ResizeColumns(int newColumns)
    {
        spare = null;
        ResizeScreenWidth(primary, newColumns);
        ResizeScreenWidth(alternate, newColumns);

        for (int i = 0; i < scrollback.Count; i++)
        {
            scrollback.Get(i).Resize(newColumns);
        }

        var stops = new bool[newColumns];
        int copy = Math.Min(newColumns, tabStops.Length);
        Array.Copy(tabStops, stops, copy);
        for (int i = copy; i < newColumns; i++)
        {
            stops[i] = i % 8 == 0 && i != 0;
        }

        tabStops = stops;
    }

    // ---- modes & reset ------------------------------------------------------------------------------------------

    private void SetAlternateScreen(bool enter, bool clearOnEnter, bool clearOnExit)
    {
        if (enter)
        {
            if (alternateActive)
            {
                if (clearOnEnter)
                {
                    ClearScreen(alternate);
                }

                return;
            }

            alternateActive = true;
            modes |= TerminalModes.AlternateScreen;
            screen = alternate;
            alternate.CursorRow = 0;
            alternate.CursorColumn = 0;
            alternate.PendingWrap = false;
            alternate.ScrollTop = 0;
            alternate.ScrollBottom = rows - 1;
            if (clearOnEnter)
            {
                ClearScreen(alternate);
            }

            return;
        }

        if (!alternateActive)
        {
            return;
        }

        if (clearOnExit)
        {
            ClearScreen(alternate);
        }

        alternateActive = false;
        modes &= ~TerminalModes.AlternateScreen;
        screen = primary;
    }

    private void ClearScreen(TerminalScreen target)
    {
        TerminalCell blank = BlankCell;
        TerminalLine[] lines = target.Lines;
        for (int i = 0; i < lines.Length; i++)
        {
            lines[i].Reset(columns, blank);
        }
    }

    private void FullReset()
    {
        attributes = CellFlags.None;
        foreground = TerminalColor.Default;
        background = TerminalColor.Default;
        hyperlink = 0;
        insertMode = false;
        lineFeedNewLine = false;
        g0Graphics = false;
        g1Graphics = false;
        activeCharset = 0;
        cursorVisible = true;
        cursorShape = CursorShape.Block;
        cursorBlinking = true;
        mouseTracking = MouseTracking.None;
        mouseSgr = false;
        modes = TerminalModes.AutoWrap;
        alternateActive = false;
        screen = primary;
        hyperlinks.Clear();
        hyperlinkIds.Clear();
        scrollbackDropped += scrollback.Count;
        scrollback.Clear();
        ClearScreen(primary);
        ClearScreen(alternate);
        ClampScreen(primary, columns, rows);
        ClampScreen(alternate, columns, rows);
        primary.CursorRow = 0;
        primary.CursorColumn = 0;
        alternate.CursorRow = 0;
        alternate.CursorColumn = 0;

        ResetTabStops();
    }

    private void SoftReset()
    {
        attributes = CellFlags.None;
        foreground = TerminalColor.Default;
        background = TerminalColor.Default;
        insertMode = false;
        lineFeedNewLine = false;
        cursorVisible = true;
        modes &= ~(TerminalModes.Origin | TerminalModes.ApplicationCursorKeys | TerminalModes.ApplicationKeypad);
        modes |= TerminalModes.AutoWrap;
        g0Graphics = false;
        g1Graphics = false;
        activeCharset = 0;
        TerminalScreen target = screen;
        target.ScrollTop = 0;
        target.ScrollBottom = rows - 1;
        target.Saved = default;
        MoveCursorTo(0, 0);
    }

    private void SaveCursor()
    {
        TerminalScreen target = screen;
        target.Saved = new SavedCursor
        {
            Valid = true,
            Row = target.CursorRow,
            Column = target.CursorColumn,
            PendingWrap = target.PendingWrap,
            Attributes = attributes,
            Foreground = foreground,
            Background = background,
            OriginMode = (modes & TerminalModes.Origin) != 0,
            AutoWrap = (modes & TerminalModes.AutoWrap) != 0,
            G0Graphics = g0Graphics,
            G1Graphics = g1Graphics,
            ActiveCharset = activeCharset,
            ScrollTop = target.ScrollTop,
            ScrollBottom = target.ScrollBottom,
        };
    }

    private void RestoreCursor()
    {
        TerminalScreen target = screen;
        SavedCursor saved = target.Saved;
        if (!saved.Valid)
        {
            MoveCursorTo(0, 0);
            return;
        }

        attributes = saved.Attributes;
        foreground = saved.Foreground;
        background = saved.Background;
        g0Graphics = saved.G0Graphics;
        g1Graphics = saved.G1Graphics;
        activeCharset = saved.ActiveCharset;
        target.ScrollTop = Math.Clamp(saved.ScrollTop, 0, rows - 1);
        target.ScrollBottom = Math.Clamp(saved.ScrollBottom, target.ScrollTop, rows - 1);
        target.CursorRow = Math.Clamp(saved.Row, 0, rows - 1);
        target.CursorColumn = Math.Clamp(saved.Column, 0, columns - 1);
        target.PendingWrap = saved.PendingWrap;
        if (saved.OriginMode)
        {
            modes |= TerminalModes.Origin;
        }
        else
        {
            modes &= ~TerminalModes.Origin;
        }

        if (saved.AutoWrap)
        {
            modes |= TerminalModes.AutoWrap;
        }
        else
        {
            modes &= ~TerminalModes.AutoWrap;
        }
    }

    /// <summary>DECALN (ESC # 8): fills the screen with 'E', homes the cursor and resets the margins.</summary>
    private void AlignmentTest()
    {
        TerminalCell cell = new() { Rune = 'E' };
        TerminalScreen target = screen;
        for (int i = 0; i < target.Lines.Length; i++)
        {
            target.Lines[i].Reset(columns, cell);
        }

        target.CursorRow = 0;
        target.CursorColumn = 0;
        target.PendingWrap = false;
        target.ScrollTop = 0;
        target.ScrollBottom = rows - 1;
    }
}
