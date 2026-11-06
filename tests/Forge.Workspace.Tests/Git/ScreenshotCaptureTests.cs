using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Xunit;

namespace Forge.Workspace.Tests.Git;

/// <summary>Shell commands and PNG bytes shared by the screenshot tests. Every command exists in a cmd and an sh flavour.</summary>
internal static class ScreenshotTestData
{
    private static readonly byte[] Signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>
    /// A file that starts with the PNG signature followed by <paramref name="tag"/>. The code under test only checks the
    /// signature, so a tag is a cheap way to tell images apart.
    /// </summary>
    public static byte[] Png(string tag) => [.. Signature, .. Encoding.UTF8.GetBytes(tag)];

    /// <summary>The tag a file made with <see cref="Png"/> carries.</summary>
    public static string Tag(string? path)
    {
        Assert.NotNull(path);
        var bytes = File.ReadAllBytes(path);
        return Encoding.UTF8.GetString(bytes, Signature.Length, bytes.Length - Signature.Length);
    }

    /// <summary>The names of the files directly in <paramref name="directory"/>, in ordinal order.</summary>
    public static string[] FileNames(string directory) =>
        Directory.GetFiles(directory).Select(static file => Path.GetFileName(file)).Order(StringComparer.Ordinal).ToArray();

    public static string Pick(string windows, string unix) => OperatingSystem.IsWindows() ? windows : unix;

    /// <summary>The committed script of the fixture: copies page.png as home.png, plus extra.png and removed.png where they exist.</summary>
    public static string CaptureCommand => Pick("call .\\capture.cmd", "sh capture.sh");

    public static string CopyPage(string name = "home") => Pick(
        $"copy /Y page.png \"%FORGE_SCREENSHOT_DIR%\\{name}.png\" >nul",
        $"cp page.png \"$FORGE_SCREENSHOT_DIR/{name}.png\"");

    /// <summary>Runs the given action on one side and copies page.png as home.png on the other.</summary>
    public static string OnSide(string side, string windowsAction, string unixAction) => Pick(
        $"if \"%FORGE_SCREENSHOT_SIDE%\"==\"{side}\" ({windowsAction}) else ({CopyPage()})",
        $"if [ \"$FORGE_SCREENSHOT_SIDE\" = \"{side}\" ]; then {unixAction}; else {CopyPage()}; fi");

    public static string FailOn(string side, int exitCode, string message) =>
        OnSide(side, $"echo {message} 1>&2 & exit /b {exitCode}", $"echo {message} >&2; exit {exitCode}");

    public static string Hang { get; } = Pick("ping -n 120 127.0.0.1 >nul", "sleep 120");

    /// <summary>Starts a child process of its own that writes its process id to <paramref name="pidFile"/> and then sleeps.</summary>
    public static string HangWithPid(string pidFile) => Pick(
        $"powershell -NoProfile -NonInteractive -Command \"Set-Content -LiteralPath '{pidFile}' -Value $PID; Start-Sleep -Seconds 120\"",
        $"sh -c 'echo $$ > \"{pidFile}\"; exec sleep 120'");

    /// <summary>Copies the tree in "fixtures" (when the checkout has one) into the output directory.</summary>
    public static string CopyFixtureTree { get; } = Pick(
        "if exist fixtures xcopy /E /I /Y /Q fixtures \"%FORGE_SCREENSHOT_DIR%\" >nul",
        "if [ -d fixtures ]; then cp -R fixtures/. \"$FORGE_SCREENSHOT_DIR/\"; fi");

    public static async Task<int?> WaitForProcessIdAsync(string pidFile, TimeSpan timeout)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < timeout)
        {
            try
            {
                if (File.Exists(pidFile) && int.TryParse(File.ReadAllText(pidFile).Trim(), out var pid))
                {
                    return pid;
                }
            }
            catch (IOException)
            {
            }

            await Task.Delay(50);
        }

        return null;
    }

    public static bool IsRunning(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    public static async Task<bool> WaitUntilGoneAsync(int pid, TimeSpan timeout)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < timeout)
        {
            if (!IsRunning(pid))
            {
                return true;
            }

            await Task.Delay(100);
        }

        return !IsRunning(pid);
    }

    /// <summary>A directory junction (Windows, no privilege needed) or symbolic link; false when the platform refuses.</summary>
    public static bool TryCreateDirectoryLink(string link, string target)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var startInfo = new ProcessStartInfo("cmd.exe")
                {
                    Arguments = $"/c mklink /J \"{link}\" \"{target}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                using var process = Process.Start(startInfo)!;
                process.StandardOutput.ReadToEnd();
                process.StandardError.ReadToEnd();
                process.WaitForExit();
                return process.ExitCode == 0 && Directory.Exists(link);
            }

            Directory.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException or Win32Exception)
        {
            return false;
        }
    }
}

/// <summary>
/// A bare origin whose address, as far as the configuration is concerned, is a GitHub one (the real path is reached through
/// <c>url.insteadOf</c>), with <c>main</c> (page.png, removed.png, capture script) and a work clone on <c>forge/feature</c> that
/// changes page.png, adds extra.png and deletes removed.png. Capturing the change therefore yields one screen with both
/// sides (home), one that only exists after (extra) and one that only exists before (removed).
/// </summary>
internal sealed class ScreenshotFixture : IDisposable
{
    public const string Branch = "forge/feature";
    public const string PublicAddress = "https://github.com/acme/forge.git";

    private const string CaptureCmd =
        "@echo off\n" +
        "copy /Y page.png \"%FORGE_SCREENSHOT_DIR%\\home.png\" >nul || exit /b 1\n" +
        "if exist extra.png copy /Y extra.png \"%FORGE_SCREENSHOT_DIR%\\extra.png\" >nul\n" +
        "if exist removed.png copy /Y removed.png \"%FORGE_SCREENSHOT_DIR%\\removed.png\" >nul\n" +
        "echo captured side=%FORGE_SCREENSHOT_SIDE%\n" +
        "exit /b 0\n";

    private const string CaptureSh =
        "#!/bin/sh\n" +
        "cp page.png \"$FORGE_SCREENSHOT_DIR/home.png\" || exit 1\n" +
        "[ -f extra.png ] && cp extra.png \"$FORGE_SCREENSHOT_DIR/extra.png\"\n" +
        "[ -f removed.png ] && cp removed.png \"$FORGE_SCREENSHOT_DIR/removed.png\"\n" +
        "echo \"captured side=$FORGE_SCREENSHOT_SIDE\"\n" +
        "exit 0\n";

    private readonly RemoteSandbox sandbox = new();
    private readonly TempDirectory scratch = new();
    private readonly TempDirectory markers = new();

    public ScreenshotFixture()
    {
        Seed = sandbox.Clone("seed");
        File.WriteAllBytes(Path.Combine(Seed, "page.png"), ScreenshotTestData.Png("page-base"));
        File.WriteAllBytes(Path.Combine(Seed, "removed.png"), ScreenshotTestData.Png("removed-base"));
        File.WriteAllText(Path.Combine(Seed, "capture.cmd"), CaptureCmd.Replace("\n", "\r\n"), new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(Seed, "capture.sh"), CaptureSh, new UTF8Encoding(false));
        sandbox.CommitFile(Seed, "base.txt", "base\n", "seed");
        IsolatedGit.RunChecked(Seed, "push", "-q", "-u", "origin", "main");

        Work = sandbox.Clone("work");
        IsolatedGit.RunChecked(Work, "checkout", "-q", "-b", Branch);
        File.WriteAllBytes(Path.Combine(Work, "page.png"), ScreenshotTestData.Png("page-feature"));
        File.WriteAllBytes(Path.Combine(Work, "extra.png"), ScreenshotTestData.Png("extra-feature"));
        File.Delete(Path.Combine(Work, "removed.png"));
        sandbox.CommitFile(Work, "feature.txt", "feature\n", "change the screens");

        UsePublicAddress();
    }

    public GitCli Git { get; } = new();

    public string Seed { get; }

    public string Work { get; }

    public string Origin => sandbox.Origin;

    /// <summary>Where captures put their temporary directories (the <c>TempRoot</c> option).</summary>
    public string Scratch => scratch.Path;

    /// <summary>A directory for marker and process id files that the commands write.</summary>
    public string Markers => markers.Path;

    public PrLifecycleFakePlatform Platform { get; } = new();

    /// <summary>Makes <c>origin</c> look like a GitHub repository while pushes still land in the local bare repository.</summary>
    public void UsePublicAddress()
    {
        var local = Origin.Replace('\\', '/');
        IsolatedGit.RunChecked(Work, "config", "--local", "remote.origin.url", PublicAddress);
        IsolatedGit.RunChecked(Work, "config", "--local", "url." + local + ".insteadOf", PublicAddress);
    }

    /// <summary>Points <c>origin</c> at an address the screenshot links cannot be built from.</summary>
    public void UseAddress(string address)
    {
        IsolatedGit.RunChecked(Work, "config", "--local", "remote.origin.url", address);
        IsolatedGit.Run(Work, "config", "--local", "--unset-all", "url." + Origin.Replace('\\', '/') + ".insteadOf");
    }

    public ScreenshotCaptureOptions Options(TimeSpan? timeout = null, IReadOnlyList<string>? overlay = null) =>
        new() { TempRoot = Scratch, Timeout = timeout, OverlayPaths = overlay ?? [] };

    public Task<ScreenshotCaptureResult> CaptureAsync(
        string? command,
        TimeSpan? timeout = null,
        IReadOnlyList<string>? overlay = null,
        string baseRef = "origin/main",
        CancellationToken ct = default) =>
        new ScreenshotCapture(Git, Options(timeout, overlay)).CaptureAsync(Work, baseRef, command, ct);

    /// <summary>Commits the screenshot declaration on the work branch.</summary>
    public void WriteConfig(string command, string output, int? timeoutSeconds = null)
    {
        var json = new StringBuilder("{\"command\":")
            .Append(System.Text.Json.JsonSerializer.Serialize(command))
            .Append(",\"output\":")
            .Append(System.Text.Json.JsonSerializer.Serialize(output));
        if (timeoutSeconds is { } seconds)
        {
            json.Append(",\"timeoutSeconds\":").Append(seconds);
        }

        json.Append('}');
        Write(".forge/screenshots.json", json.ToString());
        CommitAll("declare how to take screenshots");
    }

    public void Write(string relative, string content)
    {
        var path = Path.Combine(Work, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, new UTF8Encoding(false));
    }

    /// <summary>
    /// A one-off capture script that exists only on the work branch (uncommitted): the base worktree gets it through
    /// <see cref="ScreenshotCaptureOptions.OverlayPaths"/> for the "before" run.
    /// </summary>
    public void WriteOneOffScript()
    {
        Write("one-off.cmd", "@echo off\r\ncopy /Y page.png \"%FORGE_SCREENSHOT_DIR%\\home.png\" >nul || exit /b 1\r\nexit /b 0\r\n");
        Write("one-off.sh", "#!/bin/sh\ncp page.png \"$FORGE_SCREENSHOT_DIR/home.png\" || exit 1\nexit 0\n");
    }

    public void WritePng(string relative, string tag)
    {
        var path = Path.Combine(Work, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, ScreenshotTestData.Png(tag));
    }

    public string CommitAll(string message)
    {
        IsolatedGit.RunChecked(Work, "add", "-A");
        IsolatedGit.RunChecked(Work, "commit", "-q", "-m", message);
        return IsolatedGit.Head(Work);
    }

    /// <summary>A new commit on main that changes page.png, pushed, and fetched into the work clone.</summary>
    public string AdvanceBase(string tag)
    {
        File.WriteAllBytes(Path.Combine(Seed, "page.png"), ScreenshotTestData.Png(tag));
        IsolatedGit.RunChecked(Seed, "add", "-A");
        IsolatedGit.RunChecked(Seed, "commit", "-q", "-m", "change the base page");
        IsolatedGit.RunChecked(Seed, "push", "-q", "origin", "main");
        IsolatedGit.RunChecked(Work, "fetch", "-q", "origin");
        return IsolatedGit.Head(Seed);
    }

    public void RejectPushes()
    {
        var hook = Path.Combine(Origin, "hooks", "pre-receive");
        Directory.CreateDirectory(Path.GetDirectoryName(hook)!);
        File.WriteAllText(hook, "#!/bin/sh\necho 'rejected by policy' >&2\nexit 1\n", new UTF8Encoding(false));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(hook, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    public string Status() => IsolatedGit.RunChecked(Work, "status", "--porcelain");

    public IReadOnlyList<string> Worktrees() =>
        IsolatedGit.RunChecked(Work, "worktree", "list", "--porcelain")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(static line => line.StartsWith("worktree ", StringComparison.Ordinal))
            .ToList();

    public IReadOnlyList<string> CaptureDirectories() => Directory.GetDirectories(Scratch, "forge-screenshots-*");

    /// <summary>The tip of a branch on origin, or null when it is not there.</summary>
    public string? OriginTip(string branch)
    {
        var (exitCode, output, _) = IsolatedGit.Run(Origin, "rev-parse", "--verify", "-q", "refs/heads/" + branch);
        return exitCode == 0 ? output.Trim() : null;
    }

    /// <summary>Asserts that a capture left nothing behind: no extra worktree, no registration, no temporary directory.</summary>
    public void AssertNoTrace()
    {
        AssertNoWorktree();
        Assert.Empty(CaptureDirectories());
    }

    /// <summary>Asserts that the temporary worktree of the base is gone and no longer registered; the images of a successful capture may remain.</summary>
    public void AssertNoWorktree()
    {
        Assert.Single(Worktrees());
        var registrations = Path.Combine(Work, ".git", "worktrees");
        Assert.True(
            !Directory.Exists(registrations) || Directory.GetFileSystemEntries(registrations).Length == 0,
            "the temporary worktree is still registered");
    }

    public void Dispose()
    {
        sandbox.Dispose();
        scratch.Dispose();
        markers.Dispose();
    }
}

/// <summary>
/// <see cref="ScreenshotCapture"/> against real repositories: the command runs once in the session worktree and once in a
/// temporary worktree of the base, images are paired by name, and nothing is left behind however the run ends.
/// </summary>
public sealed class ScreenshotCaptureTests
{
    [Fact]
    public async Task Capture_pairs_before_and_after_images_by_file_name_and_keeps_one_sided_images()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var fixture = new ScreenshotFixture();

        var result = await fixture.CaptureAsync(ScreenshotTestData.CaptureCommand);

        Assert.True(result.Success, result.Message);
        Assert.Equal("Captured 3 screen(s): 1 with before and after, 1 after only, 1 before only.", result.Message);
        Assert.Equal(["extra", "home", "removed"], result.Pairs.Select(static pair => pair.Name).ToArray());
        Assert.Empty(result.Notes);

        var extra = result.Pairs[0];
        Assert.False(extra.HasBefore);
        Assert.Equal("extra-feature", ScreenshotTestData.Tag(extra.AfterPath));

        var home = result.Pairs[1];
        Assert.Equal("page-base", ScreenshotTestData.Tag(home.BeforePath));
        Assert.Equal("page-feature", ScreenshotTestData.Tag(home.AfterPath));
        Assert.Equal("home-before.png", Path.GetFileName(home.BeforePath));
        Assert.Equal("home-after.png", Path.GetFileName(home.AfterPath));

        var removed = result.Pairs[2];
        Assert.False(removed.HasAfter);
        Assert.Equal("removed-base", ScreenshotTestData.Tag(removed.BeforePath));
    }

    [Fact]
    public async Task Capture_copies_the_images_to_a_temporary_directory_and_drops_everything_else()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var fixture = new ScreenshotFixture();

        var result = await fixture.CaptureAsync(ScreenshotTestData.CaptureCommand);

        Assert.True(result.Success, result.Message);
        var directory = Assert.IsType<string>(result.CaptureDirectory);
        Assert.Equal(fixture.CaptureDirectories().Single(), directory);
        Assert.StartsWith(fixture.Scratch, directory, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            ["extra-after.png", "home-after.png", "home-before.png", "removed-before.png"],
            ScreenshotTestData.FileNames(Path.Combine(directory, "images")));
        Assert.False(Directory.Exists(Path.Combine(directory, "raw")));
        Assert.False(Directory.Exists(Path.Combine(directory, "base")));

        Assert.Single(fixture.Worktrees());
        Assert.Equal(string.Empty, fixture.Status());

        ScreenshotCapture.DeleteCaptureDirectory(directory);
        Assert.False(Directory.Exists(directory));
    }

    [Fact]
    public async Task Capture_reports_how_each_side_ran_and_tells_the_command_which_side_it_is()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var fixture = new ScreenshotFixture();

        var result = await fixture.CaptureAsync(ScreenshotTestData.CaptureCommand);

        Assert.True(result.Success, result.Message);
        var after = Assert.IsType<ScreenshotRunReport>(result.After);
        var before = Assert.IsType<ScreenshotRunReport>(result.Before);
        Assert.Equal("after", after.Side);
        Assert.Equal("before", before.Side);
        Assert.True(after.Succeeded);
        Assert.True(before.Succeeded);
        Assert.Contains("captured side=after", after.OutputTail, StringComparison.Ordinal);
        Assert.Contains("captured side=before", before.OutputTail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Capture_with_a_configuration_uses_its_command_and_output_directory_and_cleans_up_after_itself()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var fixture = new ScreenshotFixture();
        fixture.WriteConfig(ScreenshotTestData.CaptureCommand, "build/shots");

        var result = await fixture.CaptureAsync(null);

        Assert.True(result.Success, result.Message);
        Assert.Equal(["extra", "home", "removed"], result.Pairs.Select(static pair => pair.Name).ToArray());
        Assert.Equal("page-base", ScreenshotTestData.Tag(result.Pairs[1].BeforePath));
        Assert.Equal("page-feature", ScreenshotTestData.Tag(result.Pairs[1].AfterPath));
        Assert.False(Directory.Exists(Path.Combine(fixture.Work, "build")), "the output directory was left in the worktree");
        Assert.Equal(string.Empty, fixture.Status());
        Assert.Single(fixture.Worktrees());
    }

    [Fact]
    public async Task Capture_prefers_the_command_it_is_given_over_the_configured_one()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var fixture = new ScreenshotFixture();
        fixture.WriteConfig(ScreenshotTestData.Pick("exit /b 9", "exit 9"), "build/shots");

        var result = await fixture.CaptureAsync(ScreenshotTestData.CaptureCommand);

        Assert.True(result.Success, result.Message);
        Assert.Equal(3, result.Pairs.Count);
    }

    [Fact]
    public async Task Capture_leaves_files_that_were_already_in_the_output_directory_alone_and_does_not_publish_them()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var fixture = new ScreenshotFixture();
        fixture.WritePng("shots/keep.png", "tracked-before-the-run");
        fixture.WriteConfig(ScreenshotTestData.CaptureCommand, "shots");
        fixture.WritePng("shots/old.png", "stale-untracked");

        var result = await fixture.CaptureAsync(null);

        Assert.True(result.Success, result.Message);
        Assert.Equal(["extra", "home", "removed"], result.Pairs.Select(static pair => pair.Name).ToArray());
        Assert.Equal(
            ["keep.png", "old.png"],
            ScreenshotTestData.FileNames(Path.Combine(fixture.Work, "shots")));
        Assert.Equal("tracked-before-the-run", ScreenshotTestData.Tag(Path.Combine(fixture.Work, "shots", "keep.png")));
        Assert.Equal("stale-untracked", ScreenshotTestData.Tag(Path.Combine(fixture.Work, "shots", "old.png")));
        Assert.Equal("?? shots/old.png\n", fixture.Status());
    }

    [Fact]
    public async Task Capture_without_a_configuration_or_a_command_says_it_cannot_capture_automatically()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var fixture = new ScreenshotFixture();

        foreach (var command in new string?[] { null, "", "   " })
        {
            var result = await fixture.CaptureAsync(command);

            Assert.False(result.Success);
            Assert.Contains("Cannot capture screenshots automatically", result.Message, StringComparison.Ordinal);
            Assert.Contains("FORGE_SCREENSHOT_DIR", result.Message, StringComparison.Ordinal);
            Assert.Empty(result.Pairs);
            Assert.Null(result.CaptureDirectory);
            Assert.Null(result.After);
            Assert.Null(result.Before);
        }

        fixture.AssertNoTrace();
    }

    [Fact]
    public async Task Capture_with_an_invalid_configuration_explains_the_problem_and_runs_nothing()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var fixture = new ScreenshotFixture();
        fixture.Write(".forge/screenshots.json", """{"command":"x","output":"o","timeout":5}""");

        var result = await fixture.CaptureAsync(ScreenshotTestData.CaptureCommand);

        Assert.False(result.Success);
        Assert.Contains("unknown property \"timeout\"", result.Message, StringComparison.Ordinal);
        Assert.Null(result.After);
        fixture.AssertNoTrace();
    }

    [Fact]
    public async Task Capture_fails_when_the_base_does_not_resolve_to_a_commit()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var fixture = new ScreenshotFixture();

        var result = await fixture.CaptureAsync(ScreenshotTestData.CaptureCommand, baseRef: "origin/does-not-exist");

        Assert.False(result.Success);
        Assert.Contains("'origin/does-not-exist' does not resolve to a commit", result.Message, StringComparison.Ordinal);
        Assert.Null(result.After);
        fixture.AssertNoTrace();
    }

    [Fact]
    public async Task Capture_runs_the_before_side_at_the_given_base_and_notes_when_the_branch_is_behind_it()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var fixture = new ScreenshotFixture();
        fixture.AdvanceBase("page-newer-base");

        var result = await fixture.CaptureAsync(ScreenshotTestData.CaptureCommand);

        Assert.True(result.Success, result.Message);
        Assert.Equal("page-newer-base", ScreenshotTestData.Tag(result.Pairs[1].BeforePath));
        Assert.Equal("page-feature", ScreenshotTestData.Tag(result.Pairs[1].AfterPath));
        var note = Assert.Single(result.Notes);
        Assert.Contains("origin/main is not an ancestor of HEAD", note, StringComparison.Ordinal);
        Assert.Contains("pr_sync", note, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("after")]
    [InlineData("before")]
    public async Task Capture_with_a_failing_side_returns_its_exit_code_and_output_and_no_images(string failingSide)
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var fixture = new ScreenshotFixture();

        var result = await fixture.CaptureAsync(ScreenshotTestData.FailOn(failingSide, 3, "boom on the " + failingSide + " side"));

        Assert.False(result.Success);
        Assert.Empty(result.Pairs);
        Assert.Null(result.CaptureDirectory);
        Assert.Contains("failed with exit code 3", result.Message, StringComparison.Ordinal);
        Assert.Contains($"\"{failingSide}\" side", result.Message, StringComparison.Ordinal);
        Assert.Contains("boom on the " + failingSide + " side", result.Message, StringComparison.Ordinal);

        var report = failingSide == "after" ? result.After : result.Before;
        Assert.NotNull(report);
        Assert.Equal(3, report.ExitCode);
        Assert.False(report.TimedOut);
        Assert.False(report.Succeeded);
        Assert.Contains("boom on the " + failingSide, report.OutputTail, StringComparison.Ordinal);

        if (failingSide == "after")
        {
            Assert.Null(result.Before);
            Assert.DoesNotContain("also has to run on the base", result.Message, StringComparison.Ordinal);
        }
        else
        {
            Assert.True(Assert.IsType<ScreenshotRunReport>(result.After).Succeeded);
            Assert.Contains("also has to run on the base", result.Message, StringComparison.Ordinal);
        }

        fixture.AssertNoTrace();
        Assert.Equal(string.Empty, fixture.Status());
    }

    [Fact]
    public async Task Capture_removes_what_a_failing_command_left_in_the_output_directory()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var fixture = new ScreenshotFixture();
        var command = ScreenshotTestData.Pick(
            "copy /Y page.png \"%FORGE_SCREENSHOT_DIR%\\half.png\" >nul & exit /b 5",
            "cp page.png \"$FORGE_SCREENSHOT_DIR/half.png\"; exit 5");
        fixture.WriteConfig(command, "out/shots");

        var result = await fixture.CaptureAsync(null);

        Assert.False(result.Success);
        Assert.Contains("exit code 5", result.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(fixture.Work, "out")), "the half-written output was left in the worktree");
        Assert.Equal(string.Empty, fixture.Status());
        fixture.AssertNoTrace();
    }

    [Fact]
    public async Task Capture_fails_when_both_sides_succeed_but_write_no_png_file()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var fixture = new ScreenshotFixture();

        var result = await fixture.CaptureAsync(ScreenshotTestData.Pick("echo nothing to show", "echo nothing to show"));

        Assert.False(result.Success);
        Assert.Contains("wrote no PNG files", result.Message, StringComparison.Ordinal);
        Assert.Contains("FORGE_SCREENSHOT_DIR", result.Message, StringComparison.Ordinal);
        Assert.Empty(result.Pairs);
        Assert.Null(result.CaptureDirectory);
        fixture.AssertNoTrace();
    }

    [Theory]
    [InlineData("after")]
    [InlineData("before")]
    public async Task Capture_kills_a_command_that_runs_past_the_timeout_and_cleans_up(string hangingSide)
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var fixture = new ScreenshotFixture();
        var clock = Stopwatch.StartNew();

        var result = await fixture.CaptureAsync(
            ScreenshotTestData.OnSide(hangingSide, ScreenshotTestData.Hang, ScreenshotTestData.Hang),
            timeout: TimeSpan.FromSeconds(2));

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(60), "the capture waited for the sleeping command instead of killing it");
        Assert.False(result.Success);
        Assert.Empty(result.Pairs);
        Assert.Contains("timed out after", result.Message, StringComparison.Ordinal);
        Assert.Contains("process tree was killed", result.Message, StringComparison.Ordinal);
        Assert.Contains($"\"{hangingSide}\" side", result.Message, StringComparison.Ordinal);

        var report = Assert.IsType<ScreenshotRunReport>(hangingSide == "after" ? result.After : result.Before);
        Assert.True(report.TimedOut);
        Assert.False(report.Succeeded);
        Assert.True(report.Duration >= TimeSpan.FromSeconds(1.5), $"killed after only {report.Duration}");
        if (hangingSide == "before")
        {
            Assert.True(Assert.IsType<ScreenshotRunReport>(result.After).Succeeded);
        }
        else
        {
            Assert.Null(result.Before);
        }

        fixture.AssertNoTrace();
        Assert.Equal(string.Empty, fixture.Status());
    }

    [Theory]
    [InlineData("after")]
    [InlineData("before")]
    public async Task Capture_that_is_cancelled_kills_the_process_tree_and_cleans_up(string hangingSide)
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var fixture = new ScreenshotFixture();
        var pidFile = Path.Combine(fixture.Markers, "pid.txt");
        var hang = ScreenshotTestData.HangWithPid(pidFile);
        using var cancellation = new CancellationTokenSource();
        var capture = fixture.CaptureAsync(ScreenshotTestData.OnSide(hangingSide, hang, hang), ct: cancellation.Token);

        var started = await ScreenshotTestData.WaitForProcessIdAsync(pidFile, TimeSpan.FromSeconds(60));
        Assert.True(started.HasValue, "the command never started");
        var pid = started.GetValueOrDefault();
        Assert.True(ScreenshotTestData.IsRunning(pid), "the grandchild should be running before the cancel");
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => capture);
        Assert.True(
            await ScreenshotTestData.WaitUntilGoneAsync(pid, TimeSpan.FromSeconds(20)),
            "a process started by the command survived the cancel");
        fixture.AssertNoTrace();
        Assert.Equal(string.Empty, fixture.Status());
    }

    [Fact]
    public async Task Capture_with_a_token_that_is_already_cancelled_does_nothing()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var fixture = new ScreenshotFixture();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => fixture.CaptureAsync(ScreenshotTestData.CaptureCommand, ct: cancellation.Token));

        fixture.AssertNoTrace();
    }

    [Fact]
    public async Task Capture_copies_helper_paths_into_the_base_worktree_so_a_one_off_script_can_run_there()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var fixture = new ScreenshotFixture();
        WriteOneOffScript(fixture);
        var command = ScreenshotTestData.Pick("call tools\\oneoff.cmd", "sh tools/oneoff.sh");

        var result = await fixture.CaptureAsync(command, overlay: ["tools"]);

        Assert.True(result.Success, result.Message);
        var home = Assert.Single(result.Pairs);
        Assert.Equal("page-base", ScreenshotTestData.Tag(home.BeforePath));
        Assert.Equal("page-feature", ScreenshotTestData.Tag(home.AfterPath));
        Assert.Contains(result.Notes, static note => note.Contains("Copied 1 helper file(s)", StringComparison.Ordinal));
        Assert.True(File.Exists(Path.Combine(fixture.Work, "tools", ScreenshotTestData.Pick("oneoff.cmd", "oneoff.sh"))), "the script must stay for the caller to remove");
        fixture.AssertNoWorktree();
    }

    [Fact]
    public async Task Capture_without_the_helper_path_cannot_run_a_one_off_script_on_the_base()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var fixture = new ScreenshotFixture();
        WriteOneOffScript(fixture);
        var command = ScreenshotTestData.Pick("call tools\\oneoff.cmd", "sh tools/oneoff.sh");

        var result = await fixture.CaptureAsync(command);

        Assert.False(result.Success);
        Assert.Contains("\"before\" side", result.Message, StringComparison.Ordinal);
        Assert.Contains("files that exist only on this branch are missing there", result.Message, StringComparison.Ordinal);
        Assert.True(Assert.IsType<ScreenshotRunReport>(result.After).Succeeded);
        Assert.NotEqual(0, Assert.IsType<ScreenshotRunReport>(result.Before).ExitCode);
        fixture.AssertNoTrace();
    }

    [Fact]
    public async Task Capture_never_replaces_a_file_the_base_already_has_with_a_helper_path()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var fixture = new ScreenshotFixture();

        var result = await fixture.CaptureAsync(ScreenshotTestData.CaptureCommand, overlay: ["page.png", "capture.cmd", "extra.png"]);

        Assert.True(result.Success, result.Message);
        Assert.Equal("page-base", ScreenshotTestData.Tag(result.Pairs.Single(static pair => pair.Name == "home").BeforePath));
        Assert.Equal("extra-feature", ScreenshotTestData.Tag(result.Pairs.Single(static pair => pair.Name == "extra").AfterPath));
        Assert.True(
            result.Pairs.Single(static pair => pair.Name == "extra").BeforePath is not null,
            "extra.png was overlaid into the base, so the base run now sees it");
        Assert.Contains(result.Notes, static note => note.Contains("Copied 1 helper file(s)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Capture_ignores_helper_paths_outside_the_worktree_and_inside_dot_git()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var fixture = new ScreenshotFixture();
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(fixture.Work)!, "outside.txt"), "outside");

        var result = await fixture.CaptureAsync(ScreenshotTestData.CaptureCommand, overlay: ["../outside.txt", ".git/config", fixture.Work, "  "]);

        Assert.True(result.Success, result.Message);
        Assert.Contains(result.Notes, static note => note.Contains("'../outside.txt': it is not inside the worktree", StringComparison.Ordinal));
        Assert.Contains(result.Notes, static note => note.Contains("'.git/config': .git is never copied", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Notes, static note => note.Contains("Copied", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Capture_keeps_only_real_pngs_within_the_size_depth_and_name_limits()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var fixture = new ScreenshotFixture();
        fixture.WritePng("fixtures/good.png", "good");
        fixture.WritePng("fixtures/UPPER.PNG", "upper");
        fixture.WritePng("fixtures/Nested/Shot One.png", "nested");
        fixture.WritePng("fixtures/a/b/c/ok.png", "three-deep");
        fixture.WritePng("fixtures/a/b/c/d/deep.png", "four-deep");
        fixture.WritePng("fixtures/x/y.png", "first-x-y");
        fixture.WritePng("fixtures/x__y.png", "second-x-y");
        fixture.Write("fixtures/bad.png", "this is not a png");
        fixture.Write("fixtures/notes.txt", "notes");
        using (var stream = new FileStream(Path.Combine(fixture.Work, "fixtures", "big.png"), FileMode.Create))
        {
            stream.Write(ScreenshotTestData.Png("big"));
            stream.SetLength(ScreenshotCapture.MaxImageBytes + 1);
        }

        var result = await fixture.CaptureAsync(ScreenshotTestData.CopyFixtureTree);

        Assert.True(result.Success, result.Message);
        Assert.Equal(
            ["Nested__Shot_One", "UPPER", "a__b__c__ok", "good", "x__y"],
            result.Pairs.Select(static pair => pair.Name).ToArray());
        Assert.All(result.Pairs, static pair => Assert.False(pair.HasBefore));
        Assert.Equal("first-x-y", ScreenshotTestData.Tag(result.Pairs.Single(static pair => pair.Name == "x__y").AfterPath));
        Assert.Contains(result.Notes, static note => note.Contains("bad.png", StringComparison.Ordinal) && note.Contains("not a PNG image", StringComparison.Ordinal));
        Assert.Contains(result.Notes, static note => note.Contains("big.png", StringComparison.Ordinal) && note.Contains("larger than 10 MiB", StringComparison.Ordinal));
        Assert.Contains(result.Notes, static note => note.Contains("deep.png", StringComparison.Ordinal) && note.Contains("directories deep", StringComparison.Ordinal));
        Assert.Contains(result.Notes, static note => note.Contains("x__y.png", StringComparison.Ordinal) && note.Contains("another image already maps", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Notes, static note => note.Contains("notes.txt", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Capture_keeps_at_most_forty_images_per_side()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var fixture = new ScreenshotFixture();
        for (var i = 0; i < ScreenshotCapture.MaxImagesPerSide + 5; i++)
        {
            fixture.WritePng($"fixtures/n{i:00}.png", "n" + i);
        }

        var result = await fixture.CaptureAsync(ScreenshotTestData.CopyFixtureTree);

        Assert.True(result.Success, result.Message);
        Assert.Equal(ScreenshotCapture.MaxImagesPerSide, result.Pairs.Count);
        Assert.Equal("n00", result.Pairs[0].Name);
        Assert.Equal("n39", result.Pairs[^1].Name);
        Assert.Contains(result.Notes, static note => note.Contains("Kept the first 40", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Capture_refuses_an_output_directory_that_is_a_link_and_leaves_its_target_alone()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var fixture = new ScreenshotFixture();
        fixture.WriteConfig(ScreenshotTestData.CaptureCommand, "linked");
        var target = Path.Combine(fixture.Markers, "outside-target");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "precious.txt"), "precious");
        var link = Path.Combine(fixture.Work, "linked");
        if (!ScreenshotTestData.TryCreateDirectoryLink(link, target))
        {
            return;
        }

        try
        {
            var result = await fixture.CaptureAsync(null);

            Assert.False(result.Success);
            Assert.Contains("'linked' is not usable", result.Message, StringComparison.Ordinal);
            Assert.Equal(["precious.txt"], ScreenshotTestData.FileNames(target));
            Assert.Equal("precious", File.ReadAllText(Path.Combine(target, "precious.txt")));
            fixture.AssertNoTrace();
        }
        finally
        {
            Directory.Delete(link);
        }
    }

    [Fact]
    public void DeleteCaptureDirectory_only_removes_directories_a_capture_created()
    {
        using var temp = new TempDirectory();
        var ours = temp.Combine("forge-screenshots-0123456789ab");
        var theirs = temp.Combine("important");
        Directory.CreateDirectory(Path.Combine(ours, "images"));
        File.WriteAllText(Path.Combine(ours, "images", "a.png"), "x");
        Directory.CreateDirectory(theirs);
        File.WriteAllText(Path.Combine(theirs, "keep.txt"), "keep");

        ScreenshotCapture.DeleteCaptureDirectory(theirs);
        ScreenshotCapture.DeleteCaptureDirectory(temp.Path);
        ScreenshotCapture.DeleteCaptureDirectory(null);
        ScreenshotCapture.DeleteCaptureDirectory("  ");
        ScreenshotCapture.DeleteCaptureDirectory(temp.Combine("forge-screenshots-missing"));

        Assert.True(File.Exists(Path.Combine(theirs, "keep.txt")));
        Assert.True(Directory.Exists(ours));

        ScreenshotCapture.DeleteCaptureDirectory(ours);

        Assert.False(Directory.Exists(ours));
        Assert.True(Directory.Exists(theirs));
    }

    [Fact]
    public async Task Capture_rejects_missing_arguments()
    {
        Assert.Throws<ArgumentNullException>(() => new ScreenshotCapture(null!));
        var capture = new ScreenshotCapture(new GitCli());

        await Assert.ThrowsAnyAsync<ArgumentException>(() => capture.CaptureAsync(" ", "origin/main", "x", CancellationToken.None));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => capture.CaptureAsync("C:/x", " ", "x", CancellationToken.None));
    }

    private static void WriteOneOffScript(ScreenshotFixture fixture)
    {
        if (OperatingSystem.IsWindows())
        {
            fixture.Write("tools/oneoff.cmd", "@echo off\r\ncopy /Y page.png \"%FORGE_SCREENSHOT_DIR%\\home.png\" >nul\r\n");
        }
        else
        {
            fixture.Write("tools/oneoff.sh", "cp page.png \"$FORGE_SCREENSHOT_DIR/home.png\"\n");
        }
    }
}
