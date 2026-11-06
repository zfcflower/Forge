using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace Forge.Workspace;

/// <summary>What one shell command did: exit code, whether the timeout killed it, how long it ran and the end of its combined output.</summary>
internal sealed record ShellRun(int ExitCode, bool TimedOut, TimeSpan Duration, string OutputTail);

/// <summary>
/// Runs a command line through the platform shell (<c>cmd /d /s /c</c> on Windows, <c>/bin/sh -c</c> elsewhere) in a given
/// directory. The command is the project's own (it comes from <c>.forge/screenshots.json</c> or from an approved tool call),
/// so a shell is the point: pipes, <c>&amp;&amp;</c> and environment expansion work like in a terminal. Stdin is closed at once so
/// a command that waits for input ends instead of hanging. Timeout and cancellation kill the whole process tree; cancellation
/// then throws. Only the tail of the combined stdout and stderr is kept, so a chatty build cannot grow memory.
/// </summary>
internal static class ScreenshotShell
{
    private const int TailCharacters = 4000;
    private static readonly TimeSpan PumpGrace = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan ExitGrace = TimeSpan.FromSeconds(10);

    public static async Task<ShellRun> RunAsync(
        string command,
        string workingDirectory,
        IReadOnlyDictionary<string, string> environment,
        TimeSpan timeout,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        ArgumentNullException.ThrowIfNull(environment);

        ct.ThrowIfCancellationRequested();
        var stopwatch = Stopwatch.StartNew();
        Process process;
        try
        {
            process = Process.Start(CreateStartInfo(command, workingDirectory, environment))
                ?? throw new InvalidOperationException("The shell process did not start.");
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            return new ShellRun(-1, false, stopwatch.Elapsed, "Could not start the shell: " + exception.Message);
        }

        using (process)
        {
            CloseInput(process);

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linked.CancelAfter(timeout);
            using var killOnCancel = linked.Token.Register(static state => TryKill((Process)state!), process);

            var tail = new TailBuffer(TailCharacters);
            using var pumpStop = new CancellationTokenSource();
            var pumps = Task.WhenAll(
                PumpAsync(process.StandardOutput, tail, pumpStop.Token),
                PumpAsync(process.StandardError, tail, pumpStop.Token));

            var timedOut = false;
            try
            {
                await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                timedOut = !ct.IsCancellationRequested;
            }

            if (linked.IsCancellationRequested)
            {
                // The registration above already killed the tree; wait until the exit is really visible.
                try
                {
                    await process.WaitForExitAsync(CancellationToken.None).WaitAsync(ExitGrace).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                }
            }

            // A background process the command started may keep the pipes open; the grace period bounds the wait for it.
            pumpStop.CancelAfter(PumpGrace);
            await pumps.ConfigureAwait(false);

            ct.ThrowIfCancellationRequested();
            return new ShellRun(timedOut ? -1 : ExitCodeOf(process), timedOut, stopwatch.Elapsed, tail.Snapshot());
        }
    }

    private static ProcessStartInfo CreateStartInfo(string command, string workingDirectory, IReadOnlyDictionary<string, string> environment)
    {
        var info = new ProcessStartInfo
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        if (OperatingSystem.IsWindows())
        {
            // cmd takes the command line verbatim: /s strips exactly the outer quotes, so quotes inside the command survive.
            // ArgumentList would escape them and break cmd's own parsing.
            info.FileName = Environment.GetEnvironmentVariable("ComSpec") is { Length: > 0 } comspec ? comspec : "cmd.exe";
            info.Arguments = "/d /s /c \"" + command + "\"";
        }
        else
        {
            info.FileName = "/bin/sh";
            info.ArgumentList.Add("-c");
            info.ArgumentList.Add(command);
        }

        foreach (var name in GitCli.RepositoryEnvironmentVariables)
        {
            info.Environment.Remove(name);
        }

        info.Environment["GIT_TERMINAL_PROMPT"] = "0";
        foreach (var pair in environment)
        {
            info.Environment[pair.Key] = pair.Value;
        }

        return info;
    }

    private static void CloseInput(Process process)
    {
        try
        {
            process.StandardInput.Close();
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or ObjectDisposedException)
        {
        }
    }

    private static async Task PumpAsync(StreamReader reader, TailBuffer tail, CancellationToken ct)
    {
        var buffer = new char[4096];
        try
        {
            while (true)
            {
                var read = await reader.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
                if (read == 0)
                {
                    return;
                }

                tail.Append(buffer.AsSpan(0, read));
            }
        }
        catch (Exception exception) when (exception is OperationCanceledException or IOException or ObjectDisposedException or InvalidOperationException)
        {
        }
    }

    private static int ExitCodeOf(Process process)
    {
        try
        {
            return process.ExitCode;
        }
        catch (InvalidOperationException)
        {
            return -1;
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException or Win32Exception or ObjectDisposedException)
        {
        }
    }

    /// <summary>The last characters written; older output is dropped in blocks so appending stays cheap.</summary>
    private sealed class TailBuffer(int limit)
    {
        private readonly object gate = new();
        private readonly StringBuilder text = new();
        private bool dropped;

        public void Append(ReadOnlySpan<char> chunk)
        {
            lock (gate)
            {
                text.Append(chunk);
                if (text.Length > limit * 2)
                {
                    text.Remove(0, text.Length - limit);
                    dropped = true;
                }
            }
        }

        public string Snapshot()
        {
            lock (gate)
            {
                var truncated = dropped || text.Length > limit;
                var value = text.Length > limit ? text.ToString(text.Length - limit, limit) : text.ToString();
                value = value.Trim();
                return truncated && value.Length > 0 ? "..." + value : value;
            }
        }
    }
}
