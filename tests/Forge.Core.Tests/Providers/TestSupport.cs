using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Forge.Abstractions;
using Forge.Core.Providers;
using Xunit;

namespace Forge.Core.Tests.Providers;

internal sealed record RecordedRequest(
    Uri Uri,
    string? Body,
    IReadOnlyDictionary<string, string> Headers,
    IReadOnlyDictionary<string, string> ContentHeaders)
{
    public JsonDocument Json() => JsonDocument.Parse(Body ?? "{}");

    public string? Header(string name) => Headers.TryGetValue(name, out var value) ? value : null;

    public JsonElement Root => Json().RootElement;
}

internal sealed class FakeHttpMessageHandler : HttpMessageHandler
{
    private readonly Queue<Func<HttpResponseMessage>> _responses = new();

    public List<RecordedRequest> Requests { get; } = [];

    public int Calls => Requests.Count;

    public RecordedRequest Last => Requests[^1];

    public FakeHttpMessageHandler Enqueue(Func<HttpResponseMessage> response)
    {
        _responses.Enqueue(response);
        return this;
    }

    public FakeHttpMessageHandler EnqueueSse(string sse) => Enqueue(() => SseResponse(sse));

    public FakeHttpMessageHandler EnqueueStatus(HttpStatusCode status, string body = "", TimeSpan? retryAfter = null) =>
        Enqueue(() =>
        {
            var response = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8) };
            if (retryAfter is { } delay)
            {
                response.Headers.RetryAfter = new RetryConditionHeaderValue(delay);
            }

            return response;
        });

    public static HttpResponseMessage SseResponse(string sse) =>
        new(HttpStatusCode.OK) { Content = new StringContent(sse, Encoding.UTF8, "text/event-stream") };

    public static HttpResponseMessage StreamResponse(Stream stream) =>
        new(HttpStatusCode.OK) { Content = new StreamContent(stream) };

    public HttpClient CreateClient() => new(this, disposeHandler: false);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
        var contentHeaders = request.Content is null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : request.Content.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);

        Requests.Add(new RecordedRequest(request.RequestUri!, body, headers, contentHeaders));

        if (_responses.Count == 0)
        {
            throw new InvalidOperationException("FakeHttpMessageHandler has no queued response.");
        }

        return _responses.Dequeue()();
    }
}

internal static class TestData
{
    public static string Sse(params string[] payloads)
    {
        var builder = new StringBuilder();
        foreach (var payload in payloads)
        {
            builder.Append("data: ").Append(payload).Append("\n\n");
        }

        return builder.ToString();
    }

    public static string WithEvent(string name, string payload) => $"event: {name}\ndata: {payload}\n\n";

    /// <summary>An OpenAI-compatible provider over a fake handler primed with one SSE stream built from
    /// <paramref name="payloads"/>, together with that handler.</summary>
    public static (OpenAiCompatibleProvider Provider, FakeHttpMessageHandler Handler) OpenAiProvider(params string[] payloads)
    {
        var handler = new FakeHttpMessageHandler().EnqueueSse(Sse(payloads));
        return (new OpenAiCompatibleProvider(OpenAiConfig(), handler.CreateClient()), handler);
    }

    /// <summary>An Anthropic provider over a fake handler primed with a stream carrying only the terminating
    /// <c>message_stop</c> event, together with that handler.</summary>
    public static (AnthropicProvider Provider, FakeHttpMessageHandler Handler) AnthropicProvider()
    {
        var handler = new FakeHttpMessageHandler().EnqueueSse(WithEvent("message_stop", """{"type":"message_stop"}"""));
        return (new AnthropicProvider(AnthropicConfig(), handler.CreateClient()), handler);
    }

    /// <summary>An OpenAI Responses provider over a fake handler primed with only the terminal <c>response.completed</c> event.</summary>
    public static (OpenAiResponsesProvider Provider, FakeHttpMessageHandler Handler) ResponsesProvider()
    {
        var handler = new FakeHttpMessageHandler().EnqueueSse(WithEvent(
            "response.completed",
            """{"type":"response.completed","response":{"id":"resp_1","status":"completed"}}"""));
        return (
            new OpenAiResponsesProvider(
                new ProviderConfig("responses", ProviderFactory.OpenAiResponsesKind, "https://api.example.test/v1", "sk-test"),
                handler.CreateClient()),
            handler);
    }

    public static ProviderConfig OpenAiConfig(string baseUrl = "https://api.example.test/v1") =>
        new("openai", ProviderFactory.OpenAiCompatibleKind, baseUrl, "sk-test");

    public static ProviderConfig AnthropicConfig(string baseUrl = "https://api.anthropic.test") =>
        new("anthropic", ProviderFactory.AnthropicKind, baseUrl, "ak-test");

    public static ChatRequest Request(
        ModelSettings? model = null,
        IReadOnlyList<ChatMessage>? messages = null,
        IReadOnlyList<ToolDefinition>? tools = null,
        string system = "system-prompt",
        CacheHint? cache = null) =>
        new(system,
            tools ?? [],
            messages ?? [ChatMessage.User("hello")],
            model ?? new ModelSettings("test-model"),
            cache);

    public static ToolDefinition ReadFileTool() => new(
        "read_file",
        "Reads a file",
        """{"type":"object","properties":{"path":{"type":"string"}},"required":["path"]}""");
}

internal static class ProviderTestExtensions
{
    public static async Task<List<ChatStreamEvent>> CollectAsync(
        this IChatProvider provider,
        ChatRequest request,
        CancellationToken ct = default)
    {
        var events = new List<ChatStreamEvent>();
        await foreach (var evt in provider.StreamAsync(request, ct).ConfigureAwait(false))
        {
            events.Add(evt);
        }

        return events;
    }

    public static MessageCompleted Completed(this List<ChatStreamEvent> events) =>
        Assert.IsType<MessageCompleted>(events[^1]);

    public static string Text(this List<ChatStreamEvent> events) =>
        string.Concat(events.OfType<TextDelta>().Select(d => d.Text));

    public static string Thinking(this List<ChatStreamEvent> events) =>
        string.Concat(events.OfType<ThinkingDelta>().Select(d => d.Text));

    public static T Block<T>(this List<ChatStreamEvent> events) where T : ContentBlock
    {
        var completed = events.Completed();
        return Assert.IsType<T>(completed.Message.Content.Single(b => b is T));
    }
}

internal sealed class ChunkedStream(IEnumerable<byte[]> chunks) : Stream
{
    private readonly byte[] _all = chunks.SelectMany(c => c).ToArray();
    private int _position;

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => _all.Length;

    public override long Position
    {
        get => _position;
        set => throw new NotSupportedException();
    }

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var remaining = _all.Length - _position;
        var take = Math.Min(count, remaining);
        Array.Copy(_all, _position, buffer, offset, take);
        _position += take;
        return take;
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var remaining = _all.Length - _position;
        var take = Math.Min(buffer.Length, remaining);
        _all.AsSpan(_position, take).CopyTo(buffer.Span);
        _position += take;
        return ValueTask.FromResult(take);
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>Emits a prefix, then blocks until the caller cancels.</summary>
internal sealed class HangingStream(byte[] prefix) : Stream
{
    private int _position;

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => _position;
        set => throw new NotSupportedException();
    }

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_position < prefix.Length)
        {
            var take = Math.Min(buffer.Length, prefix.Length - _position);
            prefix.AsSpan(_position, take).CopyTo(buffer.Span);
            _position += take;
            return take;
        }

        await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
        return 0;
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>Emits a prefix, then fails the way a dropped connection does.</summary>
internal sealed class FaultingStream(byte[] prefix) : Stream
{
    private int _position;

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => _position;
        set => throw new NotSupportedException();
    }

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new IOException("connection reset");

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_position < prefix.Length)
        {
            var take = Math.Min(buffer.Length, prefix.Length - _position);
            prefix.AsSpan(_position, take).CopyTo(buffer.Span);
            _position += take;
            return ValueTask.FromResult(take);
        }

        throw new IOException("connection reset");
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
