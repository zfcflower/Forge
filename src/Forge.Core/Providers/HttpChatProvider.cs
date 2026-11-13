using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using Forge.Abstractions;

namespace Forge.Core.Providers;

/// <summary>HTTP + SSE plumbing shared by the built-in streaming providers.</summary>
public abstract class HttpChatProvider : IChatProvider
{
    private const int MaxErrorBodyLength = 4096;

    /// <summary>How long one stream read may wait for the next chunk before the turn is treated as stalled.</summary>
    public static TimeSpan DefaultIdleTimeout => TimeSpan.FromSeconds(120);

    private readonly ProviderConfig _config;
    private readonly HttpClient _http;
    private readonly ProviderOptions _options;
    private TimeSpan _idleTimeout;

    protected HttpChatProvider(
        ProviderConfig config,
        HttpClient http,
        TimeSpan? idleTimeout = null,
        ProviderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(http);
        _config = config;
        _http = http;
        _options = options ?? ProviderOptions.Default;
        _idleTimeout = ValidateIdleTimeout(idleTimeout ?? DefaultIdleTimeout);
    }

    /// <summary>The wire switches this provider was created with, including where usage records go.</summary>
    protected ProviderOptions Options => _options;

    public string Id => _config.Id;

    /// <summary>
    /// Maximum time to wait for the next chunk of the response body. Use <see cref="Timeout.InfiniteTimeSpan"/>
    /// to disable. Expiry is retryable and distinct from caller cancellation.
    /// </summary>
    public TimeSpan IdleTimeout
    {
        get => _idleTimeout;
        set => _idleTimeout = ValidateIdleTimeout(value);
    }

    protected abstract string Endpoint { get; }

    protected HttpClient Http => _http;

    protected ProviderConfig Config => _config;

    protected abstract void ApplyAuth(HttpRequestMessage request);

    protected abstract byte[] BuildBody(ChatRequest request);

    protected abstract IAsyncEnumerable<ChatStreamEvent> ParseAsync(SseReader reader, CancellationToken ct);

    /// <summary>
    /// The account usage windows this response reported, if its protocol has such a meter. Read once per response, before the
    /// body, and attached to the turn's <see cref="MessageCompleted"/> and — when the request was rejected — to the
    /// <see cref="ProviderException"/>. Base returns null: a protocol without the meter reports nothing, which is not the same
    /// as an account with an empty window.
    /// </summary>
    protected virtual ProviderRateLimit? ReadRateLimit(HttpResponseMessage response) => null;

    /// <summary>
    /// Headers that need the request rather than the provider (a session id, for one). Applied after the credential and
    /// before the user's own headers, so a configured header still wins.
    /// </summary>
    protected virtual void ApplyRequestHeaders(HttpRequestMessage message, ChatRequest request)
    {
    }

    /// <summary>Query string appended to the endpoint, empty unless the protocol's real client always sends one.</summary>
    protected virtual string EndpointQuery => string.Empty;

    /// <summary>
    /// Whether a failure means the request asked for more than the server offers and should be sent once more in a reduced shape
    /// (a beta the account does not accept, for one). At most one downgrade per request: a second failure is a real error.
    /// </summary>
    protected virtual bool CanDegrade(ProviderException failure) => false;

    /// <summary>Called once before the downgraded resend, so the adapter can drop whatever the server refused.</summary>
    protected virtual void Degrade()
    {
    }

    /// <summary>A sentence explaining the downgrade, attached to the turn's completion for the shell to show; null when not degraded.</summary>
    protected virtual string? DegradationNotice => null;

    public async IAsyncEnumerable<ChatStreamEvent> StreamAsync(
        ChatRequest request,
        [EnumeratorCancellation] CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ct.ThrowIfCancellationRequested();

        var body = BuildBody(request);
        var downgraded = false;
        UsageAttempt? attempt = null;
        HttpResponseMessage response;
        while (true)
        {
            using var message = CreateRequest(body, request);
            attempt = new UsageAttempt(Config.Id, request, Config.Wire?.Entrypoint, downgraded, Options.UsageRecorder);
            try
            {
                response = await SendAsync(message, ct).ConfigureAwait(false);
                attempt.Observe(response);
                break;
            }
            catch (ProviderException failure) when (!downgraded && CanDegrade(failure))
            {
                // The server refused part of the shape rather than the request: drop it and send once more, then remember for the
                // rest of the process that this provider does not accept it. The refused attempt is recorded like any other.
                attempt.Failed(failure);
                downgraded = true;
                Degrade();
                body = BuildBody(request);
            }
            catch (ProviderException failure)
            {
                attempt.Failed(failure);
                throw;
            }
            catch (Exception exception)
            {
                attempt.Failed(exception);
                throw;
            }
        }

        using var lease = response;
        var rateLimit = ReadRateLimit(response);
        var notice = downgraded ? DegradationNotice : null;
        var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var idle = new IdleTimeoutStream(stream, _idleTimeout);
        var settled = attempt;
        await using (idle.ConfigureAwait(false))
        {
            var reader = new SseReader(idle);
            try
            {
                await foreach (var evt in ParseAsync(reader, ct).WithCancellation(ct).ConfigureAwait(false))
                {
                    if (evt is MessageCompleted done)
                    {
                        settled?.Completed(done.Usage);
                    }

                    yield return evt is MessageCompleted completed && (rateLimit is not null || notice is not null)
                        ? completed with { RateLimit = rateLimit ?? completed.RateLimit, Notice = notice }
                        : evt;
                }
            }
            finally
            {
                // One record per attempt, written when the stream ends however it ends — a cancelled turn included.
                settled?.Close();
            }
        }
    }

    /// <summary>Reads one SSE event, translating transport failures into retryable provider errors.</summary>
    protected async ValueTask<SseEvent?> ReadEventAsync(SseReader reader, CancellationToken ct)
    {
        try
        {
            return await reader.ReadAsync(ct).ConfigureAwait(false);
        }
        catch (TimeoutException ex)
        {
            throw new ProviderException($"{Id}: stream idle timeout.", retryable: true, inner: ex);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ProviderException($"{Id}: response stream timed out.", retryable: true);
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException)
        {
            throw new ProviderException($"{Id}: response stream failed: {ex.Message}", retryable: true, inner: ex);
        }
    }

    private static TimeSpan ValidateIdleTimeout(TimeSpan value)
    {
        if (value != Timeout.InfiniteTimeSpan && value <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(value),
                value,
                "The idle timeout must be positive or Timeout.InfiniteTimeSpan.");
        }

        return value;
    }

    protected ProviderException Truncated() =>
        new($"{Id}: response stream ended before the turn completed.", retryable: true);

    protected ProviderException Malformed(string payload, Exception? inner = null) =>
        new($"{Id}: malformed stream payload: {payload}", retryable: false, inner: inner);

    private HttpRequestMessage CreateRequest(byte[] body, ChatRequest request)
    {
        var message = new HttpRequestMessage(HttpMethod.Post, BuildUri())
        {
            Content = new ByteArrayContent(body),
        };
        message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        ApplyAuth(message);
        ApplyRequestHeaders(message, request);

        if (_config.Headers is { Count: > 0 } headers)
        {
            ProviderHeaders.Apply(message, headers, request.SessionId);
        }

        return message;
    }

    private Uri BuildUri()
    {
        var baseUrl = _config.BaseUrl?.Trim().TrimEnd('/');
        if (string.IsNullOrEmpty(baseUrl))
        {
            throw new ProviderException($"{Id}: BaseUrl is not configured.", retryable: false);
        }

        return new Uri($"{baseUrl}/{Endpoint}{EndpointQuery}", UriKind.Absolute);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage message, CancellationToken ct)
    {
        HttpResponseMessage response;
        try
        {
            response = await _http
                .SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ProviderException($"{Id}: request timed out.", retryable: true);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            throw new ProviderException($"{Id}: request failed: {ex.Message}", retryable: true, inner: ex);
        }

        if (response.IsSuccessStatusCode)
        {
            return response;
        }

        var status = response.StatusCode;
        var retryAfter = response.Headers.RetryAfter;
        var rateLimit = ReadRateLimit(response);
        using (response)
        {
            var text = await ReadErrorBodyAsync(response, ct).ConfigureAwait(false);
            throw ProviderErrors.FromStatus(Id, status, text, retryAfter, rateLimit);
        }
    }

    private static async Task<string> ReadErrorBodyAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var buffer = new byte[MaxErrorBodyLength];
            var total = 0;
            while (total < buffer.Length)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(total), ct).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                total += read;
            }

            return total == 0 ? string.Empty : Encoding.UTF8.GetString(buffer, 0, total);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return "<unreadable body>";
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException)
        {
            return "<unreadable body>";
        }
    }
}
