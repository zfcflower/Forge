using System.Diagnostics;
using System.Text;

namespace Forge.Mcp;

public sealed class McpStdioOptions
{
    public required string Command { get; init; }

    public IReadOnlyList<string> Args { get; init; } = [];

    public IReadOnlyDictionary<string, string>? Env { get; init; }

    public string? WorkingDirectory { get; init; }

    public int StderrCapacityChars { get; init; } = 8192;

    /// <summary>Largest stdout line kept in memory; a longer line without a newline keeps only its tail.</summary>
    public int MaxStdoutLineBytes { get; init; } = 8 * 1024 * 1024;

    public TimeSpan ShutdownTimeout { get; init; } = TimeSpan.FromSeconds(2);

    public TimeSpan KillTimeout { get; init; } = TimeSpan.FromSeconds(2);
}

/// <summary>Launches the server as a child process and exchanges newline-delimited JSON over its pipes.</summary>
public sealed class StdioTransport : IMcpTransport
{
    private static readonly byte[] Newline = [(byte)'\n'];
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private const int MaxPollutionEntryBytes = 4096;

    /// <summary>How long a stdout end-of-stream waits for the child to be observed as exited before it is reported as a
    /// broken pipe. On Unix the child's pipes close during <c>exit</c>, while the parent only learns the exit code when
    /// the process is reaped afterwards, so without this window the exit code is lost to a "closed its standard output".</summary>
    private static readonly TimeSpan ExitNoticeWindow = TimeSpan.FromMilliseconds(500);

    private readonly McpStdioOptions options;
    private readonly BoundedLog stderrLog;
    private readonly BoundedLog stdoutPollution;
    private readonly int stdoutLineBytes;
    private readonly int stderrLineBytes;
    private readonly SemaphoreSlim sendLock = new(1, 1);
    private readonly Lock gate = new();
    private Run? run;
    private bool disposed;

    public StdioTransport(McpStdioOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Command);
        this.options = options;
        stderrLog = new BoundedLog(options.StderrCapacityChars);
        stdoutPollution = new BoundedLog(Math.Clamp(options.StderrCapacityChars, 512, 8192));
        stdoutLineBytes = Math.Clamp(options.MaxStdoutLineBytes, 1024, 256 * 1024 * 1024);
        stderrLineBytes = Math.Clamp(options.StderrCapacityChars, 256, 1024 * 1024);
    }

    public StdioTransport(
        string command,
        IEnumerable<string>? args = null,
        IReadOnlyDictionary<string, string>? env = null,
        string? workingDirectory = null)
        : this(new McpStdioOptions
        {
            Command = command,
            Args = args is null ? [] : [.. args],
            Env = env,
            WorkingDirectory = workingDirectory,
        })
    {
    }

    public string Name => options.Command;

    public bool IsConnected
    {
        get
        {
            lock (gate)
            {
                return run is { Exited: false };
            }
        }
    }

    public string StderrTail => stderrLog.Tail;

    public IReadOnlyList<string> StderrLines => stderrLog.Snapshot();

    /// <summary>Lines the server wrote to stdout that were not JSON-RPC messages, newest last.</summary>
    public IReadOnlyList<string> StdoutPollution => stdoutPollution.Snapshot();

    public string StdoutPollutionTail => stdoutPollution.Tail;

    public int? ProcessId
    {
        get
        {
            lock (gate)
            {
                return run?.ProcessId;
            }
        }
    }

    public event Action<ReadOnlyMemory<byte>>? MessageReceived;

    public event Action<Exception?>? Closed;

    public async Task StartAsync(CancellationToken ct)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (run is { Exited: false })
            {
                return;
            }
        }

        await StopCoreAsync(notify: false, CancellationToken.None).ConfigureAwait(false);

        var startInfo = new ProcessStartInfo
        {
            FileName = options.Command,
            WorkingDirectory = options.WorkingDirectory ?? Environment.CurrentDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Utf8,
            StandardErrorEncoding = Utf8,
        };

        foreach (var argument in options.Args)
        {
            startInfo.ArgumentList.Add(argument);
        }

        if (options.Env is { Count: > 0 } environment)
        {
            foreach (var (key, value) in environment)
            {
                startInfo.Environment[key] = value;
            }
        }

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        var current = new Run(process);
        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            process.Dispose();
            throw new McpTransportException($"Failed to start MCP server process '{options.Command}': {ex.Message}", ex);
        }

        lock (gate)
        {
            run = current;
        }

        current.StdoutTask = Task.Run(() => ReadStdoutAsync(current));
        current.StderrTask = Task.Run(() => ReadStderrAsync(current));
        current.ExitTask = Task.Run(() => MonitorExitAsync(current));
        ct.ThrowIfCancellationRequested();
    }

    public async ValueTask SendAsync(ReadOnlyMemory<byte> payload, CancellationToken ct, long? requestId = null)
    {
        Run current;
        lock (gate)
        {
            current = run ?? throw new McpConnectionClosedException($"MCP server process '{options.Command}' is not running.");
        }

        if (current.Exited)
        {
            throw new McpConnectionClosedException($"MCP server process '{options.Command}' has exited.");
        }

        try
        {
            await sendLock.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            throw new McpConnectionClosedException($"MCP server process '{options.Command}' was shut down.");
        }

        try
        {
            var stdin = current.Process.StandardInput.BaseStream;
            await stdin.WriteAsync(payload, ct).ConfigureAwait(false);
            await stdin.WriteAsync(Newline, ct).ConfigureAwait(false);
            await stdin.FlushAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            throw new McpConnectionClosedException($"Failed to write to MCP server process '{options.Command}'.", ex);
        }
        finally
        {
            sendLock.Release();
        }
    }

    public Task StopAsync(CancellationToken ct) => StopCoreAsync(notify: true, ct);

    private async Task StopCoreAsync(bool notify, CancellationToken ct)
    {
        Run? current;
        lock (gate)
        {
            current = run;
            run = null;
        }

        if (current is null)
        {
            return;
        }

        current.Graceful = true;

        try
        {
            var stdin = current.Process.StandardInput.BaseStream;
            await stdin.FlushAsync(ct).ConfigureAwait(false);
            current.Process.StandardInput.Close();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
        }

        if (!await WaitForExitAsync(current, options.ShutdownTimeout).ConfigureAwait(false))
        {
            try
            {
                current.Process.Kill(entireProcessTree: true);
            }
            catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
            {
            }

            await WaitForExitAsync(current, options.KillTimeout).ConfigureAwait(false);
        }

        current.Cancel();
        try
        {
            await current.ExitTask.WaitAsync(options.KillTimeout).ConfigureAwait(false);
        }
        catch (Exception)
        {
        }

        if (notify)
        {
            RaiseClosed(current, null, fromOwner: true);
        }
        else
        {
            Interlocked.Exchange(ref current.ClosedRaised, 1);
        }

        current.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
        }

        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        sendLock.Dispose();
    }

    private async Task ReadStdoutAsync(Run current)
    {
        var stream = current.Process.StandardOutput.BaseStream;
        var buffer = new byte[16 * 1024];
        var line = new TailLineBuffer(stdoutLineBytes);
        var token = current.Token;
        try
        {
            while (true)
            {
                var read = await stream.ReadAsync(buffer, token).ConfigureAwait(false);
                if (read <= 0)
                {
                    break;
                }

                var start = 0;
                for (var i = 0; i < read; i++)
                {
                    if (buffer[i] != (byte)'\n')
                    {
                        continue;
                    }

                    line.Append(buffer.AsSpan(start, i - start));
                    start = i + 1;
                    if (line.Length > 0)
                    {
                        Deliver(line.Memory);
                        line.Clear();
                    }
                }

                if (start < read)
                {
                    line.Append(buffer.AsSpan(start, read - start));
                }
            }

            if (line.Length > 0)
            {
                Deliver(line.Memory);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
        }
        finally
        {
            // Report a broken stdout only when the child is really still alive: a child that exited closes its pipes
            // before the parent can observe the exit, and then <see cref="MonitorExitAsync"/> owns the (more useful)
            // "exited with code N" notice.
            if (!HasExited(current) && !await WaitForExitAsync(current, ExitNoticeWindow).ConfigureAwait(false))
            {
                RaiseClosed(
                    current,
                    current.Graceful
                        ? null
                        : new McpConnectionClosedException($"MCP server process '{options.Command}' closed its standard output."));
            }
        }
    }

    private static bool HasExited(Run current)
    {
        try
        {
            return current.Process.HasExited;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            return true;
        }
    }

    private void Deliver(ReadOnlyMemory<byte> payload)
    {
        if (payload.Length == 0)
        {
            return;
        }

        var span = payload.Span;
        if (!System.Text.Unicode.Utf8.IsValid(span))
        {
            RecordPollution(span);
            return;
        }

        var handler = MessageReceived;
        if (handler is null)
        {
            return;
        }

        if (!TransportDispatch.Dispatch(payload, handler))
        {
            RecordPollution(span);
        }
    }

    private void RecordPollution(ReadOnlySpan<byte> payload)
    {
        if (payload.Length > MaxPollutionEntryBytes)
        {
            payload = payload[^MaxPollutionEntryBytes..];
        }

        string text;
        try
        {
            text = StrictUtf8.GetString(payload);
        }
        catch (DecoderFallbackException)
        {
            text = Utf8.GetString(payload);
        }

        var entry = "[stdout] " + text;
        stdoutPollution.Add(entry);
        stderrLog.Add(entry);
    }

    private async Task ReadStderrAsync(Run current)
    {
        var stream = current.Process.StandardError.BaseStream;
        var buffer = new byte[8 * 1024];
        var line = new TailLineBuffer(stderrLineBytes);
        var token = current.Token;
        try
        {
            while (true)
            {
                var read = await stream.ReadAsync(buffer, token).ConfigureAwait(false);
                if (read <= 0)
                {
                    break;
                }

                var start = 0;
                for (var i = 0; i < read; i++)
                {
                    if (buffer[i] != (byte)'\n')
                    {
                        continue;
                    }

                    line.Append(buffer.AsSpan(start, i - start));
                    start = i + 1;
                    if (line.Length > 0)
                    {
                        stderrLog.Add(DecodeLine(line.Memory.Span));
                        line.Clear();
                    }
                }

                if (start < read)
                {
                    line.Append(buffer.AsSpan(start, read - start));
                }
            }

            if (line.Length > 0)
            {
                stderrLog.Add(DecodeLine(line.Memory.Span));
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
        }
    }

    private static string DecodeLine(ReadOnlySpan<byte> line)
    {
        var end = line.Length;
        while (end > 0 && line[end - 1] is (byte)'\r' or (byte)'\n')
        {
            end--;
        }

        return Utf8.GetString(line[..end]);
    }

    private async Task MonitorExitAsync(Run current)
    {
        try
        {
            await current.Process.WaitForExitAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
        }

        current.Exited = true;
        try
        {
            await Task.WhenAll(current.StdoutTask, current.StderrTask).ConfigureAwait(false);
        }
        catch (Exception)
        {
        }

        if (current.Graceful)
        {
            RaiseClosed(current, null);
            return;
        }

        var code = TryGetExitCode(current);
        RaiseClosed(current, new McpConnectionClosedException(
            code is null
                ? $"MCP server process '{options.Command}' exited."
                : $"MCP server process '{options.Command}' exited with code {code}."));
    }

    private static int? TryGetExitCode(Run current)
    {
        try
        {
            return current.Process.ExitCode;
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
        {
            return null;
        }
    }

    private async Task<bool> WaitForExitAsync(Run current, TimeSpan timeout)
    {
        if (current.Exited)
        {
            return true;
        }

        try
        {
            using var cts = new CancellationTokenSource(timeout);
            await current.Process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return current.Exited;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            return true;
        }
    }

    /// <summary>
    /// A run that has already been replaced must not report a close to the current client, and it must not
    /// consume the one-shot flag either: the owner of the run is the only one that may report that close.
    /// </summary>
    private void RaiseClosed(Run current, Exception? error, bool fromOwner = false)
    {
        if (!fromOwner)
        {
            lock (gate)
            {
                if (!ReferenceEquals(run, current))
                {
                    return;
                }
            }
        }

        if (Interlocked.Exchange(ref current.ClosedRaised, 1) != 0)
        {
            return;
        }

        Closed?.Invoke(error);
    }

    private sealed class Run : IDisposable
    {
        private readonly CancellationTokenSource cts = new();

        public Run(Process process) => Process = process;

        public Process Process { get; }

        public int ProcessId => Process.Id;

        public volatile bool Exited;

        public volatile bool Graceful;

        public int ClosedRaised;

        public Task StdoutTask { get; set; } = Task.CompletedTask;

        public Task StderrTask { get; set; } = Task.CompletedTask;

        public Task ExitTask { get; set; } = Task.CompletedTask;

        public CancellationToken Token => cts.Token;

        public void Cancel()
        {
            try
            {
                cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        public void Dispose()
        {
            cts.Dispose();
            Process.Dispose();
        }
    }
}
