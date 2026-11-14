using Forge.Abstractions;
using Forge.App.Agents;
using Forge.App.Preferences;
using Forge.App.Providers;
using Forge.App.Settings;
using Forge.Core.Providers;
using Xunit;

namespace Forge.App.Tests.Settings;

/// <summary>
/// The providers page keeps its agent-defaults combos in step with <see cref="AppPreferences.ModelsHidden"/>: hiding a model
/// on the visibility page removes it from the combos that are already built, showing it again restores it, and a page that was
/// disposed stops listening. The rebuild goes through the injected post, like the page other background sources of change.
/// </summary>
public sealed class ProvidersSettingsHiddenModelsTests : IDisposable
{
    private readonly string root = TestTempFiles.NewDirectory("forge-providers-hidden-");
    private readonly Queue<Action> posted = new();

    public void Dispose() => TestTempFiles.DeleteQuietly(root);

    private async Task<ProvidersSettingsViewModel> OpenAsync(AppPreferences preferences, AgentDefaultsStore? defaults = null)
    {
        var services = new ProviderServices(
            Path.Combine(root, "settings.json"), root, new HttpClient { Timeout = Timeout.InfiniteTimeSpan });
        await services.SaveAsync([Definition()], "alpha", ContextWindowDefault.Empty);
        var section = new ProvidersSettingsViewModel(
            services, defaults, post: posted.Enqueue, preferences: preferences);
        section.Reload();
        return section;
    }

    private void Pump()
    {
        while (posted.TryDequeue(out var action))
        {
            action();
        }
    }

    private static string[] Offered(ProvidersSettingsViewModel section) =>
        [.. section.AgentRows[0].Models.Select(static choice => choice.Model)];

    [Fact]
    public async Task Hiding_a_model_removes_it_from_the_built_rows_and_unhiding_restores_it()
    {
        var preferences = new AppPreferences();
        using var section = await OpenAsync(preferences);
        Assert.Equal([string.Empty, "alpha-1", "alpha-2", "alpha-3"], Offered(section));

        preferences.SetModelHidden("alpha-2", hidden: true);

        // The rebuild is posted, not run on the thread that raised the change.
        Assert.Equal([string.Empty, "alpha-1", "alpha-2", "alpha-3"], Offered(section));
        Pump();
        Assert.Equal([string.Empty, "alpha-1", "alpha-3"], Offered(section));
        Assert.All(section.AgentRows, static row => Assert.DoesNotContain("alpha-2", row.Models.Select(static c => c.Model)));

        preferences.SetModelHidden("alpha-2", hidden: false);
        Pump();
        Assert.Equal([string.Empty, "alpha-1", "alpha-2", "alpha-3"], Offered(section));
    }

    [Fact]
    public async Task A_hidden_model_that_is_the_stored_selection_stays_offered()
    {
        var defaults = new AgentDefaultsStore(Path.Combine(root, "agent-defaults.json"));
        var preferences = new AppPreferences();
        using var section = await OpenAsync(preferences, defaults);
        var main = section.AgentRows[0];
        Assert.Equal(AgentIds.Main, main.AgentId);
        defaults.Save(AgentIds.Main, new AgentDefault("alpha", "alpha-2", ThinkingEffort.High));
        section.Reload();

        preferences.SetModelHidden("alpha-2", hidden: true);
        Pump();

        // The agent really runs on it, so the row keeps it as its selection while the other rows lose it.
        var rebuilt = section.AgentRows[0];
        Assert.Equal("alpha-2", rebuilt.SelectedModel.Model);
        Assert.Contains("alpha-2", rebuilt.Models.Select(static choice => choice.Model));
        Assert.DoesNotContain("alpha-2", section.AgentRows[1].Models.Select(static choice => choice.Model));
    }

    [Fact]
    public async Task An_unrelated_preference_change_does_not_rebuild_the_rows()
    {
        var preferences = new AppPreferences();
        using var section = await OpenAsync(preferences);
        var before = section.AgentRows[0];

        preferences.AgentsMaxParallelSubAgents = 9;
        Pump();

        Assert.Same(before, section.AgentRows[0]);
    }

    [Fact]
    public async Task After_dispose_a_change_to_hidden_models_rebuilds_nothing()
    {
        var preferences = new AppPreferences();
        var section = await OpenAsync(preferences);
        var rowsBefore = section.AgentRows.ToArray();
        section.Dispose();

        var failure = Record.Exception(() =>
        {
            preferences.SetModelHidden("alpha-2", hidden: true);
            Pump();
        });

        Assert.Null(failure);
        Assert.Empty(posted);
        Assert.Equal(rowsBefore.Length, section.AgentRows.Count);
        Assert.True(rowsBefore.SequenceEqual(section.AgentRows), "the rows were rebuilt after dispose");
        Assert.Contains("alpha-2", Offered(section));
    }

    [Fact]
    public async Task A_change_posted_before_dispose_but_run_after_it_is_ignored()
    {
        var preferences = new AppPreferences();
        var section = await OpenAsync(preferences);
        var rowsBefore = section.AgentRows.ToArray();

        preferences.SetModelHidden("alpha-2", hidden: true);
        Assert.Single(posted);
        section.Dispose();

        var failure = Record.Exception(Pump);

        Assert.Null(failure);
        Assert.True(rowsBefore.SequenceEqual(section.AgentRows), "a queued rebuild ran against a disposed page");
    }

    private static ProviderDefinition Definition() => new()
    {
        Id = "alpha",
        PresetId = ProviderPresets.CustomId,
        DisplayName = "alpha",
        Protocol = ProviderProtocol.OpenAiCompatible,
        BaseUrl = "https://alpha.invalid/v1",
        CredentialKind = ProviderCredentialKind.ApiKey,
        SecretReference = "provider:alpha",
        Models = ["alpha-1", "alpha-2", "alpha-3"],
        DefaultModel = "alpha-1",
        Overrides = new Dictionary<string, ModelMetadata>(StringComparer.Ordinal),
    };
}
