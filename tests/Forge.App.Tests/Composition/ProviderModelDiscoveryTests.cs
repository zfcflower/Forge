using System.Net;
using System.Net.Http.Headers;
using Forge.App.Providers;
using Forge.App.Services;
using Forge.App.ViewModels;
using Xunit;

namespace Forge.App.Tests.Composition;

/// <summary>
/// Model discovery through <c>ProviderServices.RefreshModelsAsync</c>: the configured environment key travels as a
/// bearer header, the answer lands in the in-memory cache, and the composer lists it. Ported from the single-provider
/// snapshot: the second test is rewritten because the new stack no longer fails before the network when the key is
/// missing — it fetches without an <c>Authorization</c> header instead.
/// </summary>
public sealed class ProviderModelDiscoveryTests
{
    [Fact]
    public async Task Composer_refresh_fetches_and_exposes_models_using_the_configured_environment_key()
    {
        const string variable = "DEEPSEEK_API_KEY";
        const string testKey = "test-key-not-a-credential";
        var originalKey = Environment.GetEnvironmentVariable(variable);
        Environment.SetEnvironmentVariable(variable, testKey);
        var root = TestTempFiles.NewDirectory("forge-model-discovery-");

        try
        {
            var handler = new ModelListHandler();
            using var httpClient = new HttpClient(handler);
            var services = CreateServices(root, httpClient, variable);
            var provider = Assert.Single(services.Catalog.Providers);

            var list = await services.RefreshModelsAsync(provider, CancellationToken.None);

            Assert.Equal(HttpMethod.Get, handler.Method);
            Assert.Equal(new Uri("https://api.deepseek.com/models"), handler.RequestUri);
            Assert.Equal(new AuthenticationHeaderValue("Bearer", testKey), handler.Authorization);
            Assert.Equal(["deepseek-chat", "deepseek-reasoner"], list.Ids);

            var composer = new ComposerViewModel(
                services,
                _ => { },
                new ComposerPreferences(),
                string.Empty,
                skillSource: static () => []);
            Assert.Equal(["deepseek-chat", "deepseek-reasoner"], composer.Models.Select(static model => model.Name));

            // The key itself never enters the definition or the stored document (only the variable name does).
            Assert.DoesNotContain(testKey, provider.Definition.ToString(), StringComparison.Ordinal);
            var stored = System.Text.Json.JsonSerializer.Serialize(
                provider.Definition.ToDocument(),
                Forge.App.Providers.ProvidersJsonContext.Default.ProviderEntryDocument);
            Assert.DoesNotContain(testKey, stored, StringComparison.Ordinal);
            Assert.Contains(variable, stored, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, originalKey);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Model_discovery_fetches_without_an_authorization_header_when_the_environment_key_is_missing()
    {
        // Rewritten: the old snapshot threw before the network when the key was missing; the new stack sends the
        // listing without credentials instead (the server decides whether that is allowed).
        const string variable = "DEEPSEEK_API_KEY";
        var originalKey = Environment.GetEnvironmentVariable(variable);
        Environment.SetEnvironmentVariable(variable, null);
        var root = TestTempFiles.NewDirectory("forge-model-discovery-missing-");

        try
        {
            var handler = new ModelListHandler();
            using var httpClient = new HttpClient(handler);
            var services = CreateServices(root, httpClient, variable);
            var provider = Assert.Single(services.Catalog.Providers);

            var list = await services.RefreshModelsAsync(provider, CancellationToken.None);

            Assert.Equal(new Uri("https://api.deepseek.com/models"), handler.RequestUri);
            Assert.Null(handler.Authorization);
            Assert.Equal(["deepseek-chat", "deepseek-reasoner"], list.Ids);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, originalKey);
            Directory.Delete(root, recursive: true);
        }
    }

    private static ProviderServices CreateServices(string root, HttpClient http, string variable)
    {
        var settingsPath = Path.Combine(root, "settings.json");
        File.WriteAllText(
            settingsPath,
            $$"""
            {
              "providers": {
                "version": 1,
                "defaultId": "deepseek",
                "items": [
                  {
                    "id": "deepseek",
                    "preset": "deepseek",
                    "name": "DeepSeek",
                    "protocol": "openai-compatible",
                    "baseUrl": "https://api.deepseek.com/",
                    "auth": { "kind": "api-key", "envVar": "{{variable}}" },
                    "models": ["deepseek-chat"],
                    "defaultModel": "deepseek-chat"
                  }
                ]
              }
            }
            """);
        var dataDir = Path.Combine(root, "data");
        Directory.CreateDirectory(dataDir);
        return new ProviderServices(settingsPath, dataDir, http);
    }

    private sealed class ModelListHandler : HttpMessageHandler
    {
        public HttpMethod? Method { get; private set; }

        public Uri? RequestUri { get; private set; }

        public AuthenticationHeaderValue? Authorization { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Method = request.Method;
            RequestUri = request.RequestUri;
            Authorization = request.Headers.Authorization;

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"data":[{"id":"deepseek-chat"},{"id":"deepseek-reasoner"},{"id":"deepseek-chat"},{"name":"ignored"}]}"""),
            });
        }
    }
}
