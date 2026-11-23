using Forge.Abstractions;
using Forge.App.Providers;
using Forge.Core.Providers;

namespace Forge.App.Tests;

/// <summary>A provider the fixture builds: only the fields model lookup, thinking steps and metadata resolution read.</summary>
internal sealed record FixtureProvider(
    string Id,
    string Name,
    string[] Models,
    int? MaxOutputTokens = null,
    IReadOnlyDictionary<string, ModelMetadata>? Overrides = null);

/// <summary>
/// A real <see cref="ProviderCatalog"/> over a throwaway directory and HTTP client. The tests that need the catalogue
/// exercise the same chain the app runs (the compiled thinking table, model metadata, per-provider lists) instead of a
/// stand-in that could disagree with it.
/// </summary>
internal sealed class ProviderCatalogFixture : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "forge-catalog-" + Guid.NewGuid().ToString("n"));
    private readonly HttpClient http = new();
    private readonly ProviderSecretStore secrets;
    private readonly ProviderCredentials credentials;

    public ProviderCatalogFixture()
    {
        Directory.CreateDirectory(root);
        secrets = new ProviderSecretStore(root);
        credentials = new ProviderCredentials(http, secrets);
    }

    public ProviderCatalog Create(string? defaultId, params FixtureProvider[] providers) =>
        Create(defaultId, snapshot: null, providers);

    /// <summary>The same catalogue over a snapshot of the fetched catalog, so metadata resolution sees what the daily
    /// refresh would have delivered - without the network.</summary>
    public ProviderCatalog Create(string? defaultId, ModelCatalogSnapshot? snapshot, params FixtureProvider[] providers)
    {
        ModelCatalogStore? store = null;
        if (snapshot is not null)
        {
            store = new ModelCatalogStore(root);
            store.Apply(snapshot);
        }

        var definitions = new ProviderDefinition[providers.Length];
        for (var index = 0; index < providers.Length; index++)
        {
            var provider = providers[index];
            definitions[index] = new ProviderDefinition
            {
                Id = provider.Id,
                PresetId = "custom",
                DisplayName = provider.Name,
                Protocol = ProviderProtocol.OpenAiCompatible,
                BaseUrl = "https://example.invalid",
                CredentialKind = ProviderCredentialKind.ApiKey,
                Models = provider.Models,
                DefaultModel = provider.Models[0],
                MaxOutputTokens = provider.MaxOutputTokens,
                Overrides = provider.Overrides ?? new Dictionary<string, ModelMetadata>(StringComparer.Ordinal),
            };
        }

        return ProviderCatalog.Create(
            definitions,
            defaultId,
            secrets,
            credentials,
            options: store is null ? null : new ProviderCatalogOptions { ModelCatalog = store });
    }

    /// <summary>A snapshot with one window-1M model per entry, the shape the leak regression needs: two providers
    /// listing the same model id with different caps.</summary>
    public static ModelCatalogSnapshot Snapshot(params (string Provider, string Id, int? MaxOutput)[] entries)
    {
        var providers = new Dictionary<string, IReadOnlyDictionary<string, ModelCatalogModel>>(StringComparer.Ordinal);
        foreach (var group in entries.GroupBy(entry => entry.Provider, StringComparer.Ordinal))
        {
            var models = new Dictionary<string, ModelCatalogModel>(StringComparer.Ordinal);
            foreach (var entry in group)
            {
                models[entry.Id] = new ModelCatalogModel(1_000_000, entry.MaxOutput, null, null, null);
            }

            providers[group.Key] = models;
        }

        return new ModelCatalogSnapshot("https://catalog.example", null, DateTimeOffset.UtcNow, providers);
    }

    public void Dispose()
    {
        http.Dispose();
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A leftover temp directory is not worth failing a test over.
        }
    }
}
