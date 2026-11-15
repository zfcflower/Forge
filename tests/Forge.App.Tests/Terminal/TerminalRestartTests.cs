using Avalonia.Controls;
using Forge.Abstractions;
using Forge.App.Localization;
using Forge.App.Preferences;
using Forge.App.Terminal;
using Forge.App.Terminal.Vt;
using Forge.HostClient;
using Xunit;

namespace Forge.App.Tests.Terminal;

/// <summary>
/// Pins the exited-terminal restart (forge-app spec, "App/TerminalViewModel"): the restart entry only appears once the
/// terminal has exited or failed, keeps the tab's screen, and starts a fresh process over the same shell/arguments/working
/// directory. A restart that cannot start reports the same localized failure instead of throwing.
/// </summary>
[Collection("forge-app-ui")]
public sealed class TerminalRestartTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(20);

    private sealed class Scene
    {
        private Scene(Window window, FakeTerminalHost host, TerminalViewModel viewModel)
        {
            Window = window;
            Host = host;
            ViewModel = viewModel;
        }

        public Window Window { get; }

        public FakeTerminalHost Host { get; }

        public TerminalViewModel ViewModel { get; }

        public static Scene Open(string? shell = null, string[]? arguments = null, bool noHost = false, Action<FakeTerminalHost>? configure = null)
        {
            var preferences = new AppPreferences();
            var fake = new FakeTerminalHost();
            configure?.Invoke(fake);
            var viewModel = new TerminalViewModel(
                "s1",
                3,
                noHost ? null : fake,
                new TerminalStartRequest(
                    "s1",
                    "C:\\work",
                    new TerminalSize(100, 30),
                    shell,
                    arguments),
                preferences);
            var window = new Window { Width = 100, Height = 100 };
            viewModel.Start();
            TerminalUi.Show(window);
            return new Scene(window, fake, viewModel);
        }

        public Task<bool> WaitForAsync(Func<bool> condition) =>
            TerminalUi.WaitInsideAsync(Window, condition, TimeSpan.FromSeconds(10));

        public async ValueTask DisposeAsync()
        {
            Window.Close();
            await ViewModel.DisposeAsync();
        }
    }

    [Fact]
    public async Task Restart_appears_only_after_exit_and_reuses_the_same_configuration()
    {
        var sessions = new List<FakeTerminalSession>();
        await RunAsync(async scene =>
        {
            Assert.True(await scene.WaitForAsync(() => scene.ViewModel.Status == TerminalStatus.Running));
            Assert.False(scene.ViewModel.CanRestart);

            sessions[0].Emit("first-run");
            sessions[0].Exit(0);
            Assert.True(await scene.WaitForAsync(() => scene.ViewModel.Status == TerminalStatus.Exited));
            Assert.True(scene.ViewModel.CanRestart);

            scene.ViewModel.RestartCommand.Execute(null);
            Assert.True(await scene.WaitForAsync(() => scene.ViewModel.Status == TerminalStatus.Running));
            Assert.False(scene.ViewModel.CanRestart);

            Assert.Equal(2, scene.Host.Starts.Count);
            Assert.Equal(scene.Host.Starts[0].WorkingDirectory, scene.Host.Starts[1].WorkingDirectory);
            Assert.Equal(scene.Host.Starts[0].Shell, scene.Host.Starts[1].Shell);
            Assert.Equal(scene.Host.Starts[0].Arguments, scene.Host.Starts[1].Arguments);
            Assert.Contains("first-run", SnapshotText(scene.ViewModel), StringComparison.Ordinal);
        },
        configure: fake =>
        {
            fake.StartHandler = (_, _) =>
            {
                var session = new FakeTerminalSession();
                sessions.Add(session);
                return ValueTask.FromResult<ITerminalSession>(session);
            };
        });
    }

    [Fact]
    public async Task Restart_of_a_failed_terminal_fails_again_with_the_localized_reason_and_never_throws()
    {
        await RunAsync(async scene =>
        {
            Assert.True(await scene.WaitForAsync(() => scene.ViewModel.Status == TerminalStatus.Failed));
            Assert.True(scene.ViewModel.CanRestart);
            var expected = Loc.Instance[TerminalStrings.FailureHostUnavailable];
            Assert.Contains(expected, scene.ViewModel.StatusText, StringComparison.Ordinal);

            scene.ViewModel.RestartCommand.Execute(null);
            Assert.True(await scene.WaitForAsync(() => scene.ViewModel.Status == TerminalStatus.Failed));
            Assert.Contains(expected, scene.ViewModel.StatusText, StringComparison.Ordinal);
        }, noHost: true);
    }

    private static Task<int> RunAsync(Func<Scene, Task> body, bool noHost = false, Action<FakeTerminalHost>? configure = null) =>
        TerminalUi.RunTestAsync(
            SharedUi.Session,
            async () =>
            {
                var scene = Scene.Open(shell: "pwsh", arguments: ["-l", "-i"], noHost: noHost, configure: configure);
                try
                {
                    await body(scene);
                    return 0;
                }
                finally
                {
                    await scene.DisposeAsync();
                }
            },
            Budget);

    private static string SnapshotText(TerminalViewModel viewModel)
    {
        lock (viewModel.Gate)
        {
            var emulator = viewModel.Emulator;
            return TerminalText.GetText(emulator, (0, 0), (emulator.TotalLines - 1, emulator.Columns - 1));
        }
    }
}
