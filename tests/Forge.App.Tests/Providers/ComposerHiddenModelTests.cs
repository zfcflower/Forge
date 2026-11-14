using Forge.Abstractions;
using Forge.App.Preferences;
using Forge.App.Providers;
using Forge.App.Services;
using Forge.App.ViewModels;
using Forge.Core.Providers;
using Xunit;

namespace Forge.App.Tests.Providers;

/// <summary>
/// The composer's model picker against <see cref="AppPreferences.ModelsHidden"/>: a hidden model is not offered, the model
/// the session runs on stays (it is the state, not a choice), and a hidden flag changed on the settings page re-filters the
/// open composer without a restart — and without rebuilding a single row.
/// </summary>
public sealed class ComposerHiddenModelTests : IDisposable
{
    private const string ThreeModels = """
        "items": [
          { "id": "solo", "preset": "custom-openai", "name": "Solo", "protocol": "openai-compatible",
            "baseUrl": "https://solo.invalid/v1", "auth": { "kind": "api-key" },
            "models": ["solo-1", "solo-2", "solo-3"], "defaultModel": "solo-1" }
        ]
        """;

    private readonly string root = TestTempFiles.NewDirectory("forge-composer-hidden-");

    [Fact]
    public void A_hidden_model_is_not_offered_while_the_current_model_stays()
    {
        var preferences = new AppPreferences();
        preferences.SetModelHidden("solo-2", hidden: true);

        var composer = CreateComposer(ThreeModels, preferences);

        Assert.Equal(["solo-1", "solo-3"], composer.Models.Select(static model => model.Name));

        // Hiding the model the session is on does not take the state out of the picker: the chip still names it and the
        // next turn still runs on it.
        preferences.SetModelHidden("solo-1", hidden: true);

        Assert.Equal(["solo-1", "solo-3"], composer.Models.Select(static model => model.Name));
        Assert.Equal("solo-1", composer.Model);
        Assert.True(composer.Models[0].IsSelected);
        Assert.True(preferences.IsModelHidden("solo-1"));
    }

    [Fact]
    public void Unhiding_brings_a_model_back_without_rebuilding_the_composer()
    {
        var preferences = new AppPreferences();
        preferences.SetModelHidden("solo-2", hidden: true);
        var composer = CreateComposer(ThreeModels, preferences);
        Assert.DoesNotContain("solo-2", composer.Models.Select(static model => model.Name));

        // The very rows the picker already shows are the ones that come back: no new composer, no rebuilt row objects.
        var first = composer.Models[0];
        preferences.SetModelHidden("solo-2", hidden: false);

        Assert.Equal(["solo-1", "solo-2", "solo-3"], composer.Models.Select(static model => model.Name));
        Assert.Same(first, composer.Models[0]);
        Assert.False(preferences.IsModelHidden("solo-2"));
    }

    [Fact]
    public void Leaving_a_hidden_current_model_drops_it_from_the_list()
    {
        var preferences = new AppPreferences();
        preferences.SetModelHidden("solo-1", hidden: true);
        var composer = CreateComposer(ThreeModels, preferences);

        // "solo-1" is the state: its row stays only while the session runs on it.
        Assert.Equal(["solo-1", "solo-2", "solo-3"], composer.Models.Select(static model => model.Name));

        composer.Models.Single(static model => model.Name == "solo-3").SelectCommand.Execute(null);

        // The model just left is hidden and no longer the state, so it becomes an ordinary hidden choice and drops out.
        Assert.Equal("solo-3", composer.Model);
        Assert.Equal(["solo-2", "solo-3"], composer.Models.Select(static model => model.Name));
        Assert.True(preferences.IsModelHidden("solo-1"));
    }

    [Fact]
    public void A_hidden_model_is_not_reachable_through_the_search_box()
    {
        var preferences = new AppPreferences();
        preferences.SetModelHidden("solo-2", hidden: true);
        var composer = CreateComposer(ThreeModels, preferences);

        composer.ModelSearch = "solo-2";

        Assert.Empty(composer.Models);

        // The current model is exempt from hiding, so it is still reachable by its own name.
        composer.ModelSearch = "solo-1";
        Assert.Equal(["solo-1"], composer.Models.Select(static model => model.Name));
    }

    [Fact]
    public void Search_and_pin_ordering_still_hold_for_the_visible_set()
    {
        var preferences = new AppPreferences();
        preferences.SetModelHidden("solo-2", hidden: true);
        var composer = CreateComposer(ThreeModels, preferences);

        // Pins keep the rule they had: pinned rows first, each group in catalogue order, nothing hidden in either group.
        composer.Models.Single(static model => model.Name == "solo-3").TogglePinCommand.Execute(null);
        Assert.Equal(["solo-3", "solo-1"], composer.Models.Select(static model => model.Name));

        composer.ModelSearch = "solo";
        Assert.Equal(["solo-3", "solo-1"], composer.Models.Select(static model => model.Name));

        composer.ModelSearch = string.Empty;
        preferences.SetModelHidden("solo-2", hidden: false);
        Assert.Equal(["solo-3", "solo-1", "solo-2"], composer.Models.Select(static model => model.Name));

        // The pin still wins over a narrowed list: it is the first group of whatever the search matches.
        composer.ModelSearch = "solo";
        Assert.Equal(["solo-3", "solo-1", "solo-2"], composer.Models.Select(static model => model.Name));
    }

    [Fact]
    public void A_hidden_model_that_is_pinned_stays_and_a_hidden_unpinned_one_does_not()
    {
        var preferences = new AppPreferences();
        preferences.SetModelHidden("solo-2", hidden: true);
        preferences.SetModelHidden("solo-3", hidden: true);
        var pins = new ComposerPreferences();
        pins.SetPinned("solo-2", true);

        var composer = CreateComposer(ThreeModels, preferences, pins);

        // solo-2 is hidden but pinned: the pin is the user asking to keep it in reach. solo-3 is hidden, unpinned and not
        // the current model (solo-1), so it is filtered. Pinned rows still come first.
        Assert.Equal(["solo-2", "solo-1"], composer.Models.Select(static model => model.Name));
        Assert.True(preferences.IsModelHidden("solo-2"));
        Assert.DoesNotContain("solo-3", composer.Models.Select(static model => model.Name));
    }

    [Fact]
    public void Unpinning_a_hidden_model_removes_it_from_the_list()
    {
        var preferences = new AppPreferences();
        preferences.SetModelHidden("solo-2", hidden: true);
        var pins = new ComposerPreferences();
        pins.SetPinned("solo-2", true);
        var composer = CreateComposer(ThreeModels, preferences, pins);
        Assert.Equal(["solo-2", "solo-1", "solo-3"], composer.Models.Select(static model => model.Name));

        composer.Models.Single(static model => model.Name == "solo-2").TogglePinCommand.Execute(null);

        // Unpinned, it is an ordinary hidden choice again and drops out; the hidden flag itself never changed.
        Assert.Equal(["solo-1", "solo-3"], composer.Models.Select(static model => model.Name));
        Assert.False(pins.IsPinned("solo-2"));
        Assert.True(preferences.IsModelHidden("solo-2"));
    }

    [Fact]
    public void Hiding_a_pinned_model_after_the_fact_keeps_it_and_unhiding_changes_nothing()
    {
        var preferences = new AppPreferences();
        var pins = new ComposerPreferences();
        pins.SetPinned("solo-3", true);
        var composer = CreateComposer(ThreeModels, preferences, pins);

        preferences.SetModelHidden("solo-3", hidden: true);
        Assert.Equal(["solo-3", "solo-1", "solo-2"], composer.Models.Select(static model => model.Name));

        preferences.SetModelHidden("solo-3", hidden: false);
        Assert.Equal(["solo-3", "solo-1", "solo-2"], composer.Models.Select(static model => model.Name));
    }

    [Fact]
    public void A_hidden_pinned_model_is_still_found_by_search()
    {
        var preferences = new AppPreferences();
        preferences.SetModelHidden("solo-2", hidden: true);
        preferences.SetModelHidden("solo-3", hidden: true);
        var pins = new ComposerPreferences();
        pins.SetPinned("solo-2", true);
        var composer = CreateComposer(ThreeModels, preferences, pins);

        composer.ModelSearch = "solo-2";
        Assert.Equal(["solo-2"], composer.Models.Select(static model => model.Name));

        // The unpinned hidden one stays unreachable, and a search nothing matches is still empty.
        composer.ModelSearch = "solo-3";
        Assert.Empty(composer.Models);
        Assert.True(composer.HasNoModels);
    }

    [Fact]
    public void A_pinned_model_no_provider_lists_still_shows_as_a_stub_row_even_when_hidden()
    {
        var preferences = new AppPreferences();
        preferences.SetModelHidden("ghost-model", hidden: true);
        var pins = new ComposerPreferences();
        pins.SetPinned("ghost-model", true);
        pins.SetPinned("other-ghost", true);

        var composer = CreateComposer(ThreeModels, preferences, pins);

        Assert.Equal(["ghost-model", "other-ghost", "solo-1", "solo-2", "solo-3"], composer.Models.Select(static model => model.Name));
        Assert.All(composer.Models.Where(static model => model.Name.Contains("ghost", StringComparison.Ordinal)), static model => Assert.True(model.IsPinned));

        // Unpinning the hidden stub removes it (nothing else keeps it), the unhidden one stays until unpinned too.
        composer.Models.Single(static model => model.Name == "ghost-model").TogglePinCommand.Execute(null);
        Assert.Equal(["other-ghost", "solo-1", "solo-2", "solo-3"], composer.Models.Select(static model => model.Name));
    }

    [Fact]
    public void The_composer_stops_listening_once_it_is_disposed()
    {
        var preferences = new AppPreferences();
        var composer = CreateComposer(ThreeModels, preferences);
        var raised = 0;
        composer.Models.CollectionChanged += (_, _) => raised++;

        composer.Dispose();
        preferences.SetModelHidden("solo-1", hidden: true);

        // The subscription is gone with the tab: the closed composer neither re-filters nor holds the preferences alive.
        Assert.Equal(0, raised);
        Assert.Equal(["solo-1", "solo-2", "solo-3"], composer.Models.Select(static model => model.Name));
    }

    [Fact]
    public void Without_preferences_every_model_is_listed()
    {
        // The optional parameter every existing call site and test uses: a composer built without preferences filters none.
        var composer = new ComposerViewModel(
            Services(ThreeModels),
            _ => { },
            new ComposerPreferences(),
            string.Empty,
            skillSource: static () => []);

        Assert.Equal(["solo-1", "solo-2", "solo-3"], composer.Models.Select(static model => model.Name));
    }

    public void Dispose()
    {
        TestTempFiles.DeleteQuietly(root);
    }

    private ComposerViewModel CreateComposer(string items, AppPreferences preferences, ComposerPreferences? pins = null) =>
        new(
            Services(items),
            _ => { },
            pins ?? new ComposerPreferences(),
            string.Empty,
            skillSource: static () => [],
            thinkingLevels: static (_, _) => [ThinkingEffort.Off, ThinkingEffort.High],
            appPreferences: preferences);

    private ProviderServices Services(string items)
    {
        var settingsPath = Path.Combine(root, "settings.json");
        File.WriteAllText(
            settingsPath,
            """{ "providers": { "version": 1, "defaultId": "solo", {{ITEMS}} } }"""
                .Replace("{{ITEMS}}", items, StringComparison.Ordinal));
        var data = Path.Combine(root, "data");
        Directory.CreateDirectory(data);
        return new ProviderServices(settingsPath, data, new HttpClient { Timeout = Timeout.InfiniteTimeSpan });
    }
}
