using Forge.Abstractions;
using Forge.Core.Agents;
using Xunit;

namespace Forge.App.Tests;

/// <summary>
/// What a summary remembers about files: the paths it touched and — for reads — the line window each call covered, merged so
/// a later turn knows which part of a large file was already read. Losing the range turns every compaction into "re-read the
/// whole spec", which is the cost this list exists to avoid.
/// </summary>
public class CompactionFileListTests
{
    private static StoredMessage Call(long seq, string name, string argumentsJson) =>
        new(
            seq,
            new ChatMessage(MessageRole.Assistant, [new ToolCallBlock("c" + seq, name, argumentsJson)]),
            null,
            DateTimeOffset.UnixEpoch);

    /// <summary>The tool message that answers <paramref name="seq"/>'s call, as the read tool writes it.</summary>
    private static StoredMessage Answer(long seq, long callSeq, string text) =>
        new(
            seq,
            new ChatMessage(MessageRole.Tool, [new ToolResultBlock("c" + callSeq, text)]),
            null,
            DateTimeOffset.UnixEpoch);

    [Fact]
    public void Reads_of_neighbouring_or_overlapping_lines_merge_into_one_range()
    {
        var files = CompactionFiles.Collect(
            [
                Call(1, "read", """{"path":"src/App.cs","offset":1,"limit":420}"""),
                Call(2, "read", """{"path":"src/App.cs","offset":421,"limit":420}"""),
                Call(3, "read", """{"path":"src/App.cs","offset":300,"limit":200}"""),
            ],
            3);

        // 1-420 and 421-840 are one unbroken run, and 300-499 sits inside it: nothing is written twice.
        Assert.Equal("\n\n<read-files>\nsrc/App.cs:1-840\n</read-files>", files.Render());
    }

    [Fact]
    public void A_read_without_a_window_is_the_whole_file_and_swallows_earlier_ranges()
    {
        var files = CompactionFiles.Collect(
            [
                Call(1, "read", """{"path":"src/Spec.md","offset":500,"limit":100}"""),
                Call(2, "read", """{"path":"src/Spec.md"}"""),
            ],
            2);

        Assert.Equal("\n\n<read-files>\nsrc/Spec.md\n</read-files>", files.Render());
    }

    [Fact]
    public void A_file_read_and_modified_is_listed_only_as_modified()
    {
        var files = CompactionFiles.Collect(
            [
                Call(1, "read", """{"path":"src/App.cs","offset":1,"limit":40}"""),
                Call(2, "edit", """{"path":"src/App.cs","old_text":"a","new_text":"b"}"""),
                Call(3, "copy", """{"from":"src/App.cs","to":"src/AppCopy.cs"}"""),
            ],
            3);

        Assert.Equal(
            "\n\n<modified-files>\nsrc/App.cs\nsrc/AppCopy.cs\n</modified-files>",
            files.Render());
    }

    [Fact]
    public void Stored_ranges_survive_a_round_trip_so_the_next_compaction_starts_from_them()
    {
        var first = CompactionFiles.Collect([Call(1, "read", """{"path":"src/App.cs","offset":1,"limit":420}""")], 1);
        var rendered = first.Render();

        // The next compaction seeds itself from the stored line, not from a bare path.
        var seeded = new CompactionFiles();
        foreach (var line in rendered.Split('\n'))
        {
            var entry = line.Trim();
            if (entry.Length > 0 && !entry.StartsWith('<') && !entry.StartsWith("</", StringComparison.Ordinal))
            {
                seeded.AddEntry(entry);
            }
        }

        seeded.AddRead("src/App.cs", new LineRange(421, 840));
        Assert.Equal("\n\n<read-files>\nsrc/App.cs:1-840\n</read-files>", seeded.Render());
    }

    [Fact]
    public void The_range_comes_from_what_the_read_actually_showed_not_from_the_requested_window()
    {
        // The model asked for 2000 lines from the start; the tool cut the answer at 420 and said so.
        var files = CompactionFiles.Collect(
            [
                Call(1, "read", """{"path":"src/App.cs","offset":1,"limit":2000}"""),
                Answer(2, 1, "src/App.cs: 420 lines\n1→a\n… (truncated) Shown lines 1–420. Continue with offset=421."),
            ],
            2);

        Assert.Equal("\n\n<read-files>\nsrc/App.cs:1-420\n</read-files>", files.Render());
    }

    [Fact]
    public void An_uncut_answer_counts_its_lines_from_the_requested_offset()
    {
        var files = CompactionFiles.Collect(
            [
                Call(1, "read", """{"path":"src/App.cs","offset":500}"""),
                Answer(2, 1, "src/App.cs: 300 lines\n500→a"),
            ],
            2);

        // 300 lines shown from line 500: 500-799, not "500 to the end".
        Assert.Equal("\n\n<read-files>\nsrc/App.cs:500-799\n</read-files>", files.Render());
    }

    [Fact]
    public void A_failed_read_records_no_range()
    {
        var files = CompactionFiles.Collect(
            [
                Call(1, "read", """{"path":"src/folder"}"""),
                Answer(2, 1, "read: 'src/folder' is a directory; use `ls` to list it."),
            ],
            2);

        Assert.Equal(string.Empty, files.Render());
    }

    [Fact]
    public void A_windows_path_keeps_its_drive_letter_and_only_the_last_colon_starts_a_range()
    {
        var files = new CompactionFiles();
        files.AddEntry("""D:\forge\src\App.cs:1-40""");
        files.AddEntry("""D:\forge\src\Other.cs""");

        Assert.Equal(
            "\n\n<read-files>\nD:\\forge\\src\\App.cs:1-40\nD:\\forge\\src\\Other.cs\n</read-files>",
            files.Render());
    }

    // ---------------------------------------------------------------------------------------------- the hard budget

    /// <summary>
    /// The lists are written by code and grow with every compaction that meets a file, so each block has a character cap of
    /// its own: the summary body is bounded by the summarizer's output limit, these are not.
    /// </summary>
    private static string Path(int index, int padding) => $"src/{new string('d', padding)}/{index:D3}.cs";

    [Fact]
    public void A_block_that_fills_its_budget_exactly_drops_nothing()
    {
        // One line of 3 971 characters plus its newline fills the 3 972 characters the block has below its tags.
        var path = new string('p', 3_971);
        var files = new CompactionFiles();
        files.AddRead(path, LineRange.Whole);

        var rendered = files.Render();
        Assert.Equal(CompactionFiles.ReadListChars, rendered.Length);
        Assert.Equal(string.Concat("\n\n<read-files>\n", path, "\n</read-files>"), rendered);
    }

    [Fact]
    public void A_list_over_its_budget_keeps_the_ordinal_head_and_counts_what_it_dropped()
    {
        // Every line is 101 characters plus its newline, so 38 of them fit in the 3 972 the block has below its tags; the
        // notice needs 32 of those characters, which changes nothing at this line length. Two paths are dropped.
        var files = new CompactionFiles();
        for (var index = 1; index <= 40; index++)
        {
            files.AddRead(Path(index, 90), LineRange.Whole);
        }

        var rendered = files.Render();
        Assert.Equal(3_913, rendered.Length);
        Assert.True(rendered.Length <= CompactionFiles.ReadListChars);
        Assert.Contains($"{Path(38, 90)}\n", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain(Path(39, 90), rendered, StringComparison.Ordinal);
        Assert.EndsWith("\n… 2 more</read-files>", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void One_path_longer_than_the_whole_budget_is_shortened_in_the_middle()
    {
        var files = new CompactionFiles();
        files.AddRead(string.Concat(new string('x', 5_000), "/End.cs"), LineRange.Whole);

        var rendered = files.Render();
        Assert.True(rendered.Length <= CompactionFiles.ReadListChars, $"{rendered.Length} characters");
        Assert.Contains("…", rendered, StringComparison.Ordinal);
        // The file name — the part that says which file this was — is what the tail keeps.
        Assert.EndsWith("End.cs\n</read-files>", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void The_two_lists_have_their_own_budgets_and_the_modified_one_is_wider()
    {
        // The same 300 paths fit the modified list and overflow the read one: the wider budget is what makes a change
        // record survive a list that would otherwise drop it.
        var reads = new CompactionFiles();
        var modified = new CompactionFiles();
        for (var index = 1; index <= 300; index++)
        {
            reads.AddRead(Path(index, 6), LineRange.Whole);
            modified.AddModified(Path(index, 6));
        }

        var readBlock = reads.Render();
        var modifiedBlock = modified.Render();
        Assert.InRange(readBlock.Length, 1, CompactionFiles.ReadListChars);
        Assert.Contains("more", readBlock, StringComparison.Ordinal);
        Assert.InRange(modifiedBlock.Length, 1, CompactionFiles.ModifiedListChars);
        Assert.DoesNotContain("more", modifiedBlock, StringComparison.Ordinal);
        Assert.Contains($"{Path(300, 6)}\n", modifiedBlock, StringComparison.Ordinal);
    }

    [Fact]
    public void This_passes_paths_are_rendered_before_the_ones_existing_summary_already_had()
    {
        var files = new CompactionFiles();
        files.AddRead("src/old/a.cs", LineRange.Whole);

        var second = CompactionFiles.Collect(
            [Call(1, "read", """{"path":"src/new/a.cs"}""")],
            1,
            files);

        Assert.Equal("\n\n<read-files>\nsrc/new/a.cs\nsrc/old/a.cs\n</read-files>", second.Render());
    }

    [Fact]
    public void A_stored_notice_or_a_shortened_path_is_not_a_file()
    {
        // Both forms appear in a stored summary once a list outgrows its budget, in either block. Re-seeding from that text
        // must not turn them into entries, or every later compaction would render one more line than the one before it.
        var files = new CompactionFiles();
        files.AddEntry("… 3 more");
        files.AddEntry("src/dddddddddddddd/…/Abc.cs");
        files.AddEntry("src/App.cs");
        files.AddModified("… 7 more");
        files.AddModified("src/changed/…/Abc.cs");
        files.AddModified("src/Changed.cs");

        Assert.Equal(
            "\n\n<read-files>\nsrc/App.cs\n</read-files>\n\n<modified-files>\nsrc/Changed.cs\n</modified-files>",
            files.Render());
    }

    [Fact]
    public void A_path_as_long_as_the_block_itself_is_shortened_and_never_counted_as_zero_more()
    {
        // The block has 3 972 characters below its tags, so a path of exactly that length does not fit (its newline would
        // be the 3 973rd). Reserving room for the dropped-count notice must not turn that into "… 0 more": it is shortened
        // instead, which keeps the file name and the cap.
        var files = new CompactionFiles();
        files.AddRead(new string('p', 3_972), LineRange.Whole);

        var rendered = files.Render();
        Assert.Equal(CompactionFiles.ReadListChars, rendered.Length);
        Assert.Contains("…", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("more", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void Reseeding_a_list_that_filled_its_budget_keeps_it_inside_the_budget()
    {
        // 200 paths are far more than the 3 972 characters the block has; what a stored summary carries back is that
        // shortened list, and the next pass adds one new path to it.
        var files = new CompactionFiles();
        for (var index = 1; index <= 200; index++)
        {
            files.AddRead(Path(index, 60), LineRange.Whole);
        }

        Assert.True(files.Render().Length <= CompactionFiles.ReadListChars);

        var reseeded = CompactionFiles.Collect(
            [Call(1, "read", """{"path":"src/latest.cs"}""")],
            1,
            files);

        var rendered = reseeded.Render();
        Assert.True(rendered.Length <= CompactionFiles.ReadListChars, $"{rendered.Length} characters");
        Assert.StartsWith("\n\n<read-files>\nsrc/latest.cs\n", rendered, StringComparison.Ordinal);
    }
}
