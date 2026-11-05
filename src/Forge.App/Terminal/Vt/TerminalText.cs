using System.Text;

namespace Forge.App.Terminal.Vt;

/// <summary>
/// Text extraction from the screen and the scrollback for copy / selection and for double-click word selection.
/// Line and column arguments use the same index space as <see cref="TerminalEmulator.GetLine"/>: line 0 is the oldest
/// scrollback line. Wide characters contribute one character (the second cell is skipped), combining marks stay
/// attached to the cell they belong to, soft-wrapped lines do not get a newline and trailing blanks are trimmed.
/// </summary>
internal static class TerminalText
{
    private const string WordDelimiters = "\"'`()[]{}<>,;:|";

    /// <summary>
    /// Text between two positions, inclusive. In linear mode the selection runs from <paramref name="start"/> to
    /// <paramref name="end"/> across lines; in block (column) mode every line between them contributes the same column
    /// range, which is what a rectangular selection needs.
    /// </summary>
    public static string GetText(
        TerminalEmulator emulator,
        (int line, int col) start,
        (int line, int col) end,
        bool block = false)
    {
        ArgumentNullException.ThrowIfNull(emulator);
        int total = emulator.TotalLines;
        if (total <= 0)
        {
            return string.Empty;
        }

        (int line, int col) first = start;
        (int line, int col) last = end;
        if (first.line > last.line || (first.line == last.line && first.col > last.col))
        {
            (first, last) = (last, first);
        }

        int finalLine = Math.Clamp(last.line, 0, total - 1);
        int firstLine = Math.Clamp(first.line, 0, finalLine);
        int lastColumn = emulator.Columns - 1;
        var builder = new StringBuilder();

        if (block)
        {
            int from = Math.Clamp(Math.Min(first.col, last.col), 0, lastColumn);
            int to = Math.Clamp(Math.Max(first.col, last.col), 0, lastColumn);
            for (int line = firstLine; line <= finalLine; line++)
            {
                if (line > firstLine)
                {
                    builder.Append('\n');
                }

                int lineStart = builder.Length;
                AppendCells(emulator.GetLine(line), from, to, builder);
                TrimTrailingBlanks(builder, lineStart);
            }

            return builder.ToString();
        }

        for (int line = firstLine; line <= finalLine; line++)
        {
            TerminalLine content = emulator.GetLine(line);
            int from = line == firstLine ? Math.Max(first.col, 0) : 0;
            int to = line == finalLine ? Math.Min(last.col, content.Count - 1) : content.Count - 1;
            int lineStart = builder.Length;
            AppendCells(content, from, to, builder);
            bool continues = line < finalLine;
            if (!continues || !content.Wrapped)
            {
                TrimTrailingBlanks(builder, lineStart);
            }

            if (continues && !content.Wrapped)
            {
                builder.Append('\n');
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// The word around <paramref name="column"/> as an inclusive <c>(start, end)</c> column range on that line. A word
    /// ends at whitespace and at the usual punctuation quotes and brackets, which is what double-click selection wants.
    /// </summary>
    public static (int start, int end) WordAt(TerminalEmulator emulator, int line, int col)
    {
        ArgumentNullException.ThrowIfNull(emulator);
        if (emulator.TotalLines <= 0)
        {
            return (0, 0);
        }

        line = Math.Clamp(line, 0, emulator.TotalLines - 1);
        TerminalLine content = emulator.GetLine(line);
        int columns = content.Count;
        if (columns <= 0)
        {
            return (0, 0);
        }

        col = Math.Clamp(col, 0, columns - 1);
        if (content[col].IsWideContinuation && col > 0)
        {
            col--;
        }

        if (!IsWordRune(content[col].Rune))
        {
            return (col, col);
        }

        int start = col;
        while (start > 0)
        {
            int previous = start - 1;
            if (content[previous].IsWideContinuation)
            {
                previous--;
            }

            if (previous < 0 || !IsWordRune(content[previous].Rune))
            {
                break;
            }

            start = previous;
        }

        int end = col;
        while (true)
        {
            int next = end + 1;
            if (next >= columns)
            {
                break;
            }

            if (content[next].IsWideContinuation)
            {
                next++;
            }

            if (next >= columns || !IsWordRune(content[next].Rune))
            {
                break;
            }

            end = next;
        }

        return (start, end);
    }

    /// <summary>True when the rune sticks to its neighbours for word selection (letters, digits, paths, flags).</summary>
    public static bool IsWordRune(int rune)
    {
        if (rune == 0 || rune == ' ')
        {
            return false;
        }

        if (rune < 0x80)
        {
            if (rune < 0x21 || rune == 0x7F)
            {
                return false;
            }

            return WordDelimiters.IndexOf((char)rune) < 0;
        }

        return !Rune.TryCreate(rune, out Rune value) || !Rune.IsWhiteSpace(value);
    }

    private static void AppendCells(TerminalLine line, int from, int to, StringBuilder builder)
    {
        if (from < 0)
        {
            from = 0;
        }

        to = Math.Min(to, line.Count - 1);
        for (int column = from; column <= to; column++)
        {
            TerminalCell cell = line[column];
            if (cell.IsWideContinuation)
            {
                continue;
            }

            if (cell.Rune == 0)
            {
                builder.Append(' ');
            }
            else
            {
                AppendRune(builder, cell.Rune);
            }

            string? combining = line.GetCombining(column);
            if (combining is not null)
            {
                builder.Append(combining);
            }
        }
    }

    private static void AppendRune(StringBuilder builder, int rune)
    {
        if (!Rune.TryCreate(rune, out Rune value))
        {
            builder.Append('\uFFFD');
            return;
        }

        Span<char> chars = stackalloc char[2];
        builder.Append(chars[..value.EncodeToUtf16(chars)]);
    }

    private static void TrimTrailingBlanks(StringBuilder builder, int floor)
    {
        while (builder.Length > floor && builder[^1] is ' ' or '\t')
        {
            builder.Length--;
        }
    }
}
