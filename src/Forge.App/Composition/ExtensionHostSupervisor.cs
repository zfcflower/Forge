using Forge.HostClient;

namespace Forge.App.Composition;

/// <summary>
/// Owns the <see cref="ExtensionHostClient"/> lifetime: first start, bounded restart after a crash (the restart policy
/// belongs to the composition root, not to the client), plugin status polling and a deterministic stop. Nothing here
/// touches the UI thread; events are raised on whatever thread produced them.
/// </summary>
internal sealed class ExtensionHostSupervisor : IAsyncDisposable
{
    /// <summary>Consecutive start attempts before the supervisor reports a terminal failure instead of retrying.</summary>
    private const int MaxStartAttempts = 3;

    private static readonly TimeSpan FirstRetryDelay = TimeSpan.FromSeconds(1);

    private readonly ExtensionHostClient client;
    private readonly Lock gate = new();
    private CancellationTokenSource? lifetime;
    private Task? restart;
    private int failures;
    private bool stopping;
    private bool disposed;

    private ExtensionHostSupervisor(ExtensionHostClient client)
    {
        this.client = client;
        client.StatusChanged += OnStatusChanged;
        client.ToolsChanged += OnSurfaceChanged;
        client.ContributionsChanged += OnSurfaceChanged;
    }

    public HostStatus Status => client.Status;

    /// <summary>The underlying client, so the agent runtime can reach the tool surface and <c>run_code</c>.</summary>
    public ExtensionHostClient Client => client;

    public string? StatusDetail { get; private set; }

    public int? HostProcessId => client.HostProcessId;

    public int PluginToolCount => client.Tools.Count;

    public int CommandCount => client.Commands.Count;

    /// <summary>Raised after the host reported a status change; the payload is the sentence the status bar shows.</summary>
    public event Action<HostStatus, string?>? StatusChanged;

    /// <summary>Raised when the tool or contribution surface changed, so plugin counts must be re-read.</summary>
    public event Action? SurfaceChanged;

    /// <summary>
    /// Creates the supervisor for a resolved JIT layout. The host is started with explicit worker and executor paths so
    /// discovery inside the host cannot pick up an unrelated build.
    /// </summary>
    public static ExtensionHostSupervisor Create(JitHostLayout layout, AppPaths paths)
    {
        var connection = new HostConnection
        {
            PipeName = "forge-app-" + Guid.NewGuid().ToString("N")[..20],
            HostExecutablePath = layout.HostPath,
            RuntimePath = "dotnet",
            HostArguments = ["--worker", layout.WorkerPath, "--executor", layout.ExecutorPath],
            StartTimeout = TimeSpan.FromSeconds(60),
        };
        var options = new ExtensionHostClientOptions
        {
            PluginsDirectory = paths.PluginsDirectory,
            DataDirectory = paths.HostDataDirectory,
            RequestTimeout = TimeSpan.FromSeconds(120),
            StopRequestTimeout = TimeSpan.FromSeconds(5),
            ProcessExitTimeout = TimeSpan.FromSeconds(10),
        };

        return new ExtensionHostSupervisor(new ExtensionHostClient(connection, null, null, options));
    }

    /// <summary>Starts the host in the background; returns immediately so the window is up before the pipe is open.</summary>
    public void Start()
    {
        CancellationTokenSource source;
        lock (gate)
        {
            if (disposed || stopping)
            {
                return;
            }

            lifetime?.Dispose();
            source = new CancellationTokenSource();
            lifetime = source;
            restart = Task.Run(() => StartWithRetriesAsync(source.Token));
        }
    }

    /// <summary>
    /// Reads <c>plugins.status</c>. Null means "unknown", never "no plugins": the client throws instead of faking an
    /// empty registry, and this method preserves that distinction.
    /// </summary>
    public async Task<PluginInventory?> ReadInventoryAsync(CancellationToken ct)
    {
        if (client.Status != HostStatus.Running)
        {
            return null;
        }

        try
        {
            var statuses = await client.GetPluginStatusesAsync(ct).ConfigureAwait(false);
            return new PluginInventory(statuses.Count, client.Tools.Count, client.Commands.Count);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return null;
        }
    }

    private async Task StartWithRetriesAsync(CancellationToken ct)
    {
        var delay = FirstRetryDelay;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await client.StartAsync(ct).ConfigureAwait(false);
                Interlocked.Exchange(ref failures, 0);
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                var attempt = Interlocked.Increment(ref failures);
                if (attempt >= MaxStartAttempts)
                {
                    Report(HostStatus.Failed, "扩展宿主未能启动：" + FirstLine(exception.Message));
                    return;
                }

                try
                {
                    await Task.Delay(delay, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                delay *= 2;
            }
        }
    }

    private void OnSurfaceChanged() => SurfaceChanged?.Invoke();

    private void OnStatusChanged(HostStatus status, string? detail)
    {
        lock (gate)
        {
            if (disposed || stopping)
            {
                return;
            }
        }

        if (status == HostStatus.Running)
        {
            Interlocked.Exchange(ref failures, 0);
        }

        StatusDetail = detail;
        StatusChanged?.Invoke(status, detail);

        if (status == HostStatus.Failed)
        {
            ScheduleRestart();
        }
    }

    /// <summary>A crash keeps the tool snapshot but the connection is gone; reconnect on the same instance with backoff.</summary>
    private void ScheduleRestart()
    {
        lock (gate)
        {
            if (disposed || stopping)
            {
                return;
            }

            if (restart is { IsCompleted: false })
            {
                return;
            }

            var source = lifetime;
            if (source is null || source.IsCancellationRequested)
            {
                return;
            }

            restart = Task.Run(() => StartWithRetriesAsync(source.Token));
        }
    }

    private void Report(HostStatus status, string? detail)
    {
        StatusDetail = detail;
        StatusChanged?.Invoke(status, detail);
    }

    private static string FirstLine(string text)
    {
        var line = text.AsSpan();
        var breakIndex = line.IndexOfAny('\r', '\n');
        return (breakIndex >= 0 ? line[..breakIndex] : line).ToString();
    }

    public async ValueTask DisposeAsync()
    {
        CancellationTokenSource? source;
        Task? pending;
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            stopping = true;
            source = lifetime;
            pending = restart;
            lifetime = null;
            restart = null;
        }

        client.StatusChanged -= OnStatusChanged;
        client.ToolsChanged -= OnSurfaceChanged;
        client.ContributionsChanged -= OnSurfaceChanged;

        if (source is not null)
        {
            await source.CancelAsync().ConfigureAwait(false);
        }

        if (pending is not null)
        {
            try
            {
                await pending.ConfigureAwait(false);
            }
            catch (Exception)
            {
            }
        }

        source?.Dispose();

        // The client sends host.stop, waits for the host to exit on its own and only then kills the process tree,
        // so this is the place where the child process is reaped.
        await client.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>What the host reported through <c>plugins.status</c>, plus the tool and command counts it registered.</summary>
internal sealed record PluginInventory(int PluginCount, int ToolCount, int CommandCount);
