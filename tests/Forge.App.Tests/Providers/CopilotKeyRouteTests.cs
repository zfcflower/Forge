using System.Net;
using Forge.Abstractions;
using Forge.App.Localization;
using Forge.App.Providers;
using Forge.App.Settings;
using Forge.Core.Providers;
using Xunit;

namespace Forge.App.Tests.Providers;

/// <summary>
/// GitHub Copilot's two ways in. A key (<c>COPILOT_GITHUB_TOKEN</c>, or one pasted into the editor) is a plain bearer token on
/// the preset's static API host: no device code, no session exchange and none of the integration headers the minted session
/// token is bound to. The device-code sign-in is unchanged, and it is what a provider without a key still asks for.
/// </summary>
public sealed class CopilotKeyRouteTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "forge-copilot-key-" + Guid.NewGuid().ToString("N"));

    public CopilotKeyRouteTests() => Directory.CreateDirectory(root);

    [Fact]
    public void The_preset_offers_a_key_beside_the_device_code_sign_in()
    {
        var preset = Copilot;
        var row = new BuiltInProviderRowViewModel(preset);

        Assert.Equal(ProviderAuthOptions.ApiKey | ProviderAuthOptions.GitHubCopilot, preset.Auth);
        Assert.Equal("COPILOT_GITHUB_TOKEN", preset.EnvironmentVariable);
        Assert.Equal(CopilotAuthentication.DefaultApiHost, preset.BaseUrl);
        Assert.True(preset.SupportsModelListing);
        // Both ways in arrive at one address — the sign-in only replaces it with the base its session names — so there is no
        // second endpoint to describe.
        Assert.Null(preset.Subscription);
        Assert.True(row.ShowsApiKey);
        Assert.True(row.ShowsLogin);
    }

    [Fact]
    public void The_editor_names_the_key_kind_and_keeps_the_static_base_url()
    {
        var services = Services(new RecordingHandler());
        var editor = new ProviderEditorViewModel(services, definition: null, Copilot) { Name = "Copilot" };

        Assert.True(editor.ShowsKey);
        var definition = editor.TryBuild();

        Assert.NotNull(definition);
        // The preset also signs in, but this dialog is the key way in and says so instead of taking the priority chain.
        Assert.Equal(ProviderCredentialKind.ApiKey, definition!.CredentialKind);
        Assert.Equal(ProviderProtocol.OpenAiCompatible, definition.Protocol);
        Assert.Equal(CopilotAuthentication.DefaultApiHost, definition.BaseUrl);
        Assert.Equal("provider:copilot", definition.SecretReference);
    }

    [Fact]
    public async Task A_copilot_key_is_listed_on_the_static_host_as_a_plain_bearer_and_runs_no_session()
    {
        var handler = new RecordingHandler().Answer(HttpStatusCode.OK, """{"data":[{"id":"gpt-5-codex"}]}""");
        var services = Services(handler);
        var definition = ProviderDefinition.FromPreset(
            Copilot,
            "copilot",
            credentialKind: ProviderCredentialKind.ApiKey) with { SecretReference = "provider:copilot" };
        services.Secrets.Set("provider:copilot", "ghp_key");

        var provider = services.CreateTransient(definition);
        Assert.Equal(ProviderStatusKind.Ready, provider.Status.Kind);

        var list = await services.RefreshModelsAsync(provider, CancellationToken.None);

        Assert.Equal(["gpt-5-codex"], list.Ids);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(new Uri("https://api.githubcopilot.com/models"), request.Uri);
        Assert.Equal("Bearer ghp_key", request.Header("Authorization"));
        // The key is the whole credential: no integration id or editor header (they only belong with a minted session token),
        // and nothing was exchanged or stored on the way.
        Assert.Null(request.Header("Copilot-Integration-Id"));
        Assert.Null(request.Header("Editor-Version"));
        Assert.Null(request.Header("Editor-Plugin-Version"));
        Assert.False(services.Secrets.Has(ProviderCredentials.CopilotUserTokenReference("copilot")));
    }

    [Fact]
    public void Without_a_key_the_preset_still_asks_for_the_device_code_sign_in()
    {
        var services = Services(new RecordingHandler());

        // The preset's own priority chain: a Copilot entry that named no key is the sign-in route.
        var signIn = ProviderDefinition.FromPreset(Copilot, "copilot");
        Assert.Equal(ProviderCredentialKind.GitHubCopilot, signIn.CredentialKind);
        Assert.Equal(ProviderStatusKind.NeedsSignIn, services.CreateTransient(signIn).Status.Kind);

        // The key route with nothing configured is a "needs key", never a "needs sign-in": it does not ask for a session it
        // would not use.
        var keyed = signIn with
        {
            CredentialKind = ProviderCredentialKind.ApiKey,
            SecretReference = "provider:copilot",
            EnvironmentVariable = null,
        };
        Assert.Equal(ProviderStatusKind.NeedsKey, services.CreateTransient(keyed).Status.Kind);
    }

    [Fact]
    public async Task A_copilot_provider_without_a_key_cannot_list_models_because_no_session_exists()
    {
        var services = Services(new RecordingHandler());

        var exception = await Assert.ThrowsAsync<ProviderException>(
            () => services.RefreshModelsAsync(
                services.CreateTransient(ProviderDefinition.FromPreset(Copilot, "copilot")),
                CancellationToken.None));

        Assert.Equal(ProviderStrings.CopilotSignInRequiredText().Resolve(), exception.Message);
    }

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

    private static ProviderPreset Copilot => ProviderPresets.Find(ProviderPresets.CopilotId)!;

    private ProviderServices Services(HttpMessageHandler handler) =>
        new(
            Path.Combine(root, "settings.json"),
            root,
            new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan });

    /// <summary>Answers in order and records what went out, headers included.</summary>
    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly List<(HttpStatusCode Status, string Body)> queued = [];

        public List<RecordedRequest> Requests { get; } = [];

        public RecordingHandler Answer(HttpStatusCode status, string body)
        {
            queued.Add((status, body));
            return this;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var (status, body) = queued.Count > 0 ? queued[0] : (HttpStatusCode.OK, "{}");
            if (queued.Count > 0)
            {
                queued.RemoveAt(0);
            }

            Requests.Add(new RecordedRequest(
                request.RequestUri!,
                request.Headers.ToDictionary(
                    static header => header.Key,
                    static header => string.Join(',', header.Value),
                    StringComparer.OrdinalIgnoreCase)));
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed record RecordedRequest(Uri Uri, Dictionary<string, string> Headers)
    {
        public string? Header(string name) => Headers.TryGetValue(name, out var value) ? value : null;
    }
}
