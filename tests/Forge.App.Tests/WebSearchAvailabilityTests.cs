using System.Net;
using System.Text;
using Forge.App.Providers;
using Forge.App.Web;
using Forge.Core.Tools.Web;
using Xunit;

namespace Forge.App.Tests;

/// <summary>
/// Which backends the picker may dot green and <c>web_search</c> may run on: Exa needs no credential at all, DeepSeek borrows
/// the key of the DeepSeek model provider (same account), and the rest need their own key or URL. Every test runs against a
/// stub transport, so no search leaves the machine.
/// </summary>
public sealed class WebSearchAvailabilityTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "forge-web-" + Guid.NewGuid().ToString("n"));

    public WebSearchAvailabilityTests() => Directory.CreateDirectory(root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A leftover temp directory is not worth failing a test over.
        }
    }

    [Fact]
    public void ExaIsUsableOutOfTheBoxAndTheKeyBackendsAreNot()
    {
        using var http = new HttpClient(new Stub());
        var secrets = new ProviderSecretStore(root);
        var store = new WebSettingsStore(Path.Combine(root, "settings.json"));
        using var web = new WebServices(store, secrets, http);

        // The default is Exa, and Exa answers with no key, so search is registered before anything is configured.
        Assert.Equal(WebSettings.Exa, store.Current.SearchBackend);
        Assert.True(web.SearchAvailable());
        Assert.IsType<ExaSearchBackend>(web.CreateBackend(store.Current));

        Assert.False(web.IsUsable(WebSettings.Brave));
        Assert.False(web.IsUsable(WebSettings.Tavily));
        Assert.False(web.IsUsable(WebSettings.Deepseek));
        Assert.False(web.IsUsable(WebSettings.Searxng));
        Assert.False(web.IsUsable(null));
    }

    [Fact]
    public async Task DeepSeekRunsOnTheModelProvidersKey()
    {
        var handler = new Stub();
        using var http = new HttpClient(handler);
        var secrets = new ProviderSecretStore(root);
        var store = new WebSettingsStore(Path.Combine(root, "settings.json"));
        using var sharing = new WebServices(store, secrets, http, () => "sk-from-model-provider");
        using var alone = new WebServices(store, secrets, http);

        // Without a DeepSeek provider key there is nothing to borrow, so the backend stays unavailable.
        Assert.False(alone.IsUsable(WebSettings.Deepseek));

        var backend = sharing.CreateBackend(store.Current with { SearchBackend = WebSettings.Deepseek });
        Assert.NotNull(backend);
        var results = await backend.SearchAsync("avalonia", 1, CancellationToken.None);

        Assert.Equal("Bearer sk-from-model-provider", handler.Authorization);
        Assert.Single(results);
    }

    [Fact]
    public async Task TheWebSectionsOwnDeepSeekKeyWinsOverTheModelProviders()
    {
        var handler = new Stub();
        using var http = new HttpClient(handler);
        var secrets = new ProviderSecretStore(root);
        secrets.Set(WebServices.DeepseekKeyReference, "sk-from-web-section");
        var store = new WebSettingsStore(Path.Combine(root, "settings.json"));
        using var web = new WebServices(store, secrets, http, () => "sk-from-model-provider");

        var backend = web.CreateBackend(store.Current with { SearchBackend = WebSettings.Deepseek });
        Assert.NotNull(backend);
        await backend.SearchAsync("avalonia", 1, CancellationToken.None);

        Assert.Equal("Bearer sk-from-web-section", handler.Authorization);
    }

    /// <summary>Answers one DeepSeek search result and keeps the request's authorization header.</summary>
    private sealed class Stub : HttpMessageHandler
    {
        public string? Authorization { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Authorization = request.Headers.Authorization?.ToString();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"results":[{"title":"T","url":"https://example.com/1","summary":"s"}]}""",
                    Encoding.UTF8,
                    "application/json"),
            });
        }
    }
}
