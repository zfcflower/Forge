using Avalonia.Controls;
using Avalonia.Headless;
using Forge.Abstractions;
using Forge.App.Preferences;
using Forge.App.Terminal;
using Xunit;

namespace Forge.App.Tests.Terminal;

/// <summary>
/// Pins live preferences (forge-app spec, "App/TerminalViewModel" and "App/TerminalWorkbench"): lowering
/// <c>TerminalScrollbackLines</c> trims the ring of an already-running terminal immediately, the setter clamps to
/// 1000–100000, and the shell/arguments preferences decide the next terminal's start request. The tokenizer itself is
/// pinned with its boundary cases.
/// </summary>
[Collection("forge-app-ui")]
public sealed class TerminalPreferenceTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(40);

    [Fact]
    public async Task Lowering_scrollback_trims_the_live_ring()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));
        var observed = await TerminalUi.RunTestAsync(
            session,
            async () =>
            {
                var preferences = new AppPreferences { TerminalScrollbackLines = 10000 };
                    var host = new FakeTerminalHost();
                    var viewModel = new TerminalViewModel(
                        "s1",
                        1,
                        host,
                        new TerminalStartRequest("s1", "C:\\work", new TerminalSize(80, 24)),
                        preferences);
                    var window = new Window { Width = 100, Height = 100 };
                    viewModel.Start();
                    TerminalUi.Show(window);
                    try
                    {
                        var running = await TerminalUi.WaitInsideAsync(
                            window, () => viewModel.Status == TerminalStatus.Running, TimeSpan.FromSeconds(10));
                        Assert.True(running, "the terminal never reached Running");

                        for (var i = 0; i < 1500; i++)
                        {
                            host.Session.Emit("line " + i + "\r\n");
                        }

                        var grown = await TerminalUi.WaitInsideAsync(
                            window, () => viewModel.Emulator.ScrollbackCount > 1000, TimeSpan.FromSeconds(10));
                        Assert.True(grown, "the scrollback never grew past the trim target");

                        preferences.TerminalScrollbackLines = 1000;
                        var trimmed = await TerminalUi.WaitInsideAsync(
                            window, () => viewModel.Emulator.ScrollbackCount == 1000, TimeSpan.FromSeconds(10));
                        return (
                            trimmed,
                            Limit: viewModel.Emulator.ScrollbackLimit,
                            Count: viewModel.Emulator.ScrollbackCount);
                    }
                    finally
                    {
                        window.Close();
                        await viewModel.DisposeAsync();
                    }
                },
                Budget);

        Assert.True(observed.trimmed, "lowering the preference never trimmed the ring");
        Assert.Equal(1000, observed.Limit);
        Assert.Equal(1000, observed.Count);
    }

    [Fact]
    public async Task Scrollback_preference_clamps_to_1000_and_100000()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));
        var clamped = await TerminalUi.RunTestAsync(
            session,
            async () =>
            {
                var preferences = new AppPreferences();
                preferences.TerminalScrollbackLines = 5;
                    var low = preferences.TerminalScrollbackLines;
                    preferences.TerminalScrollbackLines = 200_000;
                    var high = preferences.TerminalScrollbackLines;
                    preferences.TerminalScrollbackLines = 5000;
                    await Task.CompletedTask;
                    return (low, high, preferences.TerminalScrollbackLines);
                },
                TimeSpan.FromSeconds(10));

            Assert.Equal(AppPreferences.MinScrollbackLines, clamped.low);
            Assert.Equal(1000, clamped.low);
            Assert.Equal(AppPreferences.MaxScrollbackLines, clamped.high);
            Assert.Equal(100_000, clamped.high);
        Assert.Equal(5000, clamped.Item3);
    }

    [Fact]
    public async Task Scrollback_section_clamps_before_reaching_the_preferences()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));
        var clamped = await TerminalUi.RunTestAsync(
            session,
            async () =>
            {
                var preferences = new AppPreferences();
                var section = new TerminalSettingsViewModel(preferences);
                    section.ScrollbackLines = 5;
                    var low = preferences.TerminalScrollbackLines;
                    section.ScrollbackLines = 250_000;
                    await Task.CompletedTask;
                    return (low, preferences.TerminalScrollbackLines);
                },
                TimeSpan.FromSeconds(10));

        Assert.Equal(1000, clamped.low);
        Assert.Equal(100_000, clamped.Item2);
    }

    [Theory]
    [InlineData(null, new string[0])]
    [InlineData("", new string[0])]
    [InlineData("   ", new string[0])]
    [InlineData("-l", new[] { "-l" })]
    [InlineData("-l -i", new[] { "-l", "-i" })]
    [InlineData("  -l   -i  ", new[] { "-l", "-i" })]
    [InlineData("-l -i --title \"my shell\"", new[] { "-l", "-i", "--title", "my shell" })]
    [InlineData("\"a b", new[] { "a b" })]
    [InlineData("\"\"", new[] { "" })]
    [InlineData("\"a\"b", new[] { "ab" })]
    public void Command_line_splits_on_spaces_with_double_quote_grouping(string? text, string[] expected) =>
        Assert.Equal(expected, TerminalCommandLine.Split(text));

    [Fact]
    public async Task New_terminals_use_the_current_shell_and_arguments()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));
        var observed = await TerminalUi.RunTestAsync(
            session,
            async () =>
            {
                var preferences = new AppPreferences
                {
                    TerminalShell = "  pwsh  ",
                    TerminalArguments = "-l -i --title \"my shell\"",
                };
                    var host = new FakeTerminalHost();
                    var layout = new Forge.App.Workbench.WorkbenchLayout(new object(), new object(), new object(), new object(), new object());
                    var coordinator = new TerminalWorkbench(layout, preferences, host, static () => { });
                    try
                    {
                        coordinator.OnActiveSessionChanged("s1", "C:\\work");
                        coordinator.OpenTerminal();
                        coordinator.OpenTerminal();

                        preferences.TerminalShell = "bash";
                        preferences.TerminalArguments = "--login";
                        coordinator.OpenTerminal();
                        // The pumps start on background threads; wait until all three asked the host.
                        var started = await TerminalUi.WaitInsideAsync(
                            null, () => host.Starts.Count == 3, TimeSpan.FromSeconds(10));
                        Assert.True(started, "the terminals never asked the host to start");
                        return host.Starts;
                    }
                    finally
                    {
                        await coordinator.DisposeAsync();
                    }
                },
                TimeSpan.FromSeconds(20));

            Assert.Equal(3, observed.Count);
            Assert.Equal("pwsh", observed[0].Shell);
            Assert.Equal(["-l", "-i", "--title", "my shell"], observed[0].Arguments);
            Assert.Equal("C:\\work", observed[0].WorkingDirectory);
            Assert.Equal("bash", observed[2].Shell);
            Assert.Equal(["--login"], observed[2].Arguments);
    }
}
