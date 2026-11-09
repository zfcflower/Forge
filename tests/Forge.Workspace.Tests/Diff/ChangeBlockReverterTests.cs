using System.Text;
using Xunit;

namespace Forge.Workspace.Tests.Diff;

public sealed class ChangeBlockReverterTests
{
    private static string Numbered(int count, string prefix = "line") =>
        string.Concat(Enumerable.Range(1, count).Select(i => prefix + i + "\n"));

    private static async Task<FileDiff> DiffOf(TestRepo repo, string path, int context = 3)
    {
        var files = await new GitCli().GetDiffAsync(repo.Root, new GitDiffOptions { ContextLines = context, IncludeUntracked = true });
        return files.Single(f => f.Path == path);
    }

    private static string Read(TestRepo repo, string path) => File.ReadAllText(repo.FullPath(path), new UTF8Encoding(false));

    [Fact]
    public async Task RevertBlock_restores_exactly_one_block_of_several()
    {
        if (!GitProbe.IsAvailable) return;
        using var repo = new TestRepo();
        var original = Numbered(30);
        repo.Write("f.txt", original);
        repo.Commit("base");
        repo.Write("f.txt", original.Replace("line5\n", "FIVE\nFIVE-b\n", StringComparison.Ordinal).Replace("line25\n", "TWENTY-FIVE\n", StringComparison.Ordinal));
        var diff = await DiffOf(repo, "f.txt");
        var blocks = DiffBlocks.Split(diff);
        Assert.Equal(2, blocks.Count);

        var result = await ChangeBlockReverter.RevertBlockAsync(repo.Root, diff, blocks[0]);

        Assert.Equal(RevertOutcome.Reverted, result.Outcome);
        Assert.Equal(original.Replace("line25\n", "TWENTY-FIVE\n", StringComparison.Ordinal), Read(repo, "f.txt"));
    }

    [Fact]
    public async Task RevertBlock_follows_a_drifted_position_when_the_match_is_unique()
    {
        if (!GitProbe.IsAvailable) return;
        using var repo = new TestRepo();
        var original = Numbered(30);
        repo.Write("f.txt", original);
        repo.Commit("base");
        repo.Write("f.txt", original.Replace("line20\n", "CHANGED\n", StringComparison.Ordinal));
        var diff = await DiffOf(repo, "f.txt");

        // Lines inserted above the block after the diff was taken push it down by 3.
        repo.Write("f.txt", "x1\nx2\nx3\n" + original.Replace("line20\n", "CHANGED\n", StringComparison.Ordinal));
        var result = await ChangeBlockReverter.RevertBlockAsync(repo.Root, diff, DiffBlocks.Split(diff)[0]);

        Assert.Equal(RevertOutcome.Reverted, result.Outcome);
        Assert.Equal("x1\nx2\nx3\n" + original, Read(repo, "f.txt"));
    }

    [Fact]
    public async Task RevertBlock_reports_FileChanged_when_the_text_is_gone_or_ambiguous_and_leaves_the_file()
    {
        if (!GitProbe.IsAvailable) return;
        using var repo = new TestRepo();
        var original = Numbered(30);
        repo.Write("f.txt", original);
        repo.Commit("base");
        repo.Write("f.txt", original.Replace("line20\n", "CHANGED\n", StringComparison.Ordinal));
        var diff = await DiffOf(repo, "f.txt");
        var block = DiffBlocks.Split(diff)[0];

        var edited = original.Replace("line20\n", "SOMETHING ELSE\n", StringComparison.Ordinal);
        repo.Write("f.txt", edited);
        Assert.Equal(RevertOutcome.FileChanged, (await ChangeBlockReverter.RevertBlockAsync(repo.Root, diff, block)).Outcome);
        Assert.Equal(edited, Read(repo, "f.txt"));

        // Two equally good matches on both sides of the expected position (equidistant): ambiguous.
        var lines = Numbered(60, "pad").Split('\n').ToList();
        lines[9] = "CHANGED";
        lines[29] = "CHANGED";
        lines[49] = "CHANGED";
        repo.Write("f.txt", string.Join('\n', lines));
        Assert.Equal(RevertOutcome.FileChanged, (await ChangeBlockReverter.RevertBlockAsync(repo.Root, diff, block)).Outcome);
    }

    [Fact]
    public async Task Revert_preserves_CRLF_line_endings()
    {
        if (!GitProbe.IsAvailable) return;
        using var repo = new TestRepo();
        var original = Numbered(10).Replace("\n", "\r\n", StringComparison.Ordinal);
        repo.Write("f.txt", original);
        repo.Commit("base");
        var changed = original.Replace("line4\r\n", "FOUR\r\nFOUR-b\r\n", StringComparison.Ordinal);
        repo.Write("f.txt", changed);
        var diff = await DiffOf(repo, "f.txt");

        var result = await ChangeBlockReverter.RevertFileAsync(repo.Root, diff);

        Assert.Equal(RevertOutcome.Reverted, result.Outcome);
        Assert.Equal(original, Read(repo, "f.txt"));
    }

    [Fact]
    public async Task Revert_preserves_a_missing_trailing_newline()
    {
        if (!GitProbe.IsAvailable) return;
        using var repo = new TestRepo();
        repo.Write("f.txt", "a\nb\nc");
        repo.Commit("base");
        repo.Write("f.txt", "a\nb\nC");
        var diff = await DiffOf(repo, "f.txt");

        Assert.Equal(RevertOutcome.Reverted, (await ChangeBlockReverter.RevertFileAsync(repo.Root, diff)).Outcome);
        Assert.Equal("a\nb\nc", Read(repo, "f.txt"));
    }

    [Fact]
    public async Task Revert_preserves_a_utf8_bom()
    {
        if (!GitProbe.IsAvailable) return;
        using var repo = new TestRepo();
        byte[] Bytes(string text) => [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(text)];
        repo.WriteBytes("f.txt", Bytes("a\nb\nc\n"));
        repo.Commit("base");
        repo.WriteBytes("f.txt", Bytes("a\nB\nc\n"));
        var diff = await DiffOf(repo, "f.txt");

        Assert.Equal(RevertOutcome.Reverted, (await ChangeBlockReverter.RevertFileAsync(repo.Root, diff)).Outcome);
        Assert.Equal(Bytes("a\nb\nc\n"), File.ReadAllBytes(repo.FullPath("f.txt")));
    }

    [Fact]
    public async Task RevertBlock_reinserts_a_pure_deletion_between_its_context_lines()
    {
        if (!GitProbe.IsAvailable) return;
        using var repo = new TestRepo();
        var original = Numbered(20);
        repo.Write("f.txt", original);
        repo.Commit("base");
        repo.Write("f.txt", original.Replace("line10\nline11\n", string.Empty, StringComparison.Ordinal));
        var diff = await DiffOf(repo, "f.txt");
        var block = Assert.Single(DiffBlocks.Split(diff));
        Assert.Empty(block.NewLines);

        Assert.Equal(RevertOutcome.Reverted, (await ChangeBlockReverter.RevertBlockAsync(repo.Root, diff, block)).Outcome);
        Assert.Equal(original, Read(repo, "f.txt"));
    }

    [Fact]
    public async Task RevertBlock_of_a_pure_deletion_fails_when_the_surrounding_context_changed()
    {
        if (!GitProbe.IsAvailable) return;
        using var repo = new TestRepo();
        var original = Numbered(20);
        repo.Write("f.txt", original);
        repo.Commit("base");
        var deleted = original.Replace("line10\nline11\n", string.Empty, StringComparison.Ordinal);
        repo.Write("f.txt", deleted);
        var diff = await DiffOf(repo, "f.txt");
        var block = Assert.Single(DiffBlocks.Split(diff));

        var edited = deleted.Replace("line9\n", "other\n", StringComparison.Ordinal).Replace("line12\n", "other2\n", StringComparison.Ordinal);
        repo.Write("f.txt", edited);

        Assert.Equal(RevertOutcome.FileChanged, (await ChangeBlockReverter.RevertBlockAsync(repo.Root, diff, block)).Outcome);
        Assert.Equal(edited, Read(repo, "f.txt"));
    }

    [Fact]
    public async Task RevertBlock_handles_a_pure_deletion_at_the_start_and_end_of_the_file()
    {
        if (!GitProbe.IsAvailable) return;
        using var repo = new TestRepo();
        var original = Numbered(12);
        repo.Write("f.txt", original);
        repo.Commit("base");
        repo.Write("f.txt", original.Replace("line1\n", string.Empty, StringComparison.Ordinal).Replace("line12\n", string.Empty, StringComparison.Ordinal));
        var diff = await DiffOf(repo, "f.txt");

        Assert.Equal(RevertOutcome.Reverted, (await ChangeBlockReverter.RevertFileAsync(repo.Root, diff)).Outcome);
        Assert.Equal(original, Read(repo, "f.txt"));
    }

    [Fact]
    public async Task RevertFile_reverts_every_block_of_a_modified_file_in_one_write()
    {
        if (!GitProbe.IsAvailable) return;
        using var repo = new TestRepo();
        var original = Numbered(60);
        repo.Write("f.txt", original);
        repo.Commit("base");
        repo.Write("f.txt", original
            .Replace("line3\n", "A\nA2\n", StringComparison.Ordinal)
            .Replace("line30\n", string.Empty, StringComparison.Ordinal)
            .Replace("line55\n", "line55\nextra\n", StringComparison.Ordinal));
        var diff = await DiffOf(repo, "f.txt");
        Assert.Equal(3, DiffBlocks.Split(diff).Count);

        Assert.Equal(RevertOutcome.Reverted, (await ChangeBlockReverter.RevertFileAsync(repo.Root, diff)).Outcome);
        Assert.Equal(original, Read(repo, "f.txt"));
    }

    [Fact]
    public async Task RevertFile_changes_nothing_when_any_block_no_longer_matches()
    {
        if (!GitProbe.IsAvailable) return;
        using var repo = new TestRepo();
        var original = Numbered(60);
        repo.Write("f.txt", original);
        repo.Commit("base");
        repo.Write("f.txt", original.Replace("line3\n", "A\n", StringComparison.Ordinal).Replace("line50\n", "B\n", StringComparison.Ordinal));
        var diff = await DiffOf(repo, "f.txt");

        var edited = original.Replace("line3\n", "A\n", StringComparison.Ordinal).Replace("line50\n", "C\n", StringComparison.Ordinal);
        repo.Write("f.txt", edited);

        Assert.Equal(RevertOutcome.FileChanged, (await ChangeBlockReverter.RevertFileAsync(repo.Root, diff)).Outcome);
        Assert.Equal(edited, Read(repo, "f.txt"));
    }

    [Fact]
    public async Task RevertFile_reports_Missing_for_a_modified_file_that_was_removed()
    {
        if (!GitProbe.IsAvailable) return;
        using var repo = new TestRepo();
        repo.Write("f.txt", "a\nb\n");
        repo.Commit("base");
        repo.Write("f.txt", "a\nB\n");
        var diff = await DiffOf(repo, "f.txt");
        File.Delete(repo.FullPath("f.txt"));

        Assert.Equal(RevertOutcome.Missing, (await ChangeBlockReverter.RevertFileAsync(repo.Root, diff)).Outcome);
        Assert.Equal(RevertOutcome.Missing, (await ChangeBlockReverter.RevertBlockAsync(repo.Root, diff, DiffBlocks.Split(diff)[0])).Outcome);
    }

    [Fact]
    public async Task RevertFile_deletes_an_added_file_only_while_its_content_still_matches()
    {
        if (!GitProbe.IsAvailable) return;
        using var repo = new TestRepo();
        repo.Write("keep.txt", "k\n");
        repo.Commit("base");
        repo.Write("new.txt", "a\nb\n");
        var diff = await DiffOf(repo, "new.txt");
        Assert.Equal(FileDiffStatus.Added, diff.Status);

        repo.Write("new.txt", "a\nb\nc\n");
        Assert.Equal(RevertOutcome.FileChanged, (await ChangeBlockReverter.RevertFileAsync(repo.Root, diff)).Outcome);
        Assert.True(File.Exists(repo.FullPath("new.txt")));

        repo.Write("new.txt", "a\nb\n");
        Assert.Equal(RevertOutcome.Reverted, (await ChangeBlockReverter.RevertFileAsync(repo.Root, diff)).Outcome);
        Assert.False(File.Exists(repo.FullPath("new.txt")));

        Assert.Equal(RevertOutcome.Missing, (await ChangeBlockReverter.RevertFileAsync(repo.Root, diff)).Outcome);
    }

    [Fact]
    public async Task RevertFile_recreates_a_deleted_file_from_a_full_context_diff()
    {
        if (!GitProbe.IsAvailable) return;
        using var repo = new TestRepo();
        repo.Write("gone.txt", "one\ntwo\nthree\n");
        repo.Commit("base");
        File.Delete(repo.FullPath("gone.txt"));
        var diff = await DiffOf(repo, "gone.txt");
        Assert.Equal(FileDiffStatus.Deleted, diff.Status);

        Assert.Equal(RevertOutcome.Reverted, (await ChangeBlockReverter.RevertFileAsync(repo.Root, diff)).Outcome);
        Assert.Equal("one\ntwo\nthree\n", Read(repo, "gone.txt"));

        // The path is occupied again: never overwrite.
        Assert.Equal(RevertOutcome.FileChanged, (await ChangeBlockReverter.RevertFileAsync(repo.Root, diff)).Outcome);
    }

    [Fact]
    public async Task RevertFile_recreates_a_deleted_file_without_trailing_newline()
    {
        if (!GitProbe.IsAvailable) return;
        using var repo = new TestRepo();
        repo.Write("gone.txt", "one\ntwo");
        repo.Commit("base");
        File.Delete(repo.FullPath("gone.txt"));
        var diff = await DiffOf(repo, "gone.txt");

        Assert.Equal(RevertOutcome.Reverted, (await ChangeBlockReverter.RevertFileAsync(repo.Root, diff)).Outcome);
        Assert.Equal("one\ntwo", Read(repo, "gone.txt"));
    }

    [Fact]
    public async Task RevertFile_is_Unsupported_when_a_deleted_file_diff_is_not_whole()
    {
        var removed = Enumerable.Range(1, 3).Select(i => new DiffLine(DiffLineKind.Removed, i, 0, "l" + i)).ToList();
        var partial = new FileDiff("gone.txt", null, FileDiffStatus.Deleted, false, [new DiffHunk(1, 10, 0, 0, removed)]);
        using var sandbox = new TempDirectory();

        Assert.Equal(RevertOutcome.Unsupported, (await ChangeBlockReverter.RevertFileAsync(sandbox.Path, partial)).Outcome);
        Assert.False(File.Exists(Path.Combine(sandbox.Path, "gone.txt")));

        var offset = new FileDiff("gone.txt", null, FileDiffStatus.Deleted, false, [new DiffHunk(5, 3, 0, 0, removed)]);
        Assert.Equal(RevertOutcome.Unsupported, (await ChangeBlockReverter.RevertFileAsync(sandbox.Path, offset)).Outcome);
    }

    [Fact]
    public async Task Rename_copy_binary_and_truncated_diffs_are_Unsupported()
    {
        using var sandbox = new TempDirectory();
        File.WriteAllText(Path.Combine(sandbox.Path, "n.txt"), "x\n");
        var hunk = new DiffHunk(1, 1, 1, 1, [new DiffLine(DiffLineKind.Removed, 1, 0, "y"), new DiffLine(DiffLineKind.Added, 0, 1, "x")]);
        var cases = new[]
        {
            new FileDiff("o.txt", "n.txt", FileDiffStatus.Renamed, false, [hunk]),
            new FileDiff("o.txt", "n.txt", FileDiffStatus.Copied, false, [hunk]),
            new FileDiff("n.txt", "n.txt", FileDiffStatus.Modified, true, []),
            new FileDiff("n.txt", "n.txt", FileDiffStatus.Modified, false, [hunk]) { IsTruncated = true },
        };

        foreach (var diff in cases)
        {
            Assert.Equal(RevertOutcome.Unsupported, (await ChangeBlockReverter.RevertFileAsync(sandbox.Path, diff)).Outcome);
            Assert.Equal(RevertOutcome.Unsupported, (await ChangeBlockReverter.RevertBlockAsync(sandbox.Path, diff, new ChangeBlock(0, 1, ["y"], 1, ["x"], "h"))).Outcome);
        }

        Assert.Equal("x\n", File.ReadAllText(Path.Combine(sandbox.Path, "n.txt")));
    }

    [Fact]
    public async Task Paths_escaping_the_repository_are_refused()
    {
        using var sandbox = new TempDirectory();
        var hunk = new DiffHunk(1, 1, 1, 1, [new DiffLine(DiffLineKind.Removed, 1, 0, "y"), new DiffLine(DiffLineKind.Added, 0, 1, "x")]);
        var diff = new FileDiff("../outside.txt", "../outside.txt", FileDiffStatus.Modified, false, [hunk]);

        Assert.Equal(RevertOutcome.Unsupported, (await ChangeBlockReverter.RevertFileAsync(sandbox.Path, diff)).Outcome);
    }

    [Fact]
    public async Task Cancelled_token_is_observed_before_writing()
    {
        if (!GitProbe.IsAvailable) return;
        using var repo = new TestRepo();
        repo.Write("f.txt", "a\nb\n");
        repo.Commit("base");
        repo.Write("f.txt", "a\nB\n");
        var diff = await DiffOf(repo, "f.txt");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ChangeBlockReverter.RevertFileAsync(repo.Root, diff, cts.Token));
        Assert.Equal("a\nB\n", Read(repo, "f.txt"));
    }

    /// <summary>A fact that is reported as skipped (visibly) when git is not installed.</summary>
    private sealed class GitFactAttribute : FactAttribute
    {
        public GitFactAttribute()
        {
            if (!GitProbe.IsAvailable)
            {
                Skip = "git is not available";
            }
        }
    }

    private static async Task AssertRoundTrip(string baseText, string workingText, bool blockwise = false)
    {
        using var repo = new TestRepo();
        repo.Write("f.txt", baseText);
        repo.Commit("base");
        repo.Write("f.txt", workingText);
        var diff = await DiffOf(repo, "f.txt");

        if (blockwise)
        {
            // Bottom-up, one block at a time against the original diff: positions are matched by content.
            foreach (var block in DiffBlocks.Split(diff).Reverse())
            {
                Assert.Equal(RevertOutcome.Reverted, (await ChangeBlockReverter.RevertBlockAsync(repo.Root, diff, block)).Outcome);
            }
        }
        else
        {
            Assert.Equal(RevertOutcome.Reverted, (await ChangeBlockReverter.RevertFileAsync(repo.Root, diff)).Outcome);
        }

        Assert.Equal(baseText, Read(repo, "f.txt"));
        Assert.Equal(string.Empty, repo.RunGit("diff", "--", "f.txt"));
    }

    [GitFact]
    public Task Revert_restores_the_final_newline_that_the_working_file_lost() => AssertRoundTrip("a\n", "a");

    [GitFact]
    public Task Revert_block_restores_the_final_newline_that_the_working_file_lost() => AssertRoundTrip("a\n", "a", blockwise: true);

    [GitFact]
    public Task Revert_removes_the_final_newline_when_the_base_had_none() => AssertRoundTrip("a", "a\nb\n");

    [GitFact]
    public Task Revert_block_removes_the_final_newline_when_the_base_had_none() => AssertRoundTrip("a", "a\nb\n", blockwise: true);

    [GitFact]
    public Task Revert_restores_a_deleted_unterminated_last_line() => AssertRoundTrip("a\nb", "a\n");

    [GitFact]
    public Task Revert_CRLF_file_whose_last_line_lost_its_final_newline() =>
        AssertRoundTrip("a\r\nb\r\nc\r\n", "a\r\nb\r\nc");

    [GitFact]
    public Task Revert_CRLF_file_whose_last_line_gained_a_final_newline() =>
        AssertRoundTrip("a\r\nb\r\nc", "a\r\nb\r\nc\r\nd\r\n");

    [GitFact]
    public Task Revert_CRLF_block_whose_last_line_lost_its_final_newline() =>
        AssertRoundTrip("a\r\nb\r\nc\r\n", "a\r\nb\r\nc", blockwise: true);

    [GitFact]
    public async Task Revert_file_with_several_blocks_where_the_last_one_flips_the_final_newline()
    {
        var baseText = Numbered(30);
        var working = baseText.Replace("line5\n", "FIVE\nFIVE-b\n", StringComparison.Ordinal).TrimEnd('\n');
        await AssertRoundTrip(baseText, working);
        await AssertRoundTrip(baseText, working, blockwise: true);
    }

    [GitFact]
    public Task Revert_file_with_several_blocks_where_the_last_one_restores_a_missing_final_newline()
    {
        var baseText = Numbered(30).TrimEnd('\n');
        var working = Numbered(30).Replace("line5\n", "FIVE\n", StringComparison.Ordinal) + "extra\n";
        return AssertRoundTrip(baseText, working);
    }
}
