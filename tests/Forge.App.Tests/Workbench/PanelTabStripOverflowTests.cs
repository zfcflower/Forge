using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Forge.App.Workbench;
using Forge.App.Workbench.Controls;
using Xunit;

namespace Forge.App.Tests.Workbench;

/// <summary>
/// The tab strip's overflow behaviour: once the tabs no longer fit, the strip must offer the ones it clipped instead of
/// silently painting them out of reach.
/// </summary>
[Collection("forge-app-ui")]
public sealed class PanelTabStripOverflowTests
{
    private const double NarrowWindow = 980;
    private const double WideWindow = 1800;
    private const int TabCount = 8;
    private const double Precision = 1;

    [Fact]
    public async Task Ellipsis_appears_only_when_the_tabs_no_longer_fit()
    {
        var session = SharedUi.Session;

        var state = await WorkbenchUi.QueryAsync(session, () =>
        {
            var scene = WorkbenchUi.Open(WorkbenchUi.NewLayout(), WideWindow);
            WorkbenchUi.OpenSessionTab(scene.Layout, "s0");
            WorkbenchUi.OpenSessionTab(scene.Layout, "s1");
            WorkbenchUi.Settle(scene.Window);
            PanelTabStrip strip = Strip(scene);
            PanelIconButton button = OverflowButton(strip);
            var fits = (Overflow: strip.HasOverflow, Visible: button.IsEffectivelyVisible);

            scene.Window.Width = NarrowWindow;
            for (var i = 2; i < TabCount; i++)
            {
                WorkbenchUi.OpenSessionTab(scene.Layout, "s" + i);
            }

            WorkbenchUi.Settle(scene.Window);
            var clipped = (Overflow: strip.HasOverflow, Visible: button.IsEffectivelyVisible);

            // Closing the extra tabs has to put the strip back to "everything fits" — the flag is not sticky.
            var tabs = scene.Layout.MainTabs.Tabs.ToArray();
            for (var i = 2; i < tabs.Length; i++)
            {
                scene.Layout.CloseTab(tabs[i]);
            }

            WorkbenchUi.Settle(scene.Window);
            return (fits, clipped, Restored: strip.HasOverflow);
        });

        Assert.False(state.fits.Overflow);
        Assert.False(state.fits.Visible);
        Assert.True(state.clipped.Overflow);
        Assert.True(state.clipped.Visible);
        Assert.False(state.Restored);
    }

    [Fact]
    public async Task Clipped_menu_lists_only_the_hidden_tabs()
    {
        var session = SharedUi.Session;

        var menu = await WorkbenchUi.QueryAsync(session, () =>
        {
            var scene = WorkbenchUi.Open(WorkbenchUi.NewLayout(), NarrowWindow);
            for (var i = 0; i < TabCount; i++)
            {
                WorkbenchUi.OpenSessionTab(scene.Layout, "s" + i);
            }

            WorkbenchUi.Settle(scene.Window);
            PanelTabStrip strip = Strip(scene);
            Assert.True(strip.HasOverflow);
            var items = strip.BuildClippedMenu()?.Items.OfType<MenuItem>().Select(item => item.Header as string).ToList() ?? [];
            return (Items: items, First: scene.Layout.MainTabs.Tabs[0].Title, Last: scene.Layout.MainTabs.Tabs[^1].Title);
        });

        Assert.NotEmpty(menu.Items);
        Assert.Contains(menu.Last, menu.Items);
        Assert.DoesNotContain(menu.First, menu.Items);
    }

    [Fact]
    public async Task Picking_a_clipped_tab_activates_it_and_scrolls_it_into_the_strip()
    {
        var session = SharedUi.Session;

        var result = await WorkbenchUi.QueryAsync(session, () =>
        {
            var scene = WorkbenchUi.Open(WorkbenchUi.NewLayout(), NarrowWindow);
            for (var i = 0; i < TabCount; i++)
            {
                WorkbenchUi.OpenSessionTab(scene.Layout, "s" + i);
            }

            WorkbenchUi.Settle(scene.Window);
            PanelTabStrip strip = Strip(scene);
            MenuFlyout flyout = strip.BuildClippedMenu() ?? throw new InvalidOperationException("Nothing was clipped.");
            var item = flyout.Items.OfType<MenuItem>().Last();
            item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, item));
            WorkbenchUi.Settle(scene.Window);
            WorkbenchUi.Settle(scene.Window);

            TabHeaderView header = strip.GetVisualDescendants().OfType<TabHeaderView>()
                .Last(view => view.Model is not null && view.Model.Title == (string?)item.Header);
            var origin = header.TranslatePoint(default, strip) ?? default;
            return (Activated: scene.Layout.MainTabs.ActiveTab?.Title, Header: (string?)item.Header, Left: origin.X, Right: origin.X + header.Bounds.Width, Width: strip.Bounds.Width);
        });

        Assert.Equal(result.Header, result.Activated);
        Assert.InRange(result.Left, -Precision, result.Width);
        Assert.InRange(result.Right, 0, result.Width + Precision);
    }

    [Fact]
    public async Task The_overflow_button_wears_the_solid_dots_icon()
    {
        var session = SharedUi.Session;

        var state = await WorkbenchUi.QueryAsync(session, () =>
        {
            var scene = WorkbenchUi.Open(WorkbenchUi.NewLayout(), NarrowWindow);
            for (var i = 0; i < TabCount; i++)
            {
                WorkbenchUi.OpenSessionTab(scene.Layout, "s" + i);
            }

            WorkbenchUi.Settle(scene.Window);
            PanelIconButton button = OverflowButton(Strip(scene));
            return (Icon: button.Icon, Solid: WorkbenchIcons.Find(scene.Window, "IconEllipsisSolid"));
        });

        Assert.Same(state.Solid, state.Icon);
    }

    /// <summary>The main region's tab strip: the one the ellipsis belongs to in these tests.</summary>
    private static PanelTabStrip Strip(Scene scene) =>
        WorkbenchUi.Find<PanelChrome>(scene.Window, WorkbenchUi.PanelId("main"))
            .GetVisualDescendants()
            .OfType<PanelTabStrip>()
            .Single();

    private static PanelIconButton OverflowButton(PanelTabStrip strip) =>
        strip.GetVisualDescendants().OfType<PanelIconButton>().Single(button => button.Name == "PART_Overflow");
}
