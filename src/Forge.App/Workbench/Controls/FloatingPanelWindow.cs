using System.Collections;
using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using Forge.App.Localization;

namespace Forge.App.Workbench.Controls;

/// <summary>
/// A window of its own for one <see cref="FloatingItem"/>: a tab, or the files panel with the preview group on its left.
/// The window owns only geometry and the dock button — content comes from the same view models and data templates.
/// </summary>
internal sealed class FloatingPanelWindow : Window
{
    private readonly WorkbenchLayout layout;
    private readonly FloatingItem item;
    private ContentControl? tabContent;
    private ContentControl? previewContent;
    private Border? previewColumn;
    private PanelIconButton? dockButton;
    private TextBlock? titleLabel;
    private TabGroupModel? watchedPreview;
    private TabModel? watchedTab;
    private readonly List<TabModel> watchedPreviewTabs = [];
    private bool geometryReady;
    private int uiThread;

    public FloatingPanelWindow(WorkbenchLayout layout, FloatingItem item)
    {
        this.layout = layout;
        this.item = item;
        Title = WindowTitle;
        WindowDecorations = WindowDecorations.BorderOnly;
        ShowInTaskbar = false;
        CanResize = true;
        MinWidth = 240;
        MinHeight = 160;
        AutomationProperties.SetAutomationId(this, "floating-" + item.Id);
        AutomationProperties.SetName(this, Title);
        WorkbenchResources.Link(this, BackgroundProperty, "SurfaceBackgroundBrush");

        var geometry = item.Geometry;
        Width = geometry.Width > 0 ? geometry.Width : WorkbenchMetrics.FloatingTabWidth;
        Height = geometry.Height > 0 ? geometry.Height : WorkbenchMetrics.FloatingTabHeight;
        WindowStartupLocation = geometry.IsUnplaced ? WindowStartupLocation.CenterOwner : WindowStartupLocation.Manual;
        Content = BuildContent();
        PositionChanged += OnPositionChanged;
    }

    /// <summary>True from the moment the user closes the window: the host then only drops its tracking entry.</summary>
    public bool IsClosingSelf { get; private set; }

    /// <summary>Starts closing without docking the item back; used when the workbench host goes away.</summary>
    public void CloseWithoutDocking()
    {
        IsClosingSelf = true;
        Close();
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        if (!item.Geometry.IsUnplaced)
        {
            PlaceAt(item.Geometry.X, item.Geometry.Y);
        }

        EnsureOnScreen();
        if (item.Geometry.IsMaximized)
        {
            WindowState = WindowState.Maximized;
        }

        geometryReady = true;
        uiThread = Environment.CurrentManagedThreadId;
        Loc.Instance.LanguageChanged += OnLanguageChanged;
    }

    protected override void OnClosed(EventArgs e)
    {
        Loc.Instance.LanguageChanged -= OnLanguageChanged;

        if (watchedTab is not null)
        {
            watchedTab.PropertyChanged -= OnTabChanged;
            watchedTab = null;
        }

        if (watchedPreview is not null)
        {
            watchedPreview.PropertyChanged -= OnPreviewChanged;
            watchedPreview.Tabs.CollectionChanged -= OnPreviewTabsChanged;
            UnsubscribePreviewTabs();
            watchedPreview = null;
        }

        base.OnClosed(e);
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (e.Cancel)
        {
            return;
        }

        IsClosingSelf = true;
        if (!layout.IsShuttingDown)
        {
            layout.Dock(item);
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (!geometryReady)
        {
            return;
        }

        if (change.Property == BoundsProperty || change.Property == WindowStateProperty)
        {
            CaptureGeometry();
        }
    }

    private Control BuildContent()
    {
        var grid = new Grid { RowDefinitions = new RowDefinitions("28,*") };
        grid.Children.Add(BuildTitleBar());
        var body = item.Tab is { } tab ? BuildTabBody(tab) : BuildFilesBody();
        Grid.SetRow(body, 1);
        grid.Children.Add(body);

        // A 1 px frame around the whole floating window: with BorderOnly decorations the OS chrome is barely
        // visible, so the frame is what separates the floating panel from whatever is behind it.
        var frame = new Border { Child = grid, BorderThickness = new Thickness(1) };
        WorkbenchResources.Link(frame, Border.BorderBrushProperty, "BorderBrush");
        return frame;
    }

    private Control BuildTitleBar()
    {
        var bar = new Border
        {
            Height = PanelChrome.TitleBarHeight,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(8, 0, 4, 0),
        };
        WorkbenchResources.Link(bar, Border.BackgroundProperty, "SurfaceBackgroundBrush");
        WorkbenchResources.Link(bar, Border.BorderBrushProperty, "BorderBrush");

        var label = new TextBlock
        {
            Text = Title,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
        };
        WorkbenchResources.Link(label, TextBlock.ForegroundProperty, "TextSecondaryBrush");

        var dock = new PanelIconButton
        {
            Icon = WorkbenchIcons.Find(Application.Current, "IconDockBack"),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTip.SetTip(dock, ShellStrings.DockBack);
        AutomationProperties.SetAutomationId(dock, "floating-" + item.Id + "-dock");
        AutomationProperties.SetName(dock, ShellStrings.DockBack);
        dock.Click += OnDockClick;
        dockButton = dock;
        titleLabel = label;

        var filesPanel = item.FilesPanel;
        var content = new Grid { ColumnDefinitions = new ColumnDefinitions(filesPanel is null ? "*,Auto" : "*,Auto,Auto") };
        content.Children.Add(label);

        var dockColumn = 1;
        if (filesPanel is not null)
        {
            var tools = new ItemsControl
            {
                ItemsSource = filesPanel.HeaderTools,
                ItemTemplate = new HeaderToolTemplate(),
                Margin = new Thickness(0, 0, 4, 0),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                ItemsPanel = new FuncTemplate<Panel?>(() => new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 2,
                }),
            };
            Grid.SetColumn(tools, 1);
            content.Children.Add(tools);
            dockColumn = 2;
        }

        Grid.SetColumn(dock, dockColumn);
        content.Children.Add(dock);
        bar.Child = content;

        // The window drops the system title bar, so this bar is what drags it and what a double click maximizes.
        bar.PointerPressed += OnTitleBarPointerPressed;
        return bar;
    }

    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e) => WindowCaption.Handle(this, e);

    private Control BuildTabBody(TabModel tab)
    {
        tabContent = new ContentControl
        {
            Content = tab.Content,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch,
        };
        tab.PropertyChanged += OnTabChanged;
        watchedTab = tab;
        return tabContent;
    }

    private Control BuildFilesBody()
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };

        if (item.PreviewTabs is { } preview)
        {
            previewContent = new ContentControl
            {
                Content = preview.ActiveTab?.Content,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Stretch,
            };
            var strip = new PanelTabStrip { Layout = layout, Group = preview, Height = PanelChrome.TitleBarHeight };
            var column = new Grid { RowDefinitions = new RowDefinitions("28,*") };
            column.Children.Add(strip);
            Grid.SetRow(previewContent, 1);
            column.Children.Add(previewContent);

            previewColumn = new Border
            {
                Child = column,
                Width = layout.PreviewWidth > 0 ? layout.PreviewWidth : WorkbenchMetrics.DefaultPreviewWidth,
                BorderThickness = new Thickness(0, 0, 1, 0),
                IsVisible = preview.HasDockedTab,
            };
            WorkbenchResources.Link(previewColumn, Border.BorderBrushProperty, "BorderBrush");
            preview.PropertyChanged += OnPreviewChanged;
            preview.Tabs.CollectionChanged += OnPreviewTabsChanged;
            watchedPreview = preview;
            SubscribePreviewTabs(preview.Tabs);
            grid.Children.Add(previewColumn);
        }

        var files = new ContentControl
        {
            Content = item.FilesPanel?.Content,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch,
        };
        Grid.SetColumn(files, 1);
        grid.Children.Add(files);
        return grid;
    }

    private void OnDockClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        e.Handled = true;
        layout.Dock(item);
    }

    /// <summary>
    /// The dock button's text and the files panel's heading are built in code, so this window reads them again on a
    /// language switch; a tab's title is content and is left as it is (the files panel has no such title).
    /// </summary>
    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        if (Environment.CurrentManagedThreadId != uiThread)
        {
            return;
        }

        Title = WindowTitle;
        AutomationProperties.SetName(this, WindowTitle);

        if (dockButton is { } button)
        {
            ToolTip.SetTip(button, ShellStrings.DockBack);
            AutomationProperties.SetName(button, ShellStrings.DockBack);
        }

        if (item.Tab is null && titleLabel is not null)
        {
            titleLabel.Text = WindowTitle;
        }
    }

    private string WindowTitle => item.Tab?.Title ?? ShellStrings.FilesTitle;

    private void OnTabChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not TabModel tab)
        {
            return;
        }

        if (e.PropertyName == nameof(TabModel.Title))
        {
            Title = tab.Title;
            AutomationProperties.SetName(this, tab.Title);
        }

        if (tabContent is not null && e.PropertyName == nameof(TabModel.Content))
        {
            tabContent.Content = tab.Content;
        }
    }

    private void OnPreviewChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (previewContent is not null && watchedPreview is not null && e.PropertyName == nameof(TabGroupModel.ActiveTab))
        {
            previewContent.Content = watchedPreview.ActiveTab?.Content;
        }
    }

    private void OnPreviewTabsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add:
                SubscribePreviewTabs(e.NewItems);
                break;
            case NotifyCollectionChangedAction.Remove:
                UnsubscribePreviewTabs(e.OldItems);
                break;
            case NotifyCollectionChangedAction.Replace:
                UnsubscribePreviewTabs(e.OldItems);
                SubscribePreviewTabs(e.NewItems);
                break;
            case NotifyCollectionChangedAction.Reset:
                UnsubscribePreviewTabs();
                SubscribePreviewTabs(watchedPreview?.Tabs);
                break;
        }

        RefreshPreviewVisibility();
    }

    private void OnPreviewTabChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TabModel.IsFloating))
        {
            RefreshPreviewVisibility();
        }
    }

    private void RefreshPreviewVisibility()
    {
        if (previewColumn is not null && watchedPreview is not null)
        {
            previewColumn.IsVisible = watchedPreview.HasDockedTab;
        }
    }

    private void SubscribePreviewTabs(IList? tabs)
    {
        if (tabs is null)
        {
            return;
        }

        for (var i = 0; i < tabs.Count; i++)
        {
            if (tabs[i] is TabModel tab)
            {
                tab.PropertyChanged += OnPreviewTabChanged;
                watchedPreviewTabs.Add(tab);
            }
        }
    }

    private void UnsubscribePreviewTabs(IList? tabs)
    {
        if (tabs is null)
        {
            return;
        }

        for (var i = 0; i < tabs.Count; i++)
        {
            if (tabs[i] is TabModel tab)
            {
                tab.PropertyChanged -= OnPreviewTabChanged;
                watchedPreviewTabs.Remove(tab);
            }
        }
    }

    private void UnsubscribePreviewTabs()
    {
        for (var i = 0; i < watchedPreviewTabs.Count; i++)
        {
            watchedPreviewTabs[i].PropertyChanged -= OnPreviewTabChanged;
        }

        watchedPreviewTabs.Clear();
    }

    private void OnPositionChanged(object? sender, PixelPointEventArgs e) => CaptureGeometry();

    private void PlaceAt(double x, double y)
    {
        var scaling = ScreenScalingAt(x, y);
        Position = new PixelPoint((int)Math.Round(x * scaling), (int)Math.Round(y * scaling));
    }

    private double ScreenScalingAt(double x, double y)
    {
        var screens = Screens;
        if (screens is null)
        {
            return 1;
        }

        var current = RenderScaling > 0 ? RenderScaling : 1;
        var point = new PixelPoint((int)Math.Round(x * current), (int)Math.Round(y * current));
        var screen = screens.ScreenFromPoint(point) ?? screens.Primary;
        return screen is { Scaling: > 0 } ? screen.Scaling : 1;
    }

    /// <summary>Moves the window onto the owner's screen when its rectangle misses every working area.</summary>
    private void EnsureOnScreen()
    {
        if (Screens is not { } screens)
        {
            return;
        }

        var geometry = item.Geometry;
        var scaling = RenderScaling > 0 ? RenderScaling : 1;
        var rect = new Rect(
            double.IsNaN(geometry.X) ? Position.X / scaling : geometry.X,
            double.IsNaN(geometry.Y) ? Position.Y / scaling : geometry.Y,
            Width,
            Height);
        for (var i = 0; i < screens.All.Count; i++)
        {
            if (WorkingAreaOf(screens.All[i]).Intersects(rect))
            {
                return;
            }
        }

        var ownerScreen = Owner is { } owner ? screens.ScreenFromWindow(owner) : null;
        var target = ownerScreen ?? screens.Primary;
        if (target is null)
        {
            return;
        }

        var area = WorkingAreaOf(target);
        var targetScaling = target.Scaling > 0 ? target.Scaling : 1;
        var x = area.X + ((area.Width - Width) / 2);
        var y = area.Y + ((area.Height - Height) / 2);
        Position = new PixelPoint((int)Math.Round(x * targetScaling), (int)Math.Round(y * targetScaling));
        item.Geometry = new FloatGeometry(x, y, geometry.Width, geometry.Height, geometry.IsMaximized);
    }

    private static Rect WorkingAreaOf(Screen screen)
    {
        var scaling = screen.Scaling > 0 ? screen.Scaling : 1;
        var area = screen.WorkingArea;
        return new Rect(area.X / scaling, area.Y / scaling, area.Width / scaling, area.Height / scaling);
    }

    private void CaptureGeometry()
    {
        if (WindowState == WindowState.Minimized)
        {
            return;
        }

        var geometry = item.Geometry;
        var maximized = WindowState == WindowState.Maximized;
        var scaling = RenderScaling > 0 ? RenderScaling : 1;
        var x = geometry.X;
        var y = geometry.Y;
        var width = geometry.Width;
        var height = geometry.Height;
        if (!maximized)
        {
            var position = Position;
            x = position.X / scaling;
            y = position.Y / scaling;
            if (Bounds.Width > 0)
            {
                width = Bounds.Width;
            }

            if (Bounds.Height > 0)
            {
                height = Bounds.Height;
            }
        }

        var next = new FloatGeometry(x, y, width, height, maximized);
        if (next != geometry)
        {
            item.Geometry = next;
        }
    }
}
