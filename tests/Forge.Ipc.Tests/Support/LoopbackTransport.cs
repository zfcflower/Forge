using System.Buffers.Binary;
using System.Threading.Channels;

namespace Forge.Ipc.Tests.Support;

internal sealed class LoopbackTransport : IIpcTransport
{
    private readonly Channel<byte[]> inbox = Channel.CreateUnbounded<byte[]>();
    private readonly Channel<byte[]> observed = Channel.CreateUnbounded<byte[]>();
    private LoopbackTransport? peer;
    private int started;
    private int closed;

    private LoopbackTransport()
    {
    }

    public static (LoopbackTransport Local, LoopbackTransport Remote) CreatePair()
    {
        var local = new LoopbackTransport();
        var remote = new LoopbackTransport();
        local.peer = remote;
        remote.peer = local;
        return (local, remote);
    }

    public static async Task<(LoopbackTransport Local, LoopbackTransport Remote)> ConnectAsync()
    {
        var pair = CreatePair();
        await pair.Local.StartAsync(CancellationToken.None);
        await pair.Remote.StartAsync(CancellationToken.None);
        return pair;
    }

    public bool IsConnected => Volatile.Read(ref started) == 1 && Volatile.Read(ref closed) == 0;

    public event Action<Exception?>? Closed;

    public event Action<ReadOnlyMemory<byte>>? FrameReceived;

    public event Action<IpcProtocolException>? ProtocolError;

    public Task StartAsync(CancellationToken ct)
    {
        if (Interlocked.Exchange(ref started, 1) == 0)
        {
            _ = PumpAsync();
        }

        return Task.CompletedTask;
    }

    public ValueTask SendAsync(ReadOnlyMemory<byte> frame, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var target = peer;
        if (!IsConnected || target is null || !target.IsConnected)
        {
            throw new IpcConnectionClosedException("The loopback transport is not connected.");
        }

        target.inbox.Writer.TryWrite(StripHeader(frame));
        return ValueTask.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct)
    {
        Close(null);
        peer?.Close(new IpcConnectionClosedException("The loopback peer stopped."));
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => new(StopAsync(CancellationToken.None));

    public void Fail(Exception cause)
    {
        Close(cause);
        peer?.Close(new IpcConnectionClosedException("The loopback peer failed."));
    }

    public async Task<byte[]> ReceiveBodyAsync()
    {
        using var timeout = new CancellationTokenSource(TestTimeouts.Limit);
        return await observed.Reader.ReadAsync(timeout.Token);
    }

    public async Task<IpcMessage> ReceiveMessageAsync() => FrameCodec.Default.Decode(await ReceiveBodyAsync());

    public async Task<IpcMessage> ReceiveUntilAsync(Func<IpcMessage, bool> predicate)
    {
        while (true)
        {
            var message = await ReceiveMessageAsync();
            if (predicate(message))
            {
                return message;
            }
        }
    }

    public ValueTask SendFrameAsync(byte[] frame) => SendAsync(frame, CancellationToken.None);

    /// <summary>Sends raw body bytes (length-prefixed); used to inject malformed frames.</summary>
    public ValueTask SendBodyAsync(byte[] body) => SendAsync(TestFrames.Prefix(body), CancellationToken.None);

    private void Close(Exception? cause)
    {
        Volatile.Write(ref closed, 1);
        inbox.Writer.TryComplete();
        Closed?.Invoke(cause);
    }

    private async Task PumpAsync()
    {
        await foreach (var body in inbox.Reader.ReadAllAsync())
        {
            if (body.Length == 0)
            {
                ProtocolError?.Invoke(new IpcProtocolException("Received a frame declaring a zero-length body; skipped."));
                continue;
            }

            observed.Writer.TryWrite(body);
            FrameReceived?.Invoke(body);
        }
    }

    private static byte[] StripHeader(ReadOnlyMemory<byte> frame)
    {
        var span = frame.Span;
        if (span.Length >= TestFrames.HeaderBytes && BinaryPrimitives.ReadInt32LittleEndian(span) == span.Length - TestFrames.HeaderBytes)
        {
            return span[TestFrames.HeaderBytes..].ToArray();
        }

        return span.ToArray();
    }
}
