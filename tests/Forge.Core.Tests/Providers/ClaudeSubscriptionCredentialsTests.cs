using System.Net;
using System.Text.Json;
using Forge.Abstractions;
using Forge.Core.Providers;
using Xunit;

namespace Forge.Core.Tests.Providers;

/// <summary>A clock the test advances by hand, so nothing here waits or depends on the wall clock.</summary>
internal sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;

    public void Advance(TimeSpan delta) => Now += delta;
}

/// <summary>A scratch directory that is removed after the test, for files the code under test reads and rewrites.</summary>
internal sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Root = Path.Combine(Path.GetTempPath(), "forge-core-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public string WriteFile(string name, string content)
    {
        var path = Path.Combine(Root, name);
        File.WriteAllText(path, content);
        return path;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: a leftover scratch directory must not fail a test run.
        }
    }
}

public class ClaudeSubscriptionCredentialsTests
{
    private static readonly DateTimeOffset Now = new(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);

    private const string AccessToken = "sk-ant-oat01-access";
    private const string RefreshToken = "sk-ant-ort01-refresh";
    private const string RefreshedAccessToken = "sk-ant-oat01-refreshed";
    private const string RefreshedRefreshToken = "sk-ant-ort01-refreshed";
    private const string PreservedRootField = ",\"mcpOAuth\":{\"srv\":{\"accessToken\":\"other-server\"}}";

    [Fact]
    public async Task Reads_the_signed_in_subscription()
    {
        using var temp = new TempDirectory();
        var path = temp.WriteFile(
            ".credentials.json",
            Credentials(expiresAt: (Now + TimeSpan.FromHours(1)).ToUnixTimeMilliseconds()));
        var handler = new FakeHttpMessageHandler();

        var credentials = new ClaudeSubscriptionCredentials(path, handler.CreateClient(), new ManualTimeProvider(Now));

        var subscription = await credentials.ReadAsync(CancellationToken.None);

        Assert.True(subscription.IsSignedIn);
        Assert.Equal(AccessToken, subscription.AccessToken);
        Assert.Equal(RefreshToken, subscription.RefreshToken);
        Assert.Equal(Now + TimeSpan.FromHours(1), subscription.ExpiresAt);
        Assert.Equal("max", subscription.SubscriptionType);
        Assert.Equal(new[] { "user:inference", "user:profile" }, subscription.Scopes);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task Missing_file_is_not_signed_in()
    {
        using var temp = new TempDirectory();
        var handler = new FakeHttpMessageHandler();
        var credentials = new ClaudeSubscriptionCredentials(
            Path.Combine(temp.Root, "absent.json"),
            handler.CreateClient(),
            new ManualTimeProvider(Now));

        var subscription = await credentials.ReadAsync(CancellationToken.None);

        Assert.False(subscription.IsSignedIn);
        Assert.Same(ClaudeSubscription.None, subscription);
        Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"claudeAiOauth":{}}""")]
    [InlineData("""{"claudeAiOauth":{"accessToken":""}}""")]
    [InlineData("""{"unrelated":true}""")]
    public async Task Absent_oauth_and_empty_token_are_not_signed_in(string json)
    {
        using var temp = new TempDirectory();
        var path = temp.WriteFile(".credentials.json", json);
        var handler = new FakeHttpMessageHandler();
        var credentials = new ClaudeSubscriptionCredentials(path, handler.CreateClient(), new ManualTimeProvider(Now));

        var subscription = await credentials.ReadAsync(CancellationToken.None);

        Assert.False(subscription.IsSignedIn);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task Malformed_json_throws_without_exposing_the_token()
    {
        using var temp = new TempDirectory();
        // Truncated on purpose: the access token is inside the unparsable text.
        var path = temp.WriteFile(".credentials.json", $$"""{"claudeAiOauth":{"accessToken":"{{AccessToken}}","scope""");
        var handler = new FakeHttpMessageHandler();
        var credentials = new ClaudeSubscriptionCredentials(path, handler.CreateClient(), new ManualTimeProvider(Now));

        var exception = await Assert.ThrowsAsync<ProviderException>(() => credentials.ReadAsync(CancellationToken.None));

        Assert.False(exception.Retryable);
        Assert.DoesNotContain(AccessToken, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Fresh_token_is_not_refreshed()
    {
        using var temp = new TempDirectory();
        var path = temp.WriteFile(
            ".credentials.json",
            Credentials(expiresAt: (Now + TimeSpan.FromMinutes(10)).ToUnixTimeMilliseconds()));
        var handler = new FakeHttpMessageHandler();
        var credentials = new ClaudeSubscriptionCredentials(path, handler.CreateClient(), new ManualTimeProvider(Now));

        var subscription = await credentials.ReadAsync(CancellationToken.None);

        Assert.Equal(AccessToken, subscription.AccessToken);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task Token_expiring_within_five_minutes_is_refreshed_and_written_back()
    {
        using var temp = new TempDirectory();
        var path = temp.WriteFile(
            ".credentials.json",
            Credentials(
                expiresAt: (Now + TimeSpan.FromMinutes(4)).ToUnixTimeMilliseconds(),
                extra: PreservedRootField));
        var handler = new FakeHttpMessageHandler().EnqueueStatus(
            HttpStatusCode.OK,
            $$"""{"access_token":"{{RefreshedAccessToken}}","refresh_token":"{{RefreshedRefreshToken}}","expires_in":28800}""");
        var credentials = new ClaudeSubscriptionCredentials(path, handler.CreateClient(), new ManualTimeProvider(Now));

        var subscription = await credentials.ReadAsync(CancellationToken.None);

        Assert.Equal(RefreshedAccessToken, subscription.AccessToken);
        Assert.Equal(RefreshedRefreshToken, subscription.RefreshToken);
        Assert.Equal(Now + TimeSpan.FromHours(8), subscription.ExpiresAt);
        Assert.Equal("max", subscription.SubscriptionType);
        Assert.Equal(new[] { "user:inference", "user:profile" }, subscription.Scopes);

        var request = handler.Last;
        Assert.Equal(ClaudeSubscriptionCredentials.TokenUrl, request.Uri.ToString());
        Assert.Equal("axios/1.8.4", request.Header("User-Agent"));
        Assert.Contains("application/json", request.ContentHeaders["Content-Type"], StringComparison.Ordinal);

        using var body = JsonDocument.Parse(request.Body!);
        var root = body.RootElement;
        Assert.Equal("refresh_token", root.GetProperty("grant_type").GetString());
        Assert.Equal(RefreshToken, root.GetProperty("refresh_token").GetString());
        Assert.Equal(ClaudeSubscriptionCredentials.ClientId, root.GetProperty("client_id").GetString());

        using var rewritten = JsonDocument.Parse(File.ReadAllText(path));
        var oauth = rewritten.RootElement.GetProperty("claudeAiOauth");
        Assert.Equal(RefreshedAccessToken, oauth.GetProperty("accessToken").GetString());
        Assert.Equal(RefreshedRefreshToken, oauth.GetProperty("refreshToken").GetString());
        Assert.Equal(
            (Now + TimeSpan.FromHours(8)).ToUnixTimeMilliseconds(),
            oauth.GetProperty("expiresAt").GetInt64());
        Assert.Equal("max", oauth.GetProperty("subscriptionType").GetString());
        Assert.Equal(2, oauth.GetProperty("scopes").GetArrayLength());

        // Fields Forge does not own are untouched.
        Assert.Equal(
            "other-server",
            rewritten.RootElement.GetProperty("mcpOAuth").GetProperty("srv").GetProperty("accessToken").GetString());
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public async Task Expired_token_is_refreshed_and_a_missing_refresh_token_keeps_the_old_one()
    {
        using var temp = new TempDirectory();
        var path = temp.WriteFile(
            ".credentials.json",
            Credentials(expiresAt: (Now - TimeSpan.FromHours(1)).ToUnixTimeMilliseconds()));
        var handler = new FakeHttpMessageHandler().EnqueueStatus(
            HttpStatusCode.OK,
            $$"""{"access_token":"{{RefreshedAccessToken}}","expires_in":600}""");
        var credentials = new ClaudeSubscriptionCredentials(path, handler.CreateClient(), new ManualTimeProvider(Now));

        var subscription = await credentials.ReadAsync(CancellationToken.None);

        Assert.Equal(RefreshedAccessToken, subscription.AccessToken);
        Assert.Equal(RefreshToken, subscription.RefreshToken);
        Assert.Equal(Now + TimeSpan.FromMinutes(10), subscription.ExpiresAt);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task Unknown_expiry_with_a_refresh_token_is_refreshed_once()
    {
        using var temp = new TempDirectory();
        var path = temp.WriteFile(".credentials.json", Credentials());
        var handler = new FakeHttpMessageHandler().EnqueueStatus(
            HttpStatusCode.OK,
            $$"""{"access_token":"{{RefreshedAccessToken}}","expires_in":600}""");
        var credentials = new ClaudeSubscriptionCredentials(path, handler.CreateClient(), new ManualTimeProvider(Now));

        var subscription = await credentials.ReadAsync(CancellationToken.None);

        Assert.Equal(RefreshedAccessToken, subscription.AccessToken);
        Assert.Equal(Now + TimeSpan.FromMinutes(10), subscription.ExpiresAt);
    }

    [Fact]
    public async Task Expired_without_a_refresh_token_returns_the_stale_token()
    {
        using var temp = new TempDirectory();
        var path = temp.WriteFile(
            ".credentials.json",
            Credentials(refreshToken: null, expiresAt: (Now - TimeSpan.FromHours(1)).ToUnixTimeMilliseconds()));
        var handler = new FakeHttpMessageHandler();
        var credentials = new ClaudeSubscriptionCredentials(path, handler.CreateClient(), new ManualTimeProvider(Now));

        var subscription = await credentials.ReadAsync(CancellationToken.None);

        Assert.True(subscription.IsSignedIn);
        Assert.Equal(AccessToken, subscription.AccessToken);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task Expiry_written_in_seconds_is_read_as_seconds()
    {
        using var temp = new TempDirectory();
        var seconds = (Now + TimeSpan.FromHours(1)).ToUnixTimeSeconds();
        var path = temp.WriteFile(".credentials.json", Credentials(expiresAt: seconds));
        var handler = new FakeHttpMessageHandler();
        var credentials = new ClaudeSubscriptionCredentials(path, handler.CreateClient(), new ManualTimeProvider(Now));

        var subscription = await credentials.ReadAsync(CancellationToken.None);

        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(seconds), subscription.ExpiresAt);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task Failed_refresh_throws_without_the_token()
    {
        using var temp = new TempDirectory();
        var path = temp.WriteFile(
            ".credentials.json",
            Credentials(expiresAt: (Now - TimeSpan.FromHours(1)).ToUnixTimeMilliseconds()));
        var handler = new FakeHttpMessageHandler().EnqueueStatus(
            HttpStatusCode.BadRequest,
            $$"""{"error":"invalid_grant","refresh_token":"{{RefreshToken}}"}""");
        var credentials = new ClaudeSubscriptionCredentials(path, handler.CreateClient(), new ManualTimeProvider(Now));

        var exception = await Assert.ThrowsAsync<ProviderException>(() => credentials.ReadAsync(CancellationToken.None));

        Assert.False(exception.Retryable);
        Assert.Equal(400, exception.StatusCode);
        Assert.DoesNotContain(RefreshToken, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(AccessToken, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Refresh_response_without_a_token_throws_non_retryable()
    {
        using var temp = new TempDirectory();
        var path = temp.WriteFile(
            ".credentials.json",
            Credentials(expiresAt: (Now - TimeSpan.FromHours(1)).ToUnixTimeMilliseconds()));
        var handler = new FakeHttpMessageHandler().EnqueueStatus(HttpStatusCode.OK, """{"token_type":"Bearer"}""");
        var credentials = new ClaudeSubscriptionCredentials(path, handler.CreateClient(), new ManualTimeProvider(Now));

        var exception = await Assert.ThrowsAsync<ProviderException>(() => credentials.ReadAsync(CancellationToken.None));

        Assert.False(exception.Retryable);
    }

    [Fact]
    public async Task Unwritable_credentials_file_still_returns_the_refreshed_token()
    {
        using var temp = new TempDirectory();
        var path = temp.WriteFile(
            ".credentials.json",
            Credentials(expiresAt: (Now - TimeSpan.FromHours(1)).ToUnixTimeMilliseconds()));
        // A directory where the temporary file would go makes the atomic replace fail without touching the original.
        Directory.CreateDirectory(path + ".tmp");
        var handler = new FakeHttpMessageHandler().EnqueueStatus(
            HttpStatusCode.OK,
            $$"""{"access_token":"{{RefreshedAccessToken}}","refresh_token":"{{RefreshedRefreshToken}}","expires_in":28800}""");
        var credentials = new ClaudeSubscriptionCredentials(path, handler.CreateClient(), new ManualTimeProvider(Now));

        var subscription = await credentials.ReadAsync(CancellationToken.None);

        Assert.Equal(RefreshedAccessToken, subscription.AccessToken);
        using var unchanged = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal(
            AccessToken,
            unchanged.RootElement.GetProperty("claudeAiOauth").GetProperty("accessToken").GetString());
    }

    [Fact]
    public void Credentials_never_print_their_token()
    {
        var subscription = new ClaudeSubscription(AccessToken, RefreshToken, Now, "max", ["user:inference"]);

        Assert.DoesNotContain(AccessToken, subscription.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(RefreshToken, subscription.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Default_path_and_oauth_constants_are_the_claude_code_ones()
    {
        Assert.EndsWith(
            Path.Combine(".claude", ".credentials.json"),
            ClaudeSubscriptionCredentials.DefaultCredentialsPath,
            StringComparison.Ordinal);
        Assert.Equal("https://platform.claude.com/v1/oauth/token", ClaudeSubscriptionCredentials.TokenUrl);
        Assert.Equal("9d1c250a-e61b-44d9-88ed-5944d1962f5e", ClaudeSubscriptionCredentials.ClientId);
        Assert.Equal(
            "You are Claude Code, Anthropic's official CLI for Claude.",
            ClaudeSubscriptionCredentials.IdentityPreamble);
        Assert.Contains("oauth-2025-04-20", ClaudeSubscriptionCredentials.Betas);
    }

    private static string Credentials(
        string? accessToken = AccessToken,
        string? refreshToken = RefreshToken,
        long? expiresAt = null,
        string? subscriptionType = "max",
        string? scopes = """["user:inference","user:profile"]""",
        string extra = "")
    {
        var fields = new List<string>();
        if (accessToken is not null)
        {
            fields.Add($"\"accessToken\":\"{accessToken}\"");
        }

        if (refreshToken is not null)
        {
            fields.Add($"\"refreshToken\":\"{refreshToken}\"");
        }

        if (expiresAt is not null)
        {
            fields.Add($"\"expiresAt\":{expiresAt.Value}");
        }

        if (subscriptionType is not null)
        {
            fields.Add($"\"subscriptionType\":\"{subscriptionType}\"");
        }

        if (scopes is not null)
        {
            fields.Add($"\"scopes\":{scopes}");
        }

        return "{\"claudeAiOauth\":{" + string.Join(',', fields) + "}" + extra + "}";
    }
}
