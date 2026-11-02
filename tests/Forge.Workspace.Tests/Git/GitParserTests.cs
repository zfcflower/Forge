using Xunit;

namespace Forge.Workspace.Tests.Git;

public sealed class GitParserTests
{
    private const string Hash1 = "1111111111111111111111111111111111111111";
    private const string Hash2 = "2222222222222222222222222222222222222222";
    private const string Hash3 = "3333333333333333333333333333333333333333";
    private const string ZeroHash = "0000000000000000000000000000000000000000";

    private static IReadOnlyList<GitStatusEntry> ParseStatus(string porcelain) =>
        Internals.InvokeStatic<IReadOnlyList<GitStatusEntry>>("GitStatusParser", "Parse", porcelain);

    private static IReadOnlyList<GitWorktree> ParseWorktrees(string porcelain) =>
        Internals.InvokeStatic<IReadOnlyList<GitWorktree>>("GitWorktreeParser", "Parse", porcelain);

    private static string Unquote(string value) =>
        Internals.InvokeStatic<string>("GitQuoting", "Unquote", value);

    [Fact]
    public void Status_parses_ordinary_entries_from_literal_v2_sample()
    {
        var porcelain = string.Join('\0',
            $"1 .M N... 100644 100644 100644 {Hash1} {Hash1} a.txt",
            $"1 M. N... 100644 100644 100644 {Hash1} {Hash2} b.txt",
            $"1 MM N... 100644 100644 100644 {Hash1} {Hash2} both.txt",
            $"1 A. N... 000000 100644 100644 {ZeroHash} {Hash3} added.txt",
            $"1 .D N... 100644 100644 000000 {Hash1} {Hash1} gone.txt",
            $"1 .T N... 100644 100644 120000 {Hash1} {Hash1} link.txt",
            string.Empty);

        var entries = ParseStatus(porcelain);

        Assert.Equal(6, entries.Count);
        Assert.Equal(new GitStatusEntry("a.txt", GitStatusKind.Ordinary, GitEntryStatus.Unmodified, GitEntryStatus.Modified), entries[0]);
        Assert.Equal(new GitStatusEntry("b.txt", GitStatusKind.Ordinary, GitEntryStatus.Modified, GitEntryStatus.Unmodified), entries[1]);
        Assert.Equal(GitEntryStatus.Modified, entries[2].IndexStatus);
        Assert.Equal(GitEntryStatus.Modified, entries[2].WorkTreeStatus);
        Assert.Equal(GitEntryStatus.Added, entries[3].IndexStatus);
        Assert.Equal(GitEntryStatus.Deleted, entries[4].WorkTreeStatus);
        Assert.Equal(GitEntryStatus.TypeChanged, entries[5].WorkTreeStatus);
    }

    [Fact]
    public void Status_parses_rename_with_original_path_token_and_spaces()
    {
        var porcelain = $"2 R. N... 100644 100644 100644 {Hash1} {Hash2} R100 new name.txt\0old name.txt\0";

        var entry = Assert.Single(ParseStatus(porcelain));

        Assert.Equal(new GitStatusEntry("new name.txt", GitStatusKind.Renamed, GitEntryStatus.Renamed, GitEntryStatus.Unmodified, "old name.txt"), entry);
        Assert.True(entry.IsStaged);
    }

    [Fact]
    public void Status_parses_copy_and_keeps_following_entries_aligned()
    {
        var porcelain = $"2 C. N... 100644 100644 100644 {Hash1} {Hash2} C90 copy of.txt\0original.txt\0? after.txt\0";

        var entries = ParseStatus(porcelain);

        Assert.Equal(2, entries.Count);
        Assert.Equal(GitStatusKind.Copied, entries[0].Kind);
        Assert.Equal("copy of.txt", entries[0].Path);
        Assert.Equal("original.txt", entries[0].OriginalPath);
        Assert.Equal(new GitStatusEntry("after.txt", GitStatusKind.Untracked, GitEntryStatus.Unmodified, GitEntryStatus.Untracked), entries[1]);
    }

    [Fact]
    public void Status_parses_unmerged_untracked_and_ignored_entries()
    {
        var porcelain = string.Join('\0',
            $"u UU N... 100644 100644 100644 100644 {Hash1} {Hash2} {Hash3} conflict file.txt",
            "? untracked dir/x.txt",
            "! bin/tool.exe",
            string.Empty);

        var entries = ParseStatus(porcelain);

        Assert.Equal(GitStatusKind.Unmerged, entries[0].Kind);
        Assert.Equal("conflict file.txt", entries[0].Path);
        Assert.Equal(GitEntryStatus.Unmerged, entries[0].IndexStatus);
        Assert.Equal("untracked dir/x.txt", entries[1].Path);
        Assert.Equal(GitStatusKind.Untracked, entries[1].Kind);
        Assert.Equal(GitStatusKind.Ignored, entries[2].Kind);
        Assert.Equal("bin/tool.exe", entries[2].Path);
        Assert.False(entries[2].IsStaged);
    }

    [Fact]
    public void Status_ignores_empty_and_truncated_records()
    {
        Assert.Empty(ParseStatus(string.Empty));
        Assert.Empty(ParseStatus("\0\0"));
        Assert.Empty(ParseStatus("1 .M N... 100644"));
    }

    [Fact]
    public void Status_of_plain_ordinary_record_has_no_original_path()
    {
        var entry = Assert.Single(ParseStatus($"1 .M N... 100644 100644 100644 {Hash1} {Hash1} with space/dir file.txt\0"));

        Assert.Equal("with space/dir file.txt", entry.Path);
        Assert.Null(entry.OriginalPath);
    }

    [Fact]
    public void Worktree_parses_main_detached_locked_prunable_and_bare_entries()
    {
        var porcelain = string.Join('\n',
            "worktree /srv/repo",
            $"HEAD {Hash1}",
            "branch refs/heads/main",
            string.Empty,
            "worktree /srv/wt-detached",
            $"HEAD {Hash2}",
            "detached",
            string.Empty,
            "worktree /srv/wt-locked",
            $"HEAD {Hash3}",
            "branch refs/heads/forge/locked",
            "locked machine is asleep",
            string.Empty,
            "worktree /srv/wt-gone",
            $"HEAD {Hash1}",
            "branch refs/heads/forge/gone",
            "prunable gitdir file points to non-existent location",
            string.Empty,
            "worktree /srv/bare.git",
            "bare",
            string.Empty);

        var worktrees = ParseWorktrees(porcelain);

        Assert.Equal(5, worktrees.Count);
        Assert.Equal(Path.GetFullPath("/srv/repo"), worktrees[0].Path);
        Assert.Equal("main", worktrees[0].Branch);
        Assert.Equal(Hash1, worktrees[0].HeadCommit);
        Assert.True(worktrees[1].IsDetached);
        Assert.Null(worktrees[1].Branch);
        Assert.True(worktrees[2].IsLocked);
        Assert.Equal("machine is asleep", worktrees[2].LockReason);
        Assert.True(worktrees[3].IsPrunable);
        Assert.Equal("gitdir file points to non-existent location", worktrees[3].PrunableReason);
        Assert.True(worktrees[4].IsBare);
        Assert.Null(worktrees[4].HeadCommit);
    }

    [Fact]
    public void Worktree_accepts_crlf_and_quoted_paths_and_empty_input()
    {
        var porcelain = $"worktree \"/srv/with\\040space\"\r\nHEAD {Hash1}\r\nbranch refs/heads/x\r\n\r\n";

        var worktree = Assert.Single(ParseWorktrees(porcelain));

        Assert.Equal(Path.GetFullPath("/srv/with space"), worktree.Path);
        Assert.Equal("x", worktree.Branch);
        Assert.Empty(ParseWorktrees(string.Empty));
    }

    [Fact]
    public void Quoting_decodes_c_style_escapes()
    {
        Assert.Equal("a\tb", Unquote("\"a\\tb\""));
        Assert.Equal("quote\"and\\slash", Unquote("\"quote\\\"and\\\\slash\""));
        Assert.Equal("line\nfeed", Unquote("\"line\\nfeed\""));
    }

    [Fact]
    public void Quoting_leaves_unquoted_and_unterminated_values_alone()
    {
        Assert.Equal("plain/path.txt", Unquote("plain/path.txt"));
        Assert.Equal("\"", Unquote("\""));

    }

    [Fact]
    public void Quoting_decodes_three_digit_octal_escapes_as_ascii()
    {
        Assert.Equal("A", Unquote("\"\\101\""));
        Assert.Equal("tab\there", Unquote("\"tab\\011here\""));
    }

    [Fact]
    public void Quoting_decodes_utf8_octal_bytes_into_the_original_characters()
    {
        Assert.Equal("café.txt", Unquote("\"caf\\303\\251.txt\""));
    }
}
