using Forge.Abstractions;
using Forge.App.Providers;
using Forge.Core.Providers;
using Xunit;

namespace Forge.App.Tests.Providers;

/// <summary>
/// Pins the two derived files under the data root: the catalog snapshot (<c>model-catalog.json</c>) and the provider model
/// lists (<c>provider-models.json</c>). Both are caches — deleting them costs a fetch, never a setting — so a damaged file
/// must degrade to "nothing cached", not to a startup failure.
/// </summary>
public sealed class ModelCatalogStorageTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "forge-catalog-store-" + Guid.NewGuid().ToString("N"));

    public ModelCatalogStorageTests() => Directory.CreateDirectory(root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>A clock that does not move; the catalog's "is today's here?" test must follow it, not the wall clock.</summary>
    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static ModelCatalogSnapshot Snapshot(DateTimeOffset fetchedAt) => new(
        "https://models.dev/api.json",
        "\"v1\"",
        fetchedAt,
        new Dictionary<string, IReadOnlyDictionary<string, ModelCatalogModel>>(StringComparer.Ordinal)
        {
            ["anthropic"] = new Dictionary<string, ModelCatalogModel>(StringComparer.Ordinal)
            {
                ["claude-sonnet-4-5"] = new(200_000, 64_000, new ModelPricing(3, 15, 0.3, 3.75), ModelInputs.Text, [ThinkingEffort.Off, ThinkingEffort.High]),
            },
        });

    [Fact]
    public void A_stored_snapshot_is_read_back_by_the_next_start()
    {
        var store = new ModelCatalogStore(root);
        store.LoadNow();
        store.Apply(Snapshot(DateTimeOffset.UtcNow));

        var reopened = new ModelCatalogStore(root);
        reopened.LoadNow();

        Assert.True(File.Exists(Path.Combine(root, ModelCatalogStore.FileName)));
        Assert.Equal(200_000, reopened.View.Lookup(ProviderPresets.AnthropicId, "claude-sonnet-4-5").ContextWindow);
        Assert.Null(reopened.LoadProblem);
    }

    [Fact]
    public void A_damaged_snapshot_reads_as_no_catalog_with_a_stated_problem()
    {
        File.WriteAllText(Path.Combine(root, ModelCatalogStore.FileName), "{not json");

        var store = new ModelCatalogStore(root);
        store.LoadNow();

        Assert.NotNull(store.LoadProblem);
        Assert.Same(ModelMetadata.Unknown, store.View.Lookup(ProviderPresets.AnthropicId, "claude-sonnet-4-5"));
    }

    [Fact]
    public void The_store_knows_whether_todays_snapshot_is_here()
    {
        var store = new ModelCatalogStore(root);
        store.LoadNow();
        Assert.False(store.IsFromToday(TimeZoneInfo.Utc));
        Assert.Null(store.FetchedOn(TimeZoneInfo.Utc));

        store.Apply(Snapshot(DateTimeOffset.UtcNow));

        Assert.True(store.IsFromToday(TimeZoneInfo.Utc));
        Assert.Equal(DateOnly.FromDateTime(DateTime.UtcNow), store.FetchedOn(TimeZoneInfo.Utc));
    }

    [Fact]
    public void The_todays_catalog_question_follows_the_injected_clock()
    {
        // A fixed clock on the 13th: a snapshot checked on the 12th is yesterday's, one checked the same day is today's.
        var clock = new FixedTimeProvider(new DateTimeOffset(2026, 11, 13, 9, 0, 0, TimeSpan.Zero));
        var store = new ModelCatalogStore(root, clock);
        store.LoadNow();
        store.Apply(Snapshot(new DateTimeOffset(2026, 11, 12, 22, 0, 0, TimeSpan.Zero)));

        Assert.False(store.IsFromToday(TimeZoneInfo.Utc));

        store.Apply(Snapshot(new DateTimeOffset(2026, 11, 13, 4, 0, 0, TimeSpan.Zero)));
        Assert.True(store.IsFromToday(TimeZoneInfo.Utc));
        Assert.Equal(new DateOnly(2026, 11, 13), store.FetchedOn(TimeZoneInfo.Utc));
    }

    [Fact]
    public void A_provider_model_list_survives_a_restart_with_its_order_and_metadata()
    {
        var path = Path.Combine(root, ProviderModelsFile.FileName);
        var first = new ProviderModelCache(path);
        first.LoadNow();
        first.Set(
            "ai",
            new ProviderModelList(
                ["model-b", "model-a"],
                new Dictionary<string, ModelMetadata>(StringComparer.Ordinal)
                {
                    ["model-a"] = new(32_000, new ModelPricing(1, 2, 0.1, 0)),
                }));
        first.SaveNow();

        var reopened = new ProviderModelCache(path);
        reopened.LoadNow();

        Assert.Equal(["model-b", "model-a"], reopened.ModelsOf("ai"));
        Assert.Equal(32_000, reopened.Get("ai", "model-a")?.ContextWindow);
        Assert.Equal(ModelMetadata.Unknown, reopened.Get("ai", "model-b"));
        Assert.Null(reopened.Get("other", "model-a"));
    }

    [Fact]
    public void A_damaged_provider_list_file_leaves_the_cache_empty()
    {
        var path = Path.Combine(root, ProviderModelsFile.FileName);
        File.WriteAllText(path, "{\"version\":99}");

        var cache = new ProviderModelCache(path);
        cache.LoadNow();

        Assert.Null(cache.ModelsOf("ai"));
    }

    [Fact]
    public void A_memory_only_cache_writes_nothing()
    {
        var cache = new ProviderModelCache();
        cache.Set("ai", new ProviderModelList(["model-a"], new Dictionary<string, ModelMetadata>(StringComparer.Ordinal)));
        cache.SaveNow();

        Assert.Empty(Directory.GetFiles(root));
        Assert.Equal(["model-a"], cache.ModelsOf("ai"));
    }

    [Fact]
    public void A_resolved_metadata_is_memoized_and_dropped_when_an_input_changes()
    {
        var memo = new ModelMetadataMemo();
        Assert.False(memo.TryGet("ai", "model-a", out _));

        memo.Set("ai", "model-a", new ModelMetadata(32_000, null));
        Assert.True(memo.TryGet("ai", "model-a", out var cached));
        Assert.Equal(32_000, cached.ContextWindow);
        Assert.Equal(1, memo.Count);

        memo.Invalidate();
        Assert.False(memo.TryGet("ai", "model-a", out _));
        Assert.Equal(0, memo.Count);
    }
}
