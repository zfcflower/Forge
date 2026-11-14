using Forge.App.Localization;
using Forge.App.Preferences;
using Forge.App.Providers;
using Forge.App.Settings;
using Forge.App.Tests.Localization;
using Xunit;

namespace Forge.App.Tests.Settings;

/// <summary>
/// The <c>models-visibility</c> settings section: one switch per model of every connected provider, the search that only
/// filters this page, the hidden count and the empty state. The switches write <see cref="AppPreferences.ModelsHidden"/>;
/// the section keeps no state of its own and reads the catalogue only when <c>Reload</c> is called.
/// </summary>
public sealed class ModelsVisibilitySectionTests : IDisposable
{
    private readonly string root = TestTempFiles.NewDirectory("forge-models-visibility-");

    [Fact]
    public void One_row_per_model_of_every_connected_provider()
    {
        var preferences = new AppPreferences();
        var services = Services(TwoProviders);
        using var page = new ModelsVisibilitySectionViewModel(services, preferences);

        page.Reload();

        Assert.True(page.HasProviders);
        Assert.False(page.ShowsEmpty);
        Assert.Equal(
            ["alpha-1", "alpha-2", "shared", "beta-1", "shared"],
            page.Rows.Select(static row => row.Name));
        Assert.Equal(
            ["Alpha", "Alpha", "Alpha", "Beta", "Beta"],
            page.Rows.Select(static row => row.ProviderName));
        Assert.Equal(0, page.HiddenCount);
        Assert.All(page.Rows, static row => Assert.True(row.IsVisible));
    }

    [Fact]
    public void A_stored_hidden_model_starts_unchecked()
    {
        var preferences = new AppPreferences();
        preferences.SetModelHidden("shared", hidden: true);
        var services = Services(TwoProviders);
        using var page = new ModelsVisibilitySectionViewModel(services, preferences);

        page.Reload();

        // Hiding is by model name, so both providers' rows for "shared" start unchecked; the count says one hidden model.
        Assert.Equal(1, page.HiddenCount);
        Assert.Equal(2, page.Rows.Count(static row => !row.IsVisible));
        Assert.All(page.Rows.Where(static row => row.Name == "shared"), static row => Assert.False(row.IsVisible));
        Assert.All(page.Rows.Where(static row => row.Name != "shared"), static row => Assert.True(row.IsVisible));
    }

    [Fact]
    public void Toggling_a_row_writes_the_preference_and_raises_it()
    {
        var preferences = new AppPreferences();
        var raised = PropertyChangeLog.For(preferences);
        var services = Services(TwoProviders);
        using var page = new ModelsVisibilitySectionViewModel(services, preferences);
        page.Reload();

        page.Rows[0].IsVisible = false;

        Assert.Equal(["alpha-1"], preferences.ModelsHidden);
        Assert.True(preferences.IsModelHidden("alpha-1"));
        Assert.Contains(nameof(AppPreferences.ModelsHidden), raised);
        Assert.Equal(1, page.HiddenCount);

        // The same model on another provider follows the one flag without a reload (two rows, one name).
        var twin = page.Rows.First(static row => row.Name == "shared");
        var other = page.Rows.Last(static row => row.Name == "shared");
        twin.IsVisible = false;
        Assert.False(other.IsVisible);
        Assert.Equal(2, page.HiddenCount);

        // Unchecking writes the other direction.
        other.IsVisible = true;
        Assert.False(preferences.IsModelHidden("shared"));
        Assert.True(twin.IsVisible);
        Assert.Equal(["alpha-1"], preferences.ModelsHidden);
    }

    [Fact]
    public void The_hidden_list_is_trimmed_deduplicated_and_sorted()
    {
        var preferences = new AppPreferences { ModelsHidden = [" beta-1 ", "alpha-1", "", "alpha-1"] };

        Assert.Equal(["alpha-1", "beta-1"], preferences.ModelsHidden);
        Assert.True(preferences.IsModelHidden("alpha-1"));
        Assert.False(preferences.IsModelHidden("beta-1 "));

        // The one write path guards a blank name: it would otherwise put an unnamed row in the hidden set.
        Assert.Throws<ArgumentException>(() => preferences.SetModelHidden("  ", hidden: true));
    }

    [Fact]
    public void The_search_reuses_the_rows_and_never_re_reads_the_catalogue()
    {
        var preferences = new AppPreferences();
        var services = Services(TwoProviders);
        using var page = new ModelsVisibilitySectionViewModel(services, preferences);
        page.Reload();
        var first = page.Rows[0];

        // A catalogue change the section was not told about: a keystroke must filter the rows already built, not re-read.
        File.WriteAllText(Path.Combine(root, "settings.json"), Settings(OneShorterProvider));
        services.Reload();
        page.SearchText = "alpha";
        Assert.Equal(["alpha-1", "alpha-2", "shared"], page.Rows.Select(static row => row.Name));
        page.SearchText = string.Empty;
        Assert.Same(first, page.Rows[0]);

        // The page's own reload is what picks the catalogue change up.
        page.Reload();
        Assert.Equal(["alpha-1", "shared"], page.Rows.Select(static row => row.Name));
    }

    [Fact]
    public void The_search_narrows_the_rows_and_clearing_it_restores_them()
    {
        var preferences = new AppPreferences();
        var services = Services(TwoProviders);
        using var page = new ModelsVisibilitySectionViewModel(services, preferences);
        page.Reload();
        var all = page.Rows.Count;

        page.SearchText = "beta";

        // The provider name is searched too, so both of Beta's rows match.
        Assert.Equal(["beta-1", "shared"], page.Rows.Select(static row => row.Name));
        Assert.Equal(2, page.Rows.Count);

        page.SearchText = "alpha-2";
        Assert.Equal(["alpha-2"], page.Rows.Select(static row => row.Name));

        page.SearchText = string.Empty;
        Assert.Equal(all, page.Rows.Count);

        // The search is a page filter: it never touches the hidden set.
        Assert.Empty(preferences.ModelsHidden);
    }

    [Fact]
    public void The_hidden_count_follows_the_preference_in_both_languages()
    {
        var preferences = new AppPreferences();
        var services = Services(TwoProviders);
        using var page = new ModelsVisibilitySectionViewModel(services, preferences);
        page.Reload();

        using (new LanguageScope(UiLanguage.English))
        {
            Assert.Equal("0 models hidden", page.HiddenCountText);

            preferences.SetModelHidden("alpha-1", hidden: true);
            Assert.Equal(1, page.HiddenCount);
            Assert.Equal("1 model hidden", page.HiddenCountText);

            preferences.SetModelHidden("beta-1", hidden: true);
            Assert.Equal(2, page.HiddenCount);
            Assert.Equal("2 models hidden", page.HiddenCountText);
        }

        using (new LanguageScope(UiLanguage.Chinese))
        {
            Assert.Contains("2", page.HiddenCountText, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Show_all_clears_every_hidden_model_including_ids_no_provider_lists()
    {
        var preferences = new AppPreferences();
        preferences.SetModelHidden("alpha-1", hidden: true);
        preferences.SetModelHidden("shared", hidden: true);
        preferences.SetModelHidden("gone-from-catalogue", hidden: true);
        var services = Services(TwoProviders);
        using var page = new ModelsVisibilitySectionViewModel(services, preferences);
        page.Reload();
        Assert.Equal(2, page.HiddenCount);
        Assert.True(page.ShowAllCommand.CanExecute(null));

        page.ShowAllCommand.Execute(null);

        // The stale id has no row to switch back on; Show all is the one way it leaves the set.
        Assert.Empty(preferences.ModelsHidden);
        Assert.False(preferences.IsModelHidden("gone-from-catalogue"));
        Assert.Equal(0, page.HiddenCount);
        Assert.Equal("0 models hidden", WithEnglish(() => page.HiddenCountText));
        Assert.All(page.Rows, static row => Assert.True(row.IsVisible));
        Assert.False(page.ShowAllCommand.CanExecute(null));

        // Executing a disabled command is a no-op rather than a write.
        var raised = 0;
        preferences.PropertyChanged += (_, _) => raised++;
        page.ShowAllCommand.Execute(null);
        Assert.Equal(0, raised);
    }

    [Fact]
    public void Show_all_is_disabled_while_nothing_is_hidden_and_follows_the_hidden_set()
    {
        var preferences = new AppPreferences();
        var services = Services(TwoProviders);
        using var page = new ModelsVisibilitySectionViewModel(services, preferences);
        page.Reload();
        var changed = 0;
        page.ShowAllCommand.CanExecuteChanged += (_, _) => changed++;

        Assert.False(page.ShowAllCommand.CanExecute(null));

        // A change made on this page, then one made elsewhere (the composer's pin dialog, another settings page): both
        // re-evaluate the button.
        page.Rows[0].IsVisible = false;
        Assert.Equal(1, changed);
        Assert.True(page.ShowAllCommand.CanExecute(null));

        preferences.SetModelHidden("stale-id", hidden: true);
        Assert.Equal(2, changed);
        Assert.True(page.ShowAllCommand.CanExecute(null));

        preferences.ModelsHidden = [];
        Assert.Equal(3, changed);
        Assert.False(page.ShowAllCommand.CanExecute(null));
    }

    [Fact]
    public void Show_all_is_enabled_for_a_stale_id_alone_although_the_page_counts_zero()
    {
        var preferences = new AppPreferences();
        preferences.SetModelHidden("gone-from-catalogue", hidden: true);
        var services = Services(TwoProviders);
        using var page = new ModelsVisibilitySectionViewModel(services, preferences);
        page.Reload();

        Assert.Equal(0, page.HiddenCount);
        Assert.True(page.ShowAllCommand.CanExecute(null));

        page.ShowAllCommand.Execute(null);

        Assert.Empty(preferences.ModelsHidden);
        Assert.False(page.ShowAllCommand.CanExecute(null));
    }

    [Fact]
    public void The_show_all_label_is_translated_in_both_languages()
    {
        var preferences = new AppPreferences();
        var services = Services(TwoProviders);
        using var page = new ModelsVisibilitySectionViewModel(services, preferences);

        using (new LanguageScope(UiLanguage.English))
        {
            Assert.Equal("Show all", page.ShowAllText);
        }

        using (new LanguageScope(UiLanguage.Chinese))
        {
            Assert.NotEqual("Show all", page.ShowAllText);
            Assert.False(string.IsNullOrWhiteSpace(page.ShowAllText));
        }

        Assert.NotEqual("[" + ModelsVisibilityStrings.ShowAllKey + "]", page.ShowAllText);
    }

    [Fact]
    public void Reload_is_idempotent_and_picks_up_a_catalogue_change()
    {
        var preferences = new AppPreferences();
        var services = Services(TwoProviders);
        using var page = new ModelsVisibilitySectionViewModel(services, preferences);

        page.Reload();
        var names = page.Rows.Select(static row => row.Name).ToList();
        page.Reload();

        // Selecting the section again re-reads the catalogue; the same catalogue yields the same rows, not twice as many.
        Assert.Equal(names, page.Rows.Select(static row => row.Name));
        Assert.Equal(5, page.Rows.Count);

        // A provider edit lands in settings.json, the store reloads, the section reloads: the new model is listed.
        File.WriteAllText(Path.Combine(root, "settings.json"), Settings(OneShorterProvider));
        services.Reload();
        page.Reload();

        Assert.Equal(["alpha-1", "shared"], page.Rows.Select(static row => row.Name));
    }

    [Fact]
    public void No_provider_configured_shows_the_empty_state_and_no_rows()
    {
        var preferences = new AppPreferences();
        var services = Services("""{ "version": 1, "defaultId": null, "items": [] }""");
        using var page = new ModelsVisibilitySectionViewModel(services, preferences);

        page.Reload();

        Assert.False(page.HasProviders);
        Assert.True(page.ShowsEmpty);
        Assert.Empty(page.Rows);
        Assert.Equal(ModelsVisibilityStrings.Empty, page.EmptyText);
        Assert.NotEqual("[" + ModelsVisibilityStrings.EmptyKey + "]", page.EmptyText);
    }

    public void Dispose()
    {
        TestTempFiles.DeleteQuietly(root);
    }

    private static string WithEnglish(Func<string> read)
    {
        using var scope = new LanguageScope(UiLanguage.English);
        return read();
    }

    private const string TwoProviders = """
        "items": [
          { "id": "alpha", "preset": "custom-openai", "name": "Alpha", "protocol": "openai-compatible",
            "baseUrl": "https://alpha.invalid/v1", "auth": { "kind": "api-key" },
            "models": ["alpha-1", "alpha-2", "shared"], "defaultModel": "alpha-1" },
          { "id": "beta", "preset": "custom-openai", "name": "Beta", "protocol": "openai-compatible",
            "baseUrl": "https://beta.invalid/v1", "auth": { "kind": "api-key" },
            "models": ["beta-1", "shared"], "defaultModel": "beta-1" }
        ]
        """;

    private const string OneShorterProvider = """
        "items": [
          { "id": "alpha", "preset": "custom-openai", "name": "Alpha", "protocol": "openai-compatible",
            "baseUrl": "https://alpha.invalid/v1", "auth": { "kind": "api-key" },
            "models": ["alpha-1", "shared"], "defaultModel": "alpha-1" }
        ]
        """;

    private static string Settings(string items) =>
        """{ "providers": { "version": 1, "defaultId": "alpha", {{ITEMS}} } }"""
            .Replace("{{ITEMS}}", items, StringComparison.Ordinal);

    private ProviderServices Services(string items)
    {
        var settingsPath = Path.Combine(root, "settings.json");
        File.WriteAllText(settingsPath, Settings(items));
        var data = Path.Combine(root, "data");
        Directory.CreateDirectory(data);
        return new ProviderServices(settingsPath, data, new HttpClient { Timeout = Timeout.InfiniteTimeSpan });
    }
}
