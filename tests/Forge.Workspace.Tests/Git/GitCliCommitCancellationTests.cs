using System.Text;
using Xunit;

namespace Forge.Workspace.Tests.Git;

/// <summary>
/// <see cref="GitCli.CommitAsync"/> after git has created the commit, and the way the message reaches git. A commit that
/// exists must never be reported as cancelled (its id would be lost while HEAD has moved), and the message goes over
/// stdin so a long body is not bound by the operating system's command line limit.
/// The cancellation tests drive a stand-in for the git executable (a .cmd on Windows, a shell script elsewhere) that
/// forwards every call to the real git and, once <c>git commit</c> has succeeded, can slow the read-back down or leave a
/// stray child holding git's output pipe, so the cancellation lands at a known point instead of by luck.
/// </summary>
public sealed class GitCliCommitCancellationTests
{
    private const string FileName = "a.txt";

    [Fact]
    public async Task A_cancellation_while_the_new_commit_is_read_back_still_returns_the_commit()
    {
        if (!GitProbe.IsAvailable) return;
        using var repo = new TestRepo();
        repo.Write(FileName, "a\n");
        var baseCommit = repo.Commit("base");
        repo.Write(FileName, "b\n");
        using var probe = new CommitProbeGit(slowReadBack: true);
        var cli = new GitCli(probe.Executable);
        using var cancellation = new CancellationTokenSource();

        var commitTask = cli.CommitAsync(repo.Root, [], "the step", cancellation.Token);
        await WaitForFileAsync(probe.ReadingFlag);

        // git commit has succeeded and the read-back (git log) has started but is held back: the commit exists, HEAD moved.
        Assert.NotEqual(baseCommit, repo.Head());
        cancellation.Cancel();
        var commit = await commitTask.WaitAsync(TimeSpan.FromSeconds(60));

        Assert.Equal(repo.Head(), commit.Id);
        Assert.Equal("the step", commit.Subject);
    }

    [Fact]
    public async Task A_cancellation_after_git_commit_exited_but_before_the_call_returns_still_returns_the_commit()
    {
        if (!GitProbe.IsAvailable) return;
        using var repo = new TestRepo();
        repo.Write(FileName, "a\n");
        var baseCommit = repo.Commit("base");
        repo.Write(FileName, "b\n");
        using var probe = new CommitProbeGit(holdPipeAfterCommit: true);
        var cli = new GitCli(probe.Executable);
        using var cancellation = new CancellationTokenSource();

        var commitTask = cli.CommitAsync(repo.Root, [], "the step", cancellation.Token);
        await WaitForFileAsync(probe.HoldingFlag);

        // The stand-in has exited with code 0 by now; a stray child it left behind still holds the output pipe open, so the
        // call is still waiting for the pipe when the cancellation arrives.
        await Task.Delay(400);
        Assert.NotEqual(baseCommit, repo.Head());
        cancellation.Cancel();
        var commit = await commitTask.WaitAsync(TimeSpan.FromSeconds(60));

        Assert.Equal(repo.Head(), commit.Id);
        Assert.Equal("the step", commit.Subject);
    }

    [Fact]
    public async Task A_token_that_is_cancelled_before_the_call_still_cancels_it_and_creates_no_commit()
    {
        if (!GitProbe.IsAvailable) return;
        using var repo = new TestRepo();
        repo.Write(FileName, "a\n");
        var baseCommit = repo.Commit("base");
        repo.Write(FileName, "b\n");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new GitCli().CommitAsync(repo.Root, [], "never", cancellation.Token));

        Assert.Equal(baseCommit, repo.Head());
    }

    [Fact]
    public async Task A_hundred_kilobyte_message_is_committed_unchanged()
    {
        if (!GitProbe.IsAvailable) return;
        using var repo = new TestRepo();
        repo.Write(FileName, "a\n");
        repo.Commit("base");
        repo.Write(FileName, "b\n");
        var message = LongMessage("feat(core): a very long step summary", 100 * 1024);
        Assert.True(message.Length >= 100 * 1024);
        Assert.True(Encoding.UTF8.GetByteCount(message) > 100 * 1024);

        var commit = await new GitCli().CommitAsync(repo.Root, [], message);

        Assert.Equal("feat(core): a very long step summary", commit.Subject);
        Assert.Equal(message, repo.RunGit("log", "-1", "--format=%B").TrimEnd('\n'));
        Assert.Empty(await new GitCli().GetStatusAsync(repo.Root));
    }

    [Fact]
    public async Task The_message_reaches_git_exactly_whatever_characters_it_holds()
    {
        if (!GitProbe.IsAvailable) return;
        using var repo = new TestRepo();
        repo.Write(FileName, "a\n");
        repo.Commit("base");
        repo.Write(FileName, "b\n");
        const string message = "fix: it's \"quoted\" & $HOME %PATH% `ticks` 汉字 \U0001F600\n\n# not a comment\n- bullet\n  indented line\n\nlast line";

        var commit = await new GitCli().CommitAsync(repo.Root, [], message);

        Assert.Equal("fix: it's \"quoted\" & $HOME %PATH% `ticks` 汉字 \U0001F600", commit.Subject);
        Assert.Equal(message, repo.RunGit("log", "-1", "--format=%B").TrimEnd('\n'));
        Assert.DoesNotContain('﻿', repo.RunGit("log", "-1", "--format=%B"));
    }

    [Fact]
    public async Task A_commit_git_refuses_before_reading_a_long_message_fails_promptly_with_the_git_error()
    {
        if (!GitProbe.IsAvailable) return;
        using var repo = new TestRepo();
        repo.Write(FileName, "a\n");
        var baseCommit = repo.Commit("base");
        var message = LongMessage("chore: nothing to commit", 100 * 1024);

        // Nothing is staged, so git exits without reading stdin: the broken pipe must not hang or mask the git failure.
        var failure = await Assert.ThrowsAsync<GitException>(() => new GitCli().CommitAsync(repo.Root, [], message))
            .WaitAsync(TimeSpan.FromSeconds(60));

        Assert.Equal(1, failure.ExitCode);
        Assert.True(failure.Message.Length < 1000, "the failure message must not echo the commit message");
        Assert.Equal(baseCommit, repo.Head());
    }

    [Fact]
    public async Task A_rejecting_hook_fails_the_commit_and_surfaces_its_output_without_reading_the_message()
    {
        if (!GitProbe.IsAvailable) return;
        using var repo = new TestRepo();
        repo.Write(FileName, "a\n");
        var baseCommit = repo.Commit("base");
        repo.Write(FileName, "b\n");
        var hook = repo.FullPath(".git/hooks/pre-commit");
        Directory.CreateDirectory(Path.GetDirectoryName(hook)!);
        File.WriteAllText(hook, "#!/bin/sh\necho 'hook says no' >&2\nexit 1\n", new UTF8Encoding(false));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(hook, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        var failure = await Assert.ThrowsAsync<GitException>(
                () => new GitCli().CommitAsync(repo.Root, [], LongMessage("feat: rejected", 100 * 1024)))
            .WaitAsync(TimeSpan.FromSeconds(60));

        Assert.Equal(1, failure.ExitCode);
        Assert.Contains("hook says no", failure.StandardError, StringComparison.Ordinal);
        Assert.Equal(baseCommit, repo.Head());
    }

    /// <summary>A subject, a blank line, then paragraphs of non-ASCII lines until the message is at least <paramref name="minimumLength"/> characters.</summary>
    private static string LongMessage(string subject, int minimumLength)
    {
        var builder = new StringBuilder(subject).Append("\n\n");
        var line = 0;
        while (builder.Length < minimumLength)
        {
            builder.Append("line ").Append(line++).Append(": café 汉字 the quick brown fox jumps over the lazy dog");
            builder.Append(line % 8 == 0 ? "\n\n" : "\n");
        }

        return builder.ToString().TrimEnd('\n');
    }

    private static async Task WaitForFileAsync(string path)
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (!File.Exists(path))
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"'{path}' never appeared.");
            }

            await Task.Delay(5);
        }
    }

    /// <summary>
    /// Forwards every call to the real git. After a successful <c>commit</c> it records <c>committed.flag</c>; from then on a
    /// <c>log</c> call (the read-back) records <c>reading.flag</c> and, when asked, waits a couple of seconds before it runs,
    /// and with <paramref name="holdPipeAfterCommit"/> the stand-in leaves a short-lived child behind that keeps git's
    /// output pipe open after the stand-in itself has exited, recording <c>holding.flag</c>.
    /// </summary>
    private sealed class CommitProbeGit : IDisposable
    {
        private readonly TempDirectory root = new();

        public CommitProbeGit(bool slowReadBack = false, bool holdPipeAfterCommit = false)
        {
            Directory = root.Combine("fake");
            System.IO.Directory.CreateDirectory(Directory);
            if (slowReadBack)
            {
                File.WriteAllText(Path.Combine(Directory, "slow-log.flag"), "x");
            }

            if (holdPipeAfterCommit)
            {
                File.WriteAllText(Path.Combine(Directory, "hold.flag"), "x");
            }

            Executable = WriteScript(RealGit());
        }

        public string Directory { get; }

        public string Executable { get; }

        public string ReadingFlag => Path.Combine(Directory, "reading.flag");

        public string HoldingFlag => Path.Combine(Directory, "holding.flag");

        public void Dispose() => root.Dispose();

        private static string RealGit()
        {
            var name = OperatingSystem.IsWindows() ? "git.exe" : "git";
            foreach (var entry in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                var candidate = Path.Combine(entry.Trim('"'), name);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            throw new InvalidOperationException("git was not found on PATH.");
        }

        private string WriteScript(string realGit)
        {
            if (OperatingSystem.IsWindows())
            {
                var path = Path.Combine(Directory, "git.cmd");
                const string script = """
                    @echo off
                    set "DIR=%~dp0"
                    set "SUB="
                    :scan
                    if "%~1"=="" goto scanned
                    if not defined SUB if /i "%~1"=="commit" set "SUB=commit"
                    if not defined SUB if /i "%~1"=="log" set "SUB=log"
                    shift
                    goto scan
                    :scanned
                    if "%SUB%"=="log" if exist "%DIR%committed.flag" (
                      echo x>"%DIR%reading.flag"
                      if exist "%DIR%slow-log.flag" ping -n 3 127.0.0.1 >nul
                    )
                    "{GIT}" %*
                    set "CODE=%ERRORLEVEL%"
                    if "%SUB%"=="commit" if "%CODE%"=="0" (
                      echo x>"%DIR%committed.flag"
                      if exist "%DIR%hold.flag" (
                        echo x>"%DIR%holding.flag"
                        start "" /b /d "%SystemRoot%" ping -n 7 127.0.0.1
                      )
                    )
                    exit /b %CODE%
                    """;
                File.WriteAllText(path, script.Replace("{GIT}", realGit, StringComparison.Ordinal).ReplaceLineEndings("\r\n") + "\r\n", Encoding.ASCII);
                return path;
            }
            else
            {
                var path = Path.Combine(Directory, "git");
                const string script = """
                    #!/bin/sh
                    dir=$(dirname "$0")
                    sub=""
                    skip=0
                    for argument in "$@"; do
                      if [ "$skip" = 1 ]; then skip=0; continue; fi
                      if [ "$argument" = "-c" ]; then skip=1; continue; fi
                      sub="$argument"
                      break
                    done
                    if [ "$sub" = "log" ] && [ -f "$dir/committed.flag" ]; then
                      : > "$dir/reading.flag"
                      if [ -f "$dir/slow-log.flag" ]; then sleep 2; fi
                    fi
                    "{GIT}" "$@"
                    code=$?
                    if [ "$sub" = "commit" ] && [ "$code" = 0 ]; then
                      : > "$dir/committed.flag"
                      if [ -f "$dir/hold.flag" ]; then
                        : > "$dir/holding.flag"
                        (cd / && sleep 6 &)
                      fi
                    fi
                    exit "$code"
                    """;
                File.WriteAllText(path, script.Replace("{GIT}", realGit, StringComparison.Ordinal).ReplaceLineEndings("\n") + "\n", new UTF8Encoding(false));
                File.SetUnixFileMode(
                    path,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute);
                return path;
            }
        }
    }
}
