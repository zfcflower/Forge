using Xunit;

namespace Forge.Workspace.Tests.Git;

public sealed class GitCliBranchManagementTests
{
    private static GitCli Cli() => new();

    [Fact]
    public async Task Create_branch_at_head_without_switching()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var sandbox = new RemoteSandbox();
        var repo = sandbox.SeedTracked();
        var head = IsolatedGit.Head(repo);

        var result = await Cli().CreateBranchAsync(repo, "feature-a");

        Assert.True(result.Success);
        Assert.Equal("main", IsolatedGit.CurrentBranch(repo));
        Assert.Equal(head, IsolatedGit.Head(repo));
        Assert.Contains("feature-a", IsolatedGit.BranchList(repo), StringComparer.Ordinal);
    }

    [Fact]
    public async Task Create_branch_and_switch()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var sandbox = new RemoteSandbox();
        var repo = sandbox.SeedTracked();

        var result = await Cli().CreateBranchAsync(repo, "feature-b", switchTo: true);

        Assert.True(result.Success);
        Assert.Equal("feature-b", IsolatedGit.CurrentBranch(repo));
    }

    [Fact]
    public async Task Switch_to_remote_only_branch_creates_tracking_branch()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var sandbox = new RemoteSandbox();
        var alice = sandbox.SeedTracked("alice");
        var bob = sandbox.Clone("bob");
        IsolatedGit.RunChecked(bob, "checkout", "-q", "-b", "shared");
        sandbox.CommitFile(bob, "shared.txt", "shared\n", "shared work");
        IsolatedGit.RunChecked(bob, "push", "-q", "-u", "origin", "shared");
        IsolatedGit.RunChecked(alice, "fetch", "-q", "origin");

        var result = await Cli().SwitchBranchAsync(alice, "shared");

        Assert.True(result.Success);
        Assert.Equal("shared", IsolatedGit.CurrentBranch(alice));
        Assert.Equal("origin/shared", IsolatedGit.UpstreamOf(alice, "shared"));
    }

    [Fact]
    public async Task Switch_with_dirty_tracked_file_is_rejected_and_head_is_unchanged()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var sandbox = new RemoteSandbox();
        var repo = sandbox.SeedTracked();
        IsolatedGit.RunChecked(repo, "branch", "other");
        File.WriteAllText(Path.Combine(repo, "base.txt"), "uncommitted change\n", new System.Text.UTF8Encoding(false));
        var head = IsolatedGit.Head(repo);

        var result = await Cli().SwitchBranchAsync(repo, "other");

        Assert.False(result.Success);
        Assert.Equal(GitRemoteFailureKind.DirtyWorkTree, result.Failure);
        Assert.Equal(head, IsolatedGit.Head(repo));
        Assert.Equal("main", IsolatedGit.CurrentBranch(repo));
    }

    [Fact]
    public async Task Switch_with_only_untracked_files_succeeds()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var sandbox = new RemoteSandbox();
        var repo = sandbox.SeedTracked();
        IsolatedGit.RunChecked(repo, "branch", "other");
        File.WriteAllText(Path.Combine(repo, "new-untracked.txt"), "untracked\n", new System.Text.UTF8Encoding(false));

        var result = await Cli().SwitchBranchAsync(repo, "other");

        Assert.True(result.Success);
        Assert.Equal("other", IsolatedGit.CurrentBranch(repo));
        Assert.True(File.Exists(Path.Combine(repo, "new-untracked.txt")));
    }

    [Fact]
    public async Task Switch_to_unknown_branch_fails()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var sandbox = new RemoteSandbox();
        var repo = sandbox.SeedTracked();
        var head = IsolatedGit.Head(repo);

        var result = await Cli().SwitchBranchAsync(repo, "no-such-branch");

        Assert.False(result.Success);
        Assert.Equal(head, IsolatedGit.Head(repo));
    }

    [Theory]
    [InlineData("create")]
    [InlineData("switch")]
    public async Task Option_like_branch_name_is_never_executed_as_an_option(string operation)
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var sandbox = new RemoteSandbox();
        var repo = sandbox.SeedTracked();
        var head = IsolatedGit.Head(repo);
        var branches = IsolatedGit.BranchList(repo);
        var cli = Cli();

        GitRemoteResult? result = null;
        try
        {
            result = operation == "create"
                ? await cli.CreateBranchAsync(repo, "-d")
                : await cli.SwitchBranchAsync(repo, "-d");
        }
        catch (ArgumentException)
        {
            // Also acceptable: reject before spawning git.
        }

        if (result is not null)
        {
            Assert.False(result.Success);
        }

        Assert.Equal(head, IsolatedGit.Head(repo));
        Assert.Equal(branches, IsolatedGit.BranchList(repo));
    }

    [Fact]
    public async Task SetUpstream_makes_status_report_the_upstream()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var sandbox = new RemoteSandbox();
        var repo = sandbox.SeedTracked();
        IsolatedGit.RunChecked(repo, "checkout", "-q", "-b", "feature-x");

        var result = await Cli().SetUpstreamAsync(repo, branch: null, upstream: "origin/main");

        Assert.True(result.Success);
        var status = await Cli().GetBranchStatusAsync(repo);
        Assert.Equal("origin/main", status.Upstream);
        Assert.Equal(0, status.Ahead);
        Assert.Equal(0, status.Behind);
    }

    [Fact]
    public async Task Fetch_updates_remote_tracking_refs()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var sandbox = new RemoteSandbox();
        var alice = sandbox.SeedTracked("alice");
        var bob = sandbox.Clone("bob");
        var tip = sandbox.CommitFile(bob, "remote.txt", "remote\n", "remote work");
        IsolatedGit.RunChecked(bob, "push", "-q", "origin", "main");

        var result = await Cli().FetchAsync(alice);

        Assert.True(result.Success);
        Assert.Equal(tip, IsolatedGit.RunChecked(alice, "rev-parse", "origin/main").Trim());
    }

    [Fact]
    public async Task Fetch_with_unknown_remote_reports_remote_not_found()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var sandbox = new RemoteSandbox();
        var repo = sandbox.SeedTracked();

        var result = await Cli().FetchAsync(repo, "no-such-remote");

        Assert.False(result.Success);
        Assert.Equal(GitRemoteFailureKind.RemoteNotFound, result.Failure);
    }

    [Fact]
    public async Task Fetch_from_a_missing_path_reports_remote_not_found()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var sandbox = new RemoteSandbox();
        var repo = sandbox.SeedTracked();
        var missing = Path.Combine(sandbox.Root, "does-not-exist.git");
        IsolatedGit.RunChecked(repo, "remote", "add", "gone", missing);

        var result = await Cli().FetchAsync(repo, "gone");

        Assert.False(result.Success);
        Assert.Equal(GitRemoteFailureKind.RemoteNotFound, result.Failure);
    }

    [Fact]
    public async Task Network_timeout_on_a_hanging_remote_reports_timed_out()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var sandbox = new RemoteSandbox();
        var repo = sandbox.SeedTracked();
        using var server = new HangingHttpServer();
        IsolatedGit.RunChecked(repo, "remote", "add", "hanging", server.Url);

        var result = await Cli().FetchAsync(
            repo,
            "hanging",
            new GitNetworkOptions { Timeout = TimeSpan.FromMilliseconds(1) });

        Assert.False(result.Success);
        Assert.Equal(GitRemoteFailureKind.TimedOut, result.Failure);
    }

    [Fact]
    public async Task Fetch_from_a_closed_https_port_fails_fast_without_hanging()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var sandbox = new RemoteSandbox();
        var repo = sandbox.SeedTracked();
        var port = IsolatedGit.ClosedPort();
        IsolatedGit.RunChecked(repo, "remote", "add", "closed", $"https://127.0.0.1:{port}/x.git");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var result = await Cli().FetchAsync(repo, "closed", ct: timeout.Token);

        Assert.False(timeout.IsCancellationRequested);
        Assert.False(result.Success);
        Assert.True(
            result.Failure is GitRemoteFailureKind.Network or GitRemoteFailureKind.AuthenticationFailed,
            $"expected Network or AuthenticationFailed but got {result.Failure}: {result.StandardError}");
    }

    [Fact]
    public async Task Fetch_with_credentials_to_a_closed_port_fails_fast_without_hanging()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var sandbox = new RemoteSandbox();
        var repo = sandbox.SeedTracked();
        var port = IsolatedGit.ClosedPort();
        IsolatedGit.RunChecked(repo, "remote", "add", "locked", $"https://user:secret@127.0.0.1:{port}/x.git");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var result = await Cli().FetchAsync(repo, "locked", ct: timeout.Token);

        Assert.False(timeout.IsCancellationRequested);
        Assert.False(result.Success);
        Assert.True(
            result.Failure is GitRemoteFailureKind.Network or GitRemoteFailureKind.AuthenticationFailed,
            $"expected Network or AuthenticationFailed but got {result.Failure}: {result.StandardError}");
    }
}
