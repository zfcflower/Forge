using System.Reflection;
using Forge.App.Logging;
using ZLogger;

namespace Forge.App.Services;

/// <summary>What one startup's install housekeeping did; the lists are the paths involved.</summary>
internal sealed record UpdateApplyMaintenance(IReadOnlyList<string> Removed, IReadOnlyList<string> LeftBehind);

/// <summary>
/// The app side of wave W10c: the confirmation the applier waits for, and the housekeeping that makes a finished swap
/// permanent. Both run at startup, which is the only moment the install directory is not in use:
/// <list type="bullet">
/// <item><b>Confirmation.</b> The applier starts the new version with <c>--update-applied &lt;version&gt; --install
/// &lt;dir&gt;</c>. Once the main window is up, the app writes <c>&lt;data&gt;/updates/ok-&lt;version&gt;</c> — but only for
/// the directory it actually runs from: <c>--install</c> must be this process's <see cref="AppContext.BaseDirectory"/>, so a
/// stray confirmation can never bless a swap of some other directory.</item>
/// <item><b>Cleanup.</b> A confirmed swap keeps its <c>.old-&lt;version&gt;</c> backup until the next start (the confirming
/// process may still crash, and until it has confirmed the applier is the one that decides). On later starts the backup of a
/// confirmed version is deleted, as is every <c>.failed-&lt;version&gt;</c> tree an interrupted install left and every
/// <c>&lt;data&gt;/updates/.applier-&lt;guid&gt;</c> copy a finished applier ran from, so the install directory's parent gains at
/// most one backup at a time.</item>
/// <item><b>Half-swap repair.</b> An <c>.old-&lt;own version&gt;</c> backup without the matching confirmation means a swap
/// finished but was never confirmed (the applier died, or the machine went down). The running version is then an unverified
/// install and the previous one is still on disk: rather than keep the unverified copy, the app hands the directory back —
/// it starts the previous version's executable in the applier's rollback direction and exits without showing a window. A backup
/// whose restore died between claiming it and moving it into place is found under
/// <c>&lt;install&gt;.restoring-&lt;version&gt;-&lt;guid&gt;</c> and put back under its <c>.old</c> name first.</item>
/// </list>
/// </summary>
internal static class UpdateApplyStartup
{
    /// <summary>
    /// This build's version as it appears in marker names: the assembly <c>InformationalVersion</c> (CI stamps it from the
    /// tag) without the build metadata suffix. Null when the assembly carries no usable version.
    /// </summary>
    public static string? OwnVersionToken() =>
        UpdateApplyPaths.TryVersionToken(
            typeof(UpdateApplyStartup).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
            out var token)
            ? token
            : null;

    /// <summary>The install directory this process runs from; the applier is only ever allowed to swap this one.</summary>
    public static string OwnInstallDirectory() => UpdateApplyPaths.TrimSeparator(AppContext.BaseDirectory);

    /// <summary>
    /// Reads the confirmation launch (<c>--update-applied &lt;version&gt; --install &lt;dir&gt;</c>). False for an ordinary
    /// start, which is the common case and must stay cheap.
    /// </summary>
    public static bool TryReadConfirmation(IReadOnlyList<string> args, out string? version, out string? install)
    {
        ArgumentNullException.ThrowIfNull(args);
        version = null;
        install = null;

        for (var i = 0; i < args.Count; i++)
        {
            if (!string.Equals(args[i], UpdateApplyRequest.ConfirmedSwitch, StringComparison.Ordinal))
            {
                continue;
            }

            // The applier writes "--update-applied <version> --install <dir>"; both halves must be readable, or the start is
            // treated as an ordinary one (which then goes through the repair path instead of confirming anything).
            if (i + 1 >= args.Count || !UpdateApplyPaths.TryVersionToken(args[i + 1], out var token))
            {
                return false;
            }

            version = token;
        }

        if (version is null)
        {
            return false;
        }

        for (var i = 0; i < args.Count - 1; i++)
        {
            if (string.Equals(args[i], UpdateApplyRequest.InstallSwitch, StringComparison.Ordinal))
            {
                install = args[i + 1];
                break;
            }
        }

        if (install is null || !Path.IsPathFullyQualified(install))
        {
            version = null;
            install = null;
            return false;
        }

        return true;
    }

    /// <summary>
    /// Writes the confirmation marker for <paramref name="version"/> when <paramref name="install"/> is the directory this
    /// process runs from. Returns false (with the reason) when it is not, so the applier keeps waiting and rolls back.
    /// </summary>
    public static bool TryConfirm(string dataRoot, string install, string version, string selfDirectory, out string? reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(install);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        ArgumentException.ThrowIfNullOrWhiteSpace(selfDirectory);

        reason = null;
        var own = UpdateApplyPaths.TrimSeparator(selfDirectory);
        var claimed = UpdateApplyPaths.TrimSeparator(install);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!claimed.Equals(own, comparison))
        {
            reason = $"the confirmation names '{claimed}' but this process runs from '{own}'";
            return false;
        }

        if (!UpdateApplyPaths.TryVersionToken(version, out var token))
        {
            reason = "the confirmed version '" + version + "' is not a usable file-name token";
            return false;
        }

        var marker = UpdateApplyPaths.Marker(dataRoot, token);
        try
        {
            Directory.CreateDirectory(UpdateApplyPaths.UpdatesRoot(dataRoot));
            File.WriteAllText(marker, DateTimeOffset.Now.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            reason = "the marker '" + marker + "' could not be written: " + exception.Message;
            return false;
        }
    }

    /// <summary>
    /// The startup housekeeping described on the type: delete the backups of confirmed swaps, delete the trees of failed
    /// installs, delete the neutral copies the appliers ran from, drop markers whose backup is gone, and leave (with a log line)
    /// an unconfirmed backup alone — repairing that state is <see cref="RepairInterruptedSwap"/>'s job, because it needs a
    /// different process.
    /// </summary>
    /// <param name="preserveOldVersion">
    /// The version whose backup must survive this run: the one the current process just confirmed, kept for the applier's
    /// watchdog window (and for the next start, which then removes it).
    /// </param>
    public static UpdateApplyMaintenance Maintain(
        string dataRoot,
        string install,
        string? preserveOldVersion = null,
        Action<string>? sink = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(install);

        var removed = new List<string>();
        var leftBehind = new List<string>();
        var logPath = UpdateApplyPaths.InstallLog(dataRoot);
        var updatesRoot = UpdateApplyPaths.UpdatesRoot(dataRoot);
        var trimmed = UpdateApplyPaths.TrimSeparator(install);
        var name = Path.GetFileName(trimmed);
        var parent = UpdateApplyPaths.InstallParent(trimmed);

        if (parent is not null && Directory.Exists(parent) && name.Length > 0)
        {
            foreach (var directory in SafeEnumerateDirectories(parent, name + UpdateApplyPaths.OldPrefix + "*"))
            {
                var fileName = Path.GetFileName(directory);
                var version = fileName[(name.Length + UpdateApplyPaths.OldPrefix.Length)..];
                if (!File.Exists(UpdateApplyPaths.Marker(dataRoot, version)))
                {
                    LeftBehind(leftBehind, directory, "the swap that made it was never confirmed");
                    continue;
                }

                if (preserveOldVersion is not null &&
                    string.Equals(version, preserveOldVersion, StringComparison.Ordinal))
                {
                    LeftBehind(leftBehind, directory, "it is this run's rollback window");
                    continue;
                }

                if (TryDeleteDirectory(directory))
                {
                    Removed(removed, directory, "a confirmed install");
                }
            }

            foreach (var directory in SafeEnumerateDirectories(parent, name + UpdateApplyPaths.FailedPrefix + "*"))
            {
                if (TryDeleteDirectory(directory))
                {
                    Removed(removed, directory, "an install that failed its check");
                }
            }
        }

        // A marker without a backup has served its purpose: the backup it described is gone.
        if (Directory.Exists(updatesRoot))
        {
            // The neutral roots the appliers ran from. An applier cannot remove the directory it runs from, so the copy is
            // always left behind; a directory that is still in use (the applier that started this very process) simply
            // refuses the delete and is collected by a later start.
            foreach (var directory in SafeEnumerateDirectories(updatesRoot, UpdateApplyPaths.ApplierPrefix + "*"))
            {
                if (TryDeleteDirectory(directory))
                {
                    Removed(removed, directory, "an applier that finished its install");
                }
            }

            foreach (var marker in SafeEnumerateFiles(updatesRoot, UpdateApplyPaths.MarkerPrefix + "*"))
            {
                var version = Path.GetFileName(marker)[UpdateApplyPaths.MarkerPrefix.Length..];
                if (Directory.Exists(UpdateApplyPaths.OldDirectory(trimmed, version)))
                {
                    continue;
                }

                if (TryDeleteFile(marker))
                {
                    Removed(removed, marker, "a stale confirmation");
                }
            }
        }

        return new UpdateApplyMaintenance(removed, leftBehind);

        void Removed(List<string> list, string path, string why)
        {
            list.Add(path);
            LogTo(logPath, $"removed {why} '{path}'", sink);
        }

        void LeftBehind(List<string> list, string path, string why)
        {
            list.Add(path);
            LogTo(logPath, $"left '{path}' in place: {why}", sink);
        }
    }

    /// <summary>
    /// Detects the half swap — an <c>.old-&lt;own version&gt;</c> backup with no confirmation — and composes the rollback
    /// request that restores it. Pure: the caller decides whether to start it.
    /// <para>
    /// An <c>.old-&lt;own version&gt;</c> that is missing is not the end of the story: a restore that crashed between claiming
    /// its backup and moving it into place left the backup under
    /// <c>&lt;install&gt;.restoring-&lt;version&gt;-&lt;guid&gt;</c>. That name carries the version exactly so this method can find it,
    /// put it back under its <c>.old</c> name and repair from there — otherwise the abandoned claim would sit there forever and
    /// the install would have no previous version to fall back to.
    /// </para>
    /// </summary>
    public static bool TryComposeRepair(
        string dataRoot,
        string install,
        string? ownVersion,
        int oldProcessId,
        out UpdateApplyRequest? request,
        out string? reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(install);
        request = null;
        reason = null;

        if (ownVersion is null)
        {
            reason = "this build has no usable version token";
            return false;
        }

        install = UpdateApplyPaths.TrimSeparator(install);
        if (!File.Exists(UpdateApplyPaths.AppExecutable(install)))
        {
            reason = "this process does not run from an install directory holding " + UpdateApplyPaths.AppExecutableName;
            return false;
        }

        var backup = UpdateApplyPaths.OldDirectory(install, ownVersion);
        if (!Directory.Exists(backup))
        {
            backup = ReclaimOrphanedBackup(install, ownVersion);
            if (backup is null)
            {
                return false;
            }
        }

        if (File.Exists(UpdateApplyPaths.Marker(dataRoot, ownVersion)))
        {
            // A confirmed swap; the backup is ordinary housekeeping, not a repair.
            return false;
        }

        if (!File.Exists(UpdateApplyPaths.AppExecutable(backup)))
        {
            reason = "the backup '" + backup + "' does not hold " + UpdateApplyPaths.AppExecutableName;
            return false;
        }

        var candidate = new UpdateApplyRequest(oldProcessId, install, backup, ownVersion) { Recovery = true };
        if (!UpdateApplyRequest.TryParse(candidate.ToArguments(), dataRoot, out var validated, out var error))
        {
            // The applier would refuse it, so do not start it; the reasons are the validation's own.
            reason = "the repair request would be refused: " + error;
            return false;
        }

        request = validated;
        return true;
    }

    /// <summary>
    /// Puts a backup whose claim never completed back under its <c>.old-&lt;version&gt;</c> name and returns it; null when there is
    /// no such claim (or none could be renamed). The claims of one version are the only ones considered — another version's
    /// claim belongs to another swap.
    /// </summary>
    private static string? ReclaimOrphanedBackup(string install, string ownVersion)
    {
        var parent = UpdateApplyPaths.InstallParent(install);
        if (parent is null)
        {
            return null;
        }

        var backup = UpdateApplyPaths.OldDirectory(install, ownVersion);
        foreach (var claimed in SafeEnumerateDirectories(parent, UpdateApplyPaths.RestoringPattern(install, ownVersion)))
        {
            try
            {
                Directory.Move(claimed, backup);
                return backup;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Another applier may be holding it, or the backup name may already be taken; the caller decides from the
                // remaining claims, and a failure here simply means "nothing to repair".
                AppLogs.Update.ZLogWarning($"the orphaned backup claim '{claimed}' could not be renamed back: {exception.Message}");
            }
        }

        return null;
    }

    /// <summary>
    /// The startup repair of the process entry: when this process started from an unconfirmed swap, hand the install
    /// directory back to the previous version and exit without a UI. Returns the exit code, or null when there is nothing to
    /// repair (or it could not be started, in which case the app still starts — refusing to launch would be worse).
    /// </summary>
    public static int? RepairInterruptedSwap(
        string dataRoot,
        IReadOnlyList<string> args,
        string? install = null,
        string? ownVersion = null,
        int? oldProcessId = null,
        Func<UpdateApplyRequest, bool>? start = null,
        Action<string>? sink = null)
    {
        ArgumentNullException.ThrowIfNull(args);

        if (TryReadConfirmation(args, out _, out _))
        {
            // The applier is driving this start; the swap is not interrupted, it is in progress.
            return null;
        }

        if (!TryComposeRepair(
                dataRoot,
                install ?? OwnInstallDirectory(),
                ownVersion ?? OwnVersionToken(),
                oldProcessId ?? Environment.ProcessId,
                out var request,
                out var reason))
        {
            if (reason is not null)
            {
                LogTo(UpdateApplyPaths.InstallLog(dataRoot), "no startup repair: " + reason, sink);
            }

            return null;
        }

        var logPath = UpdateApplyPaths.InstallLog(dataRoot);
        var startProcess = start ?? (request => StartRecovery(dataRoot, request));
        LogTo(
            logPath,
            $"repairing an unconfirmed swap: restoring '{request!.OldDirectory}' and restarting it; this process exits",
            sink);
        if (!startProcess(request))
        {
            LogTo(logPath, "the repair process could not be started; continuing with the installed version", sink);
            return null;
        }

        return 0;
    }

    /// <summary>
    /// The app-side entry point used once the main window is up: confirm the swap when this start is the one the applier is
    /// waiting for, then run the housekeeping. Never throws — it runs on a pool thread after the UI is alive, and an install
    /// problem must not take the app down.
    /// </summary>
    public static void RunAfterMainWindowShown(
        string dataRoot,
        IReadOnlyList<string> args,
        string? selfDirectory = null,
        string? ownVersion = null,
        Action<string>? sink = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        ArgumentNullException.ThrowIfNull(args);

        try
        {
            var own = selfDirectory is null ? OwnInstallDirectory() : UpdateApplyPaths.TrimSeparator(selfDirectory);
            var versionToken = ownVersion ?? OwnVersionToken();
            string? confirmedVersion = null;
            if (TryReadConfirmation(args, out var version, out var install))
            {
                if (versionToken is not null && !string.Equals(versionToken, version, StringComparison.Ordinal))
                {
                    // The applier started this build for another version: writing the marker would bless an install nobody
                    // verified, so it is left unwritten and the applier's watchdog undoes the swap.
                    LogTo(
                        UpdateApplyPaths.InstallLog(dataRoot),
                        $"the confirmation names {version} but this build is {versionToken}; it was not written",
                        sink);
                }
                else if (TryConfirm(dataRoot, install!, version!, own, out var reason))
                {
                    confirmedVersion = version;
                    LogTo(UpdateApplyPaths.InstallLog(dataRoot), $"confirmed the install of {version}", sink);
                }
                else
                {
                    LogTo(UpdateApplyPaths.InstallLog(dataRoot), "the install was not confirmed: " + reason, sink);
                }
            }

            var maintenance = Maintain(dataRoot, own, confirmedVersion, sink);
            if (maintenance.LeftBehind.Count > 0)
            {
                AppLogs.Update.ZLogWarning(
                    $"{maintenance.LeftBehind.Count} leftover install directories were left in place; see the install log");
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            AppLogs.Update.ZLogWarning($"update startup housekeeping failed: {exception.Message}");
        }
    }

    /// <summary>Starts the previous version in the applier's rollback direction; detached, so it outlives this process.</summary>
    private static bool StartRecovery(string dataRoot, UpdateApplyRequest request)
    {
        // The applier must not run from the .old backup it claims: Windows cannot rename a directory holding a running image.
        var start = UpdateApplierHost.CreateStartInfo(dataRoot, request.StagingDirectory, request.ToArguments(), out var error);
        if (start is null)
        {
            AppLogs.Update.ZLogWarning($"preparing the update repair failed: {error}");
            return false;
        }

        try
        {
            using var process = System.Diagnostics.Process.Start(start);
            return process is not null;
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            AppLogs.Update.ZLogWarning($"starting the update repair failed: {exception.Message}");
            return false;
        }
    }

    private static IReadOnlyList<string> SafeEnumerateDirectories(string parent, string pattern)
    {
        try
        {
            return Directory.GetDirectories(parent, pattern, SearchOption.TopDirectoryOnly);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static IReadOnlyList<string> SafeEnumerateFiles(string parent, string pattern)
    {
        try
        {
            return Directory.GetFiles(parent, pattern, SearchOption.TopDirectoryOnly);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static bool TryDeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            AppLogs.Update.ZLogWarning($"removing the leftover install directory '{path}' failed: {exception.Message}");
            return false;
        }
    }

    private static bool TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            AppLogs.Update.ZLogWarning($"removing the stale update marker '{path}' failed: {exception.Message}");
            return false;
        }
    }

    /// <summary>Appends to the bounded install log and to the diagnostic log; the sink is where it can be read back.</summary>
    private static void LogTo(string logPath, string message, Action<string>? sink)
    {
        UpdateInstallLog.Append(logPath, message);
        AppLogs.Update.ZLogInformation($"{message}");
        sink?.Invoke(message);
    }
}
