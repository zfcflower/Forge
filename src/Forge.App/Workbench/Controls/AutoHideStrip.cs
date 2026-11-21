using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Forge.App.Workbench.Controls;

/// <summary>The text of a strip button: one 12 px semi-bold line, trimmed, so a long title does not stretch the strip.</summary>
internal static class WorkbenchStripText
{
    public static TextBlock Label(string title) => new()
    {
        Text = title,
        FontSize = 12,
        // Rotated 90 degrees on the window edge, the label is the strip's only content: bold, like the title of the panel
        // it belongs to (a SemiBold CJK face does not exist and resolved to bold anyway).
        FontWeight = FontWeight.Bold,
        TextWrapping = TextWrapping.NoWrap,
        TextTrimming = TextTrimming.CharacterEllipsis,
        MaxWidth = 180,
        VerticalAlignment = VerticalAlignment.Center,
    };
}

/// <summary>
/// The dock bar on the window edge of a side region (image-4 style): one rotated tab per panel title, each with a thick
/// indicator bar along its outer (window-edge) side. Strips are shown only for auto-hidden regions. Clicking a tab floats
/// that one panel in the drawer.
/// </summary>
internal sealed class AutoHideStrip : Panel
{
    public static readonly StyledProperty<IBrush?> SeparatorBrushProperty =
        AvaloniaProperty.Register<AutoHideStrip, IBrush?>(nameof(SeparatorBrush));

    private readonly StackPanel buttons = new() { Orientation = Orientation.Vertical, Spacing = 2, Margin = new Thickness(0, 4, 0, 4) };
    private readonly ScrollViewer scroller;
    private readonly Border separator = new();
    private readonly List<StripTab> tabs = [];
    private RegionEdge edge;
    private string? shownPanelId;

    /// <summary>The label's inset at both ends of a tab; the indicator bar is inset by the same amount, so its ends meet the
    /// text's ends instead of running past them (the label sits in a button whose padding is this number).</summary>
    private const double LabelInset = 10;

    /// <summary>Side margin of the indicator bar inside a tab.</summary>
    private const double BarInset = 1;

    /// <summary>One tab of the strip: the panel it stands for and the indicator bar beside its label.</summary>
    private readonly record struct StripTab(PanelModel Panel, Border Bar);

    public AutoHideStrip()
    {
        ClipToBounds = true;
        scroller = new ScrollViewer
        {
            Content = buttons,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Hidden,
        };
        Children.Add(scroller);
        Children.Add(separator);
        WorkbenchResources.Link(this, BackgroundProperty, "PanelTitleBarBrush");
        WorkbenchResources.Link(separator, Border.BackgroundProperty, "BorderBrush");
    }

    public WorkbenchLayout? Layout { get; set; }

    public RegionRole Role { get; set; }

    /// <summary>The window edge the strip sits on; it decides which side the indicator bars and separator are on.</summary>
    public RegionEdge Edge
    {
        get => edge;
        set
        {
            if (edge == value)
            {
                return;
            }

            edge = value;
            Rebuild();
        }
    }

    /// <summary>Id of the panel the drawer shows; only that one's indicator bar takes the active colour.</summary>
    public string? ShownPanelId
    {
        get => shownPanelId;
        set
        {
            if (string.Equals(shownPanelId, value, StringComparison.Ordinal))
            {
                return;
            }

            shownPanelId = value;
            UpdateBars();
        }
    }

    public IBrush? SeparatorBrush
    {
        get => GetValue(SeparatorBrushProperty);
        set => SetValue(SeparatorBrushProperty, value);
    }

    /// <summary>Rebuilds the tabs; called when a panel is added, removed, hidden or renamed.</summary>
    public void Rebuild()
    {
        buttons.Children.Clear();
        tabs.Clear();
        AutomationProperties.SetAutomationId(this, "autohide-" + Role);
        if (Layout is not { } layout)
        {
            return;
        }

        var region = layout.GetRegion(Role);
        for (var i = 0; i < region.Panels.Count; i++)
        {
            var panel = region.Panels[i];
            if (!panel.IsVisible)
            {
                continue;
            }

            var button = new AutoHideButton
            {
                Content = WorkbenchStripText.Label(panel.Title),
                // Rotated 90 degrees, the button's height becomes the tab's width: exactly what the strip leaves beside the
                // bar and its margins, so the label is neither clipped nor shifted away from the bar.
                Height = WorkbenchMetrics.AutoHideStripWidth
                    - WorkbenchMetrics.AutoHideStripSeparatorWidth
                    - WorkbenchMetrics.DockIndicatorWidth
                    - (2 * BarInset),
                Padding = new Thickness(LabelInset, 0),
            };
            ToolTip.SetTip(button, panel.Title);
            AutomationProperties.SetAutomationId(button, "autohide-" + Role + "-" + panel.Id);
            AutomationProperties.SetName(button, panel.Title);
            button.Click += (_, e) =>
            {
                e.Handled = true;
                Layout?.ToggleDrawer(panel);
            };

            // Text reads top to bottom on both edges, like the reference dock bars.
            var rotate = new LayoutTransformControl { LayoutTransform = new RotateTransform(90), Child = button };
            var bar = new Border
            {
                Width = WorkbenchMetrics.DockIndicatorWidth,
                CornerRadius = new CornerRadius(1.5),
                Margin = new Thickness(BarInset, LabelInset, BarInset, LabelInset),
            };
            tabs.Add(new StripTab(panel, bar));
            var tab = new DockPanel { LastChildFill = true };
            DockPanel.SetDock(bar, edge == RegionEdge.Left ? Dock.Left : Dock.Right);
            tab.Children.Add(bar);
            tab.Children.Add(rotate);
            buttons.Children.Add(tab);
        }

        UpdateBars();
    }

    private void UpdateBars()
    {
        for (var i = 0; i < tabs.Count; i++)
        {
            var shown = shownPanelId is { } id && string.Equals(tabs[i].Panel.Id, id, StringComparison.Ordinal);
            WorkbenchResources.Link(tabs[i].Bar, Border.BackgroundProperty, shown ? "BrandActionBrush" : "DockIndicatorIdleBrush");
        }
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var separatorWidth = WorkbenchMetrics.AutoHideStripSeparatorWidth;
        var x = edge == RegionEdge.Left ? Math.Max(0, finalSize.Width - separatorWidth) : 0;
        separator.Arrange(new Rect(x, 0, separatorWidth, finalSize.Height));
        var inner = edge == RegionEdge.Left
            ? new Rect(0, 0, Math.Max(0, finalSize.Width - separatorWidth), finalSize.Height)
            : new Rect(separatorWidth, 0, Math.Max(0, finalSize.Width - separatorWidth), finalSize.Height);
        scroller.Arrange(inner);
        return finalSize;
    }
}
