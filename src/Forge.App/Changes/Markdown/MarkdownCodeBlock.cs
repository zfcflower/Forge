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
/// One rendered fenced code block: a language label, a copy button and the horizontally scrolling code text. The chat
/// transcript, the diff preview and the plan panel all render through <see cref="MarkdownRenderer"/>, so they get the
/// header together.
/// </summary>
internal sealed class MarkdownCodeBlock : Border, ICopyableText
{
    private readonly SelectableTextBlock codeText;
    private readonly TextBlock lang;
    private string code;

    public MarkdownCodeBlock(string code, string info)
    {
        Classes.Add("md-code");
        this.code = code;

        codeText = new SelectableTextBlock { Text = code };
        codeText.Classes.Add("md-code-text");

        var scroller = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = codeText,
        };

        lang = new TextBlock { Text = info, VerticalAlignment = VerticalAlignment.Center };
        lang.Classes.Add("md-code-lang");
        lang.IsVisible = info.Length > 0;

        var copy = new CopyTextButton
        {
            DataContext = this,
            Width = 26,
            Height = 22,
        };
        copy.Classes.Add("ghost");
        copy.Classes.Add("icon");
        AutomationProperties.SetAutomationId(copy, "md-code-copy");
        ToolTip.SetTip(copy, Loc.Instance[MarkdownStrings.CopyCode]);
        AutomationProperties.SetName(copy, Loc.Instance[MarkdownStrings.CopyCode]);

        var icon = new FluentIcon { Width = 12, Height = 12 };
        icon.Bind(PathIcon.DataProperty, this.GetResourceObservable("IconCopy"));
        copy.Content = icon;

        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        head.Classes.Add("md-code-head");
        Grid.SetColumn(lang, 0);
        Grid.SetColumn(copy, 1);
        head.Children.Add(lang);
        head.Children.Add(copy);

        var stack = new StackPanel { Spacing = 4 };
        stack.Children.Add(head);
        stack.Children.Add(scroller);
        Child = stack;

        ApplyName(info);
    }

    /// <summary>The block's current text, read by the header's copy button when it is pressed.</summary>
    public string CopyText => code;

    /// <summary>Replaces the code and language label in place - a streaming block grows without being rebuilt.</summary>
    public void Update(string code, string info)
    {
        this.code = code;
        codeText.Text = code;
        lang.Text = info;
        lang.IsVisible = info.Length > 0;
        ApplyName(info);
    }

    /// <summary>The block's accessible name is its fence info string; without one the label and the text speak for themselves.</summary>
    private void ApplyName(string info) => AutomationProperties.SetName(this, info.Length > 0 ? info : null);
}
