using System.Text;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.LogicalTree;
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
    public async Task A_streaming_markdown_table_replaces_its_control_instead_of_growing_it()
    {
        await SharedUi.RunAsync(
            async () =>
            {
                var model = new TurnTextViewModel();
                var body = await ShowAsync(model);

                model.Append("| a | b |\n|---|---|\n| 1 | 2 |");
                var first = Assert.Single(body.Children);
                Assert.Contains("md-table", first.Classes);

                model.Append("\n| 3 | 4 |");

                var table = Assert.Single(body.Children);
                Assert.NotSame(first, table);
                Assert.Contains("md-table", table.Classes);
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
