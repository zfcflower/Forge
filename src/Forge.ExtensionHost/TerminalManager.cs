using System.Collections;
using System.Text.Json;

namespace Forge.ExtensionHost;

/// <summary>
/// The host's table of running terminals and the <c>terminal.*</c> operations that act on it: starting a shell on a fresh
/// pseudo-terminal, relaying input and acknowledgements, resizing, answering the foreground question, stopping a terminal
/// and killing every terminal when the host shuts down.
/// <para>
/// Ordering rules that the wire protocol depends on: <c>terminal.start</c> registers its session before it answers, so an
/// <c>terminal.input</c> notification that follows the answer always finds it; <c>terminal.stop</c> sends the id's
/// <c>terminal.exit</c> notification before it answers, so a caller that got the answer knows the tree is gone; and a
/// shutdown kills the terminals without sending anything, because the connection is what ends them.
/// </para>
/// <para>
/// The registry lock is only ever held for dictionary work - never across an await, a spawn or a PTY call - so a stop of
/// one terminal cannot hold up a start of another.
/// </para>
/// </summary>
internal sealed class TerminalManager : ITerminalNotifier, IAsyncDisposable
{
    /// <summary>Concurrent terminals one host serves. Above this, <c>terminal.start</c> answers <c>terminal_limit</c>.</summary>
    internal const int MaxTerminals = 32;

    /// <summary>Largest accepted column and row count of one terminal.</summary>
    internal const int MaxColumns = 1000;

    internal const int MaxRows = 500;

    /// <summary>How long the shutdown gives the terminals it just killed to be verified as gone.</summary>
    private static readonly TimeSpan ShutdownWindow = TimeSpan.FromSeconds(20);

    private readonly HostStopSignal stop;
    private readonly HostCleanupReport cleanup;
    private readonly Func<string, JsonElement, CancellationToken, Task<bool>> send;
    private readonly Dictionary<string, TerminalSession> sessions = new(StringComparer.Ordinal);
    private readonly Lock sync = new();
    private int starting;
    private int disposed;

    internal TerminalManager(
        HostStopSignal stop,
        HostCleanupReport cleanup,
        Func<string, JsonElement, CancellationToken, Task<bool>> send)
    {
        this.stop = stop;
        this.cleanup = cleanup;
        this.send = send;
    }

    /// <summary>The number of registered terminals; for diagnostics and tests.</summary>
    internal int Count
    {
        get
        {
            lock (sync)
            {
                return sessions.Count;
            }
        }
    }

    /// <summary>
    /// A closing host sends no frames at all: the connection ending is how a caller learns the terminals are gone, and a
    /// half-written notification would only be noise.
    /// </summary>
    public Task<bool> SendAsync(string operation, JsonElement payload, CancellationToken ct) =>
        stop.IsStopping ? Task.FromResult(false) : send(operation, payload, ct);

    /// <summary>
    /// Starts one terminal: validates the request, resolves the shell and the environment, creates the pseudo-terminal and
    /// registers the session. Every failure mode the contract names is answered with its own code, and a start that fails
    /// leaves nothing running.
    /// </summary>
    internal Task<JsonElement> Start(TerminalStartRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.WorkingDirectory) || !Path.IsPathFullyQualified(request.WorkingDirectory))
        {
            throw new HostOperationException(
                "invalid_request",
                "terminal.start requires workingDirectory to be an absolute path.");
        }

        if (!Directory.Exists(request.WorkingDirectory))
        {
            throw new HostOperationException(
                "invalid_request",
                $"terminal.start requires an existing workingDirectory; '{request.WorkingDirectory}' does not exist.");
        }

        if (request.Columns is < 1 or > MaxColumns || request.Rows is < 1 or > MaxRows)
        {
            throw new HostOperationException(
                "invalid_request",
                $"terminal.start requires columns in 1-{MaxColumns} and rows in 1-{MaxRows}.");
        }

        var (shell, arguments) = ResolveShell(request);
        var environment = BuildEnvironment(request.Environment);

        // The slot is reserved under the lock so concurrent starts cannot pass the limit together, and released in the
        // finally whether the pseudo-terminal was created or not. The spawn itself runs outside the lock.
        lock (sync)
        {
            if (sessions.Count + starting >= MaxTerminals)
            {
                throw new HostOperationException(
                    "terminal_limit",
                    $"The host already serves {MaxTerminals} terminals; stop one before starting another.");
            }

            starting++;
        }

        WorkerProcessOwnership.PtyProcess process;
        try
        {
            process = WorkerProcessOwnership.StartTerminal(
                shell,
                arguments,
                request.WorkingDirectory,
                environment,
                request.Columns,
                request.Rows);
        }
        catch (HostOperationException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new HostOperationException("terminal_start_failed", $"The terminal could not be started: {exception.Message}");
        }
        finally
        {
            lock (sync)
            {
                starting--;
            }
        }

        var id = Guid.NewGuid().ToString("N");
        var session = new TerminalSession(id, process, this, cleanup, Remove);
        var refused = false;
        lock (sync)
        {
            // The shutdown fence can go up while the pseudo-terminal is being created; a terminal registered after the
            // shutdown snapshot was taken would never be killed, so it is refused and torn down here instead.
            if (Volatile.Read(ref disposed) != 0 || stop.IsStopping)
            {
                refused = true;
            }
            else
            {
                sessions[id] = session;
            }
        }

        if (refused)
        {
            _ = session.StopAsync();
            throw new HostOperationException(
                "host_stopping",
                $"The extension host is stopping; terminal '{id}' was not started.");
        }

        return Task.FromResult(
            TerminalProtocol.Serialize(
                new TerminalStartResponse(id, session.ProcessId, shell),
                TerminalJsonContext.Default.TerminalStartResponse));
    }

    /// <summary>
    /// Answers <c>terminal.resize</c>. An unknown or already exited id is success: the caller cannot tell the difference
    /// between a lost race with the exit and a terminal that was never there, and neither is worth an error.
    /// </summary>
    internal Task<JsonElement> Resize(TerminalResizeRequest request)
    {
        if (request.Columns is < 1 or > MaxColumns || request.Rows is < 1 or > MaxRows)
        {
            throw new HostOperationException(
                "invalid_request",
                $"terminal.resize requires columns in 1-{MaxColumns} and rows in 1-{MaxRows}.");
        }

        Find(request.TerminalId)?.Resize(request.Columns, request.Rows);
        return Task.FromResult(HostJson.EmptyObject);
    }

    /// <summary>Answers <c>terminal.foreground</c>; an unknown id has no foreground child.</summary>
    internal Task<JsonElement> Foreground(TerminalForegroundRequest request)
    {
        var busy = Find(request.TerminalId)?.HasForegroundChild() ?? false;
        return Task.FromResult(
            TerminalProtocol.Serialize(
                new TerminalForegroundResponse(busy),
                TerminalJsonContext.Default.TerminalForegroundResponse));
    }

    /// <summary>
    /// Answers <c>terminal.stop</c> after the terminal is over, which means after its <c>terminal.exit</c> notification was
    /// written. Idempotent: a second stop of the same id, or a stop that raced the shell's own exit, awaits the same
    /// single-flight shutdown.
    /// </summary>
    internal async Task<JsonElement> StopAsync(TerminalStopRequest request)
    {
        if (Find(request.TerminalId) is { } session)
        {
            await session.StopAsync().ConfigureAwait(false);
        }

        return HostJson.EmptyObject;
    }

    /// <summary>Queues <c>terminal.input</c> bytes for the terminal; an unknown or exited id is silently ignored.</summary>
    internal void WriteInput(TerminalInputNotification notification)
    {
        if (string.IsNullOrEmpty(notification.Data) || Find(notification.TerminalId) is not { } session)
        {
            return;
        }

        byte[] data;
        try
        {
            data = Convert.FromBase64String(notification.Data);
        }
        catch (FormatException)
        {
            return;
        }

        session.WriteInput(data);
    }

    /// <summary>Applies <c>terminal.ack</c>, which reopens the flow-control window of the terminal.</summary>
    internal void Acknowledge(TerminalAckNotification notification) =>
        Find(notification.TerminalId)?.Acknowledge(notification.Bytes);

    /// <summary>
    /// Kills every terminal the host still serves: the main-program pipe closing, <c>host.stop</c> and
    /// <see cref="ExtensionHost.DisposeAsync"/> all end here. Nothing is sent - the host is stopping - and a terminal whose
    /// tree cannot be verified as gone within the window is recorded in the cleanup report instead of being reported as a
    /// clean stop.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        TerminalSession[] running;
        lock (sync)
        {
            running = [.. sessions.Values];
            sessions.Clear();
        }

        if (running.Length == 0)
        {
            return;
        }

        var stopping = running.Select(static session => session.StopAsync()).ToArray();
        try
        {
            await Task.WhenAll(stopping).WaitAsync(ShutdownWindow).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            cleanup.Record(
                $"{stopping.Length} terminal(s) did not finish closing within the {ShutdownWindow.TotalSeconds:0} s shutdown "
                + "window; the host cannot verify that their process trees are gone");
        }
    }

    private TerminalSession? Find(string? terminalId)
    {
        if (string.IsNullOrEmpty(terminalId))
        {
            return null;
        }

        lock (sync)
        {
            return sessions.TryGetValue(terminalId, out var session) ? session : null;
        }
    }

    /// <summary>Drops a terminal whose shutdown finished, so the registry never grows past the host's live terminals.</summary>
    private void Remove(TerminalSession session)
    {
        lock (sync)
        {
            if (sessions.TryGetValue(session.Id, out var registered) && ReferenceEquals(registered, session))
            {
                sessions.Remove(session.Id);
            }
        }
    }

    /// <summary>
    /// Resolves the shell the request names, or the platform default: on Windows the first real <c>pwsh.exe</c> on the PATH, then
    /// <c>%ProgramFiles%\PowerShell\7\pwsh.exe</c>, then the shipped Windows PowerShell, then <c>%COMSPEC%</c>. Microsoft Store app execution aliases
    /// (<c>%LOCALAPPDATA%\Microsoft\WindowsApps</c>) are skipped for the default: a packaged process's children are not
    /// created inside the private job the host puts the shell in, so stop, host shutdown and the busy probe would miss
    /// them. On POSIX <c>$SHELL</c> when it names an existing file, then
    /// <c>/bin/sh</c> with <c>-l</c>. A shell the caller named is used exactly as given, including its arguments.
    /// </summary>
    private static (string Shell, IReadOnlyList<string> Arguments) ResolveShell(TerminalStartRequest request)
    {
        var arguments = request.Arguments;
        if (!string.IsNullOrWhiteSpace(request.Shell))
        {
            // A shell the caller named is used with exactly the arguments it sent; only the executable itself is resolved,
            // so "pwsh" on the PATH works as well as a full path.
            return (RequireShellFile(request.Shell), arguments ?? []);
        }

        if (OperatingSystem.IsWindows())
        {
            var shell =
                FindOnPath("pwsh.exe", skipAppExecutionAliases: true)
                ?? ExistingFile(Path.Combine(
                    Environment.GetEnvironmentVariable("ProgramFiles") ?? @"C:\Program Files",
                    "PowerShell",
                    "7",
                    "pwsh.exe"))
                ?? ExistingFile(Path.Combine(
                    Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows",
                    "System32",
                    "WindowsPowerShell",
                    "v1.0",
                    "powershell.exe"))
                ?? Environment.GetEnvironmentVariable("COMSPEC");
            if (string.IsNullOrWhiteSpace(shell))
            {
                throw new HostOperationException(
                    "terminal_start_failed",
                    "No default shell was found: no real pwsh.exe is installed, Windows PowerShell is missing and COMSPEC is not set.");
            }

            return (RequireShellFile(shell), arguments ?? []);
        }

        var configured = Environment.GetEnvironmentVariable("SHELL");
        var posixShell = !string.IsNullOrWhiteSpace(configured) && File.Exists(configured) ? configured : "/bin/sh";
        return (RequireShellFile(posixShell), arguments ?? ["-l"]);
    }

    /// <summary>
    /// Turns a requested shell into a file this host can execute: a bare name is looked up on the PATH (the way a shell
    /// itself would look it up), and a path that does not name a file fails the start with <c>terminal_start_failed</c>
    /// instead of surfacing as an opaque CreateProcess error.
    /// </summary>
    private static string RequireShellFile(string shell)
    {
        var trimmed = shell.Trim();
        var resolved = trimmed;
        var separator = trimmed.AsSpan().IndexOfAny(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (separator < 0 && !Path.IsPathFullyQualified(trimmed))
        {
            resolved = FindOnPath(trimmed) ?? trimmed;
        }

        if (!File.Exists(resolved))
        {
            throw new HostOperationException(
                "terminal_start_failed",
                $"The terminal's shell '{shell}' does not name an executable file.");
        }

        return resolved;
    }

    /// <summary>True for a file under <c>%LOCALAPPDATA%\Microsoft\WindowsApps</c>, where Microsoft Store app execution
    /// aliases live. Decided by path only; the alias's reparse tag is not read.</summary>
    private static bool IsAppExecutionAlias(string path)
    {
        var localAppData = Environment.GetEnvironmentVariable("LOCALAPPDATA");
        if (string.IsNullOrWhiteSpace(localAppData))
        {
            return false;
        }

        try
        {
            var aliasDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(localAppData, "Microsoft", "WindowsApps")));
            var directory = Path.GetDirectoryName(Path.GetFullPath(path));
            while (directory is not null)
            {
                if (string.Equals(directory, aliasDirectory, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                directory = Path.GetDirectoryName(directory);
            }
        }
        catch (ArgumentException)
        {
        }
        catch (NotSupportedException)
        {
        }

        return false;
    }

    private static string? FindOnPath(string fileName, bool skipAppExecutionAliases = false)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

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

            if (ExistingFile(candidate) is { } found && !(skipAppExecutionAliases && IsAppExecutionAlias(found)))
            {
                return found;
            }
        }

        return null;
    }

    private static string? ExistingFile(string path) => File.Exists(path) ? path : null;

    /// <summary>
    /// The environment the shell inherits: the host's own, plus the three variables a terminal program expects, with the
    /// request's entries applied last. Names are compared the way the platform compares them - case-insensitively on
    /// Windows, ordinally on POSIX.
    /// </summary>
    private static IReadOnlyDictionary<string, string> BuildEnvironment(IReadOnlyDictionary<string, string>? overrides)
    {
        var environment = new Dictionary<string, string>(
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (DictionaryEntry variable in Environment.GetEnvironmentVariables())
        {
            if (variable.Key is string name && variable.Value is string value)
            {
                environment[name] = value;
            }
        }

        environment["TERM"] = "xterm-256color";
        environment["COLORTERM"] = "truecolor";
        environment["TERM_PROGRAM"] = "Forge";

        if (overrides is not null)
        {
            foreach (var pair in overrides)
            {
                if (!string.IsNullOrEmpty(pair.Key))
                {
                    environment[pair.Key] = pair.Value;
                }
            }
        }

        return environment;
    }
}
