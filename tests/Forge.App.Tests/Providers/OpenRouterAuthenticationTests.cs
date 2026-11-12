using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Forge.Abstractions;
using Forge.App.Providers;
using Forge.App.Settings;
using Forge.Core.Providers;
using Xunit;

namespace Forge.App.Tests.Providers;

/// <summary>
/// OpenRouter's PKCE sign-in against a scripted HTTP handler: the authorize page it opens, the verifier/challenge pair, and the
/// exchange that turns the returned code into an ordinary API key.
/// </summary>
public sealed class OpenRouterAuthenticationTests
{
    private const string KeyBody = """{"key":"sk-or-v1-test-key"}""";

    [Fact]
    public void The_authorize_url_carries_the_callback_the_challenge_and_S256()
    {
        var pkce = OpenRouterAuthentication.CreatePkce();
        var callback = OpenRouterAuthentication.CallbackUrl(45678);

        var authorize = OpenRouterAuthentication.AuthorizeUri(callback, pkce.Challenge);
        var query = Query(authorize);

        Assert.Equal("https", authorize.Scheme);
        Assert.Equal("openrouter.ai", authorize.Host);
        Assert.Equal("/auth", authorize.AbsolutePath);
        // The page has to come back to the listener this process just started, and S256 is the only method the app offers.
        Assert.Equal("http://127.0.0.1:45678/oauth/callback", query["callback_url"]);
        Assert.Equal(pkce.Challenge, query["code_challenge"]);
        Assert.Equal("S256", query["code_challenge_method"]);
    }

    [Fact]
    public void The_verifier_and_the_challenge_are_valid_RFC_7636_base64url()
    {
        var pkce = OpenRouterAuthentication.CreatePkce();

        // RFC 7636 §4.1: 43 to 128 characters from the unreserved set, so a base64url value with no padding.
        Assert.InRange(pkce.Verifier.Length, 43, 128);
        foreach (var character in pkce.Verifier)
        {
            Assert.True(
                char.IsAsciiLetterOrDigit(character) || character is '-' or '.' or '_' or '~',
                $"the verifier holds '{character}', which RFC 7636 does not allow");
        }

        Assert.DoesNotContain("=", pkce.Verifier, StringComparison.Ordinal);
        Assert.DoesNotContain("+", pkce.Verifier, StringComparison.Ordinal);
        Assert.DoesNotContain("/", pkce.Verifier, StringComparison.Ordinal);
        // 32 random bytes: the length the reference client uses, and it decodes back to exactly that.
        Assert.Equal(32, Base64UrlDecode(pkce.Verifier).Length);

        // The challenge is BASE64URL(SHA256(ASCII(verifier))), spelled without padding and without + or /.
        var expected = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(pkce.Verifier)))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        Assert.Equal(expected, pkce.Challenge);
        Assert.Equal(32, Base64UrlDecode(pkce.Challenge).Length);
        Assert.Equal(expected, OpenRouterAuthentication.ChallengeOf(pkce.Verifier));

        // A second sign-in never reuses the first one's pair, and nothing prints the secret half.
        Assert.NotEqual(pkce.Verifier, OpenRouterAuthentication.CreatePkce().Verifier);
        Assert.DoesNotContain(pkce.Verifier, pkce.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_exchange_posts_the_code_and_the_verifier_and_returns_the_key()
    {
        var handler = ScriptedOpenRouterHandler.Answering(HttpStatusCode.OK, KeyBody);

        var key = await Client(handler).ExchangeAsync("the-code", "the-verifier", CancellationToken.None);

        Assert.Equal("sk-or-v1-test-key", key);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://openrouter.ai/api/v1/auth/keys", request.Uri!.AbsoluteUri);
        Assert.Equal(["application/json"], request.Accept);
        Assert.Equal("application/json", request.ContentType);
        using var body = JsonDocument.Parse(request.Body);
        Assert.Equal("the-code", body.RootElement.GetProperty("code").GetString());
        Assert.Equal("the-verifier", body.RootElement.GetProperty("code_verifier").GetString());
        Assert.Equal("S256", body.RootElement.GetProperty("code_challenge_method").GetString());
    }

    [Fact]
    public async Task A_failed_exchange_reports_the_status_and_the_bodys_own_detail()
    {
        var described = Client(ScriptedOpenRouterHandler.Answering(
            HttpStatusCode.BadRequest,
            """{"error":"invalid_grant","error_description":"Code verifier does not match the challenge."}"""));
        var messaged = Client(ScriptedOpenRouterHandler.Answering(
            HttpStatusCode.UnprocessableEntity,
            """{"message":"The authorization code has expired."}"""));
        var coded = Client(ScriptedOpenRouterHandler.Answering(
            HttpStatusCode.Unauthorized,
            """{"error":"invalid_api_key"}"""));

        var describedError = await Assert.ThrowsAsync<ProviderException>(
            () => described.ExchangeAsync("code", "verifier", CancellationToken.None));
        var messagedError = await Assert.ThrowsAsync<ProviderException>(
            () => messaged.ExchangeAsync("code", "verifier", CancellationToken.None));
        var codedError = await Assert.ThrowsAsync<ProviderException>(
            () => coded.ExchangeAsync("code", "verifier", CancellationToken.None));

        Assert.Equal(400, describedError.StatusCode);
        Assert.Contains("400", describedError.Message, StringComparison.Ordinal);
        Assert.Contains("Code verifier does not match the challenge.", describedError.Message, StringComparison.Ordinal);
        // A body that names no description still explains itself through message, and then through the error code alone.
        Assert.Contains("The authorization code has expired.", messagedError.Message, StringComparison.Ordinal);
        Assert.Equal(422, messagedError.StatusCode);
        Assert.Contains("invalid_api_key", codedError.Message, StringComparison.Ordinal);
        Assert.Equal(401, codedError.StatusCode);
        Assert.False(describedError.Retryable);
        Assert.False(codedError.Retryable);
    }

    [Fact]
    public async Task A_success_without_a_key_is_an_invalid_response()
    {
        var missing = Client(ScriptedOpenRouterHandler.Answering(HttpStatusCode.OK, """{"user":"someone"}"""));
        var blank = Client(ScriptedOpenRouterHandler.Answering(HttpStatusCode.OK, """{"key":""}"""));

        var missingError = await Assert.ThrowsAsync<ProviderException>(
            () => missing.ExchangeAsync("code", "verifier", CancellationToken.None));
        var blankError = await Assert.ThrowsAsync<ProviderException>(
            () => blank.ExchangeAsync("code", "verifier", CancellationToken.None));

        Assert.Contains("no key", missingError.Message, StringComparison.Ordinal);
        Assert.Contains("no key", blankError.Message, StringComparison.Ordinal);
        Assert.False(missingError.Retryable);
        Assert.False(blankError.Retryable);
    }

    [Fact]
    public async Task A_body_that_is_the_object_form_of_an_error_is_still_reported()
    {
        var client = Client(ScriptedOpenRouterHandler.Answering(
            HttpStatusCode.Forbidden,
            """{"error":{"message":"This key was created by a different account."}}"""));

        var exception = await Assert.ThrowsAsync<ProviderException>(
            () => client.ExchangeAsync("code", "verifier", CancellationToken.None));

        Assert.Equal(403, exception.StatusCode);
        Assert.Contains("created by a different account", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>A handler that answers everything the same way and records what was asked.</summary>
    private static OpenRouterAuthentication Client(HttpMessageHandler handler) =>
        new(new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan });

    /// <summary>The query of one URL as name/value pairs, unescaped, which is how the browser reads it too.</summary>
    internal static Dictionary<string, string> Query(Uri url)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in url.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var split = pair.IndexOf('=', StringComparison.Ordinal);
            values[Uri.UnescapeDataString(pair[..split])] = Uri.UnescapeDataString(pair[(split + 1)..]);
        }

        return values;
    }

    private static byte[] Base64UrlDecode(string value)
    {
        var base64 = value.Replace('-', '+').Replace('_', '/');
        var padding = (4 - (base64.Length % 4)) % 4;
        return Convert.FromBase64String(base64.PadRight(base64.Length + padding, '='));
    }
}

/// <summary>
/// The credential side of the OpenRouter sign-in: the row that offers it, the one flow that binds a port, where the key ends up,
/// and the rule that the definition stays an ordinary API-key provider.
/// </summary>
public sealed class OpenRouterAuthenticationCredentialTests : IDisposable
{
    private const string KeyBody = """{"key":"sk-or-v1-signed-in"}""";

    private readonly string root = Path.Combine(Path.GetTempPath(), "forge-openrouter-login-" + Guid.NewGuid().ToString("N"));

    public OpenRouterAuthenticationCredentialTests() => Directory.CreateDirectory(root);

    [Fact]
    public void The_openrouter_preset_offers_a_key_and_a_sign_in_and_its_row_shows_both_buttons()
    {
        var preset = ProviderPresets.Find(ProviderPresets.OpenRouterId)!;
        var row = new BuiltInProviderRowViewModel(preset);

        Assert.True(preset.Auth.HasFlag(ProviderAuthOptions.ApiKey));
        Assert.True(preset.Auth.HasFlag(ProviderAuthOptions.OpenRouterOAuth));
        Assert.True(row.ShowsLogin);
        Assert.True(row.ShowsApiKey);

        // The sign-in returns a key, so a fresh OpenRouter provider is an ordinary API-key provider at the key endpoint.
        var definition = ProviderDefinition.FromPreset(preset, "openrouter");
        Assert.Equal(ProviderCredentialKind.ApiKey, definition.CredentialKind);
        Assert.Equal("https://openrouter.ai/api/v1", definition.BaseUrl);
        Assert.Equal(ProviderProtocol.OpenAiCompatible, definition.Protocol);
    }

    [Fact]
    public void A_stored_openrouter_provider_keeps_the_api_key_credential_kind()
    {
        var definition = ProviderDefinition.FromPreset(ProviderPresets.Find(ProviderPresets.OpenRouterId)!, "openrouter")
            with { SecretReference = ProviderCredentials.ApiKeyReference("openrouter") };

        var stored = definition.ToDocument();

        Assert.Equal("api-key", stored.Auth!.Kind);
        var restored = ProviderDefinition.FromDocument(stored, "fallback");
        Assert.Equal(ProviderCredentialKind.ApiKey, restored.CredentialKind);
        // The reference has to survive the document, or the key a sign-in stored would be lost on the next restart.
        Assert.Equal("provider:openrouter", restored.SecretReference);
        Assert.Equal("provider:openrouter", ProviderCredentials.ApiKeyReference("openrouter"));
    }

    [Fact]
    public async Task A_finished_sign_in_stores_the_key_where_a_pasted_one_goes_and_never_in_the_settings()
    {
        var handler = ScriptedOpenRouterHandler.Answering(HttpStatusCode.OK, KeyBody);
        var services = Services(handler);
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        string? opened = null;

        // The dialog's side of the flow: the page is handed over once the listener is up, and the user approves it there.
        var signIn = services.Credentials.SignInOpenRouterAsync("openrouter", page => opened = page.AbsoluteUri, guard.Token);
        Assert.NotNull(opened);
        var callback = new Uri(OpenRouterAuthenticationTests.Query(new Uri(opened!))["callback_url"]);
        // Loopback only: the code is delivered to this process and never leaves the machine.
        Assert.Equal("127.0.0.1", callback.Host);
        Assert.Equal("http", callback.Scheme);
        Assert.Equal("/oauth/callback", callback.AbsolutePath);

        // What the browser does: it comes back to the loopback URL with the authorization code.
        using (var browser = new HttpClient { Timeout = TimeSpan.FromSeconds(30) })
        {
            var page = await browser.GetStringAsync(new Uri(callback + "?code=openrouter-code"), guard.Token);
            Assert.Contains("Signed in", page, StringComparison.Ordinal);
        }

        var key = await signIn.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal("sk-or-v1-signed-in", key);
        // The exchange carried the code the browser returned and a verifier that matches the challenge in the authorize URL.
        var exchange = Assert.Single(handler.Requests);
        using var body = JsonDocument.Parse(exchange.Body);
        Assert.Equal("openrouter-code", body.RootElement.GetProperty("code").GetString());
        var verifier = body.RootElement.GetProperty("code_verifier").GetString();
        Assert.Equal(
            OpenRouterAuthenticationTests.Query(new Uri(opened!))["code_challenge"],
            OpenRouterAuthentication.ChallengeOf(verifier!));

        // The key lives under the reference a pasted key uses, and nowhere else.
        var reference = ProviderCredentials.ApiKeyReference("openrouter");
        Assert.Equal("provider:openrouter", reference);
        Assert.Equal(key, services.Secrets.Get(reference));
        using (var store = JsonDocument.Parse(await File.ReadAllTextAsync(services.Secrets.StoragePath)))
        {
            Assert.Equal(
                new[] { reference },
                store.RootElement.GetProperty("entries").EnumerateObject().Select(static entry => entry.Name));
        }

        // The definition the login dialog builds: ApiKey, with that reference and no token bundle of its own.
        var definition = ProviderDefinition.FromPreset(ProviderPresets.Find(ProviderPresets.OpenRouterId)!, "openrouter")
            with { SecretReference = reference };
        await services.SaveAsync([definition], "openrouter", defaultContextWindow: null);

        var settings = await File.ReadAllTextAsync(Path.Combine(root, "settings.json"));
        Assert.Contains(ProviderPresets.OpenRouterId, settings, StringComparison.Ordinal);
        Assert.Contains(reference, settings, StringComparison.Ordinal);
        Assert.DoesNotContain(key, settings, StringComparison.Ordinal);

        using var document = JsonDocument.Parse(settings);
        var auth = document.RootElement.GetProperty("providers").GetProperty("items")[0].GetProperty("auth");
        Assert.Equal("api-key", auth.GetProperty("kind").GetString());
        Assert.Equal(reference, auth.GetProperty("secretRef").GetString());

        // From here on it is the plain key path: the row reads as ready because the store holds the key just signed in for.
        Assert.Equal(ProviderStatusKind.Ready, services.CreateTransient(definition).Status.Kind);
    }

    [Fact]
    public async Task A_cancelled_sign_in_releases_the_listener_and_stores_nothing()
    {
        var services = Services(ScriptedOpenRouterHandler.Answering(HttpStatusCode.OK, KeyBody));
        using var cancellation = new CancellationTokenSource();
        var signIn = services.Credentials.SignInOpenRouterAsync("openrouter", _ => { }, cancellation.Token);

        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => signIn.WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.Null(services.Secrets.Get(ProviderCredentials.ApiKeyReference("openrouter")));
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

    /// <summary>
    /// No injected clock: the sign-in's own five-minute limit runs on real time here, and the test's own guard is shorter, so a
    /// stalled callback fails the test instead of waiting it out.
    /// </summary>
    private ProviderServices Services(HttpMessageHandler handler) =>
        new(Path.Combine(root, "settings.json"), root, new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan });
}

/// <summary>Answers one HTTP request the way OpenRouter would, and records what was asked.</summary>
internal sealed class ScriptedOpenRouterHandler : HttpMessageHandler
{
    private readonly HttpStatusCode status;
    private readonly string body;

    private ScriptedOpenRouterHandler(HttpStatusCode status, string body)
    {
        this.status = status;
        this.body = body;
    }

    public List<RecordedOpenRouterRequest> Requests { get; } = [];

    public static ScriptedOpenRouterHandler Answering(HttpStatusCode status, string body) => new(status, body);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var content = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add(new RecordedOpenRouterRequest(
            request.Method,
            request.RequestUri,
            content,
            request.Content?.Headers.ContentType?.MediaType,
            [.. request.Headers.Accept.Select(static accept => accept.MediaType ?? string.Empty)]));
        return new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
    }
}

/// <summary>One request the flow made, captured so a test can assert the wire shape.</summary>
internal sealed record RecordedOpenRouterRequest(
    HttpMethod Method,
    Uri? Uri,
    string Body,
    string? ContentType,
    IReadOnlyList<string> Accept);
