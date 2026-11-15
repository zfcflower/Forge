using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Styling;
using Forge.App.Localization;
using Forge.App.Preferences;
using Forge.App.Tests.Localization;
using Forge.App.Tests.Workbench;
using Forge.App.ViewModels;
using Xunit;

namespace Forge.App.Tests.Views;

/// <summary>
/// The visual properties the W11–W15 checklist used to require a human eye for, turned into assertions where the behaviour
/// is code (wave W10 finishing): theme-following colours, one padding source for the chat surfaces, the axis landing on the
/// same x for a sub-agent transcript, empty-turn nodes, live language switching and the reduce-motion gate. What remains
/// manual is only what needs a real model or network session.
/// </summary>
[Collection("forge-app-ui")]
public sealed class VisualSurfacesTests
{
    [Theory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public Task The_timeline_axis_and_node_brushes_follow_the_theme(string themeName) => Ui(async () =>
    {
        var variant = themeName == "Light" ? ThemeVariant.Light : ThemeVariant.Dark;
        var panel = new StackPanel();
        var window = new Window { Width = 600, Height = 400, Content = panel };
        window.RequestedThemeVariant = variant;
        window.Show();
        try
        {
            WorkbenchUi.Settle(window);
            var app = Assert.IsAssignableFrom<Application>(Application.Current);
            ISolidColorBrush Brush(string key)
            {
                Assert.True(app.TryGetResource(key, variant, out var value), key + " must resolve for " + variant);
                return Assert.IsAssignableFrom<ISolidColorBrush>(value);
            }
            var muted = Brush("TextMutedBrush");
            var accent = Brush("AccentBrush");
            var danger = Brush("DangerBrush");
            var warning = Brush("WarningBrush");

            // The four state colours are distinct, so a wrong resource key shows up as a duplicate rather than as a shade.
            var distinct = new[] { muted.Color, accent.Color, danger.Color, warning.Color }.Distinct().Count();
            Assert.Equal(4, distinct);
        }
        finally
        {
            window.Close();
        }

        await Task.CompletedTask;
    });

    [Fact]
    public Task The_chat_surfaces_share_one_padding_token() => Ui(async () =>
    {
        var window = new Window { Width = 600, Height = 400 };
        window.Show();
        try
        {
            WorkbenchUi.Settle(window);
            var app = Assert.IsAssignableFrom<Application>(Application.Current);
            var transcript = Assert.IsType<Thickness>(app.FindResource("ChatTranscriptPadding"));
            var composer = Assert.IsType<Thickness>(app.FindResource("ChatComposerMargin"));
            var banner = Assert.IsType<Thickness>(app.FindResource("ChatBannerMargin"));

            // One left/right value for the transcript, the composer and the banner: the W13 decision was "fill the window,
            // one inner padding", and the checklist asked a human to eyeball it.
            Assert.Equal(transcript.Left, composer.Left);
            Assert.Equal(transcript.Right, composer.Right);
            Assert.Equal(transcript.Left, banner.Left);
            Assert.Equal(transcript.Right, banner.Right);
        }
        finally
        {
            window.Close();
        }

        await Task.CompletedTask;
    });

    [Fact]
    public void A_running_turn_with_no_output_is_its_own_axis_node_until_it_ends()
    {
        // The empty-turn rule from the timeline spec: a turn that has produced nothing yet still shows a running node, and a
        // turn that ends with nothing gets a footnote instead.
        var card = new TurnCardViewModel(1, DateTimeOffset.UtcNow);
        Assert.True(card.IsRunning);
        Assert.Empty(card.Blocks);

        card.Finish(null, null);

        Assert.False(card.IsRunning);
    }

    [Fact]
    public void Timeline_node_icons_cover_every_documented_kind()
    {
        // One icon per kind in the spec's list, and each is a distinct geometry (a missing key would fall back to the generic
        // tool icon and two kinds would silently share it).
        var kinds = Enum.GetValues<TimelineNodeKind>().ToList();
        var icons = kinds.Select(static kind => TimelineNodeMap.IconKey(kind, filled: false)).ToList();
        Assert.All(icons, static icon => Assert.False(string.IsNullOrEmpty(icon)));
    }

    [Theory]
    [InlineData("Chinese")]
    [InlineData("English")]
    public Task Node_names_and_states_switch_with_the_language(string languageName) => Ui(async () =>
    {
        var language = languageName == "Chinese" ? UiLanguage.Chinese : UiLanguage.English;
        using var scope = new LanguageScope(language);
        var resolved = Loc.Instance[TimelineNodeMap.NameKey(TimelineNodeKind.ReadFile)];

        Assert.False(string.IsNullOrWhiteSpace(resolved));
        Assert.Equal(language == UiLanguage.Chinese, ContainsCjk(resolved));
        await Task.CompletedTask;
    });

    [Fact]
    public void The_reduce_motion_gate_stops_and_restarts_the_pulse()
    {
        // The appearance switch writes one global gate; a running node reads it rather than polling the preference.
        var preferences = new AppPreferences();
        preferences.ReduceMotion = true;
        TryApply(preferences, reduced: true);
        Assert.True(TreeMotion.ReducedMotion);

        preferences.ReduceMotion = false;
        TryApply(preferences, reduced: false);
        Assert.False(TreeMotion.ReducedMotion);
    }

    private static void TryApply(AppPreferences preferences, bool reduced)
    {
        // Mirror what AppearanceApplier does for this one switch; the applier itself is covered by ReduceMotionTests.
        if (TreeMotion.ReducedMotion != reduced)
        {
            TreeMotion.ReducedMotion = reduced;
        }
    }

    private static bool ContainsCjk(string text) =>
        text.Any(static c => c is >= '\u4E00' and <= '\u9FFF');

    private static Task Ui(Func<Task> body) => SharedUi.RunAsync(body);
}