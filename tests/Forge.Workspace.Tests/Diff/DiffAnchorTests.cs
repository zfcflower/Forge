using Xunit;

namespace Forge.Workspace.Tests.Diff;

public sealed class DiffAnchorTests
{
    private const string Original = """
        diff --git a/f.txt b/f.txt
        --- a/f.txt
        +++ b/f.txt
        @@ -1,5 +1,5 @@
         alpha
        -beta
        +BETA
         gamma
         delta
         epsilon
        """;

    private const string Shifted = """
        diff --git a/f.txt b/f.txt
        --- a/f.txt
        +++ b/f.txt
        @@ -1,5 +1,6 @@
        +new top
         alpha
        -beta
        +BETA
         gamma
         delta
         epsilon
        """;

    private const string Changed = """
        diff --git a/f.txt b/f.txt
        --- a/f.txt
        +++ b/f.txt
        @@ -1,5 +1,5 @@
         alpha
        -beta
        +BETA2
         gamma
         delta
         epsilon
        """;

    private const string Elsewhere = """
        diff --git a/f.txt b/f.txt
        --- a/f.txt
        +++ b/f.txt
        @@ -1,2 +1,2 @@
         x
        -y
        +z
        """;

    private static FileDiff Single(string diff) => Assert.Single(UnifiedDiffParser.Parse(diff));

    [Fact]
    public void Create_records_path_hunk_header_and_old_line_of_the_anchored_line()
    {
        var file = Single(Original);

        var anchor = DiffAnchor.Create(file, 2);

        Assert.Equal("f.txt", anchor.Path);
        Assert.Equal("@@ -1,5 +1,5 @@", anchor.HunkHeader);
        Assert.Equal(0, anchor.HunkIndex);
        Assert.Equal(0, anchor.OldLine);
        Assert.Equal(2, anchor.NewLine);
        Assert.Equal(1, anchor.NewLineCount);
        Assert.Equal(16, anchor.ContentHash.Length);
    }

    [Fact]
    public void Create_with_range_hashes_every_line_in_the_block()
    {
        var file = Single(Original);

        var single = DiffAnchor.Create(file, 2, 1);
        var block = DiffAnchor.Create(file, 2, 2);

        Assert.NotEqual(single.ContentHash, block.ContentHash);
        Assert.Equal(2, block.NewLineCount);
    }

    [Fact]
    public void Create_outside_any_hunk_or_on_removed_only_hunk_throws()
    {
        var file = Single(Original);
        Assert.Throws<ArgumentOutOfRangeException>(() => DiffAnchor.Create(file, 40));

        var removedOnly = Single("""
            diff --git a/r.txt b/r.txt
            --- a/r.txt
            +++ b/r.txt
            @@ -3,2 +2,0 @@
            -gone one
            -gone two
            """);
        Assert.Throws<ArgumentOutOfRangeException>(() => DiffAnchor.Create(removedOnly, 2));
    }

    [Fact]
    public void Create_rejects_non_positive_line_counts()
    {
        var file = Single(Original);

        Assert.Throws<ArgumentOutOfRangeException>(() => DiffAnchor.Create(file, 2, 0));
    }

    [Fact]
    public void Create_by_path_returns_null_for_unknown_file()
    {
        var files = UnifiedDiffParser.Parse(Original);

        Assert.Null(DiffAnchor.Create(files, "missing.txt", 2));
        Assert.NotNull(DiffAnchor.Create(files, "f.txt", 2));
    }

    [Fact]
    public void Resolve_on_unchanged_diff_is_exact()
    {
        var anchor = DiffAnchor.Create(Single(Original), 2);

        var match = anchor.Resolve(Single(Original));

        Assert.Equal(new DiffAnchorMatch(DiffAnchorState.Exact, 2, 1), match);
    }

    [Fact]
    public void Resolve_after_lines_shifted_finds_the_same_content_as_moved()
    {
        var anchor = DiffAnchor.Create(Single(Original), 2);

        var match = anchor.Resolve(Single(Shifted));

        Assert.NotNull(match);
        Assert.Equal(DiffAnchorState.Moved, match.State);
        Assert.Equal(3, match.NewStart);
        Assert.Equal(1, match.NewCount);
    }

    [Fact]
    public void Resolve_after_content_change_is_stale_and_keeps_original_position()
    {
        var anchor = DiffAnchor.Create(Single(Original), 2);

        var match = anchor.Resolve(Single(Changed));

        Assert.NotNull(match);
        Assert.Equal(DiffAnchorState.Stale, match.State);
        Assert.Equal(2, match.NewStart);
    }

    [Fact]
    public void Resolve_returns_null_when_file_is_absent_or_path_differs()
    {
        var anchor = DiffAnchor.Create(Single(Original), 2);

        Assert.Null(anchor.Resolve(Single(Original) with { NewPath = "other.txt" }));
        Assert.Null(anchor.Resolve(new List<FileDiff>()));
        Assert.Null(anchor.Resolve(UnifiedDiffParser.Parse(Elsewhere.Replace("f.txt", "other.txt", StringComparison.Ordinal))));
    }

    [Fact]
    public void Resolve_against_file_list_uses_the_named_file()
    {
        var anchor = DiffAnchor.Create(Single(Original), 2);
        var files = UnifiedDiffParser.Parse(Original);

        var match = anchor.Resolve(files);

        Assert.Equal(DiffAnchorState.Exact, match!.State);
    }

    [Fact]
    public void Quote_renders_anchored_line_with_context_in_unified_form()
    {
        var file = Single(Original);
        var anchor = DiffAnchor.Create(file, 2);

        var quote = anchor.Quote(file, contextLines: 1);

        Assert.NotNull(quote);
        Assert.Equal(DiffAnchorState.Exact, quote.State);
        Assert.Equal(2, quote.NewStart);
        Assert.Equal(2, quote.NewEnd);
        Assert.Equal("- beta\n+ BETA\n  gamma", quote.Text);
    }

    [Fact]
    public void Quote_with_zero_context_contains_only_the_anchored_lines()
    {
        var file = Single(Original);

        var text = DiffAnchor.Create(file, 2).Quote(file, contextLines: 0)!.Text;

        Assert.Equal("+ BETA", text);
    }

    [Fact]
    public void Quote_after_shift_reports_moved_position_and_text()
    {
        var anchor = DiffAnchor.Create(Single(Original), 2);

        var quote = anchor.Quote(Single(Shifted), contextLines: 0);

        Assert.NotNull(quote);
        Assert.Equal(DiffAnchorState.Moved, quote.State);
        Assert.Equal(3, quote.NewStart);
        Assert.Equal(3, quote.NewEnd);
        Assert.Equal("+ BETA", quote.Text);
    }

    [Fact]
    public void Quote_after_change_is_marked_stale_but_still_shows_the_line_in_place()
    {
        var anchor = DiffAnchor.Create(Single(Original), 2);

        var quote = anchor.Quote(Single(Changed), contextLines: 0);

        Assert.NotNull(quote);
        Assert.Equal(DiffAnchorState.Stale, quote.State);
        Assert.Equal("+ BETA2", quote.Text);
    }

    [Fact]
    public void Quote_returns_null_when_the_file_is_gone()
    {
        var anchor = DiffAnchor.Create(Single(Original), 2);

        Assert.Null(anchor.Quote(Single(Original) with { NewPath = "renamed.txt" }));
        Assert.Null(anchor.Quote(UnifiedDiffParser.Parse(Elsewhere.Replace("f.txt", "other.txt", StringComparison.Ordinal))));
    }

    [Fact]
    public void Quote_by_file_list_and_negative_context_rejected()
    {
        var file = Single(Original);
        var anchor = DiffAnchor.Create(file, 2);

        Assert.Contains("+ BETA", anchor.Quote(new[] { file }) ?? string.Empty, StringComparison.Ordinal);
        Assert.Throws<ArgumentOutOfRangeException>(() => anchor.Quote(file, contextLines: -1));
    }

    [Fact]
    public void Hash_is_stable_for_identical_content_and_sensitive_to_line_kind()
    {
        var first = DiffAnchor.Create(Single(Original), 2);
        var second = DiffAnchor.Create(Single(Shifted), 3);
        var alternateKind = DiffAnchor.Create(Single("""
            diff --git a/f.txt b/f.txt
            --- a/f.txt
            +++ b/f.txt
            @@ -1,3 +1,3 @@
             alpha
            -beta
             BETA
            """), 2);

        Assert.Equal(first.ContentHash, second.ContentHash);
        Assert.NotEqual(first.ContentHash, alternateKind.ContentHash);
    }

    [Fact]
    public void Anchor_record_round_trips_through_json()
    {
        var anchor = DiffAnchor.Create(Single(Original), 2, 2);

        var json = System.Text.Json.JsonSerializer.Serialize(anchor);
        var restored = System.Text.Json.JsonSerializer.Deserialize<DiffAnchor>(json);

        Assert.Equal(anchor, restored);
    }
}
