using Xunit;

namespace Forge.Workspace.Tests.Git;

[Collection("ForgeSnapshotTempFiles")]
public sealed class GitCliPinTests
{
    private static GitCli Cli() => new();

    [Fact]
    public async Task IsRepository_is_false_and_root_is_null_outside_a_repo()
    {
        if (!GitProbe.IsAvailable) return;
        using var directory = new TempDirectory();

        Assert.False(await Cli().IsRepositoryAsync(directory.Path));
        Assert.Null(await Cli().GetRepositoryRootAsync(directory.Path));
    }

    [Fact]
    public async Task IsRepository_is_true_inside_a_repo_and_root_resolves()
    {
        if (!GitProbe.IsAvailable) return;
        using var repo = new TestRepo();

        Assert.True(await Cli().IsRepositoryAsync(repo.Root));
        var root = await Cli().GetRepositoryRootAsync(repo.Root);
        Assert.NotNull(root);
        Assert.Equal(Path.GetFullPath(repo.Root), Path.GetFullPath(root), ignoreCase: OperatingSystem.IsWindows());
    }

    [Fact]
    public async Task Status_on_a_non_repo_throws_GitException()
    {
        if (!GitProbe.IsAvailable) return;
        using var directory = new TempDirectory();

        await Assert.ThrowsAsync<GitException>(() => Cli().GetStatusAsync(directory.Path));
    }

    [Fact]
    public async Task GetCommit_with_unknown_revision_throws_GitException()
    {
        if (!GitProbe.IsAvailable) return;
        using var repo = new TestRepo();
        repo.Write("a.txt", "a\n");
        repo.Commit("base");

        await Assert.ThrowsAsync<GitException>(() => Cli().GetCommitAsync(repo.Root, "does-not-exist-xyz"));
    }

    [Fact]
    public async Task RevParse_of_unknown_revision_returns_null()
    {
        if (!GitProbe.IsAvailable) return;
        using var repo = new TestRepo();
        repo.Write("a.txt", "a\n");
        repo.Commit("base");

        Assert.Null(await Cli().RevParseAsync(repo.Root, "does-not-exist-xyz"));
        Assert.Equal(repo.Head(), await Cli().RevParseAsync(repo.Root, "HEAD"));
    }

    [Fact]
    public async Task Dash_leading_revision_does_not_escape_into_options()
    {
        if (!GitProbe.IsAvailable) return;
        using var repo = new TestRepo();
        repo.Write("a.txt", "a\n");
        repo.Commit("base");

        // The typed surface rejects a leading dash before git ever sees it, so the
        // value can never escape into option position.
        await Assert.ThrowsAsync<ArgumentException>(() => Cli().RevParseAsync(repo.Root, "-HEAD"));
    }

    [Fact]
    public async Task Diff_of_clean_tree_is_empty_and_log_round_trips()
    {
        if (!GitProbe.IsAvailable) return;
        using var repo = new TestRepo();
        repo.Write("a.txt", "a\n");
        var first = repo.Commit("base");
        repo.Write("b.txt", "b\n");
        var second = repo.Commit("second");

        Assert.Empty(await Cli().GetDiffAsync(repo.Root));
        Assert.Equal(string.Empty, await Cli().GetDiffTextAsync(repo.Root));

        var log = await Cli().GetLogAsync(repo.Root, new GitLogOptions { Limit = 2 });
        Assert.Equal(2, log.Count);
        Assert.Equal(second, log[0].Id);
        Assert.Equal(first, log[1].Id);
    }

    [Fact]
    public async Task Diff_reports_modified_and_untracked_paths()
    {
        if (!GitProbe.IsAvailable) return;
        using var repo = new TestRepo();
        repo.Write("a.txt", "a\n");
        repo.Commit("base");
        repo.Write("a.txt", "changed\n");
        repo.Write("new.txt", "new\n");

        var diffs = await Cli().GetDiffAsync(repo.Root);
        Assert.Contains(diffs, d => d.Path == "a.txt");
        var text = await Cli().GetDiffTextAsync(repo.Root);
        Assert.Contains("a.txt", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Concurrent_status_calls_all_succeed()
    {
        if (!GitProbe.IsAvailable) return;
        using var repo = new TestRepo();
        repo.Write("a.txt", "a\n");
        repo.Commit("base");

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Cli().GetStatusAsync(repo.Root)))
            .WaitAsync(TimeSpan.FromSeconds(30));
        Assert.All(results, Assert.Empty);
    }

    [Fact]
    public async Task Cancelled_run_throws_OperationCanceledException()
    {
        if (!GitProbe.IsAvailable) return;
        using var repo = new TestRepo();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Cli().GetLogAsync(repo.Root, null, cancellation.Token));
    }

    [Fact]
    public async Task Status_lists_branches_and_current_branch()
    {
        if (!GitProbe.IsAvailable) return;
        using var repo = new TestRepo();
        repo.Write("a.txt", "a\n");
        repo.Commit("base");

        var branches = await Cli().GetBranchesAsync(repo.Root);
        Assert.NotEmpty(branches);
        var current = await Cli().GetCurrentBranchAsync(repo.Root);
        Assert.NotNull(current);
        Assert.Contains(branches, b => b.Name == current);
    }

    [Fact]
    public async Task Snapshot_tree_leaves_the_real_index_untouched()
    {
        if (!GitProbe.IsAvailable) return;
        using var repo = new TestRepo();
        repo.Write("a.txt", "a\n");
        repo.Commit("base");
        repo.Write("a.txt", "changed\n");
        repo.Write("new.txt", "new\n");

        var cli = Cli();
        var index = GitFixture.RunChecked(repo.Root, "rev-parse", "--path-format=absolute", "--git-path", "index").Trim();
        var indexBytes = File.ReadAllBytes(index);
        var status = await cli.GetStatusAsync(repo.Root);

        var tree = await cli.SnapshotTreeAsync(repo.Root);

        Assert.NotEmpty(tree);
        Assert.Equal(indexBytes, File.ReadAllBytes(index));
        Assert.Equal(status, await cli.GetStatusAsync(repo.Root));
    }
}
