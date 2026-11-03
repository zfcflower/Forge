using System.IO.Pipes;

namespace Forge.Ipc;

/// <summary>Connects one pipe stream and runs the shared start/send/stop lifecycle for a transport.</summary>
internal sealed class PipeEndpoint : IAsyncDisposable
{
    private readonly string name;
    private readonly IpcPipeOptions options;
    private readonly Func<CancellationToken, Task<Stream>> connect;
    private readonly SemaphoreSlim startGate = new(1, 1);
    private PipeSession? session;
    private int closedRaised;
    private int disposed;

    public PipeEndpoint(string name, IpcPipeOptions options, Func<CancellationToken, Task<Stream>> connect)
    {
        this.name = name;
        this.options = options;
        this.connect = connect;
    }

    public bool IsConnected => session is { IsConnected: true };

    public event Action<Exception?>? Closed;

    public event Action<ReadOnlyMemory<byte>>? FrameReceived;

    public event Action<IpcProtocolException>? ProtocolError;

    public async Task StartAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        if (session is { IsConnected: true })
        {
            return;
        }

        await startGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (session is { IsConnected: true })
            {
                return;
            }

            if (session is not null)
            {
                throw new InvalidOperationException(
                    $"Pipe '{name}' has already been used; create a new transport to reconnect.");
            }

            var stream = await connect(ct).ConfigureAwait(false);
            var created = new PipeSession(stream, options, name);
            created.Closed += RaiseClosed;
            created.FrameReceived += OnFrameReceived;
            created.ProtocolError += OnProtocolError;
            session = created;
            await created.StartAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            startGate.Release();
        }
    }

    public async ValueTask SendAsync(ReadOnlyMemory<byte> frame, CancellationToken ct)
    {
        var current = session ?? throw new IpcConnectionClosedException($"Pipe '{name}' is not connected.");
        await current.SendAsync(frame, ct).ConfigureAwait(false);
    }

    public async Task StopAsync(CancellationToken ct)
    {
        if (session is not { } current)
        {
            RaiseClosed(null);
            return;
        }

        await current.StopAsync(ct).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        if (session is { } current)
        {
            await current.DisposeAsync().ConfigureAwait(false);
        }
        else
        {
            RaiseClosed(null);
        }

        startGate.Dispose();
    }

    private void OnFrameReceived(ReadOnlyMemory<byte> frame) => FrameReceived?.Invoke(frame);

    private void OnProtocolError(IpcProtocolException error) => ProtocolError?.Invoke(error);

    private void RaiseClosed(Exception? cause)
    {
        if (Interlocked.Exchange(ref closedRaised, 1) != 0)
        {
            return;
        }

        Closed?.Invoke(cause);
    }
}

internal static class PipeConnect
{
    public static async Task<Stream> ServerAsync(string name, IpcPipeOptions options, CancellationToken ct)
    {
        var stream = new NamedPipeServerStream(
            name,
            PipeDirection.InOut,
            NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly,
            Math.Max(1, options.ReadBufferBytes),
            Math.Max(1, options.WriteBufferBytes));
        try
        {
            await stream.WaitForConnectionAsync(ct).WaitAsync(options.ConnectTimeout, ct).ConfigureAwait(false);
            return stream;
        }
        catch (TimeoutException ex)
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            throw new IpcTimeoutException($"connect:{name}", options.ConnectTimeout, ex);
        }
        catch
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public static async Task<Stream> ClientAsync(string name, IpcPipeOptions options, CancellationToken ct)
    {
        var stream = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await stream.ConnectAsync(ct).WaitAsync(options.ConnectTimeout, ct).ConfigureAwait(false);
            return stream;
        }
        catch (TimeoutException ex)
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            throw new IpcTimeoutException($"connect:{name}", options.ConnectTimeout, ex);
        }
        catch
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}

/// <summary>Accepts one client on a named pipe and exchanges 4-byte length prefixed JSON frames with it.</summary>
public sealed class NamedPipeServerTransport : IIpcTransport
{
    private readonly PipeEndpoint endpoint;

    public NamedPipeServerTransport(string pipeName, IpcPipeOptions? options = null)
    {
        PipeName = IpcPipe.ValidatePipeName(pipeName);
        var settings = options ?? new IpcPipeOptions();
        endpoint = new PipeEndpoint(PipeName, settings, ct => PipeConnect.ServerAsync(PipeName, settings, ct));
        endpoint.Closed += cause => Closed?.Invoke(cause);
        endpoint.FrameReceived += frame => FrameReceived?.Invoke(frame);
        endpoint.ProtocolError += error => ProtocolError?.Invoke(error);
    }

    public string PipeName { get; }

    public bool IsConnected => endpoint.IsConnected;

    public event Action<Exception?>? Closed;

    public event Action<ReadOnlyMemory<byte>>? FrameReceived;

    /// <summary>Raised when an inbound frame is rejected; the pipe is closed only when it cannot resynchronize.</summary>
    public event Action<IpcProtocolException>? ProtocolError;

    public Task StartAsync(CancellationToken ct) => endpoint.StartAsync(ct);

    public ValueTask SendAsync(ReadOnlyMemory<byte> frame, CancellationToken ct) => endpoint.SendAsync(frame, ct);

    public Task StopAsync(CancellationToken ct) => endpoint.StopAsync(ct);

    public ValueTask DisposeAsync() => endpoint.DisposeAsync();
}

/// <summary>Connects to a named pipe server and exchanges 4-byte length prefixed JSON frames with it.</summary>
public sealed class NamedPipeClientTransport : IIpcTransport
{
    private readonly PipeEndpoint endpoint;

    public NamedPipeClientTransport(string pipeName, IpcPipeOptions? options = null)
    {
        PipeName = IpcPipe.ValidatePipeName(pipeName);
        var settings = options ?? new IpcPipeOptions();
        endpoint = new PipeEndpoint(PipeName, settings, ct => PipeConnect.ClientAsync(PipeName, settings, ct));
        endpoint.Closed += cause => Closed?.Invoke(cause);
        endpoint.FrameReceived += frame => FrameReceived?.Invoke(frame);
        endpoint.ProtocolError += error => ProtocolError?.Invoke(error);
    }

    public string PipeName { get; }

    public bool IsConnected => endpoint.IsConnected;

    public event Action<Exception?>? Closed;

    public event Action<ReadOnlyMemory<byte>>? FrameReceived;

    /// <summary>Raised when an inbound frame is rejected; the pipe is closed only when it cannot resynchronize.</summary>
    public event Action<IpcProtocolException>? ProtocolError;

    public Task StartAsync(CancellationToken ct) => endpoint.StartAsync(ct);

    public ValueTask SendAsync(ReadOnlyMemory<byte> frame, CancellationToken ct) => endpoint.SendAsync(frame, ct);

    public Task StopAsync(CancellationToken ct) => endpoint.StopAsync(ct);

    public ValueTask DisposeAsync() => endpoint.DisposeAsync();
}
