using Forge.Abstractions;
using Forge.App.Localization;
using Forge.App.Preferences;
using Forge.App.Providers;
using Forge.App.Settings;
using Forge.App.Tests.Localization;
using Forge.Core.Providers;
using Xunit;

namespace Forge.App.Tests.Settings;

/// <summary>
/// Pins the settings page's model-catalog row: it says when the catalog was fetched, it refuses an address that is not an
/// absolute http(s) URL, and a saved address goes to <c>settings.json</c> — while a changed address re-fetches at once.
/// </summary>
public sealed class ProviderCatalogSettingsRowTests : IDisposable
{
    private const string CatalogJson = """
    {"anthropic":{"id":"anthropic","models":{"claude-sonnet-4-5":{"limit":{"context":200000,"output":64000},"modalities":{"input":["text","image"]},"reasoning":true,"reasoning_options":[{"type":"effort","values":["low","high"]}]}}}}
    """;

    private readonly string root = TestTempFiles.NewDirectory("forge-catalog-row-");
    private readonly HttpClient http;
    private readonly ProviderServices providers;
    private readonly ModelCatalogRefreshService refresh;
    private readonly ProvidersSettingsViewModel page;

    public ProviderCatalogSettingsRowTests()
    {
        Directory.CreateDirectory(Path.Combine(root, "data"));
        File.WriteAllText(Path.Combine(root, "settings.json"), """
        { "providers": { "version": 1, "items": [] } }
        """);
        var handler = new Handler();
        Handler_ = handler;
        http = new HttpClient(handler);
        providers = new ProviderServices(Path.Combine(root, "settings.json"), Path.Combine(root, "data"), http);
        refresh = new ModelCatalogRefreshService(providers, http);
        page = new ProvidersSettingsViewModel(providers, modelCatalogRefresh: refresh);
        page.Reload();
    }

    private Handler Handler_ { get; }

    public void Dispose()
    {
        page.Dispose();
        http.Dispose();
        TestTempFiles.DeleteQuietly(root);
    }

    [Fact]
    public void Before_any_fetch_the_row_says_it_never_updated()
    {
        Assert.Equal(ModelCatalogStrings.NeverText, page.CatalogStatusText);
        Assert.Equal(ModelCatalogFetcher.DefaultSourceUrl, page.CatalogSourceUrl);
        Assert.False(page.HasCatalogProblem);
        Assert.False(page.IsCatalogUpdating);
    }

    [Fact]
    public async Task After_a_fetch_the_row_says_when_and_how_much()
    {
        await refresh.RefreshAsync(ModelCatalogRefreshMode.Startup, CancellationToken.None);
        page.Reload();

        Assert.Contains(ModelCatalogStrings.UpdatedAt(providers.ModelCatalog.View.FetchedAt!.Value), page.CatalogStatusText, StringComparison.Ordinal);
        Assert.Contains(ModelCatalogStrings.Counts(1, 1), page.CatalogStatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_address_that_is_not_absolute_http_is_reported_and_nothing_is_saved()
    {
        page.CatalogSourceUrl = "models.dev/api.json";

        await page.CommitCatalogSourceAsync();

        Assert.True(page.HasCatalogProblem);
        Assert.Equal(ModelCatalogStrings.SourceInvalid, page.CatalogProblem);
        Assert.DoesNotContain("sourceUrl", File.ReadAllText(Path.Combine(root, "settings.json")), StringComparison.Ordinal);
        Assert.Equal(0, Handler_.Calls);
    }

    [Fact]
    public async Task A_new_address_is_saved_and_fetched_at_once()
    {
        page.CatalogSourceUrl = "https://mirror.invalid/api.json";

        await page.CommitCatalogSourceAsync();

        var settings = File.ReadAllText(Path.Combine(root, "settings.json"));
        Assert.Contains("https://mirror.invalid/api.json", settings, StringComparison.Ordinal);
        Assert.False(page.HasCatalogProblem);

        // The change is honoured regardless of today's date, so it re-fetches immediately (in the background).
        for (var attempt = 0; attempt < 100 && Handler_.Calls == 0; attempt++)
        {
            await Task.Delay(20);
        }

        Assert.Equal("https://mirror.invalid/api.json", Handler_.LastUri);
    }

    [Fact]
    public async Task An_unchanged_address_saves_without_fetching()
    {
        await page.CommitCatalogSourceAsync();

        Assert.Equal(0, Handler_.Calls);
        Assert.False(page.HasCatalogProblem);
    }

    [Fact]
    public void The_capability_summary_names_inputs_and_reasoning()
    {
        using var scope = new LanguageScope(UiLanguage.English);
        var metadata = new ModelMetadata(null, null)
        {
            Capabilities = new ModelCapabilities(ModelInputs.Text | ModelInputs.Image, [ThinkingEffort.Off, ThinkingEffort.Low, ThinkingEffort.High]),
        };

        Assert.Equal("text·image · reasoning × 2", ProviderStrings.CapabilitiesText(metadata.Capabilities));
        Assert.Equal("reasoning", ProviderStrings.CapabilitiesText(new ModelCapabilities(null, null) { Reasoning = true }));
        Assert.Equal(string.Empty, ProviderStrings.CapabilitiesText(null));
    }

    private sealed class Handler : HttpMessageHandler
    {
        public int Calls { get; private set; }

        public string? LastUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            LastUri = request.RequestUri?.AbsoluteUri;
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(CatalogJson),
            });
        }
    }
}
