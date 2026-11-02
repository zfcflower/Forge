namespace Forge.Editor.Text;

/// <summary>
/// Immutable view of a document at one version: a rope plus a lazily built line table. Offsets are UTF-16 code-unit
/// indexes into the text; line/character pairs use the snapshot's <see cref="PositionEncoding"/> for the character part.
/// </summary>
public sealed class TextSnapshot
{
    private readonly Rope _rope;
    private LineIndex? _lines;
    private string? _text;
    private ulong? _hash;

    public TextSnapshot(Rope rope, int version = 0, PositionEncoding positionEncoding = PositionEncoding.Utf16)
    {
        ArgumentNullException.ThrowIfNull(rope);
        _rope = rope;
        Version = version;
        PositionEncoding = positionEncoding;
    }

    public static TextSnapshot Empty { get; } = new(Rope.Empty);

    public int Version { get; }

    public PositionEncoding PositionEncoding { get; }

    public Rope Rope => _rope;

    public int Length => _rope.Length;

    public bool IsEmpty => _rope.IsEmpty;

    public int LineCount => Lines.LineCount;

    public LineEnding LineEnding => Lines.DominantLineEnding;

    public LineIndex Lines => GetLines();

    public char this[int index] => _rope[index];

    /// <summary>The whole text; materialised once per snapshot and cached.</summary>
    public string GetText() => _text ??= _rope.GetText();

    public string GetText(int offset, int length) => _rope.SliceToString(offset, length);

    /// <summary>FNV-1a 64 over the UTF-16 code units; cheap identity for caches keyed by content.</summary>
    public ulong ContentHash => _hash ??= _rope.ContentHash();

    public void CopyTo(int offset, Span<char> destination) => _rope.CopyTo(offset, destination);

    public LinePosition OffsetToPosition(int offset) => OffsetToPosition(offset, PositionEncoding);

    public LinePosition OffsetToPosition(int offset, PositionEncoding encoding)
    {
        var lines = GetLines();
        var clamped = Math.Clamp(offset, 0, Length);
        var line = lines.GetLineAtOffset(clamped);
        var character = CharacterOf(lines, line, clamped, encoding);
        return new LinePosition(line, character);
    }

    public int PositionToOffset(LinePosition position) => OffsetOfCharacter(position.Line, position.Character);

    /// <summary>Character offset (in the snapshot encoding) of <paramref name="offset"/> inside its line.</summary>
    public int GetCharacterOffset(int offset) => GetCharacterOffset(offset, PositionEncoding);

    /// <summary>Character offset of <paramref name="offset"/> inside its line, expressed in <paramref name="encoding"/>.</summary>
    public int GetCharacterOffset(int offset, PositionEncoding encoding)
    {
        var lines = GetLines();
        var clamped = Math.Clamp(offset, 0, Length);
        var line = lines.GetLineAtOffset(clamped);
        return CharacterOf(lines, line, clamped, encoding);
    }

    /// <summary>Text offset of a line plus a character offset expressed in the snapshot's position encoding.</summary>
    public int OffsetOfCharacter(int line, int character) => OffsetOfCharacter(line, character, PositionEncoding);

    /// <summary>Text offset of a line plus a character offset expressed in <paramref name="encoding"/>.</summary>
    public int OffsetOfCharacter(int line, int character, PositionEncoding encoding)
    {
        var lines = GetLines();
        if (lines.LineCount == 0)
        {
            return 0;
        }

        if (line < 0)
        {
            return 0;
        }

        if (line >= lines.LineCount)
        {
            return Length;
        }

        var start = lines.GetLineStart(line);
        var end = lines.GetLineEnd(line);
        if (character <= 0)
        {
            return start;
        }

        if (encoding == PositionEncoding.Utf16)
        {
            return Math.Min(start + character, end);
        }

        var text = _rope.SliceToString(start, end - start);
        var bytes = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var width = char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]) ? 2 : 1;
            var cost = System.Text.Encoding.UTF8.GetByteCount(text.AsSpan(i, width));
            if (bytes + cost > character)
            {
                return start + i;
            }

            bytes += cost;
            i += width - 1;
            if (bytes == character)
            {
                return start + i + 1;
            }
        }

        return end;
    }

    public int GetLineStart(int line) => Lines.GetLineStart(line);

    public int GetLineEnd(int line) => Lines.GetLineEnd(line);

    public int GetLineEndIncludingBreak(int line) => Lines.GetLineEndIncludingBreak(line);

    public int GetLineAtOffset(int offset) => Lines.GetLineAtOffset(Math.Clamp(offset, 0, Length));

    public string GetLineText(int line)
    {
        var lines = GetLines();
        return _rope.SliceToString(lines.GetLineStart(line), lines.GetLineEnd(line) - lines.GetLineStart(line));
    }

    /// <summary>Line text including its terminator, which is what a line diff compares.</summary>
    public string GetLineTextWithBreak(int line)
    {
        var lines = GetLines();
        var start = lines.GetLineStart(line);
        return _rope.SliceToString(start, lines.GetLineEndIncludingBreak(line) - start);
    }

    public Rope GetLineRope(int line)
    {
        var lines = GetLines();
        var start = lines.GetLineStart(line);
        return _rope.Slice(start, lines.GetLineEndIncludingBreak(line) - start);
    }

    /// <summary>Splits the text into lines without terminators.</summary>
    public string[] GetLineTexts()
    {
        var lines = GetLines();
        if (lines.LineCount == 0)
        {
            return [];
        }

        var result = new string[lines.LineCount];
        for (var line = 0; line < result.Length; line++)
        {
            var start = lines.GetLineStart(line);
            result[line] = _rope.SliceToString(start, lines.GetLineEnd(line) - start);
        }

        return result;
    }

    /// <summary>Splits the text into lines including each line's terminator, which is what a line diff compares.</summary>
    public string[] GetLineTextsWithBreak()
    {
        var lines = GetLines();
        if (lines.LineCount == 0)
        {
            return [];
        }

        var result = new string[lines.LineCount];
        for (var line = 0; line < result.Length; line++)
        {
            var start = lines.GetLineStart(line);
            result[line] = _rope.SliceToString(start, lines.GetLineEndIncludingBreak(line) - start);
        }

        return result;
    }

    public override string ToString() => GetText();

    public static TextSnapshot FromText(string text, int version = 0, PositionEncoding positionEncoding = PositionEncoding.Utf16) =>
        new(Rope.From(text), version, positionEncoding);

    private LineIndex GetLines()
    {
        var lines = Volatile.Read(ref _lines);
        if (lines is not null)
        {
            return lines;
        }

        lines = LineIndex.Build(_rope);
        return Interlocked.CompareExchange(ref _lines, lines, null) ?? lines;
    }

    private int CharacterOf(LineIndex lines, int line, int offset, PositionEncoding encoding)
    {
        var offsetInLine = offset - lines.GetLineStart(line);
        if (encoding == PositionEncoding.Utf16)
        {
            return offsetInLine;
        }

        return System.Text.Encoding.UTF8.GetByteCount(_rope.SliceToString(offset - offsetInLine, offsetInLine));
    }
}
