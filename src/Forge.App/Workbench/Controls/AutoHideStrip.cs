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
/// The button strip an auto-hidden side region collapses to: one rotated button per panel title, sitting on the window
/// edge of the side of Main the region is docked on. Clicking a button opens the region's drawer.
/// </summary>
internal sealed class AutoHideStrip : Panel
{
    public static readonly StyledProperty<IBrush?> SeparatorBrushProperty =
        AvaloniaProperty.Register<AutoHideStrip, IBrush?>(nameof(SeparatorBrush));

    private readonly StackPanel buttons = new() { Orientation = Orientation.Vertical };
    private readonly ScrollViewer scroller;
    private readonly Border separator = new();
    private RegionEdge edge;

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
        WorkbenchResources.Link(this, BackgroundProperty, "SurfaceBackgroundBrush");
        WorkbenchResources.Link(separator, Border.BackgroundProperty, "BorderBrush");
    }

    public WorkbenchLayout? Layout { get; set; }

    public RegionRole Role { get; set; }

    /// <summary>The window edge the strip sits on; it decides which way the button text is rotated.</summary>
    public RegionEdge Edge
    {
        get => edge;
        set
        {
            edge = value;
            InvalidateVisual();
        }
    }

    public IBrush? SeparatorBrush
    {
        get => GetValue(SeparatorBrushProperty);
        set => SetValue(SeparatorBrushProperty, value);
    }

    /// <summary>Rebuilds the buttons; called when a panel is added, removed, hidden or renamed.</summary>
    public void Rebuild()
    {
        buttons.Children.Clear();
        AutomationProperties.SetAutomationId(this, "autohide-" + Role);
        if (Layout is not { } layout)
        {
            return;
        }

        var region = layout.GetRegion(Role);
        var angle = edge == RegionEdge.Left ? 90 : -90;
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
                Margin = new Thickness(0, 0, 2, 0),
            };
            ToolTip.SetTip(button, panel.Title);
            AutomationProperties.SetAutomationId(button, "autohide-" + Role + "-" + panel.Id);
            AutomationProperties.SetName(button, panel.Title);
            button.Click += OnButtonClick;
            var rotate = new LayoutTransformControl { LayoutTransform = new RotateTransform(angle) };
            rotate.Child = button;
            buttons.Children.Add(rotate);
        }
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var x = edge == RegionEdge.Left ? Math.Max(0, finalSize.Width - 1) : 0;
        separator.Arrange(new Rect(x, 0, 1, finalSize.Height));
        scroller.Arrange(new Rect(0, 0, finalSize.Width, finalSize.Height));
        return finalSize;
    }

    private void OnButtonClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        Layout?.ToggleDrawer(Role);
        e.Handled = true;
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
        WorkbenchResources.Link(this, BackgroundProperty, "SurfaceBackgroundBrush");
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
