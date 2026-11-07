using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Forge.App.Workbench;
using Forge.App.Workbench.Controls;
using Xunit;

namespace Forge.App.Tests.Workbench;

/// <summary>
/// Pins the rule behind both self-drawn captions (forge-app spec, "浮动窗口"): the main window and the floating panel drop
/// the system title bar (<c>WindowDecorations="BorderOnly"</c>), so a left press on the window's own title row has to
/// start the move, a double press maximizes, and a press that lands on a button keeps its own clicks. The floating
/// window used to have no pointer handling at all, so a new window could not be dragged; the wiring tests below press
/// the real headless title row and watch the <see cref="WindowCaption.BeginMove"/> seam, which is the only observable
/// effect of a move — the headless platform does not move windows. The main window's half of the rule is pinned by
/// routing a press from its XAML-declared title row, because the headless platform never delivers a press into a
/// <see cref="MainWindow"/>'s content. The title row is the only hot zone: the files-panel window keeps its preview tab
/// strip free of window dragging.
/// </summary>
[Collection("forge-app-ui")]
public sealed class WindowCaptionTests
{
    private const double OwnerWidth = 1200;
    private const double OwnerHeight = 900;

    /// <summary>The middle of the 28 px title row; the floating window clips that row with a 1 px frame.</summary>
    private const double TitleBarPressY = 14;

    /// <summary>The middle of the main window's 40 px title row.</summary>
    private const double MainTitleRowPressY = 20;

    /// <summary>A point inside the main title row, for the synthetic press's root-visual position.</summary>
    private const double SourcePressX = 60;

    /// <summary>How far inside the tab strip's right edge a press lands on blank strip rather than on a tab header.</summary>
    private const double StripBlankMargin = 4;

    [Theory]
    [InlineData(false, 0, false, (int)CaptionPress.None)]
    [InlineData(false, 1, false, (int)CaptionPress.None)]
    [InlineData(false, 2, false, (int)CaptionPress.None)]
    [InlineData(false, 3, false, (int)CaptionPress.None)]
    [InlineData(false, 0, true, (int)CaptionPress.None)]
    [InlineData(false, 1, true, (int)CaptionPress.None)]
    [InlineData(false, 2, true, (int)CaptionPress.None)]
    [InlineData(false, 3, true, (int)CaptionPress.None)]
    [InlineData(true, 0, true, (int)CaptionPress.None)]
    [InlineData(true, 1, true, (int)CaptionPress.None)]
    [InlineData(true, 2, true, (int)CaptionPress.None)]
    [InlineData(true, 3, true, (int)CaptionPress.None)]
    [InlineData(true, 0, false, (int)CaptionPress.Move)]
    [InlineData(true, 1, false, (int)CaptionPress.Move)]
    [InlineData(true, 2, false, (int)CaptionPress.ToggleMaximize)]
    [InlineData(true, 3, false, (int)CaptionPress.Move)]
    public void A_caption_press_classifies_by_button_click_count_and_interactive_source(
        bool leftButtonPressed,
        int clickCount,
        bool interactive,
        int expected) =>
        Assert.Equal((CaptionPress)expected, WindowCaption.Classify(leftButtonPressed, clickCount, interactive));

    [Fact]
    public async Task Toggling_maximize_alternates_a_window_between_normal_and_maximized()
    {
        var session = SharedUi.Session;
        var states = await session.Dispatch(
            () =>
            {
                var window = new Window { Width = 500, Height = 400 };
                try
                {
                    window.Show();
                    var before = window.WindowState;
                    WindowCaption.ToggleMaximize(window);
                    var maximized = window.WindowState;
                    WindowCaption.ToggleMaximize(window);
                    var restored = window.WindowState;
                    return (Before: before, Maximized: maximized, Restored: restored);
                }
                finally
                {
                    window.Close();
                }
            },
            CancellationToken.None);

        Assert.Equal(WindowState.Normal, states.Before);
        Assert.Equal(WindowState.Maximized, states.Maximized);
        Assert.Equal(WindowState.Normal, states.Restored);
    }

    [Fact]
    public async Task A_left_press_on_the_floating_title_bar_starts_a_window_move()
    {
        var session = SharedUi.Session;
        var result = await session.Dispatch(
            () =>
            {
                var scene = OpenFloatingWindow();
                var original = WindowCaption.BeginMove;
                var moved = new List<Window>();
                WindowCaption.BeginMove = (movedWindow, _) => moved.Add(movedWindow);
                try
                {
                    // The left half of the title row, away from the dock button in the right-hand column.
                    var at = new Point(scene.Window.Bounds.Width / 4, TitleBarPressY);
                    scene.Window.MouseDown(at, MouseButton.Left);
                    scene.Window.MouseUp(at, MouseButton.Left);
                    Settle();
                    return (scene.Window, Moved: moved.ToArray());
                }
                finally
                {
                    WindowCaption.BeginMove = original;
                    scene.Window.Close();
                    scene.Owner.Close();
                }
            },
            CancellationToken.None);

        Assert.Same(result.Window, Assert.Single(result.Moved));
    }

    [Fact]
    public async Task A_press_on_the_floating_dock_button_keeps_its_clicks_and_starts_no_move()
    {
        var session = SharedUi.Session;
        var result = await session.Dispatch(
            () =>
            {
                var scene = OpenFloatingWindow();
                var dock = DockButton(scene.Window, scene.Item);
                var centre = CentreOf(dock, scene.Window);
                var original = WindowCaption.BeginMove;
                var moved = new List<Window>();
                WindowCaption.BeginMove = (movedWindow, _) => moved.Add(movedWindow);
                try
                {
                    var bounds = dock.Bounds;
                    scene.Window.MouseDown(centre, MouseButton.Left);
                    scene.Window.MouseUp(centre, MouseButton.Left);
                    Settle();
                    return (DockBounds: bounds, Moved: moved.ToArray(), Docked: !scene.Layout.Floating.Contains(scene.Item));
                }
                finally
                {
                    WindowCaption.BeginMove = original;
                    scene.Window.Close();
                    scene.Owner.Close();
                }
            },
            CancellationToken.None);

        // A zero-sized button would put the press on the title bar instead, proving nothing.
        Assert.True(result.DockBounds.Width > 0 && result.DockBounds.Height > 0, "the dock button must be laid out");
        Assert.True(result.Docked, "the press must reach the dock button, which docks the item back");
        Assert.Empty(result.Moved);
    }

    [Fact]
    public async Task A_double_press_on_the_floating_title_bar_maximizes_the_window()
    {
        var session = SharedUi.Session;
        var result = await session.Dispatch(
            () =>
            {
                var scene = OpenFloatingWindow();
                var clicks = new List<int>();
                void RecordClickCount(object? sender, PointerPressedEventArgs e) => clicks.Add(e.ClickCount);
                scene.Window.AddHandler(InputElement.PointerPressedEvent, RecordClickCount, RoutingStrategies.Bubble, handledEventsToo: true);

                var original = WindowCaption.BeginMove;
                var moved = new List<Window>();
                WindowCaption.BeginMove = (movedWindow, _) => moved.Add(movedWindow);
                try
                {
                    // Two press/release cycles at one point: the platform reports the second one with ClickCount 2.
                    // No layout or render pass in between, so the pair stays inside the platform's double-click time.
                    var at = new Point(scene.Window.Bounds.Width / 4, TitleBarPressY);
                    scene.Window.MouseDown(at, MouseButton.Left);
                    scene.Window.MouseUp(at, MouseButton.Left);
                    scene.Window.MouseDown(at, MouseButton.Left);
                    scene.Window.MouseUp(at, MouseButton.Left);
                    Settle();
                    return (
                        Clicks: clicks.ToArray(),
                        State: scene.Window.WindowState,
                        Geometry: scene.Item.Geometry,
                        Moved: moved.ToArray());
                }
                finally
                {
                    WindowCaption.BeginMove = original;
                    scene.Window.RemoveHandler(InputElement.PointerPressedEvent, RecordClickCount);
                    scene.Window.Close();
                    scene.Owner.Close();
                }
            },
            CancellationToken.None);

        Assert.Equal(new[] { 1, 2 }, result.Clicks);
        Assert.Equal(WindowState.Maximized, result.State);
        Assert.Single(result.Moved);

        // The maximize flag is what brings the window back maximized after a restart (forge-app spec, "浮动窗口").
        Assert.True(result.Geometry.IsMaximized);
    }

    [Fact]
    public async Task Only_the_title_bar_of_the_floating_files_panel_drags_its_window()
    {
        var session = SharedUi.Session;
        var result = await session.Dispatch(
            () =>
            {
                var scene = OpenFloatingFilesPanel();
                var strip = scene.Window.GetVisualDescendants().OfType<PanelTabStrip>().Single();
                var stripPoint = PointIn(strip, new Point(strip.Bounds.Width - StripBlankMargin, strip.Bounds.Height / 2), scene.Window);
                var titlePoint = new Point(scene.Window.Bounds.Width / 4, TitleBarPressY);
                var original = WindowCaption.BeginMove;
                var moved = new List<Window>();
                WindowCaption.BeginMove = (movedWindow, _) => moved.Add(movedWindow);
                try
                {
                    // The preview group's tab strip is a body element: its blank space must keep the tab gestures.
                    scene.Window.MouseDown(stripPoint, MouseButton.Left);
                    scene.Window.MouseUp(stripPoint, MouseButton.Left);
                    Settle();
                    var afterStrip = moved.ToArray();

                    scene.Window.MouseDown(titlePoint, MouseButton.Left);
                    scene.Window.MouseUp(titlePoint, MouseButton.Left);
                    Settle();
                    return (StripBounds: strip.Bounds, AfterStrip: afterStrip, AfterTitle: moved.ToArray());
                }
                finally
                {
                    WindowCaption.BeginMove = original;
                    scene.Window.Close();
                    scene.Owner.Close();
                }
            },
            CancellationToken.None);

        // A collapsed column would put the strip press on the title bar, proving nothing.
        Assert.True(result.StripBounds.Width > 0 && result.StripBounds.Height > 0, "the preview tab strip must be laid out");
        Assert.Empty(result.AfterStrip);
        Assert.Single(result.AfterTitle);
    }

    [Fact]
    public async Task The_main_window_title_row_hands_its_press_to_the_caption_rule()
    {
        var session = SharedUi.Session;
        var result = await session.Dispatch(
            () =>
            {
                var window = new MainWindow();
                var moved = new List<Window>();
                var original = WindowCaption.BeginMove;
                try
                {
                    window.Show();
                    Settle();
                    var bar = window.GetVisualDescendants().OfType<Border>().Single(border => border.Name == "TitleBar");
                    var search = window.GetVisualDescendants().OfType<TextBox>().Single(box => box.Name == "SearchBox");
                    WindowCaption.BeginMove = (movedWindow, _) => moved.Add(movedWindow);

                    RaisePress(bar, window);
                    var afterTitleRow = moved.ToArray();

                    // The placeholder search box lives in the title row but owns its own presses.
                    RaisePress(search, window);
                    return (AfterTitleRow: afterTitleRow, AfterSearch: moved.Count);
                }
                finally
                {
                    WindowCaption.BeginMove = original;
                    window.Close();
                }
            },
            CancellationToken.None);

        Assert.IsType<MainWindow>(Assert.Single(result.AfterTitleRow));
        Assert.Equal(1, result.AfterSearch);
    }

    private static (Window Owner, FloatingPanelWindow Window, FloatingItem Item, WorkbenchLayout Layout) OpenFloatingWindow()
    {
        var owner = new Window { Width = OwnerWidth, Height = OwnerHeight };
        owner.Show();
        var layout = new WorkbenchLayout(new object(), new object(), new object(), new object(), new object());
        layout.SetWindowWidth(OwnerWidth);
        var tab = layout.OpenTab(layout.PreviewTabs, WorkbenchIds.PreviewTab, TabKind.FilePreview, new object(), "file");
        var item = layout.FloatTab(tab);
        var window = new FloatingPanelWindow(layout, item);
        window.Show(owner);
        Settle();
        return (owner, window, item, layout);
    }

    private static (Window Owner, FloatingPanelWindow Window, FloatingItem Item, WorkbenchLayout Layout) OpenFloatingFilesPanel()
    {
        var owner = new Window { Width = OwnerWidth, Height = OwnerHeight };
        owner.Show();
        var layout = new WorkbenchLayout(new object(), new object(), new object(), new object(), new object());
        layout.SetWindowWidth(OwnerWidth);

        // The files panel floats with the preview group on its left, so a docked preview tab is what keeps that column alive.
        layout.OpenTab(layout.PreviewTabs, WorkbenchIds.PreviewTab, TabKind.FilePreview, new object(), "file");
        var item = layout.FloatFilesPanel();
        var window = new FloatingPanelWindow(layout, item);
        window.Show(owner);
        Settle();
        return (owner, window, item, layout);
    }

    private static Button DockButton(FloatingPanelWindow window, FloatingItem item)
    {
        var id = "floating-" + item.Id + "-dock";
        return window.GetVisualDescendants()
            .OfType<Button>()
            .Single(button => AutomationProperties.GetAutomationId(button) == id);
    }

    private static Point CentreOf(Control control, Window window) =>
        PointIn(control, new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window);

    private static Point PointIn(Control control, Point local, Window window) =>
        control.TranslatePoint(local, window)
        ?? throw new InvalidOperationException("The control is not in the floating window's visual tree.");

    /// <summary>
    /// Routes one synthetic left press from <paramref name="source"/> up through the window, exercising the XAML-declared
    /// handler and its delegation. The headless platform never delivers a press to a <see cref="MainWindow"/>'s content —
    /// its window-level hit test stops at the host border — so raising the press on the title row is what a test can do
    /// there; the floating windows are pressed for real above.
    /// </summary>
    private static PointerPressedEventArgs RaisePress(Control source, Window window)
    {
        var args = new PointerPressedEventArgs(
            source,
            new Pointer(1, PointerType.Mouse, isPrimary: true),
            window,
            new Point(SourcePressX, MainTitleRowPressY),
            timestamp: 1,
            new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed),
            KeyModifiers.None,
            clickCount: 1);
        source.RaiseEvent(args);
        return args;
    }

    /// <summary>
    /// Drains the dispatcher and renders, twice: hit testing sees a control only after a render pass, and a layout change
    /// can post follow-up work that only exists after the first pass. Headless rendering runs only when this is called.
    /// </summary>
    private static void Settle()
    {
        for (var pass = 0; pass < 2; pass++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }

        Dispatcher.UIThread.RunJobs();
    }
}
