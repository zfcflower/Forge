using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace Forge.Mcp.Tests.Fixtures;

/// <summary>
/// In-memory <see cref="IMcpTransport"/>. Outgoing payloads are recorded and handed to <see cref="Responder"/>;
/// everything it returns, plus anything pushed by the test, is delivered on one background pump in order.
/// </summary>
internal sealed class LoopbackTransport : IMcpTransport
{
    private readonly Channel<Action> inbound = Channel.CreateUnbounded<Action>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Task pump;
    private readonly ConcurrentQueue<string> sent = new();
    private readonly ConcurrentQueue<long> tracked = new();
    private readonly ConcurrentQueue<long> settled = new();
    private readonly ConcurrentQueue<string?> protocolVersions = new();
    private int connected;
    private int closedRaised;
    private int startCount;
    private int stopCount;
    private int disposeCount;
    private int serverStreamStarts;

    public LoopbackTransport(string name = "loopback")
    {
        Name = name;
        pump = Task.Run(PumpAsync);
    }

    public string Name { get; }

    public bool IsConnected => Volatile.Read(ref connected) != 0;

    public Func<JsonElement, IEnumerable<string>>? Responder { get; set; }

    public Exception? SendFailure { get; set; }

    public int StartCount => Volatile.Read(ref startCount);

    public int StopCount => Volatile.Read(ref stopCount);

    public int DisposeCount => Volatile.Read(ref disposeCount);

    public int ServerStreamStarts => Volatile.Read(ref serverStreamStarts);

    public IReadOnlyList<string> Sent => sent.ToArray();

    public IReadOnlyList<long> TrackedRequestIds => tracked.ToArray();

    public IReadOnlyList<long> SettledRequestIds => settled.ToArray();

    public IReadOnlyList<string?> ProtocolVersions => protocolVersions.ToArray();

    public event Action<ReadOnlyMemory<byte>>? MessageReceived;

    public event Action<Exception?>? Closed;

    public Task StartAsync(CancellationToken ct)
    {
        Interlocked.Increment(ref startCount);
        Volatile.Write(ref closedRaised, 0);
        Volatile.Write(ref connected, 1);
        return Task.CompletedTask;
    }

    public ValueTask SendAsync(ReadOnlyMemory<byte> payload, CancellationToken ct, long? requestId = null)
    {
        ct.ThrowIfCancellationRequested();
        if (SendFailure is { } failure)
        {
            throw failure;
        }

        if (!IsConnected)
        {
            throw new McpConnectionClosedException("The loopback transport is not connected.");
        }

        var text = Encoding.UTF8.GetString(payload.Span);
        sent.Enqueue(text);
        if (requestId is { } id)
        {
            tracked.Enqueue(id);
        }

        if (Responder is { } responder)
        {
            using var document = JsonDocument.Parse(text);
            foreach (var reply in responder(document.RootElement).ToArray())
            {
                Push(reply);
            }
        }

        return ValueTask.CompletedTask;
    }

    public void RequestSettled(long requestId) => settled.Enqueue(requestId);

    public Task StartServerStreamAsync(CancellationToken ct)
    {
        Interlocked.Increment(ref serverStreamStarts);
        return Task.CompletedTask;
    }

    public void SetProtocolVersion(string protocolVersion) => protocolVersions.Enqueue(protocolVersion);

    public Task StopAsync(CancellationToken ct)
    {
        Interlocked.Increment(ref stopCount);
        Volatile.Write(ref connected, 0);
        RaiseClosed(null);
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Increment(ref disposeCount) != 1)
        {
            return;
        }

        Volatile.Write(ref connected, 0);
        inbound.Writer.TryComplete();
        await pump.ConfigureAwait(false);
    }

    /// <summary>Delivers a server-originated payload (response, notification or request) asynchronously.</summary>
    public void Push(string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        inbound.Writer.TryWrite(() => MessageReceived?.Invoke(bytes));
    }

    /// <summary>Simulates the connection dropping; ordered after payloads already pushed.</summary>
    public void Drop(Exception? error = null)
    {
        Volatile.Write(ref connected, 0);
        inbound.Writer.TryWrite(() => RaiseClosed(error));
    }

    private void RaiseClosed(Exception? error)
    {
        if (Interlocked.Exchange(ref closedRaised, 1) != 0)
        {
            return;
        }

        Closed?.Invoke(error);
    }

    private async Task PumpAsync()
    {
        await foreach (var action in inbound.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            action();
        }
    }
}
