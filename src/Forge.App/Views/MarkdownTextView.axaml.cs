using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Forge.App.Changes.Markdown;
using Forge.App.Logging;
using ZLogger;

namespace Forge.App.Views;

/// <summary>
/// A fixed stretch of Markdown, rendered with the transcript's own parser, blocks and style dictionary, so a card built here
/// looks like a reply card and behaves the same on a language or theme switch. Unlike <see cref="ChatMarkdownView"/> there is
/// no streaming model behind it: the text arrives whole, so it is parsed once per value, and a value that did not change
/// re-renders nothing — a recycled list container that shows another turn therefore pays one render.
/// </summary>
public partial class MarkdownTextView : UserControl
{
    private string? rendered;

    public MarkdownTextView() => InitializeComponent();

    /// <summary>The Markdown to show; empty or null leaves the view blank.</summary>
    [GeneratedStyledProperty]
    public partial string? Text { get; set; }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TextProperty)
        {
            Render();
        }
    }

    private void Render()
    {
        var text = Text ?? string.Empty;
        if (string.Equals(text, rendered, StringComparison.Ordinal))
        {
            return;
        }

        rendered = text;
        Body.Children.Clear();
        if (text.Length == 0)
        {
            return;
        }

        try
        {
            Body.Children.Add(MarkdownRenderer.RenderBlocks(MarkdownBlockParser.Parse(text.ReplaceLineEndings("\n").Split('\n'))));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // The text is agent-written: a parse failure shows it as plain text instead of an empty card.
            AppLogs.Markdown.ZLogWarning($"could not render markdown: {exception.Message}");
            Body.Children.Add(new SelectableTextBlock { Text = text, TextWrapping = TextWrapping.Wrap });
        }
    }
}
