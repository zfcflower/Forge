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
/// The ChatGPT plan sign-in against a scripted HTTP handler: the user-code request, the poll that spells "not yet" three
/// different ways, the exchange that turns an approval into a token bundle, and the account id that has to be read out of the
/// access token before the bundle is usable at all.
/// </summary>
public sealed class ChatGptAuthenticationTests
{
    private const string UserCodeBody = """{"device_auth_id":"device-auth-id","user_code":"EFGH-5678","interval":5}""";

    [Fact]
    public async Task The_user_code_request_is_json_and_reads_an_interval_that_arrives_as_a_string()
    {
        var handler = ScriptedChatGptHandler.Answering(HttpStatusCode.OK, UserCodeBody);
        var client = Client(handler);

        var code = await client.StartAsync(CancellationToken.None);

        Assert.Equal("device-auth-id", code.DeviceAuthId);
        Assert.Equal("EFGH-5678", code.UserCode);
        Assert.Equal(5, code.IntervalSeconds);
        Assert.Equal(ChatGptAuthentication.DeadlineSeconds, (code.ExpiresAt - Epoch).TotalSeconds);
        // The device-auth id is a bearer credential for the poll call, so it must not reach a log line through ToString.
        Assert.DoesNotContain("device-auth-id", code.ToString(), StringComparison.Ordinal);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://auth.openai.com/api/accounts/deviceauth/usercode", request.Uri!.AbsoluteUri);
        Assert.Equal("application/json", request.ContentType);
        Assert.Equal("""{"client_id":"app_EMoamEEZ73f0CkXaXp7hrann"}""", request.Body);

        // The interval is a string often enough that both shapes have to be read.
        var asString = Client(ScriptedChatGptHandler.Answering(
            HttpStatusCode.OK,
            """{"device_auth_id":"d","user_code":"u","interval":"8"}"""));
        Assert.Equal(8, (await asString.StartAsync(CancellationToken.None)).IntervalSeconds);

        // No interval at all falls back to the floor.
        var withoutInterval = Client(ScriptedChatGptHandler.Answering(
            HttpStatusCode.OK,
            """{"device_auth_id":"d","user_code":"u"}"""));
        Assert.Equal(
            ChatGptAuthentication.MinimumIntervalSeconds,
            (await withoutInterval.StartAsync(CancellationToken.None)).IntervalSeconds);
    }

    [Fact]
    public async Task A_server_that_has_no_device_login_says_so()
    {
        var client = Client(ScriptedChatGptHandler.Answering(HttpStatusCode.NotFound, "not found"));

        var exception = await Assert.ThrowsAsync<ProviderException>(() => client.StartAsync(CancellationToken.None));

        Assert.Equal(404, exception.StatusCode);
        Assert.Contains("not enabled", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(exception.Retryable);
    }

    [Fact]
    public async Task A_failed_user_code_request_reports_the_status_and_the_body()
    {
        var client = Client(ScriptedChatGptHandler.Answering(HttpStatusCode.BadRequest, """{"error":"invalid_request"}"""));

        var exception = await Assert.ThrowsAsync<ProviderException>(() => client.StartAsync(CancellationToken.None));

        Assert.Equal(400, exception.StatusCode);
        Assert.Contains("400", exception.Message, StringComparison.Ordinal);
        Assert.Contains("invalid_request", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_pending_poll_is_a_403_or_a_404_and_neither_ends_the_login()
    {
        var handler = new ScriptedChatGptHandler()
            .Then(HttpStatusCode.OK, UserCodeBody)
            .Then(HttpStatusCode.Forbidden, "{}")
            .Then(HttpStatusCode.NotFound, "{}")
            .Then(HttpStatusCode.OK, """{"authorization_code":"auth-code","code_verifier":"verifier"}""");
        var client = Client(handler);
        var code = await client.StartAsync(CancellationToken.None);

        var forbidden = await client.PollAsync(code, code.IntervalSeconds, CancellationToken.None);
        var notFound = await client.PollAsync(code, forbidden.IntervalSeconds, CancellationToken.None);
        var approved = await client.PollAsync(code, notFound.IntervalSeconds, CancellationToken.None);

        Assert.True(forbidden.IsPending);
        Assert.True(notFound.IsPending);
        Assert.False(approved.IsPending);
        Assert.Equal("auth-code", approved.AuthorizationCode);
        Assert.Equal("verifier", approved.CodeVerifier);
        Assert.Equal(5, approved.IntervalSeconds);
        // Neither half of an approval is safe to print.
        Assert.DoesNotContain("auth-code", approved.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("verifier", approved.ToString(), StringComparison.Ordinal);

        var poll = handler.Requests[3];
        Assert.Equal("https://auth.openai.com/api/accounts/deviceauth/token", poll.Uri!.AbsoluteUri);
        Assert.Equal("application/json", poll.ContentType);
        Assert.Equal("""{"device_auth_id":"device-auth-id","user_code":"EFGH-5678"}""", poll.Body);
    }

    [Fact]
    public async Task A_pending_poll_can_also_be_carried_in_the_error_code()
    {
        var handler = new ScriptedChatGptHandler()
            .Then(HttpStatusCode.OK, UserCodeBody)
            .Then(HttpStatusCode.OK, """{"error":"deviceauth_authorization_pending"}""")
            .Then(HttpStatusCode.OK, """{"error":{"code":"deviceauth_authorization_pending"}}""");
        var client = Client(handler);
        var code = await client.StartAsync(CancellationToken.None);

        var plain = await client.PollAsync(code, code.IntervalSeconds, CancellationToken.None);
        var nested = await client.PollAsync(code, plain.IntervalSeconds, CancellationToken.None);

        Assert.True(plain.IsPending);
        Assert.True(nested.IsPending);
    }

    [Fact]
    public async Task Polling_too_fast_raises_the_interval()
    {
        var handler = new ScriptedChatGptHandler()
            .Then(HttpStatusCode.OK, UserCodeBody)
            .Then(HttpStatusCode.OK, """{"error":"slow_down"}""")
            .Then(HttpStatusCode.OK, """{"error":"slow_down","interval":11}""");
        var client = Client(handler);
        var code = await client.StartAsync(CancellationToken.None);

        var unnamed = await client.PollAsync(code, 5, CancellationToken.None);
        var named = await client.PollAsync(code, unnamed.IntervalSeconds, CancellationToken.None);

        // No interval in the answer: five seconds on top of what was already waited.
        Assert.Equal(10, unnamed.IntervalSeconds);
        Assert.Equal(11, named.IntervalSeconds);
    }

    [Fact]
    public async Task A_poll_answer_that_is_neither_a_code_nor_pending_ends_the_login()
    {
        var half = Client(new ScriptedChatGptHandler()
            .Then(HttpStatusCode.OK, UserCodeBody)
            .Then(HttpStatusCode.OK, """{"authorization_code":"auth-code"}"""));
        var unknown = Client(new ScriptedChatGptHandler()
            .Then(HttpStatusCode.OK, UserCodeBody)
            .Then(HttpStatusCode.BadRequest, """{"error":"invalid_device_code"}"""));
        var silent = Client(new ScriptedChatGptHandler()
            .Then(HttpStatusCode.OK, UserCodeBody)
            .Then(HttpStatusCode.BadRequest, "{}"));

        var halfCode = await half.StartAsync(CancellationToken.None);
        var unknownCode = await unknown.StartAsync(CancellationToken.None);
        var silentCode = await silent.StartAsync(CancellationToken.None);

        // An approval without both halves cannot be exchanged, so it is a failure rather than another silent wait.
        var missingVerifier = await Assert.ThrowsAsync<ProviderException>(
            () => half.PollAsync(halfCode, 5, CancellationToken.None));
        var unknownError = await Assert.ThrowsAsync<ProviderException>(
            () => unknown.PollAsync(unknownCode, 5, CancellationToken.None));
        var noError = await Assert.ThrowsAsync<ProviderException>(
            () => silent.PollAsync(silentCode, 5, CancellationToken.None));

        Assert.Contains("code_verifier", missingVerifier.Message, StringComparison.Ordinal);
        Assert.Contains("invalid_device_code", unknownError.Message, StringComparison.Ordinal);
        Assert.Contains("400", noError.Message, StringComparison.Ordinal);
        Assert.False(missingVerifier.Retryable);
        Assert.False(unknownError.Retryable);
        Assert.False(noError.Retryable);
    }

    [Fact]
    public async Task A_server_fault_fails_the_poll_instead_of_looking_pending()
    {
        var client = Client(new ScriptedChatGptHandler()
            .Then(HttpStatusCode.OK, UserCodeBody)
            .Then(HttpStatusCode.BadGateway, "upstream is down"));

        var code = await client.StartAsync(CancellationToken.None);
        var exception = await Assert.ThrowsAsync<ProviderException>(
            () => client.PollAsync(code, 5, CancellationToken.None));

        Assert.Equal(502, exception.StatusCode);
        Assert.True(exception.Retryable);
    }

    [Fact]
    public async Task The_exchange_posts_the_code_and_reads_the_account_out_of_the_access_token()
    {
        var accessToken = Jwt("acct_123");
        var handler = ScriptedChatGptHandler.Answering(
            HttpStatusCode.OK,
            """{"access_token":""" + JsonSerializer.Serialize(accessToken) +
            ""","refresh_token":"refresh-1","expires_in":3600}""");

        var bundle = await Client(handler).ExchangeAsync("auth-code", "verifier", CancellationToken.None);

        Assert.Equal(accessToken, bundle.AccessToken);
        Assert.Equal("refresh-1", bundle.RefreshToken);
        Assert.Equal("acct_123", bundle.AccountId);
        Assert.Equal(Epoch + TimeSpan.FromHours(1), bundle.ExpiresAt);
        Assert.DoesNotContain(accessToken, bundle.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("refresh-1", bundle.ToString(), StringComparison.Ordinal);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://auth.openai.com/oauth/token", request.Uri!.AbsoluteUri);
        Assert.Equal(["application/json"], request.Accept);
        Assert.Contains("grant_type=authorization_code", request.Body, StringComparison.Ordinal);
        Assert.Contains("client_id=app_EMoamEEZ73f0CkXaXp7hrann", request.Body, StringComparison.Ordinal);
        Assert.Contains("code=auth-code", request.Body, StringComparison.Ordinal);
        Assert.Contains("code_verifier=verifier", request.Body, StringComparison.Ordinal);
        Assert.Contains(
            "redirect_uri=https%3A%2F%2Fauth.openai.com%2Fdeviceauth%2Fcallback",
            request.Body,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_exchange_answer_without_a_refresh_token_or_a_lifetime_is_not_a_usable_login()
    {
        var noRefresh = Client(ScriptedChatGptHandler.Answering(
            HttpStatusCode.OK,
            """{"access_token":""" + JsonSerializer.Serialize(Jwt("acct_1")) + ""","expires_in":3600}"""));
        var noLifetime = Client(ScriptedChatGptHandler.Answering(
            HttpStatusCode.OK,
            """{"access_token":""" + JsonSerializer.Serialize(Jwt("acct_1")) + ""","refresh_token":"refresh-1"}"""));

        var withoutRefresh = await Assert.ThrowsAsync<ProviderException>(
            () => noRefresh.ExchangeAsync("auth-code", "verifier", CancellationToken.None));
        var withoutLifetime = await Assert.ThrowsAsync<ProviderException>(
            () => noLifetime.ExchangeAsync("auth-code", "verifier", CancellationToken.None));

        Assert.Contains("refresh_token", withoutRefresh.Message, StringComparison.Ordinal);
        Assert.Contains("expires_in", withoutLifetime.Message, StringComparison.Ordinal);
        Assert.False(withoutRefresh.Retryable);
        Assert.False(withoutLifetime.Retryable);
    }

    [Fact]
    public async Task A_token_without_the_account_claim_cannot_name_a_plan()
    {
        var noClaim = Client(ScriptedChatGptHandler.Answering(
            HttpStatusCode.OK,
            """{"access_token":""" + JsonSerializer.Serialize(Jwt(accountId: null)) +
            ""","refresh_token":"refresh-1","expires_in":3600}"""));
        var notAJwt = Client(ScriptedChatGptHandler.Answering(
            HttpStatusCode.OK,
            """{"access_token":"not-a-jwt","refresh_token":"refresh-1","expires_in":3600}"""));

        var withoutClaim = await Assert.ThrowsAsync<ProviderException>(
            () => noClaim.ExchangeAsync("auth-code", "verifier", CancellationToken.None));
        var unusable = await Assert.ThrowsAsync<ProviderException>(
            () => notAJwt.ExchangeAsync("auth-code", "verifier", CancellationToken.None));

        // The token is unusable without it, so the login fails here instead of on the first turn.
        Assert.Contains("chatgpt_account_id", withoutClaim.Message, StringComparison.Ordinal);
        Assert.Contains("chatgpt_account_id", unusable.Message, StringComparison.Ordinal);
        Assert.False(withoutClaim.Retryable);
    }

    [Fact]
    public async Task A_refresh_re_reads_the_account_id_from_the_new_access_token()
    {
        var handler = ScriptedChatGptHandler.Answering(
            HttpStatusCode.OK,
            """{"access_token":""" + JsonSerializer.Serialize(Jwt("acct_new")) +
            ""","refresh_token":"refresh-2","expires_in":1800}""");

        var bundle = await Client(handler).RefreshAsync("refresh-1", CancellationToken.None);

        Assert.Equal("acct_new", bundle.AccountId);
        Assert.Equal("refresh-2", bundle.RefreshToken);
        Assert.Equal(Epoch + TimeSpan.FromMinutes(30), bundle.ExpiresAt);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://auth.openai.com/oauth/token", request.Uri!.AbsoluteUri);
        Assert.Contains("grant_type=refresh_token", request.Body, StringComparison.Ordinal);
        Assert.Contains("client_id=app_EMoamEEZ73f0CkXaXp7hrann", request.Body, StringComparison.Ordinal);
        Assert.Contains("refresh_token=refresh-1", request.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_rejected_refresh_means_signing_in_again()
    {
        var client = Client(ScriptedChatGptHandler.Answering(HttpStatusCode.Unauthorized, """{"error":"invalid_grant"}"""));

        var exception = await Assert.ThrowsAsync<ProviderException>(
            () => client.RefreshAsync("stale-refresh", CancellationToken.None));

        Assert.Equal(401, exception.StatusCode);
        Assert.False(exception.Retryable);
        Assert.Contains("sign in again", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The clock every client in this class runs on, so a lifetime assertion is arithmetic and not a wait.</summary>
    private static readonly DateTimeOffset Epoch = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

    private static ChatGptAuthentication Client(HttpMessageHandler handler) =>
        new(new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan }, new InstantTime(Epoch));

    /// <summary>
    /// A real base64url JWT whose payload carries the OpenAI auth claim. The signature is not verified by the client, so its
    /// content does not matter; what matters is that the payload decodes like the tokens the server issues.
    /// </summary>
    private static string Jwt(string? accountId)
    {
        var header = Base64Url("""{"alg":"RS256","typ":"JWT"}""");
        var payload = Base64Url(accountId is null
            ? """{"sub":"user"}"""
            : """{"sub":"user","https://api.openai.com/auth":{"chatgpt_account_id":""" +
              JsonSerializer.Serialize(accountId) + "}}");
        return header + "." + payload + ".not-verified";
    }

    private static string Base64Url(string json) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>
/// The credential side of the ChatGPT login: what a row reads as before and after a sign-in, where the bundle lives, the
/// five-minute skew, the four headers every request carries, and the rule that a rejected refresh only drops the bundle it
/// actually rejected.
/// </summary>
public sealed class ChatGptAuthenticationCredentialTests : IDisposable
{
    private const string UserCodeBody = """{"device_auth_id":"device-auth-id","user_code":"EFGH-5678","interval":5}""";

    private const string CompletedSse =
        "data: {\"type\":\"response.completed\",\"response\":{\"id\":\"resp_1\",\"status\":\"completed\"," +
        "\"usage\":{\"input_tokens\":1,\"output_tokens\":1}}}\n\n";

    private readonly string root = TestTempFiles.NewDirectory("forge-chatgpt-login-");


    [Fact]
    public void The_openai_preset_offers_a_key_and_a_plan_and_the_plan_is_the_way_in_this_dialog_takes()
    {
        var preset = ProviderPresets.Find(ProviderPresets.OpenAiId)!;
        var row = new BuiltInProviderRowViewModel(preset);

        // One preset, two buttons: the key is the editor's, the plan is this dialog's.
        Assert.True(row.ShowsLogin);
        Assert.True(row.ShowsApiKey);
        Assert.True(preset.Auth.HasFlag(ProviderAuthOptions.ApiKey));
        Assert.True(preset.Auth.HasFlag(ProviderAuthOptions.ChatGptSubscription));

        // The primary endpoint is the API-key one; the plan reaches the Responses adapter under /codex, and that adapter
        // appends /responses to the URL the subscription endpoint carries.
        Assert.Equal("https://api.openai.com/v1", preset.BaseUrl);
        Assert.Equal(ProviderProtocol.OpenAiCompatible, preset.Protocol);
        var subscription = preset.Subscription!;
        Assert.Equal("https://chatgpt.com/backend-api/codex", subscription.BaseUrl);
        Assert.Equal(ProviderProtocol.OpenAiResponses, subscription.Protocol);
        Assert.Equal(ProviderCredentialKind.ChatGptSubscription, ProviderDefinition.FromPreset(preset, "chatgpt").CredentialKind);
        Assert.Contains("codex/responses", subscription.Note!, StringComparison.Ordinal);
    }

    [Fact]
    public void A_stored_chatgpt_provider_keeps_its_credential_kind_across_the_document()
    {
        var stored = ChatGpt().ToDocument();

        Assert.Equal("chatgpt-subscription", stored.Auth!.Kind);
        Assert.Equal(ProviderCredentialKind.ChatGptSubscription, ProviderDefinition.FromDocument(stored, "fallback").CredentialKind);

        stored.Auth.Kind = "api-key";
        Assert.Equal(ProviderCredentialKind.ApiKey, ProviderDefinition.FromDocument(stored, "fallback").CredentialKind);

        stored.Auth.Kind = null;
        Assert.Equal(ProviderCredentialKind.ChatGptSubscription, ProviderDefinition.FromDocument(stored, "fallback").CredentialKind);
    }

    [Fact]
    public void A_chatgpt_row_without_a_bundle_needs_a_sign_in()
    {
        var provider = Services(new ScriptedChatGptHandler()).CreateTransient(ChatGpt());

        Assert.Equal(ProviderStatusKind.NeedsSignIn, provider.Status.Kind);
        Assert.Equal(ProviderStrings.ChatGptSignInRequiredText().Resolve(), provider.Status.Detail.Resolve());
    }

    [Fact]
    public async Task A_missing_bundle_throws_the_sign_in_reason()
    {
        var services = Services(new ScriptedChatGptHandler());

        var exception = await Assert.ThrowsAsync<ProviderException>(
            () => services.Credentials.GetChatGptTokenAsync("chatgpt", CancellationToken.None));

        Assert.False(exception.Retryable);
        Assert.Equal(ProviderStrings.ChatGptSignInRequiredText().Resolve(), exception.Message);
    }

    [Fact]
    public async Task A_successful_sign_in_persists_the_bundle_and_never_writes_it_to_the_settings()
    {
        var accessToken = Jwt("acct_123");
        var handler = new ScriptedChatGptHandler()
            .Then(HttpStatusCode.OK, UserCodeBody)
            .Then(HttpStatusCode.Forbidden, "{}")
            .Then(HttpStatusCode.OK, """{"authorization_code":"auth-code","code_verifier":"verifier"}""")
            .Then(HttpStatusCode.OK,
                """{"access_token":""" + JsonSerializer.Serialize(accessToken) +
                ""","refresh_token":"refresh-1","expires_in":3600}""");
        var services = Services(handler);
        ChatGptDeviceCode? shown = null;

        var bundle = await services.Credentials.SignInChatGptAsync("chatgpt", code => shown = code, CancellationToken.None);

        Assert.Equal("EFGH-5678", shown?.UserCode);
        Assert.Equal("acct_123", bundle.AccountId);
        Assert.True(services.Credentials.HasChatGptLogin("chatgpt"));
        Assert.Equal(ProviderStatusKind.Ready, services.CreateTransient(ChatGpt()).Status.Kind);
        Assert.Equal(ProviderStrings.ChatGptSignedInText().Resolve(), services.CreateTransient(ChatGpt()).Status.Detail.Resolve());

        var reference = ProviderCredentials.ChatGptTokenReference("chatgpt");
        Assert.Equal("chatgpt:chatgpt", reference);
        var stored = services.Secrets.Get(reference);
        Assert.NotNull(stored);
        Assert.Contains("refresh-1", stored!, StringComparison.Ordinal);
        Assert.Contains("acct_123", stored, StringComparison.Ordinal);

        // The bundle is the only secret, and it lives under "chatgpt:<id>" in the encrypted store.
        using var store = JsonDocument.Parse(await File.ReadAllTextAsync(services.Secrets.StoragePath));
        Assert.Equal(
            new[] { reference },
            store.RootElement.GetProperty("entries").EnumerateObject().Select(static entry => entry.Name));

        // Settings.json holds the definition, never the tokens: the chatgpt-subscription kind carries no secret reference.
        await services.SaveAsync([ChatGpt()], "chatgpt", defaultContextWindow: null);
        var settings = await File.ReadAllTextAsync(Path.Combine(root, "settings.json"));
        Assert.DoesNotContain(accessToken, settings, StringComparison.Ordinal);
        Assert.DoesNotContain("refresh-1", settings, StringComparison.Ordinal);
        Assert.DoesNotContain(reference, settings, StringComparison.Ordinal);

        using var document = JsonDocument.Parse(settings);
        var entry = document.RootElement
            .GetProperty("providers")
            .GetProperty("items")[0];
        // The plan is the OpenAI preset's subscription way in, so the stored preset is the one merged one.
        Assert.Equal(ProviderPresets.OpenAiId, entry.GetProperty("preset").GetString());
        var auth = entry.GetProperty("auth");
        Assert.Equal("chatgpt-subscription", auth.GetProperty("kind").GetString());
        Assert.True(
            !auth.TryGetProperty("secretRef", out var secretReference) || secretReference.ValueKind is JsonValueKind.Null,
            "a sign-in provider carries no secret reference: the bundle lives in the encrypted store");
    }

    [Fact]
    public async Task A_cancelled_sign_in_stores_nothing()
    {
        var services = Services(ScriptedChatGptHandler.Answering(HttpStatusCode.OK, UserCodeBody));
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => services.Credentials.SignInChatGptAsync("chatgpt", progress: null, cancellation.Token));

        Assert.False(services.Credentials.HasChatGptLogin("chatgpt"));
        Assert.Null(services.Secrets.Get(ProviderCredentials.ChatGptTokenReference("chatgpt")));
    }

    [Fact]
    public async Task A_token_outside_the_five_minute_skew_is_used_without_a_network_call()
    {
        var handler = new ScriptedChatGptHandler();
        var services = Services(handler);
        Store(services, "still-good-access", "refresh-1", "acct_123", DateTimeOffset.UtcNow.AddMinutes(6));

        var bundle = await services.Credentials.GetChatGptTokenAsync("chatgpt", CancellationToken.None);

        Assert.Equal("still-good-access", bundle.AccessToken);
        Assert.Equal("acct_123", bundle.AccountId);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task A_token_inside_the_five_minute_skew_is_refreshed_and_the_new_account_id_is_stored()
    {
        var rotated = Jwt("acct_rotated");
        var handler = ScriptedChatGptHandler.Answering(
            HttpStatusCode.OK,
            """{"access_token":""" + JsonSerializer.Serialize(rotated) +
            ""","refresh_token":"refresh-2","expires_in":3600}""");
        var services = Services(handler);
        // Four minutes of life left: the credential counts as expiring five minutes early, so it is refreshed now.
        Store(services, "almost-expired-access", "refresh-1", "acct_123", DateTimeOffset.UtcNow.AddMinutes(4));

        var bundle = await services.Credentials.GetChatGptTokenAsync("chatgpt", CancellationToken.None);

        Assert.Equal(rotated, bundle.AccessToken);
        // The account id is re-read from the token that arrived, not carried over from the expired one.
        Assert.Equal("acct_rotated", bundle.AccountId);
        Assert.Contains("refresh_token=refresh-1", Assert.Single(handler.Requests).Body, StringComparison.Ordinal);

        var stored = services.Secrets.Get(ProviderCredentials.ChatGptTokenReference("chatgpt"));
        Assert.Contains("acct_rotated", stored!, StringComparison.Ordinal);
        Assert.DoesNotContain("almost-expired-access", stored, StringComparison.Ordinal);
        Assert.Equal(
            "acct_rotated",
            (await services.Credentials.GetChatGptTokenAsync("chatgpt", CancellationToken.None)).AccountId);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Two_concurrent_reads_share_one_refresh_and_return_the_same_rotated_bundle()
    {
        var rotated = Jwt("acct_rotated");
        var handler = ScriptedChatGptHandler.Answering(
            HttpStatusCode.OK,
            """{"access_token":""" + JsonSerializer.Serialize(rotated) +
            ""","refresh_token":"refresh-2","expires_in":3600}""");
        var services = Services(handler);
        // Four minutes of life left, so either caller on its own inside the gate would spend the refresh token.
        Store(services, "almost-expired-access", "refresh-1", "acct_123", DateTimeOffset.UtcNow.AddMinutes(4));
        // Keep the refresh on the wire until both callers are inside, so the overlap is guaranteed and not a race with the
        // handler's own latency.
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        handler.Hold = hold.Task;

        // Both callers start on the same expiring bundle. Only one may spend the refresh token: the other waits and then reads
        // the bundle the winner stored, so the loser never presents a token that has already been rotated away.
        var first = services.Credentials.GetChatGptTokenAsync("chatgpt", CancellationToken.None);
        var second = services.Credentials.GetChatGptTokenAsync("chatgpt", CancellationToken.None);
        hold.SetResult();
        var bundles = await Task.WhenAll(first, second);

        Assert.Equal(new[] { rotated, rotated }, bundles.Select(static bundle => bundle.AccessToken));
        // The account id is re-read from the access token that arrived, and the caller that waited sees the same one.
        Assert.All(bundles, static bundle => Assert.Equal("acct_rotated", bundle.AccountId));

        // Exactly one refresh went out, and it spent the token that was stored before the rotation.
        Assert.Contains("refresh_token=refresh-1", Assert.Single(handler.Requests).Body, StringComparison.Ordinal);

        var stored = services.Secrets.Get(ProviderCredentials.ChatGptTokenReference("chatgpt"));
        Assert.Contains("acct_rotated", stored!, StringComparison.Ordinal);
        Assert.Contains("refresh-2", stored, StringComparison.Ordinal);
        Assert.DoesNotContain("almost-expired-access", stored, StringComparison.Ordinal);
        Assert.Equal(
            rotated,
            (await services.Credentials.GetChatGptTokenAsync("chatgpt", CancellationToken.None)).AccessToken);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task A_refused_refresh_drops_the_bundle_so_the_row_asks_for_a_sign_in_again()
    {
        var services = Services(ScriptedChatGptHandler.Answering(HttpStatusCode.Unauthorized, """{"error":"invalid_grant"}"""));
        Store(services, "dead-access", "dead-refresh", "acct_123", DateTimeOffset.UtcNow.AddSeconds(-60));

        var exception = await Assert.ThrowsAsync<ProviderException>(
            () => services.Credentials.GetChatGptTokenAsync("chatgpt", CancellationToken.None));

        Assert.Equal(ProviderStrings.ChatGptSignInRequiredText().Resolve(), exception.Message);
        Assert.False(services.Credentials.HasChatGptLogin("chatgpt"));
        Assert.Equal(ProviderStatusKind.NeedsSignIn, services.CreateTransient(ChatGpt()).Status.Kind);
    }

    [Fact]
    public async Task A_rejected_refresh_keeps_a_stored_bundle_whose_refresh_token_is_already_newer()
    {
        var handler = ScriptedChatGptHandler.Answering(HttpStatusCode.Unauthorized, """{"error":"invalid_grant"}""");
        var services = Services(handler);
        Store(services, "stale-access", "rejected-refresh", "acct_123", DateTimeOffset.UtcNow.AddSeconds(-60));
        // Another caller rotates the bundle while this refresh is on the wire, so the token the server refused is already
        // gone from the store. Forgetting the bundle then would delete a working login.
        handler.BeforeResponding = _ => Store(
            services,
            "newer-access",
            "newer-refresh",
            "acct_newer",
            DateTimeOffset.UtcNow.AddHours(1));

        var exception = await Assert.ThrowsAsync<ProviderException>(
            () => services.Credentials.GetChatGptTokenAsync("chatgpt", CancellationToken.None));

        Assert.Equal(ProviderStrings.ChatGptSignInRequiredText().Resolve(), exception.Message);
        Assert.True(services.Credentials.HasChatGptLogin("chatgpt"));
        var stored = services.Secrets.Get(ProviderCredentials.ChatGptTokenReference("chatgpt"));
        Assert.Contains("newer-refresh", stored!, StringComparison.Ordinal);
        Assert.DoesNotContain("rejected-refresh", stored, StringComparison.Ordinal);
        Assert.Equal(ProviderStatusKind.Ready, services.CreateTransient(ChatGpt()).Status.Kind);
    }

    [Fact]
    public async Task The_chat_path_sends_the_bearer_the_account_and_the_experimental_beta_header()
    {
        var accessToken = Jwt("acct_123");
        var handler = new ScriptedChatGptHandler()
            .Then(HttpStatusCode.OK, UserCodeBody)
            .Then(HttpStatusCode.OK, """{"authorization_code":"auth-code","code_verifier":"verifier"}""")
            .Then(HttpStatusCode.OK,
                """{"access_token":""" + JsonSerializer.Serialize(accessToken) +
                ""","refresh_token":"refresh-1","expires_in":3600}""")
            .Then(HttpStatusCode.OK, CompletedSse);
        var services = Services(handler);
        await services.Credentials.SignInChatGptAsync("chatgpt", progress: null, CancellationToken.None);
        var provider = services.CreateTransient(ChatGpt()).CreateProvider(
            new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan });

        await ConsumeAsync(provider);

        var chat = handler.Requests[^1];
        // The Responses adapter appends /responses to the preset URL, so the plan is addressed at .../codex/responses.
        Assert.Equal("https://chatgpt.com/backend-api/codex/responses", chat.Uri!.AbsoluteUri);
        Assert.Equal("Bearer " + accessToken, chat.Header("Authorization"));
        Assert.Equal("acct_123", chat.Header("chatgpt-account-id"));
        Assert.Equal("responses=experimental", chat.Header("OpenAI-Beta"));
        Assert.Equal("text/event-stream", chat.Header("accept"));
    }

    [Fact]
    public async Task The_listing_path_sends_the_same_four_headers()
    {
        var accessToken = Jwt("acct_123");
        var handler = new ScriptedChatGptHandler()
            .Then(HttpStatusCode.OK, """{"data":[{"id":"gpt-5-codex"}]}""");
        var services = Services(handler);
        Store(services, accessToken, "refresh-1", "acct_123", DateTimeOffset.UtcNow.AddHours(1));

        var list = await services.RefreshModelsAsync(services.CreateTransient(ChatGpt()), CancellationToken.None);

        Assert.Equal(["gpt-5-codex"], list.Ids);
        var listing = Assert.Single(handler.Requests);
        Assert.Equal("https://chatgpt.com/backend-api/codex/models", listing.Uri!.AbsoluteUri);
        Assert.Equal("Bearer " + accessToken, listing.Header("Authorization"));
        Assert.Equal("acct_123", listing.Header("chatgpt-account-id"));
        Assert.Equal("responses=experimental", listing.Header("OpenAI-Beta"));
        Assert.Equal("text/event-stream", listing.Header("accept"));
    }

    public void Dispose()
    {
        TestTempFiles.DeleteQuietly(root);
    }

    /// <summary>Drives one streamed turn far enough that the request has gone out.</summary>
    private static async Task ConsumeAsync(IChatProvider provider)
    {
        await using var enumerator = provider
            .StreamAsync(
                new ChatRequest(
                    string.Empty,
                    [],
                    [ChatMessage.User("ping")],
                    new ModelSettings("gpt-5-codex", ThinkingEffort.Off, MaxOutputTokens: 16, ProviderId: "chatgpt")),
                CancellationToken.None)
            .GetAsyncEnumerator();
        while (await enumerator.MoveNextAsync())
        {
        }
    }

    private ProviderServices Services(HttpMessageHandler handler) =>
        new(
            Path.Combine(root, "settings.json"),
            root,
            new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan },
            time: new InstantTime(DateTimeOffset.UtcNow));

    /// <summary>Puts a bundle in the encrypted store the way a finished sign-in leaves it.</summary>
    private static void Store(
        ProviderServices services,
        string access,
        string refresh,
        string accountId,
        DateTimeOffset expiresAt)
    {
        var json = JsonSerializer.Serialize(
            new ChatGptTokenDocument
            {
                AccessToken = access,
                RefreshToken = refresh,
                AccountId = accountId,
                ExpiresAt = expiresAt,
            },
            ProvidersJsonContext.Default.ChatGptTokenDocument);
        services.Secrets.Set(ProviderCredentials.ChatGptTokenReference("chatgpt"), json);
    }

    /// <summary>
    /// A connected ChatGPT provider the way the login dialog builds it: <c>SecretReference = null</c>, because the bundle lives
    /// in the secret store under <see cref="ProviderCredentials.ChatGptTokenReference"/> and never in settings.json.
    /// </summary>
    private static ProviderDefinition ChatGpt() => new()
    {
        Id = "chatgpt",
        PresetId = ProviderPresets.OpenAiId,
        DisplayName = "OpenAI",
        Protocol = ProviderProtocol.OpenAiResponses,
        BaseUrl = "https://chatgpt.com/backend-api/codex",
        CredentialKind = ProviderCredentialKind.ChatGptSubscription,
        SecretReference = null,
        Models = ["gpt-5-codex"],
        DefaultModel = "gpt-5-codex",
        Overrides = new Dictionary<string, ModelMetadata>(StringComparer.Ordinal),
    };

    /// <summary>
    /// A real base64url JWT whose payload carries the OpenAI auth claim; the signature is not verified by the client, so only
    /// the payload shape matters. Duplicated from the flow tests on purpose: each class asserts against its own token.
    /// </summary>
    private static string Jwt(string accountId)
    {
        var header = Base64Url("""{"alg":"RS256","typ":"JWT"}""");
        var payload = Base64Url(
            """{"sub":"user","https://api.openai.com/auth":{"chatgpt_account_id":""" +
            JsonSerializer.Serialize(accountId) + "}}");
        return header + "." + payload + ".not-verified";
    }

    private static string Base64Url(string json) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>
/// Answers in order, one queued response per request, and records what was asked: the device flow is a sequence, so a
/// handler that answers everything the same way cannot express "pending, then approved".
/// </summary>
internal sealed class ScriptedChatGptHandler : ScriptedAuthHandler<RecordedAuthRequest>
{
    public static ScriptedChatGptHandler Answering(HttpStatusCode status, string body)
    {
        var handler = new ScriptedChatGptHandler();
        handler.Then(status, body);
        return handler;
    }

    protected override RecordedAuthRequest Record(HttpRequestMessage request, string body) =>
        new(
            request.RequestUri,
            body,
            [.. request.Headers.Accept.Select(static accept => accept.MediaType ?? string.Empty)],
            request.Headers.ToDictionary(static header => header.Key, static header => string.Join(',', header.Value), StringComparer.OrdinalIgnoreCase),
            request.Content?.Headers.ContentType?.MediaType ?? string.Empty);

    protected override string ContentTypeFor(string body) =>
        body.StartsWith("data:", StringComparison.Ordinal) ? "text/event-stream" : "application/json";
}
