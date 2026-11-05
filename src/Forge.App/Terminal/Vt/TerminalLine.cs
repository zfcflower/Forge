using System.Text;

namespace Forge.App.Terminal.Vt;

/// <summary>
/// One terminal row. The emulator reuses line objects (scrollback ring, alternate screen, rows scrolled out of a
/// region) instead of allocating an array per row, so a full-screen scroll moves pointers only.
/// <see cref="Version"/> changes on every content change so a renderer can cache glyph runs per line; combining marks
/// live in a per-column side table that stays <see langword="null"/> for the common case (lines without diacritics).
/// A double-width character occupies two cells: the base rune in the first and
/// <see cref="CellFlags.WideContinuation"/> in the second.
/// </summary>
internal sealed class TerminalLine
{
    private const int MaxCombiningLength = 64;

    private TerminalCell[] cells;
    private string?[]? combining;
    private bool hasWide;

    internal TerminalLine(int columns)
    {
        cells = new TerminalCell[Math.Max(columns, 0)];
    }

    /// <summary>Number of cells in the line; always equal to the emulator width at the time of the last resize.</summary>
    public int Count => cells.Length;

    /// <summary>Incremented on every change, so a renderer can keep a per-line cache keyed by object and version.</summary>
    public long Version { get; private set; }

    /// <summary>True when the line continues onto the next one because of auto-wrap (a soft break, not a newline).</summary>
    public bool Wrapped { get; private set; }

    /// <summary>All cells; the second half of a wide character carries <see cref="CellFlags.WideContinuation"/>.</summary>
    public ReadOnlySpan<TerminalCell> Cells => cells;

    public TerminalCell this[int column] => cells[column];

    /// <summary>The zero-width characters attached to <paramref name="column"/>, or <see langword="null"/> for none.</summary>
    public string? GetCombining(int column)
    {
        string?[]? table = combining;
        return table is not null && (uint)column < (uint)cells.Length ? table[column] : null;
    }

    internal void SetWrapped(bool wrapped)
    {
        if (Wrapped == wrapped)
        {
            return;
        }

        Wrapped = wrapped;
        Version++;
    }

    /// <summary>Clears the whole line and (optionally) resizes it; used when a line object is recycled.</summary>
    internal void Reset(int columns, in TerminalCell fill)
    {
        if (cells.Length != columns)
        {
            cells = new TerminalCell[columns];
            combining = null;
        }
        else if (combining is not null)
        {
            Array.Clear(combining);
        }

        if (fill.IsBlank)
        {
            Array.Clear(cells);
        }
        else
        {
            Array.Fill(cells, fill);
        }

        hasWide = false;
        Wrapped = false;
        Version++;
    }

    /// <summary>Changes the line width, keeping the overlapping part and padding or truncating (no reflow).</summary>
    internal void Resize(int columns)
    {
        columns = Math.Max(columns, 0);
        if (columns == cells.Length)
        {
            return;
        }

        var replacement = new TerminalCell[columns];
        Array.Copy(cells, replacement, Math.Min(columns, cells.Length));
        if (combining is not null)
        {
            var replacementCombining = new string?[columns];
            Array.Copy(combining, replacementCombining, Math.Min(columns, combining.Length));
            combining = replacementCombining;
        }

        cells = replacement;
        if (hasWide && columns > 0)
        {
            // A wide character cut in half at the new right edge would render as a stray glyph; blank it.
            int last = columns - 1;
            if (cells[last].IsWideContinuation)
            {
                cells[last] = default;
            }
            else if (CellWidth.Of(cells[last].Rune) == 2)
            {
                cells[last] = default;
            }
        }

        Version++;
    }

    internal void SetCell(int column, int rune, CellFlags flags, TerminalColor foreground, TerminalColor background, int hyperlinkId)
    {
        ref TerminalCell cell = ref cells[column];
        cell.Rune = rune;
        cell.Flags = flags;
        cell.Foreground = foreground;
        cell.Background = background;
        cell.HyperlinkId = hyperlinkId;
        if (combining is not null)
        {
            combining[column] = null;
        }

        Version++;
    }

    /// <summary>Writes the second half of a double-width character.</summary>
    internal void SetContinuation(int column, CellFlags flags, TerminalColor foreground, TerminalColor background, int hyperlinkId)
    {
        ref TerminalCell cell = ref cells[column];
        cell.Rune = 0;
        cell.Flags = flags | CellFlags.WideContinuation;
        cell.Foreground = foreground;
        cell.Background = background;
        cell.HyperlinkId = hyperlinkId;
        if (combining is not null)
        {
            combining[column] = null;
        }

        hasWide = true;
        Version++;
    }

    /// <summary>Blanks one cell (with the current erase colour) and drops its combining marks.</summary>
    internal void Clear(int column, in TerminalCell fill)
    {
        cells[column] = fill;
        if (combining is not null)
        {
            combining[column] = null;
        }

        Version++;
    }

    /// <summary>Blanks <c>[start, end)</c> and drops the combining marks of the range.</summary>
    internal void Fill(int start, int end, in TerminalCell fill)
    {
        if (end <= start)
        {
            return;
        }

        if (fill.IsBlank)
        {
            Array.Clear(cells, start, end - start);
        }
        else
        {
            Array.Fill(cells, fill, start, end - start);
        }

        if (combining is not null)
        {
            Array.Clear(combining, start, end - start);
        }

        if (start == 0 && end == cells.Length)
        {
            hasWide = false;
        }

        Version++;
    }

    /// <summary>Fast path for a run of printable ASCII: one version bump, no per-character attribute lookups.</summary>
    internal void WriteAsciiRun(int column, ReadOnlySpan<byte> ascii, CellFlags flags, TerminalColor foreground, TerminalColor background, int hyperlinkId)
    {
        TerminalCell[] cells = this.cells;
        if (!hasWide)
        {
            for (int i = 0; i < ascii.Length; i++)
            {
                ref TerminalCell cell = ref cells[column + i];
                cell.Rune = ascii[i];
                cell.Flags = flags;
                cell.Foreground = foreground;
                cell.Background = background;
                cell.HyperlinkId = hyperlinkId;
            }
        }
        else
        {
            for (int i = 0; i < ascii.Length; i++)
            {
                int col = column + i;
                RepairWideNeighbours(cells, col);
                ref TerminalCell cell = ref cells[col];
                cell.Rune = ascii[i];
                cell.Flags = flags;
                cell.Foreground = foreground;
                cell.Background = background;
                cell.HyperlinkId = hyperlinkId;
            }
        }

        if (combining is not null)
        {
            Array.Clear(combining, column, ascii.Length);
        }

        Version++;
    }

    internal void InsertCells(int column, int count, in TerminalCell fill)
    {
        int width = cells.Length;
        count = Math.Min(count, width - column);
        if (count <= 0)
        {
            return;
        }

        Array.Copy(cells, column, cells, column + count, width - column - count);
        if (fill.IsBlank)
        {
            Array.Clear(cells, column, count);
        }
        else
        {
            Array.Fill(cells, fill, column, count);
        }

        if (combining is not null)
        {
            Array.Copy(combining, column, combining, column + count, width - column - count);
            Array.Clear(combining, column, count);
        }

        Version++;
    }

    internal void DeleteCells(int column, int count, in TerminalCell fill)
    {
        int width = cells.Length;
        count = Math.Min(count, width - column);
        if (count <= 0)
        {
            return;
        }

        Array.Copy(cells, column + count, cells, column, width - column - count);
        if (fill.IsBlank)
        {
            Array.Clear(cells, width - count, count);
        }
        else
        {
            Array.Fill(cells, fill, width - count, count);
        }

        if (combining is not null)
        {
            Array.Copy(combining, column + count, combining, column, width - column - count);
            Array.Clear(combining, width - count, count);
        }

        Version++;
    }

    /// <summary>Attaches a zero-width rune to the cell at <paramref name="column"/>.</summary>
    internal void AppendCombining(int column, int rune)
    {
        if (!Rune.TryCreate(rune, out Rune value))
        {
            return;
        }

        string?[] table = combining ??= new string?[cells.Length];
        Span<char> encoded = stackalloc char[2];
        int written = value.EncodeToUtf16(encoded);
        string? existing = table[column];
        if (existing is null)
        {
            table[column] = new string(encoded[..written]);
        }
        else if (existing.Length + written <= MaxCombiningLength)
        {
            table[column] = string.Concat(existing, encoded[..written]);
        }

        Version++;
    }

    /// <summary>Blanks both halves of a wide character that straddles <paramref name="column"/>.</summary>
    private static void RepairWideNeighbours(TerminalCell[] cells, int column)
    {
        if (cells[column].IsWideContinuation && column > 0)
        {
            cells[column - 1] = default;
        }

        int next = column + 1;
        if (next < cells.Length && cells[next].IsWideContinuation)
        {
            cells[next] = default;
        }
    }
}
