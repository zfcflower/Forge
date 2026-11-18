using Forge.App.Git;
using Forge.Workspace;
using Xunit;

namespace Forge.App.Tests;

public sealed class RemotePairingTests
{
    private static RemotePairing Pairing()
    {
        var git = new GitCli();
        return new RemotePairing(git, new ConfiguredGh(() => null, git, () => TimeSpan.FromSeconds(5)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("-oProxyCommand=x")]
    [InlineData("https://host/a b.git")]
    [InlineData("ext::sh -c touch% /tmp/x")]
    [InlineData("ext::sh")]
    [InlineData("Fd::17")]
    [InlineData("https://user:secret@host/a/b.git")]
    [InlineData("http://token:x-oauth-basic@host/a/b.git")]
    public async Task An_address_that_could_run_a_command_or_leak_a_secret_is_refused_before_git_is_asked(string url)
    {
        var result = await Pairing().PairByUrlAsync(Path.GetTempPath(), url, CancellationToken.None);

        Assert.False(result.Success);
        Assert.DoesNotContain("secret", result.Message, StringComparison.Ordinal);
    }
}
