using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Layout;
using Avalonia.VisualTree;
using Forge.App.Workbench;
using Forge.App.Workbench.Controls;
using Xunit;

namespace Forge.App.Tests.Workbench;

[Collection("forge-app-ui")]
public sealed class PanelScrollExtentTests
{
    private const int RowCount = 100;
    private const double RowHeight = 32;
    private const double Tolerance = 1;

    public static IEnumerable<object[]> Cases()
    {
        foreach (var list in new[] { false, true })
        {
            foreach (var panel in new[] { "project", "main", "bottom", "files", "tools", "preview" })
            {
                foreach (var state in new[] { "split", "resize", "splitter", "peer-minimized", "window-restore" })
                {
                    yield return [list, panel, state];
                }

                if (panel is not ("main" or "bottom"))
                {
                    yield return [list, panel, "drawer"];
                }
            }

            yield return [list, "preview", "cover"];
            yield return [list, "bottom", "bottom-minimized"];
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Last_row_viewport_and_scrollbars_stay_inside_panel(bool list, string panel, string state)
    {
        var session = SharedUi.Session;
        await WorkbenchUi.RunAsync(session, () =>
        {
            var rows = Enumerable.Range(0, RowCount)
                .Select(i => new Border { Height = RowHeight, Child = new TextBlock { Text = $"Row {i}" } })
                .ToArray();
            Control content;
            if (list)
            {
                var items = new ListBox { ItemsSource = rows };
                ScrollViewer.SetVerticalScrollBarVisibility(items, ScrollBarVisibility.Visible);
                ScrollViewer.SetHorizontalScrollBarVisibility(items, ScrollBarVisibility.Disabled);
                content = items;
            }
            else
            {
                var stack = new StackPanel { Width = 1800 };
                foreach (var row in rows)
                {
                    stack.Children.Add(row);
                }

                content = new ScrollViewer
                {
                    Content = stack,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Visible,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Visible,
                    AllowAutoHide = false,
                };
            }

            using var layout = new WorkbenchLayout(
                panel == "project" ? content : new object(),
                panel == "files" ? content : new object(),
                panel == "tools" ? content : new object(),
                new object(), new object());
            layout.OpenTab(layout.MainTabs, "scroll-main", TabKind.Session,
                panel == "main" ? content : new object(), "Main");
            layout.OpenTab(layout.BottomTabs, "scroll-bottom", TabKind.SubAgent,
                panel == "bottom" ? content : new object(), "Bottom");
            layout.OpenTab(layout.PreviewTabs, WorkbenchIds.PreviewTab, TabKind.FilePreview,
                panel == "preview" ? content : new object(), "Preview");
            var model = panel switch
            {
                "project" => layout.ProjectPanel,
                "main" => layout.MainTabs,
                "bottom" => layout.BottomTabs,
                "files" => layout.FilesPanel,
                "tools" => layout.InspectorTools,
                "preview" => layout.PreviewTabs,
                _ => throw new InvalidOperationException(panel),
            };
            var scene = WorkbenchUi.Open(layout, 1600);
            try
            {
                void Check() => AssertScrollEnd(scene.Window, model, content, rows, $"{list}/{panel}/{state}");

                switch (state)
                {
                    case "resize":
                        scene.Window.Width = 1200;
                        scene.Window.Height = 540;
                        WorkbenchUi.Settle(scene.Window);
                        Check();
                        scene.Window.Width = 1800;
                        scene.Window.Height = 900;
                        WorkbenchUi.Settle(scene.Window);
                        Check();
                        scene.Window.Width = 1400;
                        scene.Window.Height = 620;
                        break;
                    case "splitter":
                        layout.BottomHeight = 360;
                        layout.InspectorSplitRatio = 0.7;
                        WorkbenchUi.Settle(scene.Window);
                        Check();
                        layout.BottomHeight = 180;
                        layout.InspectorSplitRatio = 0.3;
                        break;
                    case "peer-minimized":
                        // The region's other panels collapse to their title bars, so this one gets the whole region and
                        // is re-measured at a larger size than the split it started in.
                        foreach (var peer in layout.GetRegion(model.Region).Panels)
                        {
                            if (!ReferenceEquals(peer, model) && peer.CanMinimize)
                            {
                                layout.ToggleMinimize(peer);
                            }
                        }

                        WorkbenchUi.Settle(scene.Window);
                        Check();
                        break;
                    case "window-restore":
                        scene.Window.WindowState = WindowState.Maximized;
                        scene.Window.Width = 1920;
                        scene.Window.Height = 1080;
                        WorkbenchUi.Settle(scene.Window);
                        Check();
                        scene.Window.WindowState = WindowState.Normal;
                        scene.Window.Width = 1600;
                        scene.Window.Height = 800;
                        break;
                    case "drawer":
                        layout.SetAutoHidden(model.Region, true);
                        layout.ToggleDrawer(model.Region);
                        WorkbenchUi.Settle(scene.Window);
                        Check();
                        layout.ToggleDrawer(model.Region);
                        WorkbenchUi.Settle(scene.Window);
                        layout.ToggleDrawer(model.Region);
                        break;
                    case "cover":
                        layout.TogglePreviewCoverMain();
                        break;
                    case "bottom-minimized":
                        layout.IsBottomMinimized = true;
                        WorkbenchUi.Settle(scene.Window);
                        layout.IsBottomMinimized = false;
                        break;
                }

                WorkbenchUi.Settle(scene.Window);
                Check();
            }
            finally
            {
                scene.Window.Close();
            }
        });
    }

    private static void AssertScrollEnd(Window window, PanelModel model, Control content, Border[] rows, string state)
    {
        var chrome = window.GetVisualDescendants().OfType<PanelChrome>()
            .Single(c => ReferenceEquals(c.Model, model));
        var scroll = content as ScrollViewer
            ?? content.GetVisualDescendants().OfType<ScrollViewer>().Single();
        var before = $"{state}: chrome={chrome.Bounds}, scroll={scroll.Bounds}, " +
            $"extent={scroll.Extent}, viewport={scroll.Viewport}, offset={scroll.Offset}";
        AssertInside(scroll, chrome, before);
        var presenter = Assert.IsType<RegionPresenter>(chrome.GetVisualParent());
        AssertInside(chrome, presenter, before);
        Assert.True(scroll.TranslatePoint(default, chrome)!.Value.Y >= PanelChrome.TitleBarHeight - Tolerance, before);
        scroll.ScrollToEnd();
        scroll.Offset = new Vector(scroll.Extent.Width, scroll.Offset.Y);
        WorkbenchUi.Settle(window);
        var viewport = scroll.GetVisualDescendants().OfType<ScrollContentPresenter>().Single();
        var diagnostic = $"{state}: chrome={chrome.Bounds}, scroll={scroll.Bounds}, " +
            $"extent={scroll.Extent}, viewport={scroll.Viewport}, offset={scroll.Offset}";

        Assert.True(scroll.Extent.Height > scroll.Viewport.Height, diagnostic);
        Assert.True(scroll.Viewport.Height > RowHeight, diagnostic);
        Assert.True(Math.Abs(scroll.Offset.Y - (scroll.Extent.Height - scroll.Viewport.Height)) <= Tolerance, diagnostic);
        var last = content is ListBox box
            ? Assert.IsType<ListBoxItem>(box.ContainerFromIndex(RowCount - 1))
            : (Control)rows[^1];
        AssertInside(scroll, chrome, diagnostic);
        AssertInside(viewport, chrome, diagnostic);
        AssertInside(last, viewport, diagnostic, verticalOnly: true);
        AssertInside(last, chrome, diagnostic, verticalOnly: true);
        Assert.InRange(Math.Abs(scroll.Viewport.Height - viewport.Bounds.Height), 0, Tolerance);
        Assert.InRange(Math.Abs(scroll.Viewport.Width - viewport.Bounds.Width), 0, Tolerance);
        Assert.True(scroll.Offset.Y > 0, diagnostic);
        if (content is ScrollViewer)
        {
            Assert.True(scroll.Extent.Width > scroll.Viewport.Width, diagnostic);
            Assert.InRange(Math.Abs(scroll.Offset.X - (scroll.Extent.Width - scroll.Viewport.Width)), 0, Tolerance);
            var lastRight = rows[^1].TranslatePoint(new Point(rows[^1].Bounds.Width, 0), viewport)!.Value.X;
            Assert.InRange(Math.Abs(lastRight - viewport.Bounds.Width), 0, Tolerance);
        }

        foreach (var bar in scroll.GetVisualDescendants().OfType<ScrollBar>().Where(b => b.IsVisible))
        {
            AssertInside(bar, chrome, diagnostic);
            Assert.True(bar.Bounds.Width > 0 && bar.Bounds.Height > 0, diagnostic);
        }

        if (content is ListBox virtualized)
        {
            Assert.IsType<VirtualizingStackPanel>(virtualized.ItemsPanelRoot);
            Assert.True(virtualized.GetRealizedContainers().Count() < RowCount, diagnostic);
        }
    }

    private static void AssertInside(Control child, Control parent, string diagnostic, bool verticalOnly = false)
    {
        var origin = child.TranslatePoint(default, parent);
        Assert.NotNull(origin);
        var bounds = new Rect(origin.Value, child.Bounds.Size);
        Assert.True(bounds.Top >= -Tolerance && bounds.Bottom <= parent.Bounds.Height + Tolerance,
            $"{diagnostic}; {child.GetType().Name}={bounds} outside {parent.GetType().Name}={parent.Bounds.Size}");
        if (!verticalOnly)
        {
            Assert.True(bounds.Left >= -Tolerance && bounds.Right <= parent.Bounds.Width + Tolerance,
                $"{diagnostic}; {child.GetType().Name}={bounds} outside {parent.GetType().Name}={parent.Bounds.Size}");
        }
    }
}
