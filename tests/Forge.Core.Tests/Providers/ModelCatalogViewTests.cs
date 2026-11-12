using Forge.Abstractions;
using Forge.Core.Providers;
using Xunit;

namespace Forge.Core.Tests.Providers;

/// <summary>
/// Pins how a fetched catalog answers "what does this provider charge for this model": its own catalog entry first (prices
/// differ per provider), a model-id fallback for providers the build cannot map, and the strict id matching that keeps a
/// version bump from inheriting a familiar family's window.
/// </summary>
public sealed class ModelCatalogViewTests
{
    private static readonly DateTimeOffset FetchedAt = new(2026, 11, 12, 5, 0, 0, TimeSpan.Zero);

    private static ModelCatalogModel Entry(double input, double output, int context = 200_000) =>
        new(context, 64_000, new ModelPricing(input, output, 0, 0), ModelInputs.Text, null);

    /// <summary>Three providers, the same model id everywhere with different prices, which is what models.dev looks like.</summary>
    private static ModelCatalogView View() => ModelCatalogView.Create(new ModelCatalogSnapshot(
        "https://models.dev/api.json",
        null,
        FetchedAt,
        new Dictionary<string, IReadOnlyDictionary<string, ModelCatalogModel>>(StringComparer.Ordinal)
        {
            ["anthropic"] = new Dictionary<string, ModelCatalogModel>(StringComparer.Ordinal)
            {
                ["claude-sonnet-4-5"] = Entry(3, 15) with
                {
                    Inputs = ModelInputs.Text | ModelInputs.Image | ModelInputs.Pdf,
                    ReasoningSteps = [ThinkingEffort.Off, ThinkingEffort.Low, ThinkingEffort.High],
                },
                ["claude-haiku-4-5"] = Entry(1, 5),
            },
            ["openrouter"] = new Dictionary<string, ModelCatalogModel>(StringComparer.Ordinal)
            {
                ["anthropic/claude-sonnet-4-5"] = Entry(3.3, 16.5),
                ["moonshotai/kimi-k2"] = Entry(0.6, 2.5),
            },
            ["moonshotai"] = new Dictionary<string, ModelCatalogModel>(StringComparer.Ordinal)
            {
                ["kimi-k2"] = Entry(0.55, 2.2, 256_000),
            },
        }));

    [Fact]
    public void A_mapped_preset_answers_from_its_own_catalog_entry()
    {
        var view = View();

        Assert.Equal(new ModelPricing(3, 15, 0, 0), view.Lookup(ProviderPresets.AnthropicId, "claude-sonnet-4-5").Pricing);
        Assert.Equal(new ModelPricing(3.3, 16.5, 0, 0), view.Lookup(ProviderPresets.OpenRouterId, "anthropic/claude-sonnet-4-5").Pricing);
        Assert.Equal(256_000, view.Lookup(ProviderPresets.KimiId, "kimi-k2").ContextWindow);
    }

    [Fact]
    public void A_provider_the_build_cannot_map_falls_back_to_the_model_id()
    {
        // A custom gateway: nothing says it is Anthropic, but the model id is one the catalog knows. First-party wins the
        // ambiguity, so the answer is Anthropic's list price rather than an aggregator's marked-up one.
        var view = View();

        Assert.Equal(new ModelPricing(3, 15, 0, 0), view.Lookup(ProviderPresets.CustomId, "claude-sonnet-4-5").Pricing);
        Assert.Equal(new ModelPricing(3, 15, 0, 0), view.Lookup(null, "anthropic/claude-sonnet-4-5").Pricing);
    }

    [Fact]
    public void A_dated_snapshot_of_a_known_model_resolves_to_it()
    {
        var view = View();

        Assert.Equal(200_000, view.Lookup(ProviderPresets.AnthropicId, "claude-sonnet-4-5-20250929").ContextWindow);
        Assert.Equal(200_000, view.Lookup(ProviderPresets.AnthropicId, "claude-sonnet-4-5-latest").ContextWindow);
        Assert.Equal(200_000, view.Lookup(ProviderPresets.AnthropicId, "claude-sonnet-4-5-20250929-v1").ContextWindow);
    }

    [Fact]
    public void A_version_bump_is_a_different_model_and_resolves_to_nothing()
    {
        var view = View();

        // gpt-4o is not here, but the rule under test is: "claude-sonnet-4-6" must not inherit 4-5's window.
        Assert.Same(ModelMetadata.Unknown, view.Lookup(ProviderPresets.AnthropicId, "claude-sonnet-4-6"));
    }

    [Fact]
    public void A_known_id_contained_in_a_longer_one_still_matches()
    {
        var view = ModelCatalogView.Create(new ModelCatalogSnapshot(
            "u",
            null,
            FetchedAt,
            new Dictionary<string, IReadOnlyDictionary<string, ModelCatalogModel>>(StringComparer.Ordinal)
            {
                ["openai"] = new Dictionary<string, ModelCatalogModel>(StringComparer.Ordinal)
                {
                    ["gpt-4o"] = Entry(2.5, 10),
                },
            }));

        Assert.Equal(200_000, view.Lookup(ProviderPresets.OpenAiId, "openai-gpt-4o-preview").ContextWindow);
    }

    [Fact]
    public void Capabilities_and_steps_come_from_the_same_entry()
    {
        var view = View();

        var metadata = view.Lookup(ProviderPresets.AnthropicId, "claude-sonnet-4-5");
        Assert.NotNull(metadata.Capabilities);
        Assert.Equal(ModelInputs.Text | ModelInputs.Image | ModelInputs.Pdf, metadata.Capabilities!.Inputs);
        Assert.Equal([ThinkingEffort.Off, ThinkingEffort.Low, ThinkingEffort.High], view.ReasoningStepsFor(ProviderPresets.AnthropicId, "claude-sonnet-4-5"));

        // A model the catalog says nothing about has no step set, so the caller keeps its own table.
        Assert.Null(view.ReasoningStepsFor(ProviderPresets.AnthropicId, "claude-haiku-4-5"));
    }

    [Fact]
    public void The_empty_view_answers_unknown_for_everything()
    {
        Assert.Same(ModelMetadata.Unknown, ModelCatalogView.Empty.Lookup(ProviderPresets.AnthropicId, "claude-sonnet-4-5"));
        Assert.Null(ModelCatalogView.Empty.FetchedAt);
        Assert.Null(ModelCatalogView.Empty.SourceUrl);
        Assert.Equal(0, ModelCatalogView.Empty.ProviderCount);
        Assert.Equal(0, ModelCatalogView.Empty.ModelCount);
        Assert.Null(ModelCatalogView.Create(null).Snapshot);
    }

    [Fact]
    public void The_view_reports_what_it_holds()
    {
        var view = View();

        Assert.Equal(3, view.ProviderCount);
        Assert.Equal(5, view.ModelCount);
        Assert.Equal(FetchedAt, view.FetchedAt);
        Assert.Equal("https://models.dev/api.json", view.SourceUrl);
    }

    [Theory]
    [InlineData(ProviderPresets.AnthropicId, "anthropic")]
    [InlineData(ProviderPresets.OpenAiId, "openai")]
    [InlineData(ProviderPresets.CopilotId, "github-copilot")]
    [InlineData(ProviderPresets.OpenRouterId, "openrouter")]
    [InlineData(ProviderPresets.KimiId, "moonshotai")]
    [InlineData(ProviderPresets.MetaId, "meta")]
    [InlineData(ProviderPresets.XAiId, "xai")]
    public void Every_preset_this_build_knows_maps_to_one_catalog_provider(string presetId, string expected)
    {
        Assert.Equal(expected, ModelCatalogView.ProviderIdForPreset(presetId));
    }

    [Theory]
    [InlineData("opencode-zen")]
    [InlineData("opencode-go")]
    [InlineData("deepseek")]
    [InlineData("chatgpt")]
    public void A_preset_that_is_no_longer_built_in_has_no_catalog_provider_of_its_own(string retiredPresetId)
    {
        // The catalog still prices those vendors, but only through the global model-id fallback: a provider stored under a
        // retired preset is a custom entry now, and must not be mapped to a vendor it no longer claims to be.
        Assert.Null(ModelCatalogView.ProviderIdForPreset(retiredPresetId));
    }

    [Fact]
    public void A_custom_or_missing_preset_has_no_catalog_provider()
    {
        Assert.Null(ModelCatalogView.ProviderIdForPreset(ProviderPresets.CustomId));
        Assert.Null(ModelCatalogView.ProviderIdForPreset(null));
        Assert.Null(ModelCatalogView.ProviderIdForPreset("  "));
        Assert.Null(ModelCatalogView.ProviderIdForPreset("a-preset-added-upstream"));
    }
}
