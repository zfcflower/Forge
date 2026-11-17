using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avalonia.Controls.Documents;
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

    /// <summary>The thread this header was attached on; a language notification from another one is not ours.</summary>
    private int attachedThread;

    /// <summary>Styles select by the base type; without this a sealed subclass matches none of the Grid styles.</summary>
    protected override Type StyleKeyOverride => typeof(Grid);

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
        attachedThread = Environment.CurrentManagedThreadId;
        Loc.Instance.LanguageChanged += OnLanguageChanged;
        LocalizeCopy();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        Loc.Instance.LanguageChanged -= OnLanguageChanged;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        // A header of a window whose session is gone still sits in the subscriber list of the process-wide
        // table; setting its tooltip from another thread would touch this control off the dispatcher that owns it.
        if (Environment.CurrentManagedThreadId == attachedThread)
        {
            LocalizeCopy();
        }
    }

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
    private string value;
    private string language = string.Empty;
    private int version;
    private bool pending;
    private DispatcherTimer? settle;

    /// <summary>How long a streaming block's text must stay unchanged before it is tokenized again.</summary>
    private static readonly TimeSpan SettleDelay = TimeSpan.FromMilliseconds(150);

    protected override Type StyleKeyOverride => typeof(ScrollViewer);

    internal MarkdownCodeTextView(string value = "")
    {
        this.value = value;
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
        VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
        text = new SelectableTextBlock { Text = value };
        text.Classes.Add("md-code-text");
        // The code font is the user's preference (Appearance > code font, resource FontMono); bind it on the control itself so
        // no style-selector detail can leave the block on the inherited UI font.
        text.Bind(TextBlock.FontFamilyProperty, text.GetResourceObservable("FontMono"));
        text.Bind(TextBlock.FontSizeProperty, text.GetResourceObservable("FontSizeCode"));
        Content = text;
    }

    /// <summary>The current text; replacing it does not rebuild the view, which is what a streaming block relies on.</summary>
    internal string Text
    {
        get => value;
        set
        {
            if (this.value == value)
            {
                return;
            }

            this.value = value;
            text.Text = value;
            // A streaming block changes on every flush; tokenizing each time would re-colour the whole block ten times a
            // second. The plain text shows immediately and the colours follow once the text has been still for a moment.
            ScheduleHighlight(settled: false);
        }
    }

    /// <summary>The fence's info string; its first word picks the grammar. An unknown or empty one keeps the text plain.</summary>
    internal void SetLanguage(string info)
    {
        var word = CodeHighlighter.FirstWord(info);
        if (word == language)
        {
            return;
        }

        language = word;
        ScheduleHighlight();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ActualThemeVariantChanged += OnThemeChanged;
        ScheduleHighlight();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        ActualThemeVariantChanged -= OnThemeChanged;
        settle?.Stop();
        base.OnDetachedFromVisualTree(e);
    }

    private void OnThemeChanged(object? sender, EventArgs e) => ScheduleHighlight();

    /// <summary>Coalesces bursts (a streaming block changes every flush) into one tokenization after the layout has settled.</summary>
    private void ScheduleHighlight(bool settled = true)
    {
        version++;
        if (!settled)
        {
            if (language.Length == 0 || TopLevel.GetTopLevel(this) is null)
            {
                return;
            }

            if (settle is null)
            {
                settle = new DispatcherTimer { Interval = SettleDelay };
                settle.Tick += (_, _) =>
                {
                    settle.Stop();
                    ScheduleHighlight();
                };
            }

            settle.Stop();
            settle.Start();
            return;
        }

        settle?.Stop();
        if (pending || language.Length == 0 || TopLevel.GetTopLevel(this) is null)
        {
            return;
        }

        pending = true;
        Dispatcher.UIThread.Post(Highlight, DispatcherPriority.Background);
    }

    private void Highlight()
    {
        pending = false;
        if (language.Length == 0 || TopLevel.GetTopLevel(this) is null)
        {
            return;
        }

        var seen = version;
        var dark = Forge.App.Editing.EditorSettings.Current.IsDarkTheme(ActualThemeVariant == ThemeVariant.Dark);
        var lines = CodeHighlighter.Tokenize(value, language, dark);
        if (lines is null || seen != version)
        {
            return;
        }

        var runs = new InlineCollection();
        var rows = value.Split('\n');
        for (var index = 0; index < rows.Length && index < lines.Length; index++)
        {
            if (index > 0)
            {
                runs.Add(new LineBreak());
            }

            var row = rows[index].TrimEnd('\r');
            var at = 0;
            foreach (var token in lines[index])
            {
                var start = Math.Min(token.Start, row.Length);
                var end = Math.Min(token.End, row.Length);
                if (start > at)
                {
                    runs.Add(new Run(row[at..start]));
                }

                if (end > start)
                {
                    runs.Add(new Run(row[start..end]) { Foreground = token.Brush, FontStyle = token.Style });
                }

                at = Math.Max(at, end);
            }

            if (at < row.Length)
            {
                runs.Add(new Run(row[at..]));
            }
        }

        text.Inlines = runs;
    }
}
