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
/// Kimi For Coding's device-code flow against a scripted HTTP handler: what the start response has to carry, how a poll
/// separates "wait" from "done" from "give up", and how a refresh rotates the bundle or asks for a new sign-in.
/// </summary>
public sealed class KimiAuthenticationTests
{
    private const string DeviceCodeBody = """
        {"device_code":"device-code-value","user_code":"ABCD-1234",
         "verification_uri":"https://auth.kimi.com/device","verification_uri_complete":"https://auth.kimi.com/device?code=ABCD-1234",
         "interval":5,"expires_in":900}
        """;

    private const string TokenBody = """
        {"access_token":"kimi-access-token","refresh_token":"kimi-refresh-token","token_type":"Bearer","expires_in":3600}
        """;

    [Fact]
    public async Task The_start_response_yields_the_user_code_and_both_urls()
    {
        var handler = ScriptedDeviceCodeHandler.Answering(HttpStatusCode.OK, DeviceCodeBody);
        var client = Client(handler);

        var code = await client.StartAsync(CancellationToken.None);

        Assert.Equal("device-code-value", code.DeviceCode);
        Assert.Equal("ABCD-1234", code.UserCode);
        Assert.Equal("https://auth.kimi.com/device", code.VerificationUri);
        Assert.Equal("https://auth.kimi.com/device?code=ABCD-1234", code.VerificationUriComplete);
        Assert.Equal(5, code.IntervalSeconds);
        Assert.Equal(TimeSpan.FromMinutes(15), code.ExpiresAt - Epoch);
        // The device code is a bearer credential for the poll call, so it must not reach a log line through ToString.
        Assert.DoesNotContain("device-code-value", code.ToString(), StringComparison.Ordinal);

        var request = Assert.Single(handler.Requests);
        Assert.EndsWith("/api/oauth/device_authorization", request.Uri!.AbsoluteUri, StringComparison.Ordinal);
        Assert.Equal(["application/json"], request.Accept);
        Assert.Contains("client_id=" + KimiAuthentication.ClientId, request.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_missing_interval_and_expiry_fall_back_to_the_documented_defaults()
    {
        var handler = ScriptedDeviceCodeHandler.Answering(
            HttpStatusCode.OK,
            """
            {"device_code":"d","user_code":"u","verification_uri":"https://auth.kimi.com/d","verification_uri_complete":"https://auth.kimi.com/d?c=u"}
            """);
        var client = Client(handler);

        var code = await client.StartAsync(CancellationToken.None);

        Assert.Equal(KimiAuthentication.MinimumIntervalSeconds, code.IntervalSeconds);
        Assert.Equal(TimeSpan.FromMinutes(15), code.ExpiresAt - Epoch);
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
    public async Task A_start_response_without_the_codes_or_urls_is_an_invalid_response()
    {
        var client = Client(ScriptedDeviceCodeHandler.Answering(
            HttpStatusCode.OK,
            """
            {"device_code":"d","user_code":"u","verification_uri":"https://auth.kimi.com/d"}
            """));

        var exception = await Assert.ThrowsAsync<ProviderException>(() => client.StartAsync(CancellationToken.None));

        Assert.False(exception.Retryable);
        Assert.Contains("verification", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_pending_poll_keeps_the_interval_and_a_later_poll_returns_the_bundle()
    {
        var handler = new ScriptedDeviceCodeHandler()
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
        Assert.Equal("kimi-access-token", complete.Bundle!.AccessToken);
        Assert.Equal("kimi-refresh-token", complete.Bundle.RefreshToken);
        Assert.Equal(Epoch + TimeSpan.FromHours(1), complete.Bundle.ExpiresAt);
        // The bundle type never prints a token, so no diagnostic line can leak one.
        Assert.DoesNotContain("kimi-access-token", complete.Bundle.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("kimi-refresh-token", complete.Bundle.ToString(), StringComparison.Ordinal);

        var poll = handler.Requests[2];
        Assert.EndsWith("/api/oauth/token", poll.Uri!.AbsoluteUri, StringComparison.Ordinal);
        // The exact body pins the field order the wire format carries; the shared skeleton must not reshuffle it.
        Assert.Equal(
            "client_id=" + KimiAuthentication.ClientId + "&device_code=device-code-value&grant_type=urn%3Aietf%3Aparams%3Aoauth%3Agrant-type%3Adevice_code",
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
        Assert.Equal(KimiAuthentication.MinimumIntervalSeconds, tooSmall.IntervalSeconds);
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

        // The exact messages pin the shared template's wording; a shared-skeleton edit cannot drift them silently.
        Assert.Equal("Kimi: the device authorization expired. Please restart the login.", expiredError.Message);
        Assert.Equal("Kimi: the login was denied.", deniedError.Message);
        Assert.False(expiredError.Retryable);
        Assert.False(deniedError.Retryable);
    }

    [Fact]
    public async Task An_unrecognised_answer_reports_the_status_and_the_error_code()
    {
        var client = Client(
            new ScriptedDeviceCodeHandler()
                .Then(HttpStatusCode.OK, DeviceCodeBody)
                .Then(HttpStatusCode.BadRequest, """{"error":"invalid_request","error_description":"bad device code"}"""));
        var code = await client.StartAsync(CancellationToken.None);

        var exception = await Assert.ThrowsAsync<ProviderException>(
            () => client.PollAsync(code, 5, CancellationToken.None));

        Assert.Equal(400, exception.StatusCode);
        Assert.Contains("invalid_request", exception.Message, StringComparison.Ordinal);
        Assert.Contains("bad device code", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_server_fault_fails_the_poll_instead_of_looking_pending()
    {
        var client = Client(
            new ScriptedDeviceCodeHandler()
                .Then(HttpStatusCode.OK, DeviceCodeBody)
                .Then(HttpStatusCode.InternalServerError, "upstream is down"));
        var code = await client.StartAsync(CancellationToken.None);

        var exception = await Assert.ThrowsAsync<ProviderException>(
            () => client.PollAsync(code, 5, CancellationToken.None));

        Assert.Equal(500, exception.StatusCode);
        Assert.Contains("500", exception.Message, StringComparison.Ordinal);
        Assert.Contains("upstream is down", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_token_response_without_a_refresh_token_is_not_a_usable_login()
    {
        var client = Client(
            new ScriptedDeviceCodeHandler()
                .Then(HttpStatusCode.OK, DeviceCodeBody)
                .Then(HttpStatusCode.OK, """{"access_token":"only-access","expires_in":3600}"""));
        var code = await client.StartAsync(CancellationToken.None);

        var exception = await Assert.ThrowsAsync<ProviderException>(
            () => client.PollAsync(code, 5, CancellationToken.None));

        Assert.Contains("refresh_token", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("only-access", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_cancelled_poll_throws_the_cancellation_and_not_a_provider_error()
    {
        var client = Client(ScriptedDeviceCodeHandler.Answering(HttpStatusCode.OK, DeviceCodeBody));
        var code = await client.StartAsync(CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.PollAsync(code, 5, cancellation.Token));
    }

    [Fact]
    public async Task A_refresh_returns_the_rotated_bundle()
    {
        var handler = ScriptedDeviceCodeHandler.Answering(
            HttpStatusCode.OK,
            """{"access_token":"new-access","refresh_token":"new-refresh","expires_in":1800}""");
        var client = Client(handler);

        var bundle = await client.RefreshAsync("old-refresh", CancellationToken.None);

        Assert.Equal("new-access", bundle.AccessToken);
        Assert.Equal("new-refresh", bundle.RefreshToken);
        Assert.Equal(Epoch + TimeSpan.FromMinutes(30), bundle.ExpiresAt);
        var request = Assert.Single(handler.Requests);
        Assert.EndsWith("/api/oauth/token", request.Uri!.AbsoluteUri, StringComparison.Ordinal);
        Assert.Contains("grant_type=refresh_token", request.Body, StringComparison.Ordinal);
        Assert.Contains("refresh_token=old-refresh", request.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_throttled_refresh_is_retried_and_then_succeeds()
    {
        var handler = new ScriptedDeviceCodeHandler()
            .Then(HttpStatusCode.TooManyRequests, """{"error":"rate_limited"}""")
            .Then(HttpStatusCode.TooManyRequests, """{"error":"rate_limited"}""")
            .Then(HttpStatusCode.OK, """{"access_token":"third-access","refresh_token":"third-refresh","expires_in":600}""");
        var client = Client(handler);

        var bundle = await client.RefreshAsync("old-refresh", CancellationToken.None);

        Assert.Equal("third-access", bundle.AccessToken);
        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task A_refresh_that_keeps_failing_costs_only_the_retries_it_is_allowed()
    {
        var handler = new ScriptedDeviceCodeHandler()
            .Then(HttpStatusCode.InternalServerError, "down")
            .Then(HttpStatusCode.InternalServerError, "down")
            .Then(HttpStatusCode.InternalServerError, "down")
            .Then(HttpStatusCode.InternalServerError, "down");
        var client = Client(handler);

        var exception = await Assert.ThrowsAsync<ProviderException>(
            () => client.RefreshAsync("old-refresh", CancellationToken.None));

        // The first attempt plus three retries; the interval after that belongs to a new sign-in.
        Assert.Equal(4, handler.Requests.Count);
        Assert.True(exception.Retryable);
    }

    [Fact]
    public async Task A_rejected_refresh_means_signing_in_again()
    {
        var client = Client(
            new ScriptedDeviceCodeHandler().Then(HttpStatusCode.Unauthorized, """{"error":"invalid_grant"}"""));

        var exception = await Assert.ThrowsAsync<ProviderException>(
            () => client.RefreshAsync("stale-refresh", CancellationToken.None));

        Assert.Equal(401, exception.StatusCode);
        Assert.False(exception.Retryable);
        Assert.Contains("sign in again", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_oauth_host_can_be_overridden_and_never_ends_with_a_slash()
    {
        var handler = ScriptedDeviceCodeHandler.Answering(HttpStatusCode.OK, DeviceCodeBody);
        var client = Client(handler);

        var previous = Environment.GetEnvironmentVariable(KimiAuthentication.OAuthHostVariable);
        var previousFallback = Environment.GetEnvironmentVariable(KimiAuthentication.OAuthHostFallbackVariable);
        Environment.SetEnvironmentVariable(KimiAuthentication.OAuthHostFallbackVariable, null);
        Environment.SetEnvironmentVariable(KimiAuthentication.OAuthHostVariable, null);
        try
        {
            Assert.Equal(KimiAuthentication.DefaultOAuthHost, KimiAuthentication.ResolveHost());

            // The first non-empty variable wins, and a trailing slash is trimmed.
            Environment.SetEnvironmentVariable(KimiAuthentication.OAuthHostFallbackVariable, "https://fallback.invalid");
            Environment.SetEnvironmentVariable(KimiAuthentication.OAuthHostVariable, "  ");
            Assert.Equal("https://fallback.invalid", KimiAuthentication.ResolveHost());

            Environment.SetEnvironmentVariable(KimiAuthentication.OAuthHostVariable, "https://staging.invalid//");
            await client.StartAsync(CancellationToken.None);
            Assert.Equal(
                "https://staging.invalid/api/oauth/device_authorization",
                Assert.Single(handler.Requests).Uri!.AbsoluteUri);
        }
        finally
        {
            Environment.SetEnvironmentVariable(KimiAuthentication.OAuthHostVariable, previous);
            Environment.SetEnvironmentVariable(KimiAuthentication.OAuthHostFallbackVariable, previousFallback);
        }
    }

    /// <summary>The clock every client in this class runs on, so a lifetime assertion is arithmetic and not a wait.</summary>
    private static readonly DateTimeOffset Epoch = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

    private static KimiAuthentication Client(HttpMessageHandler handler) =>
        new(new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan }, new InstantTime(Epoch));
}

/// <summary>
/// The credential side of the Kimi login: what a row reads as before and after a sign-in, where the bundle lives, and the
/// rule that a rotated refresh token replaces the stored one.
/// </summary>
public sealed class KimiAuthenticationCredentialTests : IDisposable
{
    private const string DeviceCodeBody = """
        {"device_code":"device-code-value","user_code":"ABCD-1234",
         "verification_uri":"https://auth.kimi.com/device","verification_uri_complete":"https://auth.kimi.com/device?code=ABCD-1234",
         "interval":5,"expires_in":900}
        """;

    private const string TokenBody = """
        {"access_token":"kimi-access-token","refresh_token":"kimi-refresh-token","expires_in":3600}
        """;

    private readonly string root = TestTempFiles.NewDirectory("forge-kimi-login-");


    [Fact]
    public void The_kimi_preset_offers_a_key_and_a_sign_in_and_defaults_to_the_sign_in()
    {
        var preset = ProviderPresets.Find(ProviderPresets.KimiId)!;
        var row = new BuiltInProviderRowViewModel(preset);

        Assert.True(preset.Auth.HasFlag(ProviderAuthOptions.ApiKey));
        Assert.True(preset.Auth.HasFlag(ProviderAuthOptions.KimiCode));
        Assert.True(row.ShowsLogin);
        Assert.True(row.ShowsApiKey);
        Assert.Equal(ProviderCredentialKind.KimiCode, ProviderDefinition.FromPreset(preset, "kimi").CredentialKind);
    }

    [Fact]
    public void A_stored_kimi_provider_keeps_its_credential_kind_across_the_document()
    {
        var signIn = Kimi();
        var stored = signIn.ToDocument();

        Assert.Equal("kimi-code", stored.Auth!.Kind);
        Assert.Equal(ProviderCredentialKind.KimiCode, ProviderDefinition.FromDocument(stored, "fallback").CredentialKind);

        // An api-key entry for the same preset still reads as a key: the stored name comes before the preset's capabilities.
        stored.Auth.Kind = "api-key";
        Assert.Equal(ProviderCredentialKind.ApiKey, ProviderDefinition.FromDocument(stored, "fallback").CredentialKind);

        // A file that predates the kind field falls back to the preset, exactly as the other two sign-ins do.
        stored.Auth.Kind = null;
        Assert.Equal(ProviderCredentialKind.KimiCode, ProviderDefinition.FromDocument(stored, "fallback").CredentialKind);
    }

    [Fact]
    public void A_kimi_row_without_a_bundle_needs_a_sign_in()
    {
        var provider = Services(new ScriptedDeviceCodeHandler()).CreateTransient(Kimi());

        Assert.Equal(ProviderStatusKind.NeedsSignIn, provider.Status.Kind);
        Assert.Equal(ProviderStrings.KimiSignInRequiredText().Resolve(), provider.Status.Detail.Resolve());
    }

    [Fact]
    public async Task A_missing_bundle_throws_the_sign_in_reason_the_copilot_path_uses()
    {
        var services = Services(new ScriptedDeviceCodeHandler());

        var exception = await Assert.ThrowsAsync<ProviderException>(
            () => services.Credentials.GetKimiAccessTokenAsync("kimi", CancellationToken.None));

        Assert.False(exception.Retryable);
        Assert.Equal(ProviderStrings.KimiSignInRequiredText().Resolve(), exception.Message);
    }

    [Fact]
    public async Task A_successful_sign_in_persists_the_bundle_and_never_writes_it_to_the_settings()
    {
        var handler = new ScriptedDeviceCodeHandler().Then(HttpStatusCode.OK, DeviceCodeBody).Then(HttpStatusCode.OK, TokenBody);
        var services = Services(handler);
        KimiDeviceCode? shown = null;

        var bundle = await services.Credentials.SignInKimiAsync("kimi", code => shown = code, CancellationToken.None);

        Assert.Equal("ABCD-1234", shown?.UserCode);
        Assert.Equal("https://auth.kimi.com/device?code=ABCD-1234", shown?.VerificationUriComplete);
        Assert.Equal("kimi-access-token", bundle.AccessToken);
        Assert.True(services.Credentials.HasKimiLogin("kimi"));
        Assert.Equal(ProviderStatusKind.Ready, services.CreateTransient(Kimi()).Status.Kind);

        var reference = ProviderCredentials.KimiTokenReference("kimi");
        Assert.Equal("kimi:kimi", reference);
        var stored = services.Secrets.Get(reference);
        Assert.NotNull(stored);
        Assert.Contains("kimi-refresh-token", stored!, StringComparison.Ordinal);

        // The bundle is the only secret, and it lives under "kimi:<id>" in the encrypted store.
        using var store = JsonDocument.Parse(await File.ReadAllTextAsync(services.Secrets.StoragePath));
        Assert.Equal(
            new[] { reference },
            store.RootElement.GetProperty("entries").EnumerateObject().Select(static entry => entry.Name));

        // The definition reaches settings.json the way the login dialog builds it: the kimi-code credential kind and no secret
        // reference, so both tokens stay in the encrypted store and never appear in the settings file.
        await services.SaveAsync([Kimi()], "kimi", defaultContextWindow: null);
        var settings = await File.ReadAllTextAsync(Path.Combine(root, "settings.json"));
        Assert.Contains(ProviderPresets.KimiId, settings, StringComparison.Ordinal);
        Assert.DoesNotContain("kimi-access-token", settings, StringComparison.Ordinal);
        Assert.DoesNotContain("kimi-refresh-token", settings, StringComparison.Ordinal);
        Assert.DoesNotContain(reference, settings, StringComparison.Ordinal);

        using var document = JsonDocument.Parse(settings);
        var auth = document.RootElement
            .GetProperty("providers")
            .GetProperty("items")[0]
            .GetProperty("auth");
        Assert.Equal("kimi-code", auth.GetProperty("kind").GetString());
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
            () => services.Credentials.SignInKimiAsync("kimi", progress: null, cancellation.Token));

        Assert.False(services.Credentials.HasKimiLogin("kimi"));
        Assert.Null(services.Secrets.Get(ProviderCredentials.KimiTokenReference("kimi")));
    }

    [Fact]
    public async Task A_valid_stored_access_token_is_used_without_a_network_call()
    {
        var handler = new ScriptedDeviceCodeHandler();
        var services = Services(handler);
        Store(services, "still-good-access", "refresh-token", DateTimeOffset.UtcNow.AddMinutes(30));

        var token = await services.Credentials.GetKimiAccessTokenAsync("kimi", CancellationToken.None);

        Assert.Equal("still-good-access", token);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task An_access_token_near_expiry_is_refreshed_and_the_rotated_bundle_is_stored()
    {
        var handler = ScriptedDeviceCodeHandler.Answering(
            HttpStatusCode.OK,
            """{"access_token":"rotated-access","refresh_token":"rotated-refresh","expires_in":3600}""");
        var services = Services(handler);
        Store(services, "almost-expired-access", "old-refresh", DateTimeOffset.UtcNow.AddSeconds(20));

        var token = await services.Credentials.GetKimiAccessTokenAsync("kimi", CancellationToken.None);

        Assert.Equal("rotated-access", token);
        Assert.Contains("refresh_token=old-refresh", Assert.Single(handler.Requests).Body, StringComparison.Ordinal);
        // The refresh token rotates, so the store has to hold the new one or the next refresh would be rejected.
        var stored = services.Secrets.Get(ProviderCredentials.KimiTokenReference("kimi"));
        Assert.Contains("rotated-refresh", stored!, StringComparison.Ordinal);
        Assert.DoesNotContain("almost-expired-access", stored, StringComparison.Ordinal);
        Assert.Equal(
            "rotated-access",
            await services.Credentials.GetKimiAccessTokenAsync("kimi", CancellationToken.None));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task A_refused_refresh_drops_the_bundle_so_the_row_asks_for_a_sign_in_again()
    {
        var services = Services(ScriptedDeviceCodeHandler.Answering(HttpStatusCode.Unauthorized, """{"error":"invalid_grant"}"""));
        Store(services, "dead-access", "dead-refresh", DateTimeOffset.UtcNow.AddSeconds(-60));

        var exception = await Assert.ThrowsAsync<ProviderException>(
            () => services.Credentials.GetKimiAccessTokenAsync("kimi", CancellationToken.None));

        Assert.Equal(ProviderStrings.KimiSignInRequiredText().Resolve(), exception.Message);
        Assert.False(services.Credentials.HasKimiLogin("kimi"));
        Assert.Equal(ProviderStatusKind.NeedsSignIn, services.CreateTransient(Kimi()).Status.Kind);
    }

    [Fact]
    public async Task Two_concurrent_reads_share_one_refresh_and_return_the_same_access_token()
    {
        var handler = ScriptedDeviceCodeHandler.Answering(
            HttpStatusCode.OK,
            """{"access_token":"rotated-access","refresh_token":"rotated-refresh","expires_in":3600}""");
        var services = Services(handler);
        Store(services, "expired-access", "old-refresh", DateTimeOffset.UtcNow.AddSeconds(-60));
        // Keep the first refresh on the wire until both callers are inside, so the overlap is guaranteed and not a race with
        // the mock's own latency.
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        handler.Hold = hold.Task;

        // Both callers start on the same expired bundle. Only one may spend the refresh token: the other waits and then reads
        // the bundle the winner stored, so the loser never presents a token that has already been rotated away.
        var first = services.Credentials.GetKimiAccessTokenAsync("kimi", CancellationToken.None);
        var second = services.Credentials.GetKimiAccessTokenAsync("kimi", CancellationToken.None);
        hold.SetResult();
        var tokens = await Task.WhenAll(first, second);

        Assert.Equal(new[] { "rotated-access", "rotated-access" }, tokens);
        Assert.Single(handler.Requests);
        var stored = services.Secrets.Get(ProviderCredentials.KimiTokenReference("kimi"));
        Assert.Contains("rotated-refresh", stored!, StringComparison.Ordinal);
        Assert.Equal(
            "rotated-access",
            await services.Credentials.GetKimiAccessTokenAsync("kimi", CancellationToken.None));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task A_rejected_refresh_keeps_a_stored_bundle_whose_refresh_token_is_already_newer()
    {
        var handler = ScriptedDeviceCodeHandler.Answering(HttpStatusCode.Unauthorized, """{"error":"invalid_grant"}""");
        var services = Services(handler);
        Store(services, "stale-access", "rejected-refresh", DateTimeOffset.UtcNow.AddSeconds(-60));
        // Another caller rotates the bundle while this refresh is on the wire, so the token the server refused is already
        // gone from the store. Forgetting the bundle then would delete a working login.
        handler.BeforeResponding = _ => Store(services, "newer-access", "newer-refresh", DateTimeOffset.UtcNow.AddHours(1));

        var exception = await Assert.ThrowsAsync<ProviderException>(
            () => services.Credentials.GetKimiAccessTokenAsync("kimi", CancellationToken.None));

        Assert.Equal(ProviderStrings.KimiSignInRequiredText().Resolve(), exception.Message);
        Assert.True(services.Credentials.HasKimiLogin("kimi"));
        var stored = services.Secrets.Get(ProviderCredentials.KimiTokenReference("kimi"));
        Assert.Contains("newer-refresh", stored!, StringComparison.Ordinal);
        Assert.DoesNotContain("rejected-refresh", stored, StringComparison.Ordinal);
        Assert.Equal(ProviderStatusKind.Ready, services.CreateTransient(Kimi()).Status.Kind);
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
            new KimiTokenDocument { AccessToken = access, RefreshToken = refresh, ExpiresAt = expiresAt },
            ProvidersJsonContext.Default.KimiTokenDocument);
        services.Secrets.Set(ProviderCredentials.KimiTokenReference("kimi"), json);
    }

    /// <summary>
    /// A connected Kimi provider the way the login dialog builds it: <see cref="ProviderLoginViewModel.Build"/> writes
    /// <c>SecretReference = null</c> for a sign-in, because the bundle lives in the secret store under
    /// <see cref="ProviderCredentials.KimiTokenReference"/> and never in settings.json.
    /// </summary>
    private static ProviderDefinition Kimi() => new()
    {
        Id = "kimi",
        PresetId = ProviderPresets.KimiId,
        DisplayName = "Kimi For Coding",
        Protocol = ProviderProtocol.OpenAiCompatible,
        BaseUrl = "https://api.kimi.com/coding/v1",
        CredentialKind = ProviderCredentialKind.KimiCode,
        SecretReference = null,
        Models = ["k3"],
        DefaultModel = "k3",
        Overrides = new Dictionary<string, ModelMetadata>(StringComparer.Ordinal),
    };
}
