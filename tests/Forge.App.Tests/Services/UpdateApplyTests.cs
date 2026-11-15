using Forge.App.Services;
using Xunit;

namespace Forge.App.Tests.Services;

/// <summary>
/// Wave W10c's install state machine, pinned with fake directories and the scripted process seam: the command line is
/// validated before anything is touched, the running instance is waited for, the staged tree replaces the install directory,
/// and every failure path (no exit, no confirmation, an early exit, a start that never happens, a staged tree that cannot be
/// installed) ends on the previous version with the reason in <c>install.log</c> — including the half-swap repair the startup
/// runs before the UI exists.
/// </summary>
public sealed class UpdateApplyTests
{
    // ---------------------------------------------------------------- the command line

    [Fact]
    public void The_command_line_round_trips_through_the_parser()
    {
        using var sandbox = UpdateApplySandbox.Create();

        Assert.True(
            UpdateApplyRequest.TryParse(sandbox.Request.ToArguments(), sandbox.DataRoot, out var parsed, out var error),
            error);

        Assert.NotNull(parsed);
        Assert.Equal(sandbox.Request.OldProcessId, parsed!.OldProcessId);
        Assert.Equal(sandbox.Request.InstallDirectory, parsed.InstallDirectory);
        Assert.Equal(sandbox.Request.StagingDirectory, parsed.StagingDirectory);
        Assert.Equal(sandbox.Request.Version, parsed.Version);
        Assert.False(parsed.Recovery);
        Assert.Equal(sandbox.OldDirectory, parsed.OldDirectory);
    }

    [Fact]
    public void The_apply_behaviour_is_recognised_on_any_position_of_the_command_line()
    {
        Assert.True(UpdateApplyRequest.IsApplyCommand([UpdateApplyRequest.ApplySwitch]));
        Assert.True(UpdateApplyRequest.IsApplyCommand(["Forge.App.exe", UpdateApplyRequest.ApplySwitch, "--pid", "1"]));
        Assert.False(UpdateApplyRequest.IsApplyCommand(["Forge.App.exe"]));
        Assert.False(UpdateApplyRequest.IsApplyCommand([]));
    }

    [Fact]
    public void A_staging_directory_outside_the_updates_root_is_refused()
    {
        using var sandbox = UpdateApplySandbox.Create();
        var args = Replace(sandbox.Request.ToArguments(), UpdateApplyRequest.StagingSwitch, sandbox.Install);

        Assert.False(UpdateApplyRequest.TryParse(args, sandbox.DataRoot, out _, out var error));
        Assert.Contains("is not inside", error, StringComparison.Ordinal);
    }

    [Fact]
    public void The_updates_root_itself_is_not_a_staging_directory()
    {
        using var sandbox = UpdateApplySandbox.Create();
        var args = Replace(sandbox.Request.ToArguments(), UpdateApplyRequest.StagingSwitch, UpdateApplyPaths.UpdatesRoot(sandbox.DataRoot));

        Assert.False(UpdateApplyRequest.TryParse(args, sandbox.DataRoot, out _, out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void A_relative_path_is_refused()
    {
        using var sandbox = UpdateApplySandbox.Create();
        var args = Replace(sandbox.Request.ToArguments(), UpdateApplyRequest.InstallSwitch, "install");

        Assert.False(UpdateApplyRequest.TryParse(args, sandbox.DataRoot, out _, out var error));
        Assert.Contains("absolute", error, StringComparison.Ordinal);
    }

    [Fact]
    public void A_version_that_is_not_a_file_name_is_refused()
    {
        using var sandbox = UpdateApplySandbox.Create();
        var args = Replace(sandbox.Request.ToArguments(), UpdateApplyRequest.VersionSwitch, "../../1.2.0");

        Assert.False(UpdateApplyRequest.TryParse(args, sandbox.DataRoot, out _, out var error));
        Assert.Contains("not a usable file-name token", error, StringComparison.Ordinal);
    }

    [Fact]
    public void An_install_directory_without_the_published_executable_is_refused()
    {
        using var sandbox = UpdateApplySandbox.Create();
        File.Delete(sandbox.Executable);

        Assert.False(UpdateApplyRequest.TryParse(sandbox.Request.ToArguments(), sandbox.DataRoot, out _, out var error));
        Assert.Contains("does not hold", error, StringComparison.Ordinal);
    }

    [Fact]
    public void An_install_directory_inside_the_data_root_is_refused()
    {
        using var sandbox = UpdateApplySandbox.Create();
        var inside = Path.Combine(UpdateApplyPaths.UpdatesRoot(sandbox.DataRoot), "install");
        UpdateApplySandbox.WriteTree(inside, "inside");
        var args = Replace(sandbox.Request.ToArguments(), UpdateApplyRequest.InstallSwitch, inside);

        Assert.False(UpdateApplyRequest.TryParse(args, sandbox.DataRoot, out _, out var error));
        Assert.Contains("inside the data directory", error, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unknown_switch_and_a_missing_process_id_are_refused()
    {
        using var sandbox = UpdateApplySandbox.Create();

        Assert.False(UpdateApplyRequest.TryParse(
            [.. sandbox.Request.ToArguments(), "--rename", "everything"],
            sandbox.DataRoot,
            out _,
            out var unknownError));
        Assert.Contains("unknown switch", unknownError, StringComparison.Ordinal);

        var withoutPid = sandbox.Request.ToArguments()
            .Where((argument, index) => !(index is 1 or 2))
            .ToArray();
        Assert.False(UpdateApplyRequest.TryParse(withoutPid, sandbox.DataRoot, out var withoutPidRequest, out var pidError));
        Assert.Null(withoutPidRequest);
        Assert.Contains("process id is missing", pidError, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-3")]
    [InlineData("not a pid")]
    public void A_process_id_that_is_not_positive_is_refused(string pid)
    {
        using var sandbox = UpdateApplySandbox.Create();
        var args = Replace(sandbox.Request.ToArguments(), UpdateApplyRequest.PidSwitch, pid);

        Assert.False(UpdateApplyRequest.TryParse(args, sandbox.DataRoot, out _, out var error));
        Assert.Contains("process id", error, StringComparison.Ordinal);
    }

    [Fact]
    public void The_recovery_direction_only_accepts_the_install_directories_own_backup()
    {
        using var sandbox = UpdateApplySandbox.Create();
        UpdateApplySandbox.WriteTree(sandbox.OldDirectory, "previous");
        var recoveryArgs = Replace(
            [.. sandbox.Request.ToArguments(), UpdateApplyRequest.RecoverSwitch],
            UpdateApplyRequest.StagingSwitch,
            sandbox.OldDirectory);

        Assert.True(UpdateApplyRequest.TryParse(recoveryArgs, sandbox.DataRoot, out var recovery, out var recoveryError), recoveryError);
        Assert.NotNull(recovery);
        Assert.True(recovery!.Recovery);

        // The same backup name for another version is not this install directory's backup.
        var wrongVersion = Replace(recoveryArgs, UpdateApplyRequest.VersionSwitch, "9.9.9");
        Assert.False(UpdateApplyRequest.TryParse(wrongVersion, sandbox.DataRoot, out _, out var wrongError));
        Assert.Contains("backup", wrongError, StringComparison.Ordinal);

        // A staged tree is not a backup: the recovery direction does not accept the download area.
        var stagedTree = Replace(recoveryArgs, UpdateApplyRequest.StagingSwitch, sandbox.Staging);
        Assert.False(UpdateApplyRequest.TryParse(stagedTree, sandbox.DataRoot, out _, out var stagedError));
        Assert.Contains("backup", stagedError, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- install.log

    [Fact]
    public void The_install_log_keeps_its_newest_lines_and_stays_bounded()
    {
        var root = TestTempFiles.NewDirectory("forge-apply-log-");
        try
        {
            var log = Path.Combine(root, UpdateApplyPaths.InstallLogFileName);
            UpdateInstallLog.Append(log, "the first line");
            for (var i = 0; i < 4000; i++)
            {
                UpdateInstallLog.Append(log, "line " + i.ToString(System.Globalization.CultureInfo.InvariantCulture) + new string('x', 96));
            }

            Assert.True(new FileInfo(log).Length <= UpdateInstallLog.MaxBytes, "the log grew past its bound");
            var text = File.ReadAllText(log);
            Assert.Contains("line 3999", text, StringComparison.Ordinal);
            Assert.Contains("(log truncated)", text, StringComparison.Ordinal);
            Assert.DoesNotContain("the first line", text, StringComparison.Ordinal);
        }
        finally
        {
            TestTempFiles.DeleteTree(root);
        }
    }

    // ---------------------------------------------------------------- the state machine

    [Fact]
    public async Task A_running_instance_that_never_exits_aborts_without_changing_anything()
    {
        using var sandbox = UpdateApplySandbox.Create(processExitTimeout: TimeSpan.FromMilliseconds(50));
        sandbox.Processes.OldProcessExits = false;

        var result = await sandbox.Runner.RunAsync(sandbox.Request, CancellationToken.None);

        Assert.Equal(UpdateApplyStatus.Aborted, result.Status);
        Assert.Equal(4242, Assert.Single(sandbox.Processes.Waited));
        Assert.Empty(sandbox.Processes.Starts);
        Assert.Equal("old", File.ReadAllText(sandbox.InstallMarker));
        Assert.True(Directory.Exists(sandbox.Staging));
        Assert.False(Directory.Exists(sandbox.OldDirectory));
        Assert.Contains(sandbox.Log, line => line.Contains("did not exit", StringComparison.Ordinal));
        Assert.Contains("did not exit", File.ReadAllText(UpdateApplyPaths.InstallLog(sandbox.DataRoot)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_confirmed_swap_installs_the_staged_tree_and_keeps_the_backup()
    {
        using var sandbox = UpdateApplySandbox.Create();
        sandbox.Processes.OnStart = (_, arguments) =>
        {
            if (arguments.Contains(UpdateApplyRequest.ConfirmedSwitch))
            {
                sandbox.Confirm();
            }
        };

        var result = await sandbox.Runner.RunAsync(sandbox.Request, CancellationToken.None);

        Assert.Equal(UpdateApplyStatus.Applied, result.Status);
        Assert.True(result.Succeeded);
        Assert.Equal("new", File.ReadAllText(sandbox.InstallMarker));
        Assert.Equal("old", File.ReadAllText(Path.Combine(sandbox.OldDirectory, "tree.txt")));
        Assert.False(Directory.Exists(sandbox.Staging));
        Assert.True(File.Exists(sandbox.MarkerPath));

        var start = Assert.Single(sandbox.Processes.Starts);
        Assert.Equal(sandbox.Executable, start.Path);
        Assert.Contains(UpdateApplyRequest.ConfirmedSwitch, start.Arguments);
        Assert.Contains(sandbox.Request.Version, start.Arguments);
        Assert.Contains(UpdateApplyRequest.InstallSwitch, start.Arguments);
        Assert.Contains(sandbox.Install, start.Arguments);
        Assert.Contains(sandbox.Log, line => line.Contains("confirmed after", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_confirmation_left_by_an_earlier_attempt_does_not_confirm_this_run()
    {
        using var sandbox = UpdateApplySandbox.Create(confirmationTimeout: TimeSpan.FromMilliseconds(60));
        sandbox.Confirm();

        var result = await sandbox.Runner.RunAsync(sandbox.Request, CancellationToken.None);

        Assert.Equal(UpdateApplyStatus.RolledBack, result.Status);
        Assert.Contains("did not confirm", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_new_version_that_never_confirms_is_stopped_and_rolled_back()
    {
        using var sandbox = UpdateApplySandbox.Create(confirmationTimeout: TimeSpan.FromMilliseconds(60));

        var result = await sandbox.Runner.RunAsync(sandbox.Request, CancellationToken.None);

        Assert.Equal(UpdateApplyStatus.RolledBack, result.Status);
        Assert.Equal("old", File.ReadAllText(sandbox.InstallMarker));
        Assert.Equal("new", File.ReadAllText(Path.Combine(sandbox.FailedDirectory, "tree.txt")));
        Assert.False(Directory.Exists(sandbox.OldDirectory));
        Assert.Equal(1, sandbox.Processes.Processes[0].Kills);

        // The previous version is started again, without arguments: it is not the applier and has nothing to confirm.
        Assert.Equal(2, sandbox.Processes.Starts.Count);
        Assert.Empty(sandbox.Processes.Starts[1].Arguments);
        Assert.Contains(sandbox.Log, line => line.Contains("rolling back", StringComparison.Ordinal));
        Assert.Contains(sandbox.Log, line => line.Contains("restored", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_new_version_that_exits_early_is_rolled_back_with_its_exit_code()
    {
        using var sandbox = UpdateApplySandbox.Create();
        sandbox.Processes.OnStart = (process, _) => process.Exit(7);

        var result = await sandbox.Runner.RunAsync(sandbox.Request, CancellationToken.None);

        Assert.Equal(UpdateApplyStatus.RolledBack, result.Status);
        Assert.Contains("exited (7)", result.Reason, StringComparison.Ordinal);
        Assert.Equal("old", File.ReadAllText(sandbox.InstallMarker));
        Assert.Equal(0, sandbox.Processes.Processes[0].Kills);
        Assert.Contains("exited (7)", File.ReadAllText(UpdateApplyPaths.InstallLog(sandbox.DataRoot)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_new_version_that_cannot_be_started_is_rolled_back()
    {
        using var sandbox = UpdateApplySandbox.Create();
        sandbox.Processes.StartSucceeds = false;

        var result = await sandbox.Runner.RunAsync(sandbox.Request, CancellationToken.None);

        Assert.Equal(UpdateApplyStatus.RolledBack, result.Status);
        Assert.Equal("old", File.ReadAllText(sandbox.InstallMarker));
        Assert.Equal("new", File.ReadAllText(Path.Combine(sandbox.FailedDirectory, "tree.txt")));
        Assert.Empty(sandbox.Processes.Starts);
    }

    [Fact]
    public async Task A_staged_tree_that_is_gone_is_rolled_back_after_the_rename()
    {
        using var sandbox = UpdateApplySandbox.Create();
        var gone = Path.Combine(UpdateApplyPaths.UpdatesRoot(sandbox.DataRoot), UpdateApplySandbox.Version, "gone");
        sandbox.UseStaging(gone);

        var result = await sandbox.Runner.RunAsync(sandbox.Request, CancellationToken.None);

        Assert.Equal(UpdateApplyStatus.RolledBack, result.Status);
        Assert.Equal("old", File.ReadAllText(sandbox.InstallMarker));
        Assert.Contains(sandbox.Log, line => line.Contains("could not be installed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_recovery_direction_restores_the_backup_without_a_confirmation()
    {
        using var sandbox = UpdateApplySandbox.Create(confirmationTimeout: TimeSpan.FromMilliseconds(50));

        // The state the startup repair sees: the install directory holds an unconfirmed version, its previous tree is the
        // backup, and the confirmation marker was never written.
        UpdateApplySandbox.WriteTree(sandbox.OldDirectory, "previous");
        var unconfirmed = sandbox.Install;
        sandbox.UseStaging(sandbox.OldDirectory);
        sandbox.UseRecovery();

        var result = await sandbox.Runner.RunAsync(sandbox.Request, CancellationToken.None);

        Assert.Equal(UpdateApplyStatus.RolledBack, result.Status);
        Assert.Equal("previous", File.ReadAllText(Path.Combine(unconfirmed, "tree.txt")));
        Assert.Equal("old", File.ReadAllText(Path.Combine(sandbox.FailedDirectory, "tree.txt")));
        Assert.False(Directory.Exists(sandbox.OldDirectory));
        Assert.Equal(sandbox.Executable, Assert.Single(sandbox.Processes.Starts).Path);
        Assert.Empty(sandbox.Processes.Starts[0].Arguments);
    }

    [Fact]
    public async Task A_confirmation_that_arrives_while_the_new_version_is_stopped_does_not_survive_the_rollback()
    {
        using var sandbox = UpdateApplySandbox.Create(confirmationTimeout: TimeSpan.FromMilliseconds(60));

        // The unconfirmed version writes its marker just as the applier stops it (its window came up while the watchdog was
        // already firing). A marker that outlived the rollback would make the next start's housekeeping read "an .old backup
        // plus its marker" as a finished swap and delete the only copy of the previous version.
        sandbox.Processes.OnStart = (process, _) => process.OnKill = sandbox.Confirm;

        var result = await sandbox.Runner.RunAsync(sandbox.Request, CancellationToken.None);

        Assert.Equal(UpdateApplyStatus.RolledBack, result.Status);
        Assert.Equal(1, sandbox.Processes.Processes[0].Kills);
        Assert.False(File.Exists(sandbox.MarkerPath));
        Assert.Contains(sandbox.Log, line => line.Contains("dropped the late confirmation", StringComparison.Ordinal));
        Assert.Equal("old", File.ReadAllText(sandbox.InstallMarker));
    }

    [Fact]
    public async Task A_late_confirmation_is_dropped_even_when_there_is_no_backup_to_restore()
    {
        using var sandbox = UpdateApplySandbox.Create(confirmationTimeout: TimeSpan.FromMilliseconds(60));
        sandbox.Processes.OnStart = (process, _) =>
        {
            // A late confirmation and a backup that disappeared underneath the rollback: the marker still must not stay.
            process.OnKill = () =>
            {
                sandbox.Confirm();
                Directory.Delete(sandbox.OldDirectory, recursive: true);
            };
        };

        var result = await sandbox.Runner.RunAsync(sandbox.Request, CancellationToken.None);

        Assert.Equal(UpdateApplyStatus.Failed, result.Status);
        Assert.Contains("no backup", result.Reason, StringComparison.Ordinal);
        Assert.False(File.Exists(sandbox.MarkerPath));
        Assert.True(Directory.Exists(sandbox.Install), "the install directory must never be left missing");
    }

    [Fact]
    public async Task A_rollback_without_a_backup_leaves_the_install_directory_alone()
    {
        using var sandbox = UpdateApplySandbox.Create(confirmationTimeout: TimeSpan.FromMilliseconds(60));
        sandbox.Processes.OnStart = (_, _) =>
        {
            // Something outside the applier removed the backup while the new version was starting.
            Directory.Delete(sandbox.OldDirectory, recursive: true);
        };

        var result = await sandbox.Runner.RunAsync(sandbox.Request, CancellationToken.None);

        Assert.Equal(UpdateApplyStatus.Failed, result.Status);
        Assert.Contains("no backup", result.Reason, StringComparison.Ordinal);
        Assert.True(Directory.Exists(sandbox.Install), "the install directory must never be left missing");
        Assert.Equal("new", File.ReadAllText(sandbox.InstallMarker));
        Assert.False(Directory.Exists(sandbox.FailedDirectory));
    }

    [Fact]
    public async Task A_recovery_without_a_backup_does_not_move_the_install_directory()
    {
        using var sandbox = UpdateApplySandbox.Create();
        sandbox.UseStaging(Path.Combine(UpdateApplyPaths.UpdatesRoot(sandbox.DataRoot), UpdateApplySandbox.Version, "gone"));
        sandbox.UseRecovery();

        var result = await sandbox.Runner.RunAsync(sandbox.Request, CancellationToken.None);

        Assert.Equal(UpdateApplyStatus.Failed, result.Status);
        Assert.True(Directory.Exists(sandbox.Install));
        Assert.Equal("old", File.ReadAllText(sandbox.InstallMarker));
        Assert.False(Directory.Exists(sandbox.FailedDirectory));
        Assert.Empty(sandbox.Processes.Starts);
    }

    // ---------------------------------------------------------------- the process branch

    [Fact]
    public void A_refused_command_line_is_recorded_and_returns_the_refusal_code()
    {
        using var sandbox = UpdateApplySandbox.Create();

        var exitCode = UpdateApplyCommand.Run(
            [UpdateApplyRequest.ApplySwitch, UpdateApplyRequest.PidSwitch, "1"],
            sandbox.DataRoot);

        Assert.Equal(2, exitCode);
        Assert.Contains("refused", File.ReadAllText(UpdateApplyPaths.InstallLog(sandbox.DataRoot)), StringComparison.Ordinal);
        Assert.Equal("old", File.ReadAllText(sandbox.InstallMarker));
    }

    [Fact]
    public void A_confirmed_apply_returns_zero_and_an_abort_returns_its_own_code()
    {
        using var sandbox = UpdateApplySandbox.Create();
        sandbox.Processes.OnStart = (_, arguments) =>
        {
            if (arguments.Contains(UpdateApplyRequest.ConfirmedSwitch))
            {
                sandbox.Confirm();
            }
        };

        Assert.Equal(0, UpdateApplyCommand.Run(sandbox.Request.ToArguments(), sandbox.DataRoot, sandbox.Processes));
        Assert.Equal("new", File.ReadAllText(sandbox.InstallMarker));

        using var second = UpdateApplySandbox.Create(processExitTimeout: TimeSpan.FromMilliseconds(30));
        second.Processes.OldProcessExits = false;
        Assert.Equal(4, UpdateApplyCommand.Run(second.Request.ToArguments(), second.DataRoot, second.Processes));
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
