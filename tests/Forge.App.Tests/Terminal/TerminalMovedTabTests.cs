using Avalonia.Controls;
using Avalonia.Headless;
using Forge.App.Preferences;
using Forge.App.Terminal;
using Forge.App.Workbench;
using Xunit;

namespace Forge.App.Tests.Terminal;

/// <summary>
/// A terminal tab the user moved out of the bottom group still follows the active session: switching away removes it from
/// whichever docked group holds it, switching back docks it in the bottom group again, and a floating tab never moves.
/// </summary>
[Collection("forge-app-ui")]
public sealed class TerminalMovedTabTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(40);

    private static async Task<T> RunAsync<T>(Func<WorkbenchLayout, TerminalWorkbench, FakeTerminalHost, Window, Task<T>> body)
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));
        return await TerminalUi.RunTestAsync(
            session,
            async () =>
            {
                var layout = new WorkbenchLayout(new object(), new object(), new object(), new object(), new object());
                var host = new FakeTerminalHost();
                var coordinator = new TerminalWorkbench(layout, new AppPreferences(), host, static () => { });
                var window = new Window { Width = 100, Height = 100 };
                TerminalUi.Show(window);
                try
                {
                    return await body(layout, coordinator, host, window);
                }
                finally
                {
                    window.Close();
                    await coordinator.DisposeAsync();
                }
            },
            Budget);
    }

    /// <summary>What dragging a tab between groups does to the layout: the tab leaves its group and is opened in the other.</summary>
    private static TabModel MoveToGroup(WorkbenchLayout layout, TabModel tab, TabGroupModel target)
    {
        var id = tab.Id;
        var content = tab.Content!;
        var title = tab.Title;
        layout.CloseTab(tab);
        return layout.OpenTab(target, id, TabKind.Terminal, content, title);
    }

    [Fact]
    public async Task A_tab_moved_to_another_docked_group_is_removed_on_a_session_switch_and_returns_to_the_bottom_group()
    {
        var observed = await RunAsync(async (layout, coordinator, host, window) =>
        {
            coordinator.OnActiveSessionChanged("s1", "C:\\work");
            var terminal = coordinator.OpenTerminal();
            Assert.NotNull(terminal);
            var running = await TerminalUi.WaitInsideAsync(window, () => terminal!.Status == TerminalStatus.Running, TimeSpan.FromSeconds(10));
            var tabId = WorkbenchIds.TerminalTab(terminal!.Id);
            var moved = MoveToGroup(layout, layout.FindTab(tabId)!, layout.MainTabs);
            var inMain = ReferenceEquals(moved.Group, layout.MainTabs) && layout.MainTabs.Tabs.Contains(moved);

            coordinator.OnActiveSessionChanged("s2", "C:\\work2");
            var afterAway = layout.FindTab(tabId);
            var stopCalls = host.Session.StopCalls;

            coordinator.OnActiveSessionChanged("s1", "C:\\work");
            var afterBack = layout.FindTab(tabId);
            var backInBottom = afterBack is not null && layout.BottomTabs.Tabs.Contains(afterBack);
            var mainTerminals = layout.MainTabs.Tabs.Count(tab => tab.Content is TerminalViewModel);
            return (running, inMain, afterAway, stopCalls, afterBack, backInBottom, mainTerminals, terminal);
        });

        Assert.True(observed.running, "the terminal never reached Running");
        Assert.True(observed.inMain, "the test setup did not move the tab into the main group");
        Assert.Null(observed.afterAway);
        // Removing the tab only undocks it: the process keeps running.
        Assert.Equal(0, observed.stopCalls);
        Assert.NotNull(observed.afterBack);
        Assert.Same(observed.terminal, observed.afterBack!.Content);
        Assert.True(observed.backInBottom, "the tab did not return to the bottom group");
        Assert.Equal(0, observed.mainTerminals);
    }

    [Fact]
    public async Task A_floating_tab_that_came_from_another_group_stays_put_on_a_session_switch()
    {
        var observed = await RunAsync(async (layout, coordinator, host, window) =>
        {
            coordinator.OnActiveSessionChanged("s1", "C:\\work");
            var terminal = coordinator.OpenTerminal();
            Assert.NotNull(terminal);
            var running = await TerminalUi.WaitInsideAsync(window, () => terminal!.Status == TerminalStatus.Running, TimeSpan.FromSeconds(10));
            var tabId = WorkbenchIds.TerminalTab(terminal!.Id);
            var moved = MoveToGroup(layout, layout.FindTab(tabId)!, layout.MainTabs);
            layout.FloatTab(moved);

            coordinator.OnActiveSessionChanged("s2", "C:\\work2");
            var afterAway = layout.FindTab(tabId);
            var sameWhileAway = ReferenceEquals(afterAway, moved);
            var floatingWhileAway = afterAway?.IsFloating == true;

            coordinator.OnActiveSessionChanged("s1", "C:\\work");
            var afterBack = layout.FindTab(tabId);
            var bottomTerminals = layout.BottomTabs.Tabs.Count(tab => tab.Content is TerminalViewModel);
            return (running, sameWhileAway, floatingWhileAway, sameAfterBack: ReferenceEquals(afterBack, moved), floatingAfterBack: afterBack?.IsFloating == true, bottomTerminals, stopCalls: host.Session.StopCalls);
        });

        Assert.True(observed.running, "the terminal never reached Running");
        Assert.True(observed.sameWhileAway, "the session switch replaced or removed the floating tab");
        Assert.True(observed.floatingWhileAway, "the floating tab was docked by the session switch");
        // Switching back finds the tab already present: it is neither duplicated nor pulled into the bottom group.
        Assert.True(observed.sameAfterBack);
        Assert.True(observed.floatingAfterBack);
        Assert.Equal(0, observed.bottomTerminals);
        Assert.Equal(0, observed.stopCalls);
    }

    [Fact]
    public async Task Announcing_the_same_session_again_leaves_a_moved_tab_where_the_user_put_it()
    {
        var observed = await RunAsync(async (layout, coordinator, host, window) =>
        {
            coordinator.OnActiveSessionChanged("s1", "C:\\work");
            var terminal = coordinator.OpenTerminal();
            Assert.NotNull(terminal);
            var running = await TerminalUi.WaitInsideAsync(window, () => terminal!.Status == TerminalStatus.Running, TimeSpan.FromSeconds(10));
            var tabId = WorkbenchIds.TerminalTab(terminal!.Id);
            MoveToGroup(layout, layout.FindTab(tabId)!, layout.MainTabs);

            coordinator.OnActiveSessionChanged("s1", "C:\\work");
            var tab = layout.FindTab(tabId);
            var inMain = tab is not null && layout.MainTabs.Tabs.Contains(tab);
            var bottomTerminals = layout.BottomTabs.Tabs.Count(candidate => candidate.Content is TerminalViewModel);
            return (running, inMain, bottomTerminals);
        });

        Assert.True(observed.running, "the terminal never reached Running");
        Assert.True(observed.inMain, "the moved tab left the group the user put it in");
        Assert.Equal(0, observed.bottomTerminals);
    }
}
