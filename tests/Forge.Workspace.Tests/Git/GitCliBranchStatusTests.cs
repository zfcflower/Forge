using Xunit;

namespace Forge.Workspace.Tests.Git;

public sealed class GitCliBranchStatusTests
{
    private static GitCli Cli() => new();

    private static string SeedTracked(RemoteSandbox sandbox, string name = "alice")
    {
        var repo = sandbox.Clone(name);
        sandbox.CommitFile(repo, "base.txt", "base\n", "seed");
        IsolatedGit.RunChecked(repo, "push", "-q", "-u", "origin", "main");
        return repo;
    }

    [Fact]
    public async Task Clean_tracking_branch_reports_zero_ahead_behind()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var sandbox = new RemoteSandbox();
        var repo = SeedTracked(sandbox);
        var head = IsolatedGit.Head(repo);

        var status = await Cli().GetBranchStatusAsync(repo);

        Assert.Equal("main", status.Branch);
        Assert.Equal(head, status.HeadCommit);
        Assert.Equal("origin/main", status.Upstream);
        Assert.Equal(0, status.Ahead);
        Assert.Equal(0, status.Behind);
        Assert.False(status.IsDetached);
        Assert.False(status.UpstreamGone);
    }

    [Fact]
    public async Task Local_commit_reports_ahead()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var sandbox = new RemoteSandbox();
        var repo = SeedTracked(sandbox);
        var head = sandbox.CommitFile(repo, "local.txt", "local\n", "local work");

        var status = await Cli().GetBranchStatusAsync(repo);

        Assert.Equal(head, status.HeadCommit);
        Assert.Equal("origin/main", status.Upstream);
        Assert.Equal(1, status.Ahead);
        Assert.Equal(0, status.Behind);
        Assert.False(status.UpstreamGone);
    }

    [Fact]
    public async Task Remote_commit_reports_behind_after_fetch()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var sandbox = new RemoteSandbox();
        var alice = SeedTracked(sandbox, "alice");
        var bob = sandbox.Clone("bob");
        sandbox.CommitFile(bob, "remote.txt", "remote\n", "remote work");
        IsolatedGit.RunChecked(bob, "push", "-q", "origin", "main");
        IsolatedGit.RunChecked(alice, "fetch", "-q", "origin");

        var status = await Cli().GetBranchStatusAsync(alice);

        Assert.Equal("origin/main", status.Upstream);
        Assert.Equal(0, status.Ahead);
        Assert.Equal(1, status.Behind);
    }

    [Fact]
    public async Task Diverged_branch_reports_ahead_and_behind()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var sandbox = new RemoteSandbox();
        var alice = SeedTracked(sandbox, "alice");
        var bob = sandbox.Clone("bob");
        sandbox.CommitFile(bob, "remote.txt", "remote\n", "remote work");
        IsolatedGit.RunChecked(bob, "push", "-q", "origin", "main");
        sandbox.CommitFile(alice, "local.txt", "local\n", "local work");
        IsolatedGit.RunChecked(alice, "fetch", "-q", "origin");

        var status = await Cli().GetBranchStatusAsync(alice);

        Assert.Equal(1, status.Ahead);
        Assert.Equal(1, status.Behind);
        Assert.False(status.UpstreamGone);
    }

    [Fact]
    public async Task Branch_without_upstream_has_null_upstream_and_counts()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var sandbox = new RemoteSandbox();
        var repo = SeedTracked(sandbox);
        IsolatedGit.RunChecked(repo, "checkout", "-q", "-b", "feature-x");
        var head = sandbox.CommitFile(repo, "x.txt", "x\n", "feature work");

        var status = await Cli().GetBranchStatusAsync(repo);

        Assert.Equal("feature-x", status.Branch);
        Assert.Equal(head, status.HeadCommit);
        Assert.Null(status.Upstream);
        Assert.Null(status.Ahead);
        Assert.Null(status.Behind);
        Assert.False(status.IsDetached);
        Assert.False(status.UpstreamGone);
    }

    [Fact]
    public async Task Deleted_remote_branch_reports_upstream_gone_with_null_counts()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var sandbox = new RemoteSandbox();
        var repo = SeedTracked(sandbox);
        IsolatedGit.RunChecked(repo, "checkout", "-q", "-b", "doomed");
        sandbox.CommitFile(repo, "doomed.txt", "x\n", "doomed work");
        IsolatedGit.RunChecked(repo, "push", "-q", "-u", "origin", "doomed");
        IsolatedGit.RunChecked(repo, "push", "-q", "--delete", "origin", "doomed");
        IsolatedGit.RunChecked(repo, "fetch", "-q", "--prune", "origin");

        var status = await Cli().GetBranchStatusAsync(repo);

        Assert.Equal("doomed", status.Branch);
        Assert.True(status.UpstreamGone);
        Assert.Null(status.Ahead);
        Assert.Null(status.Behind);
        Assert.False(status.IsDetached);
    }

    [Fact]
    public async Task Detached_head_reports_detached_and_commit()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var sandbox = new RemoteSandbox();
        var repo = SeedTracked(sandbox);
        var head = IsolatedGit.Head(repo);
        IsolatedGit.RunChecked(repo, "checkout", "-q", "--detach", "HEAD");

        var status = await Cli().GetBranchStatusAsync(repo);

        Assert.True(status.IsDetached);
        Assert.Equal(head, status.HeadCommit);
    }

    [Fact]
    public async Task Unborn_repository_reports_null_head_commit()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var directory = new TempDirectory();
        var repo = Path.Combine(directory.Path, "fresh");
        Directory.CreateDirectory(repo);
        IsolatedGit.RunChecked(directory.Path, "init", "-q", "-b", "main", repo);
        IsolatedGit.RunChecked(repo, "config", "--local", "user.name", "Forge Test");
        IsolatedGit.RunChecked(repo, "config", "--local", "user.email", "forge@example.com");

        var status = await Cli().GetBranchStatusAsync(repo);

        Assert.Equal("main", status.Branch);
        Assert.Null(status.HeadCommit);
        Assert.Null(status.Upstream);
        Assert.Null(status.Ahead);
        Assert.Null(status.Behind);
        Assert.False(status.IsDetached);
        Assert.False(status.UpstreamGone);
    }

    [Fact]
    public async Task GetRemotes_reports_two_remotes_with_distinct_push_url()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var sandbox = new RemoteSandbox();
        var repo = SeedTracked(sandbox);
        var mirror = Path.Combine(sandbox.Root, "mirror.git");
        IsolatedGit.RunChecked(sandbox.Root, "init", "--bare", "-q", "-b", "main", mirror);
        IsolatedGit.RunChecked(repo, "remote", "add", "backup", mirror);
        IsolatedGit.RunChecked(repo, "remote", "set-url", "--push", "backup", "C:/push-only/backup.git");

        var remotes = await Cli().GetRemotesAsync(repo);

        Assert.Equal(2, remotes.Count);
        var origin = Assert.Single(remotes, static remote => remote.Name == "origin");
        var backup = Assert.Single(remotes, static remote => remote.Name == "backup");
        Assert.EndsWith("origin.git", origin.FetchUrl, StringComparison.Ordinal);
        Assert.Equal(mirror, backup.FetchUrl);
        Assert.Equal("C:/push-only/backup.git", backup.PushUrl);
        Assert.NotEqual(backup.FetchUrl, backup.PushUrl, StringComparer.Ordinal);
    }

    [Theory]
    [InlineData("main", true)]
    [InlineData("feature/login", true)]
    [InlineData("forge/slug-1", true)]
    [InlineData("v1.2.3", true)]
    [InlineData("bad..name", false)]
    [InlineData("-leading-dash", false)]
    [InlineData("trailing.lock", false)]
    [InlineData("has space", false)]
    [InlineData("double//slash", false)]
    [InlineData("ends-with-dot.", false)]
    public async Task Branch_name_validity_matches_git_rules(string name, bool expected)
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var sandbox = new RemoteSandbox();
        var repo = SeedTracked(sandbox);

        Assert.Equal(expected, await Cli().IsValidBranchNameAsync(repo, name));
    }
}
