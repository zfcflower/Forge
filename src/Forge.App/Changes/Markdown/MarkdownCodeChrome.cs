using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Forge.App.Localization;
using Forge.App.ViewModels;
using Forge.App.Views;
using Forge.App.Workbench.Controls;

namespace Forge.App.Changes.Markdown;

/// <summary>
/// The header every fenced code block carries: the fence's info string on the left, an optional action button, and the copy
/// button. Shared by <see cref="MarkdownCodeBlock"/> and the Mermaid block (wave W11), which adds a source/diagram toggle as
/// its action - one owner for the copy affordance and for the language subscription keeps the two headers from drifting.
/// </summary>
internal sealed class MarkdownCodeHeader : Grid
{
    private readonly TextBlock label;
    private readonly CopyTextButton copy;

    /// <param name="target">What the copy button reads when pressed.</param>
    /// <param name="action">An extra button placed between the label and copy; null for a plain code block.</param>
    internal MarkdownCodeHeader(ICopyableText target, Control? action = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        Classes.Add("md-code-head");
        ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto");

        label = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
        label.Classes.Add("md-code-lang");

        copy = new CopyTextButton { DataContext = target, Width = 26, Height = 22 };
        copy.Classes.Add("ghost");
        copy.Classes.Add("icon");
        AutomationProperties.SetAutomationId(copy, "md-code-copy");

        var icon = new FluentIcon { Width = 12, Height = 12 };
        icon.Bind(PathIcon.DataProperty, this.GetResourceObservable("IconCopy"));
        copy.Content = icon;

        Children.Add(label);
        if (action is not null)
        {
            Grid.SetColumn(action, 1);
            Children.Add(action);
        }

        Grid.SetColumn(copy, 2);
        Children.Add(copy);
        LocalizeCopy();
    }

    /// <summary>The fence info string; an empty one hides the label, which is what an unlabeled block shows.</summary>
    internal void SetLabel(string info)
    {
        label.Text = info;
        label.IsVisible = info.Length > 0;
    }

    /// <summary>
    /// Follows the UI language while shown: a reply rendered before a language switch stays on screen. The subscription lives
    /// only while attached, so a header that left the tree is not kept alive by the static event.
    /// </summary>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Loc.Instance.LanguageChanged += OnLanguageChanged;
        LocalizeCopy();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        Loc.Instance.LanguageChanged -= OnLanguageChanged;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnLanguageChanged(object? sender, EventArgs e) => LocalizeCopy();

    private void LocalizeCopy()
    {
        var text = Loc.Instance[MarkdownStrings.CopyCode];
        ToolTip.SetTip(copy, text);
        AutomationProperties.SetName(copy, text);
    }
}

/// <summary>
/// The body of a fenced code block: the block's text in a horizontally scrolling monospaced view. Shared so the Mermaid
/// block's "show source" state is the same widget as an ordinary code block instead of a second copy of it.
/// </summary>
internal sealed class MarkdownCodeTextView : ScrollViewer
{
    private readonly SelectableTextBlock text;

    internal MarkdownCodeTextView(string value = "")
    {
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
        VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
        text = new SelectableTextBlock { Text = value };
        text.Classes.Add("md-code-text");
        Content = text;
    }

    /// <summary>The current text; replacing it does not rebuild the view, which is what a streaming block relies on.</summary>
    internal string Text
    {
        get => text.Text ?? string.Empty;
        set => text.Text = value;
    }
}
