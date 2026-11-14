using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Forge.App.ViewModels;

namespace Forge.App.Changes.Markdown;

/// <summary>
/// One rendered fenced code block: a language label, a copy button and the horizontally scrolling code text. The chat
/// transcript, the diff preview and the plan panel all render through <see cref="MarkdownRenderer"/>, so they get the
/// header together. The Chrome (header and text view) lives in <see cref="MarkdownCodeHeader"/> and
/// <see cref="MarkdownCodeTextView"/>, shared with the Mermaid block, which shows the same source view as its fallback.
/// </summary>
internal sealed class MarkdownCodeBlock : Border, ICopyableText
{
    private readonly MarkdownCodeHeader header;
    private readonly MarkdownCodeTextView body;
    private string code;

    /// <summary>
    /// Styled as the <c>Border.md-code</c> it replaces: Avalonia matches a type selector against the style key, so without this
    /// the shared code-block surface (background, padding, corner radius) would not apply to the subclass.
    /// </summary>
    protected override Type StyleKeyOverride => typeof(Border);

    public MarkdownCodeBlock(string code, string info)
    {
        Classes.Add("md-code");
        this.code = code;

        header = new MarkdownCodeHeader(this);
        header.SetLabel(info);
        body = new MarkdownCodeTextView(code);

        var stack = new StackPanel { Spacing = 4 };
        stack.Children.Add(header);
        stack.Children.Add(body);
        Child = stack;

        ApplyName(info);
    }

    /// <summary>The block's current text, read by the header's copy button when it is pressed.</summary>
    public string CopyText => code;

    /// <summary>Replaces the code and language label in place - a streaming block grows without being rebuilt.</summary>
    public void Update(string code, string info)
    {
        this.code = code;
        body.Text = code;
        header.SetLabel(info);
        ApplyName(info);
    }

    /// <summary>The block's accessible name is its fence info string; without one the label and the text speak for themselves.</summary>
    private void ApplyName(string info) => AutomationProperties.SetName(this, info.Length > 0 ? info : null);
}
