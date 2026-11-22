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
}
