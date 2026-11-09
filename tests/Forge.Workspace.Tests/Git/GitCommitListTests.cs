using Xunit;

namespace Forge.Workspace.Tests.Git;

public sealed class GitCommitListTests
{
    private static GitCli Cli() => new();

    [Fact]
    public async Task GetCommits_lists_exclude_to_head_newest_first_with_metadata()
    {
        if (!GitProbe.IsAvailable) return;
        using var repo = new TestRepo();
        repo.Write("a.txt", "1\n");
        var baseSha = repo.Commit("base");
        repo.Write("a.txt", "2\n");
        var second = repo.Commit("second | with \"odd\" subject");
        repo.Write("a.txt", "3\n");
        var third = repo.Commit("third");

        var commits = await Cli().GetCommitsAsync(repo.Root, baseSha, "HEAD");

        Assert.Equal([third, second], commits.Select(static c => c.Sha));
        Assert.Equal(third[..7], commits[0].ShortSha);
        Assert.Equal("third", commits[0].Subject);
        Assert.Equal("second | with \"odd\" subject", commits[1].Subject);
        Assert.Equal([second], commits[0].ParentShas);
        Assert.Equal([baseSha], commits[1].ParentShas);
        Assert.True(commits[0].AuthorDate > DateTimeOffset.UtcNow.AddMinutes(-10));
    }

    [Fact]
    public async Task GetCommits_is_empty_when_nothing_is_ahead_and_honours_limit()
    {
        if (!GitProbe.IsAvailable) return;
        using var repo = new TestRepo();
        repo.Write("a.txt", "1\n");
        var baseSha = repo.Commit("base");
        Assert.Empty(await Cli().GetCommitsAsync(repo.Root, baseSha, "HEAD"));

        repo.Write("a.txt", "2\n");
        repo.Commit("two");
        repo.Write("a.txt", "3\n");
        var three = repo.Commit("three");

        var limited = await Cli().GetCommitsAsync(repo.Root, baseSha, "HEAD", limit: 1);
        Assert.Equal(three, Assert.Single(limited).Sha);
    }

    [Fact]
    public async Task GetCommits_reports_root_commit_without_parents_and_merges_with_two()
    {
        if (!GitProbe.IsAvailable) return;
        using var repo = new TestRepo();
        repo.Write("a.txt", "1\n");
        var root = repo.Commit("root");
        var all = await Cli().GetCommitsAsync(repo.Root, string.Empty, "HEAD");
        var only = Assert.Single(all);
        Assert.Equal(root, only.Sha);
        Assert.Empty(only.ParentShas);

        repo.RunGit("checkout", "-q", "-b", "side");
        repo.Write("b.txt", "b\n");
        repo.Commit("side");
        repo.RunGit("checkout", "-q", "main");
        repo.Write("c.txt", "c\n");
        repo.Commit("main2");
        repo.RunGit("merge", "-q", "--no-ff", "-m", "merge", "side");

        var merged = await Cli().GetCommitsAsync(repo.Root, root, "HEAD");
        Assert.Equal(3, merged.Count);
        Assert.Equal("merge", merged[0].Subject);
        Assert.Equal(2, merged[0].ParentShas.Count);
    }

    [Fact]
    public async Task GetCommits_rejects_option_like_refs()
    {
        using var repo = new TestRepo();
        await Assert.ThrowsAsync<ArgumentException>(() => Cli().GetCommitsAsync(repo.Root, "--all", "HEAD"));
        await Assert.ThrowsAsync<ArgumentException>(() => Cli().GetCommitsAsync(repo.Root, "HEAD", "-n1"));
    }

    [Fact]
    public async Task GetTreeDiff_with_null_from_diffs_against_the_empty_tree()
    {
        if (!GitProbe.IsAvailable) return;
        using var repo = new TestRepo();
        repo.Write("a.txt", "one\ntwo\n");
        repo.Write("dir/b.txt", "x\n");
        var root = repo.Commit("root");

        var files = await Cli().GetTreeDiffAsync(repo.Root, null, root, 3);

        Assert.Equal(2, files.Count);
        Assert.All(files, static f => Assert.Equal(FileDiffStatus.Added, f.Status));
        Assert.Equal(2, files.Single(static f => f.Path == "a.txt").AddedLineCount);
    }

    [Fact]
    public async Task GetTreeDiff_respects_the_context_line_count()
    {
        if (!GitProbe.IsAvailable) return;
        using var repo = new TestRepo();
        var body = string.Join('\n', Enumerable.Range(1, 40).Select(static i => "line" + i)) + "\n";
        repo.Write("f.txt", body);
        var before = repo.Commit("before");
        repo.Write("f.txt", body.Replace("line20\n", "LINE20\n", StringComparison.Ordinal));
        var after = repo.Commit("after");

        var narrow = Assert.Single(await Cli().GetTreeDiffAsync(repo.Root, before, after, 0));
        Assert.Equal(2, narrow.Hunks.Sum(static h => h.Lines.Count));

        var full = Assert.Single(await Cli().GetTreeDiffAsync(repo.Root, before, after, GitCli.FullContextLines));
        var hunk = Assert.Single(full.Hunks);
        Assert.Equal(1, hunk.OldStart);
        Assert.Equal(40, hunk.OldCount);
        Assert.Equal(41, hunk.Lines.Count);

        // The existing overload keeps its three lines of context: 3 + 2 changed + 3.
        var classic = Assert.Single(await Cli().GetTreeDiffAsync(repo.Root, before, after));
        Assert.Equal(8, Assert.Single(classic.Hunks).Lines.Count);
    }

    [Fact]
    public async Task GetDiff_with_full_context_and_untracked_files_are_whole_file()
    {
        if (!GitProbe.IsAvailable) return;
        using var repo = new TestRepo();
        var body = string.Join('\n', Enumerable.Range(1, 30).Select(static i => "line" + i)) + "\n";
        repo.Write("f.txt", body);
        repo.Commit("base");
        repo.Write("f.txt", body.Replace("line15\n", "X\n", StringComparison.Ordinal));
        repo.Write("new.txt", "a\nb\nc\n");

        var files = await Cli().GetDiffAsync(repo.Root, new GitDiffOptions { ContextLines = GitCli.FullContextLines, IncludeUntracked = true });

        var hunk = Assert.Single(files.Single(static d => d.Path == "f.txt").Hunks);
        Assert.Equal(1, hunk.OldStart);
        Assert.Equal(30, hunk.OldCount);
        var addHunk = Assert.Single(files.Single(static d => d.Path == "new.txt").Hunks);
        Assert.Equal(3, addHunk.NewCount);
        Assert.Equal(1, addHunk.NewStart);
    }
}
