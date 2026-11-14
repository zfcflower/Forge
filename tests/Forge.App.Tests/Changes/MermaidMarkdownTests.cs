using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Forge.App.Changes.Markdown;
using Forge.App.Services;
using Forge.App.ViewModels;
using Forge.App.Views;
using Xunit;

namespace Forge.App.Tests.Changes;

/// <summary>
/// Pins where a Mermaid fence is recognized (wave W11): the block parser records whether the fence closed, the renderer turns
/// a fence whose first info word is <c>mermaid</c> into a <see cref="MermaidBlockView"/> and leaves every other code block
/// alone, and a streaming reply upgrades a fence that gains its language after the opening backticks instead of waiting for
/// the reply to be rebuilt. A reply that ends without closing its fence shows the source while it streams and renders once
/// the reply completes - finality closes the fence, and a diagram that then fails to parse goes through the ordinary failure
/// loop (error line, retry, hand-off) instead of sitting as silent source.
/// </summary>
[Collection("forge-app-ui")]
public sealed class MermaidMarkdownTests
{
    [Fact]
    public void A_closed_fence_is_marked_closed_and_an_open_one_is_not()
    {
        var closed = Assert.Single(Parse("```mermaid", "flowchart TD", "  A-->B", "```"));
        Assert.Equal(MdBlockKind.Code, closed.Kind);
        Assert.Equal("mermaid", closed.Info);
        Assert.Equal("flowchart TD\n  A-->B", closed.Text);
        Assert.True(closed.FenceClosed);

        var open = Assert.Single(Parse("```mermaid", "flowchart TD", "  A-->B"));
        Assert.Equal(MdBlockKind.Code, open.Kind);
        Assert.False(open.FenceClosed);
    }

    [Fact]
    public void A_final_parse_reports_a_trailing_open_fence_as_closed()
    {
        var streaming = Assert.Single(Parse("```mermaid", "flowchart TD", "  A-->B"));

        var finalized = Assert.Single(MarkdownBlockParser.Parse(["```mermaid", "flowchart TD", "  A-->B"], final: true));

        Assert.False(streaming.FenceClosed);
        Assert.True(finalized.FenceClosed);
        // Finality only closes the fence: the block keeps its lines, its text and its info string, so nothing re-renders.
        Assert.Equal((streaming.StartLine, streaming.EndLine), (finalized.StartLine, finalized.EndLine));
        Assert.Equal(streaming.Text, finalized.Text);
        Assert.Equal(streaming.Info, finalized.Info);
    }

    [Fact]
    public void Finality_leaves_every_reading_but_the_trailing_open_fence_alone()
    {
        // The default parse is the streaming one: an open fence is still being written, so it waits.
        Assert.False(Assert.Single(Parse("```mermaid", "flowchart TD")).FenceClosed);
        // A fence that closed is not re-closed, a last block that is no open code fence is untouched, and so is nothing.
        Assert.True(Assert.Single(MarkdownBlockParser.Parse(["```mermaid", "flowchart TD", "```"], final: true)).FenceClosed);
        Assert.Equal(MdBlockKind.Paragraph, Assert.Single(MarkdownBlockParser.Parse(["text"], final: true)).Kind);
        Assert.Empty(MarkdownBlockParser.Parse([], final: true));
    }

    [Fact]
    public void Indented_code_is_never_a_closed_fence()
    {
        var block = Assert.Single(Parse("    flowchart TD"));

        Assert.Equal(MdBlockKind.Code, block.Kind);
        Assert.False(block.FenceClosed);
    }

    [Fact]
    public void A_fence_among_other_blocks_keeps_its_closed_state()
    {
        var blocks = Parse("# Title", "", "```mermaid", "flowchart TD", "```", "", "after");

        var mermaid = Assert.Single(blocks, static block => block.Kind == MdBlockKind.Code);
        Assert.True(mermaid.FenceClosed);
        Assert.Equal("mermaid", mermaid.Info);
    }

    [Fact]
    public void A_streaming_fence_stays_open_until_its_closing_line_arrives()
    {
        var document = new StreamingMarkdown();
        document.Append("```mermaid\nflowchart TD\n  A-->B\n");

        Assert.False(CodeBlock(document).FenceClosed);

        document.Append("```\n");

        Assert.True(CodeBlock(document).FenceClosed);
    }

    [Fact]
    public void Completing_a_reply_that_ends_in_an_open_fence_finalizes_it_and_replaces_the_live_parse()
    {
        var document = new StreamingMarkdown();
        document.Append("intro\n\n```mermaid\nflowchart TD\n  A-->B");
        var streaming = CodeBlock(document);
        Assert.False(streaming.FenceClosed);
        Assert.Equal(0, document.Revision);

        document.Complete();

        // Finality closed the fence, so the incremental parse no longer matches it and the whole document was replaced.
        var finalized = CodeBlock(document);
        Assert.True(finalized.FenceClosed);
        Assert.Equal((streaming.StartLine, streaming.EndLine), (finalized.StartLine, finalized.EndLine));
        Assert.Equal(streaming.Text, finalized.Text);
        Assert.Equal(1, document.Revision);
        Assert.Empty(document.Live);
    }

    [Fact]
    public Task The_renderer_calls_a_mermaid_fence_a_diagram_and_every_other_fence_a_code_block() => SharedUi.RunAsync(() =>
    {
        Assert.IsType<MermaidBlockView>(MarkdownRenderer.Render(Assert.Single(Parse("```mermaid", "flowchart TD", "```"))));
        Assert.IsType<MermaidBlockView>(MarkdownRenderer.Render(Assert.Single(Parse("```Mermaid title=x", "flowchart TD", "```"))));
        Assert.IsType<MarkdownCodeBlock>(MarkdownRenderer.Render(Assert.Single(Parse("```javascript", "let x = 1;", "```"))));
        Assert.IsType<MarkdownCodeBlock>(MarkdownRenderer.Render(Assert.Single(Parse("<div>x</div>"))));
        return Task.CompletedTask;
    });

    [Fact]
    public Task A_chat_reply_renders_its_mermaid_fence_as_a_diagram_and_the_next_fence_as_code() => SharedUi.RunAsync(() =>
    {
        MermaidRenderService.Current.ClearCache();
        var model = new TurnTextViewModel();
        var view = new ChatMarkdownView { DataContext = model };
        var window = SharedUi.Show(view, 620, 800);
        SharedUi.Settle(window);
        var body = Assert.IsType<StackPanel>(view.FindControl<StackPanel>("Body"));

        model.Append("```mermaid\nflowchart TD\n  A-->B\n```\n\n");
        model.Append("```csharp\nvar x = 1;\n```\n");

        Assert.Collection(
            body.Children,
            static child => Assert.IsType<MermaidBlockView>(child),
            static child => Assert.IsType<MarkdownCodeBlock>(child));
        window.Close();
        return Task.CompletedTask;
    });

    [Fact]
    public Task A_fence_that_gains_its_mermaid_info_mid_stream_is_upgraded_and_rendered_when_it_closes() => SharedUi.RunAsync(async () =>
    {
        MermaidRenderService.Current.ClearCache();
        var model = new TurnTextViewModel();
        var view = new ChatMarkdownView { DataContext = model };
        var window = SharedUi.Show(view, 620, 800);
        SharedUi.Settle(window);
        var body = Assert.IsType<StackPanel>(view.FindControl<StackPanel>("Body"));

        // The opening backticks arrive before the language does, so the block starts as ordinary code.
        model.Append("```");
        Assert.IsType<MarkdownCodeBlock>(Assert.Single(body.Children));

        model.Append("mermaid\nflowchart TD\n  A-->B\n");
        var diagram = Assert.IsType<MermaidBlockView>(Assert.Single(body.Children));
        Assert.Null(Diagram(diagram).Picture);

        model.Append("```\n");

        Assert.True(await TestPump.UntilAsync(() => Diagram(diagram).Picture is not null));
        Assert.Same(diagram, Assert.Single(body.Children));
        window.Close();
    });

    [Fact]
    public Task An_open_mermaid_fence_shows_its_source_and_renders_nothing_while_it_streams() => SharedUi.RunAsync(() =>
    {
        var (model, body, window) = ChatReply();

        model.Append("```mermaid\nflowchart TD\n  A-->B\n");

        var block = Assert.IsType<MermaidBlockView>(Assert.Single(body.Children));
        Assert.Null(Diagram(block).Picture);
        Assert.True(Source(block).IsVisible);
        Assert.Equal("flowchart TD\n  A-->B", Source(block).Text);
        Assert.False(StatusRow(block).IsVisible);
        window.Close();
        return Task.CompletedTask;
    });

    [Fact]
    public Task Completing_the_reply_renders_the_fence_it_ended_without_closing() => SharedUi.RunAsync(async () =>
    {
        var (model, body, window) = ChatReply();
        model.Append("```mermaid\nflowchart TD\n  A-->B\n");
        var streaming = Assert.IsType<MermaidBlockView>(Assert.Single(body.Children));
        Assert.Null(Diagram(streaming).Picture);

        model.Complete();

        // Finality closed the fence: the incremental parse was replaced, the reply was rebuilt around it and it rendered once.
        var finalized = Assert.IsType<MermaidBlockView>(Assert.Single(body.Children));
        Assert.NotSame(streaming, finalized);
        Assert.True(await TestPump.UntilAsync(() => Diagram(finalized).Picture is not null));
        Assert.True(Diagram(finalized).IsVisible);
        Assert.False(Source(finalized).IsVisible);
        window.Close();
    });

    [Fact]
    public Task A_finalized_open_fence_that_fails_to_parse_goes_through_the_failure_loop() => SharedUi.RunAsync(async () =>
    {
        var (model, body, window) = ChatReply();

        model.Append("```mermaid\nthis is not mermaid at all\n");
        model.Complete();

        var finalized = Assert.IsType<MermaidBlockView>(Assert.Single(body.Children));
        Assert.True(await TestPump.UntilAsync(() => Status(finalized).Classes.Contains("mermaid-status-error")));

        // Not silent source: the error line and the renderer's own message appear, with the retry offered. The hand-off
        // button stays hidden because no session hosts this transcript - its state is a decision for the tree, not the block.
        Assert.False(string.IsNullOrWhiteSpace(Status(finalized).Text));
        var detail = Detail(finalized);
        Assert.True(detail.IsVisible);
        Assert.Contains("Invalid mermaid header", detail.Text, StringComparison.Ordinal);
        Assert.True(Find<Button>(finalized, "mermaid-retry").IsVisible);
        Assert.False(Find<Button>(finalized, "mermaid-fix").IsVisible);
        Assert.True(Source(finalized).IsVisible);
        Assert.Null(Diagram(finalized).Picture);
        window.Close();
    });

    private static List<MdBlock> Parse(params string[] lines) => MarkdownBlockParser.Parse(lines);

    private static MdBlock CodeBlock(StreamingMarkdown document) =>
        document.Stable.Concat(document.Live).Single(static block => block.Kind == MdBlockKind.Code);

    private static MermaidDiagramView Diagram(Control block) => block.GetLogicalDescendants().OfType<MermaidDiagramView>().Single();

    /// <summary>A chat reply streaming into its own view, the way the timeline shows one.</summary>
    private static (TurnTextViewModel Model, StackPanel Body, Window Window) ChatReply()
    {
        MermaidRenderService.Current.ClearCache();
        var model = new TurnTextViewModel();
        var view = new ChatMarkdownView { DataContext = model };
        var window = SharedUi.Show(view, 620, 800);
        SharedUi.Settle(window);
        var body = Assert.IsType<StackPanel>(view.FindControl<StackPanel>("Body"));
        return (model, body, window);
    }

    private static MarkdownCodeTextView Source(Control block) =>
        block.GetLogicalDescendants().OfType<MarkdownCodeTextView>().Single();

    private static StackPanel StatusRow(Control block) => Find<StackPanel>(block, "mermaid-status");

    private static TextBlock Status(Control block) =>
        block.GetLogicalDescendants().OfType<TextBlock>().Single(static text => text.Classes.Contains("mermaid-status"));

    private static SelectableTextBlock Detail(Control block) =>
        block.GetLogicalDescendants().OfType<SelectableTextBlock>().Single(static text => text.Classes.Contains("mermaid-detail"));

    private static T Find<T>(Control root, string automationId)
        where T : Control =>
        root.GetLogicalDescendants().OfType<T>().Single(control => AutomationProperties.GetAutomationId(control) == automationId);
}
