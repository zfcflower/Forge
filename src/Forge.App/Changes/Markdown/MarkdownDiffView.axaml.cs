using System.ComponentModel;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Forge.App.Localization;

namespace Forge.App.Changes.Markdown;

/// <summary>
/// The rendered Markdown preview of one file diff: new-side blocks with changed ones tinted green, removed blocks tinted red,
/// a <see cref="DiffBlockBar"/> above each change, and runs of unchanged blocks folded away.
/// </summary>
internal sealed partial class MarkdownDiffView : UserControl
{
    private readonly IDiffFileItem file;
    private object? builtDiff;
    private object? builtBlocks;
    private bool built;

    public MarkdownDiffView(IDiffFileItem file)
    {
        ArgumentNullException.ThrowIfNull(file);
        this.file = file;
        InitializeComponent();
        Rebuild();
    }

    /// <summary>Raised when the rendered height changes without a layout pass of the host (fold expanded, content rebuilt).</summary>
    public event EventHandler? LayoutHeightChanged;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        file.PropertyChanged -= OnFilePropertyChanged;
        file.PropertyChanged += OnFilePropertyChanged;
        if (!ReferenceEquals(builtDiff, file.Diff) || !ReferenceEquals(builtBlocks, file.Blocks))
        {
            Rebuild();
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        file.PropertyChanged -= OnFilePropertyChanged;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnFilePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (null or "" or nameof(IDiffFileItem.Blocks) or nameof(IDiffFileItem.Diff)))
        {
            return;
        }

        if (Dispatcher.UIThread.CheckAccess())
        {
            Rebuild();
        }
        else
        {
            Dispatcher.UIThread.Post(Rebuild);
        }
    }

    private void RaiseLayoutHeightChanged() => LayoutHeightChanged?.Invoke(this, EventArgs.Empty);

    private void Rebuild()
    {
        builtDiff = file.Diff;
        builtBlocks = file.Blocks;
        var root = Root;
        root.Children.Clear();
        var items = MarkdownDiffPlan.Build(file);
        var heading = "";
        var i = 0;
        while (i < items.Count)
        {
            var item = items[i];
            if (item.Kind != MdItemKind.Block || item.Changed)
            {
                root.Children.Add(Create(item));
                TrackHeading(item, ref heading);
                i++;
                continue;
            }

            var end = i;
            while (end < items.Count && items[end] is { Kind: MdItemKind.Block, Changed: false })
            {
                end++;
            }

            // Keep the unchanged block next to a change visible as context; fold the rest when that saves at least two blocks.
            var keepHead = i > 0 ? 1 : 0;
            var keepTail = end < items.Count ? 1 : 0;
            var foldStart = i + keepHead;
            var foldEnd = end - keepTail;
            if (foldEnd - foldStart < 2)
            {
                foldStart = foldEnd = end;
            }

            for (var k = i; k < foldStart; k++)
            {
                root.Children.Add(Create(items[k]));
                TrackHeading(items[k], ref heading);
            }

            if (foldEnd > foldStart)
            {
                var hidden = items.GetRange(foldStart, foldEnd - foldStart);
                root.Children.Add(CreateFold(hidden, heading));
                foreach (var h in hidden)
                {
                    TrackHeading(h, ref heading);
                }
            }

            for (var k = Math.Max(foldEnd, foldStart); k < end; k++)
            {
                root.Children.Add(Create(items[k]));
                TrackHeading(items[k], ref heading);
            }

            i = end;
        }

        if (built)
        {
            RaiseLayoutHeightChanged();
        }

        built = true;
    }

    private static void TrackHeading(MdItem item, ref string heading)
    {
        if (item is { Kind: MdItemKind.Block, Block.Kind: MdBlockKind.Heading })
        {
            heading = item.Block.PlainText;
        }
    }

    private static Control Create(MdItem item)
    {
        switch (item.Kind)
        {
            case MdItemKind.Bar:
            {
                var bar = new DiffBlockBar { DataContext = item.Bar, HorizontalAlignment = HorizontalAlignment.Left, Height = DiffBlockBar.BarHeight };
                bar.Classes.Add("md-bar");
                AutomationProperties.SetAutomationId(bar, "md-block-bar");
                return bar;
            }

            case MdItemKind.Gap:
            {
                var gap = new Border { Child = new TextBlock { Text = Loc.Instance.Format(MarkdownStrings.Gap, item.GapLines) } };
                gap.Classes.Add("md-gap");
                AutomationProperties.SetAutomationId(gap, "md-gap");
                return gap;
            }

            default:
            {
                var border = new Border { Child = MarkdownRenderer.Render(item.Block!) };
                border.Classes.Add("md-block");
                if (item.Kind == MdItemKind.Deleted)
                {
                    border.Classes.Add("md-deleted");
                    AutomationProperties.SetAutomationId(border, "md-deleted");
                }
                else if (item.Changed)
                {
                    border.Classes.Add("md-changed");
                    AutomationProperties.SetAutomationId(border, "md-changed");
                }
                else
                {
                    AutomationProperties.SetAutomationId(border, "md-unchanged");
                }

                return border;
            }
        }
    }

    private Control CreateFold(List<MdItem> hidden, string heading)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        var count = new TextBlock { Text = "↕ " + Loc.Instance.Format(MarkdownStrings.Folded, hidden.Count) };
        count.Classes.Add("md-fold-count");
        grid.Children.Add(count);
        if (heading.Length > 0)
        {
            var section = new TextBlock
            {
                Text = "§ " + heading,
                HorizontalAlignment = HorizontalAlignment.Right,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(16, 0, 0, 0),
            };
            section.Classes.Add("md-fold-section");
            Grid.SetColumn(section, 1);
            grid.Children.Add(section);
        }

        var button = new Button { Content = grid };
        button.Classes.Add("ghost");
        button.Classes.Add("md-fold");
        AutomationProperties.SetAutomationId(button, "md-fold");
        ToolTip.SetTip(button, Loc.Instance[MarkdownStrings.FoldedTip]);
        button.Click += (_, e) =>
        {
            e.Handled = true;
            var index = Root.Children.IndexOf(button);
            if (index < 0)
            {
                return;
            }

            Root.Children.RemoveAt(index);
            foreach (var item in hidden)
            {
                Root.Children.Insert(index++, Create(item));
            }

            RaiseLayoutHeightChanged();
        };
        return button;
    }
}
