using System.Text;
using Forge.Ipc;
using Forge.Ipc.Protocol;

namespace Forge.Integration.Tests.Terminal;

/// <summary>Shells the terminal tests drive. Fast shells keep start-up cheap; the rich shell
/// (PowerShell on Windows) is used where the test reads back window sizes or process ids.</summary>
internal static class TerminalShells
{
    /// <summary>A shell that starts quickly: <c>%COMSPEC%</c> on Windows, <c>/bin/sh</c> on POSIX.</summary>
    public static string Fast =>
        OperatingSystem.IsWindows()
            ? Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe"
            : "/bin/sh";

    /// <summary>A shell that can report its window size and spawn a child whose pid it prints.</summary>
    public static string Rich()
    {
        if (!OperatingSystem.IsWindows())
        {
            return "/bin/sh";
        }

        // Same rule as the product default: Store app execution aliases are skipped because their children escape the
        // terminal's job.
        foreach (var candidate in FindOnPath("pwsh.exe"))
        {
            return candidate;
        }

        var programFiles = Path.Combine(
            Environment.GetEnvironmentVariable("ProgramFiles") ?? @"C:\Program Files", "PowerShell", "7", "pwsh.exe");
        if (File.Exists(programFiles))
        {
            return programFiles;
        }

        var systemRoot = Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows";
        var desktop = Path.Combine(systemRoot, "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
        return File.Exists(desktop)
            ? desktop
            : Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe";
    }

    private static bool IsAppExecutionAlias(string path)
    {
        var localAppData = Environment.GetEnvironmentVariable("LOCALAPPDATA");
        if (string.IsNullOrWhiteSpace(localAppData))
        {
            return false;
        }

        var aliasDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(localAppData, "Microsoft", "WindowsApps")));
        for (var directory = Path.GetDirectoryName(Path.GetFullPath(path)); directory is not null; directory = Path.GetDirectoryName(directory))
        {
            if (string.Equals(directory, aliasDirectory, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<string> FindOnPath(string fileName)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate;
            try
            {
                candidate = Path.Combine(directory.Trim('"'), fileName);
            }
            catch (ArgumentException)
            {
                continue;
            }

            if (File.Exists(candidate) && !IsAppExecutionAlias(candidate))
            {
                yield return candidate;
                yield break;
            }
        }
    }
}

/// <summary>One output frame as the test saw it, in arrival order.</summary>
internal sealed record TerminalFrame(long Seq, byte[] Data, long Ticks);

/// <summary>Polling waits with explicit budgets. Mirrors the style of the existing
/// integration tests: deadline-based polling, never an open-ended wait.</summary>
internal static class TerminalWait
{
    public static async Task WaitUntilAsync(Func<bool> condition, string what, TimeSpan limit, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(limit);
        while (!condition())
        {
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(20), deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException($"Timed out after {limit.TotalSeconds:0} s waiting for {what}.");
            }
        }
    }

    public static async Task WaitUntilAsync(Func<Task<bool>> condition, string what, TimeSpan limit, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(limit);
        while (!await condition().ConfigureAwait(false))
        {
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100), deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException($"Timed out after {limit.TotalSeconds:0} s waiting for {what}.");
            }
        }
    }

    /// <summary>Waits until the process is gone from the process table. This is the repository's
    /// established "process really disappeared" verifier (see <c>ProcessOwnershipTests</c>): it polls
    /// until the pid can no longer be opened, it does not trust a single <c>HasExited</c> read.</summary>
    public static async Task WaitForDeathAsync(int processId, string what, CancellationToken ct)
    {
        var limit = TimeSpan.FromSeconds(15);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(limit);
        while (E2eFixture.IsRunning(processId))
        {
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(25), deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException($"{what} (pid {processId}) was still running {limit.TotalSeconds:0} s after it should have exited.");
            }
        }
    }

    /// <summary>Waits for a shell to write a child's pid into <paramref name="fileName"/> under
    /// <paramref name="directory"/>. The file may be seen before its content is complete, so an unreadable
    /// or non-positive pid is polled again instead of failing.</summary>
    public static async Task<int> WaitForPidFileAsync(string directory, string fileName, TimeSpan limit, CancellationToken ct)
    {
        var marker = Path.Combine(directory, fileName);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(limit);
        while (true)
        {
            if (File.Exists(marker))
            {
                try
                {
                    if (int.TryParse(File.ReadAllText(marker).Trim(), out var pid) && pid > 0)
                    {
                        return pid;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // The shell still holds the file open for writing; keep polling.
                }
            }

            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50), deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException($"Timed out after {limit.TotalSeconds:0} s waiting for the pid file '{marker}'.");
            }
        }
    }
}

/// <summary>A real Extension Host process driven over a raw <see cref="IpcPeer"/> with the
/// <c>terminal.*</c> wire protocol. Output and exit notifications are captured per terminal id
/// with arrival timestamps, so tests can assert sequence continuity, byte order and the
/// exit-before-stop-answer ordering. Acknowledgement is explicit: <see cref="AutoAck"/> sends
/// one <c>terminal.ack</c> per frame from the notification handler, while flow-control tests
/// turn it off and ack by hand.</summary>
internal sealed class RawTerminalHost : IAsyncDisposable
{
    private readonly PluginQueueBudgetHost host;
    private readonly IpcPeer peer;
    private readonly Lock gate = new();
    private readonly Dictionary<string, List<TerminalFrame>> frames = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (int ExitCode, long Ticks)> exits = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<string>> events = new(StringComparer.Ordinal);
    private readonly List<string> problems = [];
    private readonly List<string> started = [];
    private bool disposed;

    private RawTerminalHost(PluginQueueBudgetHost host, IpcPeer peer)
    {
        this.host = host;
        this.peer = peer;
        peer.NotificationReceived += OnNotification;
    }

    public bool AutoAck { get; set; } = true;

    public string Root => host.Root;

    public IReadOnlyList<string> HostOutput => host.Output;

    public static async Task<RawTerminalHost> StartAsync(CancellationToken ct)
    {
        var host = PluginQueueBudgetHost.Start([], "terminal");
        try
        {
            var peer = await ConnectAsync(host, ct).ConfigureAwait(false);
            return new RawTerminalHost(host, peer);
        }
        catch
        {
            await host.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Connects to the host with the same 60 s budget the fixture clients use for bringing a
    /// host up, and fails fast when the host process is already gone instead of retrying the pipe for
    /// the whole test budget. A host that is merely slow still gets the full minute; one that never
    /// listens is reported with its output instead of surfacing as a bare cancellation.</summary>
    private static async Task<IpcPeer> ConnectAsync(PluginQueueBudgetHost host, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(60));
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (!E2eFixture.IsRunning(host.ProcessId))
            {
                throw new InvalidOperationException(
                    $"The Extension Host (pid {host.ProcessId}) exited before accepting a connection. " +
                    $"Output: {TailOutput(host)}.");
            }

            var transport = new NamedPipeClientTransport(
                host.Pipe, new IpcPipeOptions { ConnectTimeout = TimeSpan.FromSeconds(2) });
            try
            {
                await transport.StartAsync(deadline.Token).ConfigureAwait(false);
                var peer = new IpcPeer(transport, new IpcPeerOptions { DefaultTimeout = TimeSpan.FromSeconds(60) });
                await peer.StartAsync(deadline.Token).ConfigureAwait(false);
                return peer;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                await transport.DisposeAsync().ConfigureAwait(false);
                throw;
            }
            catch (Exception ex) when (ex is IOException or TimeoutException or InvalidOperationException or OperationCanceledException)
            {
                await transport.DisposeAsync().ConfigureAwait(false);
                if (deadline.Token.IsCancellationRequested && !ct.IsCancellationRequested)
                {
                    throw new TimeoutException(
                        $"The Extension Host (pid {host.ProcessId}) did not accept a pipe connection within 60 s. " +
                        $"Output: {TailOutput(host)}.");
                }

                try
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(50), deadline.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw new TimeoutException(
                        $"The Extension Host (pid {host.ProcessId}) did not accept a pipe connection within 60 s. " +
                        $"Output: {TailOutput(host)}.");
                }
            }
        }
    }

    private static string TailOutput(PluginQueueBudgetHost host)
    {
        var lines = host.Output;
        return lines.Count == 0 ? "<no output>" : string.Join(" | ", lines.TakeLast(5));
    }

    public async Task<(string TerminalId, int ProcessId, string Shell)> StartTerminalAsync(
        string? shell,
        string[]? arguments = null,
        int columns = 80,
        int rows = 24,
        Dictionary<string, string>? environment = null,
        CancellationToken ct = default)
    {
        var response = await peer.RequestAsync(
            TerminalWire.Start,
            TerminalWire.StartArgs(Root, columns, rows, shell, arguments, environment),
            ct).ConfigureAwait(false);
        var (terminalId, processId, resolved) = TerminalWire.ReadStartResult(response);
        lock (gate)
        {
            started.Add(terminalId);
        }

        return (terminalId, processId, resolved);
    }

    public Task SendInputAsync(string terminalId, string text, CancellationToken ct = default)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        return peer.NotifyAsync(TerminalWire.Input, TerminalWire.InputArgs(terminalId, bytes), ct);
    }

    public Task SendInputBytesAsync(string terminalId, byte[] bytes, CancellationToken ct = default) =>
        peer.NotifyAsync(TerminalWire.Input, TerminalWire.InputArgs(terminalId, bytes), ct);

    public Task AckAsync(string terminalId, long bytes, CancellationToken ct = default) =>
        peer.NotifyAsync(TerminalWire.Ack, TerminalWire.AckArgs(terminalId, bytes), ct);

    public async Task ResizeAsync(string terminalId, int columns, int rows, CancellationToken ct = default)
    {
        await peer.RequestAsync(TerminalWire.Resize, TerminalWire.ResizeArgs(terminalId, columns, rows), ct)
            .ConfigureAwait(false);
    }

    public async Task<bool> ForegroundAsync(string terminalId, CancellationToken ct = default)
    {
        var response = await peer.RequestAsync(TerminalWire.Foreground, TerminalWire.IdArgs(terminalId), ct)
            .ConfigureAwait(false);
        return TerminalWire.ReadBusy(response);
    }

    public Task<IpcMessage> StopAsync(string terminalId, CancellationToken ct = default) =>
        peer.RequestAsync(TerminalWire.Stop, TerminalWire.IdArgs(terminalId), ct);

    public Task<IpcMessage> StopHostAsync(CancellationToken ct = default) =>
        peer.RequestAsync(Forge.Plugin.Sdk.ExtensionProtocol.HostStop, IpcEmpty.Instance, ct);

    public Task<int> WaitForExitAsync(CancellationToken ct) => host.WaitForExitAsync(ct);

    public long? ExitTicks(string terminalId)
    {
        lock (gate)
        {
            return exits.TryGetValue(terminalId, out var exit) ? exit.Ticks : null;
        }
    }

    public int? ExitCodeOf(string terminalId)
    {
        lock (gate)
        {
            return exits.TryGetValue(terminalId, out var exit) ? exit.ExitCode : null;
        }
    }

    public IReadOnlyList<TerminalFrame> FramesOf(string terminalId)
    {
        lock (gate)
        {
            return frames.TryGetValue(terminalId, out var list) ? [.. list] : [];
        }
    }

    public IReadOnlyList<string> EventsOf(string terminalId)
    {
        lock (gate)
        {
            return events.TryGetValue(terminalId, out var list) ? [.. list] : [];
        }
    }

    public long ByteCountOf(string terminalId)
    {
        lock (gate)
        {
            return frames.TryGetValue(terminalId, out var list) ? list.Sum(frame => (long)frame.Data.Length) : 0;
        }
    }

    public string TextOf(string terminalId)
    {
        lock (gate)
        {
            if (!frames.TryGetValue(terminalId, out var list))
            {
                return string.Empty;
            }

            var total = list.Sum(frame => frame.Data.Length);
            var merged = new byte[total];
            var offset = 0;
            foreach (var frame in list)
            {
                Buffer.BlockCopy(frame.Data, 0, merged, offset, frame.Data.Length);
                offset += frame.Data.Length;
            }

            return Encoding.UTF8.GetString(merged);
        }
    }

    public IReadOnlyList<string> Problems()
    {
        lock (gate)
        {
            return [.. problems];
        }
    }

    public async Task<string> WaitForTextAsync(string terminalId, string needle, TimeSpan limit, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(limit);
        while (true)
        {
            var text = TextOf(terminalId);
            if (text.Contains(needle, StringComparison.Ordinal))
            {
                return text;
            }

            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50), deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"Timed out after {limit.TotalSeconds:0} s waiting for terminal '{terminalId}' to show '{needle}'. Saw: '{Tail(text)}'.");
            }
        }
    }

    public async Task<int> WaitForTerminalExitAsync(string terminalId, TimeSpan limit, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(limit);
        while (true)
        {
            int? code;
            lock (gate)
            {
                code = exits.TryGetValue(terminalId, out var exit) ? exit.ExitCode : null;
            }

            if (code.HasValue)
            {
                return code.Value;
            }

            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(25), deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException($"Timed out after {limit.TotalSeconds:0} s waiting for terminal '{terminalId}' to exit.");
            }
        }
    }

    private static string Tail(string text) => text.Length <= 400 ? text : text[^400..];

    private void OnNotification(IpcMessage message)
    {
        try
        {
            if (string.Equals(message.Operation, TerminalWire.Output, StringComparison.Ordinal))
            {
                var (terminalId, seq, data) = TerminalWire.ReadOutput(message);
                lock (gate)
                {
                    if (!frames.TryGetValue(terminalId, out var list))
                    {
                        frames[terminalId] = list = [];
                    }

                    list.Add(new TerminalFrame(seq, data, DateTime.UtcNow.Ticks));
                    EventLocked(terminalId, $"output seq {seq} ({data.Length} bytes)");
                }

                if (AutoAck && data.Length > 0)
                {
                    _ = AckAsync(terminalId, data.Length);
                }
            }
            else if (string.Equals(message.Operation, TerminalWire.Exit, StringComparison.Ordinal))
            {
                var (terminalId, exitCode) = TerminalWire.ReadExit(message);
                lock (gate)
                {
                    exits[terminalId] = (exitCode, DateTime.UtcNow.Ticks);
                    EventLocked(terminalId, $"exit {exitCode}");
                }
            }
        }
        catch (Exception exception)
        {
            lock (gate)
            {
                problems.Add($"notification '{message.Operation}' was unreadable: {exception.Message}");
            }
        }
    }

    private void EventLocked(string terminalId, string what)
    {
        if (!events.TryGetValue(terminalId, out var list))
        {
            events[terminalId] = list = [];
        }

        list.Add(what);
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        List<string> ids;
        lock (gate)
        {
            ids = [.. started];
        }

        foreach (var id in ids)
        {
            try
            {
                using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await StopAsync(id, budget.Token).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Best effort: the host teardown below kills whatever is left.
            }
        }

        try
        {
            await peer.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
        }

        await peer.DisposeAsync().ConfigureAwait(false);
        await host.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>Commands that start a 60 s sleeper child inside the terminal's shell and write its pid to a
/// marker file. The child outlives any parent-only kill, so a test that waits for the pid file and then
/// for the child's death proves the whole tree was taken.</summary>
internal static class TerminalSleeper
{
    /// <summary>The sleeper command for the current platform. On Windows the child inherits the shell's
    /// private Job Object, so killing the job kills it too; on POSIX the background job owns a process group
    /// of its own inside the shell's session, which only a session-wide sweep takes.</summary>
    public static string SpawnCommand(string pidFile) =>
        OperatingSystem.IsWindows()
            ? "$p = Start-Process -FilePath powershell.exe -ArgumentList '-NoExit','-Command','Start-Sleep 60' -PassThru; " +
              "$p.Id | Out-File -Encoding ascii '" + pidFile.Replace('\\', '/') + "'\r"
            : "sleep 60 & echo $! > '" + pidFile + "'\r";
}

/// <summary>The numbered-line flood the output and flow-control tests push through a shell. Every line has
/// the same fixed-length body, so a gap, duplicate or reorder shows up in both the byte count and the text.</summary>
internal static class TerminalFlood
{
    public const string LineBody = "xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx";

    /// <summary>Prints <c>LINE-0000001</c> … <c>LINE-&lt;lines&gt;</c> followed by the done marker.</summary>
    public static string Command(int lines) =>
        OperatingSystem.IsWindows()
            ? $"$n = {lines}; 1..$n | % {{ 'LINE-{{0:D7}}-{LineBody}' -f $_ }}; \"DONE-COUNT-$n\"\r"
            : $"n={lines}; i=1; while [ $i -le $n ]; do printf 'LINE-%07d-{LineBody}\\n' \"$i\"; i=$((i+1)); done; echo \"DONE-COUNT-$i\"\r";

    /// <summary>The marker the flood prints after its last line. POSIX shells print it after the loop
    /// counter has been incremented once more, so its number differs from Windows by one.</summary>
    public static string DoneMarker(int lines) =>
        OperatingSystem.IsWindows() ? $"DONE-COUNT-{lines}" : $"DONE-COUNT-{lines + 1}";
}
