using System.Globalization;
using Forge.App.Changes.Markdown;
using Xunit;

namespace Forge.App.Tests.Changes;

/// <summary>
/// Pins the streaming Markdown document (forge-app spec "聊天 Markdown 渲染"): a committed block is never re-parsed, nothing is
/// committed inside a fenced code block or in the middle of a list that may still grow, and a completed document renders
/// exactly like one block parse of the whole text, whatever the chunking was.
/// </summary>
public sealed class StreamingMarkdownTests
{
    /// <summary>A document that exercises every block kind, including a blank line and a table row inside a code fence.</summary>
    private const string Document = """
        # Title

        First paragraph, with **bold**, `code` and a [link](https://example.com).

        1. one
        2. two
        3. three

        | a | b |
        |---|---|
        | 1 | 2 |

        ```csharp
        var a = 1;

        var b = 2;
        ```

        > quoted
        > text

        <!-- a comment

        that runs past a blank line -->

        Closing paragraph.
        """;

    [Fact]
    public void A_completed_document_equals_one_parse_of_the_whole_text_for_any_chunking()
    {
        var expected = Canonical(MarkdownBlockParser.Parse(Split(Document)));

        foreach (var size in new[] { 1, 2, 3, 7, 16, 64, 1000 })
        {
            var document = new StreamingMarkdown();
            for (var offset = 0; offset < Document.Length; offset += size)
            {
                document.Append(Document.Substring(offset, Math.Min(size, Document.Length - offset)));
            }

            document.Complete();

            Assert.Equal(expected, Canonical(Rendered(document)));
            Assert.Equal(Document, document.Source);
        }
    }

    [Fact]
    public void Text_arrives_byte_for_byte_including_carriage_returns()
    {
        var document = new StreamingMarkdown();
        document.Append("line one\r\nli");
        document.Append("ne two\r\n");

        Assert.Equal("line one\r\nline two\r\n", document.Source);
    }

    [Fact]
    public void A_committed_block_is_never_reparsed_or_replaced()
    {
        var document = new StreamingMarkdown();
        document.Append("first paragraph\n\n");
        var committed = Assert.Single(document.Stable);

        document.Append("second paragraph, still running");
        document.Append(" and going\n");

        Assert.Same(committed, Assert.Single(document.Stable));
        Assert.Equal("second paragraph, still running and going", Assert.Single(document.Live).PlainText);
    }

    [Fact]
    public void A_blank_line_inside_a_fence_does_not_commit_the_code_block()
    {
        var document = new StreamingMarkdown();
        document.Append("```csharp\nvar a = 1;\n\nvar b = 2;\n");
        Assert.Empty(document.Stable);
        Assert.Equal(MdBlockKind.Code, Assert.Single(document.Live).Kind);

        document.Append("```\n");
        document.Complete();

        var code = Assert.Single(Rendered(document));
        Assert.Equal(MdBlockKind.Code, code.Kind);
        Assert.Equal("csharp", code.Info);
        Assert.Equal("var a = 1;\n\nvar b = 2;", code.Text);
    }

    [Fact]
    public void A_list_with_blank_lines_between_its_items_stays_one_list()
    {
        var document = new StreamingMarkdown();
        document.Append("- one\n\n");
        document.Append("- two\n\n");
        document.Append("- three\n");

        Assert.Empty(document.Stable);
        document.Complete();

        var list = Assert.Single(Rendered(document));
        Assert.Equal(MdBlockKind.List, list.Kind);
        Assert.Equal(3, list.Items.Count);
    }

    [Fact]
    public void A_paragraph_longer_than_the_fragment_limit_is_committed_in_lines_while_it_streams()
    {
        var line = new string('x', 100);
        var document = new StreamingMarkdown();
        for (var index = 0; index < 100; index++)
        {
            document.Append(line + "\n");
        }

        Assert.True(document.Stable.Count > 1, "the long paragraph should have been committed in fragments");
        Assert.All(document.Stable, block => Assert.Equal(MdBlockKind.Paragraph, block.Kind));

        document.Complete();

        var merged = Assert.Single(Rendered(document));
        Assert.Equal(MdBlockKind.Paragraph, merged.Kind);
        Assert.Equal(100, merged.EndLine - merged.StartLine + 1);
    }

    [Fact]
    public void A_rule_inside_a_document_is_not_read_as_front_matter()
    {
        var document = new StreamingMarkdown();
        document.Append("intro\n\n");
        document.Append("---\n\n");
        document.Append("after\n");
        document.Complete();

        Assert.Equal(["Paragraph", "Rule", "Paragraph"], Kinds(document));
    }

    [Fact]
    public void An_empty_document_is_empty_and_completes_to_nothing()
    {
        var document = new StreamingMarkdown();

        Assert.True(document.IsEmpty);
        document.Complete();

        Assert.Empty(Rendered(document));
        Assert.Equal(string.Empty, document.Source);
    }

    [Fact]
    public void Appending_after_completion_keeps_the_document_consistent()
    {
        var document = new StreamingMarkdown();
        document.Append("# title\n");
        document.Complete();
        document.Append("\nbody\n");
        document.Complete();

        Assert.Equal(["Heading", "Paragraph"], Kinds(document));
    }

    [Fact]
    public void SameBlock_compares_the_parse_of_a_block_not_its_object_identity()
    {
        var left = MarkdownBlockParser.Parse(["text", ""]);
        var right = MarkdownBlockParser.Parse(["text", ""], top: false);

        Assert.NotSame(left[0], right[0]);
        Assert.True(StreamingMarkdown.SameBlock(left[0], right[0]));
    }

    [Fact]
    public void SameBlock_compares_only_kind_and_the_source_line_range()
    {
        var block = new MdBlock(MdBlockKind.Code, 2, 5) { Text = "first" };

        Assert.True(StreamingMarkdown.SameBlock(block, new MdBlock(MdBlockKind.Code, 2, 5) { Text = "second", Info = "js" }));
        Assert.False(StreamingMarkdown.SameBlock(block, new MdBlock(MdBlockKind.Html, 2, 5) { Text = "first" }));
        Assert.False(StreamingMarkdown.SameBlock(block, new MdBlock(MdBlockKind.Code, 3, 5) { Text = "first" }));
        Assert.False(StreamingMarkdown.SameBlock(block, new MdBlock(MdBlockKind.Code, 2, 6) { Text = "first" }));
    }

    [Fact]
    public void Any_chunking_of_a_plain_document_equals_one_parse_of_the_whole_text()
    {
        AssertAnyChunking(PlainDocument);
    }

    [Fact]
    public void Any_chunking_of_a_front_matter_document_equals_one_parse_of_the_whole_text()
    {
        AssertAnyChunking(FrontMatterDocument);
    }

    [Fact]
    public void Indented_code_across_a_blank_line_is_one_code_block_after_completion()
    {
        var document = new StreamingMarkdown();
        document.Append("    alpha\n\n");
        Assert.Empty(document.Stable);

        document.Append("    beta\n");
        Assert.Empty(document.Stable);

        document.Complete();

        var code = Assert.Single(Rendered(document));
        Assert.Equal(MdBlockKind.Code, code.Kind);
        Assert.Equal("alpha\n\nbeta", code.Text);
        Assert.Equal((1, 3), (code.StartLine, code.EndLine));
    }

    [Fact]
    public void An_html_comment_across_a_blank_line_is_one_html_block_after_completion()
    {
        var document = new StreamingMarkdown();
        document.Append("<!-- note\n\n");
        Assert.Empty(document.Stable);

        document.Append("more -->\n");
        document.Complete();

        var html = Assert.Single(Rendered(document));
        Assert.Equal(MdBlockKind.Html, html.Kind);
        Assert.Equal("<!-- note\n\nmore -->", html.Text);
        Assert.Equal((1, 3), (html.StartLine, html.EndLine));
    }

    [Fact]
    public void A_tilde_fence_with_blank_lines_and_a_following_table_complete_correctly()
    {
        var document = new StreamingMarkdown();
        document.Append("~~~~python\nprint(1)\n\nprint(2)\n~~~~\n\n| h1 | h2 |\n|----|----|\n| a  | b  |\n");
        document.Complete();

        var rendered = Rendered(document);
        Assert.Equal([MdBlockKind.Code, MdBlockKind.Table], [.. rendered.Select(static block => block.Kind)]);
        Assert.Equal("python", rendered[0].Info);
        Assert.Equal("print(1)\n\nprint(2)", rendered[0].Text);
        Assert.Equal(2, rendered[1].Header.Count);
        Assert.Single(rendered[1].Rows);
    }

    [Fact]
    public void A_nested_list_completes_as_one_list_with_its_nesting_intact()
    {
        var document = new StreamingMarkdown();
        document.Append("- parent\n  - child one\n  - child two\n\n- second\n");
        document.Complete();

        var list = Assert.Single(Rendered(document));
        Assert.Equal(MdBlockKind.List, list.Kind);
        Assert.Equal(2, list.Items.Count);
        Assert.Equal(2, list.Items[0].Blocks.Count);
    }

    [Fact]
    public void An_ordered_list_keeps_its_numbers_across_blank_lines()
    {
        var document = new StreamingMarkdown();
        document.Append("3. three\n\n4. four\n\n5. five\n");
        document.Complete();

        var list = Assert.Single(Rendered(document));
        Assert.True(list.Ordered);
        Assert.Equal([3, 4, 5], [.. list.Items.Select(static item => item.Number!.Value)]);
    }

    [Fact]
    public void A_list_followed_by_an_unclosed_paragraph_is_committed_once_the_line_decides_it()
    {
        // The reply ends the common way: a list, a blank line, then a paragraph that is still streaming. The list must leave
        // the live region as soon as the first paragraph character arrives, otherwise every flush re-parses all of it.
        var document = new StreamingMarkdown();
        document.Append("- one\n- two\n\n");
        Assert.Empty(document.Stable);

        document.Append("final");

        var list = Assert.Single(document.Stable);
        Assert.Equal(MdBlockKind.List, list.Kind);
        var paragraph = Assert.Single(document.Live);
        Assert.Equal(MdBlockKind.Paragraph, paragraph.Kind);
        Assert.Equal("final", paragraph.PlainText);
    }

    [Fact]
    public void A_list_is_not_closed_by_a_half_written_marker()
    {
        // A list item is typed a character at a time; a lone "-" is already a marker, a lone digit is not yet one.
        var document = new StreamingMarkdown();
        document.Append("- one\n\n");
        document.Append("- ");

        Assert.Empty(document.Stable);
        Assert.Equal(MdBlockKind.List, Assert.Single(document.Live).Kind);

        document.Append("two\n");

        Assert.Empty(document.Stable);
        Assert.Equal(2, Assert.Single(document.Live).Items.Count);
    }

    [Fact]
    public void An_ordered_list_is_not_continued_by_a_number_that_is_not_a_marker()
    {
        var document = new StreamingMarkdown();
        document.Append("1. one\n2. two\n\n");
        document.Append("1984 was a year\n\n");

        Assert.Equal([MdBlockKind.List, MdBlockKind.Paragraph], [.. document.Stable.Select(static block => block.Kind)]);
    }

    [Fact]
    public void Indented_code_is_committed_once_the_following_line_is_not_indented()
    {
        var document = new StreamingMarkdown();
        document.Append("    alpha\n\n");
        Assert.Empty(document.Stable);

        document.Append("text\n");

        Assert.Equal(MdBlockKind.Code, Assert.Single(document.Stable).Kind);
        Assert.Equal(MdBlockKind.Paragraph, Assert.Single(document.Live).Kind);
    }

    [Fact]
    public void A_fenced_code_block_is_committed_even_when_indented_lines_follow()
    {
        var document = new StreamingMarkdown();
        document.Append("```csharp\nvar x = 1;\n```\n\n");

        Assert.Equal(MdBlockKind.Code, Assert.Single(document.Stable).Kind);
        Assert.Empty(document.Live);
    }

    [Fact]
    public void A_list_followed_by_a_closed_paragraph_is_committed()
    {
        var document = new StreamingMarkdown();
        document.Append("- one\n- two\n\n");
        Assert.Empty(document.Stable);

        document.Append("paragraph\n\n");

        Assert.Equal([MdBlockKind.List, MdBlockKind.Paragraph], [.. document.Stable.Select(static block => block.Kind)]);
        Assert.Empty(document.Live);
    }

    [Fact]
    public void A_paragraph_under_the_fragment_limit_is_not_split_while_streaming()
    {
        var document = new StreamingMarkdown();
        for (var index = 0; index < 40; index++)
        {
            document.Append(new string('x', 100) + "\n");
        }

        Assert.Empty(document.Stable);
        var live = Assert.Single(document.Live);
        Assert.Equal(MdBlockKind.Paragraph, live.Kind);

        document.Complete();

        var merged = Assert.Single(Rendered(document));
        Assert.Equal(MdBlockKind.Paragraph, merged.Kind);
        Assert.Equal(40, merged.EndLine - merged.StartLine + 1);
    }

    [Fact]
    public void Source_preserves_carriage_returns_tabs_and_a_missing_final_newline()
    {
        var document = new StreamingMarkdown();
        document.Append("a\tb\r\nc\r\nd");
        document.Complete();

        Assert.Equal("a\tb\r\nc\r\nd", document.Source);
        var paragraph = Assert.Single(Rendered(document));
        Assert.Equal(MdBlockKind.Paragraph, paragraph.Kind);
    }

    [Fact]
    public void An_empty_chunk_changes_nothing()
    {
        var document = new StreamingMarkdown();
        document.Append("");

        Assert.True(document.IsEmpty);
        Assert.Empty(document.Stable);
        Assert.Empty(document.Live);
        Assert.Equal(string.Empty, document.Source);

        document.Append("text");
        var stable = document.Stable.Count;
        var live = document.Live.Count;
        var source = document.Source;

        document.Append("");

        Assert.Equal(stable, document.Stable.Count);
        Assert.Equal(live, document.Live.Count);
        Assert.Equal(source, document.Source);
    }

    [Fact]
    public void Appending_a_partial_line_after_completion_stays_consistent()
    {
        var document = new StreamingMarkdown();
        document.Append("# title\n");
        document.Complete();
        document.Append("tail");
        document.Complete();

        Assert.Equal(["Heading", "Paragraph"], Kinds(document));
        Assert.Equal("tail", Rendered(document)[1].PlainText);
    }

    [Fact]
    public void Front_matter_at_the_top_is_a_yaml_code_block()
    {
        var document = new StreamingMarkdown();
        document.Append("---\ntitle: x\n---\n\nbody\n");
        document.Complete();

        var rendered = Rendered(document);
        Assert.Equal(MdBlockKind.Code, rendered[0].Kind);
        Assert.Equal("yaml", rendered[0].Info);
        Assert.Equal("title: x", rendered[0].Text);
        Assert.Equal(MdBlockKind.Paragraph, rendered[1].Kind);
    }

    /// <summary>What the chat view renders: the committed prefix followed by the live tail.</summary>
    private static List<MdBlock> Rendered(StreamingMarkdown document) => [.. document.Stable, .. document.Live];

    private static List<string> Kinds(StreamingMarkdown document) =>
        [.. Rendered(document).Select(static block => block.Kind.ToString())];

    private static string[] Split(string text)
    {
        var lines = new List<string>();
        var start = 0;
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] != '\n')
            {
                continue;
            }

            lines.Add(text[start..index].TrimEnd('\r'));
            start = index + 1;
        }

        if (start < text.Length)
        {
            lines.Add(text[start..]);
        }

        return [.. lines];
    }

    /// <summary>Feeds <paramref name="text"/> character by character, line by line, in deterministic pseudo-random chunks and as one chunk.</summary>
    private static void AssertAnyChunking(string text)
    {
        var whole = MarkdownBlockParser.Parse(Split(text));

        AssertConsistent(text, whole, Characters(text));
        AssertConsistent(text, whole, LinesWithTerminator(text));
        AssertConsistent(text, whole, [text]);

        var rng = new Random(20241113);
        for (var round = 0; round < 12; round++)
        {
            AssertConsistent(text, whole, RandomChunks(text, rng));
        }
    }

    private static void AssertConsistent(string text, IReadOnlyList<MdBlock> whole, IEnumerable<string> chunks)
    {
        var document = new StreamingMarkdown();
        foreach (var chunk in chunks)
        {
            document.Append(chunk);
        }

        document.Complete();

        Assert.Equal(Canonical(whole), Canonical([.. document.Stable, .. document.Live]));
        Assert.Equal(text, document.Source);
    }

    private static IEnumerable<string> Characters(string text)
    {
        for (var index = 0; index < text.Length; index++)
        {
            yield return text[index..(index + 1)];
        }
    }

    private static IEnumerable<string> LinesWithTerminator(string text)
    {
        var start = 0;
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] == '\n')
            {
                yield return text[start..(index + 1)];
                start = index + 1;
            }
        }

        if (start < text.Length)
        {
            yield return text[start..];
        }
    }

    private static IEnumerable<string> RandomChunks(string text, Random rng)
    {
        var offset = 0;
        while (offset < text.Length)
        {
            var size = rng.Next(1, 65);
            var end = Math.Min(offset + size, text.Length);
            yield return text[offset..end];
            offset = end;
        }
    }

    /// <summary>A complete observable snapshot of a parse: kind, line range, level, info, text, inlines, list items, quote children and table cells.</summary>
    private static List<string> Canonical(IEnumerable<MdBlock> blocks) => [.. blocks.Select(Canonical)];

    private static string Canonical(MdBlock block) =>
        string.Join('\u0002',
            block.Kind.ToString(),
            block.StartLine.ToString(CultureInfo.InvariantCulture),
            block.EndLine.ToString(CultureInfo.InvariantCulture),
            block.Level.ToString(CultureInfo.InvariantCulture),
            block.Info,
            block.Ordered.ToString(),
            block.Text,
            block.PlainText,
            CanonicalInlines(block.Inlines),
            CanonicalItems(block.Items),
            string.Join(';', Canonical(block.Children)),
            string.Join(',', block.Aligns),
            CanonicalHeader(block.Header),
            CanonicalRows(block.Rows));

    private static string CanonicalInlines(IEnumerable<MdInline> inlines) => string.Join('|', inlines.Select(CanonicalInline));

    private static string CanonicalInline(MdInline inline) =>
        $"{inline.Kind}({inline.Text})({inline.Url})[{string.Join('|', (inline.Children ?? []).Select(CanonicalInline))}]";

    private static string CanonicalItems(IEnumerable<MdListItem> items) =>
        string.Join(';', items.Select(static item => $"{item.Number}/{item.Checked}/{item.StartLine}/{item.EndLine}[{string.Join(';', item.Blocks.Select(Canonical))}]"));

    private static string CanonicalHeader(IEnumerable<IEnumerable<MdInline>> rows) =>
        string.Join(';', rows.Select(CanonicalInlines));

    private static string CanonicalRows(IEnumerable<IEnumerable<IEnumerable<MdInline>>> rows) =>
        string.Join(';', rows.Select(static row => string.Join('~', row.Select(CanonicalInlines))));

    private const string PlainDocument = """
        # Heading

        Intro with **bold**, *italic*, `code`, a [link](https://example.com) and ~~strike~~.

        - [x] done task
        - [ ] open task
        - plain
          - child

        1. first
        2. second

        | name | value |
        |------|-------|
        | a    | 1     |

        > quoted
        >
        > > nested

        ```csharp
        int a = 1;

        int b = 2;
        ```

        ~~~~python
        print("x")

        print("y")
        ~~~~

        <!-- a comment

        that spans a blank line -->

            indented code one

            indented code two

        ***

        Closing paragraph without a trailing newline
        """;

    private const string FrontMatterDocument = """
        ---
        title: demo
        tags: [a, b]
        ---

        # After front matter

        body text
        """;
}
