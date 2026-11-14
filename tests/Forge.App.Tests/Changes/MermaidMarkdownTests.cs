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
/// the reply to be rebuilt.
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

    private static List<MdBlock> Parse(params string[] lines) => MarkdownBlockParser.Parse(lines);

    private static MdBlock CodeBlock(StreamingMarkdown document) =>
        document.Stable.Concat(document.Live).Single(static block => block.Kind == MdBlockKind.Code);

    private static MermaidDiagramView Diagram(Control block) => block.GetLogicalDescendants().OfType<MermaidDiagramView>().Single();
}
