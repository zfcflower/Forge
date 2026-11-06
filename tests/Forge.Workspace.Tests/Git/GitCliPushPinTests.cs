using Forge.Workspace;
using Xunit;

namespace Forge.Workspace.Tests.Git;

/// <summary>
/// A push can be pinned to one commit (<see cref="GitPushOptions.SourceCommit"/>): the refspec names that commit instead of
/// the local branch, so a commit that lands on the branch between the caller's check and the push is never published.
/// </summary>
public sealed class GitCliPushPinTests
{
    [Fact]
    public async Task A_pinned_push_publishes_exactly_the_given_commit_even_when_head_moved_on()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var sandbox = new RemoteSandbox();
        var repo = sandbox.Clone("alice");
        var audited = sandbox.CommitFile(repo, "a.txt", "a\n", "audited");
        IsolatedGit.RunChecked(repo, "checkout", "-q", "-b", "feature");
        var late = sandbox.CommitFile(repo, "b.txt", "b\n", "landed after the check");

        var push = await new GitCli().PushAsync(
            repo,
            new GitPushOptions
            {
                Branch = "feature",
                Remote = "origin",
                ForceWithLease = true,
                ExpectedRemoteCommit = string.Empty,
                SourceCommit = audited,
            });

        Assert.True(push.Success, push.Message);
        Assert.Equal(audited, IsolatedGit.RunChecked(sandbox.Origin, "rev-parse", "refs/heads/feature").Trim());
        Assert.Equal(late, IsolatedGit.Head(repo));
    }

    [Fact]
    public async Task A_pinned_first_push_still_sets_the_upstream_of_the_branch()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var sandbox = new RemoteSandbox();
        var repo = sandbox.Clone("alice");
        var audited = sandbox.CommitFile(repo, "a.txt", "a\n", "audited");
        IsolatedGit.RunChecked(repo, "checkout", "-q", "-b", "feature");
        Assert.Null(IsolatedGit.UpstreamOf(repo, "feature"));

        var push = await new GitCli().PushAsync(
            repo,
            new GitPushOptions
            {
                Branch = "feature",
                Remote = "origin",
                SetUpstream = true,
                ForceWithLease = true,
                ExpectedRemoteCommit = string.Empty,
                SourceCommit = audited,
            });

        Assert.True(push.Success, push.Message);
        Assert.Contains("upstream set", push.Message, StringComparison.Ordinal);
        Assert.Equal("origin/feature", IsolatedGit.UpstreamOf(repo, "feature"));
        Assert.Equal(audited, IsolatedGit.RunChecked(sandbox.Origin, "rev-parse", "refs/heads/feature").Trim());
    }

    [Fact]
    public async Task A_source_commit_that_is_not_a_sha_is_refused_before_anything_is_pushed()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var sandbox = new RemoteSandbox();
        var repo = sandbox.Clone("alice");
        sandbox.CommitFile(repo, "a.txt", "a\n", "audited");
        IsolatedGit.RunChecked(repo, "checkout", "-q", "-b", "feature");

        var push = await new GitCli().PushAsync(
            repo,
            new GitPushOptions { Branch = "feature", Remote = "origin", SourceCommit = "HEAD~1" });

        Assert.False(push.Success);
        Assert.Contains("SourceCommit", push.Message, StringComparison.Ordinal);
        Assert.Contains("not a commit hash", push.Message, StringComparison.Ordinal);
        Assert.NotEqual(0, IsolatedGit.Run(sandbox.Origin, "rev-parse", "--verify", "-q", "refs/heads/feature").ExitCode);
    }
}
