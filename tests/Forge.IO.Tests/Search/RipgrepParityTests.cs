using Forge.IO.Tools;
using Xunit;
using static Forge.IO.Tests.TestSupport;

namespace Forge.IO.Tests;

/// <summary>
/// The managed search engine and the optional ripgrep backend must answer the same query with the same hits: the backend
/// takes every ignore decision away from ripgrep and re-applies the same <see cref="GitIgnore"/> matcher, so a fixture
/// repository with an ignored file, an ignored directory and a nested directory exercises the whole path. Each row
/// compares the <c>path:line</c> sets and the line texts, and a row that expects hits asserts the set is not empty so a
/// pair of empty answers cannot pass as parity. The test is skipping when no working <c>rg</c> is installed; the skip is
/// decided where xunit builds the attribute, because xunit 2 has no runtime skip API.
/// </summary>
public class RipgrepParityTests
{
    [RipgrepFact]
    public async Task The_managed_and_ripgrep_backends_return_the_same_hits()
    {
        var executable = RipgrepProbe.Path;
        Assert.NotNull(executable);
        using var workspace = new TemporaryWorkspace();
        const string Fixture = "public class A {}\n// needle one\n// needle two\n";
        workspace.Write("src/a.cs", Fixture);
        workspace.Write("src/b.cs", "needle at top\n");
        workspace.Write("src/nested/c.cs", "// NEEDLE upper\n// needle lower\n");
        workspace.Write("docs/readme.md", "needle in docs\n");
        workspace.Write("notes.txt", "needle in notes\n");
        workspace.Write("ignored.log", "hidden-token here\n");
        workspace.Write("build/generated.cs", "hidden-token in build\n");
        workspace.Write(".gitignore", "*.log\nbuild/\n");
        var runner = new ProcessRunner();
        using var cancellation = new CancellationTokenSource(TestLimit);

        var queries = new (string Name, GrepRequest Request, bool ExpectHits)[]
        {
            ("literal", Query(workspace.Root, "needle"), true),
            ("literal ignore case", Query(workspace.Root, "NEEDLE") with { CaseSensitive = false }, true),
            ("regular expression", Query(workspace.Root, "n[a-z]+dle") with { Regex = true }, true),
            ("glob *.cs", Query(workspace.Root, "needle") with { Glob = "*.cs" }, true),
            ("subdirectory", Query(workspace.Root, "needle") with { StartPath = workspace.Full("src") }, true),
            ("no match", Query(workspace.Root, "no-such-token"), false),
            ("ignored files and directories", Query(workspace.Root, "hidden-token"), false),
        };

        foreach (var (name, request, expectHits) in queries)
        {
            var managed = await GrepEngine.SearchAsync(request, cancellation.Token);
            var outcome = await RipgrepBackend.SearchAsync(request, executable!, runner, cancellation.Token);

            Assert.True(outcome.Succeeded, $"{name}: ripgrep was not used: {outcome.FailureReason}");
            var fast = outcome.Result ?? throw new InvalidOperationException($"{name}: ripgrep returned no result");

            var managedHits = Hits(managed);
            var ripgrepHits = Hits(fast);
            Assert.Equal(managedHits, ripgrepHits);
            Assert.Equal(expectHits, managedHits.Length > 0);
        }
    }

    [RipgrepFact]
    public async Task Both_backends_stop_a_file_at_its_first_NUL_and_report_it_as_binary()
    {
        // A NUL past the 8 KiB probe: the managed engine keeps the hits before it and drops the ones after, ripgrep does
        // the same by quitting at the byte offset it reports on the file's `end` event. The fixture has a hit on each side
        // so a backend that ignored the NUL would return a different set.
        var executable = RipgrepProbe.Path;
        Assert.NotNull(executable);
        using var workspace = new TemporaryWorkspace();
        var body = "needle before\n" + new string('x', 100_000) + "\n\0\nneedle after\n";
        workspace.WriteBytes("blob.bin", System.Text.Encoding.UTF8.GetBytes(body));
        var query = Query(workspace.Root, "needle");

        var managed = await GrepEngine.SearchAsync(query, CancellationToken.None);
        var outcome = await RipgrepBackend.SearchAsync(query, executable!, new ProcessRunner(), CancellationToken.None);

        Assert.True(outcome.Succeeded, outcome.FailureReason);
        var fast = outcome.Result ?? throw new InvalidOperationException("ripgrep returned no result");
        Assert.Equal(Hits(managed), Hits(fast));
        Assert.Equal(["blob.bin:1:needle before"], Hits(managed));
        // The hit counter is not compared: ripgrep emits per-file events only for files it produced output for, so a binary
        // file with no hit before its NUL is invisible to it and the two counts legitimately differ (recorded in SPEC.md).
        Assert.Equal(1, managed.FilesSkippedBinary);
    }

    [RipgrepFact]
    public async Task A_hit_after_the_NUL_on_the_same_line_is_dropped_by_both_backends()
    {
        // ripgrep's absolute_offset is the line start, so a hit that shares its line with the NUL would survive a filter
        // that compared the line offset. The probe window is 8 KiB and this NUL is past it, so both engines must cut the
        // file there and report nothing.
        var executable = RipgrepProbe.Path;
        Assert.NotNull(executable);
        using var workspace = new TemporaryWorkspace();
        var body = new string('x', 9000) + "\0needle after\n";
        var path = workspace.WriteBytes("sameline.bin", System.Text.Encoding.UTF8.GetBytes(body));
        var query = Query(workspace.Root, "needle") with { StartPath = path };

        var managed = await GrepEngine.SearchAsync(query, CancellationToken.None);
        var outcome = await RipgrepBackend.SearchAsync(query, executable!, new ProcessRunner(), CancellationToken.None);

        Assert.True(outcome.Succeeded, outcome.FailureReason);
        var fast = outcome.Result ?? throw new InvalidOperationException("ripgrep returned no result");
        Assert.Equal(Hits(managed), Hits(fast));
        Assert.Empty(Hits(managed));
    }

    [Fact]
    public async Task The_ripgrep_backend_reports_a_missing_executable_as_a_skip()
    {
        using var workspace = new TemporaryWorkspace();
        workspace.Write("a.txt", "needle\n");
        var runner = new ProcessRunner();

        var outcome = await RipgrepBackend.SearchAsync(
            Query(workspace.Root, "needle"),
            Path.Combine(workspace.Root, "no-such-rg.exe"),
            runner,
            CancellationToken.None);

        Assert.False(outcome.Succeeded);
        Assert.Null(outcome.Result);
        Assert.Contains("not an existing file", outcome.FailureReason ?? string.Empty, StringComparison.Ordinal);
    }

    [RipgrepFact]
    public async Task The_grep_tool_uses_ripgrep_when_a_path_is_configured_and_still_returns_the_same_lines()
    {
        var executable = RipgrepProbe.Path;
        Assert.NotNull(executable);
        using var workspace = new TemporaryWorkspace();
        workspace.Write("src/a.cs", "// needle one\n");
        workspace.Write("src/b.txt", "// needle two\n");
        var managed = new GrepTool(ToolOptions.Default);
        var accelerated = new GrepTool(ToolOptions.Default with { RipgrepPath = executable }, new ProcessRunner());

        var managedResult = await workspace.InvokeSuccessAsync(managed, TemporaryWorkspace.Json(new { pattern = "needle" }));
        var acceleratedResult = await workspace.InvokeSuccessAsync(accelerated, TemporaryWorkspace.Json(new { pattern = "needle" }));

        Assert.Equal(
            managedResult.Content.Split('\n').Order(StringComparer.Ordinal),
            acceleratedResult.Content.Split('\n').Order(StringComparer.Ordinal));
        Assert.DoesNotContain("ripgrep not used", acceleratedResult.Content, StringComparison.Ordinal);
    }

    private static GrepRequest Query(string root, string pattern) => new()
    {
        Root = root,
        StartPath = root,
        Pattern = pattern,
    };

    /// <summary>The hits as <c>path:line:text</c>, sorted, so two backends that find files in a different order still compare.</summary>
    private static string[] Hits(GrepResult result) =>
        [.. result.Lines.Where(line => line.IsMatch)
            .Select(line => $"{line.Path}:{line.LineNumber}:{line.Text}")
            .Order(StringComparer.Ordinal)];
}
