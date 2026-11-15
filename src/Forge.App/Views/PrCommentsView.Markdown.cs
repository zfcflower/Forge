using Avalonia;
using Avalonia.Controls;
using Forge.App.Changes.Markdown;
using Forge.App.Logging;
using ZLogger;

namespace Forge.App.Views;

/// <summary>
/// One review comment's body, rendered as Markdown with the shared renderer the chat, the diff preview and the plan panel
/// use (<see cref="MarkdownBlockParser"/> + <see cref="MarkdownRenderer"/> + <c>Resources/MarkdownStyles.axaml</c>, which
/// the panel's view includes). A reviewer writes Markdown — code fences, lists, links — so the body reads the way it was
/// written instead of as raw source.
/// <para>The render runs when the control is attached and when <see cref="Markdown"/> changes, never per frame; the thread
/// list virtualizes, so only the visible comments are ever parsed. A body that fails to parse is shown as plain selectable
/// text: reviewer text is not allowed to break the panel.</para>
/// </summary>
internal sealed class MarkdownCommentBody : Decorator
{
    public static readonly StyledProperty<string> MarkdownProperty =
        AvaloniaProperty.Register<MarkdownCommentBody, string>(nameof(Markdown), string.Empty);

    private string rendered = string.Empty;
    private bool hasRendered;

    public string Markdown
    {
        get => GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == MarkdownProperty)
        {
            Render();
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        // A recycled container (the list virtualizes) keeps its child, so the body is rendered again here when the text is
        // not the one already drawn.
        if (!hasRendered || !string.Equals(rendered, Markdown, StringComparison.Ordinal))
        {
            Render();
        }
    }

    private void Render()
    {
        var text = Markdown ?? string.Empty;
        if (hasRendered && string.Equals(rendered, text, StringComparison.Ordinal))
        {
            return;
        }

        rendered = text;
        hasRendered = true;
        if (text.Length == 0)
        {
            Child = null;
            return;
        }

        try
        {
            var blocks = MarkdownBlockParser.Parse(text.ReplaceLineEndings("\n").Split('\n'));
            Child = MarkdownRenderer.RenderBlocks(blocks);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            AppLogs.Markdown.ZLogWarning($"could not render a review comment: {exception.Message}");
            Child = new SelectableTextBlock { Text = text, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        }
    }
}