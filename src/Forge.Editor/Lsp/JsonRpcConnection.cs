using System.Buffers;
using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;

namespace Forge.Editor.Lsp;

public sealed class LspProtocolException(string message, Exception? inner = null) : Exception(message, inner);

public readonly record struct JsonRpcError(int Code, string Message, string? DataJson = null)
{
    public const int MethodNotFound = -32601;
    public const int InternalError = -32603;
}

/// <summary>How a server-initiated request is answered: either a result writer or an error.</summary>
public readonly record struct JsonRpcReply(Action<Utf8JsonWriter>? Result, JsonRpcError? Error = null)
{
    public static JsonRpcReply Null { get; } = new(static w => w.WriteNullValue());

    public static JsonRpcReply EmptyObject { get; } = new(static w =>
    {
        w.WriteStartObject();
        w.WriteEndObject();
    });

    public static JsonRpcReply Unsupported(string message) => new(null, new JsonRpcError(JsonRpcError.MethodNotFound, message));
}

internal sealed class PendingCall(long id, string method)
{
    public long Id { get; } = id;

    public string Method { get; } = method;

    public TaskCompletionSource<JsonElement> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}

/// <summary>
/// JSON-RPC 2.0 over a pair of streams with LSP <c>Content-Length</c> framing. Writes are serialised so a frame is
/// never interleaved; reads run on one loop that completes pending calls, parses notifications and answers server
/// requests off the loop so a slow handler cannot stall the pipe.
/// </summary>
internal sealed class JsonRpcConnection : IAsyncDisposable
{
    private const int ReadBufferSize = 8 * 1024;
    private const int HeaderLimit = 8 * 1024;
    private const int ContentLengthDigits = 10;

    private static ReadOnlySpan<byte> ContentLengthPrefix => "Content-Length: "u8;

    private static ReadOnlySpan<byte> HeaderTerminator => "\r\n\r\n"u8;

    private readonly Stream _input;
    private readonly Stream _output;
    private readonly Func<string, JsonElement, CancellationToken, ValueTask<JsonRpcReply>>? _serverRequests;
    private readonly ConcurrentDictionary<long, PendingCall> _pending = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private readonly byte[] _buffer = new byte[ReadBufferSize];
    private readonly byte[] _readHeader = new byte[HeaderLimit];

    private int _bufferOffset;
    private int _bufferCount;
    private long _nextId;
    private Task? _readLoop;
    private int _closed;

    public JsonRpcConnection(Stream input, Stream output, Func<string, JsonElement, CancellationToken, ValueTask<JsonRpcReply>>? serverRequests = null)
    {
        _input = input;
        _output = output;
        _serverRequests = serverRequests;
    }

    /// <summary>Raw notification sink; the element is only valid for the duration of the call.</summary>
    public event Action<string, JsonElement>? NotificationReceived;

    public event Action<Exception?>? Closed;

    public bool IsClosed => Volatile.Read(ref _closed) != 0;

    public int PendingCount => _pending.Count;

    public void Start() => _readLoop ??= Task.Run(() => ReadLoopAsync(_cts.Token));

    public async ValueTask<JsonElement> SendRequestAsync(string method, Action<Utf8JsonWriter>? parameters, CancellationToken ct)
    {
        if (IsClosed)
        {
            throw new LspProtocolException("The language server connection is closed.");
        }

        var id = Interlocked.Increment(ref _nextId);
        var pending = new PendingCall(id, method);
        _pending[id] = pending;
        using var registration = ct.Register(static state =>
        {
            var (connection, call) = ((JsonRpcConnection, PendingCall))state!;
            connection.CancelCall(call);
        }, (this, pending));

        try
        {
            await WriteMessageAsync(writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("jsonrpc", "2.0");
                writer.WriteNumber("id", id);
                writer.WriteString("method", method);
                if (parameters is not null)
                {
                    writer.WritePropertyName("params");
                    parameters(writer);
                }

                writer.WriteEndObject();
            }, ct).ConfigureAwait(false);

            return await pending.Completion.Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (pending.Completion.Task.IsCanceled)
        {
            throw new OperationCanceledException(ct);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    public ValueTask SendNotificationAsync(string method, Action<Utf8JsonWriter>? parameters, CancellationToken ct = default) =>
        WriteMessageAsync(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("jsonrpc", "2.0");
            writer.WriteString("method", method);
            if (parameters is not null)
            {
                writer.WritePropertyName("params");
                parameters(writer);
            }

            writer.WriteEndObject();
        }, ct);

    public async ValueTask DisposeAsync()
    {
        Close(null);
        if (_readLoop is not null)
        {
            try
            {
                await _readLoop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        _writeLock.Dispose();
        _cts.Dispose();
    }

    public void Close(Exception? reason)
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0)
        {
            return;
        }

        try
        {
            _cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        foreach (var call in _pending.Values)
        {
            call.Completion.TrySetException(new LspProtocolException("The language server connection closed.", reason));
        }

        _pending.Clear();
        Closed?.Invoke(reason);
    }

    private void CancelCall(PendingCall call)
    {
        _pending.TryRemove(call.Id, out _);
        call.Completion.TrySetCanceled();
        if (IsClosed)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await SendNotificationAsync("$/cancelRequest", writer =>
                {
                    writer.WriteStartObject();
                    writer.WriteNumber("id", call.Id);
                    writer.WriteEndObject();
                }).ConfigureAwait(false);
            }
            catch (Exception)
            {
            }
        });
    }

    private async ValueTask WriteMessageAsync(Action<Utf8JsonWriter> write, CancellationToken ct)
    {
        var payload = new ArrayBufferWriter<byte>(256);
        using (var writer = new Utf8JsonWriter(payload, new JsonWriterOptions { SkipValidation = true }))
        {
            write(writer);
        }

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var frameLength = payload.WrittenCount;
            var frame = ArrayPool<byte>.Shared.Rent(ContentLengthPrefix.Length + ContentLengthDigits + HeaderTerminator.Length + frameLength);
            try
            {
                var span = frame.AsSpan();
                ContentLengthPrefix.CopyTo(span);
                if (!Utf8Formatter.TryFormat(frameLength, span[ContentLengthPrefix.Length..], out var digits))
                {
                    throw new LspProtocolException($"Message of {frameLength} bytes does not fit a Content-Length header.");
                }

                var headerLength = ContentLengthPrefix.Length + digits;
                HeaderTerminator.CopyTo(span[headerLength..]);
                headerLength += HeaderTerminator.Length;
                payload.WrittenSpan.CopyTo(span[headerLength..]);
                await _output.WriteAsync(frame.AsMemory(0, headerLength + frameLength), CancellationToken.None).ConfigureAwait(false);
                await _output.FlushAsync(CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(frame);
            }
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var length = await ReadContentLengthAsync(ct).ConfigureAwait(false);
                if (length < 0)
                {
                    Close(null);
                    return;
                }

                var body = ArrayPool<byte>.Shared.Rent(Math.Max(length, 1));
                try
                {
                    await ReadBodyAsync(body.AsMemory(0, length), ct).ConfigureAwait(false);
                    using var document = JsonDocument.Parse(body.AsMemory(0, length));
                    Dispatch(document.RootElement);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(body);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Close(null);
        }
        catch (Exception ex)
        {
            Close(ex);
        }
    }

    private void Dispatch(JsonElement message)
    {
        if (message.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        var hasId = message.TryGetProperty("id", out var id);
        var hasMethod = message.TryGetProperty("method", out var method);

        if (hasId && !hasMethod)
        {
            if (id.ValueKind == JsonValueKind.Number && id.TryGetInt64(out var callId) && _pending.TryRemove(callId, out var call))
            {
                if (message.TryGetProperty("error", out var error))
                {
                    call.Completion.TrySetException(new LspProtocolException(
                        error.TryGetProperty("message", out var text) ? text.GetString() ?? "Unknown error" : "Unknown error"));
                }
                else if (message.TryGetProperty("result", out var result))
                {
                    call.Completion.TrySetResult(result.Clone());
                }
                else
                {
                    call.Completion.TrySetResult(default);
                }
            }

            return;
        }

        if (hasId && hasMethod)
        {
            var parameterElement = message.TryGetProperty("params", out var parameters) ? parameters.Clone() : default;
            var idJson = id.GetRawText();
            var methodName = method.GetString() ?? string.Empty;
            _ = Task.Run(() => AnswerServerRequestAsync(idJson, methodName, parameterElement));
            return;
        }

        if (hasMethod)
        {
            NotificationReceived?.Invoke(method.GetString() ?? string.Empty, message);
        }
    }

    private async Task AnswerServerRequestAsync(string idJson, string method, JsonElement parameters)
    {
        JsonRpcReply reply;
        if (_serverRequests is null)
        {
            reply = JsonRpcReply.Unsupported(method);
        }
        else
        {
            try
            {
                reply = await _serverRequests(method, parameters, _cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                reply = new JsonRpcReply(null, new JsonRpcError(JsonRpcError.InternalError, ex.Message));
            }
        }

        if (IsClosed)
        {
            return;
        }

        try
        {
            await WriteMessageAsync(writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("jsonrpc", "2.0");
                writer.WritePropertyName("id");
                writer.WriteRawValue(idJson, skipInputValidation: true);
                if (reply.Error is { } error)
                {
                    writer.WritePropertyName("error");
                    writer.WriteStartObject();
                    writer.WriteNumber("code", error.Code);
                    writer.WriteString("message", error.Message);
                    writer.WriteEndObject();
                }
                else
                {
                    writer.WritePropertyName("result");
                    if (reply.Result is { } result)
                    {
                        result(writer);
                    }
                    else
                    {
                        writer.WriteNullValue();
                    }
                }

                writer.WriteEndObject();
            }, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            Close(ex);
        }
    }

    private async ValueTask<int> ReadContentLengthAsync(CancellationToken ct)
    {
        var headerLength = 0;
        var contentLength = -1;
        while (true)
        {
            var b = await ReadByteAsync(ct).ConfigureAwait(false);
            if (b < 0)
            {
                return -1;
            }

            if (b != '\n')
            {
                if (headerLength >= _readHeader.Length)
                {
                    throw new LspProtocolException("Header section is too long.");
                }

                _readHeader[headerLength++] = (byte)b;
                continue;
            }

            var lineLength = headerLength > 0 && _readHeader[headerLength - 1] == '\r' ? headerLength - 1 : headerLength;
            var line = Encoding.ASCII.GetString(_readHeader, 0, lineLength);
            headerLength = 0;
            if (line.Length == 0)
            {
                if (contentLength < 0)
                {
                    throw new LspProtocolException("Message headers did not contain Content-Length.");
                }

                return contentLength;
            }

            var colon = line.IndexOf(':');
            if (colon > 0 && line.AsSpan(0, colon).Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                if (!int.TryParse(line.AsSpan(colon + 1).Trim(), out var parsed) || parsed < 0)
                {
                    throw new LspProtocolException($"Invalid Content-Length header: '{line}'.");
                }

                contentLength = parsed;
            }
        }
    }

    private async ValueTask<int> ReadByteAsync(CancellationToken ct)
    {
        if (_bufferOffset >= _bufferCount)
        {
            _bufferCount = await _input.ReadAsync(_buffer, ct).ConfigureAwait(false);
            _bufferOffset = 0;
            if (_bufferCount == 0)
            {
                return -1;
            }
        }

        return _buffer[_bufferOffset++];
    }

    private async ValueTask ReadBodyAsync(Memory<byte> destination, CancellationToken ct)
    {
        var copied = 0;
        while (copied < destination.Length)
        {
            if (_bufferOffset < _bufferCount)
            {
                var available = Math.Min(_bufferCount - _bufferOffset, destination.Length - copied);
                _buffer.AsSpan(_bufferOffset, available).CopyTo(destination.Span[copied..]);
                _bufferOffset += available;
                copied += available;
                continue;
            }

            var read = await _input.ReadAsync(destination[copied..], ct).ConfigureAwait(false);
            if (read == 0)
            {
                throw new LspProtocolException("Unexpected end of stream inside a message body.");
            }

            copied += read;
        }
    }
}
