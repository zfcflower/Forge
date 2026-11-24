using Forge.Abstractions;
using Forge.App.Providers;
using Forge.Core.Agents;
using Xunit;

namespace Forge.App.Tests;

/// <summary>
/// The app side of <c>spawn_agent</c>'s override: the text a user names becomes complete settings for one spawn, the
/// thinking step never exceeds what the model accepts, and every refusal says what to do next. Nothing here writes
/// settings — the override is per spawn by construction.
/// </summary>
public sealed class SpawnModelResolverTests : IDisposable
{
    private readonly ProviderCatalogFixture fixture = new();

    public void Dispose() => fixture.Dispose();

    private SpawnModelResolver Resolver(ProviderCatalog catalog) => new(() => catalog);

    private static SpawnModelRequest Request(string? model, string? provider, string? thinking, ModelSettings current) =>
        new(model, provider, thinking, current);

    [Fact]
    public void A_model_override_keeps_the_kinds_thinking_clamped_to_the_new_model()
    {
        var catalog = fixture.Create("probe", new FixtureProvider("probe", "Probe", ["claude-sonnet-4-5", "kimi-k2"]));
        var current = new ModelSettings("claude-sonnet-4-5", ThinkingEffort.High, null, "probe");

        var choice = Resolver(catalog).Resolve(Request("kimi-k2", null, null, current));

        Assert.Null(choice.Error);
        Assert.Equal(new ModelSettings("kimi-k2", ThinkingEffort.Off, null, "probe"), choice.Settings);
        Assert.NotNull(choice.Note);
        Assert.Contains("high", choice.Note);
        Assert.Contains("runs at off", choice.Note);
    }

    [Fact]
    public void A_thinking_only_override_keeps_the_model_and_clamps_the_step()
    {
        var catalog = fixture.Create("probe", new FixtureProvider("probe", "Probe", ["claude-sonnet-4-5"]));
        var current = new ModelSettings("claude-sonnet-4-5", ThinkingEffort.Low, null, "probe");

        var choice = Resolver(catalog).Resolve(Request(null, null, "max", current));

        Assert.Equal(new ModelSettings("claude-sonnet-4-5", ThinkingEffort.High, null, "probe"), choice.Settings);
        Assert.Contains("requested max", choice.Note);
        Assert.Contains("runs at high", choice.Note);
    }

    [Fact]
    public void A_step_the_model_accepts_is_stored_as_asked_without_a_note()
    {
        var catalog = fixture.Create("probe", new FixtureProvider("probe", "Probe", ["claude-sonnet-4-5"]));
        var current = new ModelSettings("claude-sonnet-4-5", ThinkingEffort.Low, null, "probe");

        var choice = Resolver(catalog).Resolve(Request(null, null, "medium", current));

        Assert.Equal(new ModelSettings("claude-sonnet-4-5", ThinkingEffort.Medium, null, "probe"), choice.Settings);
        Assert.Null(choice.Note);
    }

    [Fact]
    public void A_provider_only_override_moves_the_same_model_to_that_provider()
    {
        var catalog = fixture.Create(
            "first",
            new FixtureProvider("first", "First", ["kimi-k2"]),
            new FixtureProvider("second", "Second", ["kimi-k2"]));
        var current = new ModelSettings("kimi-k2", ThinkingEffort.Off, null, "first");

        var choice = Resolver(catalog).Resolve(Request(null, "second", null, current));

        Assert.Equal(new ModelSettings("kimi-k2", ThinkingEffort.Off, null, "second"), choice.Settings);
    }

    [Fact]
    public void A_provider_that_does_not_list_the_current_model_is_refused()
    {
        var catalog = fixture.Create(
            "probe",
            new FixtureProvider("probe", "Probe", ["claude-sonnet-4-5"]),
            new FixtureProvider("other", "Other", ["kimi-k2"]));
        var current = new ModelSettings("claude-sonnet-4-5", ThinkingEffort.Low, null, "probe");

        var choice = Resolver(catalog).Resolve(Request(null, "other", null, current));

        Assert.Null(choice.Settings);
        Assert.Contains("does not list the agent's current model 'claude-sonnet-4-5'", choice.Error);
    }

    [Fact]
    public void An_unknown_model_lists_the_way_forward()
    {
        var catalog = fixture.Create("probe", new FixtureProvider("probe", "Probe", ["kimi-k2"]));

        var choice = Resolver(catalog).Resolve(Request("gpt-9", null, null, new ModelSettings("kimi-k2")));

        Assert.Null(choice.Settings);
        Assert.Contains("unknown model 'gpt-9'", choice.Error);
        Assert.Contains("Call models", choice.Error);
    }

    [Fact]
    public void A_provider_level_pin_reaches_the_spawned_settings()
    {
        var catalog = fixture.Create("probe", new FixtureProvider("probe", "Probe", ["kimi-k2"], MaxOutputTokens: 5_000));
        var current = new ModelSettings("kimi-k2", ThinkingEffort.Off, null, "probe");

        var choice = Resolver(catalog).Resolve(Request(null, null, null, current));

        Assert.Equal(new ModelSettings("kimi-k2", ThinkingEffort.Off, 5_000, "probe"), choice.Settings);
    }

    [Fact]
    public void A_per_model_pin_wins_over_the_provider_level_figure()
    {
        var catalog = fixture.Create(
            "probe",
            new FixtureProvider(
                "probe",
                "Probe",
                ["kimi-k2"],
                MaxOutputTokens: 5_000,
                Overrides: new Dictionary<string, ModelMetadata> { ["kimi-k2"] = new(null, null, 4_096) }));
        var current = new ModelSettings("kimi-k2", ThinkingEffort.Off, null, "probe");

        var choice = Resolver(catalog).Resolve(Request(null, null, null, current));

        Assert.Equal(new ModelSettings("kimi-k2", ThinkingEffort.Off, 4_096, "probe"), choice.Settings);
    }

    [Fact]
    public void An_unpinned_model_carries_no_cap_even_when_the_catalog_knows_one()
    {
        // The catalog's limit is the model's capability, a display and compaction figure; loading it into the settings is
        // what put an aggregator's derived 943718 on the wire and had every request for the 131072-cap model refused.
        var catalog = fixture.Create(
            "opencode",
            ProviderCatalogFixture.Snapshot(("opencode", "glm-5.3", 131_072), ("openrouter", "z-ai/glm-5.3", 943_718)),
            new FixtureProvider("opencode", "OPENCODE", ["glm-5.3"]));
        var current = new ModelSettings("glm-5.3", ThinkingEffort.Off, null, "opencode");

        var choice = Resolver(catalog).Resolve(Request(null, null, null, current));

        Assert.Equal(new ModelSettings("glm-5.3", ThinkingEffort.Off, null, "opencode"), choice.Settings);
    }

    [Fact]
    public void A_model_two_providers_serve_is_refused_with_every_candidate()
    {
        var catalog = fixture.Create(
            "first",
            new FixtureProvider("first", "First", ["kimi-k2"]),
            new FixtureProvider("second", "Second", ["kimi-k2"]));

        var choice = Resolver(catalog).Resolve(Request("kimi-k2", null, null, new ModelSettings("kimi-k2")));

        Assert.Null(choice.Settings);
        Assert.Contains("first / kimi-k2", choice.Error);
        Assert.Contains("second / kimi-k2", choice.Error);
        Assert.Contains("Pass provider", choice.Error);
    }

    [Fact]
    public void An_unknown_thinking_lists_the_seven_steps()
    {
        var catalog = fixture.Create("probe", new FixtureProvider("probe", "Probe", ["kimi-k2"]));

        var choice = Resolver(catalog).Resolve(Request(null, null, "ultra", new ModelSettings("kimi-k2")));

        Assert.Null(choice.Settings);
        Assert.Contains("unknown thinking 'ultra'", choice.Error);
        Assert.Contains("off, minimal, low, medium, high, xhigh, max", choice.Error);
    }

    [Fact]
    public void An_unknown_provider_lists_the_connected_ones()
    {
        var catalog = fixture.Create("probe", new FixtureProvider("probe", "Probe", ["kimi-k2"]));

        var choice = Resolver(catalog).Resolve(Request("kimi-k2", "nope", null, new ModelSettings("kimi-k2")));

        Assert.Null(choice.Settings);
        Assert.Contains("unknown provider 'nope'", choice.Error);
        Assert.Contains("probe (Probe)", choice.Error);
    }

    [Fact]
    public void The_providers_output_ceiling_reaches_the_override()
    {
        var catalog = fixture.Create("probe", new FixtureProvider("probe", "Probe", ["kimi-k2"], MaxOutputTokens: 8192));

        var choice = Resolver(catalog).Resolve(Request("kimi-k2", null, null, new ModelSettings("kimi-k2")));

        Assert.Equal(8192, choice.Settings!.MaxOutputTokens);
    }

    [Fact]
    public void No_connected_provider_is_refused_before_any_matching()
    {
        var catalog = fixture.Create(null);

        var choice = Resolver(catalog).Resolve(Request("kimi-k2", null, null, new ModelSettings("kimi-k2")));

        Assert.Null(choice.Settings);
        Assert.Contains("no provider is connected", choice.Error);
    }
}
