using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Forge.App.Preferences;
using Forge.App.Terminal;
using Forge.App.Workbench;
using Xunit;

namespace Forge.App.Tests.Terminal;

/// <summary>
/// Pins <see cref="TerminalWorkbench"/> (forge-app spec, "App/TerminalWorkbench"): opening without a session returns
/// null, new terminals dock into the bottom group, switching sessions undocks (but never ends) the other session's
/// docked tabs, floating tabs stay put, switching back re-docks, archiving ends that session's terminals including
/// floating ones, and disposing the coordinator ends everything.
/// </summary>
[Collection("forge-app-ui")]
public sealed class TerminalCoordinatorTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(40);

    private sealed class Fixture
    {
        public Fixture(Window window, WorkbenchLayout layout, AppPreferences preferences, FakeTerminalHost host, TerminalWorkbench coordinator)
        {
            Window = window;
            Layout = layout;
            Preferences = preferences;
            Host = host;
            Coordinator = coordinator;
        }

        public Window Window { get; }

        public WorkbenchLayout Layout { get; }

        public AppPreferences Preferences { get; }

        public FakeTerminalHost Host { get; }

        public TerminalWorkbench Coordinator { get; }

        public static Fixture Open()
        {
            var layout = new WorkbenchLayout(new object(), new object(), new object(), new object(), new object());
            var preferences = new AppPreferences();
            var host = new FakeTerminalHost();
            var coordinator = new TerminalWorkbench(layout, preferences, host);
            // The terminals report state through UI-thread callbacks, which a headless session only
            // drains for a shown window (see TerminalLifecycleTests).
            var window = new Window { Width = 100, Height = 100 };
            TerminalUi.Show(window);
            return new Fixture(window, layout, preferences, host, coordinator);
        }

        public async ValueTask DisposeAsync()
        {
            Window.Close();
            await Coordinator.DisposeAsync();
        }
    }

    private static async Task<T> RunAsync<T>(Func<Fixture, Task<T>> body)
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));
        return await TerminalUi.RunTestAsync(
            session,
            async () =>
            {
                var fixture = Fixture.Open();
                try
                {
                    return await body(fixture);
                }
                finally
                {
                    await fixture.DisposeAsync();
                }
            },
            Budget);
    }

    private static async Task<bool> WaitForRunningAsync(Fixture fixture, params TerminalViewModel[] terminals) =>
        await TerminalUi.WaitInsideAsync(
            fixture.Window, () => terminals.All(static vm => vm.Status == TerminalStatus.Running), TimeSpan.FromSeconds(10));

    [Fact]
    public async Task Open_without_a_session_returns_null_and_docks_nothing()
    {
        var observed = await RunAsync(async fixture =>
        {
            var terminal = fixture.Coordinator.OpenTerminal();
            await Task.CompletedTask;
            return (terminal, fixture.Layout.BottomTabs.Tabs.Count);
        });

        Assert.Null(observed.terminal);
        Assert.Equal(0, observed.Item2);
    }

    [Fact]
    public async Task Open_docks_a_terminal_tab_into_the_bottom_group()
    {
        var observed = await RunAsync(async fixture =>
        {
            fixture.Coordinator.OnActiveSessionChanged("s1", "C:\\work");
            var terminal = fixture.Coordinator.OpenTerminal();
            Assert.NotNull(terminal);
            var running = await WaitForRunningAsync(fixture, terminal!);
            var tab = fixture.Layout.FindTab(WorkbenchIds.TerminalTab(terminal!.Id));
            return (
                running,
                tab,
                terminal,
                Active: fixture.Layout.BottomTabs.ActiveTab,
                Collapsed: fixture.Layout.IsBottomCollapsed);
        });

        Assert.True(observed.running, "the terminal never reached Running");
        Assert.NotNull(observed.tab);
        Assert.Equal(TabKind.Terminal, observed.tab!.Kind);
        Assert.Same(observed.terminal, observed.tab.Content);
        Assert.Same(observed.tab, observed.Active);
        Assert.False(observed.Collapsed);
    }

    [Fact]
    public async Task Switching_sessions_undocks_but_keeps_the_process_alive()
    {
        var observed = await RunAsync(async fixture =>
        {
            fixture.Coordinator.OnActiveSessionChanged("s1", "C:\\work");
            var first = fixture.Coordinator.OpenTerminal();
            var second = fixture.Coordinator.OpenTerminal();
            Assert.NotNull(first);
            Assert.NotNull(second);
            var running = await WaitForRunningAsync(fixture, first!, second!);

            fixture.Coordinator.OnActiveSessionChanged("s2", "C:\\work2");
            var undocked = new[]
            {
                fixture.Layout.FindTab(WorkbenchIds.TerminalTab(first!.Id)),
                fixture.Layout.FindTab(WorkbenchIds.TerminalTab(second!.Id)),
            };
            var stopCalls = fixture.Host.Session.StopCalls;

            fixture.Coordinator.OnActiveSessionChanged("s1", "C:\\work");
            var redocked = new[]
            {
                fixture.Layout.FindTab(WorkbenchIds.TerminalTab(first.Id)),
                fixture.Layout.FindTab(WorkbenchIds.TerminalTab(second.Id)),
            };
            await Task.CompletedTask;
            return (running, undocked, stopCalls, redocked, first, second);
        });

        Assert.True(observed.running, "s1 terminals never ran");
        Assert.Null(observed.undocked[0]);
        Assert.Null(observed.undocked[1]);
        // The processes keep running: the coordinator never asked the host to stop them.
        Assert.Equal(0, observed.stopCalls);
        Assert.Same(observed.first, observed.redocked[0]?.Content);
        Assert.Same(observed.second, observed.redocked[1]?.Content);
    }

    [Fact]
    public async Task Floating_terminals_survive_a_session_switch()
    {
        var observed = await RunAsync(async fixture =>
        {
            fixture.Coordinator.OnActiveSessionChanged("s1", "C:\\work");
            var terminal = fixture.Coordinator.OpenTerminal();
            Assert.NotNull(terminal);
            var running = await WaitForRunningAsync(fixture, terminal!);
            fixture.Layout.FloatTab(fixture.Layout.FindTab(WorkbenchIds.TerminalTab(terminal!.Id))!);

            fixture.Coordinator.OnActiveSessionChanged("s2", "C:\\work2");
            var tab = fixture.Layout.FindTab(WorkbenchIds.TerminalTab(terminal.Id));
            await Task.CompletedTask;
            return (running, tab, fixture.Host.Session.StopCalls);
        });

        Assert.True(observed.running, "the terminal never reached Running");
        Assert.NotNull(observed.tab);
        Assert.True(observed.tab!.IsFloating, "the tab lost its floating state");
        Assert.Equal(0, observed.Item3);
    }

    [Fact]
    public async Task Archiving_a_session_ends_its_terminals_including_floating_ones()
    {
        var observed = await RunAsync(async fixture =>
        {
            fixture.Coordinator.OnActiveSessionChanged("s1", "C:\\work");
            var doomed = fixture.Coordinator.OpenTerminal();
            var floating = fixture.Coordinator.OpenTerminal();
            Assert.NotNull(doomed);
            Assert.NotNull(floating);
            fixture.Layout.FloatTab(fixture.Layout.FindTab(WorkbenchIds.TerminalTab(floating!.Id))!);
            var running = await WaitForRunningAsync(fixture, doomed!, floating!);

            fixture.Coordinator.OnSessionArchived("s1");
            var ended = await TerminalUi.WaitInsideAsync(
                fixture.Window, () => fixture.Host.Session.StopCalls >= 2, TimeSpan.FromSeconds(10));
            var gone = new[]
            {
                fixture.Layout.FindTab(WorkbenchIds.TerminalTab(doomed!.Id)),
                fixture.Layout.FindTab(WorkbenchIds.TerminalTab(floating.Id)),
            };
            return (running, ended, gone);
        });

        Assert.True(observed.running, "s1 terminals never ran");
        Assert.True(observed.ended, "archiving never ended the session's terminals");
        Assert.Null(observed.gone[0]);
        Assert.Null(observed.gone[1]);
    }

    [Fact]
    public async Task Dispose_ends_every_terminal_of_every_session()
    {
        var stopCalls = await RunAsync(async fixture =>
        {
            var terminals = new List<TerminalViewModel>();
            fixture.Coordinator.OnActiveSessionChanged("s1", "C:\\work");
            for (var i = 0; i < 3; i++)
            {
                terminals.Add(fixture.Coordinator.OpenTerminal()!);
            }

            var running = await WaitForRunningAsync(fixture, [.. terminals]);
            // The fixture teardown (RunAsync's finally) disposes the coordinator; the host below
            // observes that every terminal was ended exactly once.
            return (running, fixture.Host);
        });

        Assert.True(stopCalls.running, "the terminals never reached Running");
        Assert.Equal(3, stopCalls.Host.Session.StopCalls);
    }
}

/// <summary>
/// Pins the workbench close hook of <see cref="TerminalWorkbench"/> (forge-app spec, "App/TerminalWorkbench", closing
/// hook): an idle terminal closes freely and ends its process, a busy one vetoes the close, activates its tab and
/// shows the banner in the real <see cref="TerminalView"/>, and "End and close" ends the process and closes the tab.
/// </summary>
[Collection("forge-app-ui")]
public sealed class TerminalCloseVetoTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(40);

    private sealed class Scene
    {
        public Scene(
            Window window,
            TerminalViewModel viewModel,
            FakeTerminalHost host,
            TerminalWorkbench coordinator,
            WorkbenchLayout layout,
            TabModel tab)
        {
            Window = window;
            ViewModel = viewModel;
            Host = host;
            Coordinator = coordinator;
            Layout = layout;
            Tab = tab;
        }

        public Window Window { get; }

        public TerminalViewModel ViewModel { get; }

        public FakeTerminalHost Host { get; }

        public TerminalWorkbench Coordinator { get; }

        public WorkbenchLayout Layout { get; }

        public TabModel Tab { get; }

        /// <summary>Opens a terminal through the coordinator and shows its real view; <paramref name="busy"/> decides the probe.</summary>
        public static async Task<Scene> OpenAsync(bool busy)
        {
            var preferences = new AppPreferences();
            var host = new FakeTerminalHost();
            host.Session.HasForeground = busy;
            var layout = new WorkbenchLayout(new object(), new object(), new object(), new object(), new object());
            var coordinator = new TerminalWorkbench(layout, preferences, host);
            coordinator.OnActiveSessionChanged("s1", "C:\\work");
            var viewModel = coordinator.OpenTerminal();
            Assert.NotNull(viewModel);
            var tab = layout.FindTab(WorkbenchIds.TerminalTab(viewModel!.Id));
            Assert.NotNull(tab);
            var window = new Window
            {
                Width = 1000,
                Height = 700,
                Content = new TerminalView { DataContext = viewModel },
            };
            TerminalUi.Show(window);
            var running = await TerminalUi.WaitInsideAsync(
                window, () => viewModel.Status == TerminalStatus.Running, TimeSpan.FromSeconds(10));
            Assert.True(running, "the terminal never reached Running");
            return new Scene(window, viewModel, host, coordinator, layout, tab!);
        }

        public async ValueTask DisposeAsync()
        {
            Window.Close();
            await Coordinator.DisposeAsync();
        }
    }

    private static async Task<T> RunAsync<T>(bool busy, Func<Scene, Task<T>> body)
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));
        return await TerminalUi.RunTestAsync(
            session,
            async () =>
            {
                var scene = await Scene.OpenAsync(busy);
                try
                {
                    return await body(scene);
                }
                finally
                {
                    await scene.DisposeAsync();
                }
            },
            Budget);
    }

    [Fact]
    public async Task Idle_terminal_close_is_allowed_and_ends_the_process()
    {
        var observed = await RunAsync(busy: false, async scene =>
        {
            var allowed = await scene.Layout.TabCloseRequested!(scene.Tab);
            var ended = await TerminalUi.WaitInsideAsync(
                scene.Window, () => scene.Host.Session.StopCalls > 0, TimeSpan.FromSeconds(10));
            return (allowed, ended);
        });

        Assert.True(observed.allowed, "an idle terminal must close");
        Assert.True(observed.ended, "the idle terminal was never ended");
    }

    [Fact]
    public async Task Busy_terminal_close_is_vetoed_and_shows_the_banner()
    {
        var observed = await RunAsync(busy: true, async scene =>
        {
            var allowed = await scene.Layout.TabCloseRequested!(scene.Tab);
            return (
                allowed,
                StillDocked: scene.Layout.FindTab(scene.Tab.Id) is not null,
                Active: scene.Layout.BottomTabs.ActiveTab == scene.Tab,
                PromptVisible: scene.ViewModel.IsClosePromptVisible,
                BannerVisible: TerminalUi.FindByAutomationId<Border>(scene.Window, "terminal-close-banner")?.IsVisible ?? false,
                PromptText: TerminalUi.FindByAutomationId<TextBlock>(scene.Window, "terminal-close-prompt")?.Text);
        });

        Assert.False(observed.allowed, "a busy terminal must veto the close");
        Assert.True(observed.StillDocked, "the vetoed tab left the bottom group");
        Assert.True(observed.Active, "the vetoed tab was not activated");
        Assert.True(observed.PromptVisible, "the close banner was not shown on the view model");
        Assert.True(observed.BannerVisible, "the close banner was not visible in the view");
        Assert.False(string.IsNullOrEmpty(observed.PromptText), "the banner has no prompt text");
    }

    [Fact]
    public async Task Confirming_the_banner_ends_the_process_and_closes_the_tab()
    {
        var observed = await RunAsync(busy: true, async scene =>
        {
            var allowed = await scene.Layout.TabCloseRequested!(scene.Tab);
            scene.ViewModel.ConfirmCloseCommand.Execute(null);
            var closed = await TerminalUi.WaitInsideAsync(
                scene.Window, () => scene.Layout.FindTab(scene.Tab.Id) is null, TimeSpan.FromSeconds(10));
            await Task.CompletedTask;
            return (allowed, closed, scene.ViewModel.IsClosePromptVisible);
        });

        Assert.False(observed.allowed, "a busy terminal must veto the close");
        Assert.True(observed.closed, "confirming never closed the tab");
        Assert.False(observed.Item3, "the banner stayed visible after confirming");
    }

    [Fact]
    public async Task Cancelling_the_banner_keeps_the_tab_and_the_process()
    {
        var observed = await RunAsync(busy: true, async scene =>
        {
            var allowed = await scene.Layout.TabCloseRequested!(scene.Tab);
            scene.ViewModel.CancelCloseCommand.Execute(null);
            await Task.CompletedTask;
            return (
                allowed,
                StillDocked: scene.Layout.FindTab(scene.Tab.Id) is not null,
                PromptVisible: scene.ViewModel.IsClosePromptVisible);
        });

        Assert.False(observed.allowed, "a busy terminal must veto the close");
        Assert.True(observed.StillDocked, "cancelling removed the tab");
        Assert.False(observed.PromptVisible, "cancelling left the banner visible");
    }

    [Fact]
    public async Task Closing_an_already_gone_tab_is_a_no_op_veto()
    {
        var observed = await RunAsync(busy: true, async scene =>
        {
            scene.Layout.CloseTab(scene.Tab);
            var allowed = await scene.Layout.TabCloseRequested!(scene.Tab);
            await Task.CompletedTask;
            return allowed;
        });

        Assert.False(observed, "a second close while the first is pending must not proceed");
    }
}
