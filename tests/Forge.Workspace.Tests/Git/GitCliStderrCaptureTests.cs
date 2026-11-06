using System.Text;
using Forge.Workspace;
using Xunit;

namespace Forge.Workspace.Tests.Git;

/// <summary>
/// git writes to stderr while it works, so the captured stderr keeps its end: the last line is usually the one that says why
/// git refused (a hook's verdict, the rejecting rule), and a hook that prints more than the 64 KiB capture cap must not lose
/// it. Captured stdout keeps its beginning, and the per-line callbacks still see every line either way.
/// </summary>
public sealed class GitCliStderrCaptureTests
{
    private const int StdoutLines = 5000;
    private const int StderrLines = 10000;

    [Fact]
    public async Task Stderr_beyond_the_capture_cap_keeps_its_end_and_every_line_is_streamed()
    {
        using var fake = new ChattyGit();
        var lines = 0;

        var result = await new GitCli(fake.Executable).RunAsync(
            fake.WorkingDirectory,
            ["check"],
            new GitRunOptions { OnStandardErrorLine = _ => lines++ });

        Assert.False(result.Success);
        Assert.Equal(1, result.ExitCode);

        // The tail: the refusal, which a hook prints last.
        Assert.Contains("FINAL VERDICT: reject", result.StandardError, StringComparison.Ordinal);
        Assert.StartsWith("[… output truncated; showing the last", result.StandardError, StringComparison.Ordinal);
        Assert.DoesNotContain("err-1" + Environment.NewLine, result.StandardError, StringComparison.Ordinal);
        Assert.True(
            result.StandardError.Length <= (64 * 1024) + 100,
            $"captured {result.StandardError.Length} characters");

        // Every line reached the callback, including the truncated ones.
        Assert.Equal(StderrLines + 1, lines);

        // stdout is under its own cap and keeps its beginning, unchanged by any of this.
        Assert.StartsWith("out-1", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("out-" + StdoutLines.ToString(System.Globalization.CultureInfo.InvariantCulture), result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Stdout_keeps_its_beginning_when_the_cap_is_hit_while_stderr_keeps_its_end()
    {
        using var fake = new ChattyGit();

        var result = await new GitCli(fake.Executable) { MaxCapturedCharacters = 500 }.RunAsync(fake.WorkingDirectory, ["check"]);

        Assert.False(result.Success);
        Assert.StartsWith("out-1", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("[output truncated]", result.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("out-" + StdoutLines.ToString(System.Globalization.CultureInfo.InvariantCulture), result.StandardOutput, StringComparison.Ordinal);

        Assert.EndsWith("FINAL VERDICT: reject", result.StandardError.TrimEnd(), StringComparison.Ordinal);
        Assert.StartsWith("[… output truncated; showing the last", result.StandardError, StringComparison.Ordinal);
        Assert.True(result.StandardError.Length <= 600, $"captured {result.StandardError.Length} characters");
    }

    [Fact]
    public async Task Streaming_a_record_list_also_keeps_the_end_of_stderr()
    {
        using var fake = new ChattyGit();

        var records = new List<string>();
        await Assert.ThrowsAsync<GitException>(async () =>
        {
            await foreach (var record in new GitCli(fake.Executable) { MaxCapturedCharacters = 500 }
                .StreamRecordsAsync(fake.WorkingDirectory, ["check"], '\n'))
            {
                records.Add(record);
            }
        });

        Assert.NotEmpty(records);
    }

    [Fact]
    public async Task A_rejecting_hook_that_prints_more_than_the_capture_cap_still_shows_its_verdict()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var repo = new TestRepo();
        repo.Write("a.txt", "a\n");
        var baseCommit = repo.Commit("base");
        repo.Write("a.txt", "b\n");

        var hook = repo.FullPath(Path.Combine(".git", "hooks", "pre-commit"));
        Directory.CreateDirectory(Path.GetDirectoryName(hook)!);
        File.WriteAllText(
            hook,
            "#!/bin/sh\n" +
            "i=0\n" +
            "while [ $i -lt 1500 ]; do echo \"noise line $i padding padding padding padding padding\" >&2; i=$((i+1)); done\n" +
            "echo 'FINAL VERDICT: rejected' >&2\n" +
            "exit 1\n",
            new UTF8Encoding(false));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(hook, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        var failure = await Assert.ThrowsAsync<GitException>(() => new GitCli().CommitAsync(repo.Root, [], "feat: rejected"))
            .WaitAsync(TimeSpan.FromSeconds(60));

        Assert.Equal(1, failure.ExitCode);
        Assert.Contains("FINAL VERDICT: rejected", failure.StandardError, StringComparison.Ordinal);
        Assert.DoesNotContain("noise line 0 ", failure.StandardError, StringComparison.Ordinal);
        Assert.Equal(baseCommit, repo.Head());
    }
}

/// <summary>A stand-in for git that writes to both streams and then fails, like a rejecting pre-receive hook would.</summary>
internal sealed class ChattyGit : IDisposable
{
    private static readonly UTF8Encoding Utf8 = new(false);

    private readonly TempDirectory root = new();

    public ChattyGit()
    {
        var directory = root.Combine("fake");
        Directory.CreateDirectory(directory);
        WorkingDirectory = root.Combine("work");
        Directory.CreateDirectory(WorkingDirectory);
        Executable = WriteScript(directory);
    }

    public string Executable { get; }

    public string WorkingDirectory { get; }

    public void Dispose() => root.Dispose();

    private const int StdoutLines = 5000;
    private const int StderrLines = 10000;

    private static string WriteScript(string directory)
    {
        if (OperatingSystem.IsWindows())
        {
            var path = Path.Combine(directory, "git.cmd");
            var script =
                "@echo off\r\n" +
                "for /l %%i in (1,1," + StdoutLines.ToString(System.Globalization.CultureInfo.InvariantCulture) + ") do echo out-%%i\r\n" +
                "for /l %%i in (1,1," + StderrLines.ToString(System.Globalization.CultureInfo.InvariantCulture) + ") do echo err-%%i 1>&2\r\n" +
                "echo FINAL VERDICT: reject 1>&2\r\n" +
                "exit /b 1\r\n";
            File.WriteAllText(path, script, Encoding.ASCII);
            return path;
        }

        var shell = Path.Combine(directory, "git");
        var content = new StringBuilder();
        content.Append("#!/bin/sh\n");
        content.Append("i=1; while [ $i -le ").Append(StdoutLines.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(" ]; do echo \"out-$i\"; i=$((i+1)); done\n");
        content.Append("i=1; while [ $i -le ").Append(StderrLines.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(" ]; do echo \"err-$i\" >&2; i=$((i+1)); done\n");
        content.Append("echo \"FINAL VERDICT: reject\" >&2\n");
        content.Append("exit 1\n");
        File.WriteAllText(shell, content.ToString(), Utf8);
        File.SetUnixFileMode(
            shell,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute);
        return shell;
    }
}
