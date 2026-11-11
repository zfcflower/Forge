using Forge.App.Automations;
using Forge.App.Tests.Git;
using Forge.Workspace;
using Xunit;

namespace Forge.App.Tests.Automations;

/// <summary>The CI chain's push guard against real temporary repositories (bare origin plus a clone).</summary>
public sealed class GitCliCiGitTests : IDisposable
{
    private readonly GitTestSandbox sandbox = GitTestSandbox.Create();
    private readonly GitCliCiGit ciGit = new(new GitCli());

    public void Dispose() => sandbox.Dispose();

    private static string Head(string repo) => GitTestShell.RunChecked(repo, "rev-parse", "HEAD").Trim();

    private string RemoteTip(string branch) =>
        GitTestShell.RunChecked(sandbox.Origin, "rev-parse", "refs/heads/" + branch).Trim();

    /// <summary>A pushed <c>feature</c> branch (the PR head) checked out in the clone; returns the clone and the PR head oid.</summary>
    private (string Work, string HeadOid) PrBranch()
    {
        var work = sandbox.CloneTracked("work");
        GitTestShell.RunChecked(work, "checkout", "-q", "-b", "feature");
        sandbox.CommitFile(work, "f.txt", "1\n", "feature work");
        GitTestShell.RunChecked(work, "push", "-q", "-u", "origin", "feature");
        return (work, Head(work));
    }

    [Fact]
    public async Task Pushes_HEAD_to_the_pr_head_branch()
    {
        var (work, oid) = PrBranch();
        sandbox.CommitFile(work, "fix.txt", "fix\n", "fix ci");

        var result = await ciGit.PushAsync(work, "feature", "main", oid, CancellationToken.None);

        Assert.True(result.Success, result.Error);
        Assert.Equal(Head(work), RemoteTip("feature"));
        Assert.NotEqual(oid, RemoteTip("feature"));
    }

    [Fact]
    public async Task Pushes_HEAD_of_a_differently_named_session_branch_that_tracks_the_head()
    {
        var (work, oid) = PrBranch();
        GitTestShell.RunChecked(work, "checkout", "-q", "-b", "session", "--track", "origin/feature");
        sandbox.CommitFile(work, "fix.txt", "fix\n", "fix ci");

        var result = await ciGit.PushAsync(work, "feature", "main", oid, CancellationToken.None);

        Assert.True(result.Success, result.Error);
        Assert.Equal(Head(work), RemoteTip("feature"));
    }

    [Fact]
    public async Task Refuses_the_default_branch_even_when_it_is_not_the_pr_base()
    {
        var work = sandbox.CloneTracked("work");
        var before = RemoteTip("main");
        sandbox.CommitFile(work, "fix.txt", "fix\n", "fix ci");

        var result = await ciGit.PushAsync(work, "main", "develop", before, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(CiPushRefusal.ProtectedBranch, result.Refusal);
        Assert.Equal(before, RemoteTip("main"));
    }

    [Fact]
    public async Task Refuses_the_pr_base_branch()
    {
        var (work, oid) = PrBranch();
        sandbox.CommitFile(work, "fix.txt", "fix\n", "fix ci");

        var result = await ciGit.PushAsync(work, "feature", "feature", oid, CancellationToken.None);

        Assert.Equal(CiPushRefusal.ProtectedBranch, result.Refusal);
        Assert.Equal(oid, RemoteTip("feature"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("main")]
    public async Task Refuses_a_branch_that_is_neither_the_head_nor_tracks_it(string? upstream)
    {
        var (work, oid) = PrBranch();
        GitTestShell.RunChecked(work, "checkout", "-q", "-b", "session");
        if (upstream is not null)
        {
            GitTestShell.RunChecked(work, "branch", "-q", "--set-upstream-to=origin/" + upstream);
        }

        sandbox.CommitFile(work, "fix.txt", "fix\n", "fix ci");

        var result = await ciGit.PushAsync(work, "feature", "main", oid, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(CiPushRefusal.BranchMismatch, result.Refusal);
        Assert.Equal(oid, RemoteTip("feature"));
    }

    [Fact]
    public async Task Refuses_when_HEAD_does_not_descend_from_the_pr_head_commit()
    {
        var (work, oid) = PrBranch();
        GitTestShell.RunChecked(work, "checkout", "-q", "main");
        GitTestShell.RunChecked(work, "checkout", "-q", "-b", "other");
        sandbox.CommitFile(work, "other.txt", "x\n", "unrelated");
        var unrelated = Head(work);
        GitTestShell.RunChecked(work, "checkout", "-q", "feature");

        var notAncestor = await ciGit.PushAsync(work, "feature", "main", unrelated, CancellationToken.None);
        var unknown = await ciGit.PushAsync(work, "feature", "main", new string('a', 40), CancellationToken.None);

        Assert.Equal(CiPushRefusal.NotDescended, notAncestor.Refusal);
        Assert.Equal(CiPushRefusal.NotDescended, unknown.Refusal);
        Assert.Equal(oid, RemoteTip("feature"));
    }

    [Fact]
    public async Task Refuses_a_detached_HEAD()
    {
        var (work, oid) = PrBranch();
        GitTestShell.RunChecked(work, "checkout", "-q", "--detach");
        sandbox.CommitFile(work, "fix.txt", "fix\n", "fix ci");

        var result = await ciGit.PushAsync(work, "feature", "main", oid, CancellationToken.None);

        Assert.Equal(CiPushRefusal.DetachedHead, result.Refusal);
        Assert.Equal(oid, RemoteTip("feature"));
    }

    [Fact]
    public async Task Reports_a_rejected_push_as_a_push_failure_without_forcing()
    {
        var (work, oid) = PrBranch();
        var other = sandbox.Clone("other");
        GitTestShell.RunChecked(other, "checkout", "-q", "feature");
        sandbox.CommitFile(other, "theirs.txt", "t\n", "someone else");
        GitTestShell.RunChecked(other, "push", "-q", "origin", "feature");
        var theirs = RemoteTip("feature");
        sandbox.CommitFile(work, "fix.txt", "fix\n", "fix ci");

        var result = await ciGit.PushAsync(work, "feature", "main", oid, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(CiPushRefusal.None, result.Refusal);
        Assert.Equal(theirs, RemoteTip("feature"));
    }
}
