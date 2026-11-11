using System.Net;
using Forge.Abstractions;
using Forge.Core.Providers;
using Xunit;

namespace Forge.Core.Tests.Providers;

public class CopilotAuthenticationTests
{
    private static readonly DateTimeOffset Now = new(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);

    private const string UserToken = "ghu_test_user_token";
    private const string CopilotToken = "tid=test-session-token";

    [Fact]
    public async Task Start_posts_the_device_code_request()
    {
        var handler = new FakeHttpMessageHandler().EnqueueStatus(
            HttpStatusCode.OK,
            """{"device_code":"dc-1","user_code":"ABCD-1234","verification_uri":"https://github.com/login/device","expires_in":900,"interval":5}""");
        var auth = new CopilotAuthentication(handler.CreateClient(), new ManualTimeProvider(Now));

        var code = await auth.StartAsync(enterpriseHost: null, CancellationToken.None);

        Assert.Equal("dc-1", code.DeviceCode);
        Assert.Equal("ABCD-1234", code.UserCode);
        Assert.Equal("https://github.com/login/device", code.VerificationUri);
        Assert.Equal(5, code.IntervalSeconds);
        Assert.Equal(Now + TimeSpan.FromSeconds(900), code.ExpiresAt);

        var request = handler.Last;
        Assert.Equal("https://github.com/login/device/code", request.Uri.ToString());
        Assert.Equal("application/json", request.Header("Accept"));
        Assert.Equal("Forge/1.0", request.Header("Editor-Version"));
        Assert.Equal("forge/1.0", request.Header("Editor-Plugin-Version"));
        Assert.Contains("application/x-www-form-urlencoded", request.ContentHeaders["Content-Type"], StringComparison.Ordinal);
        Assert.Contains("client_id=Iv1.b507a08c87ecfe98", request.Body!, StringComparison.Ordinal);
        Assert.Contains("scope=read%3Auser", request.Body!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Start_defaults_interval_and_lifetime_when_github_omits_them()
    {
        var handler = new FakeHttpMessageHandler().EnqueueStatus(
            HttpStatusCode.OK,
            """{"device_code":"dc-1","user_code":"ABCD-1234","verification_uri":"https://github.com/login/device"}""");
        var auth = new CopilotAuthentication(handler.CreateClient(), new ManualTimeProvider(Now));

        var code = await auth.StartAsync(enterpriseHost: null, CancellationToken.None);

        Assert.Equal(5, code.IntervalSeconds);
        Assert.Equal(Now + TimeSpan.FromMinutes(15), code.ExpiresAt);
    }

    [Fact]
    public async Task Start_rejects_a_device_code_response_without_codes()
    {
        var handler = new FakeHttpMessageHandler().EnqueueStatus(HttpStatusCode.OK, """{"error":"device_flow_disabled"}""");
        var auth = new CopilotAuthentication(handler.CreateClient(), new ManualTimeProvider(Now));

        var exception = await Assert.ThrowsAsync<ProviderException>(
            () => auth.StartAsync(null, CancellationToken.None));

        Assert.False(exception.Retryable);
    }

    [Theory]
    [InlineData("authorization_pending")]
    [InlineData("slow_down")]
    public async Task Poll_returns_null_while_the_user_has_not_finished(string error)
    {
        var handler = new FakeHttpMessageHandler().EnqueueStatus(HttpStatusCode.OK, $$"""{"error":"{{error}}"}""");
        var auth = new CopilotAuthentication(handler.CreateClient(), new ManualTimeProvider(Now));

        var token = await auth.PollAsync(Pending(), enterpriseHost: null, CancellationToken.None);

        Assert.Null(token);

        var request = handler.Last;
        Assert.Equal("https://github.com/login/oauth/access_token", request.Uri.ToString());
        Assert.Contains("device_code=dc-1", request.Body!, StringComparison.Ordinal);
        Assert.Contains("grant_type=urn%3Aietf%3Aparams%3Aoauth%3Agrant-type%3Adevice_code", request.Body!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Poll_returns_the_github_user_token()
    {
        var handler = new FakeHttpMessageHandler().EnqueueStatus(
            HttpStatusCode.OK,
            $$"""{"access_token":"{{UserToken}}","token_type":"bearer","scope":"read:user"}""");
        var auth = new CopilotAuthentication(handler.CreateClient(), new ManualTimeProvider(Now));

        var token = await auth.PollAsync(Pending(), enterpriseHost: null, CancellationToken.None);

        Assert.Equal(UserToken, token);
    }

    [Theory]
    [InlineData("access_denied")]
    [InlineData("expired_token")]
    [InlineData("incorrect_device_code")]
    public async Task Poll_throws_on_a_terminal_error_and_names_the_code(string error)
    {
        var handler = new FakeHttpMessageHandler().EnqueueStatus(
            HttpStatusCode.OK,
            $$"""{"error":"{{error}}","error_description":"GitHub says no"}""");
        var auth = new CopilotAuthentication(handler.CreateClient(), new ManualTimeProvider(Now));

        var exception = await Assert.ThrowsAsync<ProviderException>(
            () => auth.PollAsync(Pending(), enterpriseHost: null, CancellationToken.None));

        Assert.False(exception.Retryable);
        Assert.Contains(error, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Exchange_sends_the_headers_the_token_is_bound_to()
    {
        var expiresAt = (Now + TimeSpan.FromMinutes(30)).ToUnixTimeSeconds();
        var body = "{\"token\":\"" + CopilotToken + "\",\"expires_at\":" + expiresAt
            + ",\"endpoints\":{\"api\":\"https://api.githubcopilot.com\"}}";
        var handler = new FakeHttpMessageHandler().EnqueueStatus(HttpStatusCode.OK, body);
        var auth = new CopilotAuthentication(handler.CreateClient(), new ManualTimeProvider(Now));

        var session = await auth.ExchangeAsync(UserToken, CancellationToken.None);

        Assert.Equal(CopilotToken, session.Token);
        Assert.Equal("https://api.githubcopilot.com", session.ApiBaseUrl);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(expiresAt), session.ExpiresAt);

        var request = handler.Last;
        Assert.Equal("https://api.github.com/copilot_internal/v2/token", request.Uri.ToString());
        Assert.Equal("token " + UserToken, request.Header("Authorization"));
        Assert.Equal(CopilotAuthentication.IntegrationId, request.Header("Copilot-Integration-Id"));
        Assert.Equal("copilot-chat", request.Header("Copilot-Integration-Id"));
        Assert.Equal("Forge/1.0", request.Header("Editor-Version"));
        Assert.Equal("Forge/1.0", request.Header("Editor-Plugin-Version"));
        Assert.Equal("application/json", request.Header("Accept"));
    }

    [Fact]
    public async Task Exchange_uses_the_api_host_github_reports()
    {
        var handler = new FakeHttpMessageHandler().EnqueueStatus(
            HttpStatusCode.OK,
            """{"token":"tid=1","expires_at":1900000000,"endpoints":{"api":"https://api.individual.githubcopilot.com/"}}""");
        var auth = new CopilotAuthentication(handler.CreateClient(), new ManualTimeProvider(Now));

        var session = await auth.ExchangeAsync(UserToken, CancellationToken.None);

        Assert.Equal("https://api.individual.githubcopilot.com", session.ApiBaseUrl);
    }

    [Fact]
    public async Task Exchange_falls_back_to_the_default_api_host()
    {
        var handler = new FakeHttpMessageHandler().EnqueueStatus(HttpStatusCode.OK, """{"token":"tid=1","expires_at":1900000000}""");
        var auth = new CopilotAuthentication(handler.CreateClient(), new ManualTimeProvider(Now));

        var session = await auth.ExchangeAsync(UserToken, CancellationToken.None);

        Assert.Equal(CopilotAuthentication.DefaultApiHost, session.ApiBaseUrl);
        Assert.Equal("https://api.githubcopilot.com", session.ApiBaseUrl);
    }

    [Fact]
    public async Task Exchange_failure_does_not_echo_the_user_token()
    {
        var handler = new FakeHttpMessageHandler().EnqueueStatus(
            HttpStatusCode.Unauthorized,
            $$"""{"message":"Bad credentials","token":"{{UserToken}}"}""");
        var auth = new CopilotAuthentication(handler.CreateClient(), new ManualTimeProvider(Now));

        var exception = await Assert.ThrowsAsync<ProviderException>(
            () => auth.ExchangeAsync(UserToken, CancellationToken.None));

        Assert.Equal(401, exception.StatusCode);
        Assert.DoesNotContain(UserToken, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Exchange_without_a_token_is_non_retryable()
    {
        var handler = new FakeHttpMessageHandler().EnqueueStatus(HttpStatusCode.OK, """{"expires_at":1900000000}""");
        var auth = new CopilotAuthentication(handler.CreateClient(), new ManualTimeProvider(Now));

        var exception = await Assert.ThrowsAsync<ProviderException>(
            () => auth.ExchangeAsync(UserToken, CancellationToken.None));

        Assert.False(exception.Retryable);
    }

    [Fact]
    public async Task Exchange_with_an_unknown_lifetime_is_never_valid()
    {
        var handler = new FakeHttpMessageHandler().EnqueueStatus(HttpStatusCode.OK, """{"token":"tid=1"}""");
        var auth = new CopilotAuthentication(handler.CreateClient(), new ManualTimeProvider(Now));

        var session = await auth.ExchangeAsync(UserToken, CancellationToken.None);

        Assert.False(session.IsValidAt(Now));
    }

    [Fact]
    public void A_session_is_invalid_inside_the_last_minute()
    {
        var expiresAt = Now + TimeSpan.FromMinutes(5);
        var session = new CopilotSession(CopilotToken, CopilotAuthentication.DefaultApiHost, expiresAt);

        Assert.True(session.IsValidAt(Now));
        Assert.True(session.IsValidAt(expiresAt - TimeSpan.FromSeconds(61)));
        Assert.False(session.IsValidAt(expiresAt - TimeSpan.FromSeconds(30)));
        Assert.False(session.IsValidAt(expiresAt));
        Assert.False(session.IsValidAt(expiresAt + TimeSpan.FromHours(1)));
        Assert.False(new CopilotSession(string.Empty, CopilotAuthentication.DefaultApiHost, expiresAt).IsValidAt(Now));
    }

    [Fact]
    public void Sessions_and_device_codes_never_print_their_tokens()
    {
        var session = new CopilotSession(CopilotToken, CopilotAuthentication.DefaultApiHost, Now);
        var code = new CopilotDeviceCode("dc-1", "ABCD-1234", "https://github.com/login/device", 5, Now);

        Assert.DoesNotContain(CopilotToken, session.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("dc-1", code.ToString(), StringComparison.Ordinal);
    }

    private static CopilotDeviceCode Pending() =>
        new("dc-1", "ABCD-1234", "https://github.com/login/device", 5, Now + TimeSpan.FromMinutes(15));
}
