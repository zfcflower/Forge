using Xunit;

namespace Forge.Workspace.Tests.Git;

/// <summary>Smoke table over gh failure text (gh runs under LC_ALL=C, so the messages are stable English).</summary>
public sealed class GhFailureClassifierSmokeTests
{
    public static IEnumerable<object[]> Samples()
    {
        yield return new object[]
        {
            "not logged in",
            1,
            "You are not logged into any GitHub hosts. To get started with GitHub CLI, please run:  gh auth login\n",
            GitRemoteFailureKind.AuthenticationFailed,
            false,
        };
        yield return new object[]
        {
            "bad token over http",
            1,
            "gh: Bad credentials (HTTP 401)\n",
            GitRemoteFailureKind.AuthenticationFailed,
            false,
        };
        yield return new object[]
        {
            "missing scope",
            1,
            "GraphQL: Resource not accessible by personal access token (resolveReviewThread)\n",
            GitRemoteFailureKind.AuthenticationFailed,
            false,
        };
        yield return new object[]
        {
            "repository missing",
            1,
            "GraphQL: Could not resolve to a Repository with the name 'acme/nope'. (repository)\n",
            GitRemoteFailureKind.RemoteNotFound,
            false,
        };
        yield return new object[]
        {
            "not a github remote",
            1,
            "none of the git remotes configured for this repository point to a known GitHub host. To tell gh about a new GitHub host, please use `gh auth login`\n",
            GitRemoteFailureKind.RemoteNotFound,
            false,
        };
        yield return new object[]
        {
            "no pull request for the branch",
            1,
            "no pull requests found for branch \"feature\"\n",
            GitRemoteFailureKind.Other,
            true,
        };
        yield return new object[]
        {
            "pull request number does not exist",
            1,
            "GraphQL: Could not resolve to a PullRequest with the number of 999. (repository.pullRequest)\n",
            GitRemoteFailureKind.Other,
            true,
        };
        yield return new object[]
        {
            "dns failure",
            1,
            "error connecting to api.github.com\ncheck your internet connection or https://githubstatus.com\n",
            GitRemoteFailureKind.Network,
            false,
        };
        yield return new object[]
        {
            "gateway timeout",
            1,
            "gh: Server Error (HTTP 504)\n",
            GitRemoteFailureKind.Network,
            false,
        };
        yield return new object[]
        {
            "rate limit stays an ordinary failure",
            1,
            "gh: API rate limit exceeded for user ID 1. (HTTP 403)\n",
            GitRemoteFailureKind.Other,
            false,
        };
        yield return new object[]
        {
            "unrecognised text",
            1,
            "something nobody has seen before\n",
            GitRemoteFailureKind.Other,
            false,
        };
    }

    [Theory]
    [MemberData(nameof(Samples))]
    public void Classifies_captured_gh_text(string name, int exitCode, string stderr, GitRemoteFailureKind kind, bool noPullRequest)
    {
        var result = new GhCommandResult(exitCode, string.Empty, stderr);

        Assert.True(kind == GhFailureClassifier.Classify(result), name);
        Assert.True(noPullRequest == GhFailureClassifier.IsPullRequestNotFound(result), name);
    }

    [Fact]
    public void Timeout_and_start_failure_are_not_text_classified()
    {
        Assert.Equal(GitRemoteFailureKind.TimedOut, GhFailureClassifier.Classify(new GhCommandResult(-1, string.Empty, string.Empty, TimedOut: true)));
        Assert.Equal(GitRemoteFailureKind.Other, GhFailureClassifier.Classify(new GhCommandResult(-1, string.Empty, "cannot find gh") { StartFailed = true }));
        Assert.Equal(GitRemoteFailureKind.None, GhFailureClassifier.Classify(new GhCommandResult(0, "ok", string.Empty)));
    }

    [Fact]
    public void Existing_pull_request_is_recognised()
    {
        var result = new GhCommandResult(1, string.Empty, "a pull request for branch \"feature\" into branch \"main\" already exists:\nhttps://github.com/acme/forge/pull/3\n");

        Assert.True(GhFailureClassifier.IsPullRequestAlreadyExists(result));
        Assert.False(GhFailureClassifier.IsPullRequestNotFound(result));
    }
}
