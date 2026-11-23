using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Forge.App.Workbench;
using Forge.App.Workbench.Controls;
using Xunit;

namespace Forge.App.Tests;

/// <summary>
/// The tab strip shows which tab is on screen by the tab's own silhouette, so the active tab has to be inside the strip:
/// a tab opened at the end of a full strip used to land entirely past the viewport, leaving the window with the new
/// transcript in its body and no visibly active tab at all. Revealing it must not become a strip that scrolls itself on
/// every rebuild — a tab already in view stays where the user left it.
/// </summary>
[Collection("forge-app-ui")]
public sealed class TabStripActiveTabTests
{
    [Fact]
    public Task Activating_a_clipped_tab_scrolls_it_into_the_strip() => SharedUi.RunAsync(() =>
    {
        using var layout = new WorkbenchLayout(new object(), new object(), new object(), new object(), new object());
        var window = new Window { Width = 560, Height = 420, Content = new WorkbenchHost { Layout = layout } };
        window.Show();
        SharedUi.Settle(window);

        // The first tab opens as the active one; the rest push past the strip's right edge.
        var tabs = new List<TabModel>
        {
            layout.OpenTab(layout.MainTabs, "session:0", TabKind.Session, new object(), "会话 11-20 18:20"),
        };
        for (var i = 1; i < 6; i++)
        {
            tabs.Add(layout.OpenTab(
                layout.MainTabs, "session:" + i, TabKind.Session, new object(), "会话 11-20 1" + i + ":00", activate: false));
        }

        SharedUi.Settle(window);
        var strip = StripOf(window, layout);
        var scroller = Assert.Single(strip.GetVisualDescendants().OfType<ScrollViewer>());
        Assert.True(
            scroller.Extent.Width > scroller.Viewport.Width,
            $"the tabs have to overflow for this test to mean anything: extent {scroller.Extent.Width}, viewport {scroller.Viewport.Width}");

        // The last tab is the one the shell now shows: it must end up inside the strip instead of past its right edge.
        layout.ActivateTab(tabs[^1]);
        SharedUi.Settle(window);
        var active = HeaderOf(strip, tabs[^1]);
        Assert.True(active.IsActive);
        AssertInsideStrip(active, scroller, "the activated tab");

        // And a strip the user scrolled away stays where they left it: a rebuild (another tab opening in the background, a
        // language switch) re-announces the same active tab, and that must not yank the strip back to it.
        scroller.Offset = default;
        SharedUi.Settle(window);
        Assert.False(IsInsideStrip(active, scroller), "the second half has to start with the active tab outside the strip");

        layout.OpenTab(layout.MainTabs, "session:6", TabKind.Session, new object(), "会话 11-20 7:00", activate: false);
        SharedUi.Settle(window);
        Assert.True(HeaderOf(strip, tabs[^1]).IsActive);
        Assert.Equal(0, scroller.Offset.X);

        window.Close();
        return Task.CompletedTask;
    });

    private static PanelTabStrip StripOf(Window window, WorkbenchLayout layout) =>
        window.GetVisualDescendants()
            .OfType<PanelTabStrip>()
            .Single(strip => ReferenceEquals(strip.Group, layout.MainTabs));

    private static TabHeaderView HeaderOf(PanelTabStrip strip, TabModel tab) =>
        strip.GetVisualDescendants().OfType<TabHeaderView>().Single(header => ReferenceEquals(header.Model, tab));

    private static bool IsInsideStrip(TabHeaderView header, ScrollViewer scroller)
    {
        var left = header.TranslatePoint(new Point(0, 0), scroller)?.X;
        return left is { } position
               && position >= -0.5
               && position + header.Bounds.Width <= scroller.Viewport.Width + 0.5;
    }

    private static void AssertInsideStrip(TabHeaderView header, ScrollViewer scroller, string what)
    {
        var left = header.TranslatePoint(new Point(0, 0), scroller)?.X;
        Assert.True(
            IsInsideStrip(header, scroller),
            $"{what} is still outside the strip: left {left}, width {header.Bounds.Width}, viewport {scroller.Viewport.Width}");
    }
}
