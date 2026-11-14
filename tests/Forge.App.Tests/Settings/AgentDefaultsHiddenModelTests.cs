using Forge.Abstractions;
using Forge.App.Agents;
using Forge.App.Preferences;
using Forge.App.Providers;
using Forge.App.Settings;
using Forge.Core.Providers;
using Xunit;

namespace Forge.App.Tests.Settings;

/// <summary>
/// The agent-defaults combo against <see cref="AppPreferences.ModelsHidden"/>: a hidden model is not offered, but a stored
/// default that happens to be hidden still shows — the agent really runs on it — through the same "no provider lists it"
/// row a retired model gets, so there is exactly one mechanism.
/// </summary>
public sealed class AgentDefaultsHiddenModelTests : IDisposable
{
    private const string NotSet = "not set";

    private static readonly ThinkingEffort[] EveryStep =
    [
        ThinkingEffort.Off, ThinkingEffort.Minimal, ThinkingEffort.Low, ThinkingEffort.Medium, ThinkingEffort.High,
        ThinkingEffort.XHigh, ThinkingEffort.Max,
    ];

    private readonly string root = TestTempFiles.NewDirectory("forge-agent-defaults-hidden-");
    private readonly ProviderSecretStore secrets;
    private readonly ProviderCredentials credentials;

    public AgentDefaultsHiddenModelTests()
    {
        Directory.CreateDirectory(root);
        secrets = new ProviderSecretStore(root);
        credentials = new ProviderCredentials(
            new HttpClient { Timeout = Timeout.InfiniteTimeSpan },
            secrets,
            Path.Combine(root, "missing-credentials.json"));
    }

    [Fact]
    public void A_hidden_model_is_not_offered()
    {
        var preferences = new AppPreferences();
        preferences.SetModelHidden("alpha-2", hidden: true);

        var row = Row(stored: null, preferences);

        // "not set", then the two models the provider still offers: the hidden one is not a choice.
        Assert.Equal([string.Empty, "alpha-1", "alpha-3"], row.Models.Select(static choice => choice.Model));
        Assert.True(row.SelectedModel.IsInherited);
    }

    [Fact]
    public void A_stored_default_that_is_hidden_is_still_shown_and_kept_selected()
    {
        var preferences = new AppPreferences();
        preferences.SetModelHidden("alpha-2", hidden: true);

        var row = Row(new AgentDefault("alpha", "alpha-2", ThinkingEffort.High), preferences);

        // Not set, the two listed models, then the hidden stored value — exactly how a model no provider lists is handled.
        Assert.Equal([string.Empty, "alpha-1", "alpha-3", "alpha-2"], row.Models.Select(static choice => choice.Model));
        Assert.Equal(row.Models[^1], row.SelectedModel);
        Assert.Equal("alpha", row.SelectedModel.ProviderId);
        Assert.Equal("alpha · alpha-2", row.SelectedModel.Display);

        // The combo shows what the agent runs on, and saving the row keeps the entry it had.
        Assert.NotNull(row.Current);
        Assert.Equal("alpha-2", row.Current!.Model);
        Assert.Equal("alpha", row.Current.ProviderId);
        Assert.Equal(ThinkingEffort.High, row.Current.Thinking);
    }

    [Fact]
    public void Unhiding_the_stored_default_returns_it_to_the_listed_rows()
    {
        var preferences = new AppPreferences();
        preferences.SetModelHidden("alpha-2", hidden: true);
        preferences.SetModelHidden("alpha-2", hidden: false);

        var row = Row(new AgentDefault("alpha", "alpha-2", ThinkingEffort.High), preferences);

        Assert.Equal([string.Empty, "alpha-1", "alpha-2", "alpha-3"], row.Models.Select(static choice => choice.Model));
        Assert.Equal("alpha-2", row.SelectedModel.Model);
        Assert.Equal(4, row.Models.Count);
    }

    [Fact]
    public void Without_preferences_no_model_is_filtered()
    {
        // The parameter is optional and trailing: every existing construction keeps the old behaviour.
        var row = new AgentDefaultsRowViewModel(AgentIds.Main, "Main", stored: null, Catalog(), NotSet, levels: (_, _) => EveryStep);

        Assert.Equal([string.Empty, "alpha-1", "alpha-2", "alpha-3"], row.Models.Select(static choice => choice.Model));
    }

    public void Dispose()
    {
        TestTempFiles.DeleteQuietly(root);
    }

    private AgentDefaultsRowViewModel Row(AgentDefault? stored, AppPreferences preferences) =>
        new(
            AgentIds.Main,
            "Main",
            stored,
            Catalog(),
            NotSet,
            levels: (_, _) => EveryStep,
            preferences: preferences);

    private ProviderCatalog Catalog() => ProviderCatalog.Create([Definition()], "alpha", secrets, credentials);

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
