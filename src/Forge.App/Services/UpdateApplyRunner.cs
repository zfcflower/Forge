using System.Diagnostics;
using System.Globalization;
using Forge.App.Logging;
using ZLogger;

namespace Forge.App.Services;

/// <summary>The time bounds and the executable name of one install; the defaults are the shipped ones.</summary>
internal sealed record UpdateApplyOptions
{
    /// <summary>
    /// How long the applier waits for the running instance to exit before it concludes nothing can be renamed. The instance
    /// asks for the update and then shuts itself down (closing the extension host first), which takes a moment, not a minute.
    /// </summary>
    public TimeSpan ProcessExitTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>The watchdog of wave W10c: how long the new version has to show its window and confirm the swap.</summary>
    public TimeSpan ConfirmationTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Watched for the confirmation marker and for the new version exiting; cheap enough to be frequent.</summary>
    public TimeSpan ConfirmationPollInterval { get; init; } = TimeSpan.FromMilliseconds(100);

    /// <summary>How long a killed process is given to release its files before the swap continues.</summary>
    public TimeSpan KillGrace { get; init; } = TimeSpan.FromSeconds(5);

    public static UpdateApplyOptions Default { get; } = new();
}

/// <summary>How one install ended; <see cref="Reason"/> is the English sentence written to the log.</summary>
internal enum UpdateApplyStatus
{
    /// <summary>The new version was installed, started and confirmed the swap.</summary>
    Applied,

    /// <summary>The swap was undone, the previous version was restored and restarted.</summary>
    RolledBack,

    /// <summary>Nothing was changed: the running instance never exited, or the request was refused.</summary>
    Aborted,

    /// <summary>The swap and the restoration both failed; the log says which step.</summary>
    Failed,
}

internal sealed record UpdateApplyResult(UpdateApplyStatus Status, string Reason)
{
    public bool Succeeded => Status == UpdateApplyStatus.Applied;
}

/// <summary>
/// Wave W10c's install state machine, the single owner of the directory swap. The steps are the ones the module SPEC
/// records under「自动更新」:
/// <list type="number">
/// <item>wait for the process being replaced to exit (bounded — a Windows install directory cannot be renamed while its
/// executable is running);</item>
/// <item>rename the install directory to <c>.old-&lt;version&gt;</c> and move the staged tree in its place;</item>
/// <item>start the new executable with <c>--update-applied &lt;version&gt; --install &lt;dir&gt;</c>;</item>
/// <item>wait at most <see cref="UpdateApplyOptions.ConfirmationTimeout"/> for the new version to write
/// <c>&lt;data&gt;/updates/ok-&lt;version&gt;</c> once its main window is up;</item>
/// <item>on a timeout or an early exit, kill it, move the unconfirmed tree to <c>.failed-&lt;version&gt;</c>, restore
/// <c>.old-&lt;version&gt;</c> and start the previous version again.</item>
/// </list>
/// Every step is appended to <c>&lt;data&gt;/updates/install.log</c> through <see cref="UpdateInstallLog"/>, including the
/// reason of a rollback, so a machine that ends up on the old version again can say why. A successful swap leaves the
/// <c>.old-&lt;version&gt;</c> directory in place: the confirmation is written by a process that may still crash, and the
/// next startup cleans the backup up (see <see cref="UpdateApplyStartup"/>).
/// <para>
/// This runs in a process of its own — the staged new <c>Forge.App</c>, started by the installed one — so it holds no UI,
/// no Avalonia application and no data-root lock. The state machine is pure file and process work, which is also why the
/// tests can drive it with fake directories and a scripted process seam.
/// </para>
/// </summary>
internal sealed class UpdateApplyRunner
{
    private readonly string dataRoot;
    private readonly string logPath;
    private readonly UpdateApplyOptions options;
    private readonly IUpdateApplyProcesses processes;
    private readonly Action<string>? sink;

    public UpdateApplyRunner(
        string dataRoot,
        UpdateApplyOptions? options = null,
        IUpdateApplyProcesses? processes = null,
        Action<string>? sink = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        this.dataRoot = dataRoot;
        this.options = options ?? UpdateApplyOptions.Default;
        this.processes = processes ?? new SystemUpdateApplyProcesses();
        this.sink = sink;
        logPath = UpdateApplyPaths.InstallLog(dataRoot);
    }

    /// <summary>The bounded log of this data root; the UI's "open location" and a support report both point at it.</summary>
    public string LogPath => logPath;

    public async Task<UpdateApplyResult> RunAsync(UpdateApplyRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var install = request.InstallDirectory;
        var oldDirectory = request.OldDirectory;
        var marker = UpdateApplyPaths.Marker(dataRoot, request.Version);
        var executable = UpdateApplyPaths.AppExecutable(install);

        Log($"apply {request.Version}: install='{install}' staging='{request.StagingDirectory}' pid={request.OldProcessId}"
            + (request.Recovery ? " (rollback of an unconfirmed install)" : string.Empty));

        // 1. The directory cannot be renamed while the instance being replaced runs from it.
        if (!await processes.WaitForExitAsync(request.OldProcessId, options.ProcessExitTimeout, ct).ConfigureAwait(false))
        {
            return Abort($"the running instance (pid {request.OldProcessId}) did not exit within {Describe(options.ProcessExitTimeout)}; nothing was changed");
        }

        Log("the running instance has exited");

        if (request.Recovery)
        {
            // The rollback direction: the install directory holds an unconfirmed version and the staging tree is the previous
            // version's backup. Nothing is moved aside for a new version and no confirmation is awaited.
            return Restore(request, executable);
        }

        // 2. A confirmation left behind by an earlier attempt must not satisfy this run's watch; if it cannot be removed
        //    (locked by another process), abort rather than risk reading it as this run's confirmation.
        if (!TryDeleteFile(marker))
        {
            return Abort($"the stale confirmation marker '{marker}' could not be removed");
        }

        // 3. The swap. The backup is renamed first, so a failure after this point always has something to restore.
        if (Directory.Exists(oldDirectory) && !TryDeleteDirectory(oldDirectory, "stale backup"))
        {
            return Abort($"the stale backup '{oldDirectory}' could not be removed");
        }

        try
        {
            Directory.Move(install, oldDirectory);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Abort($"the install directory could not be renamed to '{oldDirectory}': {exception.Message}");
        }

        Log($"renamed '{install}' to '{oldDirectory}'");

        if (!TryInstall(request.StagingDirectory, install, out var installError))
        {
            return await RollBackAsync(request, executable, started: null, "the staged tree could not be installed: " + installError, ct)
                .ConfigureAwait(false);
        }

        Log($"installed '{request.StagingDirectory}' as '{install}'");

        // 4. Start the new version and wait for it to say that its window is up.
        var started = processes.Start(executable, [UpdateApplyRequest.ConfirmedSwitch, request.Version, UpdateApplyRequest.InstallSwitch, install]);
        if (started is null)
        {
            return await RollBackAsync(request, executable, null, $"the new version '{executable}' could not be started", ct)
                .ConfigureAwait(false);
        }

        Log($"started '{executable}' (pid {started.Id}) and waiting for '{marker}'");

        using (started)
        {
            var watch = Stopwatch.StartNew();
            while (true)
            {
                if (File.Exists(marker))
                {
                    Log($"confirmed after {Describe(watch.Elapsed)}; keeping the new version (the backup '{oldDirectory}' is cleaned at the next start)");
                    return new UpdateApplyResult(UpdateApplyStatus.Applied, "the new version confirmed the update");
                }

                if (started.HasExited)
                {
                    return await RollBackAsync(
                            request,
                            executable,
                            started,
                            $"the new version exited ({started.ExitCode}) before it confirmed the update",
                            ct)
                        .ConfigureAwait(false);
                }

                if (watch.Elapsed >= options.ConfirmationTimeout)
                {
                    return await RollBackAsync(
                            request,
                            executable,
                            started,
                            $"the new version did not confirm the update within {Describe(options.ConfirmationTimeout)}",
                            ct)
                        .ConfigureAwait(false);
                }

                await Task.Delay(options.ConfirmationPollInterval, ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// The rollback direction used by the startup repair: the unconfirmed install is moved to
    /// <c>.failed-&lt;version&gt;</c>, the backup (<paramref name="request"/>'s staging tree) is renamed into its place and the
    /// previous version is started. No confirmation is awaited — the version being restored is the one that ran before.
    /// </summary>
    private UpdateApplyResult Restore(UpdateApplyRequest request, string executable)
    {
        var install = request.InstallDirectory;
        var backup = request.StagingDirectory;
        var failedDirectory = UpdateApplyPaths.FailedDirectory(install, request.Version);
        Log($"restoring '{backup}' as '{install}'");

        // Atomically claim the backup before touching the install. Two recovery appliers can race (two launches after one
        // interrupted swap); a check-then-move would let the loser move the winner's restored install into .failed and then
        // fail on the already-consumed backup, leaving no install directory at all. The rename is the claim: exactly one
        // applier wins it, the loser fails cleanly without moving anything. The claimed name matches neither the .old-* nor
        // the .failed-* cleanup patterns, so a crash mid-restore never deletes the only good copy.
        var claimed = ClaimBackup(install, backup, request.Version);
        if (claimed is null)
        {
            return new UpdateApplyResult(UpdateApplyStatus.Failed, "there is no backup to restore at '" + backup + "'");
        }

        if (Directory.Exists(install))
        {
            if (Directory.Exists(failedDirectory) && !TryDeleteDirectory(failedDirectory, "stale failed copy"))
            {
                ReleaseClaim(claimed, backup);
                return new UpdateApplyResult(
                    UpdateApplyStatus.Failed,
                    "the stale copy '" + failedDirectory + "' could not be removed");
            }

            try
            {
                Directory.Move(install, failedDirectory);
                Log($"moved the unconfirmed install '{install}' to '{failedDirectory}'");
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                ReleaseClaim(claimed, backup);
                return new UpdateApplyResult(
                    UpdateApplyStatus.Failed,
                    "the unconfirmed install could not be moved out of the way: " + exception.Message);
            }
        }

        try
        {
            Directory.Move(claimed, install);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            ReleaseClaim(claimed, backup);
            RestoreFailedInstall(install, failedDirectory);
            return new UpdateApplyResult(
                UpdateApplyStatus.Failed,
                "the backup could not be restored: " + exception.Message);
        }

        Log($"restored '{backup}' as '{install}'");
        using var restored = processes.Start(executable, []);
        Log(restored is null
            ? $"the restored version '{executable}' could not be started"
            : $"started the restored version '{executable}' (pid {restored.Id})");
        return new UpdateApplyResult(
            restored is null ? UpdateApplyStatus.Failed : UpdateApplyStatus.RolledBack,
            "the previous version was restored");
    }

    /// <summary>
    /// Undoes the swap: the unconfirmed tree is moved out of the way (kept as <c>.failed-&lt;version&gt;</c> for whoever reads
    /// the log), the backup is renamed back and the previous version is started again.
    /// </summary>
    private async Task<UpdateApplyResult> RollBackAsync(
        UpdateApplyRequest request,
        string executable,
        IUpdateApplierProcess? started,
        string reason,
        CancellationToken ct)
    {
        var install = request.InstallDirectory;
        var failedDirectory = UpdateApplyPaths.FailedDirectory(install, request.Version);
        Log("rolling back: " + reason);

        if (started is not null && !started.HasExited)
        {
            started.Kill();
            var exited = await processes.WaitForExitAsync(started.Id, options.KillGrace, ct).ConfigureAwait(false);
            Log(exited
                ? $"stopped the unconfirmed version (pid {started.Id})"
                : $"the unconfirmed version (pid {started.Id}) did not exit within {Describe(options.KillGrace)}");
        }

        // A confirmation that arrived while the unconfirmed version was being stopped must not outlive the rollback: the next
        // start's housekeeping reads "an .old-<version> backup plus its marker" as a finished swap and would delete the only
        // remaining copy of the previous version. Best effort — the swap below does not depend on it.
        var marker = UpdateApplyPaths.Marker(dataRoot, request.Version);
        if (File.Exists(marker))
        {
            Log(TryDeleteFile(marker)
                ? $"dropped the late confirmation '{marker}'"
                : $"the late confirmation '{marker}' could not be removed");
        }

        if (!Directory.Exists(request.OldDirectory))
        {
            // The backup is gone (something outside the applier took it): keeping the unconfirmed tree is the only way to
            // leave the user with a runnable executable, so nothing is moved.
            Log($"there is no backup at '{request.OldDirectory}'; the unconfirmed version stays in place");
            return new UpdateApplyResult(UpdateApplyStatus.Failed, reason + "; no backup to restore");
        }

        // Same atomic claim as Restore: a concurrent recovery applier must not consume the backup between the check and the
        // move, or the loser would strand the install directory (see Restore).
        var claimed = ClaimBackup(install, request.OldDirectory, request.Version);
        if (claimed is null)
        {
            Log($"the backup at '{request.OldDirectory}' was claimed by another applier; the unconfirmed version stays in place");
            return new UpdateApplyResult(UpdateApplyStatus.Failed, reason + "; no backup to restore");
        }

        if (Directory.Exists(install))
        {
            if (Directory.Exists(failedDirectory) && !TryDeleteDirectory(failedDirectory, "stale failed copy"))
            {
                ReleaseClaim(claimed, request.OldDirectory);
                return new UpdateApplyResult(
                    UpdateApplyStatus.Failed,
                    reason + "; the stale copy '" + failedDirectory + "' could not be removed");
            }

            try
            {
                Directory.Move(install, failedDirectory);
                Log($"moved the unconfirmed install '{install}' to '{failedDirectory}'");
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                ReleaseClaim(claimed, request.OldDirectory);
                return new UpdateApplyResult(
                    UpdateApplyStatus.Failed,
                    reason + "; the unconfirmed install could not be moved out of the way: " + exception.Message);
            }
        }

        try
        {
            Directory.Move(claimed, install);
            Log($"restored '{request.OldDirectory}' as '{install}'");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Put the claim back so the backup keeps its .old name; the next start's repair (UpdateApplyStartup) can retry.
            ReleaseClaim(claimed, request.OldDirectory);
            RestoreFailedInstall(install, failedDirectory);
            return new UpdateApplyResult(
                UpdateApplyStatus.Failed,
                reason + "; the backup could not be restored: " + exception.Message);
        }

        using var restored = processes.Start(executable, []);
        Log(restored is null
            ? $"the restored version '{executable}' could not be started"
            : $"started the restored version '{executable}' (pid {restored.Id})");

        return new UpdateApplyResult(UpdateApplyStatus.RolledBack, reason);
    }

    /// <summary>
    /// Puts the staged tree in the install directory's place. A plain directory rename is the common case and keeps the
    /// modes; a staging root on another volume (the data directory is allowed to live anywhere) throws and the tree is
    /// copied instead. A copy leaves the staged tree behind, which the next download's staging cleanup removes.
    /// </summary>
    private bool TryInstall(string staging, string install, out string? error)
    {
        error = null;
        try
        {
            Directory.Move(staging, install);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Log($"moving the staged tree failed ({exception.Message}); copying it instead");
        }

        try
        {
            CopyTree(staging, install);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            error = exception.Message;
            return false;
        }
    }

    private void Log(string message)
    {
        UpdateInstallLog.Append(logPath, message);
        AppLogs.Update.ZLogInformation($"{message}");
        sink?.Invoke(message);
    }

    private UpdateApplyResult Abort(string reason)
    {
        Log(reason);
        return new UpdateApplyResult(UpdateApplyStatus.Aborted, reason);
    }

    private static string Describe(TimeSpan span) =>
        span.TotalSeconds >= 1
            ? span.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture) + "s"
            : span.TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture) + "ms";

    private static void CopyTree(string source, string destination)
    {
        if (!Directory.Exists(source))
        {
            throw new DirectoryNotFoundException("the staged tree '" + source + "' is gone");
        }

        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            var target = Path.Combine(destination, Path.GetFileName(file));
            File.Copy(file, target, overwrite: true);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(target, File.GetUnixFileMode(file));
            }
        }

        foreach (var directory in Directory.EnumerateDirectories(source))
        {
            CopyTree(directory, Path.Combine(destination, Path.GetFileName(directory)));
        }
    }

    private static bool TryDeleteFile(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return true;
            }

            File.Delete(path);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            AppLogs.Update.ZLogWarning($"removing '{path}' failed: {exception.Message}");
            return false;
        }
    }

    /// <summary>
    /// Best-effort recovery of the install path after the claimed→install move failed: the unconfirmed tree was already
    /// moved to <c>.failed</c>, so without this step the install path would stay empty — the one Failed terminal state that
    /// would leave the user without any executable. Putting the unconfirmed (but runnable) tree back keeps the invariant
    /// every other branch holds; the next start's repair can still roll it back properly.
    /// </summary>
    private void RestoreFailedInstall(string install, string failedDirectory)
    {
        if (Directory.Exists(install) || !Directory.Exists(failedDirectory))
        {
            return;
        }

        try
        {
            Directory.Move(failedDirectory, install);
            Log($"put the unconfirmed tree back at '{install}' after the restore move failed");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Log($"the unconfirmed tree could not be put back at '{install}': {exception.Message}");
        }
    }

    /// <summary>
    /// Atomically claims a backup directory by renaming it to <c>&lt;install&gt;.restoring-&lt;version&gt;-&lt;guid&gt;</c> beside
    /// it. The rename is the claim: of two racing appliers exactly one wins, the loser gets null and must not move anything.
    /// The claimed name matches neither the <c>.old-*</c> nor the <c>.failed-*</c> cleanup patterns, so a crash mid-restore
    /// leaves the only good copy untouched — and because the version is in the name, the next start's repair can put it back
    /// under its <c>.old-&lt;version&gt;</c> name (see <see cref="UpdateApplyStartup.TryComposeRepair"/>) instead of leaving the
    /// install without a backup forever.
    /// </summary>
    private string? ClaimBackup(string install, string backup, string version)
    {
        var parent = Path.GetDirectoryName(backup);
        if (parent is null)
        {
            return null;
        }

        var claimed = Path.Combine(parent, UpdateApplyPaths.RestoringName(install, version, Guid.NewGuid().ToString("N")));
        try
        {
            Directory.Move(backup, claimed);
            return claimed;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            Log($"the backup '{backup}' could not be claimed: {exception.Message}");
            return null;
        }
    }

    /// <summary>Puts a claimed backup back under its original name after a failed move; best effort, logged on failure.</summary>
    private void ReleaseClaim(string claimed, string backup)
    {
        try
        {
            Directory.Move(claimed, backup);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Log($"the claimed backup '{claimed}' could not be returned to '{backup}': {exception.Message}");
        }
    }

    private bool TryDeleteDirectory(string path, string what)
    {
        try
        {
            Directory.Delete(path, recursive: true);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Log($"removing the {what} '{path}' failed: {exception.Message}");
            return false;
        }
    }
}

/// <summary>
/// The <c>--apply-update</c> branch of the process entry: no Avalonia, no application graph, no single-instance lock — just
/// the state machine. A refused command line writes the reason to the install log, so a hand-typed invocation leaves a
/// trace instead of doing nothing.
/// </summary>
internal static class UpdateApplyCommand
{
    public static int Run(IReadOnlyList<string> args, string dataRoot, IUpdateApplyProcesses? processes = null)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);

        if (!UpdateApplyRequest.TryParse(args, dataRoot, out var request, out var error))
        {
            UpdateInstallLog.Append(UpdateApplyPaths.InstallLog(dataRoot), "refused: " + error);
            AppLogs.Update.ZLogWarning($"the update applier refused the command line: {error}");
            return 2;
        }

        var runner = new UpdateApplyRunner(dataRoot, processes: processes);
        var result = runner.RunAsync(request, CancellationToken.None).GetAwaiter().GetResult();
        return result.Status switch
        {
            UpdateApplyStatus.Applied => 0,
            UpdateApplyStatus.RolledBack => 3,
            UpdateApplyStatus.Aborted => 4,
            _ => 1,
        };
    }
}
