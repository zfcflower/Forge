using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Styling;
using Forge.App.Changes.Markdown;
using Forge.App.ViewModels;
using Forge.App.Views;
using Xunit;

namespace Forge.App.Tests.Views;

/// <summary>
/// Pins the chat Markdown renderer's incrementality (forge-app spec "聊天 Markdown 渲染"): a flush appends controls for the
/// blocks that were committed, and only the last live block is refreshed in place - a paragraph gains new inlines and a code
/// block new text - instead of the whole reply being rebuilt.
/// </summary>
[Collection("forge-app-ui")]
public sealed class ChatMarkdownViewTests
{
    [Fact]
    public async Task A_streaming_paragraph_updates_its_text_block_in_place()
    {
        await SharedUi.RunAsync(
            async () =>
            {
                var model = new TurnTextViewModel();
                var body = await ShowAsync(model);
                Assert.Empty(body.Children);

                model.Append("first **bold");
                var paragraph = Assert.IsType<MarkdownTextBlock>(Assert.Single(body.Children));

                model.Append("** claim");
                Assert.Same(paragraph, Assert.Single(body.Children));
                Assert.Equal("first bold claim", Flatten(paragraph.Inlines));
                Assert.Contains(paragraph.Inlines ?? [], static inline => inline is Bold);
            });
    }

    [Fact]
    public async Task A_committed_block_keeps_its_control_when_later_text_streams()
    {
        await SharedUi.RunAsync(
            async () =>
            {
                var model = new TurnTextViewModel();
                var body = await ShowAsync(model);

                model.Append("# Heading\n\n");
                var heading = Assert.Single(body.Children);
                Assert.Contains("md-h1", heading.Classes);

                model.Append("a paragraph that goes on");
                model.Append(" and on\n\n");

                Assert.Equal(2, body.Children.Count);
                Assert.Same(heading, body.Children[0]);
                Assert.Contains("md-p", body.Children[1].Classes);
            });
    }

    [Fact]
    public async Task A_streaming_table_grows_in_place_and_keeps_the_rows_of_ended_lines()
    {
        await SharedUi.RunAsync(
            async () =>
            {
                var model = new TurnTextViewModel();
                var body = await ShowAsync(model);

                model.Append("| a | b |\n|---|---|\n| 1 | 2 |\n| 3 |");
                var table = Assert.Single(body.Children);
                Assert.Contains("md-table", table.Classes);
                var grid = Assert.IsType<Grid>(Assert.IsType<Border>(table).Child);
                var header = grid.Children[0];
                var endedRow = grid.Children[2];
                var partialRow = grid.Children[4];

                model.Append(" 4 |\n| 5 | 6 |");

                Assert.Same(table, Assert.Single(body.Children));
                Assert.Equal(4, grid.RowDefinitions.Count);
                Assert.Equal(8, grid.Children.Count);
                Assert.Same(header, grid.Children[0]);
                Assert.Same(endedRow, grid.Children[2]);
                Assert.NotSame(partialRow, grid.Children[4]);
                Assert.Equal(["a", "b", "1", "2", "3", "4", "5", "6"], [.. CellTexts(grid)]);
                Assert.Equal(Enumerable.Range(0, 8).Select(static i => i / 2), grid.Children.Select(Grid.GetRow));
            });
    }

    [Fact]
    public async Task A_table_whose_delimiter_row_is_still_being_written_is_rendered_afresh()
    {
        await SharedUi.RunAsync(
            async () =>
            {
                var model = new TurnTextViewModel();
                var body = await ShowAsync(model);

                // The delimiter row is still being written: its alignment can change, so nothing of the table is kept.
                model.Append("| a | b |\n|---|--");
                var first = Assert.Single(body.Children);

                model.Append("-:|\n| 1 | 2 |\n");

                Assert.NotSame(first, Assert.Single(body.Children));
                AssertSameTree(MarkdownBlockParser.Parse(["| a | b |", "|---|---:|", "| 1 | 2 |"]), body);
            });
    }

    [Fact]
    public async Task A_streaming_list_keeps_the_items_that_another_item_follows()
    {
        await SharedUi.RunAsync(
            async () =>
            {
                var model = new TurnTextViewModel();
                var body = await ShowAsync(model);

                model.Append("1. one\n2. two\n3. thr");
                var list = Assert.IsType<StackPanel>(Assert.Single(body.Children));
                var one = list.Children[0];
                var two = list.Children[1];
                var three = list.Children[2];

                model.Append("ee\n4. four");

                // "one" is followed by an item on an ended line, so it is final; "two" was followed only by the line still
                // being written ("3. thr" could have turned into text), so it is rebuilt with the rest.
                Assert.Same(list, Assert.Single(body.Children));
                Assert.Same(one, list.Children[0]);
                Assert.NotSame(two, list.Children[1]);
                Assert.NotSame(three, list.Children[2]);
                Assert.Equal(["1.", "2.", "3.", "4."], [.. Markers(list)]);
                var secondTwo = list.Children[1];
                var secondThree = list.Children[2];

                model.Append("\n5. five\n");

                Assert.Same(list, Assert.Single(body.Children));
                Assert.Same(one, list.Children[0]);
                Assert.Same(secondTwo, list.Children[1]);
                Assert.NotSame(secondThree, list.Children[2]);
                Assert.Equal(["1.", "2.", "3.", "4.", "5."], [.. Markers(list)]);
                model.Complete();
                AssertSameTree(MarkdownBlockParser.Parse(["1. one", "2. two", "3. three", "4. four", "5. five"]), body);
            });
    }

    [Fact]
    public async Task A_half_written_marker_that_turns_into_text_does_not_keep_the_item_before_it()
    {
        await SharedUi.RunAsync(
            async () =>
            {
                var model = new TurnTextViewModel();
                var body = await ShowAsync(model);

                // "-" on the line being written is an empty item; "-x" is not an item but a continuation of "b".
                model.Append("- a\n- b\n-");
                model.Append("x\n");
                model.Complete();

                AssertSameTree(MarkdownBlockParser.Parse(["- a", "- b", "-x"]), body);
            });
    }

    private const string Reply = """
        # Title

        Intro with **bold** and `code`.

        1. one
        2. two
           - nested
        3. three
        - [x] done
        - [ ] open

        | name | value |
        |:-----|------:|
        | a    | 1     |
        | b    | 2     |

        ```csharp
        var a = 1;

        var b = 2;
        ```

        > quoted
        > text

        <!-- a comment

        that spans -->

        Closing paragraph without a trailing newline
        """;

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(17)]
    [InlineData(4096)]
    public async Task Any_chunking_renders_the_same_tree_as_one_render_of_the_whole_reply(int size)
    {
        await SharedUi.RunAsync(
            async () =>
            {
                var model = new TurnTextViewModel();
                var body = await ShowAsync(model);
                for (var offset = 0; offset < Reply.Length; offset += size)
                {
                    model.Append(Reply.Substring(offset, Math.Min(size, Reply.Length - offset)));
                }

                model.Complete();

                AssertSameTree(MarkdownBlockParser.Parse(Reply.Split('\n')), body);
            });
    }

    [Fact]
    public async Task Fragments_of_an_over_long_paragraph_render_without_the_block_gap_and_merge_on_completion()
    {
        await SharedUi.RunAsync(
            async () =>
            {
                var model = new TurnTextViewModel();
                var body = await ShowAsync(model);
                var line = new string('x', 120) + "\n";
                for (var index = 0; index < 50; index++)
                {
                    model.Append(line);
                }

                Assert.True(body.Children.Count >= 2);
                Assert.Equal(0, body.Children[0].Margin.Top);
                Assert.Equal(-body.Spacing, body.Children[1].Margin.Top);

                model.Complete();

                var paragraph = Assert.Single(body.Children);
                Assert.Equal(0, paragraph.Margin.Top);
            });
    }

    [Fact]
    public async Task A_selection_in_a_committed_block_survives_later_flushes()
    {
        await SharedUi.RunAsync(
            async () =>
            {
                var model = new TurnTextViewModel();
                var body = await ShowAsync(model);
                model.Append("select me please\n\n");
                var committed = Assert.IsType<MarkdownTextBlock>(Assert.Single(body.Children));
                committed.SelectionStart = 7;
                committed.SelectionEnd = 9;

                model.Append("more text");
                model.Append(" streaming\n\nand more");
                model.Complete();

                Assert.Same(committed, body.Children[0]);
                Assert.Equal("me", committed.SelectedText);
            });
    }

    [Fact]
    public async Task A_detached_view_stops_listening_until_it_is_attached_again()
    {
        await SharedUi.RunAsync(
            async () =>
            {
                var model = new TurnTextViewModel();
                var view = new ChatMarkdownView { DataContext = model };
                var window = SharedUi.Show(view, 620, 800);
                SharedUi.Settle(window);
                var body = Assert.IsType<StackPanel>(view.FindControl<StackPanel>("Body"));
                model.Append("para\n\n");
                Assert.Single(body.Children);

                window.Content = null;
                SharedUi.Settle(window);
                model.Append("second\n\nthird\n");

                // Unsubscribed: nothing is rendered into a tree nobody shows.
                Assert.Single(body.Children);

                window.Content = view;
                SharedUi.Settle(window);
                Assert.Equal(3, body.Children.Count);
            });
    }

    [Fact]
    public async Task Code_block_colors_follow_a_theme_switch_without_a_rebuild()
    {
        await SharedUi.RunAsync(
            async () =>
            {
                var model = new TurnTextViewModel();
                var view = new ChatMarkdownView { DataContext = model };
                var window = SharedUi.Show(view, 620, 800);
                window.RequestedThemeVariant = ThemeVariant.Light;
                SharedUi.Settle(window);
                var body = Assert.IsType<StackPanel>(view.FindControl<StackPanel>("Body"));
                model.Append("```\ncode\n```\n");
                var code = Assert.IsType<MarkdownCodeBlock>(Assert.Single(body.Children));
                SharedUi.Settle(window);
                // The subclass is styled as Border.md-code: the shared surface, padding and radius reach it.
                Assert.NotEqual(default, code.Padding);
                Assert.NotEqual(default, code.CornerRadius);
                var light = Assert.IsAssignableFrom<ISolidColorBrush>(code.Background).Color;

                window.RequestedThemeVariant = ThemeVariant.Dark;
                SharedUi.Settle(window);

                var dark = Assert.IsAssignableFrom<ISolidColorBrush>(code.Background).Color;
                Assert.NotEqual(light, dark);
                Assert.Same(code, Assert.Single(body.Children));
            });
    }

    [Fact]
    public async Task Paragraphs_code_blocks_and_tables_of_one_message_share_one_inset_and_fill_the_width()
    {
        // Chat layout decision (2026-11-14): full window width, one inset for every block - the Markdown blocks add none of
        // their own and carry no maximum width, so their outer edges coincide with the message's.
        await SharedUi.RunAsync(
            async () =>
            {
                var model = new TurnTextViewModel();
                model.Append("A paragraph of text.\n\n```js\nconst x = 1;\n```\n\n| a | b |\n|---|---|\n| 1 | 2 |\n");
                model.Complete();
                var host = new TurnTextView { DataContext = model };
                var window = SharedUi.Show(host, 900, 800);
                SharedUi.Settle(window);

                var view = Assert.Single(host.GetLogicalDescendants().OfType<ChatMarkdownView>());
                var body = Assert.IsType<StackPanel>(view.FindControl<StackPanel>("Body"));
                Assert.Equal(3, body.Children.Count);
                foreach (var block in body.Children)
                {
                    var left = block.TranslatePoint(default, host);
                    Assert.NotNull(left);
                    Assert.Equal(0, left.Value.X, 3);
                    Assert.Equal(host.Bounds.Width, block.Bounds.Width, 3);
                    Assert.True(double.IsPositiveInfinity(block.MaxWidth));
                }
            });
    }

    [Fact]
    public async Task A_fenced_code_block_shows_its_language_and_a_copy_button()
    {
        await SharedUi.RunAsync(
            async () =>
            {
                var model = new TurnTextViewModel();
                var body = await ShowAsync(model);

                model.Append("```csharp\nvar x = 1;\n```\n");

                var code = Assert.IsType<MarkdownCodeBlock>(Assert.Single(body.Children));
                Assert.Equal("var x = 1;", code.CopyText);

                var copy = Assert.Single(code.GetLogicalDescendants().OfType<CopyTextButton>());
                Assert.Equal("md-code-copy", Avalonia.Automation.AutomationProperties.GetAutomationId(copy));
                var language = Assert.Single(code.GetLogicalDescendants().OfType<TextBlock>(), static block => block.Classes.Contains("md-code-lang"));
                Assert.Equal("csharp", language.Text);
                Assert.True(language.IsVisible);
            });
    }

    [Fact]
    public async Task A_streaming_code_block_updates_its_text_in_place()
    {
        await SharedUi.RunAsync(
            async () =>
            {
                var model = new TurnTextViewModel();
                var body = await ShowAsync(model);

                model.Append("```bash\nline one\n");
                var code = Assert.IsType<MarkdownCodeBlock>(Assert.Single(body.Children));
                Assert.Equal("line one", code.CopyText);

                model.Append("line two\n");

                Assert.Same(code, Assert.Single(body.Children));
                Assert.Equal("line one\nline two", code.CopyText);
            });
    }

    [Fact]
    public async Task A_streaming_html_block_keeps_its_html_label_when_it_is_refreshed()
    {
        await SharedUi.RunAsync(
            async () =>
            {
                var model = new TurnTextViewModel();
                var body = await ShowAsync(model);

                model.Append("<!-- note\n");
                var html = Assert.IsType<MarkdownCodeBlock>(Assert.Single(body.Children));
                Assert.True(Language(html).IsVisible);

                model.Append("more\n");

                Assert.Same(html, Assert.Single(body.Children));
                Assert.Equal("html", Language(html).Text);
                Assert.True(Language(html).IsVisible);
                Assert.Equal("html", Avalonia.Automation.AutomationProperties.GetName(html));
            });
    }

    /// <summary>The language label of a rendered code block.</summary>
    private static TextBlock Language(MarkdownCodeBlock code) =>
        Assert.Single(code.GetLogicalDescendants().OfType<TextBlock>(), static block => block.Classes.Contains("md-code-lang"));

    [Fact]
    public async Task A_view_attached_to_a_reply_that_already_streamed_shows_all_of_it()
    {
        await SharedUi.RunAsync(
            async () =>
            {
                var model = new TurnTextViewModel();
                model.Append("# Title\n\ntext\n\n- item\n");
                model.Complete();

                var body = await ShowAsync(model);

                Assert.Equal(3, body.Children.Count);
                Assert.Contains("md-h1", body.Children[0].Classes);
                Assert.Contains("md-p", body.Children[1].Classes);
                Assert.Contains("md-list", body.Children[2].Classes);
            });
    }

    [Fact]
    public async Task A_committed_paragraph_keeps_its_control_when_later_text_streams()
    {
        await SharedUi.RunAsync(
            async () =>
            {
                var model = new TurnTextViewModel();
                var body = await ShowAsync(model);

                model.Append("first paragraph\n\n");
                var first = Assert.Single(body.Children);

                model.Append("second paragraph");

                Assert.Equal(2, body.Children.Count);
                Assert.Same(first, body.Children[0]);
                Assert.Contains("md-p", body.Children[1].Classes);
            });
    }

    [Fact]
    public async Task A_view_detached_and_reattached_catches_up_without_duplicating_blocks()
    {
        await SharedUi.RunAsync(
            async () =>
            {
                var model = new TurnTextViewModel();
                model.Append("# Title\n\npara\n");

                var view = new ChatMarkdownView { DataContext = model };
                var window = SharedUi.Show(view, 620, 800);
                SharedUi.Settle(window);
                var body = Assert.IsType<StackPanel>(view.FindControl<StackPanel>("Body"));
                Assert.Equal(2, body.Children.Count);

                window.Content = null;
                SharedUi.Settle(window);

                model.Append("- item\n");
                SharedUi.Settle(window);

                window.Content = view;
                SharedUi.Settle(window);

                Assert.Equal(3, body.Children.Count);
                Assert.Contains("md-h1", body.Children[0].Classes);
                Assert.Contains("md-p", body.Children[1].Classes);
                Assert.Contains("md-list", body.Children[2].Classes);
            });
    }

    [Fact]
    public async Task Changing_the_data_context_rebuilds_the_whole_tree()
    {
        await SharedUi.RunAsync(
            async () =>
            {
                var first = new TurnTextViewModel();
                first.Append("# First\n\nparagraph one\n");
                var second = new TurnTextViewModel();
                second.Append("## Second\n\nparagraph two\n");

                var view = new ChatMarkdownView { DataContext = first };
                var window = SharedUi.Show(view, 620, 800);
                SharedUi.Settle(window);
                var body = Assert.IsType<StackPanel>(view.FindControl<StackPanel>("Body"));
                Assert.Contains("md-h1", body.Children[0].Classes);

                view.DataContext = second;
                SharedUi.Settle(window);

                Assert.Equal(2, body.Children.Count);
                Assert.Contains("md-h2", body.Children[0].Classes);
                Assert.Contains("md-p", body.Children[1].Classes);
            });
    }

    [Fact]
    public async Task Headings_lists_tables_and_quotes_render_with_their_markdown_classes()
    {
        await SharedUi.RunAsync(
            async () =>
            {
                var blocks = MarkdownBlockParser.Parse(
                    "# H1\n\n## H2\n\n- [x] done\n- [ ] open\n\n1. first\n2. second\n\n| a | b |\n|---|---|\n| 1 | 2 |\n\n> quote".Split('\n'));
                var stack = Assert.IsType<StackPanel>(MarkdownRenderer.RenderBlocks(blocks));
                var window = SharedUi.Show(stack, 620, 800);
                SharedUi.Settle(window);

                Assert.Contains("md-h1", stack.Children[0].Classes);
                Assert.Contains("md-h2", stack.Children[1].Classes);

                var tasks = stack.Children[2];
                Assert.Contains("md-list", tasks.Classes);
                Assert.Equal(["☑", "☐"], [.. Markers(tasks)]);

                var ordered = stack.Children[3];
                Assert.Contains("md-list", ordered.Classes);
                Assert.Equal(["1.", "2."], [.. Markers(ordered)]);

                Assert.Contains("md-table", stack.Children[4].Classes);
                Assert.Contains("md-quote", stack.Children[5].Classes);
            });
    }

    [Fact]
    public async Task Inline_markdown_renders_runs_bold_italic_code_and_links()
    {
        await SharedUi.RunAsync(
            async () =>
            {
                var block = Assert.Single(MarkdownBlockParser.Parse(["plain **bold** *italic* `code` and [link](https://x.dev)"]));
                var text = Assert.IsType<MarkdownTextBlock>(MarkdownRenderer.Render(block));
                var inlines = text.Inlines!;

                Assert.Equal("plain bold italic code and link", Flatten(inlines));
                Assert.Contains(inlines, static inline => inline is Bold);
                Assert.Contains(inlines, static inline => inline is Italic);
                Assert.Contains(inlines.OfType<Run>(), static run => run.Text == "code");
            });
    }

    /// <summary>The text a rendered inline tree shows, which is what the reader sees rather than the source Markdown.</summary>
    private static string Flatten(InlineCollection? inlines)
    {
        var text = new StringBuilder();
        foreach (var inline in inlines ?? [])
        {
            switch (inline)
            {
                case Run run:
                    text.Append(run.Text);
                    break;
                case LineBreak:
                    text.Append('\n');
                    break;
                case Span span:
                    text.Append(Flatten(span.Inlines));
                    break;
            }
        }

        return text.ToString();
    }

    /// <summary>The texts of a table's cells in grid order.</summary>
    private static IEnumerable<string> CellTexts(Grid grid) =>
        grid.Children.Select(static cell => Flatten(Assert.IsType<MarkdownTextBlock>(Assert.IsType<Border>(cell).Child).Inlines));

    /// <summary>
    /// Asserts that the streamed panel shows exactly what one render of <paramref name="whole"/> shows: the same control types,
    /// classes, texts and grid positions, block for block.
    /// </summary>
    private static void AssertSameTree(IReadOnlyList<MdBlock> whole, StackPanel body)
    {
        var expected = Assert.IsType<StackPanel>(MarkdownRenderer.RenderBlocks(whole));
        Assert.Equal(expected.Children.Select(Dump), body.Children.Select(Dump));
    }

    private static string Dump(Control control)
    {
        var text = new StringBuilder();
        Dump(control, text);
        return text.ToString();
    }

    private static void Dump(Control control, StringBuilder text)
    {
        text.Append(control.GetType().Name).Append('[').AppendJoin(',', control.Classes).Append(']');
        text.Append('@').Append(Grid.GetRow(control)).Append(',').Append(Grid.GetColumn(control));
        switch (control)
        {
            case MarkdownTextBlock block:
                text.Append('"').Append(Flatten(block.Inlines)).Append('"');
                break;
            case MarkdownCodeBlock code:
                text.Append('"').Append(code.CopyText).Append('"');
                break;
            case TextBlock block:
                text.Append('"').Append(block.Text).Append('"');
                break;
        }

        text.Append('{');
        foreach (var child in control.GetLogicalChildren().OfType<Control>())
        {
            Dump(child, text);
        }

        text.Append('}');
    }

    /// <summary>The marker labels of a rendered list item row, in order.</summary>
    private static IEnumerable<string> Markers(Control block) =>
        block.GetLogicalDescendants()
            .OfType<TextBlock>()
            .Where(static marker => marker.Classes.Contains("md-marker"))
            .Select(static marker => marker.Text ?? string.Empty);

    private static async Task<StackPanel> ShowAsync(TurnTextViewModel model)
    {
        var view = new ChatMarkdownView { DataContext = model };
        var window = SharedUi.Show(view, 620, 800);
        SharedUi.Settle(window);
        await Task.CompletedTask;
        return Assert.IsType<StackPanel>(view.FindControl<StackPanel>("Body"));
    }
}
