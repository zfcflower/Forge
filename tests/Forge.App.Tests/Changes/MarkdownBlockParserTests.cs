using Forge.App.Changes.Markdown;
using Xunit;

namespace Forge.App.Tests.Changes;

public sealed class MarkdownBlockParserTests
{
    private static List<MdBlock> Parse(string text, int first = 1) =>
        MarkdownBlockParser.Parse(text.Split('\n'), first);

    private static List<MdInline> Inline(string text) => MarkdownBlockParser.ParseInlines(text);

    [Fact]
    public void Atx_headings_have_levels_and_trailing_hashes_are_removed()
    {
        var blocks = Parse("# One\n###### Six ##\n####### seven\n#nospace");
        Assert.Equal(MdBlockKind.Heading, blocks[0].Kind);
        Assert.Equal(1, blocks[0].Level);
        Assert.Equal("One", blocks[0].PlainText);
        Assert.Equal(6, blocks[1].Level);
        Assert.Equal("Six", blocks[1].PlainText);
        // 7 hashes and a missing space are not headings.
        Assert.Single(blocks.Skip(2));
        Assert.All(blocks.Skip(2), b => Assert.Equal(MdBlockKind.Paragraph, b.Kind));
    }

    [Fact]
    public void Setext_headings_cover_both_lines()
    {
        var blocks = Parse("Title\n=====\n\nSub\n---\n\npara");
        Assert.Equal(3, blocks.Count);
        Assert.Equal((MdBlockKind.Heading, 1, 1, 2), (blocks[0].Kind, blocks[0].Level, blocks[0].StartLine, blocks[0].EndLine));
        Assert.Equal((MdBlockKind.Heading, 2, 4, 5), (blocks[1].Kind, blocks[1].Level, blocks[1].StartLine, blocks[1].EndLine));
        Assert.Equal(MdBlockKind.Paragraph, blocks[2].Kind);
    }

    [Fact]
    public void Paragraph_lines_join_and_report_source_range_with_offset()
    {
        var blocks = Parse("first\nsecond\n\nthird", first: 10);
        Assert.Equal(2, blocks.Count);
        Assert.Equal((10, 11), (blocks[0].StartLine, blocks[0].EndLine));
        Assert.Equal((13, 13), (blocks[1].StartLine, blocks[1].EndLine));
        Assert.Equal("first second", blocks[0].PlainText);
    }

    [Fact]
    public void Fenced_code_keeps_markdown_like_text_literal_and_both_fence_kinds_work()
    {
        var blocks = Parse("```csharp\n# not heading\n- not list\n```\n~~~\nplain\n~~~~\nafter");
        Assert.Equal(3, blocks.Count);
        Assert.Equal(MdBlockKind.Code, blocks[0].Kind);
        Assert.Equal("csharp", blocks[0].Info);
        Assert.Equal("# not heading\n- not list", blocks[0].Text);
        Assert.Equal((1, 4), (blocks[0].StartLine, blocks[0].EndLine));
        Assert.Equal("plain", blocks[1].Text);
        Assert.Equal((5, 7), (blocks[1].StartLine, blocks[1].EndLine));
        Assert.Equal(MdBlockKind.Paragraph, blocks[2].Kind);
    }

    [Fact]
    public void Unclosed_fence_runs_to_the_end_and_a_shorter_inner_fence_does_not_close_it()
    {
        var blocks = Parse("````\n```\ninner\n```\nstill code");
        Assert.Single(blocks);
        Assert.Equal("```\ninner\n```\nstill code", blocks[0].Text);
        Assert.Equal(5, blocks[0].EndLine);
    }

    [Fact]
    public void Indented_code_is_a_code_block_but_does_not_interrupt_a_paragraph()
    {
        var blocks = Parse("    code line\n\npara\n    continued");
        Assert.Equal(MdBlockKind.Code, blocks[0].Kind);
        Assert.Equal("code line", blocks[0].Text);
        Assert.Equal(MdBlockKind.Paragraph, blocks[1].Kind);
        Assert.Equal("para continued", blocks[1].PlainText);
    }

    [Fact]
    public void Front_matter_becomes_a_yaml_code_block_only_at_the_top()
    {
        var blocks = Parse("---\ntitle: x\n---\n# H");
        Assert.Equal(MdBlockKind.Code, blocks[0].Kind);
        Assert.Equal("yaml", blocks[0].Info);
        Assert.Equal("title: x", blocks[0].Text);
        Assert.Equal((1, 3), (blocks[0].StartLine, blocks[0].EndLine));
        Assert.Equal(MdBlockKind.Heading, blocks[1].Kind);

        var later = Parse("para\n\n---\nnot: front\n---");
        Assert.DoesNotContain(later, b => b.Info == "yaml");
        Assert.Equal(MdBlockKind.Rule, later[1].Kind);
    }

    [Fact]
    public void Thematic_breaks_accept_all_three_markers_and_spaces()
    {
        var blocks = Parse("***\n\n- - -\n\n___\n\n--");
        Assert.Equal(MdBlockKind.Rule, blocks[0].Kind);
        Assert.Equal(MdBlockKind.Rule, blocks[1].Kind);
        Assert.Equal(MdBlockKind.Rule, blocks[2].Kind);
        Assert.NotEqual(MdBlockKind.Rule, blocks[3].Kind);
    }

    [Fact]
    public void Bullet_and_ordered_lists_have_items_numbers_and_ranges()
    {
        var blocks = Parse("- a\n- b\n\n3. x\n4. y");
        Assert.Equal(2, blocks.Count);
        Assert.False(blocks[0].Ordered);
        Assert.Equal(2, blocks[0].Items.Count);
        Assert.Equal((1, 2), (blocks[0].StartLine, blocks[0].EndLine));
        Assert.True(blocks[1].Ordered);
        Assert.Equal([3, 4], blocks[1].Items.Select(i => i.Number!.Value));
        Assert.Equal((4, 5), (blocks[1].StartLine, blocks[1].EndLine));
    }

    [Fact]
    public void Nested_lists_are_children_of_the_parent_item_with_their_own_ranges()
    {
        var blocks = Parse("- parent\n  - child 1\n  - child 2\n    - grandchild\n- sibling");
        var list = Assert.Single(blocks.Where(b => b.Kind == MdBlockKind.List).Take(1));
        Assert.Equal(2, list.Items.Count);
        var parent = list.Items[0];
        Assert.Equal(2, parent.Blocks.Count);
        var child = parent.Blocks[1];
        Assert.Equal(MdBlockKind.List, child.Kind);
        Assert.Equal(2, child.Items.Count);
        Assert.Equal((2, 4), (child.StartLine, child.EndLine));
        Assert.Equal(MdBlockKind.List, child.Items[1].Blocks[1].Kind);
        Assert.Equal((4, 4), (child.Items[1].Blocks[1].StartLine, child.Items[1].Blocks[1].EndLine));
        Assert.Equal("sibling", MarkdownBlockParser.PlainText(list.Items[1].Blocks[0].Inlines));
        Assert.Equal((1, 5), (list.StartLine, list.EndLine));
    }

    [Fact]
    public void Task_items_carry_their_checked_state_and_strip_the_box()
    {
        var list = Parse("- [ ] open\n- [x] done\n- [X] done2\n- [y] not a task")[0];
        Assert.Equal([false, true, true], list.Items.Take(3).Select(i => i.Checked!.Value));
        Assert.Null(list.Items[3].Checked);
        Assert.Equal("open", MarkdownBlockParser.PlainText(list.Items[0].Blocks[0].Inlines));
    }

    [Fact]
    public void List_item_can_contain_a_fenced_block_and_a_lazy_continuation()
    {
        var list = Parse("1. step\n   ```\n   - x\n   ```\n2. next\ncontinued lazily")[0];
        Assert.Equal(2, list.Items.Count);
        Assert.Equal(MdBlockKind.Code, list.Items[0].Blocks[1].Kind);
        Assert.Equal("- x", list.Items[0].Blocks[1].Text);
        Assert.Equal("next continued lazily", MarkdownBlockParser.PlainText(list.Items[1].Blocks[0].Inlines));
        Assert.Equal(6, list.EndLine);
    }

    [Fact]
    public void Different_bullet_markers_start_a_new_list()
    {
        var blocks = Parse("- a\n* b");
        Assert.Equal(2, blocks.Count);
    }

    [Fact]
    public void Blockquotes_nest_and_support_lazy_continuation()
    {
        var blocks = Parse("> quote one\ncontinues\n> > nested\n\nafter");
        Assert.Equal(2, blocks.Count);
        var quote = blocks[0];
        Assert.Equal(MdBlockKind.Quote, quote.Kind);
        Assert.Equal((1, 3), (quote.StartLine, quote.EndLine));
        Assert.Equal("quote one continues", quote.Children[0].PlainText);
        Assert.Equal(MdBlockKind.Quote, quote.Children[1].Kind);
        Assert.Equal((3, 3), (quote.Children[1].StartLine, quote.Children[1].EndLine));
    }

    [Fact]
    public void Quote_can_hold_a_list_and_a_fence_is_not_lazily_continued()
    {
        var blocks = Parse("> - a\n> - b\n> ```\n> code\nplain");
        Assert.Equal(MdBlockKind.Quote, blocks[0].Kind);
        Assert.Equal(MdBlockKind.List, blocks[0].Children[0].Kind);
        Assert.Equal(MdBlockKind.Code, blocks[0].Children[1].Kind);
        Assert.Equal(MdBlockKind.Paragraph, blocks[1].Kind);
    }

    [Fact]
    public void Tables_parse_header_alignment_rows_and_pad_short_rows()
    {
        var table = Parse("| a | b | c |\n|:--|:-:|--:|\n| 1 | 2 | 3 |\n| x |\n\nafter")[0];
        Assert.Equal(MdBlockKind.Table, table.Kind);
        Assert.Equal([MdAlign.Left, MdAlign.Center, MdAlign.Right], table.Aligns);
        Assert.Equal(3, table.Header.Count);
        Assert.Equal(2, table.Rows.Count);
        Assert.Equal("2", MarkdownBlockParser.PlainText(table.Rows[0][1]));
        Assert.Equal(3, table.Rows[1].Count);
        Assert.Empty(table.Rows[1][2]);
        Assert.Equal((1, 4), (table.StartLine, table.EndLine));
    }

    [Fact]
    public void Pipe_text_without_a_matching_delimiter_row_is_a_paragraph()
    {
        Assert.Equal(MdBlockKind.Paragraph, Parse("a | b\n--- | ---- | --")[0].Kind);
        Assert.Equal(MdBlockKind.Paragraph, Parse("a | b\nnot a delimiter")[0].Kind);
    }

    [Fact]
    public void Table_cells_can_hold_inline_markup_and_escaped_pipes()
    {
        var table = Parse("|h|\n|-|\n|**b** a\\|b|")[0];
        var cell = table.Rows[0][0];
        Assert.Equal(MdInlineKind.Bold, cell[0].Kind);
        Assert.Equal("b a|b", MarkdownBlockParser.PlainText(cell));
    }

    [Fact]
    public void Html_blocks_are_kept_as_raw_text_until_a_blank_line()
    {
        var blocks = Parse("<div align=\"center\">\n  <b>hi</b>\n</div>\n\ntext\n\n<!-- c\nmore -->\nx");
        Assert.Equal(MdBlockKind.Html, blocks[0].Kind);
        Assert.Contains("<b>hi</b>", blocks[0].Text);
        Assert.Equal((1, 3), (blocks[0].StartLine, blocks[0].EndLine));
        Assert.Equal(MdBlockKind.Html, blocks[2].Kind);
        Assert.Equal((7, 8), (blocks[2].StartLine, blocks[2].EndLine));
        Assert.Equal(MdBlockKind.Paragraph, blocks[3].Kind);
    }

    [Fact]
    public void A_paragraph_is_interrupted_by_a_heading_fence_and_list()
    {
        var blocks = Parse("text\n# h\ntext2\n```\nc\n```\ntext3\n- item");
        Assert.Equal(
            [MdBlockKind.Paragraph, MdBlockKind.Heading, MdBlockKind.Paragraph, MdBlockKind.Code, MdBlockKind.Paragraph, MdBlockKind.List],
            blocks.Select(b => b.Kind));
    }

    [Fact]
    public void Empty_input_and_blank_input_yield_no_blocks()
    {
        Assert.Empty(MarkdownBlockParser.Parse([]));
        Assert.Empty(Parse("\n  \n"));
    }

    [Fact]
    public void Tabs_in_leading_whitespace_count_as_indent()
    {
        var list = Parse("- a\n\t- b")[0];
        Assert.Equal(MdBlockKind.List, list.Items[0].Blocks[1].Kind);
    }

    // ---- inlines ----

    [Fact]
    public void Bold_italic_and_strike_nest()
    {
        var o = Inline("a **b *c* d** ~~e~~ _f_");
        Assert.Equal(MdInlineKind.Text, o[0].Kind);
        Assert.Equal(MdInlineKind.Bold, o[1].Kind);
        Assert.Equal(MdInlineKind.Italic, o[1].Children![1].Kind);
        Assert.Equal(MdInlineKind.Strike, o[3].Kind);
        Assert.Equal(MdInlineKind.Italic, o[5].Kind);
        Assert.Equal("a b c d e f", MarkdownBlockParser.PlainText(o));
    }

    [Fact]
    public void Triple_markers_are_bold_italic()
    {
        var o = Inline("***x***");
        var bold = Assert.Single(o);
        Assert.Equal(MdInlineKind.Bold, bold.Kind);
        Assert.Equal(MdInlineKind.Italic, bold.Children![0].Kind);
    }

    [Fact]
    public void Unmatched_and_spaced_markers_stay_literal_and_intraword_underscores_do_not_emphasize()
    {
        Assert.Equal("2 * 3 * 4", MarkdownBlockParser.PlainText(Inline("2 * 3 * 4")));
        Assert.Equal("**open", MarkdownBlockParser.PlainText(Inline("**open")));
        var snake = Inline("snake_case_name");
        Assert.Single(snake);
        Assert.Equal(MdInlineKind.Text, snake[0].Kind);
        Assert.Equal("a ~ b ~~", MarkdownBlockParser.PlainText(Inline("a ~ b ~~")));
    }

    [Fact]
    public void Code_spans_take_precedence_and_trim_one_space()
    {
        var o = Inline("`a *b*` and `` ` `` and ` x `");
        Assert.Equal(MdInlineKind.Code, o[0].Kind);
        Assert.Equal("a *b*", o[0].Text);
        Assert.Equal("`", o[2].Text);
        Assert.Equal("x", o[4].Text);
        Assert.Equal("`open", MarkdownBlockParser.PlainText(Inline("`open")));
    }

    [Fact]
    public void Emphasis_does_not_close_inside_a_code_span()
    {
        var o = Inline("*a `*` b*");
        var italic = Assert.Single(o);
        Assert.Equal(MdInlineKind.Italic, italic.Kind);
        Assert.Equal(3, italic.Children!.Count);
    }

    [Fact]
    public void Links_parse_text_url_title_and_balanced_parens()
    {
        var o = Inline("see [the **docs**](https://x.dev/a_(b) \"Title\") now");
        var link = o[1];
        Assert.Equal(MdInlineKind.Link, link.Kind);
        Assert.Equal("https://x.dev/a_(b)", link.Url);
        Assert.Equal("the docs", MarkdownBlockParser.PlainText(link.Children!));
        Assert.Equal(" now", o[2].Text);
    }

    [Fact]
    public void Broken_links_are_literal_text()
    {
        Assert.Equal("[a](b", MarkdownBlockParser.PlainText(Inline("[a](b")));
        Assert.Equal("[ref]", MarkdownBlockParser.PlainText(Inline("[ref]")));
        Assert.DoesNotContain(Inline("[a] (b)"), i => i.Kind == MdInlineKind.Link);
    }

    [Fact]
    public void Images_become_links_with_alt_text()
    {
        var o = Inline("![alt text](pic.png)");
        Assert.Equal(MdInlineKind.Link, o[0].Kind);
        Assert.Equal("pic.png", o[0].Url);
        Assert.Equal("alt text", MarkdownBlockParser.PlainText(o[0].Children!));
    }

    [Fact]
    public void Autolinks_and_bare_urls_trim_trailing_punctuation()
    {
        var o = Inline("<https://a.dev/x> and https://b.dev/y). and <me@x.dev> plus (see https://c.dev/z).");
        var links = o.Where(i => i.Kind == MdInlineKind.Link).ToList();
        Assert.Equal(4, links.Count);
        Assert.Equal("https://a.dev/x", links[0].Url);
        Assert.Equal("https://b.dev/y", links[1].Url);
        Assert.Equal("mailto:me@x.dev", links[2].Url);
        Assert.Equal("https://c.dev/z", links[3].Url);
    }

    [Fact]
    public void Backslash_escapes_make_markers_literal()
    {
        Assert.Equal("*not italic* [x] `y`", MarkdownBlockParser.PlainText(Inline("\\*not italic\\* \\[x] \\`y\\`")));
        Assert.Equal("a\\b", MarkdownBlockParser.PlainText(Inline("a\\b")));
    }

    [Fact]
    public void Line_breaks_are_soft_by_default_and_hard_with_two_spaces_or_backslash()
    {
        Assert.Equal(MdInlineKind.SoftBreak, Inline("a\nb")[1].Kind);
        Assert.Equal(MdInlineKind.LineBreak, Inline("a  \nb")[1].Kind);
        Assert.Equal(MdInlineKind.LineBreak, Inline("a\\\nb")[1].Kind);
        Assert.Equal("a", Inline("a  \nb")[0].Text);
    }

    [Fact]
    public void Unicode_text_passes_through()
    {
        var o = Inline("**中文** é");
        Assert.Equal("中文 é", MarkdownBlockParser.PlainText(o));
    }
}
