using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Forge.App;
using Forge.App.Localization;
using Forge.App.ViewModels;
using Forge.App.Workbench;
using Forge.App.Workbench.Controls;
using Xunit;

namespace Forge.App.Tests.Workbench;

/// <summary>
/// Lifecycle and allocation tests of the workbench controls: the synchronous splitter-drag chain must not allocate,
/// replacing the layout must close the old model's floating windows, runtime bottom tabs must be subscribed per item,
/// and the files panel's floating window must render its header tools and ignore geometry while minimized.
/// </summary>
[Collection("forge-app-ui")]
public sealed class WorkbenchControlsLifecycleTests
{
    [Fact]
    public async Task Splitter_drag_synchronous_chain_allocates_zero_after_warmup()
    {
        var session = SharedUi.Session;

        var allocated = await session.Dispatch(() =>
        {
            var scene = WorkbenchLifecycle.Open(WorkbenchLifecycle.NewLayout());
            var layout = scene.Layout;

            layout.BeginSplitterDrag(0);
            for (var i = 0; i < 64; i++)
            {
                layout.UpdateSplitterDrag(0.5 + (i % 4));
            }

            layout.EndSplitterDrag();

            // No layout jobs run between warmup and measurement: the warmup's first invalidate already
            // marked the host dirty and scheduled the (still pending) layout pass, the steady state of a drag.
            layout.BeginSplitterDrag(0);
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 128; i++)
            {
                layout.UpdateSplitterDrag(1.0 + (i % 3));
            }

            var after = GC.GetAllocatedBytesForCurrentThread();
            layout.EndSplitterDrag();
            return after - before;
        }, WorkbenchLifecycle.Budget());

        Assert.Equal(0L, allocated);
    }

    [Fact]
    public async Task Replacing_layout_closes_old_floating_windows_and_ignores_old_model()
    {
        var session = SharedUi.Session;

        var result = await session.Dispatch(() =>
        {
            var oldLayout = WorkbenchLifecycle.NewLayout();
            var oldTab = oldLayout.OpenTab(oldLayout.MainTabs, WorkbenchIds.SessionTab("old"), TabKind.Session, new object(), "old");
            oldLayout.FloatFilesPanel();

            var scene = WorkbenchLifecycle.Open(oldLayout);
            var oldWindow = WorkbenchLifecycle.FloatingWindow(scene.Window, WorkbenchIds.FilesPanel);
            var oldWindowOpened = oldWindow is not null;

            var newLayout = WorkbenchLifecycle.NewLayout();
            scene.Host.Layout = newLayout;
            SharedUi.Settle(scene.Window);

            var oldClosed = oldWindow is null || !scene.Window.OwnedWindows.Contains(oldWindow);
            var oldNotVisible = oldWindow is null || !oldWindow.IsVisible;

            // The old model is no longer the host's model: mutating it must not move, open or close anything.
            oldTab.Title = "renamed";
            oldLayout.IsBottomMinimized = true;
            oldLayout.Project.Width = 999;
            SharedUi.Settle(scene.Window);
            var noReaction = scene.Window.OwnedWindows.Count == 0;

            // The host is subscribed to the new model: its floating items still open windows.
            newLayout.FloatFilesPanel();
            SharedUi.Settle(scene.Window);
            var newWindowOpened = WorkbenchLifecycle.FloatingWindow(scene.Window, WorkbenchIds.FilesPanel) is not null;

            return (oldWindowOpened, oldClosed, oldNotVisible, noReaction, newWindowOpened);
        }, WorkbenchLifecycle.Budget());

        Assert.True(result.oldWindowOpened);
        Assert.True(result.oldClosed);
        Assert.True(result.oldNotVisible);
        Assert.True(result.noReaction);
        Assert.True(result.newWindowOpened);
    }

    [Fact]
    public async Task Floating_files_window_renders_the_files_open_changes_header_tool()
    {
        var session = SharedUi.Session;

        var found = await session.Dispatch(() =>
        {
            var layout = WorkbenchLifecycle.NewLayout();
            layout.FilesPanel.HeaderTools.Add(new PanelToolButton(
                "files-open-changes",
                ShellStrings.OpenChangesTooltip,
                "IconRestore",
                new RelayCommand(() => { })));
            layout.FloatFilesPanel();

            var scene = WorkbenchLifecycle.Open(layout);
            var floating = WorkbenchLifecycle.FloatingWindow(scene.Window, WorkbenchIds.FilesPanel)
                ?? throw new InvalidOperationException("The files panel did not open a floating window.");

            return WorkbenchLifecycle.TryFind<PanelIconButton>(floating, "files-open-changes") is not null;
        }, WorkbenchLifecycle.Budget());

        Assert.True(found);
    }

    [Fact]
    public async Task Minimized_floating_window_does_not_write_its_geometry()
    {
        var session = SharedUi.Session;

        var result = await session.Dispatch(() =>
        {
            var layout = WorkbenchLifecycle.NewLayout();
            var item = layout.FloatFilesPanel();
            var scene = WorkbenchLifecycle.Open(layout);
            var floating = WorkbenchLifecycle.FloatingWindow(scene.Window, WorkbenchIds.FilesPanel)
                ?? throw new InvalidOperationException("The files panel did not open a floating window.");

            var before = item.Geometry;

            floating.WindowState = WindowState.Minimized;
            floating.Position = new PixelPoint(700, 700);
            SharedUi.Settle(scene.Window);
            var during = item.Geometry;

            floating.WindowState = WindowState.Normal;
            floating.Width = 400;
            floating.Height = 300;
            SharedUi.Settle(scene.Window);
            var afterResize = item.Geometry;

            return (before, during, afterResize);
        }, WorkbenchLifecycle.Budget());

        Assert.Equal(result.before, result.during);
        Assert.NotEqual(result.before, result.afterResize);
    }
}

/// <summary>Self-contained UI helpers for this file; independent of the helpers in the other workbench test files.</summary>
internal static class WorkbenchLifecycle
{
    public const double WindowWidth = 1200;
    public const double WindowHeight = 800;

    public static CancellationToken Budget() => new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token;

    public static WorkbenchLayout NewLayout() => new(new object(), new object(), new object(), new object(), new object());

    public static LifecycleScene Open(WorkbenchLayout layout)
    {
        var host = new WorkbenchHost { Layout = layout };
        var window = SharedUi.Show(host, WindowWidth, WindowHeight);
        SharedUi.Settle(window);
        return new LifecycleScene(window, host, layout);
    }

    public static T Find<T>(Visual root, string automationId) where T : Visual =>
        TryFind<T>(root, automationId)
        ?? throw new InvalidOperationException($"No {typeof(T).Name} with AutomationId '{automationId}'.");

    public static T? TryFind<T>(Visual root, string automationId) where T : Visual =>
        root.GetVisualDescendants().OfType<T>().FirstOrDefault(control => AutomationProperties.GetAutomationId(control) == automationId);

    public static Window? FloatingWindow(Window owner, string itemId) =>
        owner.OwnedWindows.FirstOrDefault(window => AutomationProperties.GetAutomationId(window) == "floating-" + itemId);

}

internal sealed record LifecycleScene(Window Window, WorkbenchHost Host, WorkbenchLayout Layout);
