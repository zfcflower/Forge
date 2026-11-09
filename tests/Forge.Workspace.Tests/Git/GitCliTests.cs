using System.Diagnostics;
using System.Text;
using Xunit;

namespace Forge.Workspace.Tests.Git;

public sealed class GitCliTests
{
    private static readonly string[] SleepArguments = ["-c", "alias.forgewait=!sleep 60", "forgewait"];

    [Fact]
    public async Task Status_reports_modified_staged_renamed_untracked_and_spaced_paths()
    {
        if (!GitProbe.IsAvailable) return;
        using var repo = new TestRepo();
        repo.Write("a.txt", "a\n");
        repo.Write("b.txt", "b\n");
        repo.Write("c.txt", "c\n");
        repo.Commit("base");

        repo.Write("a.txt", "a changed\n");
        repo.Write("b.txt", "b changed\n");
        repo.RunGit("add", "b.txt");
        repo.RunGit("mv", "c.txt", "d.txt");
        repo.Write("new file.txt", "untracked\n");
        repo.Write("dir with space/inner file.txt", "x\n");

        var entries = await new GitCli().GetStatusAsync(repo.Root);
        var byPath = entries.ToDictionary(entry => entry.Path, StringComparer.Ordinal);

        var modified = byPath["a.txt"];
        Assert.Equal(GitStatusKind.Ordinary, modified.Kind);
        Assert.Equal(GitEntryStatus.Unmodified, modified.IndexStatus);
        Assert.Equal(GitEntryStatus.Modified, modified.WorkTreeStatus);
        Assert.True(modified.IsUnstaged);
        Assert.False(modified.IsStaged);

        var staged = byPath["b.txt"];
        Assert.Equal(GitEntryStatus.Modified, staged.IndexStatus);
        Assert.Equal(GitEntryStatus.Unmodified, staged.WorkTreeStatus);
        Assert.True(staged.IsStaged);
        Assert.False(staged.IsUnstaged);

        var renamed = byPath["d.txt"];
        Assert.Equal(GitStatusKind.Renamed, renamed.Kind);
        Assert.Equal("c.txt", renamed.OriginalPath);
        Assert.Equal(GitEntryStatus.Renamed, renamed.IndexStatus);
        Assert.True(renamed.IsStaged);
        Assert.False(byPath.ContainsKey("c.txt"));

        var untracked = byPath["new file.txt"];
        Assert.Equal(GitStatusKind.Untracked, untracked.Kind);
        Assert.Equal(GitEntryStatus.Untracked, untracked.WorkTreeStatus);
        Assert.False(untracked.IsStaged);
        Assert.True(untracked.IsUnstaged);

        Assert.True(byPath.ContainsKey("dir with space/inner file.txt"));
    }

    [Fact]
    public async Task Status_without_untracked_omits_untracked_files()
    {
        if (!GitProbe.IsAvailable) return;
        using var repo = new TestRepo();
        repo.Write("tracked.txt", "1\n");
        repo.Commit("base");
        repo.Write("untracked.txt", "u\n");

        var entries = await new GitCli().GetStatusAsync(repo.Root, includeUntracked: false);

        Assert.Empty(entries);
    }

    [Fact]
    public async Task Status_of_clean_repository_is_empty()
    {
        if (!GitProbe.IsAvailable) return;
        using var repo = new TestRepo();
        repo.Write("tracked.txt", "1\n");
        repo.Commit("base");

        Assert.Empty(await new GitCli().GetStatusAsync(repo.Root));
    }

    [Fact]
    public async Task Commit_with_paths_stages_only_those_paths()
    {
        if (!GitProbe.IsAvailable) return;
        using var repo = new TestRepo();
        repo.Write("a.txt", "a\n");
        repo.Write("b.txt", "b\n");
        repo.Commit("base");
        repo.Write("a.txt", "a2\n");
        repo.Write("b.txt", "b2\n");

        var commit = await new GitCli().CommitAsync(repo.Root, ["a.txt"], "only a");

        Assert.Equal("only a", commit.Subject);
        Assert.Equal(repo.Head(), commit.Id);
        var status = await new GitCli().GetStatusAsync(repo.Root);
        var remaining = Assert.Single(status);
        Assert.Equal("b.txt", remaining.Path);
    }

    [Fact]
    public async Task Commit_without_paths_stages_everything_and_records_author()
    {
        if (!GitProbe.IsAvailable) return;
        using var repo = new TestRepo();
        repo.Write("a.txt", "a\n");
        repo.Commit("base");
        repo.Write("a.txt", "a2\n");
        repo.Write("fresh.txt", "f\n");

        var commit = await new GitCli().CommitAsync(repo.Root, [], "everything");

        Assert.Equal("Forge Test", commit.AuthorName);
        Assert.Equal("forge@example.com", commit.AuthorEmail);
        Assert.Empty(await new GitCli().GetStatusAsync(repo.Root));
        Assert.StartsWith(commit.ShortId, commit.Id, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Commit_message_starting_with_dash_is_data()
    {
        if (!GitProbe.IsAvailable) return;
        using var repo = new TestRepo();
        repo.Write("a.txt", "a\n");
        repo.Commit("base");
        repo.Write("a.txt", "a2\n");

        var commit = await new GitCli().CommitAsync(repo.Root, [], "--amend everything");

        Assert.Equal("--amend everything", commit.Subject);
    }

    [Fact]
    public async Task Path_starting_with_dash_is_treated_as_a_file_name()
    {
        if (!GitProbe.IsAvailable) return;
        using var repo = new TestRepo();
        repo.Write("base.txt", "b\n");
        repo.Commit("base");
        repo.Write("--dash-name.txt", "d\n");

        var commit = await new GitCli().CommitAsync(repo.Root, ["--dash-name.txt"], "dash file");

        Assert.Equal("dash file", commit.Subject);
        Assert.Empty(await new GitCli().GetStatusAsync(repo.Root));
        var logged = await new GitCli().GetLogAsync(repo.Root, new GitLogOptions { Paths = ["--dash-name.txt"] });
        Assert.Equal(["dash file"], logged.Select(static entry => entry.Subject));
    }

    [Fact]
    public async Task Log_honours_limit_skip_and_subject_body_fields()
    {
        if (!GitProbe.IsAvailable) return;
        using var repo = new TestRepo();
        repo.Write("f.txt", "1\n");
        repo.Commit("first");
        repo.Write("f.txt", "2\n");
        repo.Commit("second\n\nBody line one\nBody line two");
        repo.Write("f.txt", "3\n");
        repo.Commit("third");

        var cli = new GitCli();
        var page = await cli.GetLogAsync(repo.Root, new GitLogOptions { Limit = 2, Skip = 1 });
        Assert.Equal(["second", "first"], page.Select(static commit => commit.Subject));

        var all = await cli.GetLogAsync(repo.Root);
        Assert.Equal(["third", "second", "first"], all.Select(static commit => commit.Subject));

        var second = all[1];
        Assert.Contains("Body line one", second.Body, StringComparison.Ordinal);
        Assert.Contains("Body line two", second.Body, StringComparison.Ordinal);
        Assert.StartsWith(second.ShortId, second.Id, StringComparison.Ordinal);
        Assert.NotEqual(default, second.AuthoredAt);
    }

    [Fact]
    public async Task Log_with_unknown_revision_throws_git_exception()
    {
        if (!GitProbe.IsAvailable) return;
        using var repo = new TestRepo();
        repo.Write("f.txt", "1\n");
        repo.Commit("first");

        var error = await Assert.ThrowsAsync<GitException>(() => new GitCli().GetLogAsync(repo.Root, new GitLogOptions { Revision = "no-such-ref" }));

        Assert.NotEqual(0, error.ExitCode);
    }

    [Fact]
    public async Task Get_commit_returns_head_and_throws_for_missing_revision()
    {
        if (!GitProbe.IsAvailable) return;
        using var repo = new TestRepo();
        repo.Write("f.txt", "1\n");
        var head = repo.Commit("first");

        var commit = await new GitCli().GetCommitAsync(repo.Root, "HEAD");

        Assert.Equal(head, commit.Id);
        await Assert.ThrowsAsync<GitException>(() => new GitCli().GetCommitAsync(repo.Root, "does-not-exist"));
    }

    [Fact]
    public async Task Diff_lists_worktree_changes_and_appends_untracked_files_on_request()
    {
        if (!GitProbe.IsAvailable) return;
        using var repo = new TestRepo();
        repo.Write("keep.txt", "one\ntwo\n");
        repo.Commit("base");
        repo.Write("keep.txt", "one\nTWO\nthree\n");
        repo.Write("added.txt", "brand new\n");

        var cli = new GitCli();
        var plain = await cli.GetDiffAsync(repo.Root);
        var modified = Assert.Single(plain);
        Assert.Equal("keep.txt", modified.Path);
        Assert.Equal(FileDiffStatus.Modified, modified.Status);

        var withUntracked = await cli.GetDiffAsync(repo.Root, new GitDiffOptions { IncludeUntracked = true });
        Assert.Equal(["keep.txt", "added.txt"], withUntracked.Select(static file => file.Path));
        var added = withUntracked[1];
        Assert.Equal(FileDiffStatus.Added, added.Status);
        Assert.Equal("brand new", Assert.Single(added.Hunks[0].Lines).Text);
    }

    [Fact]
    public async Task Diff_staged_compares_index_against_head()
    {
        if (!GitProbe.IsAvailable) return;
        using var repo = new TestRepo();
        repo.Write("f.txt", "old\n");
        repo.Commit("base");
        repo.Write("f.txt", "new\n");
        repo.RunGit("add", "f.txt");
        repo.Write("f.txt", "newer\n");

        var staged = await new GitCli().GetDiffAsync(repo.Root, new GitDiffOptions { Staged = true });
        var unstaged = await new GitCli().GetDiffAsync(repo.Root);

        Assert.Equal("new", Assert.Single(staged[0].Hunks[0].LinesOf(DiffLineKind.Added)).Text);
        Assert.Equal("newer", Assert.Single(unstaged[0].Hunks[0].LinesOf(DiffLineKind.Added)).Text);
    }

    [Fact]
    public async Task Diff_between_revisions_and_path_filter()
    {
        if (!GitProbe.IsAvailable) return;
        using var repo = new TestRepo();
        repo.Write("a.txt", "a1\n");
        repo.Write("b.txt", "b1\n");
        var first = repo.Commit("first");
        repo.Write("a.txt", "a2\n");
        repo.Write("b.txt", "b2\n");
        repo.Commit("second");

        var files = await new GitCli().GetDiffAsync(repo.Root, new GitDiffOptions { From = first, To = "HEAD", Paths = ["a.txt"] });

        Assert.Equal(["a.txt"], files.Select(static file => file.Path));
    }

    [Fact]
    public async Task Diff_text_reports_bad_revision_as_exception()
    {
        if (!GitProbe.IsAvailable) return;
        using var repo = new TestRepo();
        repo.Write("a.txt", "a\n");
        repo.Commit("base");

        await Assert.ThrowsAsync<GitException>(() => new GitCli().GetDiffTextAsync(repo.Root, new GitDiffOptions { From = "missing-rev", To = "HEAD" }));
    }

    [Fact]
    public async Task Diff_keeps_a_lone_carriage_return_inside_a_line()
    {
        if (!GitProbe.IsAvailable) return;
        using var repo = new TestRepo();
        repo.Write("cr.txt", "base\n");
        repo.Commit("base");
        repo.WriteBytes("cr.txt", "base\na\rb\n"u8.ToArray());

        var files = await new GitCli().GetDiffAsync(repo.Root);

        var added = Assert.Single(Assert.Single(files).Hunks[0].LinesOf(DiffLineKind.Added));
        Assert.Equal("a\rb", added.Text);
    }
    [Fact]
    public async Task Show_returns_file_content_at_revision_and_null_when_absent()
    {
        if (!GitProbe.IsAvailable) return;
        using var repo = new TestRepo();
        repo.Write("doc.md", "version one\n");
        var first = repo.Commit("first");
        repo.Write("doc.md", "version two\n");
        repo.Commit("second");

        var cli = new GitCli();
        Assert.Equal("version one\n", await cli.GetFileAtRevisionAsync(repo.Root, first, "doc.md"));
        Assert.Equal("version two\n", await cli.GetFileAtRevisionAsync(repo.Root, "HEAD", "doc.md"));
        Assert.Null(await cli.GetFileAtRevisionAsync(repo.Root, "HEAD", "missing.md"));
    }

    [Fact]
    public async Task Show_preserves_crlf_line_endings()
    {
        if (!GitProbe.IsAvailable) return;
        using var repo = new TestRepo();
        repo.Write("win.txt", "first\r\nsecond\r\n");
        repo.Commit("crlf");

        var content = await new GitCli().GetFileAtRevisionAsync(repo.Root, "HEAD", "win.txt");

        Assert.Equal("first\r\nsecond\r\n", content);
    }

    [Fact]
    public async Task Merge_base_of_diverged_branches_is_the_fork_point()
    {
        if (!GitProbe.IsAvailable) return;
        using var repo = new TestRepo();
        repo.Write("f.txt", "base\n");
        var fork = repo.Commit("fork");
        repo.RunGit("checkout", "-q", "-b", "feature");
        repo.Write("f.txt", "feature\n");
        repo.Commit("feature work");
        repo.RunGit("checkout", "-q", "main");
        repo.Write("g.txt", "main\n");
        var mainHead = repo.Commit("main work");

        var cli = new GitCli();
        Assert.Equal(fork, await cli.GetMergeBaseAsync(repo.Root, "main", "feature"));
        Assert.Null(await cli.GetMergeBaseAsync(repo.Root, "main", "no-such-branch"));
        Assert.Equal(mainHead, await cli.RevParseAsync(repo.Root, "main"));
    }

    [Fact]
    public async Task Rev_parse_resolves_full_sha_and_returns_null_for_unknown_revision()
    {
        if (!GitProbe.IsAvailable) return;
        using var repo = new TestRepo();
        repo.Write("f.txt", "1\n");
        var head = repo.Commit("first");

        var cli = new GitCli();
        var sha = await cli.RevParseAsync(repo.Root, "HEAD");

        Assert.Equal(head, sha);
        Assert.Matches("^[0-9a-f]{40}$", sha!);
        Assert.Null(await cli.RevParseAsync(repo.Root, "refs/heads/nope"));
    }

    [Fact]
    public async Task Rev_parse_rejects_option_like_revision_before_launching_git()
    {
        if (!GitProbe.IsAvailable) return;
        using var repo = new TestRepo();
        repo.Write("f.txt", "1\n");
        repo.Commit("first");
        var cli = new GitCli();

        var error = await Assert.ThrowsAsync<ArgumentException>(() => cli.RevParseAsync(repo.Root, "--show-toplevel"));
        Assert.Equal("revision", error.ParamName);

        var missingDirectory = repo.FullPath("no-such-dir");
        await Assert.ThrowsAsync<ArgumentException>(() => cli.RevParseAsync(missingDirectory, "--show-toplevel"));
    }

    [Fact]
    public async Task Repository_detection_and_root_resolution()
    {
        if (!GitProbe.IsAvailable) return;
        using var repo = new TestRepo();
        repo.Write("sub/dir/file.txt", "x\n");
        repo.Commit("base");
        var cli = new GitCli();

        Assert.True(await cli.IsRepositoryAsync(repo.FullPath("sub/dir")));
        Assert.Equal(
            Path.GetFullPath(repo.Root).TrimEnd('\\', '/'),
            (await cli.GetRepositoryRootAsync(repo.FullPath("sub/dir")))!.TrimEnd('\\', '/'),
            ignoreCase: OperatingSystem.IsWindows());
        Assert.Equal("main", await cli.GetCurrentBranchAsync(repo.Root));
    }

    [Fact]
    public async Task Non_repository_directory_is_reported_without_throwing()
    {
        if (!GitProbe.IsAvailable) return;
        using var temp = new TempDirectory();
        var cli = new GitCli();

        Assert.False(await cli.IsRepositoryAsync(temp.Path));
        Assert.Null(await cli.GetRepositoryRootAsync(temp.Path));
        Assert.Null(await cli.GetCurrentBranchAsync(temp.Path));
    }

    [Fact]
    public async Task Current_branch_is_null_when_head_is_detached()
    {
        if (!GitProbe.IsAvailable) return;
        using var repo = new TestRepo();
        repo.Write("f.txt", "1\n");
        var head = repo.Commit("first");
        repo.RunGit("checkout", "-q", "--detach", head);

        Assert.Null(await new GitCli().GetCurrentBranchAsync(repo.Root));
    }

    [Fact]
    public async Task Branches_list_current_flag_and_commit_ids()
    {
        if (!GitProbe.IsAvailable) return;
        using var repo = new TestRepo();
        repo.Write("f.txt", "1\n");
        var head = repo.Commit("first");
        repo.RunGit("branch", "side");

        var branches = await new GitCli().GetBranchesAsync(repo.Root);

        var main = branches.Single(static branch => branch.Name == "main");
        Assert.True(main.IsCurrent);
        Assert.Equal(head, main.CommitId);
        Assert.False(branches.Single(static branch => branch.Name == "side").IsCurrent);
        Assert.NotNull(main.CommittedAt);
    }

    [Fact]
    public async Task Branch_list_marks_remote_refs_and_preserves_full_names_for_slash_branches()
    {
        if (!GitProbe.IsAvailable) return;
        using var repo = new TestRepo();
        repo.Write("f.txt", "first\n");
        var head = repo.Commit("first");
        repo.RunGit("branch", "feature/tooling");
        repo.RunGit("update-ref", "refs/remotes/origin/main", head);
        repo.RunGit("update-ref", "refs/remotes/origin/feature/tooling", head);

        var branches = await new GitCli().GetBranchesAsync(repo.Root, includeRemote: true);

        var local = Assert.Single(branches, static branch => branch.Name == "feature/tooling" && !branch.IsRemote);
        Assert.Equal("refs/heads/feature/tooling", local.RefName);
        var remote = Assert.Single(branches, static branch => branch.Name == "origin/feature/tooling" && branch.IsRemote);
        Assert.Equal("refs/remotes/origin/feature/tooling", remote.RefName);
        Assert.False(remote.IsCurrent);
    }

    [Fact]
    public async Task Default_branch_falls_back_to_main_then_master_then_null()
    {
        if (!GitProbe.IsAvailable) return;
        using var repo = new TestRepo();
        repo.Write("f.txt", "1\n");
        repo.Commit("first");
        var cli = new GitCli();

        Assert.Equal("main", await cli.GetDefaultBranchAsync(repo.Root));

        repo.RunGit("branch", "-m", "main", "trunk-less");
        Assert.Null(await cli.GetDefaultBranchAsync(repo.Root));
    }

    [Fact]
    public async Task Count_commits_ahead_counts_only_unmerged_commits()
    {
        if (!GitProbe.IsAvailable) return;
        using var repo = new TestRepo();
        repo.Write("f.txt", "1\n");
        repo.Commit("first");
        repo.RunGit("checkout", "-q", "-b", "feature");
        repo.Write("f.txt", "2\n");
        repo.Commit("two");
        repo.Write("f.txt", "3\n");
        repo.Commit("three");

        Assert.Equal(2, await new GitCli().CountCommitsAheadAsync(repo.Root, "HEAD", "main"));
        Assert.Equal(0, await new GitCli().CountCommitsAheadAsync(repo.Root, "main", "HEAD"));
    }

    [Fact]
    public async Task Worktree_add_list_remove_round_trip_keeps_branch()
    {
        if (!GitProbe.IsAvailable) return;
        using var repo = new TestRepo();
        repo.Write("f.txt", "1\n");
        repo.Commit("first");
        using var external = new TempDirectory();
        var target = Path.Combine(external.Path, "wt");
        var cli = new GitCli();

        var added = await cli.AddWorktreeAsync(repo.Root, target, "forge/roundtrip");
        Assert.Equal("forge/roundtrip", added.Branch);
        Assert.Contains(await cli.ListWorktreesAsync(repo.Root), worktree => worktree.Branch == "forge/roundtrip");

        await cli.RemoveWorktreeAsync(repo.Root, target);

        Assert.DoesNotContain(await cli.ListWorktreesAsync(repo.Root), worktree => worktree.Branch == "forge/roundtrip");
        Assert.NotNull(await cli.RevParseAsync(repo.Root, "refs/heads/forge/roundtrip"));
    }

    [Fact]
    public async Task Worktree_add_finds_the_worktree_when_the_path_goes_through_a_symlink()
    {
        if (!GitProbe.IsAvailable) return;
        using var repo = new TestRepo();
        repo.Write("f.txt", "1\n");
        repo.Commit("first");
        using var external = new TempDirectory();
        var real = Path.Combine(external.Path, "real");
        Directory.CreateDirectory(real);
        var link = Path.Combine(external.Path, "link");
        try
        {
            Directory.CreateSymbolicLink(link, real);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // Creating a symlink needs a privilege a Windows runner may withhold; the same resolution runs on
            // macOS for every temp directory, because /var is a link to /private/var.
            return;
        }

        var cli = new GitCli();
        var added = await cli.AddWorktreeAsync(repo.Root, Path.Combine(link, "wt"), "forge/linked");

        Assert.Equal("forge/linked", added.Branch);
        Assert.Contains(
            await cli.ListWorktreesAsync(repo.Root),
            worktree => worktree.Branch == "forge/linked" && TestPaths.Same(worktree.Path, added.Path));
    }

    [Fact]
    public async Task Run_captures_exit_code_and_both_streams()
    {
        if (!GitProbe.IsAvailable) return;
        using var repo = new TestRepo();

        var ok = await new GitCli().RunAsync(repo.Root, ["--version"]);
        var failed = await new GitCli().RunAsync(repo.Root, ["rev-parse", "--verify", "--quiet", "nope"]);

        Assert.True(ok.Success);
        Assert.StartsWith("git version", ok.StandardOutput, StringComparison.Ordinal);
        Assert.False(failed.Success);
        Assert.NotEqual(0, failed.ExitCode);
        Assert.False(failed.TimedOut);
    }

    [Fact]
    public async Task Run_reports_missing_executable_as_git_exception()
    {
        if (!GitProbe.IsAvailable) return;
        using var temp = new TempDirectory();
        var cli = new GitCli(Path.Combine(temp.Path, "no-such-git-binary"));

        var error = await Assert.ThrowsAsync<GitException>(() => cli.RunAsync(temp.Path, ["--version"]));
        Assert.Contains("no-such-git-binary", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cancellation_kills_the_process_and_throws()
    {
        if (!GitProbe.IsAvailable) return;
        using var repo = new TestRepo();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var stopwatch = Stopwatch.StartNew();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new GitCli().RunAsync(repo.Root, SleepArguments, cts.Token));

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(30), $"cancellation took {stopwatch.Elapsed}");
    }

    [Fact]
    public async Task Already_cancelled_token_throws_without_waiting_for_the_process()
    {
        if (!GitProbe.IsAvailable) return;
        using var repo = new TestRepo();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var stopwatch = Stopwatch.StartNew();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new GitCli().RunAsync(repo.Root, SleepArguments, cts.Token));

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task Timeout_returns_timed_out_result_instead_of_throwing()
    {
        if (!GitProbe.IsAvailable) return;
        using var repo = new TestRepo();
        var stopwatch = Stopwatch.StartNew();

        var result = await new GitCli().RunAsync(
            repo.Root,
            SleepArguments,
            new GitRunOptions { Timeout = TimeSpan.FromMilliseconds(400) });

        Assert.True(result.TimedOut);
        Assert.False(result.Success);
        Assert.Equal(-1, result.ExitCode);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task Output_beyond_the_cap_is_truncated_while_every_line_is_streamed()
    {
        if (!GitProbe.IsAvailable) return;
        using var repo = new TestRepo();
        var builder = new StringBuilder();
        for (var i = 0; i < 5000; i++)
        {
            builder.Append("line-").Append(i.ToString("D5")).Append('\n');
        }

        repo.Write("big.txt", builder.ToString());
        repo.Commit("big");
        var streamed = 0;
        var options = new GitRunOptions
        {
            MaxCapturedCharacters = 500,
            OnStandardOutputLine = _ => streamed++,
        };

        var result = await new GitCli().RunAsync(repo.Root, ["show", "HEAD:big.txt"], options);

        Assert.True(result.Success);
        Assert.Contains("[output truncated]", result.StandardOutput, StringComparison.Ordinal);
        Assert.True(result.StandardOutput.Length < 600, $"captured {result.StandardOutput.Length} characters");
        Assert.Equal(5000, streamed);
    }

    [Fact]
    public async Task Stream_lines_yields_lines_and_throws_on_failure()
    {
        if (!GitProbe.IsAvailable) return;
        using var repo = new TestRepo();
        repo.Write("f.txt", "1\n");
        repo.Commit("first");
        repo.Write("f.txt", "2\n");
        repo.Commit("second");
        var cli = new GitCli();

        var lines = new List<string>();
        await foreach (var line in cli.StreamLinesAsync(repo.Root, ["log", "--format=%s"]))
        {
            lines.Add(line);
        }

        Assert.Equal(["second", "first"], lines);
        await Assert.ThrowsAsync<GitException>(async () =>
        {
            await foreach (var _ in cli.StreamLinesAsync(repo.Root, ["log", "no-such-ref"]))
            {
            }
        });
    }

    [Fact]
    public async Task Stream_records_splits_on_separator_across_lines()
    {
        if (!GitProbe.IsAvailable) return;
        using var repo = new TestRepo();
        repo.Write("f.txt", "1\n");
        repo.Commit("first");
        var cli = new GitCli();

        var records = new List<string>();
        await foreach (var record in cli.StreamRecordsAsync(repo.Root, ["log", "--format=%s%n%b", "-z", "--no-color"], '\0'))
        {
            records.Add(record);
        }

        Assert.Single(records);
        Assert.StartsWith("first", records[0], StringComparison.Ordinal);
    }

    [Fact]
    public void Constructor_falls_back_to_git_on_blank_executable()
    {
        Assert.Equal("git", new GitCli("   ").ExecutablePath);
        Assert.Equal("custom-git", new GitCli("custom-git").ExecutablePath);
    }
}
