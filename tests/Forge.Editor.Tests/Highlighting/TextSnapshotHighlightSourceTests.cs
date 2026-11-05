using Forge.Editor.Highlighting;
using Forge.Editor.Text;
using Forge.Editor.TextMate;

namespace Forge.Editor.Tests.Highlighting;

public class TextSnapshotHighlightSourceTests
{
    public static TheoryData<string> LineEndingTexts => new()
    {
        "int a = 1;",
        "int a = 1;\nint b = 2;",
        "int a = 1;\r\nint b = 2;",
        "int a = 1;\rint b = 2;",
        "int a = 1;\nint b = 2;\r\nint c = 3;\rint d = 4;",
        string.Empty,
    };

    [Theory]
    [MemberData(nameof(LineEndingTexts))]
    public void Snapshot_source_matches_string_source_token_for_token(string text)
    {
        var tokenizer = new Tokenizer(new GrammarRegistry());
        var expected = tokenizer.HighlightText(text, "csharp");

        var actual = tokenizer.Highlight(TextSnapshot.FromText(text), "csharp");

        AssertSameTokens(expected, actual);
    }

    [Fact]
    public void Snapshot_source_matches_string_source_for_a_line_longer_than_one_rope_leaf()
    {
        var longLine = string.Concat(Enumerable.Repeat("int a = 1; // ", 200));
        var tokenizer = new Tokenizer(new GrammarRegistry());
        var expected = tokenizer.HighlightText(longLine, "csharp");

        var actual = tokenizer.Highlight(TextSnapshot.FromText(longLine), "csharp");

        Assert.True(longLine.Length > Rope.MaxLeafLength);
        AssertSameTokens(expected, actual);
    }

    [Fact]
    public void Reading_lines_allocates_nothing_after_warmup()
    {
        var text = string.Join("\n", Enumerable.Range(0, 64).Select(i => $"int x{i} = {i}; // line {i}"));
        var source = new TextSnapshotHighlightSource(TextSnapshot.FromText(text));

        for (var line = 0; line < source.LineCount; line++)
        {
            _ = source.GetLine(line).Length;
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        long total = 0;
        for (var iteration = 0; iteration < 100; iteration++)
        {
            for (var line = 0; line < source.LineCount; line++)
            {
                total += source.GetLine(line).Length;
            }
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(total > 0);
        Assert.Equal(0, allocated);
    }

    [Fact]
    public void Highlighting_a_new_snapshot_version_uses_the_edited_text()
    {
        var document = new TextDocument("int a = 1;\nint b = 2;");
        var tokenizer = new Tokenizer(new GrammarRegistry());
        var before = document.Snapshot;

        _ = tokenizer.Highlight(before, "csharp");
        var edited = document.ApplyChange(TextChange.Insert(0, "// header\n"));
        var second = tokenizer.Highlight(edited, "csharp");

        Assert.NotSame(before, edited);
        Assert.Same(edited, document.Snapshot);
        Assert.True(edited.Version > before.Version);
        Assert.Equal(3, second.LineCount);
        Assert.Equal(TokenCategory.Comment, CategoryAt(second, 0, 0));
        Assert.Equal(TokenCategory.Keyword, CategoryAt(second, 1, 0));
        AssertSameTokens(tokenizer.HighlightText(edited.GetText(), "csharp"), second);
    }

    private static TokenCategory CategoryAt(HighlightResult result, int line, int column)
    {
        foreach (var token in result.RenderLine(line))
        {
            if (column >= token.Start && column < token.End)
            {
                return token.Category;
            }
        }

        return TokenCategory.None;
    }

    private static void AssertSameTokens(HighlightResult expected, HighlightResult actual)
    {
        Assert.Equal(expected.LineCount, actual.LineCount);
        for (var line = 0; line < expected.LineCount; line++)
        {
            var left = expected.GetTokens(line).Span.ToArray().Select(t => (t.Start, t.Length, Scopes: ScopeOf(expected, t))).ToArray();
            var right = actual.GetTokens(line).Span.ToArray().Select(t => (t.Start, t.Length, Scopes: ScopeOf(actual, t))).ToArray();
            Assert.True(left.SequenceEqual(right), $"line {line}: expected {string.Join(" | ", left)} but was {string.Join(" | ", right)}");
            Assert.Equal(expected.IsContinuation(line), actual.IsContinuation(line));
        }
    }

    private static string ScopeOf(HighlightResult result, HighlightToken token) =>
        string.Join(' ', result.Scopes.GetScopes(token.ScopeId).ToArray());
}
