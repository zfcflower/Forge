using Forge.Editor.Highlighting;
using Forge.Editor.Text;
using Forge.Editor.TextMate;

namespace Forge.Editor.Tests.Highlighting;

public class HighlighterTests
{
    private static readonly string[] Snippets =
    [
        "int a = 1;",
        "/* open",
        "close */",
        "// note",
        "var s = \"str",
        "end\";",
        "class C {",
        "}",
        "    void M() { }",
        "@\"verbatim",
        "\"",
        string.Empty,
        "x = y;",
        "#region R",
        "#endregion",
        "[Attr]",
        "string t = $\"{a}\";",
        "/* one */ int b;",
    ];

    [Fact]
    public void Csharp_tokens_get_lexical_categories()
    {
        var tokenizer = new Tokenizer(new GrammarRegistry());

        var result = tokenizer.HighlightText("// note\nclass Widget\n{\n    int count = 42;\n    string s = \"q\";\n}", "csharp");

        Assert.Equal("source.cs", result.ScopeName);
        Assert.Equal(6, result.LineCount);
        Assert.Equal(TokenCategory.Comment, CategoryAt(result, 0, 0));
        Assert.Equal(TokenCategory.Keyword, CategoryAt(result, 1, 0));
        Assert.Equal(TokenCategory.Type, CategoryAt(result, 1, 6));
        Assert.Equal(TokenCategory.Keyword, CategoryAt(result, 3, 4));
        Assert.Equal(TokenCategory.Number, CategoryAt(result, 3, 16));
        Assert.Equal(TokenCategory.String, CategoryAt(result, 4, 16));
        Assert.Equal(TokenCategory.Punctuation, CategoryAt(result, 2, 0));
    }

    [Fact]
    public void Scopes_name_the_grammar_rules_that_produced_each_token()
    {
        var result = new Tokenizer(new GrammarRegistry()).HighlightText("int a;", "csharp");

        var first = result.GetTokens(0).Span[0];

        Assert.Contains("keyword.type.int.cs", ScopeOf(result, first));
        Assert.Contains("source.cs", ScopeOf(result, first));
    }

    [Fact]
    public void Markdown_fenced_csharp_is_highlighted_as_csharp()
    {
        var result = new Tokenizer(new GrammarRegistry()).HighlightText("# Title\n\n```csharp\nclass B {}\n```\n", "markdown");

        Assert.Equal("text.html.markdown", result.ScopeName);
        Assert.Equal(TokenCategory.Keyword, CategoryAt(result, 3, 0));
        Assert.Contains("meta.embedded.block.csharp", ScopeOf(result, result.GetTokens(3).Span[0]));
        Assert.Equal(TokenCategory.Type, CategoryAt(result, 3, 6));
        Assert.Contains("markup.fenced_code.block.markdown", ScopeOf(result, result.GetTokens(3).Span[0]));
    }

    [Fact]
    public void Json_values_and_keys_get_their_categories()
    {
        var result = new Tokenizer(new GrammarRegistry()).HighlightText("{\"name\": [1, true, \"s\"]}", "json");

        Assert.Equal(TokenCategory.String, CategoryAt(result, 0, 1));
        Assert.Equal(TokenCategory.Number, CategoryAt(result, 0, 10));
        Assert.Equal(TokenCategory.Constant, CategoryAt(result, 0, 13));
        Assert.Equal(TokenCategory.String, CategoryAt(result, 0, 20));
    }

    [Fact]
    public void Tokens_tile_every_line_without_gaps_or_overlaps()
    {
        var text = string.Join("\n", Snippets);
        var result = new Tokenizer(new GrammarRegistry()).HighlightText(text, "csharp");
        var lines = new StringHighlightSource(text);

        for (var line = 0; line < result.LineCount; line++)
        {
            var tokens = result.GetTokens(line).Span;
            var length = lines.GetLine(line).Length;
            var position = 0;
            foreach (var token in tokens)
            {
                Assert.Equal(position, token.Start);
                Assert.True(token.Length > 0);
                position = token.End;
            }

            Assert.Equal(length, position);
        }
    }

    [Fact]
    public void Continuation_flag_marks_lines_that_start_inside_a_block_comment_or_class_body()
    {
        var tokenizer = new Tokenizer(new GrammarRegistry());

        var comment = tokenizer.HighlightText("/* a\nb */\nint c;", "csharp");
        var body = tokenizer.HighlightText("class A {\n  int b;\n}\nint c;", "csharp");

        Assert.False(comment.IsContinuation(0));
        Assert.True(comment.IsContinuation(1));
        Assert.False(comment.IsContinuation(2));
        Assert.False(body.IsContinuation(0));
        Assert.True(body.IsContinuation(1));
        Assert.True(body.IsContinuation(2));
        Assert.False(body.IsContinuation(3));
    }

    [Fact]
    public void Unterminated_verbatim_string_continues_onto_the_next_line()
    {
        var result = new Tokenizer(new GrammarRegistry()).HighlightText("var s = @\"a\nb\";\nint c;", "csharp");

        Assert.True(result.IsContinuation(1));
        Assert.False(result.IsContinuation(2));
    }

    [Fact]
    public void Line_after_a_line_comment_is_not_a_continuation()
    {
        var result = new Tokenizer(new GrammarRegistry()).HighlightText("// a\nint b;", "csharp");

        Assert.False(result.IsContinuation(1));
    }

    [Fact]
    public void Adjacent_line_comment_is_not_a_continuation()
    {
        var result = new Tokenizer(new GrammarRegistry()).HighlightText("// a\n// b\nint c;", "csharp");

        Assert.False(result.IsContinuation(1));
    }

    [Fact]
    public void Results_are_versioned_and_publish_immutable_line_data()
    {
        var tokenizer = new Tokenizer(new GrammarRegistry());

        var first = tokenizer.HighlightText("int a;", "csharp");
        var second = tokenizer.HighlightText("int a;\nint b;", "csharp");

        Assert.True(second.Version > first.Version);
        Assert.Equal(1, first.LineCount);
        Assert.Equal(2, second.LineCount);
        Assert.Throws<ArgumentOutOfRangeException>(() => first.GetTokens(1));
        Assert.Throws<ArgumentOutOfRangeException>(() => first.GetTokens(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => first.IsContinuation(3));
    }

    [Fact]
    public void Unknown_or_blank_language_ids_are_rejected()
    {
        var tokenizer = new Tokenizer(new GrammarRegistry());

        Assert.Throws<KeyNotFoundException>(() => tokenizer.HighlightText("x", "cobol"));
        Assert.Throws<ArgumentException>(() => tokenizer.HighlightText("x", " "));
        Assert.Throws<ArgumentNullException>(() => new Tokenizer(null!));
    }

    [Fact]
    public void Missing_grammars_of_an_embedded_language_are_empty()
    {
        var tokenizer = new Tokenizer(new GrammarRegistry());

        Assert.Empty(tokenizer.MissingGrammars("csharp"));
        Assert.Empty(tokenizer.Diagnostics);
    }

    [Fact]
    public void Highlighting_an_empty_document_yields_one_empty_line()
    {
        var result = new Tokenizer(new GrammarRegistry()).HighlightText(string.Empty, "csharp");

        Assert.Equal(1, result.LineCount);
        Assert.True(result.GetTokens(0).IsEmpty);
    }

    [Fact]
    public void Lf_and_crlf_split_lines_without_their_terminators()
    {
        Assert.Equal(new[] { "a", "b", string.Empty }, StringHighlightSource.SplitLines("a\r\nb\n"));
    }

    [Fact]
    public void Lone_cr_splits_lines_like_a_snapshot_does()
    {
        Assert.Equal(new[] { "a", "b" }, StringHighlightSource.SplitLines("a\rb"));
        Assert.Equal(TextSnapshot.FromText("a\rb").LineCount, StringHighlightSource.SplitLines("a\rb").Length);
    }

    [Fact]
    public void Cancelled_highlight_throws_before_any_work()
    {
        var tokenizer = new Tokenizer(new GrammarRegistry());
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() => tokenizer.HighlightText("int a;", "csharp", cts.Token));
    }

    [Fact]
    public void Cancellation_requested_mid_document_stops_the_pass()
    {
        using var cts = new CancellationTokenSource();
        var source = new ListSource(Enumerable.Repeat("int a = 1;", 2000), onRead: index =>
        {
            if (index == 100)
            {
                cts.Cancel();
            }
        });

        Assert.Throws<OperationCanceledException>(() => new Tokenizer(new GrammarRegistry()).Highlight(source, "csharp", cts.Token));
    }

    [Fact]
    public void Cancelled_incremental_update_keeps_the_previous_result()
    {
        var source = new ListSource(["int a;", "int b;"]);
        var incremental = new IncrementalHighlighter(new Tokenizer(new GrammarRegistry()), source, "csharp");
        var previous = incremental.Current;
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        source.Replace(0, "int c;");

        Assert.Throws<OperationCanceledException>(() => incremental.Update(LineChange.Replace(0), cts.Token));
        Assert.Same(previous, incremental.Current);
    }

    [Fact]
    public void Incremental_update_after_a_single_line_replace_equals_a_full_pass()
    {
        var source = new ListSource(["class A", "{", "/* start", "still comment", "*/", "}"]);
        var tokenizer = new Tokenizer(new GrammarRegistry());
        var incremental = new IncrementalHighlighter(tokenizer, source, "csharp");

        source.Replace(3, "no longer a comment");
        var updated = incremental.Update(LineChange.Replace(3));

        AssertSameTokens(tokenizer.Highlight(source, "csharp"), updated);
    }

    [Fact]
    public void Removing_the_comment_closer_keeps_the_rest_of_the_file_in_comment_state()
    {
        var source = new ListSource(["int a;", "/* start", "int b;", "end */", "int c;"]);
        var tokenizer = new Tokenizer(new GrammarRegistry());
        var incremental = new IncrementalHighlighter(tokenizer, source, "csharp");
        Assert.Equal(TokenCategory.Keyword, CategoryAt(incremental.Current, 4, 0));

        source.Replace(3, "end");
        var opened = incremental.Update(LineChange.Replace(3));

        AssertSameTokens(tokenizer.Highlight(source, "csharp"), opened);
        Assert.Equal(TokenCategory.Comment, CategoryAt(opened, 4, 0));

        source.Replace(3, "end */");
        var closed = incremental.Update(LineChange.Replace(3));

        Assert.Equal(TokenCategory.Keyword, CategoryAt(closed, 4, 0));
        AssertSameTokens(tokenizer.Highlight(source, "csharp"), closed);
    }

    [Fact]
    public void Inserting_and_removing_lines_match_a_full_pass_over_random_edits()
    {
        var random = new Random(20240611);
        var source = new ListSource(Enumerable.Range(0, 12).Select(_ => Snippets[random.Next(Snippets.Length)]));
        var tokenizer = new Tokenizer(new GrammarRegistry());
        var incremental = new IncrementalHighlighter(tokenizer, source, "csharp");

        for (var step = 0; step < 150; step++)
        {
            LineChange change;
            switch (random.Next(4))
            {
                case 0:
                {
                    var line = random.Next(source.LineCount);
                    source.Replace(line, Snippets[random.Next(Snippets.Length)]);
                    change = LineChange.Replace(line);
                    break;
                }
                case 1:
                {
                    var line = random.Next(source.LineCount + 1);
                    source.Insert(line, Snippets[random.Next(Snippets.Length)]);
                    change = LineChange.Insert(line, 1);
                    break;
                }
                case 2:
                {
                    if (source.LineCount < 2)
                    {
                        continue;
                    }

                    var line = random.Next(source.LineCount);
                    source.Remove(line);
                    change = LineChange.Remove(line, 1);
                    break;
                }
                default:
                {
                    var line = random.Next(source.LineCount);
                    var removed = source.LineCount > 2 ? Math.Min(2, source.LineCount - line) : 0;
                    source.RemoveRange(line, removed);
                    var inserted = random.Next(0, 4);
                    for (var i = 0; i < inserted; i++)
                    {
                        source.Insert(line + i, Snippets[random.Next(Snippets.Length)]);
                    }

                    change = new LineChange(line, removed, inserted);
                    break;
                }
            }

            var updated = incremental.Update(change);

            AssertSameTokens(tokenizer.Highlight(source, "csharp"), updated);
        }
    }

    [Fact]
    public void Incremental_highlighter_can_rebuild_everything_and_retheme_without_retokenizing()
    {
        var source = new ListSource(["int a;", "// c"]);
        var tokenizer = new Tokenizer(new GrammarRegistry());
        var incremental = new IncrementalHighlighter(tokenizer, source, "csharp");
        var before = incremental.Current.Version;

        var rebuilt = incremental.HighlightAll();
        Assert.True(rebuilt.Version > before);
        AssertSameTokens(rebuilt, incremental.Current);

        var theme = new ThemeMapper().AddFirst(TokenCategory.Macro, "keyword");
        var rethemed = incremental.Retheme(theme);

        Assert.Equal(rebuilt.LineCount, rethemed.LineCount);
        Assert.Equal(TokenCategory.Macro, CategoryAt(rethemed, 0, 0));
        Assert.Equal(TokenCategory.Comment, CategoryAt(rethemed, 1, 0));
        Assert.Equal(TokenCategory.Keyword, CategoryAt(rebuilt, 0, 0));
        Assert.Throws<ArgumentNullException>(() => incremental.Retheme(null!));
    }

    [Fact]
    public void Theme_foreground_colours_are_attached_to_tokens_by_category()
    {
        var theme = new ThemeMapper().SetForeground(TokenCategory.Comment, 7).SetForeground(TokenCategory.Keyword, 3);
        var result = new Tokenizer(new GrammarRegistry(), theme).HighlightText("// c\nint a;", "csharp");

        var comment = result.GetTokens(0).Span[0];
        var keyword = result.GetTokens(1).Span[0];
        Assert.Equal(7, result.GetForeground(comment.ScopeId));
        Assert.Equal(3, result.GetForeground(keyword.ScopeId));
        Assert.Equal(0, result.GetForeground(-1));
        Assert.Equal(TokenCategory.None, result.GetCategory(int.MaxValue));
    }

    [Fact]
    public void Changing_the_tokenizer_theme_resolves_new_categories_on_the_next_pass()
    {
        var tokenizer = new Tokenizer(new GrammarRegistry());
        var first = tokenizer.HighlightText("int a;", "csharp");
        Assert.Equal(TokenCategory.Keyword, CategoryAt(first, 0, 0));

        tokenizer.Theme = new ThemeMapper().AddFirst(TokenCategory.Operator, "keyword");
        var second = tokenizer.HighlightText("int a;", "csharp");

        Assert.Equal(TokenCategory.Operator, CategoryAt(second, 0, 0));
        Assert.Equal(TokenCategory.Keyword, CategoryAt(first, 0, 0));
        Assert.Throws<ArgumentNullException>(() => tokenizer.Theme = null!);
    }

    [Fact]
    public void Tokenizer_options_with_a_line_budget_still_produce_complete_results_for_normal_lines()
    {
        var options = new TokenizerOptions { LineTimeBudget = TimeSpan.FromSeconds(5) };
        var result = new Tokenizer(new GrammarRegistry(), options: options).HighlightText("int a = 1;", "csharp");

        Assert.Equal(TokenCategory.Keyword, CategoryAt(result, 0, 0));
        Assert.Equal(TokenCategory.Number, CategoryAt(result, 0, 8));
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

    private static string ScopeOf(HighlightResult result, HighlightToken token) =>
        string.Join(' ', result.Scopes.GetScopes(token.ScopeId).ToArray());

    private static void AssertSameTokens(HighlightResult expected, HighlightResult actual)
    {
        Assert.Equal(expected.LineCount, actual.LineCount);
        for (var line = 0; line < expected.LineCount; line++)
        {
            var left = expected.GetTokens(line).Span.ToArray().Select(t => (t.Start, t.Length, ScopeOf(expected, t))).ToArray();
            var right = actual.GetTokens(line).Span.ToArray().Select(t => (t.Start, t.Length, ScopeOf(actual, t))).ToArray();
            Assert.True(left.SequenceEqual(right), $"line {line}: expected {string.Join(" | ", left)} but was {string.Join(" | ", right)}");
            Assert.Equal(expected.IsContinuation(line), actual.IsContinuation(line));
        }
    }

    private sealed class ListSource : IHighlightSource
    {
        private readonly List<string> _lines;
        private readonly Action<int>? _onRead;

        public ListSource(IEnumerable<string> lines, Action<int>? onRead = null)
        {
            _lines = [.. lines];
            _onRead = onRead;
        }

        public int LineCount => _lines.Count;

        public ReadOnlySpan<char> GetLine(int index)
        {
            _onRead?.Invoke(index);
            return _lines[index];
        }

        public void Replace(int line, string text) => _lines[line] = text;

        public void Insert(int line, string text) => _lines.Insert(line, text);

        public void Remove(int line) => _lines.RemoveAt(line);

        public void RemoveRange(int line, int count) => _lines.RemoveRange(line, count);
    }
}
