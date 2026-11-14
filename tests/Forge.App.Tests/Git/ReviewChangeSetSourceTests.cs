using System.Text;
using Forge.App.Git;
using Forge.Core.Tools;
using Forge.Workspace;
using Xunit;

namespace Forge.App.Tests.Git;

/// <summary>
/// Pins <see cref="ReviewChangeSetSource"/> against real git repositories: one commit against its first parent, a root
/// commit against the empty tree, the branch plus the uncommitted tree against an explicit base (an untracked file is in
/// the file list but never in the diff text), the HEAD fallback when no default branch resolves, path narrowing in both
/// modes, the text failures (blank directory, not a repository, an unresolvable base, a leading-dash base) and
/// cancellation. Expected failures are results, never exceptions.
/// </summary>
public sealed class ReviewChangeSetSourceTests
{
    private static ReviewChangeSetSource NewSource() => new(new GitCli());

    private static string HeadOf(string repository) => GitTestShell.RunChecked(repository, "rev-parse", "HEAD").Trim();

    private static void Write(string repository, string relativePath, string content) =>
        File.WriteAllText(Path.Combine(repository, relativePath), content, new UTF8Encoding(false));

    private static void Commit(string repository, string message, params (string Path, string Content)[] files)
    {
        foreach (var file in files)
        {
            Write(repository, file.Path, file.Content);
        }

        GitTestShell.RunChecked(repository, "add", "-A");
        GitTestShell.RunChecked(repository, "commit", "-q", "-m", message);
    }

    // ------------------------------------------------------------ commit mode

    [Fact]
    public async Task A_commit_review_diffs_the_commit_against_its_first_parent_and_lists_only_that_commit()
    {
        using var sandbox = GitTestSandbox.Create();
        var work = sandbox.CloneTracked("work");
        sandbox.CommitAndPush(work, "first.txt", "first\n", "first change");
        sandbox.CommitAndPush(work, "second.txt", "second\n", "second change");
        var head = HeadOf(work);
        var parent = GitTestShell.RunChecked(work, "rev-parse", "HEAD~1").Trim();

        var result = await NewSource().BuildAsync(
            new ReviewChangeSetRequest { WorkingDirectory = work, Commit = head },
            CancellationToken.None);

        Assert.Null(result.Error);
        Assert.False(result.IsError);
        var changeSet = result.ChangeSet!;
        Assert.Equal(parent, changeSet.Base);
        Assert.Equal(head, changeSet.HeadCommit);
        var commit = Assert.Single(changeSet.Commits);
        Assert.Equal(head, commit.Sha);
        Assert.Equal("second change", commit.Subject);
        var file = Assert.Single(changeSet.Files);
        Assert.Equal("second.txt", file.Path);
        Assert.Equal("added", file.Status);
        Assert.False(file.IsBinary);
        Assert.Contains("+second", changeSet.DiffText, StringComparison.Ordinal);
        // The earlier commit's file is not part of this change set.
        Assert.DoesNotContain("first.txt", changeSet.DiffText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_root_commit_is_reviewed_against_the_empty_tree_with_a_null_base()
    {
        using var sandbox = GitTestSandbox.Create();
        var work = sandbox.Clone("work");
        sandbox.CommitFile(work, "root.txt", "root\n", "only commit");
        var head = HeadOf(work);

        var result = await NewSource().BuildAsync(
            new ReviewChangeSetRequest { WorkingDirectory = work, Commit = head },
            CancellationToken.None);

        Assert.Null(result.Error);
        var changeSet = result.ChangeSet!;
        Assert.Null(changeSet.Base);
        Assert.Equal(head, changeSet.HeadCommit);
        var commit = Assert.Single(changeSet.Commits);
        Assert.Equal(head, commit.Sha);
        Assert.Equal("only commit", commit.Subject);
        var file = Assert.Single(changeSet.Files);
        Assert.Equal("root.txt", file.Path);
        Assert.Equal("added", file.Status);
        // Non-empty text is what proves the EmptyTreeSha path ran instead of an empty diff.
        Assert.Contains("+root", changeSet.DiffText, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------ branch mode

    [Fact]
    public async Task A_branch_review_against_a_base_lists_the_commit_the_tracked_edit_and_the_untracked_file()
    {
        using var sandbox = GitTestSandbox.Create();
        var work = sandbox.CloneTracked("work");
        var baseline = HeadOf(work);
        sandbox.CommitAndPush(work, "tracked.txt", "tracked change\n", "tracked work");
        var head = HeadOf(work);
        Write(work, "file.txt", "v2\n");
        Write(work, "untracked.txt", "untracked body\n");

        var result = await NewSource().BuildAsync(
            new ReviewChangeSetRequest { WorkingDirectory = work, Base = baseline },
            CancellationToken.None);

        Assert.Null(result.Error);
        var changeSet = result.ChangeSet!;
        Assert.Equal(baseline, changeSet.Base);
        Assert.Equal(head, changeSet.HeadCommit);
        var commit = Assert.Single(changeSet.Commits);
        Assert.Equal(head, commit.Sha);
        Assert.Equal("tracked work", commit.Subject);
        Assert.Equal(3, changeSet.Files.Count);
        Assert.Contains(changeSet.Files, file => file.Path == "tracked.txt" && file.Status == "added");
        Assert.Contains(changeSet.Files, file => file.Path == "file.txt" && file.Status == "modified");
        Assert.Contains(changeSet.Files, file => file.Path == "untracked.txt" && file.Status == "added");
        Assert.Contains("+tracked change", changeSet.DiffText, StringComparison.Ordinal);
        Assert.Contains("-v1", changeSet.DiffText, StringComparison.Ordinal);
        Assert.Contains("+v2", changeSet.DiffText, StringComparison.Ordinal);
        // The text is git's own diff; only the file list names an untracked file, never its content.
        Assert.DoesNotContain("untracked body", changeSet.DiffText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_repository_without_a_resolvable_default_branch_reviews_the_uncommitted_tree_against_head()
    {
        var work = GitTestFiles.NewTempDir("forge-review-nobase-");
        try
        {
            GitTestShell.RunChecked(work, "init", "-q", "-b", "feature");
            Commit(work, "initial", ("file.txt", "v1\n"));
            var head = HeadOf(work);
            Assert.Equal(new[] { "feature" }, GitTestShell.BranchList(work));
            Write(work, "file.txt", "v2\n");
            Write(work, "untracked.txt", "untracked body\n");

            var result = await NewSource().BuildAsync(
                new ReviewChangeSetRequest { WorkingDirectory = work, Base = null },
                CancellationToken.None);

            Assert.Null(result.Error);
            var changeSet = result.ChangeSet!;
            // GetDefaultBranchAsync returns null here (the only branch is "feature" and there is no remote), so
            // ReviewChangeSetSource keeps baseRevision = HEAD: Base is that sha and the commit range HEAD..HEAD is empty.
            Assert.Equal(head, changeSet.Base);
            Assert.Equal(head, changeSet.HeadCommit);
            Assert.Empty(changeSet.Commits);
            Assert.Equal(2, changeSet.Files.Count);
            Assert.Contains(changeSet.Files, file => file.Path == "file.txt" && file.Status == "modified");
            Assert.Contains(changeSet.Files, file => file.Path == "untracked.txt" && file.Status == "added");
            Assert.Contains("+v2", changeSet.DiffText, StringComparison.Ordinal);
            Assert.DoesNotContain("untracked body", changeSet.DiffText, StringComparison.Ordinal);
        }
        finally
        {
            GitTestFiles.DeleteTree(work);
        }
    }

    // ------------------------------------------------------------ path narrowing

    [Fact]
    public async Task Paths_narrow_a_commit_review_to_the_named_file()
    {
        using var sandbox = GitTestSandbox.Create();
        var work = sandbox.CloneTracked("work");
        Commit(work, "two files", ("one.txt", "one\n"), ("two.txt", "two\n"));
        var head = HeadOf(work);

        var result = await NewSource().BuildAsync(
            new ReviewChangeSetRequest { WorkingDirectory = work, Commit = head, Paths = ["one.txt"] },
            CancellationToken.None);

        Assert.Null(result.Error);
        var changeSet = result.ChangeSet!;
        var file = Assert.Single(changeSet.Files);
        Assert.Equal("one.txt", file.Path);
        Assert.Contains("one.txt", changeSet.DiffText, StringComparison.Ordinal);
        Assert.DoesNotContain("two.txt", changeSet.DiffText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Paths_narrow_a_branch_review_to_the_named_file()
    {
        using var sandbox = GitTestSandbox.Create();
        var work = sandbox.CloneTracked("work");
        var baseline = HeadOf(work);
        sandbox.CommitAndPush(work, "one.txt", "one\n", "one file");
        sandbox.CommitAndPush(work, "two.txt", "two\n", "two file");
        Write(work, "three.txt", "three\n");

        var result = await NewSource().BuildAsync(
            new ReviewChangeSetRequest { WorkingDirectory = work, Base = baseline, Paths = ["two.txt"] },
            CancellationToken.None);

        Assert.Null(result.Error);
        var changeSet = result.ChangeSet!;
        var file = Assert.Single(changeSet.Files);
        Assert.Equal("two.txt", file.Path);
        Assert.Equal("added", file.Status);
        Assert.Contains("two.txt", changeSet.DiffText, StringComparison.Ordinal);
        Assert.DoesNotContain("one.txt", changeSet.DiffText, StringComparison.Ordinal);
        Assert.DoesNotContain("three.txt", changeSet.DiffText, StringComparison.Ordinal);
        // The commit list is narrowed too: a commit that only touched one.txt is not part of this change set.
        var commit = Assert.Single(changeSet.Commits);
        Assert.Equal("two file", commit.Subject);
    }

    // ------------------------------------------------------------ failures are results, not exceptions

    [Fact]
    public async Task A_blank_working_directory_fails_with_text()
    {
        var result = await NewSource().BuildAsync(
            new ReviewChangeSetRequest { WorkingDirectory = "   " },
            CancellationToken.None);

        Assert.Null(result.ChangeSet);
        Assert.True(result.IsError);
        Assert.Contains("no working directory", result.Error!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_directory_outside_a_repository_fails_with_text()
    {
        var directory = GitTestFiles.NewTempDir("forge-review-notrepo-");
        try
        {
            var result = await NewSource().BuildAsync(
                new ReviewChangeSetRequest { WorkingDirectory = directory },
                CancellationToken.None);

            Assert.Null(result.ChangeSet);
            Assert.Contains("not inside a git repository", result.Error!, StringComparison.Ordinal);
        }
        finally
        {
            GitTestFiles.DeleteTree(directory);
        }
    }

    [Fact]
    public async Task An_unresolvable_base_fails_with_text()
    {
        using var sandbox = GitTestSandbox.Create();
        var work = sandbox.CloneTracked("work");

        var result = await NewSource().BuildAsync(
            new ReviewChangeSetRequest { WorkingDirectory = work, Base = "does-not-exist" },
            CancellationToken.None);

        Assert.Null(result.ChangeSet);
        Assert.Contains("does not resolve", result.Error!, StringComparison.Ordinal);
        Assert.Contains("does-not-exist", result.Error!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_base_with_a_leading_dash_is_refused_before_git_sees_it()
    {
        using var sandbox = GitTestSandbox.Create();
        var work = sandbox.CloneTracked("work");

        var result = await NewSource().BuildAsync(
            new ReviewChangeSetRequest { WorkingDirectory = work, Base = "--evil" },
            CancellationToken.None);

        Assert.Null(result.ChangeSet);
        Assert.Contains("cannot be used", result.Error!, StringComparison.Ordinal);
        Assert.Contains("leading '-'", result.Error!, StringComparison.Ordinal);
        // A base that reached git would surface as the generic "Reading the change set failed" text instead.
        Assert.DoesNotContain("Reading the change set failed", result.Error!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_base_that_moved_ahead_is_compared_from_the_merge_base_not_from_the_base_tip()
    {
        // The two-dot trap: `git diff origin/main` against a base that gained commits would show those upstream commits as
        // deletions in this change set. The rest of the app reads `base...HEAD`, so a review must too.
        using var sandbox = GitTestSandbox.Create();
        var work = sandbox.CloneTracked("work");
        var fork = HeadOf(work);
        var other = sandbox.Clone("other");
        sandbox.CommitAndPush(other, "upstream.txt", "upstream\n", "upstream moved on");
        GitTestShell.RunChecked(work, "fetch", "-q", "origin");
        Commit(work, "this branch only", ("feat.txt", "feat\n"));

        var result = await NewSource().BuildAsync(
            new ReviewChangeSetRequest { WorkingDirectory = work, Base = "origin/main" },
            CancellationToken.None);

        Assert.Null(result.Error);
        var changeSet = result.ChangeSet!;
        Assert.Equal(fork, changeSet.Base);
        var file = Assert.Single(changeSet.Files);
        Assert.Equal("feat.txt", file.Path);
        Assert.DoesNotContain("upstream.txt", changeSet.DiffText, StringComparison.Ordinal);
        var commit = Assert.Single(changeSet.Commits);
        Assert.Equal("this branch only", commit.Subject);
    }

    [Fact]
    public async Task The_default_base_is_resolved_to_a_commit_so_the_package_can_shorten_it()
    {
        // GetDefaultBranchAsync answers with a ref name such as origin/main; shortening that to seven characters would print
        // "base origin/". `remote set-head` because a bare origin created empty has no origin/HEAD to clone yet.
        using var sandbox = GitTestSandbox.Create();
        var work = sandbox.CloneTracked("work");
        var baseline = HeadOf(work);
        GitTestShell.RunChecked(work, "remote", "set-head", "origin", "-a");
        Commit(work, "this branch only", ("feat.txt", "feat\n"));

        var result = await NewSource().BuildAsync(
            new ReviewChangeSetRequest { WorkingDirectory = work },
            CancellationToken.None);

        Assert.Null(result.Error);
        var changeSet = result.ChangeSet!;
        Assert.Equal(baseline, changeSet.Base);
        Assert.Equal(40, changeSet.Base!.Length);
        Assert.DoesNotContain("origin/", changeSet.Base, StringComparison.Ordinal);
        var commit = Assert.Single(changeSet.Commits);
        Assert.Equal("this branch only", commit.Subject);
    }

    [Fact]
    public async Task A_default_branch_that_is_the_current_branch_reviews_the_uncommitted_tree()
    {
        // Same semantic as git_diff: base = the branch you are on is no branch diff at all, so what is left to review is
        // the working tree. Saying so is better than failing, and Base names exactly what was compared against.
        using var sandbox = GitTestSandbox.Create();
        var work = sandbox.CloneTracked("work");
        GitTestShell.RunChecked(work, "remote", "set-head", "origin", "-a");
        GitTestShell.RunChecked(work, "push", "-q", "origin", "main");
        var head = HeadOf(work);
        Write(work, "file.txt", "v2\n");

        var result = await NewSource().BuildAsync(
            new ReviewChangeSetRequest { WorkingDirectory = work },
            CancellationToken.None);

        Assert.Null(result.Error);
        var changeSet = result.ChangeSet!;
        Assert.Equal(head, changeSet.Base);
        Assert.Empty(changeSet.Commits);
        Assert.Contains("+v2", changeSet.DiffText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_commit_that_is_gone_fails_with_text_instead_of_gits_own_fatal_line()
    {
        using var sandbox = GitTestSandbox.Create();
        var work = sandbox.CloneTracked("work");

        var result = await NewSource().BuildAsync(
            new ReviewChangeSetRequest { WorkingDirectory = work, Commit = "0123456789abcdef0123456789abcdef01234567" },
            CancellationToken.None);

        Assert.Null(result.ChangeSet);
        Assert.Contains("does not exist in this repository", result.Error!, StringComparison.Ordinal);
        Assert.DoesNotContain("bad object", result.Error!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_change_set_carries_the_paths_it_was_narrowed_to()
    {
        using var sandbox = GitTestSandbox.Create();
        var work = sandbox.CloneTracked("work");
        var baseline = HeadOf(work);
        Commit(work, "two files", ("one.txt", "one\n"), ("two.txt", "two\n"));

        var result = await NewSource().BuildAsync(
            new ReviewChangeSetRequest { WorkingDirectory = work, Base = baseline, Paths = ["one.txt"] },
            CancellationToken.None);

        Assert.Null(result.Error);
        var changeSet = result.ChangeSet!;
        Assert.Equal(["one.txt"], changeSet.Paths);
        Assert.False(changeSet.CommitsTruncated);
    }

    // ------------------------------------------------------------ cancellation and the result contract

    [Fact]
    public async Task A_cancelled_token_surfaces_the_cancellation_instead_of_a_failed_result()
    {
        using var sandbox = GitTestSandbox.Create();
        var work = sandbox.CloneTracked("work");
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => NewSource().BuildAsync(
            new ReviewChangeSetRequest { WorkingDirectory = work },
            cts.Token));
    }

    [Fact]
    public async Task A_successful_read_is_never_also_an_error_and_is_not_marked_truncated()
    {
        using var sandbox = GitTestSandbox.Create();
        var work = sandbox.CloneTracked("work");
        Write(work, "file.txt", "v2\n");

        var result = await NewSource().BuildAsync(
            new ReviewChangeSetRequest { WorkingDirectory = work, Base = "main" },
            CancellationToken.None);

        Assert.Null(result.Error);
        Assert.False(result.IsError);
        var changeSet = result.ChangeSet!;
        Assert.False(changeSet.DiffTruncated);
        var file = Assert.Single(changeSet.Files);
        Assert.Equal("file.txt", file.Path);
        Assert.Equal("modified", file.Status);
        Assert.Contains("+v2", changeSet.DiffText, StringComparison.Ordinal);
    }
}
