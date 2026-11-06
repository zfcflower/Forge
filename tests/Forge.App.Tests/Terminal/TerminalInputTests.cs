using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Forge.Abstractions;
using Forge.App.Preferences;
using Forge.App.Terminal;
using Forge.App.Terminal.Vt;
using Xunit;

namespace Forge.App.Tests.Terminal;

/// <summary>
/// Pins the key-to-bytes path of <see cref="TerminalControl"/> (forge-app spec, "App/TerminalControl", keyboard and
/// text input): every key gesture is driven through the real headless window input and the exact bytes arriving at the
/// session's <c>WriteAsync</c> are asserted — Enter/Tab/arrows, Ctrl+letters, Alt letters, application-cursor mode,
/// IME text as UTF-8, and bracketed paste wrapping with CRLF normalization.
/// </summary>
[Collection("forge-app-ui")]
public sealed class TerminalInputTests
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

        public void Press(PhysicalKey key, RawInputModifiers modifiers = RawInputModifiers.None)
        {
            Window.KeyPressQwerty(key, modifiers);
            TerminalUi.Settle(Window);
        }

        public void Text(string text)
        {
            Window.KeyTextInput(text);
            TerminalUi.Settle(Window);
        }

        public void Feed(string text)
        {
            Host.Session.Emit(text);
            TerminalUi.Settle(Window);
        }

        public async Task PasteAsync()
        {
            var clipboard = TopLevel.GetTopLevel(Window)?.Clipboard;
            Assert.NotNull(clipboard);
            await clipboard!.SetTextAsync(PasteText);
            Window.KeyPressQwerty(PhysicalKey.V, RawInputModifiers.Control | RawInputModifiers.Shift);
            TerminalUi.Settle(Window);
        }

        public string PasteText { get; set; } = string.Empty;

        public byte[] Written() => Host.Session.WrittenBytes();

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
    public async Task Enter_tab_and_arrows_send_their_exact_bytes()
    {
        var written = await RunAsync(async scene =>
        {
            scene.Press(PhysicalKey.Enter);
            scene.Press(PhysicalKey.Tab);
            scene.Press(PhysicalKey.ArrowUp);
            scene.Press(PhysicalKey.ArrowDown);
            scene.Press(PhysicalKey.ArrowLeft);
            scene.Press(PhysicalKey.ArrowRight);
            scene.Press(PhysicalKey.Escape);
            scene.Press(PhysicalKey.F5);
            await Task.CompletedTask;
            return scene.Written();
        });

        Assert.Equal(
            "\r\t\x1b[A\x1b[B\x1b[D\x1b[C\x1b\x1b[15~"u8.ToArray(),
            written);
    }

    [Fact]
    public async Task Control_letters_send_control_codes_and_alt_prefixes_escape()
    {
        var written = await RunAsync(async scene =>
        {
            scene.Press(PhysicalKey.C, RawInputModifiers.Control);
            scene.Press(PhysicalKey.D, RawInputModifiers.Control);
            scene.Press(PhysicalKey.B, RawInputModifiers.Alt);
            await Task.CompletedTask;
            return scene.Written();
        });

        Assert.Equal(["\x03", "\x04", "\x1b" + "b"], ToChunks(written, [1, 1, 2]));
    }

    [Fact]
    public async Task Shift_tab_sends_back_tab_and_control_tab_sends_plain_tab()
    {
        var written = await RunAsync(async scene =>
        {
            scene.Press(PhysicalKey.Tab, RawInputModifiers.Shift);
            scene.Press(PhysicalKey.Tab, RawInputModifiers.Control);
            await Task.CompletedTask;
            return scene.Written();
        });

        Assert.Equal("\x1b[Z\t"u8.ToArray(), written);
    }

    [Fact]
    public async Task Application_cursor_mode_switches_arrows_to_ss3()
    {
        var written = await RunAsync(async scene =>
        {
            scene.Press(PhysicalKey.ArrowUp);
            scene.Feed("\x1b[?1h");
            var applied = await TerminalUi.WaitInsideAsync(
                scene.Window,
                () => (scene.ViewModel.Emulator.Modes & TerminalModes.ApplicationCursorKeys) != 0,
                TimeSpan.FromSeconds(10));
            Assert.True(applied, "DECCKM never applied");
            scene.Press(PhysicalKey.ArrowUp);
            scene.Press(PhysicalKey.ArrowUp, RawInputModifiers.Control);
            return scene.Written();
        });

        Assert.Equal("\x1b[A\x1bOA\x1b[1;5A"u8.ToArray(), written);
    }

    [Fact]
    public async Task Typed_text_is_sent_as_utf8_and_control_text_is_not_duplicated()
    {
        var observed = await RunAsync(async scene =>
        {
            scene.Text("h\x00e9\x4e2d");
            var first = scene.Written();

            var before = first.Length;
            scene.Text("\r");
            var after = scene.Written();
            await Task.CompletedTask;
            return (first, before, after);
        });

        Assert.Equal("h\x00e9\x4e2d"u8.ToArray(), observed.first);
        // A lone carriage return through TextInput must not duplicate the Enter key path.
        Assert.Equal(observed.before, observed.after.Length);
    }

    [Fact]
    public async Task Bracketed_paste_wraps_and_normalizes_newlines()
    {
        var written = await RunAsync(async scene =>
        {
            scene.Feed("\x1b[?2004h");
            var bracketed = await TerminalUi.WaitInsideAsync(
                scene.Window,
                () => (scene.ViewModel.Emulator.Modes & TerminalModes.BracketedPaste) != 0,
                TimeSpan.FromSeconds(10));
            Assert.True(bracketed, "bracketed paste never applied");
            scene.PasteText = "a\r\nb\nc";
            await scene.PasteAsync();
            return scene.Written();
        });

        Assert.Equal("\x1b[200~a\rb\rc\x1b[201~"u8.ToArray(), written);
    }

    [Fact]
    public async Task Paste_without_bracketed_mode_sends_raw_text_with_normalized_newlines()
    {
        var written = await RunAsync(async scene =>
        {
            scene.PasteText = "x\r\ny";
            await scene.PasteAsync();
            return scene.Written();
        });

        Assert.Equal("x\ry"u8.ToArray(), written);
    }

    private static string[] ToChunks(byte[] written, int[] sizes)
    {
        var chunks = new string[sizes.Length];
        var offset = 0;
        for (var i = 0; i < sizes.Length; i++)
        {
            chunks[i] = System.Text.Encoding.Latin1.GetString(written, offset, sizes[i]);
            offset += sizes[i];
        }

        Assert.Equal(written.Length, offset);
        return chunks;
    }
}
