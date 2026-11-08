using Forge.Abstractions;
using Forge.Core.Providers;
using Xunit;

namespace Forge.Core.Tests.Providers;

/// <summary>
/// Pins the matching rules of the built-in model metadata table (task spec "Forge.Core", <c>ModelMetadataCatalog</c>):
/// after normalizing, an exact id wins, then the longest id the model id starts with, then the longest id it contains.
/// A model the table does not know must stay <see cref="ModelMetadata.Unknown"/> rather than inherit a neighbour's
/// window or price — the strip then shows tokens instead of a percentage and a cost.
/// </summary>
public sealed class ModelMetadataCatalogTests
{
    [Fact]
    public void An_exact_id_beats_the_family_id_that_prefixes_it()
    {
        var family = ModelMetadataCatalog.Lookup("gpt-4.1");
        var mini = ModelMetadataCatalog.Lookup("gpt-4.1-mini");

        Assert.Equal(1_047_576, family.ContextWindow);
        Assert.Equal(new ModelPricing(2.00, 8.00, 0.50, 0), family.Pricing);
        Assert.Equal(1_047_576, mini.ContextWindow);
        Assert.Equal(new ModelPricing(0.40, 1.60, 0.10, 0), mini.Pricing);
    }

    [Fact]
    public void A_prefix_beats_an_id_that_is_only_contained()
    {
        // "claude-sonnet-4-5" is both a prefix and contained here; the prefix pass must answer before the containment pass.
        var dated = ModelMetadataCatalog.Lookup("claude-sonnet-4-5-20250929");

        Assert.Equal(200_000, dated.ContextWindow);
        Assert.Equal(new ModelPricing(3.00, 15.00, 0.30, 3.75), dated.Pricing);
    }

    [Fact]
    public void A_prefix_match_wins_over_a_longer_id_that_is_only_contained()
    {
        // A synthetic id built so the two passes disagree: the containment pass would answer "claude-sonnet-4-5"
        // first (it is the longest entry and it is contained), the prefix pass answers "gpt-4.1". The prefix result is
        // the documented one, so the ordering of the passes is what this pins — not either entry's numbers.
        var prefixed = ModelMetadataCatalog.Lookup("gpt-4.1-claude-sonnet-4-5");

        Assert.Equal(1_047_576, prefixed.ContextWindow);
        Assert.Equal(new ModelPricing(2.00, 8.00, 0.50, 0), prefixed.Pricing);
        Assert.NotEqual(ModelMetadataCatalog.Lookup("claude-sonnet-4-5").Pricing, prefixed.Pricing);
    }

    [Fact]
    public void The_longest_prefix_wins_over_a_shorter_one()
    {
        // "gpt-4o" is contained, "gpt-4o-mini" is the longest prefix: the mini prices must come back, not the family's.
        var dated = ModelMetadataCatalog.Lookup("gpt-4o-mini-2024-07-18");

        Assert.Equal(128_000, dated.ContextWindow);
        Assert.Equal(new ModelPricing(0.15, 0.60, 0.075, 0), dated.Pricing);
    }

    [Fact]
    public void An_id_that_only_contains_a_known_one_still_matches()
    {
        // No slash, no known prefix: only the containment pass can recognize this gateway spelling.
        var prefixed = ModelMetadataCatalog.Lookup("openai-gpt-4o-preview");

        Assert.Equal(128_000, prefixed.ContextWindow);
        Assert.Equal(new ModelPricing(2.50, 10.00, 1.25, 0), prefixed.Pricing);
    }

    [Theory]
    [InlineData("deepseek/deepseek-chat", 128_000)] // OpenRouter-style vendor prefix
    [InlineData("openrouter/anthropic/claude-haiku-4-5", 200_000)] // only the last segment counts
    [InlineData("gpt-4o:free", 128_000)] // gateway suffix
    [InlineData("gpt-4o-latest", 128_000)] // moving alias
    [InlineData("GPT-4O", 128_000)] // upper case
    [InlineData("  gpt-4o  ", 128_000)] // surrounding whitespace
    [InlineData("Claude-Sonnet-4-5-LATEST", 200_000)] // suffix strip is case-insensitive too
    public void Normalization_maps_every_spelling_of_an_id_onto_the_table(string modelId, int window)
    {
        Assert.Equal(window, ModelMetadataCatalog.Lookup(modelId).ContextWindow);
    }

    [Fact]
    public void A_vendor_prefixed_id_keeps_its_own_prices()
    {
        var routed = ModelMetadataCatalog.Lookup("deepseek/deepseek-chat");

        Assert.Equal(new ModelPricing(0.27, 1.10, 0.07, 0), routed.Pricing);
    }

    [Theory]
    [InlineData("totally-unknown-model")]
    [InlineData("llama-3.1-70b")]
    [InlineData("model:free")]
    [InlineData("deepseek/")]
    public void An_unknown_id_is_unknown(string modelId)
    {
        Assert.Same(ModelMetadata.Unknown, ModelMetadataCatalog.Lookup(modelId));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_missing_id_is_unknown(string? modelId)
    {
        Assert.Same(ModelMetadata.Unknown, ModelMetadataCatalog.Lookup(modelId));
    }
}
