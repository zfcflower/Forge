using System.Diagnostics;
using Forge.App.Services;
using Xunit;

namespace Forge.App.Tests.Services;

/// <summary>
/// The state machine's edges that a single happy-path run cannot pin: the bounded process wait of the real seam, a stale
/// backup left by an earlier attempt, the decision order between the confirmation marker and the watchdog, the idempotency
/// of two recovery appliers hitting the same install directory, the recovery command line's bounds, and the startup
/// housekeeping's classification of mixed leftover directories and markers.
/// </summary>
public sealed class UpdateApplyBoundaryTests
{
    // ---------------------------------------------------------------- the real process seam

    [Fact]
    public async Task A_pid_that_does_not_exist_counts_as_already_exited()
    {
        var processes = new SystemUpdateApplyProcesses();

        // int.MaxValue is not a valid Windows process id, so GetProcessById must throw and the wait must resolve as "gone".
        Assert.True(await processes.WaitForExitAsync(int.MaxValue, TimeSpan.FromSeconds(1), CancellationToken.None));
    }

    [Fact]
    public async Task A_pid_that_is_still_running_times_out_instead_of_waiting_forever()
    {
        // A reused pid belongs to some other process that may never exit; the wait is bounded so the applier does not hang.
        var processes = new SystemUpdateApplyProcesses();
        var watch = Stopwatch.StartNew();

        Assert.False(await processes.WaitForExitAsync(Environment.ProcessId, TimeSpan.FromMilliseconds(150), CancellationToken.None));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), "the bounded wait must not hang");
    }

    // ---------------------------------------------------------------- stale backup before the swap

    [Fact]
    public async Task A_stale_backup_from_an_earlier_attempt_is_removed_before_the_swap()
    {
        using var sandbox = UpdateApplySandbox.Create();
        UpdateApplySandbox.WriteTree(sandbox.OldDirectory, "stale");
        sandbox.Processes.OnStart = (_, arguments) =>
        {
            if (arguments.Contains(UpdateApplyRequest.ConfirmedSwitch))
            {
                sandbox.Confirm();
            }
        };

        var result = await sandbox.Runner.RunAsync(sandbox.Request, CancellationToken.None);

        Assert.Equal(UpdateApplyStatus.Applied, result.Status);
        Assert.Equal("new", File.ReadAllText(sandbox.InstallMarker));
        Assert.Equal("old", File.ReadAllText(Path.Combine(sandbox.OldDirectory, "tree.txt")));
    }

    [Fact]
    public async Task A_stale_backup_that_cannot_be_removed_aborts_without_touching_the_install()
    {
        using var sandbox = UpdateApplySandbox.Create();
        UpdateApplySandbox.WriteTree(sandbox.OldDirectory, "stale");
        using (new FileStream(Path.Combine(sandbox.OldDirectory, "tree.txt"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var result = await sandbox.Runner.RunAsync(sandbox.Request, CancellationToken.None);

            Assert.Equal(UpdateApplyStatus.Aborted, result.Status);
            Assert.Contains("stale backup", result.Reason, StringComparison.Ordinal);
            Assert.Equal("old", File.ReadAllText(sandbox.InstallMarker));
            Assert.True(Directory.Exists(sandbox.OldDirectory));
            Assert.True(Directory.Exists(sandbox.Staging));
        }
    }

    // ---------------------------------------------------------------- marker vs watchdog decision order

    [Fact]
    public async Task A_confirmation_written_at_the_deadline_still_wins_because_the_marker_is_checked_first()
    {
        // A zero confirmation timeout means the deadline is already past on the first poll; the marker is checked before
        // the timeout, so a version that confirmed still stays, rather than being rolled back by the boundary.
        using var sandbox = UpdateApplySandbox.Create(confirmationTimeout: TimeSpan.Zero);
        sandbox.Processes.OnStart = (_, arguments) =>
        {
            if (arguments.Contains(UpdateApplyRequest.ConfirmedSwitch))
            {
                sandbox.Confirm();
            }
        };

        var result = await sandbox.Runner.RunAsync(sandbox.Request, CancellationToken.None);

        Assert.Equal(UpdateApplyStatus.Applied, result.Status);
        Assert.Equal("new", File.ReadAllText(sandbox.InstallMarker));
    }

    [Fact]
    public async Task A_confirmation_takes_precedence_over_an_early_exit()
    {
        // The loop checks the marker before the process exit, so a new version that confirms and then crashes is kept.
        using var sandbox = UpdateApplySandbox.Create();
        sandbox.Processes.OnStart = (process, arguments) =>
        {
            if (arguments.Contains(UpdateApplyRequest.ConfirmedSwitch))
            {
                sandbox.Confirm();
                process.Exit(7);
            }
        };

        var result = await sandbox.Runner.RunAsync(sandbox.Request, CancellationToken.None);

        Assert.Equal(UpdateApplyStatus.Applied, result.Status);
        Assert.Equal("new", File.ReadAllText(sandbox.InstallMarker));
    }

    // ---------------------------------------------------------------- two recovery appliers

    [Fact]
    public async Task A_second_recovery_applier_does_not_undo_the_first_restoration()
    {
        // Two appliers can race on one interrupted swap; the slower one must not undo the faster one's work. The recovery
        // direction checks the backup before it moves anything, so once the first restore consumed the backup the second
        // finds nothing to restore and leaves the restored install in place.
        using var sandbox = UpdateApplySandbox.Create(confirmationTimeout: TimeSpan.FromMilliseconds(50));
        UpdateApplySandbox.WriteTree(sandbox.OldDirectory, "previous");
        sandbox.UseStaging(sandbox.OldDirectory);
        sandbox.UseRecovery();

        var first = await sandbox.Runner.RunAsync(sandbox.Request, CancellationToken.None);
        var second = await sandbox.Runner.RunAsync(sandbox.Request, CancellationToken.None);

        Assert.Equal(UpdateApplyStatus.RolledBack, first.Status);
        Assert.Equal(UpdateApplyStatus.Failed, second.Status);
        Assert.Contains("no backup", second.Reason, StringComparison.Ordinal);
        Assert.Equal("previous", File.ReadAllText(sandbox.InstallMarker));
        Assert.Equal("old", File.ReadAllText(Path.Combine(sandbox.FailedDirectory, "tree.txt")));
        Assert.False(Directory.Exists(sandbox.OldDirectory));
    }

    // ---------------------------------------------------------------- recovery command-line bounds

    [Fact]
    public void A_recovery_whose_backup_lost_its_executable_is_refused()
    {
        using var sandbox = UpdateApplySandbox.Create();
        UpdateApplySandbox.WriteTree(sandbox.OldDirectory, "previous");
        File.Delete(UpdateApplyPaths.AppExecutable(sandbox.OldDirectory));
        var args = Replace(
            [.. sandbox.Request.ToArguments(), UpdateApplyRequest.RecoverSwitch],
            UpdateApplyRequest.StagingSwitch,
            sandbox.OldDirectory);

        Assert.False(UpdateApplyRequest.TryParse(args, sandbox.DataRoot, out _, out var error));
        Assert.Contains("does not hold", error, StringComparison.Ordinal);
    }

    [Fact]
    public void A_recovery_source_that_is_not_a_directory_is_refused()
    {
        using var sandbox = UpdateApplySandbox.Create();
        File.WriteAllText(sandbox.OldDirectory, "a file pretending to be the backup");
        var args = Replace(
            [.. sandbox.Request.ToArguments(), UpdateApplyRequest.RecoverSwitch],
            UpdateApplyRequest.StagingSwitch,
            sandbox.OldDirectory);

        Assert.False(UpdateApplyRequest.TryParse(args, sandbox.DataRoot, out _, out var error));
        Assert.Contains("does not exist", error, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- a claim that never completed

    [Fact]
    public void A_claim_left_by_a_crashed_applier_is_put_back_under_its_old_name_and_used_for_the_repair()
    {
        // The applier crashed between claiming the backup and moving it into place: the only good copy of the previous
        // version sits under .restoring-<version>-<guid> and the .old name the repair looks for is gone. Without the version
        // in the claim name nothing could tell which backup the orphan holds, and the install would never get the previous
        // version back.
        var root = TestTempFiles.NewDirectory("forge-claim-orphan-");
        try
        {
            var dataRoot = Path.Combine(root, "data");
            var install = Path.Combine(root, "install");
            UpdateApplySandbox.WriteTree(install, "unconfirmed");
            var claimed = Path.Combine(
                Path.GetDirectoryName(install)!,
                UpdateApplyPaths.RestoringName(install, "1.2.0", Guid.NewGuid().ToString("N")));
            UpdateApplySandbox.WriteTree(claimed, "previous");
            var backup = UpdateApplyPaths.OldDirectory(install, "1.2.0");
            Assert.False(Directory.Exists(backup));

            Assert.True(UpdateApplyStartup.TryComposeRepair(dataRoot, install, "1.2.0", 777, out var request, out var reason), reason);
            Assert.NotNull(request);
            Assert.True(request!.Recovery);
            Assert.Equal(backup, request.StagingDirectory);
            Assert.False(Directory.Exists(claimed));
            Assert.Equal("previous", File.ReadAllText(Path.Combine(backup, "tree.txt")));

            // The request the repair composes out of a reclaimed backup is one the applier accepts.
            Assert.True(UpdateApplyRequest.TryParse(request.ToArguments(), dataRoot, out _, out var parseError), parseError);
        }
        finally
        {
            TestTempFiles.DeleteTree(root);
        }
    }

    [Fact]
    public void A_claim_of_another_version_is_not_used_for_this_versions_repair()
    {
        var root = TestTempFiles.NewDirectory("forge-claim-orphan-");
        try
        {
            var dataRoot = Path.Combine(root, "data");
            var install = Path.Combine(root, "install");
            UpdateApplySandbox.WriteTree(install, "unconfirmed");
            var foreign = Path.Combine(
                Path.GetDirectoryName(install)!,
                UpdateApplyPaths.RestoringName(install, "9.9.9", Guid.NewGuid().ToString("N")));
            UpdateApplySandbox.WriteTree(foreign, "someone else");

            Assert.False(UpdateApplyStartup.TryComposeRepair(dataRoot, install, "1.2.0", 777, out _, out var reason));
            Assert.Null(reason);
            Assert.True(Directory.Exists(foreign));
        }
        finally
        {
            TestTempFiles.DeleteTree(root);
        }
    }

    [Fact]
    public async Task A_recovery_from_a_reclaimed_backup_restores_the_previous_version()
    {
        // The claim of a crashed restore is reclaimed and the whole rollback then runs against it: the same directories, the
        // same outcome as a repair that never lost its backup.
        using var sandbox = UpdateApplySandbox.Create(confirmationTimeout: TimeSpan.FromMilliseconds(50));
        var claimed = Path.Combine(
            Path.GetDirectoryName(sandbox.Install)!,
            UpdateApplyPaths.RestoringName(sandbox.Install, UpdateApplySandbox.Version, Guid.NewGuid().ToString("N")));
        UpdateApplySandbox.WriteTree(claimed, "previous");

        Assert.True(
            UpdateApplyStartup.TryComposeRepair(
                sandbox.DataRoot,
                sandbox.Install,
                UpdateApplySandbox.Version,
                sandbox.Request.OldProcessId,
                out var request,
                out var reason),
            reason);

        var result = await sandbox.Runner.RunAsync(request!, CancellationToken.None);

        Assert.Equal(UpdateApplyStatus.RolledBack, result.Status);
        Assert.Equal("previous", File.ReadAllText(sandbox.InstallMarker));
        Assert.Equal("old", File.ReadAllText(Path.Combine(sandbox.FailedDirectory, "tree.txt")));
        Assert.False(Directory.Exists(UpdateApplyPaths.OldDirectory(sandbox.Install, UpdateApplySandbox.Version)));
    }

    // ---------------------------------------------------------------- the contradictory half-swap state

    [Fact]
    public void A_half_swap_with_a_confirmation_marker_is_treated_as_confirmed_not_repaired()
    {
        var root = TestTempFiles.NewDirectory("forge-contradict-");
        try
        {
            var dataRoot = Path.Combine(root, "data");
            var install = Path.Combine(root, "install");
            UpdateApplySandbox.WriteTree(install, "unconfirmed");
            var old = UpdateApplyPaths.OldDirectory(install, "1.2.0");
            UpdateApplySandbox.WriteTree(old, "previous");
            Directory.CreateDirectory(UpdateApplyPaths.UpdatesRoot(dataRoot));
            File.WriteAllText(UpdateApplyPaths.Marker(dataRoot, "1.2.0"), "confirmed");

            // The ok marker wins over the half-swap evidence: this is a confirmed swap, so no repair is composed.
            Assert.False(UpdateApplyStartup.TryComposeRepair(dataRoot, install, "1.2.0", 777, out _, out _));

            // And the housekeeping finishes it as a confirmed install: the backup and the marker both go away.
            var maintenance = UpdateApplyStartup.Maintain(dataRoot, install);
            Assert.Contains(old, maintenance.Removed);
            Assert.Empty(maintenance.LeftBehind);
            Assert.False(Directory.Exists(old));
            Assert.False(File.Exists(UpdateApplyPaths.Marker(dataRoot, "1.2.0")));
        }
        finally
        {
            TestTempFiles.DeleteTree(root);
        }
    }

    // ---------------------------------------------------------------- mixed startup housekeeping

    [Fact]
    public void Maintenance_removes_each_confirmed_backup_and_failed_tree_but_leaves_each_unconfirmed_backup()
    {
        var root = TestTempFiles.NewDirectory("forge-maintain-mixed-");
        try
        {
            var dataRoot = Path.Combine(root, "data");
            var install = Path.Combine(root, "install");
            UpdateApplySandbox.WriteTree(install, "current");
            var confirmedOld = UpdateApplyPaths.OldDirectory(install, "1.1.0");
            var unconfirmedOld = UpdateApplyPaths.OldDirectory(install, "1.2.0");
            var failedOne = UpdateApplyPaths.FailedDirectory(install, "1.3.0");
            var failedTwo = UpdateApplyPaths.FailedDirectory(install, "1.4.0");
            UpdateApplySandbox.WriteTree(confirmedOld, "confirmed-old");
            UpdateApplySandbox.WriteTree(unconfirmedOld, "unconfirmed-old");
            UpdateApplySandbox.WriteTree(failedOne, "failed-one");
            UpdateApplySandbox.WriteTree(failedTwo, "failed-two");
            Directory.CreateDirectory(UpdateApplyPaths.UpdatesRoot(dataRoot));
            File.WriteAllText(UpdateApplyPaths.Marker(dataRoot, "1.1.0"), "confirmed");
            File.WriteAllText(UpdateApplyPaths.Marker(dataRoot, "9.9.9"), "orphan");

            var maintenance = UpdateApplyStartup.Maintain(dataRoot, install);

            Assert.Contains(confirmedOld, maintenance.Removed);
            Assert.Contains(failedOne, maintenance.Removed);
            Assert.Contains(failedTwo, maintenance.Removed);
            Assert.Contains(UpdateApplyPaths.Marker(dataRoot, "1.1.0"), maintenance.Removed);
            Assert.Contains(UpdateApplyPaths.Marker(dataRoot, "9.9.9"), maintenance.Removed);
            Assert.Contains(unconfirmedOld, maintenance.LeftBehind);
            Assert.False(Directory.Exists(confirmedOld));
            Assert.False(Directory.Exists(failedOne));
            Assert.False(Directory.Exists(failedTwo));
            Assert.True(Directory.Exists(unconfirmedOld));
            Assert.False(File.Exists(UpdateApplyPaths.Marker(dataRoot, "1.1.0")));
            Assert.False(File.Exists(UpdateApplyPaths.Marker(dataRoot, "9.9.9")));
        }
        finally
        {
            TestTempFiles.DeleteTree(root);
        }
    }

    private static string[] Replace(IEnumerable<string> arguments, string @switch, string value)
    {
        var copy = arguments.ToArray();
        var index = Array.IndexOf(copy, @switch);
        Assert.True(index >= 0, "the switch '" + @switch + "' is not on the command line");
        copy[index + 1] = value;
        return copy;
    }
}
