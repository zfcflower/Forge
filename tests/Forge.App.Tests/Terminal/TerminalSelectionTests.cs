using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Forge.Abstractions;
using Forge.App.Preferences;
using Forge.App.Terminal;
using Xunit;

namespace Forge.App.Tests.Terminal;

/// <summary>
/// Pins selection and copy of <see cref="TerminalControl"/> (forge-app spec, "App/TerminalControl", selection):
/// a drag copies the exact range, soft-wrapped rows join without a newline, wide characters appear once, and
/// double/triple clicks select the word/line under the pointer. All input is real headless pointer and key input;
/// the double click is synthesized with the real device pointer because the headless driver only emits single clicks.
/// </summary>
[Collection("forge-app-ui")]
public sealed class TerminalSelectionTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(40);

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

        public async Task<bool> WaitForScreenAsync(string rowText, int row) =>
            await TerminalUi.WaitInsideAsync(
                Window,
                () => VtHelpers.RowText(ViewModel.Emulator, row).StartsWith(rowText, StringComparison.Ordinal),
                TimeSpan.FromSeconds(10));

        public async Task CopyAfterDragAsync(int fromColumn, int fromRow, int toColumn, int toRow)
        {
            var from = Control.TranslatePoint(CellLocal(fromColumn, fromRow), Window) ?? CellLocal(fromColumn, fromRow);
            var to = Control.TranslatePoint(CellLocal(toColumn, toRow), Window) ?? CellLocal(toColumn, toRow);
            Window.MouseDown(from, MouseButton.Left);
            Window.MouseMove(to);
            Window.MouseUp(to, MouseButton.Left);
            TerminalUi.Settle(Window);
            Window.KeyPressQwerty(PhysicalKey.C, RawInputModifiers.Control | RawInputModifiers.Shift);
            TerminalUi.Settle(Window);
            await Task.CompletedTask;
        }

        /// <summary>
        /// A double or triple click at a cell, built from the real device pointer: the headless driver only emits
        /// single clicks, while the OS double click is exactly this event with a higher <c>ClickCount</c>.
        /// </summary>
        public void ClickCount(int column, int row, int clickCount)
        {
            IPointer? device = null;
            void Capture(object? sender, PointerPressedEventArgs e) => device ??= e.Pointer;
            try
            {
                // The control marks the press handled, so the capture must opt into handled events.
                Control.AddHandler(InputElement.PointerPressedEvent, Capture, handledEventsToo: true);
                var probe = Control.TranslatePoint(CellLocal(column, row), Window) ?? CellLocal(column, row);
                Window.MouseDown(probe, MouseButton.Left);
                Window.MouseUp(probe, MouseButton.Left);
                TerminalUi.Settle(Window);
                Assert.NotNull(device);

                var at = Control.TranslatePoint(CellLocal(column, row), Window) ?? CellLocal(column, row);
                var pressed = new PointerPressedEventArgs(
                    Control,
                    device!,
                    Window,
                    at,
                    0ul,
                    new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonPressed),
                    KeyModifiers.None,
                    clickCount);
                Control.RaiseEvent(pressed);

                var released = new PointerReleasedEventArgs(
                    Control,
                    device!,
                    Window,
                    at,
                    0ul,
                    new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased),
                    KeyModifiers.None,
                    MouseButton.Left);
                Control.RaiseEvent(released);
                TerminalUi.Settle(Window);
            }
            finally
            {
                Control.RemoveHandler(InputElement.PointerPressedEvent, Capture);
            }
        }

        public void Copy()
        {
            Window.KeyPressQwerty(PhysicalKey.C, RawInputModifiers.Control | RawInputModifiers.Shift);
            TerminalUi.Settle(Window);
        }

        public async Task<string?> ClipboardTextAsync()
        {
            var clipboard = TopLevel.GetTopLevel(Window)?.Clipboard;
            return clipboard is null ? null : await clipboard.TryGetTextAsync();
        }

        private Point CellLocal(int column, int row)
        {
            Control.Measure(Size.Infinity);
            var cellWidth = Control.DesiredSize.Width / 80;
            var cellHeight = Control.DesiredSize.Height / 24;
            return new Point((column + 0.5) * cellWidth, (row + 0.5) * cellHeight);
        }

        public async ValueTask DisposeAsync()
        {
            Window.Close();
            await ViewModel.DisposeAsync();
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
    public async Task Drag_selects_the_range_and_copy_returns_its_exact_text()
    {
        var text = await RunAsync(async scene =>
        {
            scene.Host.Session.Emit("hello world\r\nsecond line");
            Assert.True(await scene.WaitForScreenAsync("hello world", 0), "the first row never arrived");
            Assert.True(await scene.WaitForScreenAsync("second line", 1), "the second row never arrived");

            await scene.CopyAfterDragAsync(0, 0, 5, 1);
            return await scene.ClipboardTextAsync();
        });

        Assert.Equal("hello world\nsecond", text);
    }

    [Fact]
    public async Task Soft_wrapped_rows_copy_without_a_newline()
    {
        var text = await RunAsync(async scene =>
        {
            // Emit only after the laid-out grid reached the emulator, so the wrap width is deterministic.
            var laidOut = await TerminalUi.WaitInsideAsync(
                scene.Window,
                () => scene.Host.Session.Resizes.Count > 0,
                TimeSpan.FromSeconds(10));
            Assert.True(laidOut, "the laid-out grid never replaced the placeholder");
            var columns = scene.ViewModel.Emulator.Columns;
            var wrapped = new string('w', columns + 50);
            scene.Host.Session.Emit(wrapped);
            Assert.True(await scene.WaitForScreenAsync(wrapped[..columns], 0), "the wrapped row never arrived");

            await scene.CopyAfterDragAsync(0, 0, 49, 1);
            return (await scene.ClipboardTextAsync(), wrapped);
        });

        Assert.Equal(text.wrapped, text.Item1);
    }

    [Fact]
    public async Task Wide_characters_copy_once_not_per_cell()
    {
        var text = await RunAsync(async scene =>
        {
            scene.Host.Session.Emit("a\u4e2db");
            Assert.True(await scene.WaitForScreenAsync("a", 0), "the wide row never arrived");

            // a=col 0, the wide rune covers cols 1-2, b=col 3.
            await scene.CopyAfterDragAsync(0, 0, 3, 0);
            return await scene.ClipboardTextAsync();
        });

        Assert.Equal("a\u4e2db", text);
    }

    [Fact]
    public async Task Double_click_selects_the_word_and_triple_click_the_line()
    {
        var observed = await RunAsync(async scene =>
        {
            scene.Host.Session.Emit("hello world foo\r\nsecond line here");
            Assert.True(await scene.WaitForScreenAsync("hello world", 0), "the rows never arrived");

            scene.ClickCount(7, 0, clickCount: 2);
            scene.Copy();
            var word = await scene.ClipboardTextAsync();

            scene.ClickCount(3, 1, clickCount: 3);
            scene.Copy();
            var line = await scene.ClipboardTextAsync();
            return (word, line);
        });

        Assert.Equal("world", observed.word);
        Assert.Equal("second line here", observed.line);
    }
}
