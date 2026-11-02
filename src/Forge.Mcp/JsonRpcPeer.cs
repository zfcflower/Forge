using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;

namespace Forge.Mcp;

/// <summary>
/// JSON-RPC 2.0 peer over an <see cref="IMcpTransport"/>: correlates concurrent requests by id, applies a
/// per-request timeout, cancels in-flight work on the wire, and answers server-initiated requests.
/// </summary>
internal sealed class JsonRpcPeer : IAsyncDisposable
{
    private readonly IMcpTransport transport;
    private readonly TimeSpan requestTimeout;
    private readonly ConcurrentDictionary<long, Pending> pending = new();
    private readonly Channel<JsonDocument> serverRequests = Channel.CreateUnbounded<JsonDocument>(
        new UnboundedChannelOptions { SingleReader = true, AllowSynchronousContinuations = false });
    private readonly CancellationTokenSource lifetime = new();
    private readonly Task pump;
    private long nextId;
    private int closed;
    private int disposed;

    public JsonRpcPeer(IMcpTransport transport, TimeSpan requestTimeout)
    {
        this.transport = transport;
        this.requestTimeout = requestTimeout > TimeSpan.Zero && requestTimeout != Timeout.InfiniteTimeSpan
            ? requestTimeout
            : Timeout.InfiniteTimeSpan;
        transport.MessageReceived += OnMessage;
        transport.Closed += OnTransportClosed;
        pump = Task.Run(PumpServerRequestsAsync);
    }

    /// <summary>Handles a server-initiated request; returning null answers with "method not found".</summary>
    public Func<string, JsonElement, string?>? ServerRequestHandler { get; set; }

    /// <summary>Receives server notifications; the element is only valid inside the call.</summary>
    public Action<string, JsonElement>? NotificationHandler { get; set; }

    public event Action<Exception?>? Closed;

    public bool IsClosed => Volatile.Read(ref closed) != 0;

    public async ValueTask NotifyAsync(string method, Action<Utf8JsonWriter>? writeParams, CancellationToken ct)
    {
        ThrowIfClosed(method);
        await transport.SendAsync(RpcJson.Notification(method, writeParams), ct).ConfigureAwait(false);
    }

    public async ValueTask<JsonDocument> RequestAsync(string method, Action<Utf8JsonWriter>? writeParams, CancellationToken ct)
    {
        ThrowIfClosed(method);

        var id = Interlocked.Increment(ref nextId);
        using var deadline = new CancellationTokenSource(requestTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token, lifetime.Token);
        var token = linked.Token;
        var completion = new TaskCompletionSource<JsonDocument>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending[id] = new Pending(method, completion);
        try
        {
            await transport.SendAsync(RpcJson.Request(id, method, writeParams), token, id).ConfigureAwait(false);
            return await completion.Task.WaitAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            if (!IsClosed)
            {
                SendCancellation(id, method, "cancelled by client");
            }

            throw;
        }
        catch (OperationCanceledException)
        {
            if (IsClosed)
            {
                if (completion.Task.IsCompleted)
                {
                    return await completion.Task.ConfigureAwait(false);
                }

                throw new McpConnectionClosedException(
                    $"The MCP connection closed before '{method}' completed.", null);
            }

            if (deadline.IsCancellationRequested)
            {
                SendCancellation(id, method, "request timed out");
                throw new McpTimeoutException(method, requestTimeout);
            }

            throw;
        }
        finally
        {
            pending.TryRemove(id, out _);
            transport.RequestSettled(id);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        transport.MessageReceived -= OnMessage;
        transport.Closed -= OnTransportClosed;
        OnTransportClosed(null);
        try
        {
            await pump.ConfigureAwait(false);
        }
        catch (Exception)
        {
        }

        lifetime.Dispose();
    }

    private void ThrowIfClosed(string method)
    {
        if (IsClosed)
        {
            throw new McpConnectionClosedException($"The MCP transport is closed; '{method}' was not sent.");
        }
    }

    private void OnMessage(ReadOnlyMemory<byte> payload)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(payload.ToArray());
        }
        catch (JsonException)
        {
            return;
        }

        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            document.Dispose();
            return;
        }

        var hasId = root.TryGetProperty("id", out var idElement) &&
                    idElement.ValueKind is JsonValueKind.Number or JsonValueKind.String;
        var hasMethod = root.TryGetProperty("method", out var methodElement) &&
                        methodElement.ValueKind == JsonValueKind.String;

        if (!hasId)
        {
            if (hasMethod)
            {
                DispatchNotification(document, methodElement.GetString()!);
            }
            else
            {
                document.Dispose();
            }

            return;
        }

        if (!hasMethod)
        {
            DispatchResponse(document, idElement);
            return;
        }

        if (!serverRequests.Writer.TryWrite(document))
        {
            document.Dispose();
        }
    }

    private void DispatchResponse(JsonDocument document, JsonElement idElement)
    {
        if (!TryReadId(idElement, out var id) || !pending.TryRemove(id, out var entry))
        {
            document.Dispose();
            return;
        }

        transport.RequestSettled(id);
        if (document.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
        {
            var code = error.TryGetProperty("code", out var codeElement) && codeElement.TryGetInt32(out var parsed)
                ? parsed
                : McpErrorCodes.InternalError;
            var message = error.TryGetProperty("message", out var messageElement) && messageElement.ValueKind == JsonValueKind.String
                ? messageElement.GetString()!
                : "unknown error";
            var data = error.TryGetProperty("data", out var dataElement) ? dataElement.GetRawText() : null;
            document.Dispose();
            entry.Completion.TrySetException(new McpProtocolException(entry.Method, code, message, data));
            return;
        }

        entry.Completion.TrySetResult(document);
    }

    private void DispatchNotification(JsonDocument document, string method)
    {
        try
        {
            if (NotificationHandler is not { } handler)
            {
                return;
            }

            var parameters = document.RootElement.TryGetProperty("params", out var value) ? value : default;
            handler(method, parameters);
        }
        catch (Exception)
        {
        }
        finally
        {
            document.Dispose();
        }
    }

    private async Task PumpServerRequestsAsync()
    {
        var reader = serverRequests.Reader;
        try
        {
            while (await reader.WaitToReadAsync(lifetime.Token).ConfigureAwait(false))
            {
                while (reader.TryRead(out var document))
                {
                    await AnswerServerRequestAsync(document).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task AnswerServerRequestAsync(JsonDocument document)
    {
        try
        {
            var root = document.RootElement;
            var method = root.GetProperty("method").GetString() ?? string.Empty;
            var idElement = root.GetProperty("id");
            var parameters = root.TryGetProperty("params", out var value) ? value : default;

            string? result;
            try
            {
                result = ServerRequestHandler?.Invoke(method, parameters);
            }
            catch (Exception ex)
            {
                await SendErrorAsync(idElement, McpErrorCodes.InternalError, ex.Message).ConfigureAwait(false);
                return;
            }

            if (result is null)
            {
                await SendErrorAsync(idElement, McpErrorCodes.MethodNotFound, $"Method not supported by this client: {method}").ConfigureAwait(false);
                return;
            }

            await transport.SendAsync(RpcJson.Result(idElement, result), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
        }
        finally
        {
            document.Dispose();
        }
    }

    private async Task SendErrorAsync(JsonElement idElement, int code, string message)
    {
        try
        {
            await transport.SendAsync(RpcJson.Error(idElement, code, message), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
        }
    }

    private void SendCancellation(long id, string method, string reason)
    {
        if (IsClosed || string.Equals(method, McpProtocol.Initialize, StringComparison.Ordinal))
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                await transport.SendAsync(
                    RpcJson.Notification(McpProtocol.Cancelled, writer =>
                    {
                        writer.WriteNumber("requestId", id);
                        writer.WriteString("reason", reason);
                    }),
                    timeout.Token).ConfigureAwait(false);
            }
            catch (Exception)
            {
            }
        });
    }

    private void OnTransportClosed(Exception? error)
    {
        if (Interlocked.Exchange(ref closed, 1) != 0)
        {
            return;
        }

        try
        {
            Closed?.Invoke(error);
        }
        catch (Exception)
        {
        }

        foreach (var id in pending.Keys)
        {
            if (pending.TryRemove(id, out var removed))
            {
                removed.Completion.TrySetException(new McpConnectionClosedException(
                    $"The MCP connection closed before '{removed.Method}' completed.", error));
            }

            transport.RequestSettled(id);
        }

        serverRequests.Writer.TryComplete();
        while (serverRequests.Reader.TryRead(out var document))
        {
            document.Dispose();
        }

        lifetime.Cancel();
    }

    /// <summary>Ids correlate by JSON type and exact value: a client request id is a JSON integer and nothing else.</summary>
    private static bool TryReadId(JsonElement element, out long id)
    {
        id = 0;
        return element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out id);
    }

    private sealed class Pending(string method, TaskCompletionSource<JsonDocument> completion)
    {
        public string Method { get; } = method;

        public TaskCompletionSource<JsonDocument> Completion { get; } = completion;
    }
}
