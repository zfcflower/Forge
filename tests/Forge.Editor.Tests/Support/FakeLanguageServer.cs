using System.IO.Pipelines;
using System.Text;
using System.Text.Json;
using Forge.Editor.Lsp;

namespace Forge.Editor.Tests.Support;

/// <summary>In-process language server peer over two pipes; speaks the same Content-Length framing as a real server.</summary>
internal sealed class FakeLanguageServer : IAsyncDisposable
{
    private static readonly TimeSpan ReceiveTimeout = TimeSpan.FromSeconds(10);

    private readonly Pipe _clientToServer = new();
    private readonly Pipe _serverToClient = new();
    private readonly Stream _fromClient;
    private readonly Stream _toClient;
    private readonly Stream _clientWrites;
    private readonly Stream _clientReads;
    private int _outputClosed;

    public FakeLanguageServer()
    {
        _fromClient = _clientToServer.Reader.AsStream();
        _toClient = _serverToClient.Writer.AsStream();
        _clientWrites = _clientToServer.Writer.AsStream();
        _clientReads = _serverToClient.Reader.AsStream();
        Streams = new ProcessStreams(_clientWrites, _clientReads);
    }

    public ProcessStreams Streams { get; }

    public static byte[] Frame(string json)
    {
        var body = Encoding.UTF8.GetBytes(json);
        return [.. Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n"), .. body];
    }

    public async Task<JsonDocument> ReceiveAsync(CancellationToken ct = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ReceiveTimeout);
        var header = new List<byte>();
        while (header.Count < 4 || header[^4] != '\r' || header[^3] != '\n' || header[^2] != '\r' || header[^1] != '\n')
        {
            header.Add(await ReadByteAsync(timeout.Token).ConfigureAwait(false));
        }

        var text = Encoding.ASCII.GetString(header.ToArray());
        var length = -1;
        foreach (var line in text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
            {
                length = int.Parse(line["Content-Length:".Length..].Trim(), System.Globalization.CultureInfo.InvariantCulture);
            }
        }

        if (length < 0)
        {
            throw new InvalidDataException("Client frame without Content-Length.");
        }

        var body = new byte[length];
        var read = 0;
        while (read < length)
        {
            var n = await _fromClient.ReadAsync(body.AsMemory(read), timeout.Token).ConfigureAwait(false);
            if (n == 0)
            {
                throw new EndOfStreamException();
            }

            read += n;
        }

        return JsonDocument.Parse(body);
    }

    public async Task SendAsync(string json) => await SendRawAsync(Frame(json)).ConfigureAwait(false);

    public async Task SendRawAsync(ReadOnlyMemory<byte> bytes)
    {
        await _toClient.WriteAsync(bytes).ConfigureAwait(false);
        await _toClient.FlushAsync().ConfigureAwait(false);
    }

    public Task RespondAsync(JsonElement id, string resultJson) =>
        SendAsync($"{{\"jsonrpc\":\"2.0\",\"id\":{id.GetRawText()},\"result\":{resultJson}}}");

    public Task RespondErrorAsync(JsonElement id, int code, string message) =>
        SendAsync($"{{\"jsonrpc\":\"2.0\",\"id\":{id.GetRawText()},\"error\":{{\"code\":{code},\"message\":\"{message}\"}}}}");

    public Task NotifyAsync(string method, string paramsJson) =>
        SendAsync($"{{\"jsonrpc\":\"2.0\",\"method\":\"{method}\",\"params\":{paramsJson}}}");

    public Task RequestAsync(int id, string method, string paramsJson) =>
        SendAsync($"{{\"jsonrpc\":\"2.0\",\"id\":{id},\"method\":\"{method}\",\"params\":{paramsJson}}}");

    public void CloseOutput()
    {
        if (Interlocked.Exchange(ref _outputClosed, 1) == 0)
        {
            _serverToClient.Writer.Complete();
        }
    }

    public ValueTask DisposeAsync()
    {
        _clientToServer.Writer.Complete();
        _serverToClient.Writer.Complete();
        _clientToServer.Reader.Complete();
        _serverToClient.Reader.Complete();
        return ValueTask.CompletedTask;
    }

    private async Task<byte> ReadByteAsync(CancellationToken ct)
    {
        var buffer = new byte[1];
        var read = await _fromClient.ReadAsync(buffer, ct).ConfigureAwait(false);
        return read == 0 ? throw new EndOfStreamException() : buffer[0];
    }
}
