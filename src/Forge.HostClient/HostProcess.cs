using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace Forge.HostClient;

/// <summary>The Extension Host process this client started, plus a bounded tail of its output for failure
/// reporting. Process-tree killing is best effort: a host that ignores the protocol still gets terminated.</summary>
internal sealed class HostProcess : IAsyncDisposable
{
    private const int TailLimit = 4000;
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(2);

    private readonly Process process;
    private readonly Lock gate = new();
    private readonly StringBuilder tail = new();
    private readonly List<Task> readers = [];
    private int disposed;

    private HostProcess(Process process) => this.process = process;

    public int ProcessId => process.Id;

    public bool HasExited
    {
        get
        {
            try
            {
                return process.HasExited;
            }
            catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
            {
                return true;
            }
        }
    }

    /// <summary>Launches <c>RuntimePath HostExecutablePath HostArguments --pipe &lt;name&gt; [--plugins ..] [--data ..]</c>.</summary>
    public static HostProcess Start(HostConnection connection, ExtensionHostClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(connection.HostExecutablePath))
        {
            throw new ArgumentException("A host executable path is required to start the host.", nameof(connection));
        }

        var info = new ProcessStartInfo
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
        };

        if (string.IsNullOrWhiteSpace(connection.RuntimePath))
        {
            info.FileName = connection.HostExecutablePath;
        }
        else
        {
            info.FileName = connection.RuntimePath;
            info.ArgumentList.Add(connection.HostExecutablePath);
        }

        foreach (var argument in connection.HostArguments)
        {
            info.ArgumentList.Add(argument);
        }

        info.ArgumentList.Add("--pipe");
        info.ArgumentList.Add(connection.PipeName);
        if (options.PluginsDirectory is { Length: > 0 } plugins)
        {
            info.ArgumentList.Add("--plugins");
            info.ArgumentList.Add(plugins);
        }

        if (options.DataDirectory is { Length: > 0 } data)
        {
            info.ArgumentList.Add("--data");
            info.ArgumentList.Add(data);
        }

        var process = new Process { StartInfo = info };
        var host = new HostProcess(process);
        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException(
                    $"The Extension Host process could not be started: '{info.FileName}'.");
            }
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException or PlatformNotSupportedException)
        {
            process.Dispose();
            throw new InvalidOperationException(
                $"The Extension Host process could not be started: '{info.FileName}'. {ex.Message}", ex);
        }

        host.StartDraining();
        return host;
    }

    /// <summary>The last output of the host, for failure messages.</summary>
    public string OutputTail()
    {
        lock (gate)
        {
            return tail.ToString().Trim();
        }
    }

    /// <summary>Ends the process tree: kill when it is still running, then wait for it and its output readers.</summary>
    public async Task TerminateAsync(TimeSpan grace)
    {
        try
        {
            if (!HasExited)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or Win32Exception)
                {
                }
            }

            var bound = grace > TimeSpan.Zero ? grace : TimeSpan.FromSeconds(5);
            using var timeout = new CancellationTokenSource(bound);
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException)
            {
            }
        }
        finally
        {
            await AwaitReadersAsync().ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        await AwaitReadersAsync().ConfigureAwait(false);
        process.Dispose();
    }

    private async Task AwaitReadersAsync()
    {
        Task[] pending;
        lock (gate)
        {
            pending = [.. readers];
        }

        if (pending.Length == 0)
        {
            return;
        }

        try
        {
            await Task.WhenAll(pending).WaitAsync(DrainTimeout).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException or IOException or ObjectDisposedException)
        {
        }
    }

    private void StartDraining()
    {
        var stdout = process.StandardOutput;
        var stderr = process.StandardError;
        lock (gate)
        {
            readers.Add(Task.Run(() => DrainAsync(stdout)));
            readers.Add(Task.Run(() => DrainAsync(stderr)));
        }
    }

    private async Task DrainAsync(StreamReader reader)
    {
        try
        {
            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                Append(line);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException or OperationCanceledException)
        {
        }
    }

    private void Append(string line)
    {
        lock (gate)
        {
            if (tail.Length + line.Length + 1 > TailLimit)
            {
                var keepFrom = Math.Max(0, tail.Length - (TailLimit / 2));
                tail.Remove(0, keepFrom);
            }

            tail.Append(line).Append('\n');
        }
    }
}
