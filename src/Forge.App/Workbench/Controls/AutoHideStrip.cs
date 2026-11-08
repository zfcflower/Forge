using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Forge.App.Workbench.Controls;

/// <summary>The text of a strip button: one 12 px line, trimmed, so a long title does not stretch the strip.</summary>
internal static class WorkbenchStripText
{
    public static TextBlock Label(string title) => new()
    {
        Text = title,
        FontSize = 12,
        TextWrapping = TextWrapping.NoWrap,
        TextTrimming = TextTrimming.CharacterEllipsis,
        MaxWidth = 180,
        VerticalAlignment = VerticalAlignment.Center,
    };
}

/// <summary>
/// The dock bar on the window edge of a side region (image-4 style): one rotated tab per panel title, each with a thick
/// indicator bar along its outer (window-edge) side. Strips are shown only for auto-hidden regions. Clicking a tab toggles
/// that region's drawer.
/// </summary>
internal sealed class AutoHideStrip : Panel
{
    public static readonly StyledProperty<IBrush?> SeparatorBrushProperty =
        AvaloniaProperty.Register<AutoHideStrip, IBrush?>(nameof(SeparatorBrush));

    private readonly StackPanel buttons = new() { Orientation = Orientation.Vertical, Spacing = 2, Margin = new Thickness(0, 4, 0, 4) };
    private readonly ScrollViewer scroller;
    private readonly Border separator = new();
    private readonly List<Border> bars = [];
    private RegionEdge edge;
    private bool isRegionShown;

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
        WorkbenchResources.Link(this, BackgroundProperty, "DockStripBackgroundBrush");
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

    /// <summary>True while the auto-hidden region's drawer is open; the indicator bars follow it.</summary>
    public bool IsRegionShown
    {
        get => isRegionShown;
        set
        {
            if (isRegionShown == value)
            {
                return;
            }

            isRegionShown = value;
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
        bars.Clear();
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
            };
            ToolTip.SetTip(button, panel.Title);
            AutomationProperties.SetAutomationId(button, "autohide-" + Role + "-" + panel.Id);
            AutomationProperties.SetName(button, panel.Title);
            button.Click += OnButtonClick;

            // Text reads top to bottom on both edges, like the reference dock bars.
            var rotate = new LayoutTransformControl { LayoutTransform = new RotateTransform(90), Child = button };
            var bar = new Border
            {
                Width = WorkbenchMetrics.DockIndicatorWidth,
                CornerRadius = new CornerRadius(1.5),
                Margin = new Thickness(1, 2, 1, 2),
            };
            bars.Add(bar);
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
        for (var i = 0; i < bars.Count; i++)
        {
            WorkbenchResources.Link(bars[i], Border.BackgroundProperty, isRegionShown ? "DockIndicatorIdleBrush" : "AccentBrush");
        }
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var x = edge == RegionEdge.Left ? Math.Max(0, finalSize.Width - 1) : 0;
        separator.Arrange(new Rect(x, 0, 1, finalSize.Height));
        var inner = edge == RegionEdge.Left ? new Rect(0, 0, Math.Max(0, finalSize.Width - 1), finalSize.Height) : new Rect(1, 0, Math.Max(0, finalSize.Width - 1), finalSize.Height);
        scroller.Arrange(inner);
        return finalSize;
    }

    private void OnButtonClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        e.Handled = true;
        if (Layout is not { } layout)
        {
            return;
        }

        layout.ToggleDrawer(Role);
    }
}

/// <summary>
/// The strip the bottom tab group collapses to on Main's bottom edge: one button per tab title; clicking one brings the
/// group back at its remembered height.
/// </summary>
internal sealed class BottomCollapsedStrip : Panel
{
    public static readonly StyledProperty<IBrush?> SeparatorBrushProperty =
        AvaloniaProperty.Register<BottomCollapsedStrip, IBrush?>(nameof(SeparatorBrush));

    private readonly StackPanel buttons = new() { Orientation = Orientation.Horizontal };
    private readonly ScrollViewer scroller;
    private readonly Border separator = new();

    public BottomCollapsedStrip()
    {
        ClipToBounds = true;
        scroller = new ScrollViewer
        {
            Content = buttons,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Hidden,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
        };
        Children.Add(scroller);
        Children.Add(separator);
        // The collapsed bottom group is a dock strip like the side ones, so it wears the same chrome colour.
        WorkbenchResources.Link(this, BackgroundProperty, "DockStripBackgroundBrush");
        WorkbenchResources.Link(separator, Border.BackgroundProperty, "BorderBrush");
    }

    public WorkbenchLayout? Layout { get; set; }

    public IBrush? SeparatorBrush
    {
        get => GetValue(SeparatorBrushProperty);
        set => SetValue(SeparatorBrushProperty, value);
    }

    public void Rebuild()
    {
        buttons.Children.Clear();
        if (Layout is not { } layout)
        {
            return;
        }

        var group = layout.BottomTabs;
        var added = false;
        for (var i = 0; i < group.Tabs.Count; i++)
        {
            var tab = group.Tabs[i];
            if (tab.IsFloating)
            {
                continue;
            }

            added = true;
            var button = new AutoHideButton
            {
                Content = WorkbenchStripText.Label(tab.Title),
                Margin = new Thickness(0, 0, 2, 0),
            };
            ToolTip.SetTip(button, tab.Title);
            AutomationProperties.SetAutomationId(button, "autohide-bottom-" + tab.Id);
            AutomationProperties.SetName(button, tab.Title);
            var captured = tab;
            button.Click += (_, args) => OnTabClick(captured, args);
            buttons.Children.Add(button);
        }

        if (!added && group.KeepsVisibleWhenEmpty)
        {
            var button = new AutoHideButton
            {
                Content = WorkbenchStripText.Label(group.Title),
                Margin = new Thickness(0, 0, 2, 0),
            };
            ToolTip.SetTip(button, group.Title);
            AutomationProperties.SetAutomationId(button, "autohide-bottom-expand");
            AutomationProperties.SetName(button, group.Title);
            button.Click += OnExpandClick;
            buttons.Children.Add(button);
        }
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        separator.Arrange(new Rect(0, 0, finalSize.Width, 1));
        scroller.Arrange(new Rect(0, 0, finalSize.Width, finalSize.Height));
        return finalSize;
    }

    private void OnTabClick(TabModel tab, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (Layout is not { } layout)
        {
            return;
        }

        layout.ActivateTab(tab);
        layout.IsBottomCollapsed = false;
        e.Handled = true;
    }

    private void OnExpandClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (Layout is not { } layout)
        {
            return;
        }

        layout.IsBottomCollapsed = false;
        e.Handled = true;
    }
}
