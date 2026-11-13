using System.IO.Pipes;
using System.Text;

namespace Forge.Ipc;

/// <summary>A full-duplex, framed message transport. Implementations must deliver complete frames
/// in order and surface the connection state through <see cref="Closed"/>.</summary>
public interface IIpcTransport : IAsyncDisposable
{
    bool IsConnected { get; }

    /// <summary>Raised exactly once per connection with the cause; <c>null</c> when stopped cleanly by the owner.</summary>
    event Action<Exception?>? Closed;

    /// <summary>Raised for each complete frame body. The buffer is handed over: the transport never reuses it, so a
    /// decoded message may keep slices of it.</summary>
    event Action<ReadOnlyMemory<byte>>? FrameReceived;

    /// <summary>Raised once for each inbound frame the transport rejects (zero-length body or above the frame limit);
    /// the frame is dropped and the connection stays usable.</summary>
    event Action<IpcProtocolException>? ProtocolError;

    Task StartAsync(CancellationToken ct);

    /// <summary>Sends one frame; the transport copies it before the returned task completes, so the caller may
    /// return a pooled buffer afterwards.</summary>
    ValueTask SendAsync(ReadOnlyMemory<byte> frame, CancellationToken ct);

    Task StopAsync(CancellationToken ct);
}

/// <summary>Creates named-pipe transports. On Linux/macOS this maps to a Unix domain socket via
/// <see cref="NamedPipeServerStream"/> with the same pipe name, so one API covers all three platforms.</summary>
public static class IpcPipe
{
    /// <summary>Name-only upper bound; the path bound on Linux/macOS is <see cref="MaxNameLength"/>.</summary>
    private const int MaxValidatedNameLength = 108;

    private const string UnixSocketFilePrefix = "CoreFxPipe_";

    private static readonly Lazy<int> MaxNameLengthValue = new(ComputeMaxNameLength, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>The longest pipe name that still fits a Unix domain socket on this platform. The BCL keeps the socket
    /// file at <c>&lt;temporary directory&gt;/CoreFxPipe_&lt;name&gt;</c> and <c>sockaddr_un.sun_path</c> is 108 bytes on
    /// Linux but only 104 on macOS, so a name accepted by <see cref="ValidatePipeName"/> can still be unusable there.
    /// Callers that generate names should stay at or below this value; Windows has no path bound and reports 108.</summary>
    public static int MaxNameLength => MaxNameLengthValue.Value;

    /// <summary>Rejects pipe names that cannot round-trip through a Unix socket path.</summary>
    public static string ValidatePipeName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name.Length > MaxValidatedNameLength)
            throw new ArgumentException("Pipe name too long (max 108 chars for Unix domain sockets).", nameof(name));
        foreach (var c in name)
            if (c is '/' or '\\' or ':')
                throw new ArgumentException("Pipe name must not contain path separators or ':'.", nameof(name));
        return name;
    }

    private static int ComputeMaxNameLength()
    {
        if (OperatingSystem.IsWindows())
        {
            return MaxValidatedNameLength;
        }

        // sockaddr_un.sun_path includes the terminating NUL.
        var socketPathLimit = OperatingSystem.IsMacOS() ? 104 : 108;
        try
        {
            var spent = Encoding.UTF8.GetByteCount(Path.GetTempPath()) + UnixSocketFilePrefix.Length + 1;
            return Math.Max(16, socketPathLimit - spent);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return 16;
        }
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
