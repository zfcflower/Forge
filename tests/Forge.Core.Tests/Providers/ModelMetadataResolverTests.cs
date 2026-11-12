using Forge.Abstractions;
using Forge.Core.Providers;
using Xunit;

namespace Forge.Core.Tests.Providers;

/// <summary>
/// Pins the metadata chain (<c>ModelMetadataResolver</c>): the fetched catalog snapshot is the weakest source, then what
/// the provider reported for its own models, then the user's <c>settings.json</c> override. The merge is field-wise, so a
/// window can come from one source while the prices come from another, and a source that knows nothing must never erase
/// what a weaker one knew.
/// </summary>
public sealed class ModelMetadataResolverTests
{
    private static readonly ModelPricing CatalogPrices = new(2.50, 10.00, 1.25, 0);

    private static readonly ModelPricing ProviderPrices = new(9, 8, 7, 6);

    private static readonly ModelPricing UserPrices = new(1, 2, 3, 4);

    [Fact]
    public void The_strongest_source_wins_each_field_on_its_own()
    {
        var fromCatalog = ModelMetadataResolver.Resolve("gpt-4o", new ModelMetadata(128_000, CatalogPrices), null, null);
        var fromProvider = ModelMetadataResolver.Resolve("gpt-4o", new ModelMetadata(128_000, CatalogPrices), new ModelMetadata(64_000, null), null);
        var fromUser = ModelMetadataResolver.Resolve(
            "gpt-4o",
            new ModelMetadata(128_000, CatalogPrices),
            new ModelMetadata(64_000, null),
            new ModelMetadata(32_000, null));

        Assert.Equal(128_000, fromCatalog.ContextWindow);
        Assert.Equal(CatalogPrices, fromCatalog.Pricing);
        Assert.Equal(64_000, fromProvider.ContextWindow);
        Assert.Equal(CatalogPrices, fromProvider.Pricing);
        Assert.Equal(32_000, fromUser.ContextWindow);
        Assert.Equal(CatalogPrices, fromUser.Pricing);
    }

    [Fact]
    public void The_user_override_beats_the_provider_prices_that_beat_the_catalog()
    {
        var fromProvider = ModelMetadataResolver.Resolve("gpt-4o", new ModelMetadata(null, CatalogPrices), new ModelMetadata(null, ProviderPrices), null);
        var fromUser = ModelMetadataResolver.Resolve(
            "gpt-4o",
            new ModelMetadata(null, CatalogPrices),
            new ModelMetadata(null, ProviderPrices),
            new ModelMetadata(null, UserPrices));

        Assert.Equal(ProviderPrices, fromProvider.Pricing);
        Assert.Equal(UserPrices, fromUser.Pricing);
        // No source named a window, so the result keeps it unknown instead of inventing one.
        Assert.Null(fromUser.ContextWindow);
    }

    [Fact]
    public void A_window_from_one_source_sits_next_to_a_price_from_another()
    {
        // The provider only knows prices, the user only knows the window: the result carries both halves.
        var resolved = ModelMetadataResolver.Resolve(
            "gpt-4o",
            catalogSnapshot: null,
            new ModelMetadata(null, ProviderPrices),
            new ModelMetadata(32_768, null));

        Assert.Equal(32_768, resolved.ContextWindow);
        Assert.Equal(ProviderPrices, resolved.Pricing);
        Assert.True(resolved.IsKnown);
    }

    [Fact]
    public void A_window_the_user_overrides_keeps_the_catalog_prices_beside_it()
    {
        var resolved = ModelMetadataResolver.Resolve(
            "claude-sonnet-4-5",
            new ModelMetadata(200_000, new ModelPricing(3.00, 15.00, 0.30, 3.75)),
            null,
            new ModelMetadata(1_000_000, null));

        Assert.Equal(1_000_000, resolved.ContextWindow);
        Assert.Equal(new ModelPricing(3.00, 15.00, 0.30, 3.75), resolved.Pricing);
    }

    [Fact]
    public void Capabilities_merge_field_wise_like_the_numbers()
    {
        // The catalog knows the input kinds and the steps; the user override only narrows the inputs.
        var resolved = ModelMetadataResolver.Resolve(
            "mystery-model",
            new ModelMetadata(null, null)
            {
                Capabilities = new ModelCapabilities(
                    ModelInputs.Text | ModelInputs.Image,
                    [ThinkingEffort.Off, ThinkingEffort.High]),
            },
            null,
            new ModelMetadata(null, null) { Capabilities = new ModelCapabilities(ModelInputs.Text, null) });

        Assert.NotNull(resolved.Capabilities);
        Assert.Equal(ModelInputs.Text, resolved.Capabilities!.Inputs);
        Assert.Equal([ThinkingEffort.Off, ThinkingEffort.High], resolved.Capabilities.ReasoningSteps);
    }

    [Fact]
    public void Everything_unknown_stays_unknown()
    {
        var resolved = ModelMetadataResolver.Resolve("no-such-model", ModelMetadata.Unknown, ModelMetadata.Unknown, ModelMetadata.Unknown);

        Assert.Same(ModelMetadata.Unknown, resolved);
        Assert.False(resolved.IsKnown);
        Assert.Null(resolved.ContextWindow);
        Assert.Null(resolved.Pricing);
    }

    [Fact]
    public void An_empty_model_id_with_no_other_source_is_unknown()
    {
        Assert.Same(ModelMetadata.Unknown, ModelMetadataResolver.Resolve(null, null, null, null));
        Assert.Same(ModelMetadata.Unknown, ModelMetadataResolver.Resolve(string.Empty, null, null, null));
    }

    [Fact]
    public void A_non_positive_window_is_ignored_instead_of_erasing_a_known_one()
    {
        var resolved = ModelMetadataResolver.Resolve(
            "gpt-4o",
            new ModelMetadata(128_000, null),
            new ModelMetadata(0, null),
            new ModelMetadata(-1, null));

        Assert.Equal(128_000, resolved.ContextWindow);
    }

    [Fact]
    public void Merge_ignores_a_null_or_unknown_source()
    {
        var known = new ModelMetadata(1_000, UserPrices);

        Assert.Same(known, known.With(null));
        Assert.Same(known, known.With(ModelMetadata.Unknown));
        Assert.Same(ModelMetadata.Unknown, ModelMetadata.Unknown.With(null));
    }

    [Fact]
    public void Capabilities_alone_make_metadata_known()
    {
        var capabilitiesOnly = new ModelMetadata(null, null)
        {
            Capabilities = new ModelCapabilities(ModelInputs.Text, null),
        };

        Assert.True(capabilitiesOnly.IsKnown);
        var resolved = ModelMetadataResolver.Resolve("m", null, capabilitiesOnly, null);
        Assert.Equal(ModelInputs.Text, resolved.Capabilities?.Inputs);
    }
}
