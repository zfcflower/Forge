using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Forge.App.Changes.Markdown;
using Forge.App.Localization;
using Forge.App.Views;
using Xunit;

namespace Forge.App.Tests.Views;

/// <summary>
/// Pins the fenced code block chrome: <see cref="MarkdownCodeBlock.Update"/> swaps the code and the language label in
/// place, an empty info string hides the label, the copy button carries its automation id and tooltip, and an HTML block
/// renders through the same control with the label "html".
/// </summary>
[Collection("forge-app-ui")]
public sealed class MarkdownCodeBlockTests
{
    [Fact]
    public async Task Update_replaces_text_and_language_label_in_place()
    {
        await SharedUi.RunAsync(
            async () =>
            {
                var code = new MarkdownCodeBlock("old", "js");
                var window = SharedUi.Show(code, 620, 200);
                SharedUi.Settle(window);

                Assert.Equal("old", code.CopyText);
                var lang = Assert.Single(
                    code.GetLogicalDescendants().OfType<TextBlock>(),
                    static block => block.Classes.Contains("md-code-lang"));
                Assert.Equal("js", lang.Text);
                Assert.True(lang.IsVisible);

                code.Update("new\nline", "csharp");

                Assert.Equal("new\nline", code.CopyText);
                Assert.Equal("csharp", lang.Text);
                Assert.True(lang.IsVisible);

                code.Update("x", "");

                Assert.Equal("x", code.CopyText);
                Assert.Equal(string.Empty, lang.Text);
                Assert.False(lang.IsVisible);
            });
    }

    [Fact]
    public async Task The_copy_button_has_the_documented_automation_id_and_tooltip()
    {
        await SharedUi.RunAsync(
            async () =>
            {
                var code = new MarkdownCodeBlock("body", "go");
                var window = SharedUi.Show(code, 620, 200);
                SharedUi.Settle(window);

                var copy = Assert.Single(code.GetLogicalDescendants().OfType<CopyTextButton>());
                Assert.Equal("md-code-copy", AutomationProperties.GetAutomationId(copy));
                Assert.Equal(Loc.Instance[MarkdownStrings.CopyCode], ToolTip.GetTip(copy));
            });
    }

    [Fact]
    public async Task An_empty_info_hides_the_language_label()
    {
        await SharedUi.RunAsync(
            async () =>
            {
                var code = new MarkdownCodeBlock("text", "");
                var window = SharedUi.Show(code, 620, 200);
                SharedUi.Settle(window);

                var lang = Assert.Single(
                    code.GetLogicalDescendants().OfType<TextBlock>(),
                    static block => block.Classes.Contains("md-code-lang"));
                Assert.False(lang.IsVisible);
            });
    }

    [Fact]
    public async Task Update_keeps_the_accessible_name_in_step_with_the_language()
    {
        // The info string can arrive across chunks, so a streaming block's name must follow Update instead of the constructor.
        await SharedUi.RunAsync(
            async () =>
            {
                var code = new MarkdownCodeBlock("body", "");
                var window = SharedUi.Show(code, 620, 200);
                SharedUi.Settle(window);
                Assert.Null(AutomationProperties.GetName(code));

                code.Update("body", "java");
                Assert.Equal("java", AutomationProperties.GetName(code));

                code.Update("body", "javascript");
                Assert.Equal("javascript", AutomationProperties.GetName(code));

                code.Update("body", "");
                Assert.Null(AutomationProperties.GetName(code));
            });
    }

    [Fact]
    public async Task Rendering_an_html_block_produces_a_code_block_labelled_html()
    {
        await SharedUi.RunAsync(
            async () =>
            {
                var block = new MdBlock(MdBlockKind.Html, 1, 1) { Text = "<div>hello</div>" };
                var code = Assert.IsType<MarkdownCodeBlock>(MarkdownRenderer.Render(block));
                var window = SharedUi.Show(code, 620, 200);
                SharedUi.Settle(window);

                Assert.Equal("<div>hello</div>", code.CopyText);
                var lang = Assert.Single(
                    code.GetLogicalDescendants().OfType<TextBlock>(),
                    static label => label.Classes.Contains("md-code-lang"));
                Assert.Equal("html", lang.Text);
                Assert.True(lang.IsVisible);
            });
    }
}
