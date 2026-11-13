using System.Globalization;
using System.Text;

namespace Forge.App.ViewModels.Preview;

/// <summary>What one parse produced: the rows kept (up to the limit) and how many more the file held.</summary>
internal readonly record struct TablePreviewParse(int TotalRows, IReadOnlyList<IReadOnlyList<string>> Rows, int SkippedRows);

/// <summary>
/// A real RFC 4180 reader for table previews: quoted fields, doubled quotes, CRLF and LF line endings, embedded
/// newlines inside quotes, and ragged rows padded to the widest row. TSV is tab-separated without quoting. The parser
/// stores at most <c>maxRows</c> rows, keeps streaming to count the rest, and reports them as skipped. Runs off the UI
/// thread; pure logic over a <see cref="TextReader"/>, so tests feed it strings.
/// </summary>
internal static class TablePreviewReader
{
    public static TablePreviewParse Parse(TextReader reader, int maxRows, char separator)
    {
        var rows = new List<IReadOnlyList<string>>(Math.Min(Math.Max(maxRows, 1), 256));
        var row = new List<string>(8);
        var field = new StringBuilder();
        var skipped = 0;
        var rowCount = 0;
        var inQuotes = false;
        var afterQuote = false;
        var sawAny = false;
        var pendingCr = false;
        var quoting = separator == ',';

        int value;
        while ((value = reader.Read()) >= 0)
        {
            // A CR already ended the row; the LF of a CRLF pair is consumed, anything else starts the next row.
            if (pendingCr)
            {
                pendingCr = false;
                if (value == '\n')
                {
                    continue;
                }
            }

            var c = (char)value;
            if (inQuotes)
            {
                if (c == '"')
                {
                    inQuotes = false;
                    afterQuote = true;
                }
                else
                {
                    field.Append(c);
                }

                sawAny = true;
                continue;
            }

            if (afterQuote)
            {
                afterQuote = false;
                if (c == '"')
                {
                    // A doubled quote: one literal quote, and the field is still quoted.
                    field.Append('"');
                    inQuotes = true;
                }
                else if (c == separator)
                {
                    EndField();
                }
                else if (c is '\r' or '\n')
                {
                    EndField();
                    EndRow();
                    pendingCr = c == '\r';
                }
                else
                {
                    // A quote followed by other characters: liberal fallback, keep the character.
                    field.Append(c);
                }

                sawAny = true;
                continue;
            }

            if (quoting && c == '"' && !sawAny)
            {
                inQuotes = true;
                sawAny = true;
                continue;
            }

            if (c == separator)
            {
                EndField();
                continue;
            }

            if (c is '\r' or '\n')
            {
                EndField();
                EndRow();
                pendingCr = c == '\r';
                continue;
            }

            field.Append(c);
            sawAny = true;
        }

        // A trailing CR already ended its row; a pending field completes a final row without a line break.
        if (sawAny || row.Count > 0)
        {
            EndField();
            EndRow();
        }

        var width = 0;
        foreach (var parsed in rows)
        {
            width = Math.Max(width, parsed.Count);
        }

        if (width > 0)
        {
            for (var r = 0; r < rows.Count; r++)
            {
                if (rows[r].Count == width)
                {
                    continue;
                }

                var padded = new string[width];
                for (var c = 0; c < width; c++)
                {
                    padded[c] = c < rows[r].Count ? rows[r][c] : string.Empty;
                }

                rows[r] = padded;
            }
        }

        return new TablePreviewParse(rowCount, rows, skipped);

        void EndField()
        {
            row.Add(field.ToString());
            field.Clear();
            sawAny = false;
        }

        void EndRow()
        {
            // An empty line is one record with a single empty field; a trailing newline adds none.
            if (row.Count == 0 && !sawAny)
            {
                row.Add(string.Empty);
            }

            if (rowCount < maxRows)
            {
                rows.Add(row.ToArray());
            }
            else
            {
                skipped++;
            }

            rowCount++;
            row.Clear();
        }
    }

    /// <summary>
    /// The header heuristic: the first row is a header when one of its cells is text while the cell below it is a
    /// number (a named column over data). A file of all numbers, all text or a single row has no header.
    /// </summary>
    public static bool HasHeaderRow(IReadOnlyList<IReadOnlyList<string>> rows)
    {
        if (rows.Count < 2)
        {
            return false;
        }

        var header = rows[0];
        var data = rows[1];
        for (var column = 0; column < Math.Min(header.Count, data.Count); column++)
        {
            if (!IsNumber(header[column]) && IsNumber(data[column]))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Display width in monospace columns: wide East Asian characters count two, everything else one.</summary>
    public static int DisplayWidth(string text)
    {
        var width = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            width += IsWide(rune.Value) ? 2 : 1;
        }

        return width;
    }

    private static bool IsNumber(string cell) =>
        cell.Length > 0 && double.TryParse(cell, NumberStyles.Float, CultureInfo.InvariantCulture, out _);

    /// <summary>East Asian wide and fullwidth code points (and the common emoji blocks), which take two monospace columns.</summary>
    private static bool IsWide(int codePoint) =>
        codePoint is
            >= 0x1100 and <= 0x115F or
            >= 0x2E80 and <= 0x303E or
            >= 0x3041 and <= 0x33FF or
            >= 0x3400 and <= 0x4DBF or
            >= 0x4E00 and <= 0x9FFF or
            >= 0xA000 and <= 0xA4CF or
            >= 0xAC00 and <= 0xD7A3 or
            >= 0xF900 and <= 0xFAFF or
            >= 0xFE10 and <= 0xFE19 or
            >= 0xFE30 and <= 0xFE4F or
            >= 0xFF00 and <= 0xFF60 or
            >= 0xFFE0 and <= 0xFFE6 or
            >= 0x1F300 and <= 0x1F64F or
            >= 0x1F900 and <= 0x1F9FF or
            >= 0x20000 and <= 0x2FFFD or
            >= 0x30000 and <= 0x3FFFD;
}
