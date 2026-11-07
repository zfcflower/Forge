using System.Text;
using Forge.Abstractions;
using Forge.IO.Tools;
using Xunit;

namespace Forge.IO.Tests;

/// <summary>
/// The three read-only search tools through <see cref="ITool.InvokeAsync"/>, the way the agent loop calls them: <c>ls</c>
/// sorts and does not apply ignore rules, <c>find</c> does, both stop at their cap and say so, and <c>grep</c> answers a
/// fixed <c>path:line:text</c> shape with <c>-</c> for context lines, filters by glob, truncates at its limit, reports an
/// empty result as "no matches" rather than an error, skips binary files, and rejects a bad regular expression or a path
/// outside the workspace with an error result.
/// </summary>
public class SearchToolsTests
{
    [Fact]
    public async Task Ls_sorts_by_path_and_does_not_apply_gitignore()
    {
        using var workspace = new TemporaryWorkspace();
        workspace.Write("zdir/inside.txt", "i");
        workspace.Write("c.txt", "c");
        workspace.Write("a.txt", "a");
        workspace.Write("b.txt", "b");
        workspace.Write(".gitignore", "b.txt\n");
        var tool = new ListTool(ToolOptions.Default);

        var result = await workspace.InvokeSuccessAsync(tool, TemporaryWorkspace.Json(new { }));

        Assert.Equal(
            ["d zdir/", "f .gitignore", "f a.txt", "f b.txt", "f c.txt"],
            result.Content.Split('\n').Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Ls_does_not_enter_the_version_control_directory()
    {
        using var workspace = new TemporaryWorkspace();
        workspace.Write(".git/HEAD", "ref: refs/heads/main");
        workspace.Write("tracked.txt", "t");
        var tool = new ListTool(ToolOptions.Default);

        var result = await workspace.InvokeSuccessAsync(tool, TemporaryWorkspace.Json(new { depth = 5 }));

        Assert.Equal(["f tracked.txt"], result.Content.Split('\n'));
    }

    [Fact]
    public async Task Ls_prints_a_directory_listing_by_depth()
    {
        using var workspace = new TemporaryWorkspace();
        workspace.Write("sub/file.txt", "f");
        workspace.Write("sub/deeper/leaf.txt", "l");
        workspace.Write("top.txt", "t");
        var tool = new ListTool(ToolOptions.Default);

        var one = await workspace.InvokeSuccessAsync(tool, TemporaryWorkspace.Json(new { }));
        var two = await workspace.InvokeSuccessAsync(tool, TemporaryWorkspace.Json(new { depth = 2 }));

        Assert.Equal(["d sub/", "f top.txt"], one.Content.Split('\n'));
        Assert.Equal(["d sub/", "d sub/deeper/", "f sub/file.txt", "f top.txt"], two.Content.Split('\n').Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Ls_stops_at_its_entry_cap_and_says_so()
    {
        using var workspace = new TemporaryWorkspace();
        for (var index = 0; index < 6; index++)
        {
            workspace.Write($"file-{index}.txt", "x");
        }

        var tool = new ListTool(ToolOptions.Default with { ListMaxEntries = 3 });

        var result = await workspace.InvokeSuccessAsync(tool, TemporaryWorkspace.Json(new { }));

        var lines = result.Content.Split('\n');
        Assert.Equal(4, lines.Length);
        Assert.Equal("… (truncated)", lines[3]);

        // The cap keeps three of the six entries; which three depends on the directory enumeration order, so the check
        // is that exactly three distinct entries survived, that they are printed in sorted order, and that they are real.
        var kept = lines[..3];
        Assert.Equal(3, kept.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(kept.Order(StringComparer.Ordinal), kept);
        Assert.All(kept, line => Assert.Matches(@"^f file-[0-5]\.txt$", line));
    }

    [Fact]
    public async Task Ls_on_a_missing_directory_is_an_error_result()
    {
        using var workspace = new TemporaryWorkspace();
        var tool = new ListTool(ToolOptions.Default);

        var result = await workspace.InvokeAsync(tool, TemporaryWorkspace.Json(new { path = "nope" }));

        Assert.True(result.IsError);
        Assert.Contains("does not exist", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Find_applies_gitignore_rules()
    {
        using var workspace = new TemporaryWorkspace();
        workspace.Write(".gitignore", "*.log\nbuild/\n");
        workspace.Write("keep.txt", "k");
        workspace.Write("debug.log", "l");
        workspace.Write("build/generated.cs", "g");
        var tool = new FindTool(ToolOptions.Default);

        var result = await workspace.InvokeSuccessAsync(tool, TemporaryWorkspace.Json(new { pattern = "*" }));

        Assert.Equal([".gitignore", "keep.txt"], result.Content.Split('\n').Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Find_filters_by_glob()
    {
        using var workspace = new TemporaryWorkspace();
        workspace.Write("src/a.cs", "a");
        workspace.Write("src/b.txt", "b");
        workspace.Write("src/nested/c.cs", "c");
        var tool = new FindTool(ToolOptions.Default);

        var result = await workspace.InvokeSuccessAsync(tool, TemporaryWorkspace.Json(new { pattern = "*.cs" }));

        Assert.Equal(["src/a.cs", "src/nested/c.cs"], result.Content.Split('\n').Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Find_stops_at_its_limit_and_says_so()
    {
        using var workspace = new TemporaryWorkspace();
        for (var index = 0; index < 5; index++)
        {
            workspace.Write($"file-{index}.txt", "x");
        }

        var tool = new FindTool(ToolOptions.Default);

        var result = await workspace.InvokeSuccessAsync(tool, TemporaryWorkspace.Json(new { limit = 2 }));

        var lines = result.Content.Split('\n');
        Assert.Equal(3, lines.Length);
        Assert.Equal("… (truncated)", lines[2]);
        Assert.Equal(2, lines[..2].Distinct(StringComparer.Ordinal).Count());
        Assert.All(lines[..2], line => Assert.Matches(@"^file-[0-4]\.txt$", line));
    }

    [Fact]
    public async Task Find_on_a_missing_directory_is_an_error_result()
    {
        using var workspace = new TemporaryWorkspace();
        var tool = new FindTool(ToolOptions.Default);

        var result = await workspace.InvokeAsync(tool, TemporaryWorkspace.Json(new { pattern = "*", path = "nope" }));

        Assert.True(result.IsError);
        Assert.Contains("does not exist", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Grep_reports_each_matching_line_as_path_line_text()
    {
        using var workspace = new TemporaryWorkspace();
        workspace.Write("src/a.txt", "alpha\nneedle here\ngamma\nneedle again\n");
        var tool = new GrepTool(ToolOptions.Default);

        var result = await workspace.InvokeSuccessAsync(tool, TemporaryWorkspace.Json(new { pattern = "needle" }));

        Assert.Equal(["src/a.txt:2:needle here", "src/a.txt:4:needle again"], result.Content.Split('\n').Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Grep_context_lines_are_separated_with_a_dash()
    {
        using var workspace = new TemporaryWorkspace();
        workspace.Write("a.txt", "first\nsecond needle\nthird\nfourth\n");
        var tool = new GrepTool(ToolOptions.Default);

        var result = await workspace.InvokeSuccessAsync(tool, TemporaryWorkspace.Json(new { pattern = "needle", context = 1 }));

        Assert.Equal(["a.txt-1-first", "a.txt-3-third", "a.txt:2:second needle"], result.Content.Split('\n').Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Grep_filters_the_files_by_glob()
    {
        using var workspace = new TemporaryWorkspace();
        workspace.Write("src/a.cs", "needle in cs\n");
        workspace.Write("src/b.txt", "needle in txt\n");
        workspace.Write("src/nested/c.cs", "needle in nested cs\n");
        var tool = new GrepTool(ToolOptions.Default);

        var result = await workspace.InvokeSuccessAsync(tool, TemporaryWorkspace.Json(new { pattern = "needle", glob = "*.cs" }));

        Assert.Equal(
            ["src/a.cs:1:needle in cs", "src/nested/c.cs:1:needle in nested cs"],
            result.Content.Split('\n').Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Grep_searches_one_subdirectory_when_a_path_is_given()
    {
        using var workspace = new TemporaryWorkspace();
        workspace.Write("src/a.txt", "needle in src\n");
        workspace.Write("docs/b.txt", "needle in docs\n");
        var tool = new GrepTool(ToolOptions.Default);

        var result = await workspace.InvokeSuccessAsync(
            tool,
            TemporaryWorkspace.Json(new { pattern = "needle", path = "src" }));

        Assert.Equal(["src/a.txt:1:needle in src"], result.Content.Split('\n'));
    }

    [Fact]
    public async Task Grep_stops_at_its_limit_and_marks_the_output_truncated()
    {
        using var workspace = new TemporaryWorkspace();
        workspace.Write("many.txt", string.Concat(Enumerable.Range(0, 10).Select(index => $"needle {index}\n")));
        var tool = new GrepTool(ToolOptions.Default);

        var result = await workspace.InvokeSuccessAsync(tool, TemporaryWorkspace.Json(new { pattern = "needle", limit = 3 }));

        var lines = result.Content.Split('\n');
        Assert.Equal(4, lines.Length);
        Assert.Equal("… (truncated at 3 matches)", lines[3]);
        Assert.Equal(
            ["many.txt:1:needle 0", "many.txt:2:needle 1", "many.txt:3:needle 2"],
            lines[..3].Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Grep_without_a_hit_answers_no_matches_and_is_not_an_error()
    {
        using var workspace = new TemporaryWorkspace();
        workspace.Write("a.txt", "nothing relevant\n");
        var tool = new GrepTool(ToolOptions.Default);

        var result = await workspace.InvokeAsync(tool, TemporaryWorkspace.Json(new { pattern = "absent" }));

        Assert.False(result.IsError);
        Assert.Equal("no matches", result.Content);
    }

    [Fact]
    public async Task Grep_skips_a_binary_file_and_reports_how_many()
    {
        using var workspace = new TemporaryWorkspace();
        workspace.WriteBytes("blob.bin", [(byte)'n', (byte)'e', (byte)'e', (byte)'d', (byte)'l', (byte)'e', 0, 1, 2]);
        var tool = new GrepTool(ToolOptions.Default);

        var result = await workspace.InvokeSuccessAsync(tool, TemporaryWorkspace.Json(new { pattern = "needle" }));

        Assert.Equal(["no matches", "1 file(s) skipped: binary"], result.Content.Split('\n'));
    }

    [Fact]
    public async Task Grep_with_an_invalid_regular_expression_is_an_error_result()
    {
        using var workspace = new TemporaryWorkspace();
        workspace.Write("a.txt", "text\n");
        var tool = new GrepTool(ToolOptions.Default);

        var result = await workspace.InvokeAsync(tool, TemporaryWorkspace.Json(new { pattern = "[", regex = true }));

        Assert.True(result.IsError);
        Assert.StartsWith("grep failed:", result.Content, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("..")]
    [InlineData("../outside")]
    public async Task Grep_with_a_path_outside_the_workspace_is_an_error_result(string path)
    {
        using var workspace = new TemporaryWorkspace();
        var tool = new GrepTool(ToolOptions.Default);

        var result = await workspace.InvokeAsync(tool, TemporaryWorkspace.Json(new { pattern = "x", path }));

        Assert.True(result.IsError);
        Assert.Contains(path, result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Grep_is_case_insensitive_when_asked()
    {
        using var workspace = new TemporaryWorkspace();
        workspace.Write("a.txt", "Needle\n");
        var tool = new GrepTool(ToolOptions.Default);

        var sensitive = await workspace.InvokeAsync(tool, TemporaryWorkspace.Json(new { pattern = "needle" }));
        var insensitive = await workspace.InvokeSuccessAsync(
            tool,
            TemporaryWorkspace.Json(new { pattern = "needle", case_sensitive = false }));

        Assert.Equal("no matches", sensitive.Content);
        Assert.Equal("a.txt:1:Needle", insensitive.Content);
    }

    [Fact]
    public async Task Grep_treats_the_pattern_as_a_regular_expression_when_asked()
    {
        using var workspace = new TemporaryWorkspace();
        workspace.Write("a.txt", "line 42\nline 7\nnone\n");
        var tool = new GrepTool(ToolOptions.Default);

        var result = await workspace.InvokeSuccessAsync(
            tool,
            TemporaryWorkspace.Json(new { pattern = @"line \d+\b", regex = true }));

        Assert.Equal(["a.txt:1:line 42", "a.txt:2:line 7"], result.Content.Split('\n').Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Grep_stops_a_text_scan_at_the_first_NUL_so_ripgrep_and_the_managed_engine_agree()
    {
        // The probe window is 8 KiB: a NUL past it is what the two backends used to disagree on — the managed engine kept
        // scanning the bytes after it while ripgrep stops there. A hit before the NUL is still reported, one after is not.
        using var workspace = new TemporaryWorkspace();
        var body = "needle before\n" + new string('x', 100_000) + "\n\0\nneedle after\n";
        workspace.WriteBytes("blob.bin", System.Text.Encoding.UTF8.GetBytes(body));
        var tool = new GrepTool(ToolOptions.Default);

        var result = await workspace.InvokeSuccessAsync(tool, TemporaryWorkspace.Json(new { pattern = "needle" }));

        var lines = result.Content.Split('\n');
        Assert.Equal("blob.bin:1:needle before", lines[0]);
        Assert.DoesNotContain(lines, line => line.Contains("needle after", StringComparison.Ordinal));
        Assert.Contains("1 file(s) skipped: binary", lines);
    }

    [Fact]
    public async Task Ls_marks_a_link_with_its_own_marker()
    {
        using var workspace = new TemporaryWorkspace();
        workspace.Write("plain.txt", "p");
        if (!DirectoryLinks.TryCreateFileLink(Path.Combine(workspace.Root, "alias.txt"), workspace.Full("plain.txt")))
        {
            // A machine without the symlink privilege cannot create the fixture; there is nothing to assert about.
            return;
        }

        var result = await workspace.InvokeSuccessAsync(new ListTool(ToolOptions.Default), TemporaryWorkspace.Json(new { }));

        Assert.Equal(["f plain.txt", "l alias.txt"], result.Content.Split('\n').Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Find_and_grep_skip_a_link_so_a_walk_cannot_reach_past_a_link()
    {
        using var workspace = new TemporaryWorkspace();
        workspace.Write("real/inside.txt", "needle\n");
        var link = Path.Combine(workspace.Root, "alias.txt");
        if (!DirectoryLinks.TryCreateFileLink(link, workspace.Full("real/inside.txt")))
        {
            return;
        }

        var find = await workspace.InvokeSuccessAsync(
            new FindTool(ToolOptions.Default),
            TemporaryWorkspace.Json(new { pattern = "*.txt" }));
        var grep = await workspace.InvokeSuccessAsync(
            new GrepTool(ToolOptions.Default),
            TemporaryWorkspace.Json(new { pattern = "needle" }));

        Assert.Equal(["real/inside.txt"], find.Content.Split('\n').Order(StringComparer.Ordinal));
        Assert.Equal("real/inside.txt:1:needle", grep.Content);
    }
}
