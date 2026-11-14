using Forge.Workspace;
using Xunit;

namespace Forge.Workspace.Tests.Git;

/// <summary>
/// `gh pr ready` through <see cref="GitHubPlatform"/> against a scripted stand-in for the gh executable: the argument the
/// panel's 标记可评审 runs, the idempotent "already ready" answer, a missing pull request, and the refusal to call gh for a
/// number that cannot be one. The interface's own default (a platform that cannot do this) is pinned next to the merge
/// default it mirrors.
/// </summary>
public sealed class GitHubPlatformReadyTests
{
    private const string ReadyOutput = "✓ Pull request acme/forge#7 is marked as ready for review\n";

    [Fact]
    public async Task Ready_runs_gh_pr_ready_for_the_number_and_reports_it()
    {
        using var fake = new SmokeFakeGh(stdout: ReadyOutput);
        var platform = new GitHubPlatform(fake.Cli);

        var result = await platform.MarkPullRequestReadyAsync(fake.WorkingDirectory, 7);

        Assert.True(result.Success, result.Status.Message + result.Status.StandardError);
        Assert.Equal(7, result.Value!.Number);
        Assert.Contains("pr ready 7", Assert.Single(fake.Calls));
    }

    [Fact]
    public async Task Ready_keeps_the_number_it_asked_for_when_gh_prints_no_address()
    {
        using var fake = new SmokeFakeGh(stdout: string.Empty);
        var platform = new GitHubPlatform(fake.Cli);

        var result = await platform.MarkPullRequestReadyAsync(fake.WorkingDirectory, 12);

        Assert.True(result.Success, result.Status.Message);
        Assert.Equal(12, result.Value!.Number);
        Assert.Equal(string.Empty, result.Value.Url);
    }

    [Fact]
    public async Task Ready_is_idempotent_when_gh_says_the_pull_request_already_is_ready()
    {
        // gh (and GitHub) refuse the second call with a non-zero exit code; that is the state the caller asked for.
        using var fake = new SmokeFakeGh(
            exitCode: 1,
            stderr: "failed to mark pull request as ready: pull request acme/forge#7 is already ready for review\n");
        var platform = new GitHubPlatform(fake.Cli);

        var result = await platform.MarkPullRequestReadyAsync(fake.WorkingDirectory, 7);

        Assert.True(result.Success, result.Status.Message + result.Status.StandardError);
        Assert.Equal(7, result.Value!.Number);
    }

    [Fact]
    public async Task Ready_reports_a_missing_pull_request_as_such()
    {
        using var fake = new SmokeFakeGh(
            exitCode: 1,
            stderr: "GraphQL: Could not resolve to a PullRequest with the number of 404. (repository.pullRequest)\n");
        var platform = new GitHubPlatform(fake.Cli);

        var result = await platform.MarkPullRequestReadyAsync(fake.WorkingDirectory, 404);

        Assert.False(result.Success);
        Assert.Equal(PrFailureReason.PullRequestNotFound, result.Reason);
    }

    [Fact]
    public async Task Ready_reports_a_missing_platform_cli_instead_of_throwing()
    {
        var platform = new GitHubPlatform(new GhCli(Path.Combine(Path.GetTempPath(), "forge-no-gh-" + Guid.NewGuid().ToString("n"))));

        var result = await platform.MarkPullRequestReadyAsync(Path.GetTempPath(), 7);

        Assert.False(result.Success);
        Assert.Equal(PrFailureReason.PlatformNotInstalled, result.Reason);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public async Task Ready_refuses_a_number_that_cannot_be_one_without_calling_gh(int number)
    {
        using var fake = new SmokeFakeGh();
        var platform = new GitHubPlatform(fake.Cli);

        var result = await platform.MarkPullRequestReadyAsync(fake.WorkingDirectory, number);

        Assert.False(result.Success);
        Assert.Empty(fake.Calls);
    }

    [Fact]
    public async Task A_platform_that_cannot_mark_ready_refuses_instead_of_pretending()
    {
        IPrPlatform platform = new ChecksOnlyPlatform();

        var result = await platform.MarkPullRequestReadyAsync("/work", 7);

        Assert.False(result.Success);
        Assert.Contains("cannot mark a pull request ready", result.Status.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The interface's defaults: a host that reads checks but cannot change the pull request.</summary>
    private sealed class ChecksOnlyPlatform : IPrPlatform
    {
        public string Name => "Stub";

        public Task<PrPlatformAvailability> GetAvailabilityAsync(string workingDirectory, CancellationToken ct = default) =>
            Task.FromResult(new PrPlatformAvailability(PrPlatformState.Ready, string.Empty));

        public Task<PrResult<PullRequestInfo>> ViewPullRequestAsync(
            string workingDirectory,
            string? selector = null,
            CancellationToken ct = default) =>
            Task.FromResult(PrResult<PullRequestInfo>.Fail(GitRemoteFailureKind.Other, "not implemented"));

        public Task<PrResult<PrReference>> CreatePullRequestAsync(
            string workingDirectory,
            string title,
            string body,
            string baseBranch,
            string headBranch,
            bool draft = false,
            CancellationToken ct = default) =>
            Task.FromResult(PrResult<PrReference>.Fail(GitRemoteFailureKind.Other, "not implemented"));

        public Task<PrResult<PrReference>> EditPullRequestAsync(
            string workingDirectory,
            int number,
            string? title = null,
            string? body = null,
            CancellationToken ct = default) =>
            Task.FromResult(PrResult<PrReference>.Fail(GitRemoteFailureKind.Other, "not implemented"));

        public Task<PrResult<PrChecksSnapshot>> GetChecksAsync(string workingDirectory, int number, CancellationToken ct = default) =>
            Task.FromResult(PrResult<PrChecksSnapshot>.Ok(new PrChecksSnapshot([])));

        public Task<PrResult<PrMergeState>> GetMergeStateAsync(string workingDirectory, int number, CancellationToken ct = default) =>
            Task.FromResult(PrResult<PrMergeState>.Fail(GitRemoteFailureKind.Other, "not implemented"));

        public Task<PrResult<PrText>> GetFailedJobLogAsync(
            string workingDirectory,
            long? runId,
            long jobId,
            CancellationToken ct = default) =>
            Task.FromResult(PrResult<PrText>.Fail(GitRemoteFailureKind.Other, "not implemented"));

        public Task<PrResult<IReadOnlyList<PrReviewThread>>> GetReviewThreadsAsync(
            string workingDirectory,
            int number,
            CancellationToken ct = default) =>
            Task.FromResult(PrResult<IReadOnlyList<PrReviewThread>>.Fail(GitRemoteFailureKind.Other, "not implemented"));

        public Task<PrResult<PrText>> GetPullRequestDiffAsync(
            string workingDirectory,
            int number,
            CancellationToken ct = default) =>
            Task.FromResult(PrResult<PrText>.Fail(GitRemoteFailureKind.Other, "not implemented"));

        public Task<PrResult<PrReviewComment>> ReplyToReviewThreadAsync(
            string workingDirectory,
            string threadId,
            string body,
            CancellationToken ct = default) =>
            Task.FromResult(PrResult<PrReviewComment>.Fail(GitRemoteFailureKind.Other, "not implemented"));

        public Task<PrResult<PrThreadResolution>> ResolveReviewThreadAsync(
            string workingDirectory,
            string threadId,
            CancellationToken ct = default) =>
            Task.FromResult(PrResult<PrThreadResolution>.Fail(GitRemoteFailureKind.Other, "not implemented"));
    }
}
