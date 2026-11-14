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
/// xAI's device-code flow against a scripted HTTP handler: what the start response has to carry, how a poll separates "wait"
/// from "done" from "give up", and how a refresh keeps or rotates the token bundle.
/// </summary>
public sealed class XAiAuthenticationTests
{
    private const string DeviceCodeBody = """
        {"device_code":"xai-device-code","user_code":"WXYZ-9876",
         "verification_uri":"https://accounts.x.ai/device","verification_uri_complete":"https://accounts.x.ai/device?code=WXYZ-9876",
         "interval":5,"expires_in":900}
        """;

    private const string TokenBody = """
        {"access_token":"xai-access-token","refresh_token":"xai-refresh-token","token_type":"Bearer","expires_in":3600}
        """;

    [Fact]
    public async Task The_start_request_carries_the_client_the_scopes_and_the_referrer()
    {
        var handler = ScriptedXAiHandler.Answering(HttpStatusCode.OK, DeviceCodeBody);
        var client = Client(handler);

        var code = await client.StartAsync(CancellationToken.None);

        Assert.Equal("xai-device-code", code.DeviceCode);
        Assert.Equal("WXYZ-9876", code.UserCode);
        Assert.Equal("https://accounts.x.ai/device", code.VerificationUri);
        Assert.Equal("https://accounts.x.ai/device?code=WXYZ-9876", code.VerificationUriComplete);
        Assert.Equal(5, code.IntervalSeconds);
        Assert.Equal(TimeSpan.FromMinutes(15), code.ExpiresAt - Epoch);
        // The device code is a bearer credential for the poll call, so it must not reach a log line through ToString.
        Assert.DoesNotContain("xai-device-code", code.ToString(), StringComparison.Ordinal);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://auth.x.ai/oauth2/device/code", request.Uri!.AbsoluteUri);
        Assert.Equal(["application/json"], request.Accept);
        Assert.Contains("client_id=" + XAiAuthentication.ClientId, request.Body, StringComparison.Ordinal);
        Assert.Contains(
            "scope=openid+profile+email+offline_access+grok-cli%3Aaccess+api%3Aaccess",
            request.Body,
            StringComparison.Ordinal);
        Assert.Contains("referrer=pi", request.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_complete_url_is_optional_and_falls_back_to_the_plain_one()
    {
        var client = Client(ScriptedXAiHandler.Answering(
            HttpStatusCode.OK,
            """
            {"device_code":"d","user_code":"u","verification_uri":"https://accounts.x.ai/device","expires_in":600}
            """));

        var code = await client.StartAsync(CancellationToken.None);

        Assert.Equal("https://accounts.x.ai/device", code.VerificationUriComplete);
        Assert.Equal(XAiAuthentication.MinimumIntervalSeconds, code.IntervalSeconds);
    }

    [Fact]
    public async Task A_verification_url_that_is_not_https_or_a_missing_expiry_is_an_invalid_response()
    {
        var plainHttp = Client(ScriptedXAiHandler.Answering(
            HttpStatusCode.OK,
            """
            {"device_code":"d","user_code":"u","verification_uri":"http://accounts.x.ai/device","expires_in":600}
            """));
        var noExpiry = Client(ScriptedXAiHandler.Answering(
            HttpStatusCode.OK,
            """
            {"device_code":"d","user_code":"u","verification_uri":"https://accounts.x.ai/device"}
            """));

        var insecure = await Assert.ThrowsAsync<ProviderException>(() => plainHttp.StartAsync(CancellationToken.None));
        var lifeless = await Assert.ThrowsAsync<ProviderException>(() => noExpiry.StartAsync(CancellationToken.None));

        // The page carries the user code, so it is never opened over plain http; a code with no lifetime would read as expired.
        Assert.Contains("https", insecure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("expires_in", lifeless.Message, StringComparison.Ordinal);
        Assert.False(insecure.Retryable);
        Assert.False(lifeless.Retryable);
    }

    [Fact]
    public async Task A_failed_start_reports_the_status_and_the_body()
    {
        var client = Client(ScriptedXAiHandler.Answering(HttpStatusCode.Forbidden, """{"error":"invalid_client"}"""));

        var exception = await Assert.ThrowsAsync<ProviderException>(() => client.StartAsync(CancellationToken.None));

        Assert.Equal(403, exception.StatusCode);
        Assert.Contains("403", exception.Message, StringComparison.Ordinal);
        Assert.Contains("invalid_client", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_pending_poll_keeps_the_interval_and_a_later_poll_returns_the_bundle()
    {
        var handler = new ScriptedXAiHandler()
            .Then(HttpStatusCode.OK, DeviceCodeBody)
            .Then(HttpStatusCode.OK, """{"error":"authorization_pending"}""")
            .Then(HttpStatusCode.OK, TokenBody);
        var client = Client(handler);
        var code = await client.StartAsync(CancellationToken.None);

        var pending = await client.PollAsync(code, code.IntervalSeconds, CancellationToken.None);
        var complete = await client.PollAsync(code, pending.IntervalSeconds, CancellationToken.None);

        Assert.True(pending.IsPending);
        Assert.Null(pending.Bundle);
        Assert.Equal(5, pending.IntervalSeconds);
        Assert.False(complete.IsPending);
        Assert.Equal("xai-access-token", complete.Bundle!.AccessToken);
        Assert.Equal("xai-refresh-token", complete.Bundle.RefreshToken);
        Assert.Equal(Epoch + TimeSpan.FromHours(1), complete.Bundle.ExpiresAt);
        // The bundle type never prints a token, so no diagnostic line can leak one.
        Assert.DoesNotContain("xai-access-token", complete.Bundle.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("xai-refresh-token", complete.Bundle.ToString(), StringComparison.Ordinal);

        var poll = handler.Requests[2];
        Assert.Equal("https://auth.x.ai/oauth2/token", poll.Uri!.AbsoluteUri);
        Assert.Contains("device_code=xai-device-code", poll.Body, StringComparison.Ordinal);
        Assert.Contains("client_id=" + XAiAuthentication.ClientId, poll.Body, StringComparison.Ordinal);
        Assert.Contains(
            "grant_type=urn%3Aietf%3Aparams%3Aoauth%3Agrant-type%3Adevice_code",
            poll.Body,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Polling_too_fast_raises_the_interval()
    {
        var handler = new ScriptedXAiHandler()
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
        Assert.Equal(XAiAuthentication.MinimumIntervalSeconds, tooSmall.IntervalSeconds);
        Assert.True(tooSmall.IsPending);
    }

    [Fact]
    public async Task An_expired_code_and_a_denied_login_end_the_flow_with_their_own_reason()
    {
        var expired = Client(
            new ScriptedXAiHandler().Then(HttpStatusCode.OK, DeviceCodeBody).Then(HttpStatusCode.OK, """{"error":"expired_token"}"""));
        var denied = Client(
            new ScriptedXAiHandler().Then(HttpStatusCode.OK, DeviceCodeBody).Then(HttpStatusCode.OK, """{"error":"access_denied"}"""));
        var deniedOther = Client(
            new ScriptedXAiHandler().Then(HttpStatusCode.OK, DeviceCodeBody).Then(HttpStatusCode.OK, """{"error":"authorization_denied"}"""));

        var expiredCode = await expired.StartAsync(CancellationToken.None);
        var deniedCode = await denied.StartAsync(CancellationToken.None);
        var deniedOtherCode = await deniedOther.StartAsync(CancellationToken.None);
        var expiredError = await Assert.ThrowsAsync<ProviderException>(
            () => expired.PollAsync(expiredCode, 5, CancellationToken.None));
        var deniedError = await Assert.ThrowsAsync<ProviderException>(
            () => denied.PollAsync(deniedCode, 5, CancellationToken.None));
        // The authorization server spells a refusal two ways; both mean the user said no.
        var deniedOtherError = await Assert.ThrowsAsync<ProviderException>(
            () => deniedOther.PollAsync(deniedOtherCode, 5, CancellationToken.None));

        Assert.Contains("expired", expiredError.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("restart", expiredError.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("denied", deniedError.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("denied", deniedOtherError.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(expiredError.Retryable);
        Assert.False(deniedError.Retryable);
        Assert.False(deniedOtherError.Retryable);
    }

    [Fact]
    public async Task A_token_response_without_a_refresh_token_is_not_a_usable_login()
    {
        var client = Client(
            new ScriptedXAiHandler()
                .Then(HttpStatusCode.OK, DeviceCodeBody)
                .Then(HttpStatusCode.OK, """{"access_token":"only-access","expires_in":3600}"""));

        var code = await client.StartAsync(CancellationToken.None);
        var exception = await Assert.ThrowsAsync<ProviderException>(
            () => client.PollAsync(code, 5, CancellationToken.None));

        Assert.Contains("refresh_token", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("only-access", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_server_fault_fails_the_poll_instead_of_looking_pending()
    {
        var client = Client(
            new ScriptedXAiHandler()
                .Then(HttpStatusCode.OK, DeviceCodeBody)
                .Then(HttpStatusCode.InternalServerError, "upstream is down"));

        var code = await client.StartAsync(CancellationToken.None);
        var exception = await Assert.ThrowsAsync<ProviderException>(
            () => client.PollAsync(code, 5, CancellationToken.None));

        Assert.Equal(500, exception.StatusCode);
        Assert.True(exception.Retryable);
    }

    [Fact]
    public async Task A_refresh_that_omits_the_rotating_token_keeps_the_one_it_used_and_defaults_to_an_hour()
    {
        var handler = ScriptedXAiHandler.Answering(HttpStatusCode.OK, """{"access_token":"fresh-access"}""");
        var client = Client(handler);

        var bundle = await client.RefreshAsync("kept-refresh", CancellationToken.None);

        Assert.Equal("fresh-access", bundle.AccessToken);
        Assert.Equal("kept-refresh", bundle.RefreshToken);
        Assert.Equal(Epoch + TimeSpan.FromHours(1), bundle.ExpiresAt);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://auth.x.ai/oauth2/token", request.Uri!.AbsoluteUri);
        Assert.Contains("grant_type=refresh_token", request.Body, StringComparison.Ordinal);
        Assert.Contains("client_id=" + XAiAuthentication.ClientId, request.Body, StringComparison.Ordinal);
        Assert.Contains("refresh_token=kept-refresh", request.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_refresh_that_rotates_the_token_replaces_it()
    {
        var handler = ScriptedXAiHandler.Answering(
            HttpStatusCode.OK,
            """{"access_token":"new-access","refresh_token":"new-refresh","expires_in":1800}""");

        var bundle = await Client(handler).RefreshAsync("old-refresh", CancellationToken.None);

        Assert.Equal("new-access", bundle.AccessToken);
        Assert.Equal("new-refresh", bundle.RefreshToken);
        Assert.Equal(Epoch + TimeSpan.FromMinutes(30), bundle.ExpiresAt);
    }

    [Fact]
    public async Task A_throttled_refresh_is_retried_and_then_succeeds()
    {
        var handler = new ScriptedXAiHandler()
            .Then(HttpStatusCode.TooManyRequests, """{"error":"rate_limited"}""")
            .Then(HttpStatusCode.OK, """{"access_token":"second-access","refresh_token":"second-refresh","expires_in":600}""");

        var bundle = await Client(handler).RefreshAsync("old-refresh", CancellationToken.None);

        Assert.Equal("second-access", bundle.AccessToken);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task A_rejected_refresh_means_signing_in_again()
    {
        var client = Client(
            new ScriptedXAiHandler().Then(HttpStatusCode.Unauthorized, """{"error":"invalid_grant"}"""));

        var exception = await Assert.ThrowsAsync<ProviderException>(
            () => client.RefreshAsync("stale-refresh", CancellationToken.None));

        Assert.Equal(401, exception.StatusCode);
        Assert.False(exception.Retryable);
        Assert.Contains("sign in again", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The clock every client in this class runs on, so a lifetime assertion is arithmetic and not a wait.</summary>
    private static readonly DateTimeOffset Epoch = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

    private static XAiAuthentication Client(HttpMessageHandler handler) =>
        new(new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan }, new InstantTime(Epoch));
}

/// <summary>
/// The credential side of the xAI login: what a row reads as before and after a sign-in, where the bundle lives, the
/// five-minute skew, and the rule that a rejected refresh only drops the bundle it actually rejected.
/// </summary>
public sealed class XAiAuthenticationCredentialTests : IDisposable
{
    private const string DeviceCodeBody = """
        {"device_code":"xai-device-code","user_code":"WXYZ-9876",
         "verification_uri":"https://accounts.x.ai/device","verification_uri_complete":"https://accounts.x.ai/device?code=WXYZ-9876",
         "interval":5,"expires_in":900}
        """;

    private const string TokenBody = """
        {"access_token":"xai-access-token","refresh_token":"xai-refresh-token","expires_in":3600}
        """;

    private readonly string root = TestTempFiles.NewDirectory("forge-xai-login-");


    [Fact]
    public void The_xai_preset_offers_a_key_and_a_sign_in_and_its_row_shows_the_button()
    {
        var preset = ProviderPresets.Find(ProviderPresets.XAiId)!;
        var row = new BuiltInProviderRowViewModel(preset);

        Assert.True(preset.Auth.HasFlag(ProviderAuthOptions.ApiKey));
        Assert.True(preset.Auth.HasFlag(ProviderAuthOptions.XAiSubscription));
        Assert.True(row.ShowsLogin);
        Assert.True(row.ShowsApiKey);
        Assert.Equal(ProviderCredentialKind.XAiSubscription, ProviderDefinition.FromPreset(preset, "xai").CredentialKind);
    }

    [Fact]
    public void A_stored_xai_provider_keeps_its_credential_kind_across_the_document()
    {
        var stored = Xai().ToDocument();

        Assert.Equal("xai-subscription", stored.Auth!.Kind);
        Assert.Equal(ProviderCredentialKind.XAiSubscription, ProviderDefinition.FromDocument(stored, "fallback").CredentialKind);

        // An api-key entry for the same preset still reads as a key: the stored name comes before the preset's capabilities.
        stored.Auth.Kind = "api-key";
        Assert.Equal(ProviderCredentialKind.ApiKey, ProviderDefinition.FromDocument(stored, "fallback").CredentialKind);

        // A file that predates the kind field falls back to the preset, exactly as the other sign-ins do.
        stored.Auth.Kind = null;
        Assert.Equal(ProviderCredentialKind.XAiSubscription, ProviderDefinition.FromDocument(stored, "fallback").CredentialKind);
    }

    [Fact]
    public void The_meta_preset_takes_its_own_credential_and_the_responses_protocol()
    {
        var meta = ProviderPresets.Find(ProviderPresets.MetaId)!;

        Assert.Equal(ProviderProtocol.OpenAiResponses, meta.Protocol);
        Assert.Equal(ProviderAuthOptions.ApiKey | ProviderAuthOptions.MetaSubscription, meta.Auth);
        Assert.Equal("https://api.meta.ai/v1", meta.BaseUrl);

        // The subscription is the default way in: a key is the alternative, never the fallback of a vendor whose models
        // are only reachable with the plan.
        var metaDefinition = ProviderDefinition.FromPreset(meta, "meta");
        Assert.Equal(ProviderCredentialKind.MetaSubscription, metaDefinition.CredentialKind);

        // Reaching "Responses" is the whole point of the preset: the stored name, the parsed protocol and the wire kind the
        // Core factory builds all have to agree.
        var stored = metaDefinition.ToDocument();
        Assert.Equal(ProviderFactory.OpenAiResponsesKind, stored.Protocol);
        Assert.Equal(ProviderProtocol.OpenAiResponses, ProviderDefinition.FromDocument(stored, "meta").Protocol);

        var transient = Services(new ScriptedXAiHandler()).CreateTransient(metaDefinition);
        Assert.Equal(
            ProviderFactory.OpenAiResponsesKind,
            transient.BuildConfig("key", metaDefinition.BaseUrl, ProviderAuthStyle.Default, preamble: null).Kind);
    }

    [Fact]
    public void A_chatgpt_provider_reaches_the_responses_kind_from_the_preset_alone()
    {
        // The plan is the OpenAI preset's subscription way in: naming that kind is what moves the endpoint and the protocol.
        var chatGpt = ProviderDefinition.FromPreset(
            ProviderPresets.Find(ProviderPresets.OpenAiId)!,
            "chatgpt",
            credentialKind: ProviderCredentialKind.ChatGptSubscription);

        var transient = Services(new ScriptedXAiHandler()).CreateTransient(chatGpt);

        Assert.Equal(ProviderProtocol.OpenAiResponses, chatGpt.Protocol);
        Assert.Equal(
            ProviderFactory.OpenAiResponsesKind,
            transient.BuildConfig("token", chatGpt.BaseUrl, ProviderAuthStyle.BearerOnly, preamble: null).Kind);
        Assert.Equal(ProviderStatusKind.NeedsSignIn, transient.Status.Kind);
    }

    [Fact]
    public void A_xai_row_without_a_bundle_needs_a_sign_in()
    {
        var provider = Services(new ScriptedXAiHandler()).CreateTransient(Xai());

        Assert.Equal(ProviderStatusKind.NeedsSignIn, provider.Status.Kind);
        Assert.Equal(ProviderStrings.XAiSignInRequiredText().Resolve(), provider.Status.Detail.Resolve());
    }

    [Fact]
    public async Task A_missing_bundle_throws_the_sign_in_reason()
    {
        var services = Services(new ScriptedXAiHandler());

        var exception = await Assert.ThrowsAsync<ProviderException>(
            () => services.Credentials.GetXAiAccessTokenAsync("xai", CancellationToken.None));

        Assert.False(exception.Retryable);
        Assert.Equal(ProviderStrings.XAiSignInRequiredText().Resolve(), exception.Message);
    }

    [Fact]
    public async Task A_successful_sign_in_persists_the_bundle_and_never_writes_it_to_the_settings()
    {
        var handler = new ScriptedXAiHandler().Then(HttpStatusCode.OK, DeviceCodeBody).Then(HttpStatusCode.OK, TokenBody);
        var services = Services(handler);
        XAiDeviceCode? shown = null;

        var bundle = await services.Credentials.SignInXAiAsync("xai", code => shown = code, CancellationToken.None);

        Assert.Equal("WXYZ-9876", shown?.UserCode);
        Assert.Equal("https://accounts.x.ai/device?code=WXYZ-9876", shown?.VerificationUriComplete);
        Assert.Equal("xai-access-token", bundle.AccessToken);
        Assert.True(services.Credentials.HasXAiLogin("xai"));
        Assert.Equal(ProviderStatusKind.Ready, services.CreateTransient(Xai()).Status.Kind);

        var reference = ProviderCredentials.XAiTokenReference("xai");
        Assert.Equal("xai:xai", reference);
        var stored = services.Secrets.Get(reference);
        Assert.NotNull(stored);
        Assert.Contains("xai-refresh-token", stored!, StringComparison.Ordinal);

        // The bundle is the only secret, and it lives under "xai:<id>" in the encrypted store.
        using var store = JsonDocument.Parse(await File.ReadAllTextAsync(services.Secrets.StoragePath));
        Assert.Equal(
            new[] { reference },
            store.RootElement.GetProperty("entries").EnumerateObject().Select(static entry => entry.Name));

        // The definition reaches settings.json the way the login dialog builds it: the xai-subscription credential kind and
        // no secret reference, so both tokens stay in the encrypted store and never appear in the settings file.
        await services.SaveAsync([Xai()], "xai", defaultContextWindow: null);
        var settings = await File.ReadAllTextAsync(Path.Combine(root, "settings.json"));
        Assert.Contains(ProviderPresets.XAiId, settings, StringComparison.Ordinal);
        Assert.DoesNotContain("xai-access-token", settings, StringComparison.Ordinal);
        Assert.DoesNotContain("xai-refresh-token", settings, StringComparison.Ordinal);
        Assert.DoesNotContain(reference, settings, StringComparison.Ordinal);

        using var document = JsonDocument.Parse(settings);
        var auth = document.RootElement
            .GetProperty("providers")
            .GetProperty("items")[0]
            .GetProperty("auth");
        Assert.Equal("xai-subscription", auth.GetProperty("kind").GetString());
        Assert.True(
            !auth.TryGetProperty("secretRef", out var secretReference) || secretReference.ValueKind is JsonValueKind.Null,
            "a sign-in provider carries no secret reference: the bundle lives in the encrypted store");
    }

    [Fact]
    public async Task A_cancelled_sign_in_stores_nothing()
    {
        var services = Services(ScriptedXAiHandler.Answering(HttpStatusCode.OK, DeviceCodeBody));
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => services.Credentials.SignInXAiAsync("xai", progress: null, cancellation.Token));

        Assert.False(services.Credentials.HasXAiLogin("xai"));
        Assert.Null(services.Secrets.Get(ProviderCredentials.XAiTokenReference("xai")));
    }

    [Fact]
    public async Task A_token_outside_the_five_minute_skew_is_used_without_a_network_call()
    {
        var handler = new ScriptedXAiHandler();
        var services = Services(handler);
        Store(services, "still-good-access", "refresh-token", DateTimeOffset.UtcNow.AddMinutes(6));

        var token = await services.Credentials.GetXAiAccessTokenAsync("xai", CancellationToken.None);

        Assert.Equal("still-good-access", token);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task A_token_inside_the_five_minute_skew_is_refreshed_and_the_new_bundle_is_stored()
    {
        var handler = ScriptedXAiHandler.Answering(
            HttpStatusCode.OK,
            """{"access_token":"rotated-access","refresh_token":"rotated-refresh","expires_in":3600}""");
        var services = Services(handler);
        // Four minutes of life left: the credential counts as expiring five minutes early, so it is refreshed now.
        Store(services, "almost-expired-access", "old-refresh", DateTimeOffset.UtcNow.AddMinutes(4));

        var token = await services.Credentials.GetXAiAccessTokenAsync("xai", CancellationToken.None);

        Assert.Equal("rotated-access", token);
        Assert.Contains("refresh_token=old-refresh", Assert.Single(handler.Requests).Body, StringComparison.Ordinal);
        var stored = services.Secrets.Get(ProviderCredentials.XAiTokenReference("xai"));
        Assert.Contains("rotated-refresh", stored!, StringComparison.Ordinal);
        Assert.DoesNotContain("almost-expired-access", stored, StringComparison.Ordinal);
        Assert.Equal(
            "rotated-access",
            await services.Credentials.GetXAiAccessTokenAsync("xai", CancellationToken.None));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task A_refused_refresh_drops_the_bundle_so_the_row_asks_for_a_sign_in_again()
    {
        var services = Services(ScriptedXAiHandler.Answering(HttpStatusCode.Unauthorized, """{"error":"invalid_grant"}"""));
        Store(services, "dead-access", "dead-refresh", DateTimeOffset.UtcNow.AddSeconds(-60));

        var exception = await Assert.ThrowsAsync<ProviderException>(
            () => services.Credentials.GetXAiAccessTokenAsync("xai", CancellationToken.None));

        Assert.Equal(ProviderStrings.XAiSignInRequiredText().Resolve(), exception.Message);
        Assert.False(services.Credentials.HasXAiLogin("xai"));
        Assert.Equal(ProviderStatusKind.NeedsSignIn, services.CreateTransient(Xai()).Status.Kind);
    }

    [Fact]
    public async Task A_rejected_refresh_keeps_a_stored_bundle_whose_refresh_token_is_already_newer()
    {
        var handler = ScriptedXAiHandler.Answering(HttpStatusCode.Unauthorized, """{"error":"invalid_grant"}""");
        var services = Services(handler);
        Store(services, "stale-access", "rejected-refresh", DateTimeOffset.UtcNow.AddSeconds(-60));
        // Another caller rotates the bundle while this refresh is on the wire, so the token the server refused is already
        // gone from the store. Forgetting the bundle then would delete a working login.
        handler.BeforeResponding = _ => Store(services, "newer-access", "newer-refresh", DateTimeOffset.UtcNow.AddHours(1));

        var exception = await Assert.ThrowsAsync<ProviderException>(
            () => services.Credentials.GetXAiAccessTokenAsync("xai", CancellationToken.None));

        Assert.Equal(ProviderStrings.XAiSignInRequiredText().Resolve(), exception.Message);
        Assert.True(services.Credentials.HasXAiLogin("xai"));
        var stored = services.Secrets.Get(ProviderCredentials.XAiTokenReference("xai"));
        Assert.Contains("newer-refresh", stored!, StringComparison.Ordinal);
        Assert.DoesNotContain("rejected-refresh", stored, StringComparison.Ordinal);
        Assert.Equal(ProviderStatusKind.Ready, services.CreateTransient(Xai()).Status.Kind);
    }

    [Fact]
    public async Task Two_concurrent_reads_share_one_refresh_and_return_the_same_access_token()
    {
        var handler = ScriptedXAiHandler.Answering(
            HttpStatusCode.OK,
            """{"access_token":"rotated-access","refresh_token":"rotated-refresh","expires_in":3600}""");
        var services = Services(handler);
        Store(services, "expired-access", "old-refresh", DateTimeOffset.UtcNow.AddSeconds(-60));
        // Keep the first refresh on the wire until both callers are inside, so the overlap is guaranteed.
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        handler.Hold = hold.Task;

        // Both callers start on the same expired bundle. Only one may spend the refresh token: the other waits and then reads
        // the bundle the winner stored, so the loser never presents a token that has already been rotated away.
        var first = services.Credentials.GetXAiAccessTokenAsync("xai", CancellationToken.None);
        var second = services.Credentials.GetXAiAccessTokenAsync("xai", CancellationToken.None);
        hold.SetResult();
        var tokens = await Task.WhenAll(first, second);

        Assert.Equal(new[] { "rotated-access", "rotated-access" }, tokens);
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
    private static void Store(ProviderServices services, string access, string refresh, DateTimeOffset expiresAt)
    {
        var json = JsonSerializer.Serialize(
            new XAiTokenDocument { AccessToken = access, RefreshToken = refresh, ExpiresAt = expiresAt },
            ProvidersJsonContext.Default.XAiTokenDocument);
        services.Secrets.Set(ProviderCredentials.XAiTokenReference("xai"), json);
    }

    /// <summary>
    /// A connected xAI provider the way the login dialog builds it: <c>SecretReference = null</c>, because the bundle lives in
    /// the secret store under <see cref="ProviderCredentials.XAiTokenReference"/> and never in settings.json.
    /// </summary>
    private static ProviderDefinition Xai() => new()
    {
        Id = "xai",
        PresetId = ProviderPresets.XAiId,
        DisplayName = "xAI",
        Protocol = ProviderProtocol.OpenAiCompatible,
        BaseUrl = "https://api.x.ai/v1",
        CredentialKind = ProviderCredentialKind.XAiSubscription,
        SecretReference = null,
        Models = [],
        DefaultModel = string.Empty,
        Overrides = new Dictionary<string, ModelMetadata>(StringComparer.Ordinal),
    };
}

/// <summary>
/// Answers in order, one queued response per request, and records what was asked: the device flow is a sequence, so a
/// handler that answers everything the same way cannot express "pending, then approved".
/// </summary>
internal sealed class ScriptedXAiHandler : ScriptedAuthHandler<RecordedXAiRequest>
{
    public static ScriptedXAiHandler Answering(HttpStatusCode status, string body)
    {
        var handler = new ScriptedXAiHandler();
        handler.Then(status, body);
        return handler;
    }

    protected override RecordedXAiRequest Record(HttpRequestMessage request, string body) =>
        new(
            request.RequestUri,
            body,
            [.. request.Headers.Accept.Select(static accept => accept.MediaType ?? string.Empty)]);
}

/// <summary>One request a scripted flow made, captured so a test can assert the wire shape.</summary>
internal sealed record RecordedXAiRequest(Uri? Uri, string Body, IReadOnlyList<string> Accept);
