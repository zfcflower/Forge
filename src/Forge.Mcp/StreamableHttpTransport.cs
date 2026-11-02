using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace Forge.Mcp;

public sealed class McpHttpOptions
{
    public required Uri Endpoint { get; init; }

    public IReadOnlyDictionary<string, string>? Headers { get; init; }

    /// <summary>Supplied by the host when it wants connection pooling or a proxy of its own.</summary>
    public HttpClient? HttpClient { get; init; }

    public bool OpenServerStream { get; init; } = true;

    public TimeSpan ServerStreamRetryDelay { get; init; } = TimeSpan.FromSeconds(2);

    public int MaxServerStreamRetries { get; init; } = 3;

    public string? SessionId { get; init; }

    /// <summary>Deadline for the session-terminating DELETE and for waiting out a cancelled reader.</summary>
    public TimeSpan SessionShutdownTimeout { get; init; } = TimeSpan.FromSeconds(5);
}

/// <summary>Streamable HTTP transport: JSON-RPC over POST, optional SSE responses and an optional GET stream.</summary>
public sealed class StreamableHttpTransport : IMcpTransport
{
    private static readonly HttpClient SharedClient = CreateSharedClient();
    private static readonly TimeSpan DefaultShutdownTimeout = TimeSpan.FromSeconds(5);

    private readonly Uri endpoint;
    private readonly IReadOnlyDictionary<string, string>? headers;
    private readonly HttpClient http;
    private readonly bool openServerStream;
    private readonly TimeSpan serverStreamRetryDelay;
    private readonly TimeSpan shutdownTimeout;
    private readonly int maxServerStreamRetries;
    private readonly ConcurrentDictionary<long, byte> inFlight = new();
    private readonly ConcurrentDictionary<long, PostStream> postStreams = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly CancellationToken lifetimeToken;
    private readonly Lock gate = new();

    private string? configuredSessionId;
    private string? sessionId;
    private string? protocolVersion;
    private string? serverStreamLastEventId;
    private string? postStreamLastEventId;
    private Task streamTask = Task.CompletedTask;
    private CancellationTokenSource? streamCts;
    private long nextStreamId;
    private int closed;
    private int disposed;
    private int generation;

    public StreamableHttpTransport(McpHttpOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        endpoint = options.Endpoint;
        headers = options.Headers;
        openServerStream = options.OpenServerStream;
        serverStreamRetryDelay = options.ServerStreamRetryDelay;
        maxServerStreamRetries = Math.Max(0, options.MaxServerStreamRetries);
        configuredSessionId = options.SessionId;
        sessionId = options.SessionId;
        shutdownTimeout = options.SessionShutdownTimeout > TimeSpan.Zero ? options.SessionShutdownTimeout : DefaultShutdownTimeout;
        http = options.HttpClient ?? SharedClient;
        lifetimeToken = lifetime.Token;
    }

    public StreamableHttpTransport(Uri endpoint, IReadOnlyDictionary<string, string>? headers = null, HttpClient? httpClient = null)
        : this(new McpHttpOptions { Endpoint = endpoint, Headers = headers, HttpClient = httpClient })
    {
    }

    public string Name => endpoint.ToString();

    public Uri Endpoint => endpoint;

    public string? SessionId
    {
        get
        {
            lock (gate)
            {
                return sessionId;
            }
        }
    }

    public bool IsConnected => Volatile.Read(ref closed) == 0;

    public event Action<ReadOnlyMemory<byte>>? MessageReceived;

    public event Action<Exception?>? Closed;

    public Task StartAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);

        var generationNow = Interlocked.Increment(ref generation);
        Volatile.Write(ref closed, 0);

        CancellationTokenSource? previousStream;
        lock (gate)
        {
            previousStream = streamCts;
            streamCts = null;
            streamTask = Task.CompletedTask;
            sessionId = configuredSessionId;
            protocolVersion = null;
            serverStreamLastEventId = null;
            postStreamLastEventId = null;
        }

        previousStream?.Cancel();
        previousStream?.Dispose();
        CancelPostStreams(oldGeneration: generationNow);
        inFlight.Clear();
        return Task.CompletedTask;
    }

    public void SetProtocolVersion(string protocolVersion)
    {
        lock (gate)
        {
            this.protocolVersion = protocolVersion;
        }
    }

    public async ValueTask SendAsync(ReadOnlyMemory<byte> payload, CancellationToken ct, long? requestId = null)
    {
        if (Volatile.Read(ref closed) != 0)
        {
            throw new McpConnectionClosedException($"The MCP session with '{endpoint}' is closed.");
        }

        if (requestId is { } tracked)
        {
            inFlight[tracked] = 0;
        }

        var generationAtSend = Volatile.Read(ref generation);

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new ReadOnlyMemoryContent(payload),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        ApplyHeaders(request, eventStreamOnly: false);

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            throw new McpTransportException($"POST to MCP endpoint '{endpoint}' failed: {ex.Message}", ex);
        }

        CaptureSession(response);
        var status = (int)response.StatusCode;
        if (status == 404 && SessionId is not null)
        {
            ClearSession();
            response.Dispose();
            var expired = new McpSessionExpiredException($"MCP session with '{endpoint}' expired.");
            RaiseClosed(generationAtSend, expired);
            throw expired;
        }

        if (response.StatusCode is HttpStatusCode.Accepted or HttpStatusCode.NoContent)
        {
            response.Dispose();
            return;
        }

        if (!response.IsSuccessStatusCode)
        {
            var body = await ReadSnippetAsync(response, ct).ConfigureAwait(false);
            response.Dispose();
            throw new McpTransportException($"MCP endpoint '{endpoint}' returned HTTP {status}: {body}");
        }

        var mediaType = response.Content.Headers.ContentType?.MediaType;
        if (string.Equals(mediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
        {
            var stream = new PostStream(Interlocked.Increment(ref nextStreamId), requestId, generationAtSend, lifetimeToken);
            postStreams[stream.Id] = stream;
            _ = Task.Run(() => PumpResponseStreamAsync(response, stream), CancellationToken.None);
            return;
        }

        try
        {
            var body = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            if (body.Length > 0)
            {
                DispatchPayload(body, generationAtSend);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            throw new McpTransportException($"Reading the MCP response from '{endpoint}' failed: {ex.Message}", ex);
        }
        finally
        {
            response.Dispose();
        }
    }

    public void RequestSettled(long requestId)
    {
        inFlight.TryRemove(requestId, out _);
        if (postStreams.IsEmpty)
        {
            return;
        }

        foreach (var stream in postStreams.Values)
        {
            if (stream.RequestId == requestId)
            {
                stream.CancelAndDispose();
            }
        }
    }

    public Task StartServerStreamAsync(CancellationToken ct)
    {
        if (!openServerStream || Volatile.Read(ref closed) != 0)
        {
            return Task.CompletedTask;
        }

        CancellationTokenSource cts;
        lock (gate)
        {
            if (streamCts is not null)
            {
                return Task.CompletedTask;
            }

            cts = CancellationTokenSource.CreateLinkedTokenSource(lifetimeToken);
            streamCts = cts;
        }

        var generationAtStart = Volatile.Read(ref generation);
        streamTask = Task.Run(() => RunServerStreamAsync(cts.Token, generationAtStart), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken ct)
    {
        CancellationTokenSource? streamCts;
        Task streamPending;
        string? session;
        lock (gate)
        {
            streamCts = this.streamCts;
            this.streamCts = null;
            streamPending = streamTask;
            streamTask = Task.CompletedTask;
            session = sessionId;
        }

        Interlocked.Increment(ref generation);
        streamCts?.Cancel();
        var postPending = CancelPostStreams(oldGeneration: null);

        if (session is not null)
        {
            await DeleteSessionAsync(session, ct).ConfigureAwait(false);
        }

        lock (gate)
        {
            sessionId = null;
            serverStreamLastEventId = null;
            postStreamLastEventId = null;
        }

        inFlight.Clear();
        RaiseClosed(Volatile.Read(ref generation), null);
        await AwaitReadersAsync(streamPending, postPending).ConfigureAwait(false);
        streamCts?.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        await lifetime.CancelAsync().ConfigureAwait(false);
        lifetime.Dispose();
    }

    private async Task PumpResponseStreamAsync(HttpResponseMessage response, PostStream stream)
    {
        var token = stream.Token;
        try
        {
            await using var content = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            using var reader = new SseEventStream(content);
            while (true)
            {
                var next = await reader.ReadAsync(token).ConfigureAwait(false);
                if (next is null)
                {
                    break;
                }

                var message = next.Value;
                if (!string.IsNullOrEmpty(message.Id))
                {
                    stream.LastEventId = message.Id;
                    lock (gate)
                    {
                        postStreamLastEventId = message.Id;
                    }
                }

                if (message.Data.Length == 0 || !IsCurrentGeneration(stream.Generation))
                {
                    continue;
                }

                DispatchPayload(Encoding.UTF8.GetBytes(message.Data), stream.Generation);
            }

            if (stream.RequestId is { } id && IsCurrentGeneration(stream.Generation) && inFlight.ContainsKey(id))
            {
                var resumed = stream.LastEventId is null ? string.Empty : $" (last event '{stream.LastEventId}')";
                RaiseClosed(stream.Generation, new McpTransportException($"MCP endpoint '{endpoint}' closed the stream before answering request {id}{resumed}."));
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            if (!stream.CancellationRequested && IsCurrentGeneration(stream.Generation))
            {
                RaiseClosed(stream.Generation, new McpTransportException($"MCP response stream from '{endpoint}' failed: {ex.Message}", ex));
            }
        }
        finally
        {
            postStreams.TryRemove(stream.Id, out _);
            stream.CancelAndDispose();
            stream.Complete();
            response.Dispose();
        }
    }

    private async Task RunServerStreamAsync(CancellationToken ct, int generationAtStart)
    {
        var failures = 0;
        while (!ct.IsCancellationRequested && Volatile.Read(ref closed) == 0)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
                ApplyHeaders(request, eventStreamOnly: true);
                string? resume;
                lock (gate)
                {
                    resume = serverStreamLastEventId ?? postStreamLastEventId;
                }

                if (resume is not null)
                {
                    request.Headers.TryAddWithoutValidation("Last-Event-ID", resume);
                }

                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                CaptureSession(response);
                if (response.StatusCode is HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotFound or HttpStatusCode.NotAcceptable)
                {
                    return;
                }

                if (!response.IsSuccessStatusCode)
                {
                    throw new McpTransportException($"GET on MCP endpoint '{endpoint}' returned HTTP {(int)response.StatusCode}.");
                }

                if (!string.Equals(response.Content.Headers.ContentType?.MediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                failures = 0;
                await using var content = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                using var stream = new SseEventStream(content);
                while (true)
                {
                    var next = await stream.ReadAsync(ct).ConfigureAwait(false);
                    if (next is null)
                    {
                        break;
                    }

                    var message = next.Value;
                    if (!string.IsNullOrEmpty(message.Id))
                    {
                        lock (gate)
                        {
                            serverStreamLastEventId = message.Id;
                        }
                    }

                    if (message.Data.Length == 0 || !IsCurrentGeneration(generationAtStart))
                    {
                        continue;
                    }

                    DispatchPayload(Encoding.UTF8.GetBytes(message.Data), generationAtStart);
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
            {
                return;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or McpTransportException)
            {
                if (++failures > maxServerStreamRetries)
                {
                    RaiseClosed(generationAtStart, new McpTransportException($"MCP server stream from '{endpoint}' failed: {ex.Message}", ex));
                    return;
                }
            }

            try
            {
                await Task.Delay(serverStreamRetryDelay, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task DeleteSessionAsync(string session, CancellationToken ct)
    {
        if (Volatile.Read(ref closed) != 0)
        {
            return;
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(shutdownTimeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Delete, endpoint);
            ApplyHeaders(request, eventStreamOnly: false);
            request.Headers.Remove(McpProtocol.SessionIdHeader);
            request.Headers.TryAddWithoutValidation(McpProtocol.SessionIdHeader, session);
            using var response = await http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException or ObjectDisposedException)
        {
        }
    }

    private async Task AwaitReadersAsync(Task streamPending, IReadOnlyList<Task> postPending)
    {
        try
        {
            if (postPending.Count == 0)
            {
                await streamPending.WaitAsync(shutdownTimeout).ConfigureAwait(false);
                return;
            }

            var readers = new Task[postPending.Count + 1];
            readers[0] = streamPending;
            for (var i = 0; i < postPending.Count; i++)
            {
                readers[i + 1] = postPending[i];
            }

            await Task.WhenAll(readers).WaitAsync(shutdownTimeout).ConfigureAwait(false);
        }
        catch (Exception)
        {
        }
    }

    private List<Task> CancelPostStreams(int? oldGeneration)
    {
        var pending = new List<Task>(postStreams.Count);
        foreach (var stream in postStreams.Values)
        {
            if (oldGeneration is { } generation && stream.Generation >= generation)
            {
                continue;
            }

            pending.Add(stream.Completion);
            stream.CancelAndDispose();
        }

        return pending;
    }

    private void ApplyHeaders(HttpRequestMessage request, bool eventStreamOnly)
    {
        if (headers is { Count: > 0 } custom)
        {
            foreach (var (name, value) in custom)
            {
                request.Headers.TryAddWithoutValidation(name, value);
            }
        }

        request.Headers.Remove("Accept");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        if (eventStreamOnly)
        {
            request.Headers.Accept.Clear();
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        }

        if (SessionId is { } session)
        {
            request.Headers.Remove(McpProtocol.SessionIdHeader);
            request.Headers.TryAddWithoutValidation(McpProtocol.SessionIdHeader, session);
        }

        lock (gate)
        {
            if (protocolVersion is { } version)
            {
                request.Headers.Remove(McpProtocol.ProtocolVersionHeader);
                request.Headers.TryAddWithoutValidation(McpProtocol.ProtocolVersionHeader, version);
            }
        }
    }

    private void CaptureSession(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues(McpProtocol.SessionIdHeader, out var values))
        {
            return;
        }

        foreach (var value in values)
        {
            if (string.IsNullOrEmpty(value))
            {
                continue;
            }

            lock (gate)
            {
                if (!string.Equals(sessionId, value, StringComparison.Ordinal))
                {
                    serverStreamLastEventId = null;
                    postStreamLastEventId = null;
                }

                sessionId = value;
            }

            return;
        }
    }

    private void ClearSession()
    {
        lock (gate)
        {
            sessionId = null;
            serverStreamLastEventId = null;
            postStreamLastEventId = null;
        }
    }

    private bool IsCurrentGeneration(int generationAtMessage) =>
        generationAtMessage == Volatile.Read(ref generation);

    private void DispatchPayload(ReadOnlyMemory<byte> payload, int generationAtMessage)
    {
        if (!IsCurrentGeneration(generationAtMessage))
        {
            return;
        }

        var handler = MessageReceived;
        if (handler is null)
        {
            return;
        }

        TransportDispatch.Dispatch(payload, handler);
    }

    private void RaiseClosed(int generationAtClose, Exception? error)
    {
        if (generationAtClose != Volatile.Read(ref generation))
        {
            return;
        }

        if (Interlocked.Exchange(ref closed, 1) != 0)
        {
            return;
        }

        inFlight.Clear();
        Closed?.Invoke(error);
    }

    private static async Task<string> ReadSnippetAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return body.Length <= 512 ? body : body[..512];
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or ObjectDisposedException)
        {
            return "<unreadable body>";
        }
    }

    private static HttpClient CreateSharedClient()
    {
        var handler = new SocketsHttpHandler
        {
            UseCookies = false,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectTimeout = TimeSpan.FromSeconds(30),
            AutomaticDecompression = DecompressionMethods.None,
        };
        return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    private sealed class PostStream
    {
        private readonly CancellationTokenSource cts;
        private readonly TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int cancelled;

        public PostStream(long id, long? requestId, int generation, CancellationToken lifetimeToken)
        {
            Id = id;
            RequestId = requestId;
            Generation = generation;
            cts = CancellationTokenSource.CreateLinkedTokenSource(lifetimeToken);
            Token = cts.Token;
        }

        public long Id { get; }

        public long? RequestId { get; }

        public int Generation { get; }

        public CancellationToken Token { get; }

        public string? LastEventId { get; set; }

        public Task Completion => completion.Task;

        public bool CancellationRequested => Volatile.Read(ref cancelled) != 0;

        public void Complete() => completion.TrySetResult();

        public void CancelAndDispose()
        {
            if (Interlocked.Exchange(ref cancelled, 1) != 0)
            {
                return;
            }

            try
            {
                cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }

            cts.Dispose();
        }
    }
}
