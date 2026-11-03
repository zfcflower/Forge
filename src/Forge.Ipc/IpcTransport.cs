using System.IO.Pipes;

namespace Forge.Ipc;

/// <summary>A full-duplex, framed message transport. Implementations must deliver complete frames
/// in order and surface the connection state through <see cref="Closed"/>.</summary>
public interface IIpcTransport : IAsyncDisposable
{
    bool IsConnected { get; }

    /// <summary>Raised exactly once per connection with the cause; <c>null</c> when stopped cleanly by the owner.</summary>
    event Action<Exception?>? Closed;

    /// <summary>Raised for each complete frame; the buffer is valid only during the callback.</summary>
    event Action<ReadOnlyMemory<byte>>? FrameReceived;

    /// <summary>Raised once for each inbound frame the transport rejects (zero-length body or above the frame limit);
    /// the frame is dropped and the connection stays usable.</summary>
    event Action<IpcProtocolException>? ProtocolError;

    Task StartAsync(CancellationToken ct);

    ValueTask SendAsync(ReadOnlyMemory<byte> frame, CancellationToken ct);

    Task StopAsync(CancellationToken ct);
}

/// <summary>Creates named-pipe transports. On Linux/macOS this maps to a Unix domain socket via
/// <see cref="NamedPipeServerStream"/> with the same pipe name, so one API covers all three platforms.</summary>
public static class IpcPipe
{
    /// <summary>Rejects pipe names that cannot round-trip through a Unix socket path.</summary>
    public static string ValidatePipeName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name.Length > 108)
            throw new ArgumentException("Pipe name too long (max 108 chars for Unix domain sockets).", nameof(name));
        foreach (var c in name)
            if (c is '/' or '\\' or ':')
                throw new ArgumentException("Pipe name must not contain path separators or ':'.", nameof(name));
        return name;
    }

    /// <summary>Creates a server transport and waits for one client, bounded by <see cref="IpcPipeOptions.ConnectTimeout"/>.</summary>
    public static async Task<NamedPipeServerTransport> CreateServer(string name, CancellationToken ct, IpcPipeOptions? options = null)
    {
        var server = new NamedPipeServerTransport(name, options);
        try
        {
            await server.StartAsync(ct).ConfigureAwait(false);
            return server;
        }
        catch
        {
            await server.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Creates a server transport and waits for one client without a caller cancellation token.</summary>
    public static Task<NamedPipeServerTransport> CreateServer(string name, IpcPipeOptions? options = null) =>
        CreateServer(name, CancellationToken.None, options);

    /// <summary>Creates a client transport and connects, bounded by <see cref="IpcPipeOptions.ConnectTimeout"/>.</summary>
    public static async Task<NamedPipeClientTransport> CreateClient(string name, CancellationToken ct, IpcPipeOptions? options = null)
    {
        var client = new NamedPipeClientTransport(name, options);
        try
        {
            await client.StartAsync(ct).ConfigureAwait(false);
            return client;
        }
        catch
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Creates a client transport and connects without a caller cancellation token.</summary>
    public static Task<NamedPipeClientTransport> CreateClient(string name, IpcPipeOptions? options = null) =>
        CreateClient(name, CancellationToken.None, options);
}

public sealed class IpcTimeoutException(string operation, TimeSpan timeout, Exception? inner = null)
    : TimeoutException($"IPC request '{operation}' timed out after {timeout.TotalMilliseconds:F0} ms.", inner)
{
    public string Operation { get; } = operation;
    public TimeSpan Timeout { get; } = timeout;
}

public sealed class IpcConnectionClosedException(string? message = null, Exception? inner = null)
    : IOException(message ?? "IPC connection closed before the request completed.", inner);

public sealed class IpcProtocolException(string message, Exception? inner = null)
    : InvalidOperationException(message, inner);
