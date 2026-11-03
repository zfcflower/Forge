using Forge.Ipc;

namespace Forge.PluginWorker;

/// <summary>The worker process. It connects to the Extension Host's worker pipe, serves exactly one
/// plugin, and holds nothing in common with the host or with other workers, so it can be killed as a
/// whole (process and descendants) at any time.</summary>
public sealed class WorkerServer(WorkerOptions options)
{
    public WorkerOptions Options { get; } = options ?? throw new ArgumentNullException(nameof(options));

    /// <summary>Connects, serves until <c>worker.stop</c> or disconnection, and reports the process
    /// exit code: 0 for a clean stop, 1 when the connection failed.</summary>
    public async Task<int> RunAsync(CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Options.PipeName);

        var transportOptions = new IpcPipeOptions { ConnectTimeout = Options.ConnectTimeout };
        await using var transport = new NamedPipeClientTransport(
            IpcPipe.ValidatePipeName(Options.PipeName),
            transportOptions);
        await using var peer = new IpcPeer(
            transport,
            new IpcPeerOptions { DefaultTimeout = Options.ToolCallTimeout });

        var session = new WorkerSession(peer, Options);
        await peer.StartAsync(ct).ConfigureAwait(false);
        try
        {
            return await session.ServeAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            await session.ReleasePluginAsync().ConfigureAwait(false);
        }
    }
}
