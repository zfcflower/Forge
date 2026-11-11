using Forge.App.Providers;
using Xunit;

namespace Forge.App.Tests.Providers;

/// <summary>
/// The sign-in state a provider row shows must match what a turn will find. For Claude that is the local CLI login: a file
/// that exists but holds no token reads as "not signed in", not as connected.
/// </summary>
public sealed class ProviderCredentialsTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "forge-provider-credentials-" + Guid.NewGuid().ToString("N"));

    public ProviderCredentialsTests() => Directory.CreateDirectory(root);

    [Fact]
    public void A_credentials_file_with_an_access_token_reads_as_signed_in()
    {
        var path = WriteCredentials("""{ "claudeAiOauth": { "accessToken": "sk-ant-oat01-test", "expiresAt": 1 } }""");

        Assert.True(Credentials(path).HasClaudeLogin());
    }

    [Fact]
    public void A_credentials_file_without_a_token_or_that_is_malformed_reads_as_not_signed_in()
    {
        Assert.False(Credentials(WriteCredentials("""{ "claudeAiOauth": { "accessToken": "  " } }""")).HasClaudeLogin());
        Assert.False(Credentials(WriteCredentials("""{ "unrelated": true }""")).HasClaudeLogin());
        Assert.False(Credentials(WriteCredentials("{ not json")).HasClaudeLogin());
        Assert.False(Credentials(Path.Combine(root, "missing.json")).HasClaudeLogin());
    }

    [Fact]
    public void A_copilot_provider_without_a_login_is_not_signed_in()
    {
        var credentials = Credentials(Path.Combine(root, "missing.json"));

        Assert.False(credentials.HasCopilotLogin("copilot"));
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

    private ProviderCredentials Credentials(string claudePath) =>
        new(new HttpClient { Timeout = Timeout.InfiniteTimeSpan }, new ProviderSecretStore(root), claudePath);

    private string WriteCredentials(string json)
    {
        var path = Path.Combine(root, "credentials.json");
        File.WriteAllText(path, json);
        return path;
    }
}
