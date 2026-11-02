using Xunit;

namespace Forge.Workspace.Tests.Diff;

public sealed class UnifiedDiffParserTests
{
    [Fact]
    public void Pure_rename_has_similarity_and_no_hunks()
    {
        const string diff = """
            diff --git a/old.txt b/new.txt
            similarity index 100%
            rename from old.txt
            rename to new.txt
            """;

        var file = Assert.Single(UnifiedDiffParser.Parse(diff));

        Assert.Equal(FileDiffStatus.Renamed, file.Status);
        Assert.True(file.IsRenameOrCopy);
        Assert.Equal("old.txt", file.OldPath);
        Assert.Equal("new.txt", file.NewPath);
        Assert.Equal("new.txt", file.Path);
        Assert.Equal(100, file.Similarity);
        Assert.Empty(file.Hunks);
    }

    [Fact]
    public void Rename_with_spaces_and_content_change_keeps_both_paths_and_lines()
    {
        const string diff = """
            diff --git a/old name.txt b/new name.txt
            similarity index 90%
            rename from old name.txt
            rename to new name.txt
            index 1111111..2222222 100644
            --- a/old name.txt
            +++ b/new name.txt
            @@ -1,2 +1,2 @@
             keep
            -old
            +new
            """;

        var file = Assert.Single(UnifiedDiffParser.Parse(diff));

        Assert.Equal(FileDiffStatus.Renamed, file.Status);
        Assert.Equal("old name.txt", file.OldPath);
        Assert.Equal("new name.txt", file.NewPath);
        Assert.Equal(90, file.Similarity);
        var hunk = Assert.Single(file.Hunks);
        Assert.Equal(["keep", "old", "new"], hunk.Lines.Select(static line => line.Text));
        Assert.Equal(1, hunk.Lines[0].OldLineNumber);
        Assert.Equal(1, hunk.Lines[0].NewLineNumber);
        Assert.Equal(0, hunk.Lines[2].OldLineNumber);
        Assert.Equal(2, hunk.Lines[2].NewLineNumber);
        Assert.Equal(1, file.AddedLineCount);
        Assert.Equal(1, file.RemovedLineCount);
    }

    [Fact]
    public void Copy_header_is_reported_as_copied()
    {
        const string diff = """
            diff --git a/src.txt b/dup.txt
            similarity index 100%
            copy from src.txt
            copy to dup.txt
            """;

        var file = Assert.Single(UnifiedDiffParser.Parse(diff));

        Assert.Equal(FileDiffStatus.Copied, file.Status);
        Assert.Equal("src.txt", file.OldPath);
        Assert.Equal("dup.txt", file.NewPath);
    }

    [Fact]
    public void Binary_change_is_flagged_without_hunks()
    {
        const string diff = """
            diff --git a/img.png b/img.png
            index 1111111..2222222 100644
            Binary files a/img.png and b/img.png differ
            """;

        var file = Assert.Single(UnifiedDiffParser.Parse(diff));

        Assert.True(file.IsBinary);
        Assert.Equal(FileDiffStatus.Modified, file.Status);
        Assert.Equal("img.png", file.Path);
        Assert.Empty(file.Hunks);
    }

    [Fact]
    public void Git_binary_patch_is_flagged_as_binary()
    {
        const string diff = """
            diff --git a/blob.bin b/blob.bin
            new file mode 100644
            index 0000000..3333333
            GIT binary patch
            literal 4
            """;

        var file = Assert.Single(UnifiedDiffParser.Parse(diff));

        Assert.True(file.IsBinary);
        Assert.Equal(FileDiffStatus.Added, file.Status);
    }

    [Fact]
    public void Quoted_header_and_minus_plus_paths_are_unquoted()
    {
        const string diff = "diff --git \"a/tab\\tname.txt\" \"b/tab\\tname.txt\"\n"
            + "--- \"a/tab\\tname.txt\"\n"
            + "+++ \"b/tab\\tname.txt\"\n"
            + "@@ -1 +1 @@\n"
            + "-x\n"
            + "+y\n";

        var file = Assert.Single(UnifiedDiffParser.Parse(diff));

        Assert.Equal("tab\tname.txt", file.NewPath);
        Assert.Equal("tab\tname.txt", file.OldPath);
        Assert.Equal("y", Assert.Single(file.Hunks[0].LinesOf(DiffLineKind.Added)).Text);
    }

    [Fact]
    public void Quoted_header_decodes_octal_escapes_and_embedded_quotes()
    {
        const string diff = "diff --git \"a/caf\\101.txt\" \"b/say \\\"hi\\\".txt\"\n"
            + "similarity index 50%\n"
            + "rename from \"caf\\101.txt\"\n"
            + "rename to \"say \\\"hi\\\".txt\"\n";

        var file = Assert.Single(UnifiedDiffParser.Parse(diff));

        Assert.Equal("cafA.txt", file.OldPath);
        Assert.Equal("say \"hi\".txt", file.NewPath);
    }

    [Fact]
    public void No_newline_marker_flags_the_preceding_line()
    {
        const string diff = """
            diff --git a/f b/f
            --- a/f
            +++ b/f
            @@ -1 +1 @@
            -old
            \ No newline at end of file
            +new
            \ No newline at end of file
            """;

        var hunk = Assert.Single(Assert.Single(UnifiedDiffParser.Parse(diff)).Hunks);

        Assert.True(hunk.Lines[0].NoNewLineAtEnd);
        Assert.Equal("old", hunk.Lines[0].Text);
        Assert.True(hunk.Lines[1].NoNewLineAtEnd);
        Assert.Equal("new", hunk.Lines[1].Text);
    }

    [Fact]
    public void Crlf_content_is_parsed_without_carriage_returns()
    {
        var diff = "diff --git a/win.txt b/win.txt\r\n"
            + "--- a/win.txt\r\n"
            + "+++ b/win.txt\r\n"
            + "@@ -1,2 +1,2 @@\r\n"
            + " first\r\n"
            + "-second\r\n"
            + "+SECOND\r\n";

        var file = Assert.Single(UnifiedDiffParser.Parse(diff));

        Assert.Equal("win.txt", file.Path);
        var hunk = Assert.Single(file.Hunks);
        Assert.Equal(["first", "second", "SECOND"], hunk.Lines.Select(static line => line.Text));
        Assert.All(hunk.Lines, static line => Assert.DoesNotContain('\r', line.Text));
    }

    [Fact]
    public void Mode_only_change_reports_both_modes_and_no_hunks()
    {
        const string diff = """
            diff --git a/run.sh b/run.sh
            old mode 100644
            new mode 100755
            """;

        var file = Assert.Single(UnifiedDiffParser.Parse(diff));

        Assert.Equal(FileDiffStatus.Modified, file.Status);
        Assert.Equal("100644", file.OldMode);
        Assert.Equal("100755", file.NewMode);
        Assert.False(file.IsBinary);
        Assert.Empty(file.Hunks);
    }

    [Fact]
    public void New_and_deleted_files_drop_the_missing_side_path()
    {
        const string diff = """
            diff --git a/added.txt b/added.txt
            new file mode 100644
            index 0000000..1111111
            --- /dev/null
            +++ b/added.txt
            @@ -0,0 +1,2 @@
            +one
            +two
            diff --git a/gone.txt b/gone.txt
            deleted file mode 100644
            index 2222222..0000000
            --- a/gone.txt
            +++ /dev/null
            @@ -1 +0,0 @@
            -bye
            """;

        var files = UnifiedDiffParser.Parse(diff);

        Assert.Equal(2, files.Count);
        Assert.Equal(FileDiffStatus.Added, files[0].Status);
        Assert.Null(files[0].OldPath);
        Assert.Equal("added.txt", files[0].NewPath);
        Assert.Equal("100644", files[0].NewMode);
        Assert.Equal(FileDiffStatus.Deleted, files[1].Status);
        Assert.Null(files[1].NewPath);
        Assert.Equal("gone.txt", files[1].OldPath);
        Assert.Equal("gone.txt", files[1].Path);
    }

    [Fact]
    public void Multi_file_diff_keeps_order_hunks_and_line_numbers()
    {
        const string diff = """
            diff --git a/a.cs b/a.cs
            index 1..2 100644
            --- a/a.cs
            +++ b/a.cs
            @@ -10,3 +10,4 @@ class A
             ten
            -eleven
            +eleven!
            +eleven?
             twelve
            @@ -30,2 +31,2 @@
             thirty
            -thirty one
            +thirty-one
            diff --git a/b.cs b/b.cs
            index 3..4 100644
            --- a/b.cs
            +++ b/b.cs
            @@ -1 +1 @@
            -a
            +b
            """;

        var files = UnifiedDiffParser.Parse(diff);

        Assert.Equal(["a.cs", "b.cs"], files.Select(static file => file.Path));
        Assert.Equal(2, files[0].Hunks.Count);
        var first = files[0].Hunks[0];
        Assert.Equal("@@ -10,3 +10,4 @@", first.Header);
        Assert.Equal(12, first.OldEnd);
        Assert.Equal(13, first.NewEnd);
        Assert.Equal(new[] { 10, 11, 0, 0, 12 }, first.Lines.Select(static line => line.OldLineNumber).ToArray());
        Assert.Equal(new[] { 10, 0, 11, 12, 13 }, first.Lines.Select(static line => line.NewLineNumber).ToArray());
        Assert.Equal(31, files[0].Hunks[1].NewStart);
        Assert.Equal(3, files[0].AddedLineCount);
        Assert.Equal(2, files[0].RemovedLineCount);
        Assert.Equal(files[0].Hunks[1], files[0].FindHunk(31));
        Assert.Null(files[0].FindHunk(20));
        Assert.Equal(first, files[0].FindHunk(12));
    }

    [Fact]
    public void Removed_line_that_looks_like_a_minus_header_stays_in_the_hunk()
    {
        const string diff = """
            diff --git a/notes.txt b/notes.txt
            --- a/notes.txt
            +++ b/notes.txt
            @@ -1,2 +1,1 @@
            --- old separator
            -keep
            +joined
            """;

        var hunk = Assert.Single(Assert.Single(UnifiedDiffParser.Parse(diff)).Hunks);

        Assert.Equal(["-- old separator", "keep", "joined"], hunk.Lines.Select(static line => line.Text));
        Assert.Equal(DiffLineKind.Removed, hunk.Lines[0].Kind);
    }

    [Fact]
    public void Hunk_header_without_counts_defaults_to_one_line()
    {
        const string diff = """
            diff --git a/one.txt b/one.txt
            --- a/one.txt
            +++ b/one.txt
            @@ -4 +4 @@
            -x
            +y
            """;

        var hunk = Assert.Single(Assert.Single(UnifiedDiffParser.Parse(diff)).Hunks);

        Assert.Equal(1, hunk.OldCount);
        Assert.Equal(1, hunk.NewCount);
        Assert.Equal("@@ -4,1 +4,1 @@", hunk.Header);
    }

    [Fact]
    public void Malformed_hunk_header_is_dropped_without_throwing()
    {
        const string diff = """
            diff --git a/bad.txt b/bad.txt
            --- a/bad.txt
            +++ b/bad.txt
            @@ -x,y +1,2 @@
            -lost
            +also lost
            @@ garbage
            @@ -5,1 +5,1 @@
            -kept
            +kept too
            """;

        var file = Assert.Single(UnifiedDiffParser.Parse(diff));

        var hunk = Assert.Single(file.Hunks);
        Assert.Equal(5, hunk.OldStart);
        Assert.Equal(["kept", "kept too"], hunk.Lines.Select(static line => line.Text));
    }

    [Fact]
    public void Malformed_header_without_closing_marker_yields_no_hunks()
    {
        const string diff = """
            diff --git a/x b/x
            --- a/x
            +++ b/x
            @@ -1,1 +1,1
            -a
            +b
            """;

        var file = Assert.Single(UnifiedDiffParser.Parse(diff));

        Assert.Empty(file.Hunks);
    }

    [Fact]
    public void Empty_input_yields_no_files()
    {
        Assert.Empty(UnifiedDiffParser.Parse(string.Empty));
        Assert.Empty(UnifiedDiffParser.Parse("\n\n"));
    }

    [Fact]
    public void Similarity_and_index_lines_do_not_leak_into_paths()
    {
        const string diff = """
            diff --git a/tests/x.cs b/tests/x.cs
            index 1234567..89abcde 100644
            --- a/tests/x.cs
            +++ b/tests/x.cs
            @@ -1 +1 @@
            -a
            +b
            """;

        var file = Assert.Single(UnifiedDiffParser.Parse(diff));

        Assert.Equal("tests/x.cs", file.Path);
        Assert.Null(file.Similarity);
    }

    [Fact]
    public async Task Parse_from_reader_matches_string_parse()
    {
        const string diff = """
            diff --git a/r.txt b/r.txt
            --- a/r.txt
            +++ b/r.txt
            @@ -1 +1 @@
            -p
            +q
            """;

        using var reader = new StringReader(diff);
        var fromReader = UnifiedDiffParser.Parse(reader);
        var fromAsync = await UnifiedDiffParser.ParseAsync(new StringReader(diff));

        Assert.Equal("r.txt", fromReader.Single().Path);
        Assert.Equal(["-p", "+q"], fromAsync.Single().Hunks.Single().Lines.Select(static line => (line.Kind == DiffLineKind.Added ? "+" : "-") + line.Text));
        Assert.Equal(fromReader.Single().Hunks.Single().Lines.Count, fromAsync.Single().Hunks.Single().Lines.Count);
    }

    [Fact]
    public void Null_input_is_rejected()
    {
        Assert.Throws<ArgumentNullException>(() => UnifiedDiffParser.Parse((string)null!));
    }
}
