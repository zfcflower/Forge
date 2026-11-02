using System.Text;
using Forge.Editor.Text;

namespace Forge.Editor.Tests.Text;

public class RopeModelTests
{
    private static readonly string[] Pieces =
    [
        "a", "b", "x", " ", "\t", "\r", "\n", "\r\n", "\n\r", "é", "中", "😀", "\uD83D", "\uDE00", "line of text\n",
        "fn main() {\r\n", "}\r", "\r\r\n\n",
    ];

    [Fact]
    public void Empty_rope_has_no_text_and_one_line()
    {
        var snapshot = TextSnapshot.FromText(string.Empty);

        Assert.Equal(0, Rope.Empty.Length);
        Assert.True(Rope.Empty.IsEmpty);
        Assert.Equal(string.Empty, snapshot.GetText());
        Assert.Equal(1, snapshot.LineCount);
        Assert.Equal(new LinePosition(0, 0), snapshot.OffsetToPosition(0));
        Assert.Equal(0, snapshot.OffsetOfCharacter(5, 5));
        Assert.Equal(Rope.From(string.Empty).ContentHash(), Rope.Empty.ContentHash());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(7)]
    [InlineData(20240611)]
    public void Random_edits_agree_with_a_StringBuilder_model(int seed)
    {
        var random = new Random(seed);
        var model = new StringBuilder();
        var rope = Rope.Empty;
        for (var step = 0; step < 300; step++)
        {
            var offset = random.Next(model.Length + 1);
            var span = random.Next(Math.Min(model.Length - offset, 1800) + 1);
            var text = RandomText(random);
            switch (random.Next(3))
            {
                case 0:
                    rope = rope.Insert(offset, text);
                    model.Insert(offset, text);
                    break;
                case 1:
                    rope = rope.Replace(offset, span, text);
                    model.Remove(offset, span).Insert(offset, text);
                    break;
                default:
                    rope = rope.Remove(offset, span);
                    model.Remove(offset, span);
                    break;
            }

            AssertRopeMatches(model.ToString(), rope, random, step);
        }
    }

    [Fact]
    public void Content_hash_depends_only_on_text_not_on_tree_shape()
    {
        var text = new string('q', 5000) + "\r\n" + new string('z', 3000);
        var built = Rope.From(text);
        var grown = Rope.Empty;
        for (var i = 0; i < text.Length; i += 37)
        {
            grown = grown.Insert(grown.Length, text.AsSpan(i, Math.Min(37, text.Length - i)));
        }

        Assert.Equal(text, grown.GetText());
        Assert.Equal(built.ContentHash(), grown.ContentHash());
        Assert.NotEqual(built.ContentHash(), Rope.From(text + "!").ContentHash());
        Assert.NotEqual(Rope.From("ab").ContentHash(), Rope.From("ba").ContentHash());
    }

    [Fact]
    public void Chunked_build_matches_single_build_for_line_queries()
    {
        var text = string.Concat(Enumerable.Repeat("abc\r\ndef\rghi\nj", 400));
        var built = TextSnapshot.FromText(text);
        var grown = Rope.Empty;
        foreach (var piece in text.Chunk(13))
        {
            grown = grown.Insert(grown.Length, new string(piece));
        }

        var grownSnapshot = new TextSnapshot(grown);
        Assert.Equal(built.LineCount, grownSnapshot.LineCount);
        Assert.Equal(built.Lines.Endings, grownSnapshot.Lines.Endings);
        Assert.Equal(built.GetLineTexts(), grownSnapshot.GetLineTexts());
    }

    [Fact]
    public void Crlf_split_across_leaf_boundary_is_one_break()
    {
        var text = new string('a', 1023) + "\r\n" + new string('b', 1000);
        var snapshot = TextSnapshot.FromText(text);

        Assert.Equal(2, snapshot.LineCount);
        Assert.Equal(2, snapshot.Lines.GetLineBreakLength(0));
        Assert.Equal(1023, snapshot.GetLineEnd(0));
        Assert.Equal(new LineEndingInfo(0, 1, 0), snapshot.Lines.Endings);
    }

    [Fact]
    public void Lone_cr_at_leaf_boundary_is_a_break()
    {
        var text = new string('a', 1023) + "\r" + "x" + new string('b', 1000);
        var snapshot = TextSnapshot.FromText(text);

        Assert.Equal(2, snapshot.LineCount);
        Assert.Equal(1024, snapshot.GetLineStart(1));
        Assert.Equal(new LineEndingInfo(0, 0, 1), snapshot.Lines.Endings);
    }

    [Fact]
    public void Mixed_line_endings_are_counted_per_kind()
    {
        var snapshot = TextSnapshot.FromText("a\r\nb\rc\nd");

        Assert.Equal(4, snapshot.LineCount);
        Assert.Equal(new[] { "a", "b", "c", "d" }, snapshot.GetLineTexts());
        Assert.Equal("a\r\n", snapshot.GetLineTextWithBreak(0));
        Assert.Equal("b\r", snapshot.GetLineTextWithBreak(1));
        Assert.Equal(new LineEndingInfo(1, 1, 1), snapshot.Lines.Endings);
        Assert.Equal(LineEnding.Mixed, snapshot.LineEnding);
    }

    [Fact]
    public void Dominant_line_ending_is_reported_for_uniform_text()
    {
        Assert.Equal(LineEnding.CrLf, TextSnapshot.FromText("a\r\nb\r\n").LineEnding);
        Assert.Equal(LineEnding.Lf, TextSnapshot.FromText("a\nb").LineEnding);
        Assert.Equal(LineEnding.Cr, TextSnapshot.FromText("a\rb").LineEnding);
        Assert.Equal(LineEnding.None, TextSnapshot.FromText("ab").LineEnding);
    }

    [Fact]
    public void Trailing_newline_creates_an_empty_last_line()
    {
        var snapshot = TextSnapshot.FromText("one\ntwo\n");

        Assert.Equal(3, snapshot.LineCount);
        Assert.Equal(string.Empty, snapshot.GetLineText(2));
        Assert.Equal(8, snapshot.GetLineStart(2));
        Assert.Equal(new LinePosition(2, 0), snapshot.OffsetToPosition(8));
    }

    [Fact]
    public void Utf16_and_utf8_columns_for_a_surrogate_pair()
    {
        var snapshot = TextSnapshot.FromText("a😀b\nc");

        Assert.Equal(new LinePosition(0, 3), snapshot.OffsetToPosition(3, PositionEncoding.Utf16));
        Assert.Equal(new LinePosition(0, 5), snapshot.OffsetToPosition(3, PositionEncoding.Utf8));
        Assert.Equal(3, snapshot.OffsetOfCharacter(0, 3, PositionEncoding.Utf16));
        Assert.Equal(3, snapshot.OffsetOfCharacter(0, 5, PositionEncoding.Utf8));
        Assert.Equal(1, snapshot.OffsetOfCharacter(0, 1, PositionEncoding.Utf8));
        Assert.Equal(1, snapshot.OffsetOfCharacter(0, 4, PositionEncoding.Utf8));
    }

    [Fact]
    public void Utf8_column_inside_a_multibyte_character_snaps_to_its_start()
    {
        var snapshot = TextSnapshot.FromText("é!");

        Assert.Equal(0, snapshot.OffsetOfCharacter(0, 1, PositionEncoding.Utf8));
        Assert.Equal(1, snapshot.OffsetOfCharacter(0, 2, PositionEncoding.Utf8));
        Assert.Equal(2, snapshot.OffsetOfCharacter(0, 3, PositionEncoding.Utf8));
    }

    [Fact]
    public void Positions_clamp_to_document_bounds()
    {
        var snapshot = TextSnapshot.FromText("ab\ncd");

        Assert.Equal(new LinePosition(1, 2), snapshot.OffsetToPosition(999));
        Assert.Equal(new LinePosition(0, 0), snapshot.OffsetToPosition(-5));
        Assert.Equal(snapshot.Length, snapshot.OffsetOfCharacter(9, 0));
        Assert.Equal(snapshot.GetLineStart(0), snapshot.OffsetOfCharacter(-1, 3));
        Assert.Equal(snapshot.GetLineEnd(0), snapshot.OffsetOfCharacter(0, 99));
    }

    [Fact]
    public void Position_inside_crlf_resolves_to_the_line_before_the_break()
    {
        var snapshot = TextSnapshot.FromText("ab\r\ncd");

        Assert.Equal(0, snapshot.GetLineAtOffset(3));
        Assert.Equal(1, snapshot.GetLineAtOffset(4));
        Assert.Equal(2, snapshot.GetLineEnd(0));
        Assert.Equal(4, snapshot.GetLineEndIncludingBreak(0));
    }

    [Fact]
    public void Random_surrogate_free_positions_round_trip_in_both_encodings()
    {
        var random = new Random(99);
        var text = string.Concat(Enumerable.Range(0, 200).Select(_ => RandomText(random, surrogates: false)));
        var snapshot = TextSnapshot.FromText(text);
        for (var i = 0; i < 500; i++)
        {
            var offset = random.Next(text.Length + 1);
            if (IsInsideBreakOrPair(text, offset))
            {
                continue;
            }

            var utf16 = snapshot.OffsetToPosition(offset, PositionEncoding.Utf16);
            Assert.Equal(offset, snapshot.OffsetOfCharacter(utf16.Line, utf16.Character, PositionEncoding.Utf16));
            var utf8 = snapshot.OffsetToPosition(offset, PositionEncoding.Utf8);
            Assert.Equal(offset, snapshot.OffsetOfCharacter(utf8.Line, utf8.Character, PositionEncoding.Utf8));
        }
    }

    [Fact]
    public void Snapshot_encoding_drives_position_helpers()
    {
        var snapshot = new TextSnapshot(Rope.From("é😀x"), 3, PositionEncoding.Utf8);

        Assert.Equal(PositionEncoding.Utf8, snapshot.PositionEncoding);
        Assert.Equal(3, snapshot.Version);
        Assert.Equal(new LinePosition(0, 6), snapshot.OffsetToPosition(3));
        Assert.Equal(3, snapshot.PositionToOffset(new LinePosition(0, 6)));
        Assert.Equal(6, snapshot.GetCharacterOffset(3));
        Assert.Equal(3, snapshot.GetCharacterOffset(3, PositionEncoding.Utf16));
    }

    [Fact]
    public void Slice_copy_and_indexer_agree_with_string_operations()
    {
        var text = string.Concat(Enumerable.Repeat("0123456789ABCDEFGHIJ", 150));
        var rope = Rope.From(text);
        var random = new Random(5);
        for (var i = 0; i < 300; i++)
        {
            var start = random.Next(text.Length);
            var length = random.Next(text.Length - start + 1);
            Assert.Equal(text.Substring(start, length), rope.SliceToString(start, length));
            Assert.Equal(text.Substring(start, length), rope.Slice(start, length).GetText());

            var buffer = new char[length];
            rope.CopyTo(start, buffer);
            Assert.Equal(text.Substring(start, length), new string(buffer));
            Assert.Equal(text[start], rope[start]);
        }
    }

    [Fact]
    public void Out_of_range_edits_throw_without_changing_the_rope()
    {
        var rope = Rope.From("abc");

        Assert.Throws<ArgumentOutOfRangeException>(() => rope.Replace(4, 0, "x"));
        Assert.Throws<ArgumentOutOfRangeException>(() => rope.Remove(2, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => _ = rope[3]);
        Assert.Throws<ArgumentOutOfRangeException>(() => _ = rope[-1]);
        Assert.Equal("abc", rope.GetText());
    }

    [Fact]
    public void Line_index_rejects_out_of_range_lines()
    {
        var lines = TextSnapshot.FromText("a\nb").Lines;

        Assert.Throws<ArgumentOutOfRangeException>(() => lines.GetLineStart(2));
        Assert.Throws<ArgumentOutOfRangeException>(() => lines.GetLineEnd(-1));
        Assert.Equal(1, lines.GetLineAtOffset(2));
        Assert.Equal(0, lines.GetLineAtOffset(-3));
    }

    [Fact]
    public void Trailing_lone_cr_starts_a_new_line()
    {
        var snapshot = TextSnapshot.FromText("a\r");

        Assert.Equal(2, snapshot.LineCount);
        Assert.Equal(new LinePosition(1, 0), snapshot.OffsetToPosition(2));
    }

    private static string RandomText(Random random, bool surrogates = true)
    {
        if (random.Next(60) == 0)
        {
            return new string((char)('a' + random.Next(26)), random.Next(1200, 2600));
        }

        var count = random.Next(0, 6);
        var builder = new StringBuilder();
        for (var i = 0; i < count; i++)
        {
            var piece = Pieces[random.Next(Pieces.Length)];
            if (!surrogates && piece.Any(char.IsSurrogate))
            {
                piece = "k";
            }

            builder.Append(piece);
        }

        return builder.ToString();
    }

    private static void AssertRopeMatches(string expected, Rope rope, Random random, int step)
    {
        Assert.Equal(expected.Length, rope.Length);
        Assert.Equal(expected, rope.GetText());
        Assert.Equal(expected.Count(c => c == '\n'), rope.LineBreakCount);
        Assert.Equal(Rope.From(expected).ContentHash(), rope.ContentHash());

        if (expected.Length > 0)
        {
            var probe = random.Next(expected.Length);
            Assert.Equal(expected[probe], rope[probe]);
            var start = random.Next(expected.Length);
            var length = random.Next(expected.Length - start + 1);
            Assert.Equal(expected.Substring(start, length), rope.Slice(start, length).GetText());
        }

        if (expected.EndsWith('\r'))
        {
            return;
        }

        var snapshot = new TextSnapshot(rope, step, PositionEncoding.Utf16);
        var starts = LineStarts(expected);
        Assert.Equal(starts.Count, snapshot.LineCount);
        Assert.Equal(CountEndings(expected), snapshot.Lines.Endings);

        for (var i = 0; i < 12; i++)
        {
            var offset = random.Next(expected.Length + 1);
            var line = LineOf(starts, offset);
            var lineStart = starts[line];
            Assert.Equal(new LinePosition(line, offset - lineStart), snapshot.OffsetToPosition(offset, PositionEncoding.Utf16));
            var utf8Column = Encoding.UTF8.GetByteCount(expected.AsSpan(lineStart, offset - lineStart));
            Assert.Equal(new LinePosition(line, utf8Column), snapshot.OffsetToPosition(offset, PositionEncoding.Utf8));

            if (IsInsideBreakOrPair(expected, offset))
            {
                continue;
            }

            Assert.Equal(offset, snapshot.OffsetOfCharacter(line, offset - lineStart, PositionEncoding.Utf16));
            Assert.Equal(offset, snapshot.OffsetOfCharacter(line, utf8Column, PositionEncoding.Utf8));
        }

        var lineToCheck = random.Next(starts.Count);
        var isLast = lineToCheck + 1 == starts.Count;
        var contentEnd = isLast ? expected.Length : ContentEnd(expected, starts[lineToCheck + 1]);
        Assert.Equal(starts[lineToCheck], snapshot.GetLineStart(lineToCheck));
        Assert.Equal(contentEnd, snapshot.GetLineEnd(lineToCheck));
    }

    private static int ContentEnd(string text, int nextStart)
    {
        if (nextStart >= 2 && text[nextStart - 1] == '\n' && text[nextStart - 2] == '\r')
        {
            return nextStart - 2;
        }

        return nextStart - 1;
    }

    private static bool IsInsideBreakOrPair(string text, int offset) =>
        offset > 0 && offset < text.Length &&
        ((text[offset - 1] == '\r' && text[offset] == '\n') || (char.IsHighSurrogate(text[offset - 1]) && char.IsLowSurrogate(text[offset])));

    private static List<int> LineStarts(string text)
    {
        var starts = new List<int> { 0 };
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i++;
                }

                starts.Add(i + 1);
            }
            else if (text[i] == '\n')
            {
                starts.Add(i + 1);
            }
        }

        return starts;
    }

    private static int LineOf(List<int> starts, int offset)
    {
        var line = 0;
        for (var i = 1; i < starts.Count && starts[i] <= offset; i++)
        {
            line = i;
        }

        return line;
    }

    private static LineEndingInfo CountEndings(string text)
    {
        int lf = 0, crlf = 0, cr = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n')
                {
                    crlf++;
                    i++;
                }
                else
                {
                    cr++;
                }
            }
            else if (text[i] == '\n')
            {
                lf++;
            }
        }

        return new LineEndingInfo(lf, crlf, cr);
    }
}
