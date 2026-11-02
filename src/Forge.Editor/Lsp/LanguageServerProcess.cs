using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;

namespace Forge.Editor.Lsp;

/// <summary>How to launch a language server: an executable plus arguments and extra environment.</summary>
public sealed record LanguageServerLaunch(
    string Command,
    IReadOnlyList<string>? Arguments = null,
    IReadOnlyDictionary<string, string>? Environment = null,
    string? WorkingDirectory = null);

/// <summary>Backoff for restarting a server that crashed.</summary>
public sealed record RestartPolicy
{
    public int MaxAttempts { get; init; } = 4;

    public TimeSpan InitialDelay { get; init; } = TimeSpan.FromMilliseconds(250);

    public TimeSpan MaxDelay { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Uptime after which the attempt counter is considered reset.</summary>
    public TimeSpan StableUptime { get; init; } = TimeSpan.FromSeconds(30);
}

public sealed class LanguageServerNotFoundException(string command)
    : Exception($"Language server executable '{command}' was not found. Install it and make sure it is on PATH.")
{
    public string Command { get; } = command;
}

public sealed class LanguageServerCrashedException(string message) : Exception(message);

public sealed class LanguageServerExitedEventArgs(int? exitCode, bool expected) : EventArgs
{
    public int? ExitCode { get; } = exitCode;

    public bool Expected { get; } = expected;
}

public sealed record ProcessStreams(Stream Input, Stream Output);

/// <summary>Bounded ring of the most recent stderr lines of a server, for crash reports.</summary>
public sealed class StderrRing
{
    private readonly object _gate = new();
    private readonly Queue<string> _lines;
    private readonly int _capacity;
    private readonly int _maxChars;

    public StderrRing(int capacity = 200, int maxChars = 16 * 1024)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _capacity = capacity;
        _maxChars = maxChars;
        _lines = new Queue<string>(capacity);
    }

    public void Add(string line)
    {
        if (string.IsNullOrEmpty(line))
        {
            return;
        }

        lock (_gate)
        {
            _lines.Enqueue(line.Length > _maxChars ? line[.._maxChars] : line);
            while (_lines.Count > _capacity)
            {
                _lines.Dequeue();
            }
        }
    }

    /// <summary>Newest lines first, at most <paramref name="maxLines"/> of them.</summary>
    public IReadOnlyList<string> Tail(int maxLines = 40)
    {
        lock (_gate)
        {
            var count = Math.Min(maxLines, _lines.Count);
            var result = new string[count];
            var skip = _lines.Count - count;
            var index = 0;
            foreach (var line in _lines)
            {
                if (skip-- > 0)
                {
                    continue;
                }

                result[index++] = line;
            }

            Array.Reverse(result);
            return result;
        }
    }

    public string Text(int maxLines = 40) => string.Join(Environment.NewLine, Tail(maxLines));

    public void Clear()
    {
        lock (_gate)
        {
            _lines.Clear();
        }
    }
}

/// <summary>
/// Owns one language server child process: launch resolution, stdio pipes, stderr capture, crash detection and
/// restart with exponential backoff. Streams are handed out once per start; the caller owns protocol framing.
/// </summary>
public class LanguageServerProcess : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly int _idleExitMilliseconds;

    private Process? _process;
    private Task? _stderrPump;
    private DateTimeOffset _startedAt;
    private int _attempt;
    private bool _stopping;
    private bool _disposed;

    public LanguageServerProcess(LanguageServerLaunch launch, RestartPolicy? restartPolicy = null, StderrRing? stderr = null, int idleExitMilliseconds = 2000)
    {
        Launch = launch;
        RestartPolicy = restartPolicy ?? new RestartPolicy();
        Stderr = stderr ?? new StderrRing();
        _idleExitMilliseconds = idleExitMilliseconds;
    }

    public LanguageServerLaunch Launch { get; }

    public RestartPolicy RestartPolicy { get; }

    public StderrRing Stderr { get; }

    /// <summary>
    /// Creates the process a session of a launch uses. Null starts a child process; a host or a test installs a factory
    /// here to supply its own transport, including for sessions a <see cref="LanguageSessionPool"/> creates.
    /// </summary>
    public static Func<LanguageServerLaunch, RestartPolicy, LanguageServerProcess>? DefaultFactory { get; set; }

    public virtual bool IsRunning
    {
        get
        {
            lock (_gate)
            {
                return _process is { HasExited: false };
            }
        }
    }

    public int? ProcessId
    {
        get
        {
            lock (_gate)
            {
                return _process?.Id;
            }
        }
    }

    /// <summary>Number of consecutive crashes since the last start that stayed up for <see cref="RestartPolicy.StableUptime"/>.</summary>
    public int CrashCount => Volatile.Read(ref _attempt);

    public virtual event EventHandler<LanguageServerExitedEventArgs>? Exited;

    /// <summary>Starts the server; throws <see cref="LanguageServerNotFoundException"/> when the executable is missing.</summary>
    public Task<ProcessStreams> StartAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(Start());
    }

    public virtual ProcessStreams Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_process is { HasExited: false })
            {
                throw new InvalidOperationException("The language server is already running.");
            }

            if (_process is { } previous)
            {
                _process = null;
                previous.Dispose();
            }

            var startInfo = CreateStartInfo(Launch);
            var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            process.Exited += OnProcessExited;
            try
            {
                if (!process.Start())
                {
                    throw new LanguageServerCrashedException($"Failed to start '{Launch.Command}'.");
                }
            }
            catch
            {
                process.Dispose();
                throw;
            }

            _process = process;
            _startedAt = DateTimeOffset.UtcNow;
            _stopping = false;
            _stderrPump = Task.Run(() => PumpStderrAsync(process, _lifetime.Token));
            return new ProcessStreams(process.StandardInput.BaseStream, process.StandardOutput.BaseStream);
        }
    }

    public virtual async ValueTask StopAsync(CancellationToken ct = default)
    {
        Process? process;
        Task? pump;
        lock (_gate)
        {
            process = _process;
            pump = _stderrPump;
            _stopping = true;
        }

        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                try
                {
                    process.StandardInput.Close();
                }
                catch (IOException)
                {
                }

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(_idleExitMilliseconds);
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
        }
        finally
        {
            if (pump is not null)
            {
                try
                {
                    await pump.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
            }

            lock (_gate)
            {
                if (ReferenceEquals(_process, process))
                {
                    _process = null;
                }

                _stderrPump = null;
            }

            process.Dispose();
        }
    }

    /// <summary>Stops the current process and starts a fresh one after the backoff delay; throws when attempts are exhausted.</summary>
    public virtual async Task<ProcessStreams> RestartAsync(CancellationToken ct = default)
    {
        var uptime = DateTimeOffset.UtcNow - _startedAt;
        if (uptime >= RestartPolicy.StableUptime)
        {
            Interlocked.Exchange(ref _attempt, 0);
        }

        await StopAsync(ct).ConfigureAwait(false);

        var attempt = Interlocked.Increment(ref _attempt);
        if (attempt > RestartPolicy.MaxAttempts)
        {
            throw new LanguageServerCrashedException(
                $"'{Launch.Command}' failed to stay up after {RestartPolicy.MaxAttempts} restart attempts.{Environment.NewLine}{Stderr.Text()}");
        }

        var delay = TimeSpan.FromMilliseconds(
            Math.Min(RestartPolicy.MaxDelay.TotalMilliseconds, RestartPolicy.InitialDelay.TotalMilliseconds * Math.Pow(2, attempt - 1)));
        await Task.Delay(delay, ct).ConfigureAwait(false);
        return Start();
    }

    public virtual async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        _lifetime.Dispose();
    }

    public static string ResolveExecutable(string command)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        if (System.IO.Path.IsPathRooted(command) || command.Contains(System.IO.Path.DirectorySeparatorChar) ||
            command.Contains(System.IO.Path.AltDirectorySeparatorChar))
        {
            return File.Exists(command) ? System.IO.Path.GetFullPath(command) : throw new LanguageServerNotFoundException(command);
        }

        var extensions = OperatingSystem.IsWindows()
            ? new[] { string.Empty, ".exe", ".cmd", ".bat" }
            : new[] { string.Empty };
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in path.Split(System.IO.Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var extension in extensions)
            {
                var candidate = System.IO.Path.Combine(directory.Trim(), command + extension);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        throw new LanguageServerNotFoundException(command);
    }

    private void OnProcessExited(object? sender, EventArgs e)
    {
        int? exitCode = null;
        try
        {
            exitCode = (sender as Process)?.ExitCode;
        }
        catch (InvalidOperationException)
        {
        }

        bool expected;
        lock (_gate)
        {
            expected = _stopping;
        }

        Exited?.Invoke(this, new LanguageServerExitedEventArgs(exitCode, expected));
    }

    private async Task PumpStderrAsync(Process process, CancellationToken ct)
    {
        try
        {
            var reader = process.StandardError;
            while (!ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                if (line is null)
                {
                    return;
                }

                Stderr.Add(line);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
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
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or IOException)
        {
        }
    }

    private static ProcessStartInfo CreateStartInfo(LanguageServerLaunch launch)
    {
        var executable = ResolveExecutable(launch.Command);
        var needsShellShim = OperatingSystem.IsWindows() &&
            (executable.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || executable.EndsWith(".bat", StringComparison.OrdinalIgnoreCase));

        var startInfo = new ProcessStartInfo
        {
            FileName = needsShellShim ? "cmd.exe" : executable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        if (needsShellShim)
        {
            startInfo.Arguments = BuildBatchCommandLine(executable, launch.Arguments);
        }
        else
        {
            foreach (var argument in launch.Arguments ?? [])
            {
                startInfo.ArgumentList.Add(argument);
            }
        }

        if (!string.IsNullOrEmpty(launch.WorkingDirectory))
        {
            startInfo.WorkingDirectory = launch.WorkingDirectory;
        }

        if (launch.Environment is { Count: > 0 } environment)
        {
            foreach (var (key, value) in environment)
            {
                startInfo.Environment[key] = value;
            }
        }

        return startInfo;
    }

    private const string BatchLiteralCharacters = "#$*+-./:?@\\_";

    private static string BuildBatchCommandLine(string script, IReadOnlyList<string>? arguments)
    {
        if (script.AsSpan().IndexOfAny('"', '^', '%') >= 0 || script.EndsWith('\\'))
        {
            throw new LanguageServerCrashedException(
                $"Batch language server '{script}' cannot be launched: its path contains a quote, caret or percent or ends with a backslash.");
        }

        var builder = new StringBuilder(64 + script.Length + ((arguments?.Count ?? 0) * 8));
        builder.Append("/e:ON /v:OFF /d /c \"");
        builder.Append('"').Append(script).Append('"');
        if (arguments is not null)
        {
            foreach (var argument in arguments)
            {
                if (argument.AsSpan().IndexOfAny('\r', '\n') >= 0)
                {
                    throw new LanguageServerCrashedException(
                        $"Batch language server '{script}' cannot receive an argument containing a line break.");
                }

                builder.Append(' ');
                AppendBatchArgument(builder, argument);
            }
        }

        builder.Append('"');
        return builder.ToString();
    }

    private static void AppendBatchArgument(StringBuilder builder, string argument)
    {
        var quote = BatchArgumentNeedsQuotes(argument);
        if (quote)
        {
            builder.Append('"');
        }

        var backslashes = 0;
        foreach (var character in argument)
        {
            if (character == '\\')
            {
                backslashes++;
            }
            else
            {
                if (character == '"')
                {
                    builder.Append('\\', backslashes);
                    builder.Append('"');
                }
                else if (character == '%')
                {
                    builder.Append("%%cd:~,");
                }

                backslashes = 0;
            }

            builder.Append(character);
        }

        if (quote)
        {
            builder.Append('\\', backslashes);
            builder.Append('"');
        }
    }

    private static bool BatchArgumentNeedsQuotes(string argument)
    {
        if (argument.Length == 0 || argument[^1] == '\\')
        {
            return true;
        }

        foreach (var character in argument)
        {
            if (char.IsControl(character))
            {
                return true;
            }

            if (character < 128 && !(char.IsAsciiLetterOrDigit(character) || BatchLiteralCharacters.Contains(character)))
            {
                return true;
            }
        }

        return false;
    }
}
