namespace Forge.Editor.Text;

/// <summary>A zero-based line/character pair; the character is an offset inside the line in the document's position encoding.</summary>
public readonly record struct LinePosition(int Line, int Character)
{
    public override string ToString() => $"{Line}:{Character}";
}

/// <summary>How LSP-style columns are counted.</summary>
public enum PositionEncoding
{
    /// <summary>UTF-16 code units, which is what .NET strings index by (LSP default).</summary>
    Utf16,

    /// <summary>UTF-8 bytes.</summary>
    Utf8,
}

public enum LineEnding
{
    /// <summary>The text has no line break at all.</summary>
    None,

    Lf,
    CrLf,
    Cr,

    /// <summary>More than one kind of line break occurs in the text.</summary>
    Mixed,
}

/// <summary>Counts per line-break kind, as found in one snapshot.</summary>
public readonly record struct LineEndingInfo(int Lf, int CrLf, int Cr)
{
    public int Total => Lf + CrLf + Cr;

    public LineEnding Dominant
    {
        get
        {
            if (Total == 0)
            {
                return LineEnding.None;
            }

            var kinds = (Lf > 0 ? 1 : 0) + (CrLf > 0 ? 1 : 0) + (Cr > 0 ? 1 : 0);
            if (kinds > 1)
            {
                return LineEnding.Mixed;
            }

            return CrLf > 0 ? LineEnding.CrLf : Cr > 0 ? LineEnding.Cr : LineEnding.Lf;
        }
    }

    public static string Text(LineEnding ending) => ending switch
    {
        LineEnding.CrLf => "\r\n",
        LineEnding.Cr => "\r",
        _ => "\n",
    };
}

/// <summary>
/// Immutable line table for one rope. Line <c>i</c> spans <c>[GetLineStart(i), GetLineEnd(i))</c>; the terminator is
/// excluded from the line end but included when asked for, so both LSP ranges and selections map without extra scans.
/// </summary>
public sealed class LineIndex
{
    private const byte BreakNone = 0;
    private const byte BreakLf = 1;
    private const byte BreakCrLf = 2;
    private const byte BreakCr = 3;

    private readonly int[] _starts;
    private readonly byte[] _breaks;

    private LineIndex(int[] starts, byte[] breaks, LineEndingInfo endings)
    {
        _starts = starts;
        _breaks = breaks;
        Endings = endings;
    }

    /// <summary>Number of lines; a text ending with a line break has a trailing empty line.</summary>
    public int LineCount => _breaks.Length;

    public int TextLength => _starts[^1];

    public LineEndingInfo Endings { get; }

    public LineEnding DominantLineEnding => Endings.Dominant;

    public static LineIndex Build(Rope rope)
    {
        ArgumentNullException.ThrowIfNull(rope);
        var capacity = rope.LineBreakCount + 2;
        var starts = new List<int>(capacity) { 0 };
        var breaks = new List<byte>(capacity);
        int lf = 0, crlf = 0, cr = 0;
        var offset = 0;
        var pendingCr = false;

        foreach (var chunk in rope)
        {
            var span = chunk.Span;
            for (var i = 0; i < span.Length; i++)
            {
                var c = span[i];
                if (pendingCr)
                {
                    pendingCr = false;
                    if (c == '\n')
                    {
                        starts.Add(offset + i + 1);
                        breaks.Add(BreakCrLf);
                        crlf++;
                        continue;
                    }

                    starts.Add(offset + i);
                    breaks.Add(BreakCr);
                    cr++;
                }

                if (c == '\r')
                {
                    pendingCr = true;
                }
                else if (c == '\n')
                {
                    starts.Add(offset + i + 1);
                    breaks.Add(BreakLf);
                    lf++;
                }
            }

            offset += span.Length;
        }

        if (pendingCr)
        {
            starts.Add(rope.Length);
            breaks.Add(BreakCr);
            cr++;
        }

        starts.Add(rope.Length);
        breaks.Add(BreakNone);
        return new LineIndex([.. starts], [.. breaks], new LineEndingInfo(lf, crlf, cr));
    }

    public int GetLineStart(int line)
    {
        ValidateLine(line);
        return _starts[line];
    }

    /// <summary>Offset just past the last character of the line, excluding the line break.</summary>
    public int GetLineEnd(int line)
    {
        ValidateLine(line);
        return _starts[line + 1] - BreakLength(_breaks[line]);
    }

    /// <summary>Offset just past the line break, i.e. the start of the next line.</summary>
    public int GetLineEndIncludingBreak(int line)
    {
        ValidateLine(line);
        return _starts[line + 1];
    }

    public int GetLineBreakLength(int line)
    {
        ValidateLine(line);
        return BreakLength(_breaks[line]);
    }

    private static int BreakLength(byte code) => code switch
    {
        BreakLf => 1,
        BreakCrLf => 2,
        BreakCr => 1,
        _ => 0,
    };

    /// <summary>Line containing <paramref name="offset"/>; offsets outside the text clamp to the first or last line.</summary>
    public int GetLineAtOffset(int offset)
    {
        if (offset <= 0 || LineCount == 0)
        {
            return 0;
        }

        if (offset >= TextLength)
        {
            return LineCount - 1;
        }

        var low = 0;
        var high = LineCount - 1;
        while (low < high)
        {
            var mid = (low + high + 1) >> 1;
            if (_starts[mid] <= offset)
            {
                low = mid;
            }
            else
            {
                high = mid - 1;
            }
        }

        return low;
    }

    private void ValidateLine(int line)
    {
        if ((uint)line >= (uint)LineCount)
        {
            throw new ArgumentOutOfRangeException(nameof(line), line, "Line is outside the text.");
        }
    }
}
