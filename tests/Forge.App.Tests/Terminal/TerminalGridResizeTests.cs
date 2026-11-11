using Avalonia.Controls;
using Avalonia.Headless;
using Forge.Abstractions;
using Forge.App.Preferences;
using Forge.App.Terminal;
using Xunit;

namespace Forge.App.Tests.Terminal;

/// <summary>
/// Pins grid reporting of <see cref="TerminalControl"/> (forge-app spec, "App/TerminalControl", size): a size change
/// is debounced and then delivers the real column/row count to the session's <c>ResizeAsync</c>. The regression case is
/// a view that finishes layout while the host is still starting: the pending grid must be pushed once the session
/// exists instead of being silently dropped, or full-screen programs would keep the placeholder size forever.
/// </summary>
[Collection("forge-app-ui")]
public sealed class TerminalGridResizeTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(40);

    private sealed class Scene
    {
        private Scene(
            Window window,
            TerminalControl control,
            TerminalViewModel viewModel,
            FakeTerminalHost host,
            TaskCompletionSource<ITerminalSession>? gate)
        {
            Window = window;
            Control = control;
            ViewModel = viewModel;
            Host = host;
            Gate = gate;
        }

        public Window Window { get; }

        public TerminalControl Control { get; }

        public TerminalViewModel ViewModel { get; }

        public FakeTerminalHost Host { get; }

        public TaskCompletionSource<ITerminalSession>? Gate { get; }

        /// <summary>Opens a terminal whose host answers <see cref="ITerminalHost.StartAsync"/> only when released.</summary>
        public static Scene OpenGated()
        {
            var gate = new TaskCompletionSource<ITerminalSession>(TaskCreationOptions.RunContinuationsAsynchronously);
            var host = new FakeTerminalHost
            {
                StartHandler = async (_, ct) => await gate.Task.WaitAsync(ct),
            };
            return Open(host, gate);
        }

        public static Scene OpenImmediate() => Open(new FakeTerminalHost(), null);

        private static Scene Open(FakeTerminalHost host, TaskCompletionSource<ITerminalSession>? gate)
        {
            var viewModel = new TerminalViewModel(
                "s1",
                1,
                host,
                new TerminalStartRequest("s1", "C:\\work", new TerminalSize(100, 30)),
                new AppPreferences());
            var control = new TerminalControl { Source = viewModel };
            var window = new Window { Width = 1000, Height = 700, Content = control };
            viewModel.Start();
            TerminalUi.Show(window);
            return new Scene(window, control, viewModel, host, gate);
        }

        public async ValueTask DisposeAsync()
        {
            Window.Close();
            await ViewModel.DisposeAsync();
        }
    }

    private static async Task<T> RunAsync<T>(Func<Scene, Task<T>> body, Func<Scene>? open = null)
    {
        var session = SharedUi.Session;
        return await TerminalUi.RunTestAsync(
            session,
            async () =>
            {
                var scene = open is null ? Scene.OpenImmediate() : open();
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
    public async Task Initial_layout_reports_the_real_grid_not_the_placeholder()
    {
        var observed = await RunAsync(async scene =>
        {
            var running = await TerminalUi.WaitInsideAsync(
                scene.Window, () => scene.ViewModel.Status == TerminalStatus.Running, TimeSpan.FromSeconds(10));
            Assert.True(running, "the terminal never reached Running");
            var reported = await TerminalUi.WaitInsideAsync(
                scene.Window, () => scene.Host.Session.Resizes.Count > 0, TimeSpan.FromSeconds(10));
            return (
                reported,
                Reported: scene.Host.Session.Resizes.Count > 0 ? scene.Host.Session.Resizes[^1] : default,
                Grid: new TerminalSize(scene.ViewModel.Emulator.Columns, scene.ViewModel.Emulator.Rows));
        });

        Assert.True(observed.reported, "the initial grid never reached the session");
        Assert.Equal(observed.Grid, observed.Reported);
        Assert.NotEqual(new TerminalSize(100, 30), observed.Reported);
    }

    [Fact]
    public async Task Rapid_size_changes_coalesce_into_one_resize_with_the_final_grid()
    {
        var observed = await RunAsync(async scene =>
        {
            var running = await TerminalUi.WaitInsideAsync(
                scene.Window, () => scene.ViewModel.Status == TerminalStatus.Running, TimeSpan.FromSeconds(10));
            Assert.True(running, "the terminal never reached Running");
            var initial = await TerminalUi.WaitInsideAsync(
                scene.Window, () => scene.Host.Session.Resizes.Count > 0, TimeSpan.FromSeconds(10));
            Assert.True(initial, "the initial grid never reached the session");

            var before = scene.Host.Session.Resizes.Count;
            // Two changes inside one dispatcher pass: the 50 ms timer restarts and fires once.
            scene.Window.Width = 900;
            scene.Window.Width = 760;
            var settled = await TerminalUi.WaitInsideAsync(
                scene.Window, () => scene.Host.Session.Resizes.Count > before, TimeSpan.FromSeconds(10));
            return (
                settled,
                Count: scene.Host.Session.Resizes.Count - before,
                Reported: scene.Host.Session.Resizes.Count > before ? scene.Host.Session.Resizes[^1] : default,
                Grid: new TerminalSize(scene.ViewModel.Emulator.Columns, scene.ViewModel.Emulator.Rows));
        });

        Assert.True(observed.settled, "the resized grid never reached the session");
        Assert.Equal(1, observed.Count);
        Assert.Equal(observed.Grid, observed.Reported);
    }

    [Fact]
    public async Task Grid_computed_before_the_session_exists_is_pushed_once_the_host_answers()
    {
        var observed = await RunAsync(
            async scene =>
            {
                // The view lays out while the host is still starting: the emulator already leaves the placeholder...
                var laidOut = await TerminalUi.WaitInsideAsync(
                    scene.Window,
                    () => scene.ViewModel.Emulator.Columns != 100 || scene.ViewModel.Emulator.Rows != 30,
                    TimeSpan.FromSeconds(10));
                var starting = scene.ViewModel.Status;

                // ...and the resize that no session could receive yet must go out once the host answers.
                scene.Gate!.TrySetResult(scene.Host.Session);
                var running = await TerminalUi.WaitInsideAsync(
                    scene.Window, () => scene.ViewModel.Status == TerminalStatus.Running, TimeSpan.FromSeconds(10));
                var reported = await TerminalUi.WaitInsideAsync(
                    scene.Window, () => scene.Host.Session.Resizes.Count > 0, TimeSpan.FromSeconds(10));
                return (
                    laidOut,
                    starting,
                    running,
                    reported,
                    Reported: scene.Host.Session.Resizes.Count > 0 ? scene.Host.Session.Resizes[^1] : default,
                    Grid: new TerminalSize(scene.ViewModel.Emulator.Columns, scene.ViewModel.Emulator.Rows));
            },
            Scene.OpenGated);

        Assert.True(observed.laidOut, "the laid-out grid never replaced the placeholder");
        Assert.Equal(TerminalStatus.Starting, observed.starting);
        Assert.True(observed.running, "the terminal never reached Running");
        Assert.True(observed.reported, "the pre-session grid was dropped instead of reported");
        Assert.Equal(observed.Grid, observed.Reported);
    }
}
