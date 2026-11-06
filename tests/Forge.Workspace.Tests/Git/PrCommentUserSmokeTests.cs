using Xunit;

namespace Forge.Workspace.Tests.Git;

public sealed class PrCommentUserSmokeTests
{
    [Fact]
    public async Task The_login_is_read_from_gh_api_user_for_the_origin_host()
    {
        using var fake = new SmokeFakeGh(stdout: "octo\n");

        var login = await PrCommentUser.FromGh(fake.Cli)(fake.WorkingDirectory, CancellationToken.None);

        Assert.Equal("octo", login);
        var call = Assert.Single(fake.Calls);
        Assert.StartsWith("api user", call);
        Assert.Contains("--jq .login", call);
    }

    [Fact]
    public async Task The_login_is_read_fresh_every_time_so_a_switched_account_is_noticed()
    {
        using var fake = new SmokeFakeGh(stdout: "octo\n");
        var resolve = PrCommentUser.FromGh(fake.Cli);

        var first = await resolve(fake.WorkingDirectory, CancellationToken.None);
        fake.SetOutputFor("api", "hubot\n");
        var second = await resolve(fake.WorkingDirectory, CancellationToken.None);

        Assert.Equal("octo", first);
        Assert.Equal("hubot", second);
    }

    [Fact]
    public async Task A_failing_gh_yields_null_instead_of_throwing()
    {
        using var fake = new SmokeFakeGh(exitCode: 1, stderr: "gh: not logged in\n");

        var login = await PrCommentUser.FromGh(fake.Cli)(fake.WorkingDirectory, CancellationToken.None);

        Assert.Null(login);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \n")]
    [InlineData("two words\n")]
    [InlineData("a\nb\n")]
    public async Task Output_that_is_not_a_single_login_yields_null(string output)
    {
        using var fake = new SmokeFakeGh(stdout: output);

        var login = await PrCommentUser.FromGh(fake.Cli)(fake.WorkingDirectory, CancellationToken.None);

        Assert.Null(login);
    }

    [Fact]
    public async Task A_missing_gh_executable_yields_null()
    {
        using var temp = new TempDirectory();

        var login = await PrCommentUser.FromGh(new GhCli(temp.Combine("no-such-gh")))(temp.Path, CancellationToken.None);

        Assert.Null(login);
    }

    [Fact]
    public async Task A_resolver_backed_by_gh_recognises_the_markers_of_an_earlier_run()
    {
        using var fake = new SmokeFakeGh(stdout: "octo\n");
        using var temp = new TempDirectory();
        var platform = new FakePrPlatform();
        platform.Threads.Add(PrCommentHarness.Thread(
            "PRRT_1",
            comments:
            [
                PrCommentHarness.Comment("c0", "reviewer", "please fix"),
                PrCommentHarness.Comment("c1", "octo", PrReplyLedger.FormatReply(PrReplyVerdict.PushBack, null, "Out of scope."), 1),
            ]));
        var tools = new PrCommentTools(
            new GitCli(), platform, new GitToolSettings(), new PrReplyLedger(), null, PrCommentUser.FromGh(fake.Cli));

        var result = await PrCommentHarness.InvokeAsync(tools.Resolve, temp.Path, """{"thread_id":"PRRT_1"}""");

        Assert.False(result.IsError, result.Content);
        Assert.Equal("PRRT_1", Assert.Single(platform.Resolves));
    }

    [Fact]
    public async Task A_cancelled_lookup_throws()
    {
        using var fake = new SmokeFakeGh(stdout: "octo\n");
        using var source = new CancellationTokenSource();
        source.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => PrCommentUser.FromGh(fake.Cli)(fake.WorkingDirectory, source.Token));
    }
}
