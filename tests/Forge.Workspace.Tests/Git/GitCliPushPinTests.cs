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
    public async Task Everything_up_to_date_with_a_stale_lease_reports_non_fast_forward()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var sandbox = new RemoteSandbox();
        var repo = sandbox.Clone("alice");
        sandbox.CommitFile(repo, "base.txt", "base\n", "seed");
        IsolatedGit.RunChecked(repo, "push", "-q", "-u", "origin", "main");

        // The lease is taken while the remote still points at the seed commit; the local branch stays there.
        var lease = await new GitCli().LsRemoteAsync(repo, "origin", "main");
        Assert.True(lease.Result.Success);
        Assert.NotNull(lease.Commit);

        // While this clone's main branch stays put, another writer advances the remote to the very commit this
        // push is pinned to, so git has nothing to update and skips its own lease check.
        IsolatedGit.RunChecked(repo, "checkout", "-q", "-b", "dev");
        var later = sandbox.CommitFile(repo, "later.txt", "later\n", "later work");
        IsolatedGit.RunChecked(repo, "push", "-q", "origin", "dev:main");
        IsolatedGit.RunChecked(repo, "checkout", "-q", "main");

        var push = await new GitCli().PushAsync(
            repo,
            new GitPushOptions
            {
                Branch = "main",
                Remote = "origin",
                ForceWithLease = true,
                ExpectedRemoteCommit = lease.Commit,
                SourceCommit = later,
            });

        // The remote tip moved past the leased commit, so the stale lease is upgraded to non-fast-forward even
        // though nothing needed pushing and the local branch still matches the lease.
        Assert.False(push.Success);
        Assert.Equal(GitRemoteFailureKind.NonFastForward, push.Failure);
        Assert.Contains("lease", push.Message, StringComparison.Ordinal);
        Assert.Equal(later, IsolatedGit.RunChecked(sandbox.Origin, "rev-parse", "refs/heads/main").Trim());
        Assert.Equal(lease.Commit, IsolatedGit.Head(repo));
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
