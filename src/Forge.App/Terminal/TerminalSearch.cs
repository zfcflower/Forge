using System.Text;
using Forge.App.Terminal.Vt;

namespace Forge.App.Terminal;

/// <summary>
/// A match found in the terminal buffer. Lines are stored relative to the emulator's <see cref="TerminalEmulator.ScrollbackDropped"/>
/// counter at snapshot time (<c>LineBase = absolute line + dropped</c>), so later scrollback drops only subtract from the
/// counter: a match's current absolute line is <c>LineBase - dropped</c> and needs no per-drop shifting, the same trick the
/// selection anchor uses.
/// </summary>
internal readonly record struct TerminalSearchMatch(long LineBase, int StartColumn, int EndColumn)
{
    /// <summary>The line in the emulator's current index space, or negative once the line was dropped from the scrollback.</summary>
    public int AbsoluteLine(long dropped) => (int)(LineBase - dropped);
}

/// <summary>One line's plain text plus, for every UTF-16 code unit, the cell column it starts in and how many cells it spans.
/// A <see langword="default"/> value stands for a blank row: <see cref="TerminalSearch.Find"/> skips it without touching the maps.</summary>
internal readonly struct TerminalSearchLine(string text, int[] startColumns, int[] widths)
{
    public string Text { get; } = text;

    public int[] StartColumns { get; } = startColumns;

    public int[] Widths { get; } = widths;
}

/// <summary>
/// Search over the terminal's buffer text (screen and scrollback). Pure and allocation-light: it takes an already
/// extracted <see cref="TerminalSearchLine"/> snapshot, so it never touches the emulator or the UI thread. Matching is
/// per line (terminal search is line based, not cross-line), case sensitivity follows the toggle, and wide characters map
/// back to their two-cell column range so the highlight covers exactly what the glyph covers.
/// </summary>
internal static class TerminalSearch
{
    /// <summary>
    /// Extracts one row's text and column map. Blank cells become spaces (they are part of the visual line), wide
    /// characters contribute one rune but two cell columns, and combining marks attach to the base cell.
    /// </summary>
    public static TerminalSearchLine SnapshotLine(TerminalLine line)
    {
        ArgumentNullException.ThrowIfNull(line);
        var text = new StringBuilder(line.Count);
        var startColumns = new int[line.Count + 8];
        var widths = new int[line.Count + 8];
        var count = 0;

        void AppendChar(char character, int column, int width)
        {
            if (count == startColumns.Length)
            {
                Array.Resize(ref startColumns, startColumns.Length * 2);
                Array.Resize(ref widths, widths.Length * 2);
            }

            text.Append(character);
            startColumns[count] = column;
            widths[count] = width;
            count++;
        }

        for (var column = 0; column < line.Count; column++)
        {
            var cell = line[column];
            if (cell.IsWideContinuation)
            {
                continue;
            }

            if (cell.Rune == 0)
            {
                AppendChar(' ', column, 1);
                continue;
            }

            var rune = char.ConvertFromUtf32(cell.Rune);
            AppendChar(rune[0], column, CellWidth.Of(cell.Rune));
            for (var i = 1; i < rune.Length; i++)
            {
                AppendChar(rune[i], column, 0);
            }

            var combining = line.GetCombining(column);
            if (combining is null)
            {
                continue;
            }

            foreach (var character in combining)
            {
                AppendChar(character, column, 0);
            }
        }

        return new TerminalSearchLine(text.ToString(), count == startColumns.Length ? startColumns : startColumns[..count], count == widths.Length ? widths : widths[..count]);
    }

    /// <summary>Finds every per-line match of <paramref name="query"/> across <paramref name="lines"/>.</summary>
    public static List<TerminalSearchMatch> Find(
        IReadOnlyList<TerminalSearchLine> lines,
        string query,
        bool caseSensitive,
        long baseDropped)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(query);
        if (query.Length == 0)
        {
            return [];
        }

        var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var matches = new List<TerminalSearchMatch>();
        for (var line = 0; line < lines.Count; line++)
        {
            var snapshot = lines[line];
            var text = snapshot.Text;

            // A default line is a blank row: the caller skips blank rows on purpose, so there is nothing to scan.
            if (text is not { Length: > 0 })
            {
                continue;
            }

            var searchFrom = 0;
            while (searchFrom <= text.Length - query.Length)
            {
                var found = text.IndexOf(query, searchFrom, comparison);
                if (found < 0)
                {
                    break;
                }

                var last = found + query.Length - 1;
                matches.Add(new TerminalSearchMatch(
                    line + baseDropped,
                    snapshot.StartColumns[found],
                    snapshot.StartColumns[last] + Math.Max(0, snapshot.Widths[last] - 1)));
                searchFrom = found + query.Length;
            }
        }

        return matches;
    }
}
