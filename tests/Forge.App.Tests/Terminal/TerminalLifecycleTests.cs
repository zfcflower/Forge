using System.Threading.Channels;
using Avalonia.Controls;
using Avalonia.Headless;
using Forge.Abstractions;
using Forge.App.Localization;
using Forge.App.Preferences;
using Forge.App.Terminal;
using Forge.Ipc;
using Xunit;

namespace Forge.App.Tests.Terminal;

/// <summary>
/// Pins <see cref="TerminalViewModel"/> lifetime (forge-app spec, "App/TerminalViewModel"): starting → running →
/// OSC-title → exited-with-code, and every failed start lands in <see cref="TerminalStatus.Failed"/> with a localized
/// reason instead of throwing. A second group pins the disconnect contract: when the session's output completes with a
/// <see cref="ChannelClosedException"/> that wraps an <see cref="IpcConnectionClosedException"/>, the view model shows
/// the <em>inner</em> message and never hangs waiting for bytes that will never come.
///
/// Headless note: every test runs its UI work inside a single dispatch with a shown window. The pump reports state
/// through a UI-thread callback, and such a callback does not reliably survive into the next dispatch (a shown window
/// changes which dispatcher a later dispatch pumps), so starting, waiting, acting and cleaning up share one dispatch.
/// </summary>
[Collection("forge-app-ui")]
public sealed class TerminalLifecycleTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(20);

    private sealed class Scene
    {
        private Scene(Window window, AppPreferences preferences, FakeTerminalHost host, TerminalViewModel viewModel)
        {
            Window = window;
            Preferences = preferences;
            Host = host;
            ViewModel = viewModel;
        }

        public Window Window { get; }

        public AppPreferences Preferences { get; }

        public FakeTerminalHost Host { get; }

        public TerminalViewModel ViewModel { get; }

        public static Scene Open(
            string workDir = "C:\\work",
            int displayIndex = 3,
            bool noHost = false,
            Action<FakeTerminalHost>? configure = null)
        {
            var preferences = new AppPreferences();
            var fake = new FakeTerminalHost();
            configure?.Invoke(fake);
            var viewModel = new TerminalViewModel(
                "s1",
                displayIndex,
                noHost ? null : fake,
                new TerminalStartRequest("s1", workDir, new TerminalSize(100, 30)),
                preferences);
            var window = new Window { Width = 100, Height = 100 };
            viewModel.Start();
            TerminalUi.Show(window);
            return new Scene(window, preferences, fake, viewModel);
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
    public async Task Start_reaches_running_with_an_empty_status_line()
    {
        var status = await RunAsync(async scene =>
        {
            var running = await scene.WaitForAsync(() => scene.ViewModel.Status == TerminalStatus.Running);
            return (running, scene.ViewModel.Status, scene.ViewModel.StatusText, scene.ViewModel.ShowStatus);
        });

        Assert.True(status.running, "the terminal never reached Running");
        Assert.Equal(TerminalStatus.Running, status.Status);
        Assert.Equal(string.Empty, status.StatusText);
        Assert.False(status.ShowStatus);
    }

    [Fact]
    public async Task Osc_title_becomes_the_tab_title()
    {
        var observed = await RunAsync(async scene =>
        {
            var titles = new List<string>();
            scene.ViewModel.TitleChanged += (_, _) => titles.Add(scene.ViewModel.TabTitle);
            var running = await scene.WaitForAsync(() => scene.ViewModel.Status == TerminalStatus.Running);

            scene.Host.Session.Emit("\x1b]0;shell-42\x07");
            var titled = await scene.WaitForAsync(() => scene.ViewModel.Title == "shell-42");
            return (running, titled, scene.ViewModel.Title, scene.ViewModel.TabTitle, titles);
        });

        Assert.True(observed.running, "the terminal never reached Running");
        Assert.True(observed.titled, "the OSC title never arrived");
        Assert.Equal("shell-42", observed.Title);
        Assert.Equal("shell-42", observed.TabTitle);
        Assert.NotEmpty(observed.titles);
    }

    [Fact]
    public async Task Exit_reports_the_code_in_a_localized_line()
    {
        var observed = await RunAsync(async scene =>
        {
            var running = await scene.WaitForAsync(() => scene.ViewModel.Status == TerminalStatus.Running);

            scene.Host.Session.Exit(3);
            var exited = await scene.WaitForAsync(() => scene.ViewModel.Status == TerminalStatus.Exited);
            return (
                running,
                exited,
                scene.ViewModel.Status,
                scene.ViewModel.StatusText,
                scene.ViewModel.ShowStatus,
                ExpectedExit: Loc.Instance.Format(TerminalStrings.StatusExited, 3));
        });

        Assert.True(observed.running, "the terminal never reached Running");
        Assert.True(observed.exited, "the terminal never reported its exit");
        Assert.Equal(TerminalStatus.Exited, observed.Status);
        Assert.True(observed.ShowStatus);
        Assert.Equal(observed.ExpectedExit, observed.StatusText);
        Assert.Contains("3", observed.StatusText);
    }

    [Fact]
    public async Task Missing_host_fails_with_the_localized_reason_and_never_throws()
    {
        var observed = await RunAsync(async scene =>
        {
            var failed = await scene.WaitForAsync(() => scene.ViewModel.Status == TerminalStatus.Failed);
            return (
                failed,
                scene.ViewModel.Status,
                scene.ViewModel.StatusText,
                Expected: Loc.Instance.Format(
                    TerminalStrings.StatusFailed,
                    Loc.Instance[TerminalStrings.FailureHostUnavailable]));
        }, noHost: true);

        Assert.True(observed.failed, "a host-less terminal never failed");
        Assert.Equal(TerminalStatus.Failed, observed.Status);
        Assert.Equal(observed.Expected, observed.StatusText);
    }

    [Fact]
    public async Task Unknown_terminal_fails_with_the_host_message_and_never_throws()
    {
        var observed = await RunAsync(
            async scene =>
            {
                var failed = await scene.WaitForAsync(() => scene.ViewModel.Status == TerminalStatus.Failed);
                return (failed, scene.ViewModel.StatusText);
            },
            configure: static fake => fake.StartHandler = (_, _) => throw new InvalidOperationException("unknown terminal: dead-beef"));

        Assert.True(observed.failed, "a rejected start never failed");
        Assert.Contains("unknown terminal: dead-beef", observed.StatusText);
    }

    [Fact]
    public async Task Empty_working_directory_fails_without_asking_the_host()
    {
        var observed = await RunAsync(
            async scene =>
            {
                var failed = await scene.WaitForAsync(() => scene.ViewModel.Status == TerminalStatus.Failed);
                return (
                    failed,
                    scene.ViewModel.StatusText,
                    Starts: scene.Host.Starts.Count,
                    Expected: Loc.Instance.Format(
                        TerminalStrings.StatusFailed,
                        Loc.Instance[TerminalStrings.FailureNoSession]));
            },
            workDir: string.Empty);

        Assert.True(observed.failed, "an empty working directory never failed");
        Assert.Equal(observed.Expected, observed.StatusText);
        Assert.Equal(0, observed.Starts);
    }

    [Fact]
    public async Task Connection_error_wrapping_completes_as_failed_with_the_inner_message()
    {
        var observed = await RunAsync(async scene =>
        {
            var running = await scene.WaitForAsync(() => scene.ViewModel.Status == TerminalStatus.Running);

            scene.Host.Session.FailOutput(
                new ChannelClosedException(new IpcConnectionClosedException("pipe-broke-7")));
            var failed = await scene.WaitForAsync(() => scene.ViewModel.Status == TerminalStatus.Failed);
            return (running, failed, scene.ViewModel.Status, scene.ViewModel.StatusText, scene.ViewModel.ShowStatus);
        });

        Assert.True(observed.running, "the terminal never reached Running");
        Assert.True(observed.failed, "the view model hung instead of failing on the connection error");
        Assert.Equal(TerminalStatus.Failed, observed.Status);
        Assert.True(observed.ShowStatus);
        // The consumer must unwrap InnerException: the wrapper's own message is generic and must not leak through.
        Assert.Contains("pipe-broke-7", observed.StatusText);
    }

    [Fact]
    public async Task Faulted_exit_completes_as_failed_with_the_transport_message()
    {
        var observed = await RunAsync(async scene =>
        {
            var running = await scene.WaitForAsync(() => scene.ViewModel.Status == TerminalStatus.Running);

            scene.Host.Session.FailExit(new IpcConnectionClosedException("host-gone-9"));
            var failed = await scene.WaitForAsync(() => scene.ViewModel.Status == TerminalStatus.Failed);
            return (running, failed, scene.ViewModel.StatusText);
        });

        Assert.True(observed.running, "the terminal never reached Running");
        Assert.True(observed.failed, "a faulted Exited never failed");
        Assert.Contains("host-gone-9", observed.StatusText);
    }

    private static async Task<T> RunAsync<T>(
        Func<Scene, Task<T>> body,
        string workDir = "C:\\work",
        bool noHost = false,
        Action<FakeTerminalHost>? configure = null)
    {
        var session = SharedUi.Session;
        return await TerminalUi.RunTestAsync(
            session,
            async () =>
            {
                var scene = Scene.Open(workDir, noHost: noHost, configure: configure);
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
}
