namespace Forge.Mcp;

/// <summary>
/// Carries JSON-RPC messages to one MCP server. <see cref="SendAsync"/> completes once the payload has been
/// handed to the peer; inbound messages arrive on <see cref="MessageReceived"/>, whose payload is only valid
/// for the duration of the call.
/// </summary>
public interface IMcpTransport : IAsyncDisposable
{
    string Name { get; }

    bool IsConnected { get; }

    event Action<ReadOnlyMemory<byte>>? MessageReceived;

    /// <summary>Raised once per connection: <c>null</c> when the owner stopped it, otherwise why it closed.</summary>
    event Action<Exception?>? Closed;

    Task StartAsync(CancellationToken ct);

    /// <summary><paramref name="requestId"/> is set when <paramref name="payload"/> is a request the caller is tracking.</summary>
    ValueTask SendAsync(ReadOnlyMemory<byte> payload, CancellationToken ct, long? requestId = null);

    Task StopAsync(CancellationToken ct);

    /// <summary>Reports that no reply for this request is awaited any more, so its stream need not stay open.</summary>
    void RequestSettled(long requestId)
    {
    }

    /// <summary>Opens a server-to-client stream where the transport has one; a no-op otherwise.</summary>
    Task StartServerStreamAsync(CancellationToken ct) => Task.CompletedTask;

    /// <summary>Tells header-carrying transports the version negotiated during initialization.</summary>
    void SetProtocolVersion(string protocolVersion)
    {
    }
}

/// <summary>Dispatches one transport payload, unbatching a JSON-RPC batch array if the peer sent one.</summary>
internal static class TransportDispatch
{
    /// <summary>
    /// Delivers the JSON-RPC messages of one payload. Returns false when the payload is not a JSON-RPC object
    /// or array, so a caller can report protocol noise instead of treating it as a message.
    /// </summary>
    public static bool Dispatch(ReadOnlyMemory<byte> payload, Action<ReadOnlyMemory<byte>> sink)
    {
        var span = payload.Span;
        var index = 0;
        while (index < span.Length && span[index] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')
        {
            index++;
        }

        if (index >= span.Length)
        {
            return false;
        }

        if (span[index] == (byte)'{')
        {
            if (!EndsWithObject(span))
            {
                return false;
            }

            sink(payload);
            return true;
        }

        if (span[index] != (byte)'[')
        {
            return false;
        }

        System.Text.Json.JsonDocument document;
        try
        {
            document = System.Text.Json.JsonDocument.Parse(payload);
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }

        using (document)
        {
            if (document.RootElement.ValueKind != System.Text.Json.JsonValueKind.Array)
            {
                return false;
            }

            foreach (var item in document.RootElement.EnumerateArray())
            {
                sink(JsonText.ToUtf8(item));
            }
        }

        return true;
    }

    private static bool EndsWithObject(ReadOnlySpan<byte> span)
    {
        var end = span.Length - 1;
        while (end > 0 && span[end] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')
        {
            end--;
        }

        return span[end] == (byte)'}';
    }
}
