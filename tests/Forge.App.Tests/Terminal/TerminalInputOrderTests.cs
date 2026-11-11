using System.Text;
using Avalonia.Controls;
using Avalonia.Headless;
using Forge.Abstractions;
using Forge.App.Preferences;
using Forge.App.Terminal;
using Xunit;

namespace Forge.App.Tests.Terminal;

/// <summary>
/// Keyboard input and the emulator's device answers share one ordered channel per terminal: whatever the interleaving,
/// the session receives the typed chunks in the order they were typed, every device answer arrives whole, and a slow write
/// holds later input back instead of letting it overtake.
/// </summary>
[Collection("forge-app-ui")]
public sealed class TerminalInputOrderTests
{
    private const string Da1Answer = "\x1b[?62;22c";
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(40);

    private sealed class Scene
    {
        private Scene(Window window, TerminalViewModel viewModel, FakeTerminalHost host)
        {
            Window = window;
            ViewModel = viewModel;
            Host = host;
        }

        public Window Window { get; }

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
            return new Scene(window, viewModel, host);
        }

        public async ValueTask DisposeAsync()
        {
            Window.Close();
            await ViewModel.DisposeAsync();
        }
    }

    private static async Task<T> RunAsync<T>(Func<Scene, Task<T>> body)
    {
        var session = SharedUi.Session;
        return await TerminalUi.RunTestAsync(
            session,
            async () =>
            {
                var scene = Scene.Open();
                try
                {
                    Assert.True(
                        await TerminalUi.WaitInsideAsync(scene.Window, () => scene.ViewModel.Status == TerminalStatus.Running, TimeSpan.FromSeconds(10)),
                        "the terminal never reached Running");
                    return await body(scene);
                }
                finally
                {
                    await scene.DisposeAsync();
                }
            },
            Budget);
    }

    private static int CountOccurrences(string text, string needle)
    {
        var count = 0;
        for (var at = text.IndexOf(needle, StringComparison.Ordinal); at >= 0; at = text.IndexOf(needle, at + needle.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    [Fact]
    public async Task Rapid_typed_chunks_interleaved_with_device_answers_reach_the_session_in_order_and_whole()
    {
        const int Rounds = 60;
        var observed = await RunAsync(async scene =>
        {
            var expectedKeys = new StringBuilder();
            for (var round = 0; round < Rounds; round++)
            {
                var chunk = $"[{round:000}]";
                expectedKeys.Append(chunk);
                scene.ViewModel.SendInput(Encoding.ASCII.GetBytes(chunk));

                // A DA1 query makes the emulator answer through the same input path.
                scene.Host.Session.Emit("\x1b[c");
            }

            var written = await scene.Host.Session.WaitForWrittenAsync(
                bytes =>
                {
                    var text = Encoding.Latin1.GetString(bytes);
                    return CountOccurrences(text, Da1Answer) >= Rounds && text.Replace(Da1Answer, string.Empty, StringComparison.Ordinal).Length >= expectedKeys.Length;
                },
                TimeSpan.FromSeconds(20));
            return (Text: Encoding.Latin1.GetString(written), Expected: expectedKeys.ToString());
        });

        Assert.Equal(Rounds, CountOccurrences(observed.Text, Da1Answer));
        // Taking the answers out leaves exactly the typed chunks, in typing order: no chunk was split, dropped or reordered.
        Assert.Equal(observed.Expected, observed.Text.Replace(Da1Answer, string.Empty, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_slow_write_holds_later_input_and_device_answers_back_instead_of_being_overtaken()
    {
        var observed = await RunAsync(async scene =>
        {
            var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var calls = 0;
            scene.Host.Session.WriteInterceptor = async _ =>
            {
                if (Interlocked.Increment(ref calls) == 1)
                {
                    firstStarted.TrySetResult();
                    await releaseFirst.Task.ConfigureAwait(false);
                }
            };

            scene.ViewModel.SendInput("A1"u8);
            await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

            // The first write is parked; everything below queues behind it.
            scene.ViewModel.SendInput("B2"u8);
            scene.Host.Session.Emit("\x1b[c");
            scene.ViewModel.SendInput("C3"u8);
            scene.ViewModel.SendInput("D4"u8);
            var bytesWhileParked = scene.Host.Session.WrittenSnapshot().Length;

            releaseFirst.TrySetResult();
            var written = await scene.Host.Session.WaitForWrittenAsync(
                bytes =>
                {
                    var text = Encoding.Latin1.GetString(bytes);
                    return text.Contains(Da1Answer, StringComparison.Ordinal) && text.Replace(Da1Answer, string.Empty, StringComparison.Ordinal).Length >= 8;
                },
                TimeSpan.FromSeconds(20));
            return (bytesWhileParked, Text: Encoding.Latin1.GetString(written));
        });

        Assert.Equal(0, observed.bytesWhileParked);
        Assert.StartsWith("A1", observed.Text, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(observed.Text, Da1Answer));
        Assert.Equal("A1B2C3D4", observed.Text.Replace(Da1Answer, string.Empty, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Input_after_a_failed_write_is_still_delivered_in_order()
    {
        var observed = await RunAsync(async scene =>
        {
            var calls = 0;
            scene.Host.Session.WriteInterceptor = _ =>
                Interlocked.Increment(ref calls) == 1
                    ? Task.FromException(new InvalidOperationException("the host rejected the first chunk"))
                    : Task.CompletedTask;

            scene.ViewModel.SendInput("X1"u8);
            scene.ViewModel.SendInput("Y2"u8);
            scene.ViewModel.SendInput("Z3"u8);
            var written = await scene.Host.Session.WaitForWrittenBytesAsync(4, TimeSpan.FromSeconds(10));
            return Encoding.ASCII.GetString(written);
        });

        // The first chunk is lost with its failed write (a dead session only leaves a diagnostic); the rest keeps its order.
        Assert.Equal("Y2Z3", observed);
    }

    [Fact]
    public async Task Input_after_a_write_that_timed_out_is_still_delivered_in_order()
    {
        var observed = await RunAsync(async scene =>
        {
            var calls = 0;
            scene.Host.Session.WriteInterceptor = _ =>
                Interlocked.Increment(ref calls) == 1
                    ? Task.FromException(new TimeoutException("the write timed out"))
                    : Task.CompletedTask;

            scene.ViewModel.SendInput("X1"u8);
            scene.ViewModel.SendInput("Y2"u8);
            scene.ViewModel.SendInput("Z3"u8);
            var written = await scene.Host.Session.WaitForWrittenBytesAsync(4, TimeSpan.FromSeconds(10));
            return Encoding.ASCII.GetString(written);
        });

        // An unexpected failure type must not end the writer: the first chunk is lost, the rest keeps its order.
        Assert.Equal("Y2Z3", observed);
    }
}
