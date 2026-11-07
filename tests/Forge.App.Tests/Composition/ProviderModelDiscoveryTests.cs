using System.Net;
using System.Net.Http.Headers;
using Forge.App.Composition;
using Forge.App.Services;
using Forge.App.ViewModels;
using Xunit;

namespace Forge.App.Tests.Composition;

public sealed class ProviderModelDiscoveryTests
{
    [Fact]
    public async Task Composer_refresh_fetches_and_exposes_models_using_the_configured_environment_key()
    {
        const string variable = "DEEPSEEK_API_KEY";
        const string testKey = "test-key-not-a-credential";
        var originalKey = Environment.GetEnvironmentVariable(variable);
        Environment.SetEnvironmentVariable(variable, testKey);

        try
        {
            var snapshot = ProviderSnapshot.FromSection(new ProviderSection
            {
                Kind = "openai-compatible",
                BaseUrl = "https://api.deepseek.com/",
                Model = "deepseek-chat",
                ApiKeyEnv = variable,
            });
            var handler = new ModelListHandler();
            using var httpClient = new HttpClient(handler);
            var composer = new ComposerViewModel(
                new ProviderSettings(snapshot, "ready"),
                _ => { },
                new ComposerPreferences(),
                string.Empty,
                skillSource: static () => [],
                modelSource: cancellationToken => snapshot.FetchModelsAsync(httpClient, cancellationToken));

            await composer.RefreshAsync(CancellationToken.None);

            Assert.Equal(HttpMethod.Get, handler.Method);
            Assert.Equal(new Uri("https://api.deepseek.com/models"), handler.RequestUri);
            Assert.Equal(new AuthenticationHeaderValue("Bearer", testKey), handler.Authorization);
            Assert.Equal(["deepseek-chat", "deepseek-reasoner"], composer.Models.Select(static model => model.Name));
            Assert.DoesNotContain(testKey, snapshot.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain(testKey, snapshot.Summary, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, originalKey);
        }
    }

    [Fact]
    public async Task Model_discovery_fails_before_network_when_the_environment_key_is_missing()
    {
        const string variable = "DEEPSEEK_API_KEY";
        var originalKey = Environment.GetEnvironmentVariable(variable);
        Environment.SetEnvironmentVariable(variable, null);

        try
        {
            var snapshot = ProviderSnapshot.FromSection(new ProviderSection
            {
                Kind = "openai-compatible",
                BaseUrl = "https://api.deepseek.com",
                Model = "deepseek-chat",
                ApiKeyEnv = variable,
            });
            var handler = new ModelListHandler();
            using var httpClient = new HttpClient(handler);

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => snapshot.FetchModelsAsync(httpClient, CancellationToken.None));

            Assert.Contains(variable, exception.Message, StringComparison.Ordinal);
            Assert.Null(handler.RequestUri);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, originalKey);
        }
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
