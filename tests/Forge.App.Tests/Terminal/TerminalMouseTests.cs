using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Forge.Abstractions;
using Forge.App.Preferences;
using Forge.App.Terminal;
using Forge.App.Terminal.Vt;
using Xunit;
using MouseButton = Avalonia.Input.MouseButton;

namespace Forge.App.Tests.Terminal;

/// <summary>
/// Pins mouse reporting of <see cref="TerminalControl"/> (forge-app spec, "App/TerminalControl", pointer): with
/// 1002+1006 enabled, press/drag/release become the exact SGR sequences; 1000 reports press and release but no drag;
/// holding Shift keeps the press local (selection, no bytes); with reporting off a press only starts a local
/// selection and reports nothing.
/// </summary>
[Collection("forge-app-ui")]
public sealed class TerminalMouseTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(30);

    private sealed class Scene
    {
        private Scene(Window window, TerminalControl control, TerminalViewModel viewModel, FakeTerminalHost host)
        {
            Window = window;
            Control = control;
            ViewModel = viewModel;
            Host = host;
        }

        public Window Window { get; }

        public TerminalControl Control { get; }

        public TerminalViewModel ViewModel { get; }

        public FakeTerminalHost Host { get; }

        public static Scene Open()
        {
            var host = new FakeTerminalHost();
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
            return new Scene(window, control, viewModel, host);
        }

        public async Task<bool> WaitForRunningAsync() =>
            await TerminalUi.WaitInsideAsync(Window, () => ViewModel.Status == TerminalStatus.Running, TimeSpan.FromSeconds(10));

        public async Task EnableMouseAsync(string sequence, MouseTracking mode)
        {
            Host.Session.Emit(sequence);
            var enabled = await TerminalUi.WaitInsideAsync(
                Window,
                () => ViewModel.Emulator.MouseMode == mode && ViewModel.Emulator.MouseSgr,
                TimeSpan.FromSeconds(10));
            Assert.True(enabled, "mouse reporting never applied");
        }

        public void Mouse(Action<Point> act, int column, int row)
        {
            var at = Control.TranslatePoint(CellLocal(column, row), Window) ?? CellLocal(column, row);
            act(at);
            TerminalUi.Settle(Window);
        }

        public Task<byte[]> WrittenAsync() => TerminalUi.WrittenAfterBarrierAsync(ViewModel, Host.Session);

        public async ValueTask DisposeAsync()
        {
            Window.Close();
            await ViewModel.DisposeAsync();
        }

        private Point CellLocal(int column, int row)
        {
            Control.Measure(Size.Infinity);
            var cellWidth = Control.DesiredSize.Width / 80;
            var cellHeight = Control.DesiredSize.Height / 24;
            return new Point((column + 0.5) * cellWidth, (row + 0.5) * cellHeight);
        }
    }

    private static async Task<T> RunAsync<T>(Func<Scene, Task<T>> body)
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));
        return await TerminalUi.RunTestAsync(
            session,
            async () =>
            {
                var scene = Scene.Open();
                try
                {
                    Assert.True(await scene.WaitForRunningAsync(), "the terminal never reached Running");
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
    public async Task Press_drag_release_with_1002_and_1006_send_sgr_sequences()
    {
        var written = await RunAsync(async scene =>
        {
            await scene.EnableMouseAsync("\x1b[?1002h\x1b[?1006h", MouseTracking.ButtonEvent);
            scene.Mouse(at => scene.Window.MouseDown(at, MouseButton.Left), 4, 2);
            scene.Mouse(at => scene.Window.MouseMove(at), 6, 3);
            scene.Mouse(at => scene.Window.MouseUp(at, MouseButton.Left), 6, 3);
            await Task.CompletedTask;
            return await scene.WrittenAsync();
        });

        Assert.Equal("\x1b[<0;5;3M\x1b[<32;7;4M\x1b[<0;7;4m"u8.ToArray(), written);
    }

    [Fact]
    public async Task Mode_1000_reports_press_and_release_but_not_drag()
    {
        var observed = await RunAsync(async scene =>
        {
            await scene.EnableMouseAsync("\x1b[?1000h\x1b[?1006h", MouseTracking.Normal);
            scene.Mouse(at => scene.Window.MouseDown(at, MouseButton.Left), 4, 2);
            scene.Mouse(at => scene.Window.MouseMove(at), 6, 3);
            var afterDrag = await scene.WrittenAsync();
            scene.Mouse(at => scene.Window.MouseUp(at, MouseButton.Left), 6, 3);
            await Task.CompletedTask;
            return (afterDrag, await scene.WrittenAsync());
        });

        Assert.Equal("\x1b[<0;5;3M"u8.ToArray(), observed.afterDrag);
        Assert.Equal("\x1b[<0;5;3M\x1b[<0;7;4m"u8.ToArray(), observed.Item2);
    }

    [Fact]
    public async Task Shift_press_with_reporting_on_stays_a_local_selection()
    {
        var written = await RunAsync(async scene =>
        {
            await scene.EnableMouseAsync("\x1b[?1002h\x1b[?1006h", MouseTracking.ButtonEvent);
            scene.Mouse(at => scene.Window.MouseDown(at, MouseButton.Left, RawInputModifiers.Shift), 4, 2);
            scene.Mouse(at => scene.Window.MouseUp(at, MouseButton.Left, RawInputModifiers.Shift), 6, 3);
            await Task.CompletedTask;
            return await scene.WrittenAsync();
        });

        Assert.Empty(written);
    }

    [Fact]
    public async Task Press_with_reporting_off_reports_nothing()
    {
        var written = await RunAsync(async scene =>
        {
            Assert.True(
                scene.ViewModel.Emulator.MouseMode == MouseTracking.None,
                "mouse reporting should be off by default");
            scene.Mouse(at => scene.Window.MouseDown(at, MouseButton.Left), 4, 2);
            scene.Mouse(at => scene.Window.MouseMove(at), 6, 3);
            scene.Mouse(at => scene.Window.MouseUp(at, MouseButton.Left), 6, 3);
            await Task.CompletedTask;
            return await scene.WrittenAsync();
        });

        Assert.Empty(written);
    }
}
