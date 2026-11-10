using Xunit;

namespace Forge.Workspace.Tests.Diff;

public sealed class DiffBlocksTests
{
    private static FileDiff Parse(string diff) => Assert.Single(UnifiedDiffParser.Parse(diff.ReplaceLineEndings("\n")));

    private const string Mixed = """
        diff --git a/a.txt b/a.txt
        --- a/a.txt
        +++ b/a.txt
        @@ -1,9 +1,10 @@
         l1
        -l2
        +L2
        +L2b
         l3
         l4
         l5
         l6
        +ins
         l7
        -l8
         l9

        """;

    private const string Plain = """
        diff --git a/a.txt b/a.txt
        --- a/a.txt
        +++ b/a.txt
        @@ -1,3 +1,3 @@
         l1
        -l2
        +L2
         l3

        """;

    [Fact]
    public void Split_separates_mixed_pure_add_and_pure_delete_blocks()
    {
        var blocks = DiffBlocks.Split(Parse(Mixed));

        Assert.Equal(3, blocks.Count);
        Assert.Equal([0, 1, 2], blocks.Select(static b => b.Index));

        Assert.Equal(2, blocks[0].OldStart);
        Assert.Equal(["l2"], blocks[0].OldLines);
        Assert.Equal(2, blocks[0].NewStart);
        Assert.Equal(["L2", "L2b"], blocks[0].NewLines);
        Assert.Equal(3, blocks[0].NewEnd);

        // Pure insertion: the old side has no lines, so OldStart is the old line it sits after.
        Assert.Empty(blocks[1].OldLines);
        Assert.Equal(6, blocks[1].OldStart);
        Assert.Equal(["ins"], blocks[1].NewLines);
        Assert.Equal(8, blocks[1].NewStart);

        // Pure deletion: the new side is empty, NewStart is the new line it sits after.
        Assert.Equal(["l8"], blocks[2].OldLines);
        Assert.Equal(8, blocks[2].OldStart);
        Assert.Empty(blocks[2].NewLines);
        Assert.Equal(9, blocks[2].NewStart);
        Assert.Equal(blocks[2].NewStart, blocks[2].NewEnd);
    }

    [Fact]
    public void Split_numbers_blocks_across_hunks_in_file_order()
    {
        var diff = Parse("""
            diff --git a/a.txt b/a.txt
            --- a/a.txt
            +++ b/a.txt
            @@ -1,3 +1,3 @@
             a
            -b
            +B
             c
            @@ -20,3 +20,3 @@
             x
            -y
            +Y
             z

            """);

        var blocks = DiffBlocks.Split(diff);

        Assert.Equal(2, blocks.Count);
        Assert.Equal([0, 1], blocks.Select(static b => b.Index));
        Assert.Equal(21, blocks[1].NewStart);
    }

    [Fact]
    public void Split_handles_deletion_at_file_start_and_whole_new_file()
    {
        var deletion = Parse("""
            diff --git a/a.txt b/a.txt
            --- a/a.txt
            +++ b/a.txt
            @@ -1,2 +0,0 @@
            -a
            -b

            """);
        var block = Assert.Single(DiffBlocks.Split(deletion));
        Assert.Equal(0, block.NewStart);
        Assert.Equal(["a", "b"], block.OldLines);

        var added = Parse("""
            diff --git a/n.txt b/n.txt
            new file mode 100644
            --- /dev/null
            +++ b/n.txt
            @@ -0,0 +1,2 @@
            +a
            +b

            """);
        var addBlock = Assert.Single(DiffBlocks.Split(added));
        Assert.Equal(1, addBlock.NewStart);
        Assert.Equal(0, addBlock.OldStart);
        Assert.Equal(2, addBlock.NewCount);
    }

    [Fact]
    public void Split_returns_no_blocks_for_a_diff_without_hunks()
    {
        var binary = new FileDiff("a.bin", "a.bin", FileDiffStatus.Modified, true, []);
        Assert.Empty(DiffBlocks.Split(binary));
    }

    [Fact]
    public void Hash_is_stable_and_content_addressed_not_position_addressed()
    {
        var first = DiffBlocks.Split(Parse(Mixed));
        var again = DiffBlocks.Split(Parse(Mixed));
        Assert.Equal(first.Select(static b => b.Hash), again.Select(static b => b.Hash));
        Assert.Equal(3, first.Select(static b => b.Hash).Distinct().Count());

        // Same change shifted by ten lines: the hash is unchanged, so a "kept" mark survives unrelated edits above.
        var shifted = Parse(Plain.Replace("@@ -1,3 +1,3 @@", "@@ -11,3 +11,3 @@", StringComparison.Ordinal));
        Assert.Equal(DiffBlocks.Split(Parse(Plain))[0].Hash, DiffBlocks.Split(shifted)[0].Hash);

        // A different path or different text changes it.
        var otherPath = Parse(Plain.Replace("a.txt", "b.txt", StringComparison.Ordinal));
        Assert.NotEqual(DiffBlocks.Split(Parse(Plain))[0].Hash, DiffBlocks.Split(otherPath)[0].Hash);
        var otherText = Parse(Plain.Replace("+L2", "+L3", StringComparison.Ordinal));
        Assert.NotEqual(DiffBlocks.Split(Parse(Plain))[0].Hash, DiffBlocks.Split(otherText)[0].Hash);
    }

    private static string TwinDiff(string extraBlock = "") => $"""
        diff --git a/a.txt b/a.txt
        --- a/a.txt
        +++ b/a.txt
        @@ -1,17 +1,17 @@
         c1
        -old
        +new
         c2
         c3
         c4
         c5
         c6
         c7
         c8
        {extraBlock}
         c9
         c10
         c11
         c12
         c13
        -old
        +new
         c14

        """;

    [Fact]
    public void Identical_blocks_in_one_file_get_distinct_deterministic_hashes()
    {
        var twins = DiffBlocks.Split(Parse(TwinDiff(" c8b")));
        Assert.Equal(2, twins.Count);
        Assert.NotEqual(twins[0].Hash, twins[1].Hash);
        Assert.Equal(twins.Select(static b => b.Hash), DiffBlocks.Split(Parse(TwinDiff(" c8b"))).Select(static b => b.Hash));

        // The first occurrence keeps the plain content hash it would have without a twin.
        var alone = Parse("""
            diff --git a/a.txt b/a.txt
            --- a/a.txt
            +++ b/a.txt
            @@ -1,3 +1,3 @@
             c1
            -old
            +new
             c2

            """);
        Assert.Equal(DiffBlocks.Split(alone)[0].Hash, twins[0].Hash);
    }

    [Fact]
    public void Twin_hashes_ignore_edits_to_unrelated_blocks()
    {
        var before = DiffBlocks.Split(Parse(TwinDiff(" c8b")));
        var withUnrelated = DiffBlocks.Split(Parse(TwinDiff("-gone" + '\n' + "+here")));
        Assert.Equal(3, withUnrelated.Count);

        Assert.Equal(before[0].Hash, withUnrelated[0].Hash);
        Assert.Equal(before[1].Hash, withUnrelated[2].Hash);
    }

    [Fact]
    public void Fingerprint_changes_when_any_block_changes()
    {
        var original = Parse(Mixed);
        var edited = Parse(Mixed.Replace("+ins", "+ins2", StringComparison.Ordinal));

        Assert.Equal(DiffBlocks.Fingerprint(original), DiffBlocks.Fingerprint(Parse(Mixed)));
        Assert.NotEqual(DiffBlocks.Fingerprint(original), DiffBlocks.Fingerprint(edited));
    }
}
