using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
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
}

internal static class Json
{
    public static JsonElement Parse(string text)
    {
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }
}

internal static class Wire
{
    public static string BodyText(IpcMessage message) =>
        Encoding.UTF8.GetString(FrameCodec.Default.Serialize(message).Span[TestFrames.HeaderBytes..]);
}

internal static class Peers
{
    public static IpcPeer Create(IIpcTransport transport, TimeSpan? timeout = null, int maxOutstanding = 0, IJsonFrameCodec? codec = null) =>
        new(transport, new IpcPeerOptions
        {
            DefaultTimeout = timeout ?? TestTimeouts.Request,
            MaxOutstandingRequests = maxOutstanding,
            Codec = codec,
        });
}

internal static class TestPipes
{
    public static string NewName() => "forge-ipc-test-" + Guid.NewGuid().ToString("N");
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

    public async Task<IpcMessage> NextMessageAsync() => FrameCodec.Default.Deserialize(await NextBodyAsync());
}
