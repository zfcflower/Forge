using System.Globalization;
using System.Text;
using Forge.Workspace;
using Xunit;

namespace Forge.Workspace.Tests.Git;

/// <summary>
/// <c>git clone --progress</c> (wave W9): the pure parse of one progress line, a real clone into a fresh destination (the
/// phases, the percentage reach the caller, the parent is created, a failure and a cancellation leave no half-filled
/// repository), the refusal of a destination that already holds something, and the carriage-return line splitting that makes
/// git's in-place rewrites arrive one by one instead of all at once when the phase ends.
/// </summary>
public sealed class GitCliCloneTests
{
    // ---------------------------------------------------------------- TryParse: the pure grammar of one line

    /// <summary>
    /// One stderr line of <c>git clone --progress</c> and the one update it must produce (or the fact that it produces
    /// none). Every line here was copied from git's own output; the detail is what git appended after the counter, with the
    /// counter itself already removed.
    /// </summary>
    public static TheoryData<string?, bool, GitClonePhase, int?, string> Lines { get; } = new()
    {
        { "Cloning into 'x'...", true, GitClonePhase.Connecting, null, "Cloning into 'x'..." },
        { "remote: Counting objects:   1% (1/54)      ", true, GitClonePhase.Counting, 1, "" },
        { "remote: Counting objects: 100% (54/54), done.", true, GitClonePhase.Counting, 100, "done." },
        { "Receiving objects:  98% (53/54), 1.20 MiB | 2.00 MiB/s", true, GitClonePhase.Receiving, 98, "1.20 MiB | 2.00 MiB/s" },
        { "Receiving objects: 100% (54/54), 978.98 KiB | 3.82 MiB/s, done.", true, GitClonePhase.Receiving, 100, "978.98 KiB | 3.82 MiB/s, done." },
        { "Receiving objects:  50% (27/54)", true, GitClonePhase.Receiving, 50, "" },
        { "Receiving objects:  50%", true, GitClonePhase.Receiving, 50, "" },
        { "remote: Enumerating objects: 54, done.", true, GitClonePhase.Enumerating, null, "done." },
        { "remote: Compressing objects: 100% (27/27), done.", true, GitClonePhase.Compressing, 100, "done." },
        { "Resolving deltas: 100% (3/3), done.", true, GitClonePhase.Resolving, 100, "done." },
        { "Updating files:  50% (27/54)", true, GitClonePhase.Updating, 50, "" },
        { "Checking out files: 100% (54/54), done.", true, GitClonePhase.Updating, 100, "done." },

        // Indented and carriage-return-wrapped lines still parse: git pads its counter and rewrites the line in place.
        { "  remote: Enumerating objects:  54, done.  ", true, GitClonePhase.Enumerating, null, "done." },
        { "\r\rReceiving objects:  40% (4/10)\r", true, GitClonePhase.Receiving, 40, "" },

        // Lines that say nothing about progress: the remote's own summary, blanks, transport chatter.
        { "remote: Total 54 (delta 3), reused 0 (delta 0), pack-reused 0", false, default, null, "" },
        { "", false, default, null, "" },
        { "   \t  ", false, default, null, "" },
        { "fatal: destination path 'x' already exists", false, default, null, "" },

        // The prefix is compared exactly: git prints lowercase, an upper-case one is not a progress line.
        { "Remote: Counting objects: 100% (54/54), done.", false, default, null, "" },

        // A count no int can hold is not trusted: it becomes "no percentage" instead of throwing on the stderr reader.
        { "Receiving objects: 99999999999999999999% (1/2)", true, GitClonePhase.Receiving, null, "% (1/2)" },
    };

    [Theory]
    [MemberData(nameof(Lines))]
    public void One_progress_line_is_parsed_into_one_update(
        string? line,
        bool parsed,
        GitClonePhase phase,
        int? percent,
        string detail)
    {
        var result = GitCloneProgress.TryParse(line, out var progress);

        Assert.Equal(parsed, result);
        if (!parsed)
        {
            Assert.Null(progress);
            return;
        }

        Assert.Equal(phase, progress.Phase);
        Assert.Equal(percent, progress.Percent);
        Assert.Equal(detail, progress.Detail);
    }

    [Fact]
    public void A_line_longer_than_the_cap_keeps_only_the_beginning_of_its_detail()
    {
        var tail = new string('x', 400);
        var line = "Receiving objects: 100% (54/54), " + tail;

        Assert.True(GitCloneProgress.TryParse(line, out var progress));

        // Capped at the documented length, and it is the beginning of git's tail that survives.
        Assert.Equal(GitCloneProgress.MaxDetailCharacters, progress.Detail.Length);
        Assert.Equal(tail[..GitCloneProgress.MaxDetailCharacters], progress.Detail);
    }

    [Fact]
    public void The_phases_are_declared_in_the_order_git_reports_them()
    {
        // A later phase never reports a smaller number than an earlier one, so the enum's own order is the ordering the
        // caller may rely on when it compares phases.
        var order = new[]
        {
            GitClonePhase.Connecting,
            GitClonePhase.Enumerating,
            GitClonePhase.Counting,
            GitClonePhase.Compressing,
            GitClonePhase.Receiving,
            GitClonePhase.Resolving,
            GitClonePhase.Updating,
        };

        for (var i = 0; i < order.Length; i++)
        {
            Assert.Equal(i, (int)order[i]);
        }
    }

    // ---------------------------------------------------------------- CloneAsync: a real clone

    [Fact]
    public async Task A_clone_reaches_the_destination_creates_the_parent_and_reports_progress_in_order()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var sandbox = new TempDirectory();
        var source = CreateSourceRepository(sandbox);
        var destination = sandbox.Combine("out", "nested", "clone");

        var updates = new List<GitCloneProgress>();
        await Git.CloneAsync(
            FileUrl(source.Root),
            destination,
            new GitCloneOptions { OnProgress = progress => { lock (updates) { updates.Add(progress); } } });

        // The clone is a repository with the source's HEAD, and the two missing parent directories were created.
        Assert.True(Directory.Exists(destination));
        Assert.True(Directory.Exists(sandbox.Combine("out")));
        Assert.Equal(source.Head(), RevParse(destination, "HEAD"));

        // git reported while it worked, and every phase and percentage moved forward only. For a repository this small a
        // single line per phase is plausible, so "more than one update" is the assertion that must hold, not "many lines":
        // the phases themselves are the evidence that the stream was read as it arrived.
        Assert.True(updates.Count > 1, $"git reported {updates.Count} update(s)");
        Assert.Contains(updates, static update => update.Percent is not null);

        var phase = 0;
        var perPhasePercent = new Dictionary<GitClonePhase, int>();
        foreach (var update in updates)
        {
            Assert.True((int)update.Phase >= phase, $"phase went back from {(GitClonePhase)phase} to {update.Phase}");
            phase = (int)update.Phase;

            if (update.Percent is not { } percent)
            {
                continue;
            }

            if (perPhasePercent.TryGetValue(update.Phase, out var previous))
            {
                Assert.True(percent >= previous, $"{update.Phase} reported {percent}% after {previous}%");
            }

            perPhasePercent[update.Phase] = percent;
        }

        // The first update is the destination message, before there is any ratio to show.
        Assert.Equal(GitClonePhase.Connecting, updates[0].Phase);
        Assert.Null(updates[0].Percent);
    }

    [Fact]
    public async Task An_existing_destination_that_holds_something_is_refused_and_left_untouched()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var sandbox = new TempDirectory();
        var source = CreateSourceRepository(sandbox);
        var destination = sandbox.Combine("occupied");
        Directory.CreateDirectory(destination);
        var kept = Path.Combine(destination, "keep.txt");
        File.WriteAllText(kept, "not ours to delete", new UTF8Encoding(false));

        await Assert.ThrowsAsync<InvalidOperationException>(() => Git.CloneAsync(FileUrl(source.Root), destination));

        Assert.Equal("not ours to delete", File.ReadAllText(kept, Encoding.UTF8));
        Assert.False(Directory.Exists(Path.Combine(destination, ".git")));
    }

    [Fact]
    public async Task An_existing_empty_destination_is_cloned_into()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var sandbox = new TempDirectory();
        var source = CreateSourceRepository(sandbox);
        var destination = sandbox.Combine("prepared");
        Directory.CreateDirectory(destination);

        await Git.CloneAsync(FileUrl(source.Root), destination);

        Assert.True(Directory.Exists(Path.Combine(destination, ".git")));
        Assert.Equal(source.Head(), RevParse(destination, "HEAD"));
    }

    [Fact]
    public async Task A_url_that_starts_with_a_dash_is_refused_before_anything_is_created()
    {
        using var sandbox = new TempDirectory();
        var destination = sandbox.Combine("dash");

        var failure = await Assert.ThrowsAsync<ArgumentException>(() => Git.CloneAsync("--upload-pack=evil", destination));

        Assert.Equal("url", failure.ParamName);
        Assert.False(Directory.Exists(destination));
    }

    [Fact]
    public async Task A_destination_that_is_not_absolute_is_refused()
    {
        using var sandbox = new TempDirectory();
        var relative = Path.Combine("relative", "clone");

        var failure = await Assert.ThrowsAsync<ArgumentException>(() => Git.CloneAsync("https://example.invalid/team/repo.git", relative));

        Assert.Equal("destination", failure.ParamName);
        Assert.False(Directory.Exists(Path.Combine(sandbox.Path, relative)));
    }

    [Fact]
    public async Task A_failing_clone_removes_the_destination_it_created()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var sandbox = new TempDirectory();
        var missing = sandbox.Combine("no-such-repository");
        var destination = sandbox.Combine("clone");

        var failure = await Assert.ThrowsAsync<GitException>(() => Git.CloneAsync(FileUrl(missing), destination));

        Assert.NotEqual(0, failure.ExitCode);
        Assert.False(Directory.Exists(destination), "a failed clone must not leave a half-filled repository behind");
    }

    [Fact]
    public async Task A_cancelled_clone_throws_and_leaves_no_destination()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var sandbox = new TempDirectory();
        var source = CreateSourceRepository(sandbox);
        var destination = sandbox.Combine("cancelled");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Git.CloneAsync(FileUrl(source.Root), destination, null, cancelled.Token));

        Assert.False(Directory.Exists(destination), "a cancelled clone must not leave a half-filled repository behind");
    }

    [Fact]
    public async Task A_progress_reporter_that_throws_does_not_fail_the_clone()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var sandbox = new TempDirectory();
        var source = CreateSourceRepository(sandbox);
        var destination = sandbox.Combine("reporter-throws");
        var reported = 0;

        await Git.CloneAsync(
            FileUrl(source.Root),
            destination,
            new GitCloneOptions
            {
                OnProgress = _ =>
                {
                    // The reporter runs on the thread that reads git's stderr, not the caller's.
                    Interlocked.Increment(ref reported);
                    throw new InvalidOperationException("the reporter is broken");
                },
            });

        Assert.True(Volatile.Read(ref reported) > 0, "the reporter was never called, so the swallow was not exercised");
        Assert.Equal(source.Head(), RevParse(destination, "HEAD"));
    }

    // ---------------------------------------------------------------- CarriageReturnEndsLine

    /// <summary>
    /// Git rewrites one progress line in place, so a phase writes several updates separated by a bare <c>\r</c> and no
    /// newline at all. A stand-in executable that writes exactly those bytes proves the option turns that one stream into
    /// the individual updates, without depending on how fast a real clone runs.
    /// </summary>
    [Fact]
    public async Task A_carriage_return_ends_a_line_and_a_crlf_ends_one_line_not_two()
    {
        using var fake = new FixedOutputGit("a\r\nb\rc\r\n");

        var splitting = await LinesAsync(fake, carriageReturnEndsLine: true);
        Assert.Equal(["a", "b", "c"], splitting);

        // The same bytes read the old way: only LF breaks a line, so the bare CRs stay inside one line.
        var joined = await LinesAsync(fake, carriageReturnEndsLine: false);
        Assert.Equal(["a", "b\rc"], joined);
    }

    /// <summary>
    /// The <c>\r</c> that ends a chunk and the <c>\n</c> that opens the next one are one CRLF. The bytes below put that pair
    /// exactly at the stream buffer's boundary, and the expected lines hold wherever the reads happened to split: a CRLF is
    /// one break, never a break plus an empty line.
    /// </summary>
    [Fact]
    public async Task A_crlf_split_across_two_reads_is_still_one_break()
    {
        var filler = new string('x', (16 * 1024) - 1);
        using var fake = new FixedOutputGit(filler + "\r\n tail\r\n");

        var lines = await LinesAsync(fake, carriageReturnEndsLine: true);

        Assert.Equal(2, lines.Count);
        Assert.Equal(filler, lines[0]);
        Assert.Equal(" tail", lines[1]);
    }

    [Fact]
    public async Task A_bit_of_text_ending_in_a_bare_cr_is_reported_without_it()
    {
        using var fake = new FixedOutputGit("Receiving objects:  10% (1/10)\rReceiving objects: 100% (10/10), done.\r\n");

        var updates = new List<GitCloneProgress>();
        var result = await new GitCli(fake.Executable).RunAsync(
            fake.WorkingDirectory,
            [],
            new GitRunOptions
            {
                CarriageReturnEndsLine = true,
                OnStandardErrorLine = line =>
                {
                    if (GitCloneProgress.TryParse(line, out var progress))
                    {
                        updates.Add(progress);
                    }
                },
            });

        Assert.True(result.Success);
        Assert.Equal([10, 100], updates.Select(static update => update.Percent));
        Assert.Equal("done.", updates[1].Detail);
    }

    // ---------------------------------------------------------------- helpers

    private static readonly GitCli Git = new();

    private static async Task<List<string>> LinesAsync(FixedOutputGit fake, bool carriageReturnEndsLine)
    {
        var lines = new List<string>();
        var result = await new GitCli(fake.Executable).RunAsync(
            fake.WorkingDirectory,
            [],
            new GitRunOptions
            {
                CarriageReturnEndsLine = carriageReturnEndsLine,
                OnStandardErrorLine = line => { lock (lines) { lines.Add(line); } },
            });

        Assert.True(result.Success, result.StandardError);
        return lines;
    }

    /// <summary>A repository on <c>main</c> with a couple of committed files, built by git itself (never by the code under test).</summary>
    private static TestRepo CreateSourceRepository(TempDirectory sandbox)
    {
        var repo = new TestRepo(sandbox, "source");
        for (var i = 0; i < 4; i++)
        {
            repo.Write(Path.Combine("src", "file" + i.ToString(CultureInfo.InvariantCulture) + ".txt"), "content " + i.ToString(CultureInfo.InvariantCulture) + "\n");
        }

        repo.Write("README.md", "# source\n");
        repo.Commit("initial import");
        return repo;
    }

    /// <summary>The <c>file://</c> form of a local path: git then uses its transport layer, which reports progress.</summary>
    private static string FileUrl(string path) => new Uri(Path.GetFullPath(path)).AbsoluteUri;

    private static string RevParse(string directory, string revision)
    {
        var (exitCode, output, error) = GitFixture.Run(directory, "rev-parse", revision);
        Assert.True(exitCode == 0, $"git rev-parse {revision} failed: {error}");
        return output.Trim();
    }
}

/// <summary>
/// A stand-in for git that writes a fixed byte sequence to stderr and exits 0. A batch file cannot print a bare carriage
/// return (<c>echo</c> always writes CRLF), so the bytes live in a file beside the script and the script copies them to
/// stderr; the payload is what the test wrote, byte for byte.
/// </summary>
internal sealed class FixedOutputGit : IDisposable
{
    private readonly TempDirectory root = new();

    public FixedOutputGit(string standardErrorPayload)
    {
        var directory = root.Combine("fake");
        Directory.CreateDirectory(directory);
        WorkingDirectory = root.Combine("work");
        Directory.CreateDirectory(WorkingDirectory);
        File.WriteAllBytes(Path.Combine(directory, "payload.bin"), new UTF8Encoding(false).GetBytes(standardErrorPayload));
        Executable = WriteScript(directory);
    }

    public string Executable { get; }

    public string WorkingDirectory { get; }

    public void Dispose() => root.Dispose();

    private static string WriteScript(string directory)
    {
        if (OperatingSystem.IsWindows())
        {
            var path = Path.Combine(directory, "git.cmd");
            File.WriteAllText(
                path,
                "@echo off\r\n" +
                "type \"%~dp0payload.bin\" 1>&2\r\n" +
                "exit /b 0\r\n",
                Encoding.ASCII);
            return path;
        }

        var shell = Path.Combine(directory, "git");
        File.WriteAllText(
            shell,
            "#!/bin/sh\n" +
            "cat \"$(dirname \"$0\")/payload.bin\" >&2\n" +
            "exit 0\n",
            new UTF8Encoding(false));
        File.SetUnixFileMode(
            shell,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute);
        return shell;
    }
}