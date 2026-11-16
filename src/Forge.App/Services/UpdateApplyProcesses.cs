using System.Diagnostics;
using Forge.App.Logging;
using ZLogger;

namespace Forge.App.Services;

/// <summary>
/// One process the applier started (the new app it must confirm, or the version it restores). Wrapped so the state machine
/// can be driven without spawning anything, and so the two platform facts it needs — "has it exited" and "kill it"
/// — have one implementation.
/// </summary>
internal interface IUpdateApplierProcess : IDisposable
{
    /// <summary>The executable that was started.</summary>
    string Path { get; }

    int Id { get; }

    bool HasExited { get; }

    /// <summary>The exit code; only meaningful once <see cref="HasExited"/> is true.</summary>
    int ExitCode { get; }

    /// <summary>Kills the process tree; a process that is already gone is not an error.</summary>
    void Kill();
}

/// <summary>
/// The two operating-system interactions of the applier: waiting for the process it replaces to exit, and starting the app
/// again. The default implementation is <see cref="SystemUpdateApplyProcesses"/>; a scripted one can be supplied.
/// </summary>
internal interface IUpdateApplyProcesses
{
    /// <summary>
    /// Waits for <paramref name="processId"/> to exit; true when it is gone, false when the timeout ran out. A process that
    /// no longer exists counts as exited (its process id may already be reused, which is exactly why the wait is bounded).
    /// </summary>
    Task<bool> WaitForExitAsync(int processId, TimeSpan timeout, CancellationToken ct);

    /// <summary>Starts an executable detached from this process; null when the operating system refused.</summary>
    IUpdateApplierProcess? Start(string executablePath, IReadOnlyList<string> arguments);
}

/// <summary>
/// The real processes: <see cref="Process"/> with the exit wait bounded by a timeout.
/// </summary>
internal sealed class SystemUpdateApplyProcesses : IUpdateApplyProcesses
{
    public async Task<bool> WaitForExitAsync(int processId, TimeSpan timeout, CancellationToken ct)
    {
        Process process;
        try
        {
            process = Process.GetProcessById(processId);
        }
        catch (ArgumentException)
        {
            // There is no such process any more: the one we were waiting for is gone (or was never there).
            return true;
        }

        using (process)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(timeout);
            try
            {
                await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
                return true;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return false;
            }
            catch (InvalidOperationException)
            {
                // The handle went away underneath the wait; treat it as exited and let the swap decide the rest.
                return true;
            }
        }
    }

    public IUpdateApplierProcess? Start(string executablePath, IReadOnlyList<string> arguments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentNullException.ThrowIfNull(arguments);

        var start = new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory = Path.GetDirectoryName(executablePath) ?? AppContext.BaseDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        try
        {
            var process = Process.Start(start);
            return process is null ? null : new ProcessHandle(executablePath, process);
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            return null;
        }
    }

    private sealed class ProcessHandle(string path, Process process) : IUpdateApplierProcess
    {
        public string Path { get; } = path;

        public int Id => process.Id;

        public bool HasExited => process.HasExited;

        public int ExitCode => process.ExitCode;

        public void Kill()
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
            {
                // The process exited between the check and the kill: nothing left to stop.
            }
        }

        public void Dispose() => process.Dispose();
    }
}

/// <summary>
/// Where an applier process runs from. Windows refuses to rename a directory that holds the image of a running process, and
/// renaming one directory is the applier's whole job: the staging tree in the forward direction, the
/// <c>.old-&lt;version&gt;</c> backup in the rollback direction. Started out of that tree, every forward install fell back to
/// copying the whole tree instead of a rename (and left the staging tree behind), and the rollback direction could not claim
/// its backup at all — the startup repair then failed at every launch and the app never got past it.
/// <para>
/// The applier is therefore started from a neutral copy of its own tree under <c>&lt;data&gt;/updates/.applier-&lt;guid&gt;/</c>,
/// with that directory as its working directory, so neither the staging tree, the install directory nor the backup is ever the
/// place it runs from. What is copied is the executable and the files the Native AOT publish places beside it: the managed code
/// is compiled into the image (the <c>--apply-update</c> branch never initializes Avalonia, Skia or anything else that would
/// load a native shim, and the app has no P/Invoke of its own), but an applier that cannot load one of its image's own native
/// dependencies would fail the whole install, so they come along. Only the top level of the tree is copied — subdirectories are
/// the app's data, not its image — and a symbol file is skipped because a running process never loads it.
/// </para>
/// <para>
/// The copy is left behind on purpose — a process cannot delete the directory it runs from — and
/// <see cref="UpdateApplyStartup.Maintain"/> removes the leftovers at the next start.
/// </para>
/// </summary>
internal static class UpdateApplierHost
{
    /// <summary>
    /// Copies <paramref name="sourceDirectory"/>'s executable and its sibling files into a fresh neutral root and returns it,
    /// or null (with the reason) when the copy cannot be made. The source tree itself is never modified.
    /// </summary>
    public static string? Prepare(string dataRoot, string sourceDirectory, out string? error)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDirectory);

        error = null;
        if (!File.Exists(UpdateApplyPaths.AppExecutable(sourceDirectory)))
        {
            error = "'" + sourceDirectory + "' does not hold " + UpdateApplyPaths.AppExecutableName;
            return null;
        }

        var neutral = Path.Combine(
            UpdateApplyPaths.UpdatesRoot(dataRoot),
            UpdateApplyPaths.ApplierPrefix + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(neutral);
            foreach (var file in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.TopDirectoryOnly))
            {
                if (file.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase))
                {
                    // A symbol file is never loaded by the running applier; copying it would only cost the update its size.
                    continue;
                }

                var target = Path.Combine(neutral, Path.GetFileName(file));
                File.Copy(file, target, overwrite: true);
                if (!OperatingSystem.IsWindows())
                {
                    // A copy that lost the execute bit could not be started, and the applier is exactly that file.
                    File.SetUnixFileMode(target, File.GetUnixFileMode(file));
                }
            }

            return neutral;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            error = exception.Message;
            TryDelete(neutral);
            return null;
        }
    }

    /// <summary>
    /// The start of an applier: the neutral copy of <paramref name="sourceDirectory"/> as the executable and the working
    /// directory, and <paramref name="arguments"/> as the private command line. Null (with the reason) when the copy cannot be
    /// made — the caller reports it and nothing is renamed.
    /// </summary>
    public static ProcessStartInfo? CreateStartInfo(
        string dataRoot,
        string sourceDirectory,
        IReadOnlyList<string> arguments,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        var neutral = Prepare(dataRoot, sourceDirectory, out error);
        if (neutral is null)
        {
            return null;
        }

        var start = new ProcessStartInfo
        {
            FileName = UpdateApplyPaths.AppExecutable(neutral),
            WorkingDirectory = neutral,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        return start;
    }

    private static void TryDelete(string directory)
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            AppLogs.Update.ZLogWarning($"removing the half-made applier copy '{directory}' failed: {exception.Message}");
        }
    }
}
