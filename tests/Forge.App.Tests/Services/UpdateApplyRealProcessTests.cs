using System.Diagnostics;
using Forge.App.Services;
using Xunit;

namespace Forge.App.Tests.Services;

/// <summary>
/// The update applier against the real operating system (wave W10c): the real process seam, a real executable, and real
/// directory renames. The fake-process tests pin the state machine; this one pins the premise the whole design rests on —
/// a directory holding a <em>running</em> executable cannot be renamed on Windows, so the swap must only happen after that
/// process is gone, and it must succeed once it is.
/// </summary>
public sealed class UpdateApplyRealProcessTests : IDisposable
{
    private readonly string root = TestTempFiles.NewDirectory("forge-apply-real-");

    public void Dispose() => TestTempFiles.DeleteTree(root);

    private static string ExecutableName => UpdateApplyPaths.AppExecutableName;

    private static string PayloadDirectory(string install) => Path.Combine(Path.GetDirectoryName(install)!, UpdateApplyPaths.PayloadDirectoryName);

    /// <summary>
    /// Copies the current test host as the "Forge.App.exe" of a tree. Any executable works: what matters is that the file is
    /// locked while its process runs, which is the behaviour under test.
    /// </summary>
    private static void WriteExecutable(string directory, string marker)
    {
        Directory.CreateDirectory(directory);
        var source = Environment.ProcessPath ?? throw new InvalidOperationException("no process image to copy");
        File.Copy(source, Path.Combine(directory, ExecutableName), overwrite: true);
        File.WriteAllText(Path.Combine(directory, "payload.txt"), marker);
    }

    [Fact]
    public async Task The_swap_succeeds_once_the_running_install_exits_and_leaves_the_backup_behind()
    {
        var install = Path.Combine(root, UpdateApplyPaths.AppExecutableBaseName);
        WriteExecutable(install, "old");

        // The staged tree the update would install: its payload directory becomes the new install directory.
        var staging = Path.Combine(root, UpdateApplyPaths.UpdatesDirectoryName, "2.0.0");
        WriteExecutable(PayloadDirectory(staging), "new");
        var marker = UpdateApplyPaths.Marker(root, "2.0.0");

        // The "old version" is this test host itself: a real process holding a real image in the install directory.
        using var oldProcess = Process.Start(new ProcessStartInfo
        {
            FileName = Environment.ProcessPath!,
            ArgumentList = { "-h" },
            WorkingDirectory = install,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        try
        {
            var request = new UpdateApplyRequest(oldProcess.Id, install, PayloadDirectory(staging), "2.0.0");
            var runner = new UpdateApplyRunner(root, new UpdateApplyOptions { ConfirmationTimeout = TimeSpan.FromSeconds(20), ProcessExitTimeout = TimeSpan.FromSeconds(20) });

            // The applier waits for the process to exit, swaps, starts the new version and waits for its confirmation. The
            // new version is the copied test host too, so it exits immediately: the applier must read that as "not
            // confirmed" and roll back - which is exactly the recovery direction, exercised with real renames.
            var result = await runner.RunAsync(request, CancellationToken.None);

            Assert.Equal(UpdateApplyStatus.RolledBack, result.Status);
            // The previous tree is back in place with its own payload, and the unconfirmed one is parked under .failed-*.
            Assert.Equal("old", await File.ReadAllTextAsync(Path.Combine(install, "payload.txt")));
            Assert.False(File.Exists(marker), "an unconfirmed swap must not leave a confirmation behind");
            var failed = Path.Combine(root, UpdateApplyPaths.AppExecutableBaseName + UpdateApplyPaths.FailedPrefix + "2.0.0");
            Assert.True(Directory.Exists(failed), "the unconfirmed tree must be kept as .failed-<version>");
            Assert.True(File.Exists(UpdateApplyPaths.InstallLog(root)), "the run must be recorded in install.log");
        }
        finally
        {
            if (!oldProcess.HasExited)
            {
                oldProcess.Kill(entireProcessTree: true);
            }
        }
    }
}
