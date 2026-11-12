using Xunit;

namespace Forge.Workspace.Tests.Git;

public sealed class GitCliPullPushTests
{
    private static GitCli Cli() => new();

    private static string OriginTip(RemoteSandbox sandbox, string branch = "main") =>
        IsolatedGit.RunChecked(sandbox.Origin, "rev-parse", branch).Trim();

    [Fact]
    public async Task Pull_fast_forward_only_advances_a_behind_branch()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var sandbox = new RemoteSandbox();
        var (alice, _, tip) = sandbox.SeedRemoteAhead();

        var result = await Cli().PullAsync(alice, GitPullStrategy.FastForwardOnly);

        Assert.True(result.Success);
        Assert.Equal(tip, IsolatedGit.Head(alice));
    }

    [Fact]
    public async Task Pull_fast_forward_only_on_diverged_branches_reports_non_fast_forward()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var sandbox = new RemoteSandbox();
        var (alice, _, _) = sandbox.SeedRemoteAhead();
        var head = sandbox.CommitFile(alice, "local.txt", "local\n", "local work");

        var result = await Cli().PullAsync(alice, GitPullStrategy.FastForwardOnly);

        Assert.False(result.Success);
        Assert.Equal(GitRemoteFailureKind.NonFastForward, result.Failure);
        Assert.Equal(head, IsolatedGit.Head(alice));
    }

    [Fact]
    public async Task Pull_with_rebase_replays_non_conflicting_commits_linearly()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var sandbox = new RemoteSandbox();
        var (alice, _, _) = sandbox.SeedRemoteAhead();
        sandbox.CommitFile(alice, "local.txt", "local\n", "local work");

        var result = await Cli().PullAsync(alice, GitPullStrategy.Rebase);

        Assert.True(result.Success);
        Assert.True(File.Exists(Path.Combine(alice, "remote.txt")));
        Assert.True(File.Exists(Path.Combine(alice, "local.txt")));
        var parents = IsolatedGit.RunChecked(alice, "log", "-1", "--format=%P", "HEAD").Trim();
        Assert.Single(parents.Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    [Fact]
    public async Task Pull_with_merge_creates_a_merge_commit()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var sandbox = new RemoteSandbox();
        var (alice, _, _) = sandbox.SeedRemoteAhead();
        sandbox.CommitFile(alice, "local.txt", "local\n", "local work");

        var result = await Cli().PullAsync(alice, GitPullStrategy.Merge);

        Assert.True(result.Success);
        var parents = IsolatedGit.RunChecked(alice, "log", "-1", "--format=%P", "HEAD").Trim();
        Assert.Equal(2, parents.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length);
    }

    [Fact]
    public async Task Pull_with_rebase_reports_conflict_and_abort_restores_head()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var sandbox = new RemoteSandbox();
        var (alice, _, _) = sandbox.SeedRemoteAhead("clash.txt", "bob\n", "bob work");
        sandbox.CommitFile(alice, "clash.txt", "alice\n", "alice work");
        var head = IsolatedGit.Head(alice);

        var pull = await Cli().PullAsync(alice, GitPullStrategy.Rebase);

        Assert.False(pull.Success);
        Assert.Equal(GitRemoteFailureKind.Conflict, pull.Failure);
        var state = await Cli().GetConflictStateAsync(alice);
        Assert.Equal(GitOperationKind.Rebase, state.Operation);
        Assert.Contains("clash.txt", state.ConflictedPaths, StringComparer.Ordinal);

        var abort = await Cli().AbortOperationAsync(alice);

        Assert.True(abort.Success);
        Assert.Equal(head, IsolatedGit.Head(alice));
        Assert.False((await Cli().GetConflictStateAsync(alice)).InProgress);
    }

    [Fact]
    public async Task Pull_without_upstream_reports_no_upstream()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var sandbox = new RemoteSandbox();
        var repo = sandbox.SeedTracked();
        IsolatedGit.RunChecked(repo, "checkout", "-q", "-b", "lonely");

        var result = await Cli().PullAsync(repo, GitPullStrategy.FastForwardOnly);

        Assert.False(result.Success);
        Assert.Equal(GitRemoteFailureKind.NoUpstream, result.Failure);
    }

    [Fact]
    public async Task First_push_with_set_upstream_configures_the_upstream()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var sandbox = new RemoteSandbox();
        var repo = sandbox.SeedTracked();
        IsolatedGit.RunChecked(repo, "checkout", "-q", "-b", "feature-c");
        sandbox.CommitFile(repo, "c.txt", "c\n", "feature work");

        var result = await Cli().PushAsync(repo, new GitPushOptions { SetUpstream = true });

        Assert.True(result.Success);
        Assert.Equal("origin/feature-c", IsolatedGit.UpstreamOf(repo, "feature-c"));
        Assert.Equal(IsolatedGit.Head(repo), OriginTip(sandbox, "feature-c"));
    }

    [Fact]
    public async Task Push_behind_the_remote_reports_non_fast_forward_and_keeps_the_remote_tip()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var sandbox = new RemoteSandbox();
        var (alice, _, _) = sandbox.SeedRemoteAhead();
        sandbox.CommitFile(alice, "local.txt", "local\n", "local work");
        var remoteBefore = OriginTip(sandbox);

        var result = await Cli().PushAsync(alice, new GitPushOptions());

        Assert.False(result.Success);
        Assert.Equal(GitRemoteFailureKind.NonFastForward, result.Failure);
        Assert.Equal(remoteBefore, OriginTip(sandbox));
    }

    [Fact]
    public async Task Force_with_lease_after_fresh_read_succeeds()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var sandbox = new RemoteSandbox();
        var (alice, _, _) = sandbox.SeedRemoteAhead();
        sandbox.CommitFile(alice, "local.txt", "local\n", "local work");
        IsolatedGit.RunChecked(alice, "fetch", "-q", "origin");
        IsolatedGit.RunChecked(alice, "rebase", "-q", "origin/main");

        var read = await Cli().LsRemoteAsync(alice, "origin", "main");
        Assert.True(read.Result.Success);

        var push = await Cli().PushAsync(alice, new GitPushOptions
        {
            ForceWithLease = true,
            ExpectedRemoteCommit = read.Commit,
        });

        Assert.True(push.Success);
        Assert.Equal(IsolatedGit.Head(alice), OriginTip(sandbox));
    }

    [Fact]
    public async Task Force_with_lease_on_a_stale_read_reports_non_fast_forward()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var sandbox = new RemoteSandbox();
        var (alice, bob, _) = sandbox.SeedRemoteAhead();
        sandbox.CommitFile(alice, "local.txt", "local\n", "local work");
        IsolatedGit.RunChecked(alice, "fetch", "-q", "origin");
        IsolatedGit.RunChecked(alice, "rebase", "-q", "origin/main");

        var stale = await Cli().LsRemoteAsync(alice, "origin", "main");
        Assert.True(stale.Result.Success);
        var newer = sandbox.CommitFile(bob, "newer.txt", "newer\n", "newer work");
        IsolatedGit.RunChecked(bob, "push", "-q", "origin", "main");

        var push = await Cli().PushAsync(alice, new GitPushOptions
        {
            ForceWithLease = true,
            ExpectedRemoteCommit = stale.Commit,
        });

        Assert.False(push.Success);
        Assert.Equal(GitRemoteFailureKind.NonFastForward, push.Failure);
        Assert.Equal(newer, OriginTip(sandbox));
    }

    [Fact]
    public async Task Force_with_lease_without_expected_commit_behaves_as_plain_push()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var sandbox = new RemoteSandbox();
        var (alice, _, _) = sandbox.SeedRemoteAhead();
        sandbox.CommitFile(alice, "local.txt", "local\n", "local work");

        var result = await Cli().PushAsync(alice, new GitPushOptions { ForceWithLease = true });

        Assert.False(result.Success);
        Assert.Equal(GitRemoteFailureKind.NonFastForward, result.Failure);
    }

    [Fact]
    public async Task Force_with_lease_claiming_absence_is_rejected_when_the_branch_exists()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var sandbox = new RemoteSandbox();
        var repo = sandbox.SeedTracked();
        var remoteBefore = OriginTip(sandbox);

        var result = await Cli().PushAsync(repo, new GitPushOptions
        {
            ForceWithLease = true,
            ExpectedRemoteCommit = string.Empty,
        });

        Assert.False(result.Success);
        Assert.Equal(remoteBefore, OriginTip(sandbox));
    }

    [Fact]
    public async Task LsRemote_returns_the_exact_tip_for_an_existing_branch()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var sandbox = new RemoteSandbox();
        var repo = sandbox.SeedTracked();
        var tip = OriginTip(sandbox);

        var read = await Cli().LsRemoteAsync(repo, "origin", "main");

        Assert.True(read.Result.Success);
        Assert.Equal(tip, read.Commit);
    }

    [Fact]
    public async Task LsRemote_reports_success_with_null_commit_for_a_missing_branch()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var sandbox = new RemoteSandbox();
        var repo = sandbox.SeedTracked();

        var read = await Cli().LsRemoteAsync(repo, "origin", "no-such-branch");

        Assert.True(read.Result.Success);
        Assert.Null(read.Commit);
    }

    [Fact]
    public async Task LsRemote_with_unknown_remote_fails()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var sandbox = new RemoteSandbox();
        var repo = sandbox.SeedTracked();

        var read = await Cli().LsRemoteAsync(repo, "no-such-remote", "main");

        Assert.False(read.Result.Success);
    }
}
