using System.Buffers.Binary;
using System.Threading.Channels;

namespace Forge.Ipc.Tests.Support;

internal static class TestTimeouts
{
    public static readonly TimeSpan Limit = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan Request = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan Short = TimeSpan.FromMilliseconds(150);
}

internal static class TestAwait
{
    public static Task<T> Bounded<T>(this Task<T> task) => task.WaitAsync(TestTimeouts.Limit);

    public static Task Bounded(this Task task) => task.WaitAsync(TestTimeouts.Limit);

    public static async Task UntilAsync(Func<bool> condition, string description)
    {
        using var timeout = new CancellationTokenSource(TestTimeouts.Limit);
        while (!condition())
        {
            try
            {
                await Task.Delay(5, timeout.Token);
            }
            catch (OperationCanceledException)
            {
                throw new TimeoutException($"Timed out waiting for {description}.");
            }
        }
    }
}

internal static class TestFrames
{
    public const int HeaderBytes = 4;

    public static byte[] Prefix(byte[] body)
    {
        var frame = new byte[HeaderBytes + body.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, body.Length);
        body.CopyTo(frame, HeaderBytes);
        return frame;
    }

    public static byte[] Header(int declaredLength)
    {
        var header = new byte[HeaderBytes];
        BinaryPrimitives.WriteInt32LittleEndian(header, declaredLength);
        return header;
    }

    public static byte[] Filler(int count, byte value = (byte)'x') => Enumerable.Repeat(value, count).ToArray();

    public static string Padded(int length) => new('x', length);

    public static byte[] Request<T>(long id, string operation, T args) =>
        FrameCodec.Default.EncodeToArray(IpcHeader.Request(id, operation), args);

    public static byte[] Notification<T>(string operation, T args) =>
        FrameCodec.Default.EncodeToArray(IpcHeader.Notification(operation), args);

    public static byte[] Result<T>(long id, T result) =>
        FrameCodec.Default.EncodeToArray(IpcHeader.Result(id), result);

    public static byte[] Failure(long id, IpcError error) => HeaderOnly(IpcHeader.Failure(id, error));

    public static byte[] HeaderOnly(in IpcHeader header)
    {
        using var buffer = FrameCodec.Default.Encode(header);
        return buffer.WrittenMemory.ToArray();
    }
}

internal static class Peers
{
    public static IpcPeer Create(IIpcTransport transport, TimeSpan? timeout = null, int maxOutstanding = 0, FrameCodec? codec = null) =>
        new(transport, new IpcPeerOptions
        {
            DefaultTimeout = timeout ?? TestTimeouts.Request,
            MaxOutstandingRequests = maxOutstanding,
            Codec = codec,
        });
}

internal static class TestPipes
{
    /// <summary>Short enough to stay inside macOS's 104-byte domain socket path limit once the BCL prepends
    /// <c>CoreFxPipe_</c> under a long per-user temporary directory.</summary>
    public static string NewName() => "fx-" + Guid.NewGuid().ToString("N")[..16];
}

internal sealed class FrameSink
{
    private readonly Channel<byte[]> frames = Channel.CreateUnbounded<byte[]>();

    public FrameSink(IIpcTransport transport)
    {
        transport.FrameReceived += frame => frames.Writer.TryWrite(frame.ToArray());
    }

    public async Task<byte[]> NextBodyAsync()
    {
        using var timeout = new CancellationTokenSource(TestTimeouts.Limit);
        return await frames.Reader.ReadAsync(timeout.Token);
    }

    public async Task<IpcMessage> NextMessageAsync() => FrameCodec.Default.Decode(await NextBodyAsync());
}
