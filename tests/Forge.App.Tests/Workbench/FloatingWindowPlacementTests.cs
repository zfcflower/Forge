using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Forge.App.Workbench;
using Forge.App.Workbench.Controls;
using Xunit;

namespace Forge.App.Tests.Workbench;

/// <summary>
/// Pins the placement rules of <see cref="FloatingPanelWindow"/> (forge-app spec, "浮动窗口"): a window whose saved
/// rectangle misses every screen is moved onto the screen the main window is on, a rectangle inside the screen is left
/// alone, and a window that was never placed is centred over its owner.
///
/// The headless platform reports a single screen at the origin, so the multi-monitor half of the rule (the owner's
/// screen wins over the primary) cannot be exercised here; the off-screen case still proves the window does not stay
/// where the snapshot put it.
/// </summary>
[Collection("forge-app-ui")]
public sealed class FloatingWindowPlacementTests
{
    private const double OwnerWidth = 1200;
    private const double OwnerHeight = 900;
    private const double FloatingWidth = WorkbenchMetrics.FloatingTabWidth;
    private const double FloatingHeight = WorkbenchMetrics.FloatingTabHeight;
    private const double InsideX = 100;
    private const double InsideY = 120;
    private const double OffScreenMargin = 500;

    [Fact]
    public async Task A_window_whose_rectangle_misses_every_screen_is_centred_on_the_owner_screen()
    {
        var session = SharedUi.Session;
        var (position, expected, area) = await session.Dispatch(
            () =>
            {
                var scene = Scene();
                var offScreen = BelowAndRightOf(WorkingAreaOf(scene.Owner));
                var floating = Float(scene.Layout, offScreen);
                var window = Show(scene.Layout, floating, scene.Owner);
                var area = WorkingAreaOf(scene.Owner);
                var result = (window.Position, CentredIn(area, FloatingWidth, FloatingHeight), area);
                window.Close();
                return result;
            },
            CancellationToken.None);

        Assert.True(area.Width > 0 && area.Height > 0, "the headless platform must report a usable screen");
        Assert.Equal(expected, position);
    }

    [Fact]
    public async Task A_window_that_misses_every_screen_writes_the_placed_position_back_to_its_geometry()
    {
        var session = SharedUi.Session;
        var (geometry, expected) = await session.Dispatch(
            () =>
            {
                var scene = Scene();
                var floating = Float(scene.Layout, BelowAndRightOf(WorkingAreaOf(scene.Owner)));
                var window = Show(scene.Layout, floating, scene.Owner);
                var centred = CentredIn(WorkingAreaOf(scene.Owner), FloatingWidth, FloatingHeight);
                var result = (
                    floating.Geometry,
                    new FloatGeometry(centred.X, centred.Y, FloatingWidth, FloatingHeight, IsMaximized: false));
                window.Close();
                return result;
            },
            CancellationToken.None);

        Assert.False(geometry.IsUnplaced);
        Assert.Equal(expected, geometry);
    }

    [Fact]
    public async Task A_window_inside_the_screen_keeps_its_saved_position()
    {
        var session = SharedUi.Session;
        var (position, geometry) = await session.Dispatch(
            () =>
            {
                var scene = Scene();
                var floating = Float(scene.Layout, new FloatGeometry(InsideX, InsideY, FloatingWidth, FloatingHeight, IsMaximized: false));
                var window = Show(scene.Layout, floating, scene.Owner);
                var result = (window.Position, floating.Geometry);
                window.Close();
                return result;
            },
            CancellationToken.None);

        Assert.Equal(new PixelPoint((int)InsideX, (int)InsideY), position);
        Assert.Equal(InsideX, geometry.X);
        Assert.Equal(InsideY, geometry.Y);
    }

    [Fact]
    public async Task A_window_that_was_never_placed_is_centred_over_the_owner()
    {
        var session = SharedUi.Session;
        var (windowCentre, ownerCentre) = await session.Dispatch(
            () =>
            {
                var scene = Scene();
                var unplaced = FloatUnplaced(scene.Layout);
                var window = Show(scene.Layout, unplaced, scene.Owner);
                var result = (
                    new Point(window.Position.X + (window.Bounds.Width / 2), window.Position.Y + (window.Bounds.Height / 2)),
                    new Point(
                        scene.Owner.Position.X + (scene.Owner.Bounds.Width / 2),
                        scene.Owner.Position.Y + (scene.Owner.Bounds.Height / 2)));
                window.Close();
                return result;
            },
            CancellationToken.None);

        Assert.Equal(ownerCentre.X, windowCentre.X);
        Assert.Equal(ownerCentre.Y, windowCentre.Y);
    }

    [Fact]
    public async Task A_window_that_was_never_placed_records_a_placed_position()
    {
        var session = SharedUi.Session;
        var geometry = await session.Dispatch(
            () =>
            {
                var scene = Scene();
                var unplaced = FloatUnplaced(scene.Layout);
                var window = Show(scene.Layout, unplaced, scene.Owner);
                var result = unplaced.Geometry;
                window.Close();
                return result;
            },
            CancellationToken.None);

        Assert.False(geometry.IsUnplaced);
    }

    private static (Window Owner, WorkbenchLayout Layout) Scene()
    {
        var owner = new Window { Width = OwnerWidth, Height = OwnerHeight };
        owner.Show();
        var layout = new WorkbenchLayout(new object(), new object(), new object(), new object(), new object());
        layout.SetWindowWidth(OwnerWidth);
        return (owner, layout);
    }

    private static FloatingItem Float(WorkbenchLayout layout, FloatGeometry geometry) =>
        layout.FloatTab(OpenPreview(layout), geometry);

    private static FloatingItem FloatUnplaced(WorkbenchLayout layout) =>
        layout.FloatTab(OpenPreview(layout), FloatGeometry.Default(FloatingWidth, FloatingHeight));

    private static TabModel OpenPreview(WorkbenchLayout layout) =>
        layout.OpenTab(layout.PreviewTabs, WorkbenchIds.PreviewTab, TabKind.FilePreview, new object(), "file");

    private static FloatingPanelWindow Show(WorkbenchLayout layout, FloatingItem item, Window owner)
    {
        var window = new FloatingPanelWindow(layout, item);
        window.Show(owner);
        return window;
    }

    private static FloatGeometry BelowAndRightOf(Rect area) =>
        new(area.Right + OffScreenMargin, area.Bottom + OffScreenMargin, FloatingWidth, FloatingHeight, IsMaximized: false);

    private static Rect WorkingAreaOf(Window window)
    {
        var screens = window.Screens;
        Assert.NotNull(screens);
        var screen = screens.All.Count > 0 ? screens.All[0] : screens.Primary;
        Assert.NotNull(screen);
        var scaling = screen.Scaling > 0 ? screen.Scaling : 1;
        var area = screen.WorkingArea;
        return new Rect(area.X / scaling, area.Y / scaling, area.Width / scaling, area.Height / scaling);
    }

    private static PixelPoint CentredIn(Rect area, double width, double height) =>
        new(
            (int)Math.Round(area.X + ((area.Width - width) / 2)),
            (int)Math.Round(area.Y + ((area.Height - height) / 2)));
}
