using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Styling;
using Forge.Abstractions;
using Forge.App.Preferences;
using Forge.App.Terminal;
using Forge.App.Terminal.Vt;
using Xunit;

namespace Forge.App.Tests.Terminal;

/// <summary>
/// Pins theming and font handling (forge-app spec, "App/TerminalControl", theme and metrics): the dark 16-colour tray
/// carries the documented dark values, a theme switch re-answers OSC 10 with the new foreground, and a code-font size
/// change re-measures the cell and pushes a smaller grid to the session.
/// </summary>
[Collection("forge-app-ui")]
public sealed class TerminalThemeFontTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(40);

    private sealed class Scene
    {
        private Scene(
            Window window,
            TerminalControl control,
            TerminalViewModel viewModel,
            FakeTerminalHost host,
            AppPreferences preferences)
        {
            Window = window;
            Control = control;
            ViewModel = viewModel;
            Host = host;
            Preferences = preferences;
        }

        public Window Window { get; }

        public TerminalControl Control { get; }

        public TerminalViewModel ViewModel { get; }

        public FakeTerminalHost Host { get; }

        public AppPreferences Preferences { get; }

        public static Scene Open()
        {
            var preferences = new AppPreferences();
            var host = new FakeTerminalHost();
            var viewModel = new TerminalViewModel(
                "s1",
                1,
                host,
                new TerminalStartRequest("s1", "C:\\work", new TerminalSize(100, 30)),
                preferences);
            var control = new TerminalControl { Source = viewModel };
            var window = new Window { Width = 1000, Height = 700, Content = control };
            viewModel.Start();
            TerminalUi.Show(window);
            return new Scene(window, control, viewModel, host, preferences);
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
                    var running = await TerminalUi.WaitInsideAsync(
                        scene.Window, () => scene.ViewModel.Status == TerminalStatus.Running, TimeSpan.FromSeconds(10));
                    Assert.True(running, "the terminal never reached Running");
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
    public void Dark_palette_carries_the_documented_16_colours()
    {
        var dark = TerminalPalette.For(dark: true);
        var light = TerminalPalette.For(dark: false);

        string[] expectedDark =
        [
            "#3F4541", "#E06C75", "#98C379", "#E5C07B",
            "#61AFEF", "#C678DD", "#56B6C2", "#DDE3DF",
            "#5C625E", "#FF7B86", "#B0D98C", "#F0D098",
            "#7CC0FF", "#D79AE8", "#7FD4DE", "#F5F7F5",
        ];
        string[] expectedLight =
        [
            "#2B2F2C", "#B3261E", "#2F7449", "#8A5A00",
            "#1C5FA8", "#7B3FA0", "#146C7E", "#6E7671",
            "#5C625E", "#C7372E", "#3A8F5C", "#A06A00",
            "#2A74C2", "#9350B8", "#1B8399", "#3F4541",
        ];

        for (var i = 0; i < 16; i++)
        {
            Assert.Equal(Color.Parse(expectedDark[i]), dark.FromIndex(i));
            Assert.Equal(Color.Parse(expectedLight[i]), light.FromIndex(i));
        }

        // The trays agree on slot 8 by design (both are the same grey); the lead slots differ per theme.
        Assert.NotEqual(light.FromIndex(0), dark.FromIndex(0));
        Assert.NotEqual(light.FromIndex(1), dark.FromIndex(1));

        Assert.Equal(Color.Parse("#2B2F2C"), light.FallbackForeground);
        Assert.Equal(Color.Parse("#DDE3DF"), dark.FallbackForeground);
    }

    [Fact]
    public async Task Theme_switch_re_answers_osc10_with_the_new_foreground()
    {
        var observed = await RunAsync(async scene =>
        {
            var lightAnswer = await QueryForegroundAsync(scene);
            var lightForeground = ThemeForeground(scene.Control, dark: false);

            scene.Window.RequestedThemeVariant = ThemeVariant.Dark;
            var darkApplied = await TerminalUi.WaitInsideAsync(
                scene.Window, () => scene.Control.ActualThemeVariant == ThemeVariant.Dark, TimeSpan.FromSeconds(10));

            var darkAnswer = await QueryForegroundAsync(scene);
            var darkForeground = ThemeForeground(scene.Control, dark: true);
            return (lightAnswer, darkAnswer, lightForeground, darkForeground, darkApplied);
        });

        Assert.True(observed.darkApplied, "the dark theme never applied");
        Assert.NotEqual(observed.lightAnswer, observed.darkAnswer);
        Assert.Contains(ToOscReply(observed.lightForeground), observed.lightAnswer);
        Assert.Contains(ToOscReply(observed.darkForeground), observed.darkAnswer);
        Assert.NotEqual(observed.lightForeground, observed.darkForeground);
    }

    [Fact]
    public async Task Code_font_size_change_re_measures_the_cell_and_resizes_the_grid()
    {
        var observed = await RunAsync(async scene =>
        {
            var initial = await TerminalUi.WaitInsideAsync(
                scene.Window, () => scene.Host.Session.Resizes.Count > 0, TimeSpan.FromSeconds(10));
            Assert.True(initial, "the initial grid never reached the session");

            var beforeWidth = MeasureWidth(scene.Control);
            var beforeColumns = scene.ViewModel.Emulator.Columns;
            var beforeResizes = scene.Host.Session.Resizes.Count;

            scene.Preferences.CodeFontSize = 20;
            var remeasured = await TerminalUi.WaitInsideAsync(
                scene.Window, () => MeasureWidth(scene.Control) > beforeWidth, TimeSpan.FromSeconds(10));
            var resized = await TerminalUi.WaitInsideAsync(
                scene.Window, () => scene.Host.Session.Resizes.Count > beforeResizes, TimeSpan.FromSeconds(10));
            return (
                remeasured,
                resized,
                beforeWidth,
                beforeColumns,
                Reported: scene.Host.Session.Resizes.Count > beforeResizes ? scene.Host.Session.Resizes[^1] : default,
                Columns: scene.ViewModel.Emulator.Columns,
                Rows: scene.ViewModel.Emulator.Rows);
        });

        Assert.True(observed.remeasured, "the cell was never re-measured after the font change");
        Assert.True(observed.resized, "the new grid never reached the session");
        Assert.Equal(observed.Columns, observed.Reported.Columns);
        Assert.Equal(observed.Rows, observed.Reported.Rows);
        Assert.True(observed.Columns < observed.beforeColumns, "a larger font must fit fewer columns");
    }

    private static async Task<string> QueryForegroundAsync(Scene scene)
    {
        scene.Host.Session.DropWritten();
        scene.Host.Session.Emit("\x1b]10;?\x07");
        var answered = await TerminalUi.WaitInsideAsync(
            scene.Window, () => scene.Host.Session.WrittenCount() > 0, TimeSpan.FromSeconds(10));
        Assert.True(answered, "the OSC 10 answer never arrived");
        return System.Text.Encoding.Latin1.GetString(scene.Host.Session.WrittenSnapshot());
    }

    private static double MeasureWidth(TerminalControl control)
    {
        control.Measure(Size.Infinity);
        return control.DesiredSize.Width;
    }

    /// <summary>The foreground the control hands to the emulator: the theme token when present, else the tray fallback.</summary>
    private static Color ThemeForeground(TerminalControl control, bool dark)
    {
        var variant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
        if (control.TryFindResource("TextPrimaryBrush", variant, out var value) &&
            value is ISolidColorBrush brush)
        {
            return brush.Color;
        }

        return TerminalPalette.For(dark).FallbackForeground;
    }

    private static string ToOscReply(Color color) =>
        $"rgb:{color.R:x2}{color.R:x2}/{color.G:x2}{color.G:x2}/{color.B:x2}{color.B:x2}";
}
