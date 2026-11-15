using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using Forge.App.Logging;
using ZLogger;

namespace Forge.App.Services;

/// <summary>
/// Where the update applier keeps its state and how a version becomes a file name. Wave W10c's install protocol is private
/// to this app (the command line and the marker file are not an external contract), so every path lives here once:
/// <c>&lt;data&gt;/updates/</c> holds the staged packages, the bounded <c>install.log</c>, the <c>ok-&lt;version&gt;</c>
/// confirmations and the <c>.applier-&lt;guid&gt;</c> copies every applier runs from; a swap leaves
/// <c>&lt;install&gt;.old-&lt;version&gt;</c> and <c>&lt;install&gt;.failed-&lt;version&gt;</c> next to the install directory, and a
/// restore claims its backup as <c>&lt;install&gt;.restoring-&lt;version&gt;-&lt;guid&gt;</c> while it runs.
/// </summary>
internal static class UpdateApplyPaths
{
    /// <summary>The staging root under the data directory (wave W10b downloads into it).</summary>
    public const string UpdatesDirectoryName = "updates";

    public const string InstallLogFileName = "install.log";

    /// <summary>The published executable's base name; the platform suffix is added by <see cref="AppExecutableName"/>.</summary>
    public const string AppExecutableBaseName = "Forge.App";

    /// <summary>The extracted package inside a staged version directory; installed trees are moved from here.</summary>
    public const string PayloadDirectoryName = "app";

    public const string OldPrefix = ".old-";

    public const string FailedPrefix = ".failed-";

    public const string MarkerPrefix = "ok-";

    /// <summary>
    /// A backup that a restore has claimed carries this prefix and encodes the version it belongs to:
    /// <c>&lt;install&gt;.restoring-&lt;version&gt;-&lt;guid&gt;</c>. The version is part of the name because a crash between
    /// the claim and the final move must not hide which backup the orphan holds — the startup repair puts it back under
    /// <see cref="OldPrefix"/>. The claimed name matches neither the <see cref="OldPrefix"/> nor the <see cref="FailedPrefix"/>
    /// cleanup patterns, so an orphan is never deleted.
    /// </summary>
    public const string RestoringPrefix = ".restoring-";

    /// <summary>
    /// The neutral root an applier process runs from, below <c>&lt;data&gt;/updates/</c>: <c>.applier-&lt;guid&gt;</c>. An
    /// applier renames the staging tree (forward) or the <c>.old-&lt;version&gt;</c> backup (rollback), and Windows refuses to
    /// move a directory that holds the image of a running process, so the applier must not live in either of them. A copy is
    /// made per launch and removed by the next start's housekeeping.
    /// </summary>
    public const string ApplierPrefix = ".applier-";

    /// <summary>A version token longer than this is not a version; the bound keeps marker and backup names sane.</summary>
    public const int MaxVersionLength = 64;

    /// <summary>Windows paths are case-insensitive; every other platform compares them byte for byte.</summary>
    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>The executable file name of the published app on this platform (<c>Forge.App.exe</c> / <c>Forge.App</c>).</summary>
    public static string AppExecutableName => OperatingSystem.IsWindows() ? AppExecutableBaseName + ".exe" : AppExecutableBaseName;

    public static string UpdatesRoot(string dataRoot) => Path.Combine(dataRoot, UpdatesDirectoryName);

    public static string InstallLog(string dataRoot) => Path.Combine(UpdatesRoot(dataRoot), InstallLogFileName);

    /// <summary>The <c>ok-&lt;version&gt;</c> confirmation the applier waits for; a file, not a directory.</summary>
    public static string Marker(string dataRoot, string version) =>
        Path.Combine(UpdatesRoot(dataRoot), MarkerPrefix + version);

    /// <summary>The tree that is installed: the package extracted inside the version directory.</summary>
    public static string PayloadDirectory(string versionDirectory) => Path.Combine(versionDirectory, PayloadDirectoryName);

    public static string AppExecutable(string directory) => Path.Combine(directory, AppExecutableName);

    public static string OldDirectory(string installDirectory, string version) =>
        TrimSeparator(installDirectory) + OldPrefix + version;

    public static string FailedDirectory(string installDirectory, string version) =>
        TrimSeparator(installDirectory) + FailedPrefix + version;

    /// <summary>
    /// The file name under which <paramref name="version"/>'s backup is claimed during a restore; it sits beside the install
    /// directory, like the backup it renames.
    /// </summary>
    public static string RestoringName(string installDirectory, string version, string token) =>
        Path.GetFileName(TrimSeparator(installDirectory)) + RestoringPrefix + version + "-" + token;

    /// <summary>The same name with a wildcard token, for finding the claims of one version below the install parent.</summary>
    public static string RestoringPattern(string installDirectory, string version) =>
        Path.GetFileName(TrimSeparator(installDirectory)) + RestoringPrefix + version + "-*";

    /// <summary>True when <paramref name="directory"/> is the parent whose siblings hold the backups.</summary>
    public static string? InstallParent(string installDirectory) =>
        Path.GetDirectoryName(TrimSeparator(installDirectory));

    /// <summary>Strips the trailing separators an <see cref="AppContext.BaseDirectory"/> always carries.</summary>
    public static string TrimSeparator(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    /// <summary>
    /// The version as a file-name token: the assembly <c>InformationalVersion</c> and the manifest version carry build
    /// metadata (<c>1.2.0+abc1234</c>) that is not part of the version identity, and a path separator would let a hostile
    /// manifest escape the staging directory. Both sides of the protocol run through this, so the marker the new app writes
    /// and the marker the applier waits for are the same string.
    /// </summary>
    public static bool TryVersionToken(string? raw, [NotNullWhen(true)] out string? token)
    {
        token = null;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        var value = raw.Trim();
        var metadata = value.IndexOf('+');
        if (metadata >= 0)
        {
            value = value[..metadata].Trim();
        }

        if (value.Length is 0 or > MaxVersionLength ||
            value is "." or ".." ||
            value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            value.IndexOf('/') >= 0 ||
            value.IndexOf('\\') >= 0)
        {
            return false;
        }

        token = value;
        return true;
    }

    /// <summary>True when <paramref name="candidate"/> is <paramref name="directory"/> itself or lives below it.</summary>
    public static bool IsInside(string candidate, string directory)
    {
        var child = TrimSeparator(candidate);
        var parent = TrimSeparator(directory);
        if (child.Equals(parent, PathComparison))
        {
            return true;
        }

        return child.Length > parent.Length
            && child.StartsWith(parent, PathComparison)
            && (child[parent.Length] == Path.DirectorySeparatorChar || child[parent.Length] == Path.AltDirectorySeparatorChar);
    }

    /// <summary>True when <paramref name="candidate"/> lives below <paramref name="directory"/> but is not it.</summary>
    public static bool IsStrictlyInside(string candidate, string directory) =>
        !TrimSeparator(candidate).Equals(TrimSeparator(directory), PathComparison) && IsInside(candidate, directory);
}

/// <summary>
/// The private command line of an install: the staged new <c>Forge.App</c> is started with
/// <c>--apply-update --pid &lt;old&gt; --install &lt;dir&gt; --staging &lt;dir&gt; --version &lt;v&gt;</c>, waits for the old
/// process to exit, swaps the install directory and restarts the app. <c>--recover</c> marks the rollback direction (the
/// previous install is restored from its <c>.old-&lt;version&gt;</c> backup and no confirmation is awaited).
/// <para>
/// Every path argument is validated before anything is touched. The staging tree must be inside
/// <c>&lt;data&gt;/updates</c> (the only place a package is ever unpacked) or be the <c>.old-&lt;version&gt;</c> backup of
/// this install directory (the only legitimate rollback source), the install directory must be an existing directory that
/// holds the published executable, and nothing is resolved relative to the working directory. That is what keeps a
/// hand-typed invocation from renaming an arbitrary directory, and it is the only reason this type exists rather than an
/// array of strings.
/// </para>
/// </summary>
internal sealed record UpdateApplyRequest(int OldProcessId, string InstallDirectory, string StagingDirectory, string Version)
{
    public const string ApplySwitch = "--apply-update";

    /// <summary>Passed by the applier to the new app: "you came out of a swap, confirm it once your window is up".</summary>
    public const string ConfirmedSwitch = "--update-applied";

    public const string PidSwitch = "--pid";

    public const string InstallSwitch = "--install";

    public const string StagingSwitch = "--staging";

    public const string VersionSwitch = "--version";

    public const string RecoverSwitch = "--recover";

    /// <summary>
    /// True for the rollback direction: the staging tree is the previous version's <c>.old-&lt;version&gt;</c> backup, it is
    /// installed the same way, and the applier does not wait for a confirmation marker (the restored version has nothing to
    /// confirm).
    /// </summary>
    public bool Recovery { get; init; }

    /// <summary>The backup that holds the version being replaced (or restored).</summary>
    public string OldDirectory => UpdateApplyPaths.OldDirectory(InstallDirectory, Version);

    /// <summary>The command line, exactly as <see cref="TryParse"/> reads it back.</summary>
    public IReadOnlyList<string> ToArguments()
    {
        var arguments = new List<string>(Recovery ? 12 : 11)
        {
            ApplySwitch,
            PidSwitch,
            OldProcessId.ToString(CultureInfo.InvariantCulture),
            InstallSwitch,
            InstallDirectory,
            StagingSwitch,
            StagingDirectory,
            VersionSwitch,
            Version,
        };
        if (Recovery)
        {
            arguments.Add(RecoverSwitch);
        }

        return arguments;
    }

    /// <summary>True when the process was started as the applier; the branch runs without Avalonia and without a UI.</summary>
    public static bool IsApplyCommand(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        for (var i = 0; i < args.Count; i++)
        {
            if (string.Equals(args[i], ApplySwitch, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Reads and validates the command line. Failed validation is a refusal before any file is touched: the returned reason
    /// is English and goes to <c>install.log</c> and the diagnostic log, never to the UI.
    /// </summary>
    public static bool TryParse(
        IReadOnlyList<string> args,
        string dataRoot,
        [NotNullWhen(true)] out UpdateApplyRequest? request,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);

        request = null;
        error = null;
        int? pid = null;
        string? install = null;
        string? staging = null;
        string? version = null;
        var recovery = false;
        var sawApply = false;

        for (var i = 0; i < args.Count; i++)
        {
            var argument = args[i];
            if (!argument.StartsWith("--", StringComparison.Ordinal))
            {
                // The process path of Environment.GetCommandLineArgs()[0]; everything else must be a switch.
                if (i == 0)
                {
                    continue;
                }

                error = "unexpected argument '" + argument + "'.";
                return false;
            }

            if (string.Equals(argument, ApplySwitch, StringComparison.Ordinal))
            {
                if (sawApply)
                {
                    error = "the apply switch appears twice.";
                    return false;
                }

                sawApply = true;
                continue;
            }

            if (string.Equals(argument, RecoverSwitch, StringComparison.Ordinal))
            {
                recovery = true;
                continue;
            }

            if (!TryReadValue(args, ref i, out var value, out error))
            {
                return false;
            }

            switch (argument)
            {
                case PidSwitch when pid is null:
                    if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) || parsed <= 0)
                    {
                        error = "the process id '" + value + "' is not a positive integer.";
                        return false;
                    }

                    pid = parsed;
                    break;
                case PidSwitch:
                    error = "the process id appears twice.";
                    return false;
                case InstallSwitch when install is null:
                    install = value;
                    break;
                case InstallSwitch:
                    error = "the install directory appears twice.";
                    return false;
                case StagingSwitch when staging is null:
                    staging = value;
                    break;
                case StagingSwitch:
                    error = "the staging directory appears twice.";
                    return false;
                case VersionSwitch when version is null:
                    version = value;
                    break;
                case VersionSwitch:
                    error = "the version appears twice.";
                    return false;
                default:
                    error = "unknown switch '" + argument + "'.";
                    return false;
            }
        }

        if (!sawApply)
        {
            error = "the apply switch is missing.";
            return false;
        }

        if (pid is not { } oldProcessId)
        {
            error = "the process id is missing.";
            return false;
        }

        if (!UpdateApplyPaths.TryVersionToken(version, out var token))
        {
            error = "the version '" + version + "' is not a usable file-name token.";
            return false;
        }

        if (install is null || staging is null)
        {
            error = "both the install and the staging directory are required.";
            return false;
        }

        if (!Path.IsPathFullyQualified(install) || !Path.IsPathFullyQualified(staging))
        {
            error = "the install and staging directories must be absolute paths.";
            return false;
        }

        var installDirectory = UpdateApplyPaths.TrimSeparator(install);
        var stagingDirectory = UpdateApplyPaths.TrimSeparator(staging);
        var updatesRoot = UpdateApplyPaths.UpdatesRoot(dataRoot);

        if (!Directory.Exists(installDirectory))
        {
            error = "the install directory '" + installDirectory + "' does not exist.";
            return false;
        }

        if (UpdateApplyPaths.InstallParent(installDirectory) is null)
        {
            error = "the install directory '" + installDirectory + "' is a volume root.";
            return false;
        }

        if (!File.Exists(UpdateApplyPaths.AppExecutable(installDirectory)))
        {
            error = "the install directory '" + installDirectory + "' does not hold " + UpdateApplyPaths.AppExecutableName + ".";
            return false;
        }

        if (UpdateApplyPaths.IsInside(installDirectory, dataRoot))
        {
            error = "the install directory '" + installDirectory + "' is inside the data directory.";
            return false;
        }

        if (!Directory.Exists(stagingDirectory))
        {
            error = "the staging directory '" + stagingDirectory + "' does not exist.";
            return false;
        }

        var isBackup = stagingDirectory.Equals(
            UpdateApplyPaths.OldDirectory(installDirectory, token),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        var isStaged = UpdateApplyPaths.IsStrictlyInside(stagingDirectory, updatesRoot);

        // Exactly two shapes are legitimate: a tree unpacked under <data>/updates, or this install directory's own backup
        // for the rollback direction. Anything else is a hand-typed path and is refused before a single file is touched.
        if (!(recovery ? isBackup : isStaged))
        {
            error = recovery
                ? "the recovery source '" + stagingDirectory + "' is not this install directory's .old-" + token + " backup."
                : "the staging directory '" + stagingDirectory + "' is not inside '" + updatesRoot + "'.";
            return false;
        }

        if (!File.Exists(UpdateApplyPaths.AppExecutable(stagingDirectory)))
        {
            error = "the staging directory '" + stagingDirectory + "' does not hold " + UpdateApplyPaths.AppExecutableName + ".";
            return false;
        }

        request = new UpdateApplyRequest(oldProcessId, installDirectory, stagingDirectory, token) { Recovery = recovery };
        return true;
    }

    private static bool TryReadValue(IReadOnlyList<string> args, ref int index, out string value, out string? error)
    {
        value = string.Empty;
        error = null;
        if (index + 1 >= args.Count)
        {
            error = "the switch '" + args[index] + "' has no value.";
            return false;
        }

        index++;
        value = args[index];
        return true;
    }
}

/// <summary>
/// The bounded append log of an install (<c>&lt;data&gt;/updates/install.log</c>): every step and every failure recovery is
/// written there so a failed swap can be explained afterwards, but the file never grows without limit — once it is full the
/// oldest half is dropped at a line boundary. It is a one-shot updater's log, not a hot path, so a lock and a rewrite are
/// fine.
/// </summary>
internal static class UpdateInstallLog
{
    /// <summary>Upper bound of the file; a log that matters is read by a person, not by a machine.</summary>
    public const long MaxBytes = 128 * 1024;

    private static readonly Lock Gate = new();

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>Appends one timestamped line; a failure to log never fails the install.</summary>
    public static void Append(string path, string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(message);

        var line = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture) + " " + message + "\n";
        lock (Gate)
        {
            try
            {
                var directory = Path.GetDirectoryName(Path.GetFullPath(path));
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                if (File.Exists(path) && new FileInfo(path).Length + line.Length > MaxBytes)
                {
                    Truncate(path);
                }

                File.AppendAllText(path, line, Utf8);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                AppLogs.Update.ZLogWarning($"writing the install log '{path}' failed: {exception.Message}");
            }
        }
    }

    /// <summary>Rewrites the file with its newest half, starting at a line boundary.</summary>
    private static void Truncate(string path)
    {
        var keep = (int)Math.Min(MaxBytes / 2, new FileInfo(path).Length);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        stream.Seek(-keep, SeekOrigin.End);
        var buffer = new byte[keep];
        stream.ReadExactly(buffer);

        var text = Encoding.UTF8.GetString(buffer);
        var firstLine = text.IndexOf('\n');
        if (firstLine >= 0)
        {
            text = text[(firstLine + 1)..];
        }

        File.WriteAllText(path, "(log truncated)\n" + text, Utf8);
    }
}
