using System.Runtime.InteropServices;
using Forge.IO;
using Xunit;

namespace Forge.App.Tests;

/// <summary>
/// How the runner reports that a child ended: its own exit code, the timeout classification, and — on the platforms that
/// have them — the signal. The signal matters because a signal death otherwise arrives as nothing but a number (SIGKILL
/// is 137), so an externally killed process and one that chose 137 are indistinguishable in a tool result.
/// </summary>
public sealed class ProcessRunnerTests : IDisposable
{
    private readonly string workspace = Directory.CreateTempSubdirectory("forge-process-runner-").FullName;

    public void Dispose() => Directory.Delete(workspace, recursive: true);

    private ProcessRequest Command(string fileName, params string[] arguments) => new()
    {
        FileName = fileName,
        Arguments = arguments,
        WorkingDirectory = workspace,
        Timeout = TimeSpan.FromSeconds(30),
    };

    private static Task<ProcessResult> RunAsync(ProcessRequest request) =>
        new ProcessRunner().ExecuteAsync(request, CancellationToken.None);

    [Fact]
    public async Task TheChildsOwnExitCodeIsReportedWithoutASignal()
    {
        var result = await RunAsync(OperatingSystem.IsWindows()
            ? Command("cmd.exe", "/c", "exit 7")
            : Command("/bin/sh", "-c", "exit 7"));

        Assert.Equal(7, result.ExitCode);
        Assert.Equal(ProcessEndReason.Exited, result.EndReason);
        Assert.Null(result.Signal);
    }

    [Fact]
    public async Task ATimeoutIsStillClassifiedAsTimedOutAndNamesTheKillOnPosix()
    {
        var command = OperatingSystem.IsWindows()
            ? Command("cmd.exe", "/c", "ping -n 6 127.0.0.1")
            : Command("/bin/sh", "-c", "sleep 5");

        var result = await RunAsync(command with { Timeout = TimeSpan.FromMilliseconds(500) });

        Assert.Equal(ProcessEndReason.TimedOut, result.EndReason);

        // The tree kill is SIGKILL on POSIX, which is exactly the case an exit code alone reports as 137. Windows has no
        // signals at all, so the same kill there must leave the signal empty rather than invent one.
        Assert.Equal(OperatingSystem.IsWindows() ? null : PosixSignal.SIGKILL, result.Signal);
    }
}
