using Forge.Editor.Highlighting;
using Forge.Editor.Text;
using Forge.Editor.TextMate;

namespace Forge.Editor.Tests.TextMate;

public class TokenizeLineAllocationTests
{
    private const string CaptureFreeGrammar = """
        {"scopeName":"source.alloc","patterns":[{"match":"foo","name":"kw.foo"},{"match":"[0-9]+","name":"num"}]}
        """;

    private const string EndOfLineGrammar = """
        {"scopeName":"source.eoltest",
         "patterns":[
           {"match":"foo$","name":"foo.eol"},
           {"match":"\\d+$","name":"number.eol"},
           {"match":"\\z","name":"never.z"},
           {"begin":"quote:","end":"$","name":"quoted.eol","patterns":[{"match":"\\w+","name":"word.inside"}]}
         ]}
        """;

    [Fact]
    public void Capture_free_lines_tokenize_without_a_per_line_string()
    {
        var registry = new GrammarRegistry(includeEmbeddedGrammars: false);
        Assert.True(registry.AddGrammarText(CaptureFreeGrammar, languageId: "alloc"));
        var tokenizer = new Tokenizer(registry);

        const int lineCount = 4000;
        const int extraChars = 1000;

        static string[] MakeLines(int pad)
        {
            var lines = new string[lineCount];
            for (var i = 0; i < lines.Length; i++)
            {
                lines[i] = "foo" + new string('x', pad) + i;
            }

            return lines;
        }

        var shortText = string.Join('\n', MakeLines(10));
        var longText = string.Join('\n', MakeLines(10 + extraChars));

        // Warm up the tokenizer's buffers and interned scopes, then measure the steady-state cost of two documents
        // that differ only in line length, never in token count.
        var warmShort = new TextSnapshotHighlightSource(TextSnapshot.FromText(shortText));
        var warmLong = new TextSnapshotHighlightSource(TextSnapshot.FromText(longText));
        for (var i = 0; i < 3; i++)
        {
            tokenizer.Highlight(warmShort, "alloc");
            tokenizer.Highlight(warmLong, "alloc");
        }

        var shortSource = new TextSnapshotHighlightSource(TextSnapshot.FromText(shortText));
        var longSource = new TextSnapshotHighlightSource(TextSnapshot.FromText(longText));

        long shortAlloc = long.MaxValue;
        long longAlloc = long.MaxValue;
        for (var i = 0; i < 4; i++)
        {
            shortAlloc = Math.Min(shortAlloc, Measure(tokenizer, shortSource));
            longAlloc = Math.Min(longAlloc, Measure(tokenizer, longSource));
        }

        // The removed string.Concat(line, "\n") cost 2 bytes per extra character on every line; the span scanner has
        // no per-character allocation left, so the longer document must not cost meaningfully more.
        Assert.True(Math.Abs(longAlloc - shortAlloc) < 4096, $"long {longAlloc} vs short {shortAlloc}");
    }

    [Fact]
    public void End_of_line_anchors_tokenize_identically_to_the_string_path()
    {
        var registry = new GrammarRegistry(includeEmbeddedGrammars: false);
        Assert.True(registry.AddGrammarText(EndOfLineGrammar, languageId: "eoltest"));
        var tokenizer = new Tokenizer(registry);

        var result = tokenizer.HighlightText("foo\nbar foo\nquote: hello world\n123\nquote: a\n\n", "eoltest");

        AssertTokens(result, 0, [("source.eoltest foo.eol", 0, 3)]);
        AssertTokens(result, 1, [("source.eoltest", 0, 4), ("source.eoltest foo.eol", 4, 3)]);
        AssertTokens(result, 2,
        [
            ("source.eoltest quoted.eol", 0, 6),
            ("source.eoltest quoted.eol", 6, 1),
            ("source.eoltest quoted.eol word.inside", 7, 5),
            ("source.eoltest quoted.eol", 12, 1),
            ("source.eoltest quoted.eol word.inside", 13, 5),
        ]);
        AssertTokens(result, 3, [("source.eoltest number.eol", 0, 3)]);
        AssertTokens(result, 4,
        [
            ("source.eoltest quoted.eol", 0, 6),
            ("source.eoltest quoted.eol", 6, 1),
            ("source.eoltest quoted.eol word.inside", 7, 1),
        ]);
        Assert.Empty(result.GetTokens(5).Span.ToArray());
        Assert.Empty(result.GetTokens(6).Span.ToArray());
    }

    private static long Measure(Tokenizer tokenizer, IHighlightSource source)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var before = GC.GetAllocatedBytesForCurrentThread();
        tokenizer.Highlight(source, "alloc");
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    private static void AssertTokens(HighlightResult result, int line, (string Scopes, int Start, int Length)[] expected)
    {
        var tokens = result.GetTokens(line).Span.ToArray();
        Assert.Equal(expected.Length, tokens.Length);
        for (var i = 0; i < expected.Length; i++)
        {
            var token = tokens[i];
            Assert.Equal(expected[i].Start, token.Start);
            Assert.Equal(expected[i].Length, token.Length);
            Assert.Equal(expected[i].Scopes, ScopeOf(result, token));
        }
    }

    private static string ScopeOf(HighlightResult result, HighlightToken token)
        => string.Join(' ', result.Scopes.GetScopes(token.ScopeId).ToArray());
}
