using Forge.Abstractions;
using Forge.App.Agents;
using Forge.Core.Providers;
using Xunit;

namespace Forge.App.Tests;

/// <summary>
/// The settings-file half of the agent defaults: the model and thinking step an agent id resolves to. The output cap is
/// the part this file pins: <see cref="Forge.Abstractions.ModelSettings.MaxOutputTokens"/> carries only what the user
/// pinned (the per-model <c>modelMeta</c> override, else the provider-level figure) - the catalogue's limit is the
/// model's capability and used to ride into the settings instead, so an aggregator's derived cap reached the wire and
/// every request for the real cap's model was refused.
/// </summary>
public sealed class AgentDefaultsStoreTests : IDisposable
{
    private readonly ProviderCatalogFixture fixture = new();
    private readonly string settingsPath = Path.Combine(Path.GetTempPath(), "forge-agent-defaults-" + Guid.NewGuid().ToString("n") + ".json");

    public void Dispose()
    {
        fixture.Dispose();
        try
        {
            File.Delete(settingsPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A leftover temp file is not worth failing a test over.
        }
    }

    private AgentDefaultsStore Store(string json)
    {
        File.WriteAllText(settingsPath, json);
        return new AgentDefaultsStore(settingsPath);
    }

    [Fact]
    public void An_unpinned_model_resolves_with_the_catalogue_cap_bounded_by_the_compatible_ceiling()
    {
        var store = Store("""{"agents":{"scout":{"providerId":"opencode","model":"glm-5.3","thinking":"low"}}}""");
        var catalog = fixture.Create(
            "opencode",
            ProviderCatalogFixture.Snapshot(("opencode", "glm-5.3", 131_072), ("openrouter", "z-ai/glm-5.3", 943_718)),
            new FixtureProvider("opencode", "OPENCODE", ["glm-5.3"]));

        var resolved = store.Resolve("scout", catalog);

        Assert.Equal(new ModelSettings("glm-5.3", ThinkingEffort.Low, 65_536, "opencode"), resolved);
    }

    [Theory]
    [InlineData(null, null, null)]
    [InlineData(null, 4_096, 8_192)]
    [InlineData(null, 32_000, 32_000)]
    [InlineData(null, 943_718, 65_536)]
    [InlineData(12_345, 943_718, 12_345)]
    public void The_compatible_cap_is_the_pin_else_the_bounded_catalogue_figure(int? pinned, int? catalogue, int? expected)
    {
        // One place decides the wire cap for the protocols that otherwise send nothing: a pin always wins, a catalogue
        // figure is clamped (an aggregator's derived six-figure value must never reach the wire, and a tiny one must not
        // truncate every answer), and nothing known sends nothing.
        Assert.Equal(expected, OutputCeiling.Compatible(pinned, catalogue));
    }

    [Fact]
    public void An_unknown_catalogue_limit_leaves_the_cap_unset()
    {
        Assert.Null(OutputCeiling.Compatible(pinned: null, catalogueLimit: null));
    }

    [Fact]
    public void A_per_model_pin_reaches_the_resolved_settings_and_wins_over_the_provider_level()
    {
        var store = Store("""{"agents":{"scout":{"providerId":"opencode","model":"glm-5.3","thinking":"low"}}}""");
        var catalog = fixture.Create(
            "opencode",
            new FixtureProvider(
                "opencode",
                "OPENCODE",
                ["glm-5.3"],
                MaxOutputTokens: 5_000,
                Overrides: new Dictionary<string, ModelMetadata> { ["glm-5.3"] = new(null, null, 4_096) }));

        var resolved = store.Resolve("scout", catalog);

        Assert.Equal(new ModelSettings("glm-5.3", ThinkingEffort.Low, 4_096, "opencode"), resolved);
    }

    [Fact]
    public void A_provider_level_pin_reaches_the_resolved_settings()
    {
        var store = Store("""{"agents":{"scout":{"providerId":"opencode","model":"glm-5.3","thinking":"low"}}}""");
        var catalog = fixture.Create(
            "opencode",
            new FixtureProvider("opencode", "OPENCODE", ["glm-5.3"], MaxOutputTokens: 5_000));

        var resolved = store.Resolve("scout", catalog);

        Assert.Equal(new ModelSettings("glm-5.3", ThinkingEffort.Low, 5_000, "opencode"), resolved);
    }
}
