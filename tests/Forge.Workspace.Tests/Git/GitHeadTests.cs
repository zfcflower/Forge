using Xunit;

namespace Forge.Workspace.Tests.Git;

/// <summary>
/// Covers <see cref="GitCli.GetHeadAsync"/>: which branch a directory has checked out, or whether
/// it is detached, unborn or not a repository at all. Every expected state comes back as a value;
/// only cancellation escapes as an exception.
/// </summary>
public sealed class GitHeadTests
{
    [Fact]
    public async Task Head_on_a_branch_with_a_commit_reports_the_branch_name()
    {
        if (!GitProbe.IsAvailable) return;
        using var repo = new TestRepo();
        repo.CommitFile("a.txt", "a\n", "first");

        var actual = await new GitCli().GetHeadAsync(repo.Root);

        Assert.Equal(GitHeadKind.Branch, actual.Kind);
        Assert.Equal("main", actual.Branch);
    }

    [Fact]
    public async Task Fresh_init_without_a_commit_reports_the_unborn_branch_name()
    {
        if (!GitProbe.IsAvailable) return;
        using var repo = new TestRepo();

        var actual = await new GitCli().GetHeadAsync(repo.Root);

        Assert.Equal(GitHeadKind.UnbornBranch, actual.Kind);
        Assert.Equal("main", actual.Branch);
    }

    [Fact]
    public async Task Detached_head_reports_detached_without_a_branch()
    {
        if (!GitProbe.IsAvailable) return;
        using var repo = new TestRepo();
        repo.CommitFile("a.txt", "a\n", "first");
        repo.RunGit("checkout", "--detach");

        var actual = await new GitCli().GetHeadAsync(repo.Root);

        Assert.Equal(GitHeadKind.Detached, actual.Kind);
        Assert.Null(actual.Branch);
    }

    [Fact]
    public async Task Directory_outside_a_repository_reports_not_a_repository_without_throwing()
    {
        if (!GitProbe.IsAvailable) return;
        using var directory = new TempDirectory();

        var actual = await new GitCli().GetHeadAsync(directory.Path);

        Assert.Equal(GitHeadKind.NotRepository, actual.Kind);
        Assert.Null(actual.Branch);
    }

    [Fact]
    public async Task Subdirectory_reports_the_repository_branch()
    {
        if (!GitProbe.IsAvailable) return;
        using var repo = new TestRepo();
        repo.CommitFile("a.txt", "a\n", "first");
        var subdirectory = repo.FullPath("sub/dir");
        Directory.CreateDirectory(subdirectory);

        var actual = await new GitCli().GetHeadAsync(subdirectory);

        Assert.Equal(GitHeadKind.Branch, actual.Kind);
        Assert.Equal("main", actual.Branch);
    }

    [Fact]
    public async Task Bare_repository_reports_not_a_repository_because_it_has_no_checkout()
    {
        if (!GitProbe.IsAvailable) return;
        using var sandbox = new TempDirectory();
        var bare = Path.Combine(sandbox.Path, "bare.git");
        Directory.CreateDirectory(bare);
        GitFixture.RunChecked(bare, "init", "--bare", "-q", "-b", "main");

        var actual = await new GitCli().GetHeadAsync(bare);

        // HEAD names a branch, but nothing is checked out anywhere: treating the data directory as a working tree would
        // let a session write its files into the repository's own object store.
        Assert.Equal(GitHeadKind.NotRepository, actual.Kind);
        Assert.Null(actual.Branch);
    }

    [Fact]
    public async Task Cancelled_token_throws_operation_canceled()
    {
        if (!GitProbe.IsAvailable) return;
        using var repo = new TestRepo();
        repo.CommitFile("a.txt", "a\n", "first");
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new GitCli().GetHeadAsync(repo.Root, cancelled.Token));
    }
}
