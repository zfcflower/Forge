using System.Net;
using Forge.Abstractions;
using Forge.App.Providers;
using Forge.Core.Providers;
using Xunit;

namespace Forge.App.Tests.Composition;

/// <summary>
/// Pins the two provider-side sources of the metadata chain: the user's override, which is the strongest source, and
/// what a provider reports for its own models on <c>/models</c>, which sits between it and the built-in table. Both
/// merge field by field, so a window from one source can sit next to a price from another, and an entry that only
/// carries a cache price must not invent a whole price record.
/// Ported from the single-provider snapshot to the multi-provider catalogue: overrides live on
/// <c>ProviderDefinition</c>, reported metadata in <c>ProviderModelCache</c>, resolution on
/// <c>ConnectedProvider.MetadataFor</c> / <c>ProviderCatalog.MetadataFor</c>.
/// </summary>
public sealed class ProviderMetadataTests
{
    [Fact]
    public void A_settings_file_with_model_meta_produces_metadata_through_the_catalogue()
    {
        var root = Path.Combine(Path.GetTempPath(), "forge-provider-meta-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "settings.json");
        File.WriteAllText(
            path,
            """
            {
              "providers": {
                "version": 1,
                "defaultId": "house",
                "items": [
                  {
                    "id": "house",
                    "preset": "custom-openai",
                    "name": "House",
                    "protocol": "openai-compatible",
                    "baseUrl": "https://api.example.com",
                    "auth": { "kind": "api-key" },
                    "models": ["house-model"],
                    "defaultModel": "house-model",
                    "modelMeta": {
                      "house-model": { "contextWindow": 262144, "input": 0.5, "output": 1.5, "cacheRead": 0.05, "cacheWrite": 0.75 }
                    }
                  }
                ]
              }
            }
            """);

        try
        {
            var catalog = Load(path);

            Assert.True(catalog.IsConfigured);
            var metadata = catalog.MetadataFor("house-model");
            Assert.Equal(262_144, metadata.ContextWindow);
            Assert.Equal(new ModelPricing(0.5, 1.5, 0.05, 0.75), metadata.Pricing);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void A_model_the_settings_do_not_list_falls_back_to_the_built_in_table()
    {
        var provider = Provider(
            new Dictionary<string, ModelMetadataSection>
            {
                ["house-model"] = new() { ContextWindow = 262_144 },
            });

        Assert.Equal(262_144, provider.MetadataFor("house-model").ContextWindow);
        Assert.Equal(128_000, provider.MetadataFor("gpt-4o").ContextWindow);
        Assert.Equal(new ModelPricing(2.50, 10.00, 1.25, 0), provider.MetadataFor("gpt-4o").Pricing);
    }

    [Fact]
    public void The_user_override_wins_over_the_built_in_table_field_by_field()
    {
        var provider = Provider(
            new Dictionary<string, ModelMetadataSection>
            {
                // Only the window: the prices must stay the table's.
                ["gpt-4o"] = new() { ContextWindow = 64_000 },
            });

        var windowed = provider.MetadataFor("gpt-4o");
        Assert.Equal(64_000, windowed.ContextWindow);
        Assert.Equal(new ModelPricing(2.50, 10.00, 1.25, 0), windowed.Pricing);

        var priced = Provider(
            new Dictionary<string, ModelMetadataSection>
            {
                // Only the prices: the window must stay the table's. A price record replaces the whole record, so the
                // cache paths the table knew about are the ones this entry states (zero when it states none).
                ["gpt-4o"] = new() { Input = 1.00, Output = 2.00 },
            });

        var repriced = priced.MetadataFor("gpt-4o");
        Assert.Equal(128_000, repriced.ContextWindow);
        Assert.Equal(new ModelPricing(1.00, 2.00, 0, 0), repriced.Pricing);
    }

    [Fact]
    public void An_override_with_only_a_cache_price_invents_nothing()
    {
        var provider = Provider(
            new Dictionary<string, ModelMetadataSection>
            {
                // A cache price without an input or an output price is not a price record.
                ["house-model"] = new() { CacheRead = 0.25 },
                ["gpt-4o"] = new() { CacheWrite = 0.25 },
            });

        Assert.Same(ModelMetadata.Unknown, provider.MetadataFor("house-model"));
        Assert.Equal(new ModelPricing(2.50, 10.00, 1.25, 0), provider.MetadataFor("gpt-4o").Pricing);
    }

    [Fact]
    public void A_non_positive_window_in_the_settings_is_not_a_window()
    {
        var provider = Provider(
            new Dictionary<string, ModelMetadataSection>
            {
                ["house-model"] = new() { ContextWindow = 0 },
            });

        Assert.Same(ModelMetadata.Unknown, provider.MetadataFor("house-model"));
    }

    [Fact]
    public void An_entry_with_no_values_at_all_is_ignored()
    {
        var provider = Provider(
            new Dictionary<string, ModelMetadataSection> { ["gpt-4o"] = new() });

        var metadata = provider.MetadataFor("gpt-4o");
        Assert.Equal(128_000, metadata.ContextWindow);
        Assert.Equal(new ModelPricing(2.50, 10.00, 1.25, 0), metadata.Pricing);
    }

    [Fact]
    public async Task The_model_list_metadata_maps_a_reported_window_and_per_token_prices()
    {
        var cache = new ProviderModelCache();
        var provider = Provider(cache: cache);
        var handler = new ModelListHandler(
            """
            {
              "data": [
                {
                  "id": "vendor/model-a",
                  "context_length": 200000,
                  "pricing": {
                    "prompt": "0.0000005",
                    "completion": "0.0000015",
                    "input_cache_read": "0.00000005",
                    "input_cache_write": "0.000003"
                  }
                },
                {
                  "id": "model-b",
                  "top_provider": { "context_length": 32000 },
                  "pricing": { "prompt": 0.000002, "completion": 0.000004 }
                },
                { "id": "model-c" }
              ]
            }
            """);
        using var httpClient = new HttpClient(handler);

        var list = await ProviderModelListing.FetchAsync("https://api.example.com", headers: null, httpClient, CancellationToken.None);
        Assert.Equal(["vendor/model-a", "model-b", "model-c"], list.Ids);
        cache.Set(provider.Id, list);

        // Per-token prices in the response become the per-million unit the table and the strip use.
        var routed = provider.MetadataFor("vendor/model-a");
        Assert.Equal(200_000, routed.ContextWindow);
        Assert.NotNull(routed.Pricing);
        Assert.Equal(0.5, routed.Pricing!.Input, 9);
        Assert.Equal(1.5, routed.Pricing.Output, 9);
        Assert.Equal(0.05, routed.Pricing.CacheRead, 9);
        Assert.Equal(3.0, routed.Pricing.CacheWrite, 9);

        // A window can also sit under top_provider, and an absent cache price is billed as zero.
        var nested = provider.MetadataFor("model-b");
        Assert.Equal(32_000, nested.ContextWindow);
        Assert.NotNull(nested.Pricing);
        Assert.Equal(2.0, nested.Pricing!.Input, 9);
        Assert.Equal(4.0, nested.Pricing.Output, 9);
        Assert.Equal(0d, nested.Pricing.CacheRead, 9);

        // A model that reports nothing keeps whatever the built-in table knows and invents no window.
        Assert.Same(ModelMetadata.Unknown, provider.MetadataFor("model-c"));
    }

    [Fact]
    public async Task The_provider_window_beats_the_table_while_the_user_override_beats_the_provider()
    {
        var cache = new ProviderModelCache();
        var reported = Provider(cache: cache);
        var handler = new ModelListHandler(
            """{"data":[{"id":"gpt-4o","context_length":65536,"pricing":{"prompt":0.000001}}]}""");
        using var httpClient = new HttpClient(handler);

        var list = await ProviderModelListing.FetchAsync("https://api.example.com", headers: null, httpClient, CancellationToken.None);
        cache.Set(reported.Id, list);

        var fromProvider = reported.MetadataFor("gpt-4o");
        Assert.Equal(65_536, fromProvider.ContextWindow);
        Assert.NotNull(fromProvider.Pricing);
        Assert.Equal(1.0, fromProvider.Pricing!.Input, 9);
        Assert.Equal(0d, fromProvider.Pricing.Output, 9);

        // The same response, with the user's window in settings.json: the override wins, the provider's price stays.
        var overriddenCache = new ProviderModelCache();
        var overridden = Provider(
            new Dictionary<string, ModelMetadataSection> { ["gpt-4o"] = new() { ContextWindow = 1_000 } },
            overriddenCache);
        var overriddenHandler = new ModelListHandler(
            """{"data":[{"id":"gpt-4o","context_length":65536,"pricing":{"prompt":0.000001}}]}""");
        using var overriddenClient = new HttpClient(overriddenHandler);
        var overriddenList = await ProviderModelListing.FetchAsync("https://api.example.com", headers: null, overriddenClient, CancellationToken.None);
        overriddenCache.Set(overridden.Id, overriddenList);

        var resolved = overridden.MetadataFor("gpt-4o");
        Assert.Equal(1_000, resolved.ContextWindow);
        Assert.NotNull(resolved.Pricing);
        Assert.Equal(1.0, resolved.Pricing!.Input, 9);
    }

    [Fact]
    public async Task A_model_the_provider_reports_without_prices_takes_the_table_prices()
    {
        var cache = new ProviderModelCache();
        var provider = Provider(cache: cache);
        var handler = new ModelListHandler("""{"data":[{"id":"gpt-4o","context_length":65536}]}""");
        using var httpClient = new HttpClient(handler);

        var list = await ProviderModelListing.FetchAsync("https://api.example.com", headers: null, httpClient, CancellationToken.None);
        cache.Set(provider.Id, list);

        var metadata = provider.MetadataFor("gpt-4o");
        Assert.Equal(65_536, metadata.ContextWindow);
        Assert.Equal(new ModelPricing(2.50, 10.00, 1.25, 0), metadata.Pricing);
    }

    [Fact]
    public async Task The_reported_metadata_belongs_to_the_provider_that_fetched_it()
    {
        var cache = new ProviderModelCache();
        var reported = Provider(cache: cache, id: "reported");
        var unrelated = Provider(cache: cache, id: "unrelated");
        var handler = new ModelListHandler("""{"data":[{"id":"house-model","context_length":8192}]}""");
        using var httpClient = new HttpClient(handler);

        var list = await ProviderModelListing.FetchAsync("https://api.example.com", headers: null, httpClient, CancellationToken.None);
        cache.Set(reported.Id, list);

        Assert.Equal(8_192, reported.MetadataFor("house-model").ContextWindow);
        Assert.Same(ModelMetadata.Unknown, unrelated.MetadataFor("house-model"));
    }

    [Fact]
    public void A_missing_settings_file_seeds_one_anthropic_entry_that_is_never_ready()
    {
        // Rewritten for the multi-provider behavior: the old build reported "not configured" when the file was
        // missing; the new build seeds one Anthropic entry so the settings page opens on something fillable.
        var root = Path.Combine(Path.GetTempPath(), "forge-missing-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var catalog = Load(Path.Combine(root, "settings.json"));

            Assert.True(catalog.IsConfigured);
            var single = Assert.Single(catalog.Providers);
            Assert.Equal(ProviderPresets.AnthropicId, single.Definition.PresetId);
            Assert.Equal(ProviderPresets.AnthropicId, single.Id);
            Assert.NotEqual(ProviderStatusKind.Ready, single.Status.Kind);
            Assert.True(
                single.Status.Kind is ProviderStatusKind.NeedsKey or ProviderStatusKind.NeedsSignIn,
                "a seeded entry must say a key or sign-in is missing, got " + single.Status.Kind);
            // The table still resolves through the seeded catalogue.
            Assert.Equal(128_000, catalog.MetadataFor("gpt-4o").ContextWindow);
            Assert.Same(ModelMetadata.Unknown, catalog.MetadataFor("house-model-that-exists-nowhere"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static ProviderCatalog Load(string settingsPath)
    {
        // A directory that stays empty: no secret file, no key file, and a credentials path that cannot exist, so
        // the seeded entry's status never depends on the machine running the test.
        var probeDir = Path.Combine(Path.GetTempPath(), "forge-provider-meta-data-" + Guid.NewGuid().ToString("N"));
        var secrets = new ProviderSecretStore(probeDir);
        using var http = new HttpClient(new ModelListHandler("""{"data":[]}"""));
        var credentials = new ProviderCredentials(http, secrets, claudeCredentialsPath: Path.Combine(probeDir, "credentials.json"));
        return new ProviderStore(settingsPath).Load(secrets, credentials);
    }

    private static ConnectedProvider Provider(
        Dictionary<string, ModelMetadataSection>? modelMeta = null,
        ProviderModelCache? cache = null,
        string id = "house")
    {
        var entry = new ProviderEntryDocument
        {
            Id = id,
            Preset = ProviderPresets.CustomId,
            Name = "House",
            Protocol = "openai-compatible",
            BaseUrl = "https://api.example.com",
            Auth = new ProviderAuthDocument { Kind = "api-key" },
            Models = ["house-model"],
            DefaultModel = "house-model",
            ModelMeta = modelMeta,
        };
        var definition = ProviderDefinition.FromDocument(entry, id);
        // A path that is never created: nothing is stored, so nothing is written and nothing needs cleanup.
        var probeDir = Path.Combine(Path.GetTempPath(), "forge-provider-meta-store-" + Guid.NewGuid().ToString("N"));
        var secrets = new ProviderSecretStore(probeDir);
        var sharedCache = cache ?? new ProviderModelCache();
        using var http = new HttpClient(new ModelListHandler("""{"data":[]}"""));
        var credentials = new ProviderCredentials(http, secrets, claudeCredentialsPath: Path.Combine(probeDir, "credentials.json"));
        return new ConnectedProvider(definition, secrets, sharedCache, credentials);
    }

    /// <summary>Answers <c>/models</c> with one fixed document; the listing parser only reads that response here.</summary>
    private sealed class ModelListHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
    }
}
