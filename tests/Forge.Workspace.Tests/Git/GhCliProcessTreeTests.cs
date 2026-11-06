using System.Text;
using Forge.Workspace;
using Xunit;

namespace Forge.Workspace.Tests.Git;

/// <summary>
/// A gh that hung must not leave anything of itself behind: a timeout or a cancellation kills the whole process tree, not
/// just the process <see cref="GhCli"/> started. The stand-in starts a child that writes a marker file after a few seconds,
/// so a surviving child would leave a trace.
/// </summary>
public sealed class GhCliProcessTreeTests
{
    private static readonly TimeSpan ChildDelayRoom = TimeSpan.FromSeconds(6);

    [Fact]
    public async Task A_timed_out_gh_takes_its_child_processes_down_with_it()
    {
        using var fake = new TreeFakeGh();

        var result = await fake.RunAsync(new GhRunOptions { Timeout = TimeSpan.FromMilliseconds(500) });

        Assert.True(result.TimedOut);
        Assert.False(result.Success);

        await Task.Delay(ChildDelayRoom);
        Assert.False(File.Exists(fake.ChildMarker), "the child of gh survived the timeout");
    }

    [Fact]
    public async Task A_cancelled_gh_takes_its_child_processes_down_with_it()
    {
        using var fake = new TreeFakeGh();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fake.RunAsync(null, cancellation.Token));

        await Task.Delay(ChildDelayRoom);
        Assert.False(File.Exists(fake.ChildMarker), "the child of gh survived the cancellation");
    }
}

/// <summary>A stand-in for gh that starts a child which writes <c>child.alive</c> after a few seconds, then hangs.</summary>
internal sealed class TreeFakeGh : IDisposable
{
    private static readonly UTF8Encoding Utf8 = new(false);

    private readonly TempDirectory root = new();

    public TreeFakeGh()
    {
        var directory = root.Combine("fake");
        Directory.CreateDirectory(directory);
        WorkingDirectory = root.Combine("work");
        Directory.CreateDirectory(WorkingDirectory);
        ChildMarker = Path.Combine(directory, "child.alive");
        Executable = WriteScript(directory);
    }

    public string Executable { get; }

    public string WorkingDirectory { get; }

    /// <summary>The file the child writes when it is still alive a few seconds in.</summary>
    public string ChildMarker { get; }

    public Task<GhCommandResult> RunAsync(GhRunOptions? options, CancellationToken ct = default) =>
        new GhCli(Executable).RunAsync(WorkingDirectory, ["pr", "view"], options, ct);

    public void Dispose() => root.Dispose();

    private static string WriteScript(string directory)
    {
        if (OperatingSystem.IsWindows())
        {
            // The child is a second cmd.exe: only a tree kill reaches it.
            File.WriteAllText(
                Path.Combine(directory, "child.cmd"),
                "@echo off\r\nping -n 4 127.0.0.1 >nul\r\necho alive> \"%~dp0child.alive\"\r\n",
                Encoding.ASCII);

            var path = Path.Combine(directory, "gh.cmd");
            const string script = """
                @echo off
                set "DIR=%~dp0"
                start "" /b /wait cmd /c "%DIR%child.cmd"
                ping -n 30 127.0.0.1 >nul
                exit /b 0
                """;
            File.WriteAllText(path, script.ReplaceLineEndings("\r\n") + "\r\n", Encoding.ASCII);
            return path;
        }

        var shell = Path.Combine(directory, "gh");
        const string content = """
            #!/bin/sh
            dir=$(dirname "$0")
            ( sleep 3; echo alive > "$dir/child.alive" ) &
            sleep 30
            exit 0
            """;
        File.WriteAllText(shell, content.ReplaceLineEndings("\n") + "\n", Utf8);
        File.SetUnixFileMode(
            shell,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute);
        return shell;
    }
}
