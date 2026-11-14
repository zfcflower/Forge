using System.Net;
using System.Text;
using System.Text.Json;
using Forge.Abstractions;
using Forge.App.Localization;
using Forge.App.Providers;
using Forge.App.Settings;
using Forge.Core.Providers;
using Xunit;

namespace Forge.App.Tests.Providers;

/// <summary>
/// Meta's subscription sign-in against a scripted HTTP handler: what the device authorization has to carry, how a poll
/// separates "wait" from "approved" from "give up", and how the identity token is minted into the API key the requests use.
/// </summary>
public sealed class MetaAuthenticationTests
{
    private const string DeviceCodeBody = """
        {"device_code":"meta-device-code","user_code":"ABCD-1234",
         "verification_uri":"https://auth.meta.com/device","verification_uri_complete":"https://auth.meta.com/device?c=ABCD-1234",
         "interval":5,"expires_in":900}
        """;

    private const string IdentityBody = """
        {"access_token":"meta-identity-token","token_type":"Bearer","expires_in":3600}
        """;

    private const string MintBody = """{"api_key":"meta-api-key"}""";

    [Fact]
    public async Task The_start_request_is_form_encoded_with_the_client_and_reads_the_complete_url()
    {
        var handler = ScriptedDeviceCodeHandler.Answering(HttpStatusCode.OK, DeviceCodeBody);
        var client = Client(handler);

        var code = await client.StartAsync(CancellationToken.None);

        Assert.Equal("meta-device-code", code.DeviceCode);
        Assert.Equal("ABCD-1234", code.UserCode);
        Assert.Equal("https://auth.meta.com/device", code.VerificationUri);
        Assert.Equal("https://auth.meta.com/device?c=ABCD-1234", code.VerificationUriComplete);
        Assert.Equal(5, code.IntervalSeconds);
        Assert.Equal(TimeSpan.FromMinutes(15), code.ExpiresAt - Epoch);
        // The device code is a bearer credential for the poll call, so it must not reach a log line through ToString.
        Assert.DoesNotContain("meta-device-code", code.ToString(), StringComparison.Ordinal);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://auth.meta.com/oidc/device/authorization/", request.Uri!.AbsoluteUri);
        Assert.Equal(["application/json"], request.Accept);
        Assert.Equal("client_id=1031625952748946", request.Body);
    }

    [Fact]
    public async Task The_plain_url_stands_in_when_no_complete_url_comes_back()
    {
        var client = Client(ScriptedDeviceCodeHandler.Answering(
            HttpStatusCode.OK,
            """{"device_code":"d","user_code":"u","verification_uri":"https://auth.meta.com/device"}"""));

        var code = await client.StartAsync(CancellationToken.None);

        Assert.Equal("https://auth.meta.com/device", code.VerificationUriComplete);
        Assert.Equal(MetaAuthentication.MinimumIntervalSeconds, code.IntervalSeconds);
        // No lifetime in the answer still has to leave a deadline for the poll loop.
        Assert.True(code.ExpiresAt > Epoch);
    }

    [Fact]
    public async Task A_response_without_the_two_codes_is_unusable()
    {
        var client = Client(ScriptedDeviceCodeHandler.Answering(HttpStatusCode.OK, """{"user_code":"u"}"""));

        var exception = await Assert.ThrowsAsync<ProviderException>(() => client.StartAsync(CancellationToken.None));

        Assert.Contains("device_code", exception.Message, StringComparison.Ordinal);
        Assert.False(exception.Retryable);
    }

    [Fact]
    public async Task A_failed_start_reports_the_status_and_the_body()
    {
        var client = Client(ScriptedDeviceCodeHandler.Answering(HttpStatusCode.Forbidden, """{"error":"invalid_client"}"""));

        var exception = await Assert.ThrowsAsync<ProviderException>(() => client.StartAsync(CancellationToken.None));

        Assert.Equal(403, exception.StatusCode);
        Assert.Contains("403", exception.Message, StringComparison.Ordinal);
        Assert.Contains("invalid_client", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_pending_poll_keeps_the_interval_and_a_later_poll_returns_the_identity_token()
    {
        var handler = new ScriptedDeviceCodeHandler()
            .Then(HttpStatusCode.OK, DeviceCodeBody)
            .Then(HttpStatusCode.OK, """{"error":"authorization_pending"}""")
            .Then(HttpStatusCode.OK, IdentityBody);
        var client = Client(handler);
        var code = await client.StartAsync(CancellationToken.None);

        var pending = await client.PollAsync(code, code.IntervalSeconds, CancellationToken.None);
        var complete = await client.PollAsync(code, pending.IntervalSeconds, CancellationToken.None);

        Assert.True(pending.IsPending);
        Assert.Null(pending.IdentityToken);
        Assert.Equal(5, pending.IntervalSeconds);
        Assert.False(complete.IsPending);
        Assert.Equal("meta-identity-token", complete.IdentityToken);
        // The identity token is a credential: neither answer's ToString may print it.
        Assert.DoesNotContain("meta-identity-token", complete.ToString(), StringComparison.Ordinal);

        var poll = handler.Requests[2];
        Assert.Equal("https://auth.meta.com/oidc/device/token/", poll.Uri!.AbsoluteUri);
        Assert.Equal(["application/json"], poll.Accept);
        // The exact body pins the field order the wire format carries; the shared skeleton must not reshuffle it.
        Assert.Equal(
            "grant_type=urn%3Aietf%3Aparams%3Aoauth%3Agrant-type%3Adevice_code&device_code=meta-device-code&client_id=1031625952748946",
            poll.Body);
    }

    [Fact]
    public async Task Polling_too_fast_raises_the_interval()
    {
        var handler = new ScriptedDeviceCodeHandler()
            .Then(HttpStatusCode.OK, DeviceCodeBody)
            .Then(HttpStatusCode.OK, """{"error":"slow_down"}""")
            .Then(HttpStatusCode.OK, """{"error":"slow_down","interval":12}""")
            .Then(HttpStatusCode.OK, """{"error":"slow_down","interval":2}""");
        var client = Client(handler);
        var code = await client.StartAsync(CancellationToken.None);

        var unnamed = await client.PollAsync(code, 5, CancellationToken.None);
        var named = await client.PollAsync(code, unnamed.IntervalSeconds, CancellationToken.None);
        var tooSmall = await client.PollAsync(code, named.IntervalSeconds, CancellationToken.None);

        // No interval in the answer: five seconds on top of what was already waited.
        Assert.Equal(10, unnamed.IntervalSeconds);
        Assert.Equal(12, named.IntervalSeconds);
        // A named interval below the floor never speeds the poll up.
        Assert.Equal(MetaAuthentication.MinimumIntervalSeconds, tooSmall.IntervalSeconds);
        Assert.True(tooSmall.IsPending);
    }

    [Fact]
    public async Task An_expired_code_and_a_denied_login_end_the_flow_with_their_own_reason()
    {
        var expired = Client(
            new ScriptedDeviceCodeHandler().Then(HttpStatusCode.OK, DeviceCodeBody).Then(HttpStatusCode.OK, """{"error":"expired_token"}"""));
        var denied = Client(
            new ScriptedDeviceCodeHandler().Then(HttpStatusCode.OK, DeviceCodeBody).Then(HttpStatusCode.OK, """{"error":"access_denied"}"""));

        var expiredCode = await expired.StartAsync(CancellationToken.None);
        var deniedCode = await denied.StartAsync(CancellationToken.None);
        var expiredError = await Assert.ThrowsAsync<ProviderException>(
            () => expired.PollAsync(expiredCode, 5, CancellationToken.None));
        var deniedError = await Assert.ThrowsAsync<ProviderException>(
            () => denied.PollAsync(deniedCode, 5, CancellationToken.None));

        Assert.Contains("expired", expiredError.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("restart", expiredError.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("denied", deniedError.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(expiredError.Retryable);
        Assert.False(deniedError.Retryable);
    }

    [Fact]
    public async Task An_unrecognised_error_reports_whatever_field_carried_the_detail()
    {
        var asDescription = Client(new ScriptedDeviceCodeHandler()
            .Then(HttpStatusCode.OK, DeviceCodeBody)
            .Then(HttpStatusCode.BadRequest, """{"error":"invalid_request","error_description":"device_code is malformed"}"""));
        var asDetail = Client(new ScriptedDeviceCodeHandler()
            .Then(HttpStatusCode.OK, DeviceCodeBody)
            .Then(HttpStatusCode.BadRequest, """{"error":"invalid_request","detail":"device_code is malformed"}"""));
        var asMessage = Client(new ScriptedDeviceCodeHandler()
            .Then(HttpStatusCode.OK, DeviceCodeBody)
            .Then(HttpStatusCode.BadRequest, """{"message":"device_code is malformed"}"""));
        var asCode = Client(new ScriptedDeviceCodeHandler()
            .Then(HttpStatusCode.OK, DeviceCodeBody)
            .Then(HttpStatusCode.BadRequest, """{"error":"device_code is malformed"}"""));

        foreach (var client in new[] { asDescription, asDetail, asMessage, asCode })
        {
            var code = await client.StartAsync(CancellationToken.None);
            var exception = await Assert.ThrowsAsync<ProviderException>(
                () => client.PollAsync(code, 5, CancellationToken.None));
            Assert.Contains("device_code is malformed", exception.Message, StringComparison.Ordinal);
            Assert.False(exception.Retryable);
        }
    }

    [Fact]
    public async Task A_server_fault_fails_the_poll_instead_of_looking_pending()
    {
        var client = Client(new ScriptedDeviceCodeHandler()
            .Then(HttpStatusCode.OK, DeviceCodeBody)
            .Then(HttpStatusCode.InternalServerError, "upstream is down"));

        var code = await client.StartAsync(CancellationToken.None);
        var exception = await Assert.ThrowsAsync<ProviderException>(
            () => client.PollAsync(code, 5, CancellationToken.None));

        Assert.Equal(500, exception.StatusCode);
        Assert.True(exception.Retryable);
    }

    [Fact]
    public async Task The_mint_carries_the_identity_token_and_the_api_version_and_returns_the_key()
    {
        var handler = ScriptedDeviceCodeHandler.Answering(HttpStatusCode.OK, MintBody);

        var bundle = await Client(handler).MintAsync("meta-identity-token", CancellationToken.None);

        Assert.Equal("meta-api-key", bundle.ApiKey);
        Assert.Equal("meta-identity-token", bundle.IdentityToken);
        Assert.Equal(Epoch + MetaAuthentication.KeyLifetime, bundle.ExpiresAt);
        Assert.DoesNotContain("meta-api-key", bundle.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("meta-identity-token", bundle.ToString(), StringComparison.Ordinal);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://api.meta.ai/muse-code/key", request.Uri!.AbsoluteUri);
        Assert.Equal("{}", request.Body);
        Assert.Equal("Bearer meta-identity-token", request.Header("Authorization"));
        Assert.Equal("1.0.0", request.Header("x-api-version"));
        Assert.Equal("application/json", request.ContentType);
    }

    [Fact]
    public async Task A_refused_mint_means_the_session_expired()
    {
        var client = Client(ScriptedDeviceCodeHandler.Answering(HttpStatusCode.Unauthorized, """{"error":"invalid_grant"}"""));

        var exception = await Assert.ThrowsAsync<ProviderException>(
            () => client.MintAsync("stale-identity-token", CancellationToken.None));

        Assert.Equal(401, exception.StatusCode);
        Assert.False(exception.Retryable);
        Assert.Contains("sign in again", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("stale-identity-token", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_mint_without_a_key_says_where_to_finish_the_setup()
    {
        var withAction = Client(ScriptedDeviceCodeHandler.Answering(
            HttpStatusCode.OK,
            """{"action_url":"https://api.meta.ai/muse-code/setup?account=7"}"""));
        var without = Client(ScriptedDeviceCodeHandler.Answering(HttpStatusCode.OK, """{}"""));

        var actionable = await Assert.ThrowsAsync<ProviderException>(
            () => withAction.MintAsync("meta-identity-token", CancellationToken.None));
        var bare = await Assert.ThrowsAsync<ProviderException>(
            () => without.MintAsync("meta-identity-token", CancellationToken.None));

        // The setup page is the only useful thing to say: without it the failure reads like a broken server.
        Assert.Contains("https://api.meta.ai/muse-code/setup?account=7", actionable.Message, StringComparison.Ordinal);
        Assert.False(actionable.Retryable);
        Assert.Contains("api_key", bare.Message, StringComparison.Ordinal);
        Assert.False(bare.Retryable);
    }

    /// <summary>The clock every client in this class runs on, so a lifetime assertion is arithmetic and not a wait.</summary>
    private static readonly DateTimeOffset Epoch = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

    private static MetaAuthentication Client(HttpMessageHandler handler) =>
        new(new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan }, new InstantTime(Epoch));
}

/// <summary>
/// The credential side of the Meta login: what a row reads as before and after a sign-in, where the bundle lives, the
/// five-minute skew, the re-mint that keeps a session alive, and the rule that a refused re-mint only drops the bundle whose
/// identity token was actually refused.
/// </summary>
public sealed class MetaAuthenticationCredentialTests : IDisposable
{
    private const string DeviceCodeBody = """
        {"device_code":"meta-device-code","user_code":"ABCD-1234",
         "verification_uri":"https://auth.meta.com/device","verification_uri_complete":"https://auth.meta.com/device?c=ABCD-1234",
         "interval":5,"expires_in":900}
        """;

    private const string IdentityBody = """
        {"access_token":"meta-identity-token","token_type":"Bearer","expires_in":3600}
        """;

    private const string MintBody = """{"api_key":"meta-api-key"}""";

    private readonly string root = TestTempFiles.NewDirectory("forge-meta-login-");


    [Fact]
    public void The_meta_preset_offers_a_key_and_a_sign_in_and_its_row_shows_the_button()
    {
        var preset = ProviderPresets.Find(ProviderPresets.MetaId)!;
        var row = new BuiltInProviderRowViewModel(preset);

        Assert.True(preset.Auth.HasFlag(ProviderAuthOptions.ApiKey));
        Assert.True(preset.Auth.HasFlag(ProviderAuthOptions.MetaSubscription));
        Assert.True(row.ShowsLogin);
        Assert.True(row.ShowsApiKey);
        Assert.Equal(ProviderCredentialKind.MetaSubscription, ProviderDefinition.FromPreset(preset, "meta").CredentialKind);
    }

    [Fact]
    public void A_stored_meta_provider_keeps_its_credential_kind_across_the_document()
    {
        var stored = Meta().ToDocument();

        Assert.Equal("meta-subscription", stored.Auth!.Kind);
        Assert.Equal(ProviderCredentialKind.MetaSubscription, ProviderDefinition.FromDocument(stored, "fallback").CredentialKind);

        // An api-key entry for the same preset still reads as a key: the stored name comes before the preset's capabilities.
        stored.Auth.Kind = "api-key";
        Assert.Equal(ProviderCredentialKind.ApiKey, ProviderDefinition.FromDocument(stored, "fallback").CredentialKind);

        // A file that predates the kind field falls back to the preset, exactly as the other sign-ins do.
        stored.Auth.Kind = null;
        Assert.Equal(ProviderCredentialKind.MetaSubscription, ProviderDefinition.FromDocument(stored, "fallback").CredentialKind);
    }

    [Fact]
    public void A_meta_row_without_a_bundle_needs_a_sign_in()
    {
        var provider = Services(new ScriptedDeviceCodeHandler()).CreateTransient(Meta());

        Assert.Equal(ProviderStatusKind.NeedsSignIn, provider.Status.Kind);
        Assert.Equal(ProviderStrings.MetaSignInRequiredText().Resolve(), provider.Status.Detail.Resolve());
    }

    [Fact]
    public async Task A_missing_bundle_throws_the_sign_in_reason()
    {
        var services = Services(new ScriptedDeviceCodeHandler());

        var exception = await Assert.ThrowsAsync<ProviderException>(
            () => services.Credentials.GetMetaApiKeyAsync("meta", CancellationToken.None));

        Assert.False(exception.Retryable);
        Assert.Equal(ProviderStrings.MetaSignInRequiredText().Resolve(), exception.Message);
    }

    [Fact]
    public async Task A_successful_sign_in_shows_the_code_then_enables_access_and_persists_the_bundle()
    {
        var handler = new ScriptedDeviceCodeHandler()
            .Then(HttpStatusCode.OK, DeviceCodeBody)
            .Then(HttpStatusCode.OK, """{"error":"authorization_pending"}""")
            .Then(HttpStatusCode.OK, IdentityBody)
            .Then(HttpStatusCode.OK, MintBody);
        var services = Services(handler);
        MetaDeviceCode? shown = null;
        var enabling = 0;

        var bundle = await services.Credentials.SignInMetaAsync(
            "meta",
            code => shown = code,
            () => enabling++,
            CancellationToken.None);

        Assert.Equal("ABCD-1234", shown?.UserCode);
        Assert.Equal("https://auth.meta.com/device?c=ABCD-1234", shown?.VerificationUriComplete);
        // The mint is a second round trip the dialog has to narrate, so the callback fires exactly once.
        Assert.Equal(1, enabling);
        Assert.Equal("meta-api-key", bundle.ApiKey);
        Assert.True(services.Credentials.HasMetaLogin("meta"));
        Assert.Equal(ProviderStatusKind.Ready, services.CreateTransient(Meta()).Status.Kind);
        Assert.Equal(ProviderStrings.MetaSignedInText().Resolve(), services.CreateTransient(Meta()).Status.Detail.Resolve());
        Assert.Equal("meta-api-key", await services.Credentials.GetMetaApiKeyAsync("meta", CancellationToken.None));

        // The mint request carried the identity token the poll returned, not the API key.
        Assert.Equal("Bearer meta-identity-token", handler.Requests[3].Header("Authorization"));

        var reference = ProviderCredentials.MetaTokenReference("meta");
        Assert.Equal("meta:meta", reference);
        var stored = services.Secrets.Get(reference);
        Assert.NotNull(stored);
        Assert.Contains("meta-api-key", stored!, StringComparison.Ordinal);
        Assert.Contains("meta-identity-token", stored, StringComparison.Ordinal);

        // The bundle is the only secret, and it lives under "meta:<id>" in the encrypted store.
        using var store = JsonDocument.Parse(await File.ReadAllTextAsync(services.Secrets.StoragePath));
        Assert.Equal(
            new[] { reference },
            store.RootElement.GetProperty("entries").EnumerateObject().Select(static entry => entry.Name));

        // The definition reaches settings.json the way the login dialog builds it: the meta-subscription credential kind and
        // no secret reference, so the key and the identity token never appear in the settings file.
        await services.SaveAsync([Meta()], "meta", defaultContextWindow: null);
        var settings = await File.ReadAllTextAsync(Path.Combine(root, "settings.json"));
        Assert.Contains(ProviderPresets.MetaId, settings, StringComparison.Ordinal);
        Assert.DoesNotContain("meta-api-key", settings, StringComparison.Ordinal);
        Assert.DoesNotContain("meta-identity-token", settings, StringComparison.Ordinal);
        Assert.DoesNotContain(reference, settings, StringComparison.Ordinal);

        using var document = JsonDocument.Parse(settings);
        var auth = document.RootElement
            .GetProperty("providers")
            .GetProperty("items")[0]
            .GetProperty("auth");
        Assert.Equal("meta-subscription", auth.GetProperty("kind").GetString());
        Assert.True(
            !auth.TryGetProperty("secretRef", out var secretReference) || secretReference.ValueKind is JsonValueKind.Null,
            "a sign-in provider carries no secret reference: the bundle lives in the encrypted store");
    }

    [Fact]
    public async Task A_cancelled_sign_in_stores_nothing()
    {
        var services = Services(ScriptedDeviceCodeHandler.Answering(HttpStatusCode.OK, DeviceCodeBody));
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => services.Credentials.SignInMetaAsync("meta", progress: null, enablingAccess: null, cancellation.Token));

        Assert.False(services.Credentials.HasMetaLogin("meta"));
        Assert.Null(services.Secrets.Get(ProviderCredentials.MetaTokenReference("meta")));
    }

    [Fact]
    public async Task A_key_outside_the_five_minute_skew_is_used_without_a_network_call()
    {
        var handler = new ScriptedDeviceCodeHandler();
        var services = Services(handler);
        Store(services, "still-good-key", "identity-token", DateTimeOffset.UtcNow.AddMinutes(6));

        var key = await services.Credentials.GetMetaApiKeyAsync("meta", CancellationToken.None);

        Assert.Equal("still-good-key", key);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task A_key_inside_the_five_minute_skew_is_re_minted_with_the_stored_identity_token()
    {
        var handler = ScriptedDeviceCodeHandler.Answering(HttpStatusCode.OK, """{"api_key":"reminted-key"}""");
        var services = Services(handler);
        // Four minutes of life left: the key counts as expiring five minutes early, so it is re-minted now.
        Store(services, "almost-expired-key", "stored-identity-token", DateTimeOffset.UtcNow.AddMinutes(4));

        var key = await services.Credentials.GetMetaApiKeyAsync("meta", CancellationToken.None);

        Assert.Equal("reminted-key", key);
        var request = Assert.Single(handler.Requests);
        // A re-mint, not a new login: no device call at all, just the stored identity token presented for a fresh key.
        Assert.Equal("https://api.meta.ai/muse-code/key", request.Uri!.AbsoluteUri);
        Assert.Equal("Bearer stored-identity-token", request.Header("Authorization"));

        var stored = services.Secrets.Get(ProviderCredentials.MetaTokenReference("meta"));
        Assert.Contains("reminted-key", stored!, StringComparison.Ordinal);
        Assert.DoesNotContain("almost-expired-key", stored, StringComparison.Ordinal);
        Assert.Contains("stored-identity-token", stored, StringComparison.Ordinal);
        Assert.Equal("reminted-key", await services.Credentials.GetMetaApiKeyAsync("meta", CancellationToken.None));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task A_refused_re_mint_drops_the_bundle_so_the_row_asks_for_a_sign_in_again()
    {
        var services = Services(ScriptedDeviceCodeHandler.Answering(HttpStatusCode.Unauthorized, """{"error":"invalid_grant"}"""));
        Store(services, "dead-key", "dead-identity-token", DateTimeOffset.UtcNow.AddSeconds(-60));

        var exception = await Assert.ThrowsAsync<ProviderException>(
            () => services.Credentials.GetMetaApiKeyAsync("meta", CancellationToken.None));

        Assert.Equal(ProviderStrings.MetaSignInRequiredText().Resolve(), exception.Message);
        Assert.False(services.Credentials.HasMetaLogin("meta"));
        Assert.Equal(ProviderStatusKind.NeedsSignIn, services.CreateTransient(Meta()).Status.Kind);
    }

    [Fact]
    public async Task A_refused_re_mint_keeps_a_stored_bundle_whose_identity_token_is_already_newer()
    {
        var handler = ScriptedDeviceCodeHandler.Answering(HttpStatusCode.Unauthorized, """{"error":"invalid_grant"}""");
        var services = Services(handler);
        Store(services, "stale-key", "rejected-identity-token", DateTimeOffset.UtcNow.AddSeconds(-60));
        // Another caller re-mints while this request is on the wire, so the identity token the server refused is already gone
        // from the store. Forgetting the bundle then would delete a working login.
        handler.BeforeResponding = _ => Store(
            services,
            "newer-key",
            "newer-identity-token",
            DateTimeOffset.UtcNow.AddHours(1));

        var exception = await Assert.ThrowsAsync<ProviderException>(
            () => services.Credentials.GetMetaApiKeyAsync("meta", CancellationToken.None));

        Assert.Equal(ProviderStrings.MetaSignInRequiredText().Resolve(), exception.Message);
        Assert.True(services.Credentials.HasMetaLogin("meta"));
        var stored = services.Secrets.Get(ProviderCredentials.MetaTokenReference("meta"));
        Assert.Contains("newer-key", stored!, StringComparison.Ordinal);
        Assert.DoesNotContain("rejected-identity-token", stored, StringComparison.Ordinal);
        Assert.Equal(ProviderStatusKind.Ready, services.CreateTransient(Meta()).Status.Kind);
    }

    [Fact]
    public async Task Two_concurrent_reads_share_one_re_mint_and_return_the_same_key()
    {
        var handler = ScriptedDeviceCodeHandler.Answering(HttpStatusCode.OK, """{"api_key":"reminted-key"}""");
        var services = Services(handler);
        Store(services, "expired-key", "stored-identity-token", DateTimeOffset.UtcNow.AddSeconds(-60));
        // Keep the first mint on the wire until both callers are inside, so the overlap is guaranteed.
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        handler.Hold = hold.Task;

        var first = services.Credentials.GetMetaApiKeyAsync("meta", CancellationToken.None);
        var second = services.Credentials.GetMetaApiKeyAsync("meta", CancellationToken.None);
        hold.SetResult();
        var keys = await Task.WhenAll(first, second);

        Assert.Equal(new[] { "reminted-key", "reminted-key" }, keys);
        Assert.Single(handler.Requests);
    }

    public void Dispose()
    {
        TestTempFiles.DeleteQuietly(root);
    }

    private ProviderServices Services(HttpMessageHandler handler) =>
        new(
            Path.Combine(root, "settings.json"),
            root,
            new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan },
            time: new InstantTime(DateTimeOffset.UtcNow));

    /// <summary>Puts a bundle in the encrypted store the way a finished sign-in leaves it.</summary>
    private static void Store(ProviderServices services, string apiKey, string identityToken, DateTimeOffset expiresAt)
    {
        var json = JsonSerializer.Serialize(
            new MetaTokenDocument { ApiKey = apiKey, IdentityToken = identityToken, ExpiresAt = expiresAt },
            ProvidersJsonContext.Default.MetaTokenDocument);
        services.Secrets.Set(ProviderCredentials.MetaTokenReference("meta"), json);
    }

    /// <summary>
    /// A connected Meta provider the way the login dialog builds it: <c>SecretReference = null</c>, because the bundle lives in
    /// the secret store under <see cref="ProviderCredentials.MetaTokenReference"/> and never in settings.json.
    /// </summary>
    private static ProviderDefinition Meta() => new()
    {
        Id = "meta",
        PresetId = ProviderPresets.MetaId,
        DisplayName = "Meta",
        Protocol = ProviderProtocol.OpenAiResponses,
        BaseUrl = "https://api.meta.ai/v1",
        CredentialKind = ProviderCredentialKind.MetaSubscription,
        SecretReference = null,
        Models = [],
        DefaultModel = string.Empty,
        Overrides = new Dictionary<string, ModelMetadata>(StringComparer.Ordinal),
    };
}
