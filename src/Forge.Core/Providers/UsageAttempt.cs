using System.Diagnostics;
using Forge.Abstractions;

namespace Forge.Core.Providers;

/// <summary>
/// One HTTP attempt as the usage log sees it: started when the request is built, closed when it completes or fails, and recorded
/// exactly once either way. Kept separate from the provider so the recording rules (one record per attempt, never throw) sit in one
/// place rather than inside the streaming loop.
/// </summary>
internal sealed class UsageAttempt
{
    private readonly string providerId;
    private readonly IUsageRecorder? recorder;
    private readonly ChatRequest request;
    private readonly string? entrypoint;
    private readonly bool downgraded;
    private readonly long started = Stopwatch.GetTimestamp();
    private HttpResponseMessage? response;
    private TokenUsage usage = TokenUsage.Empty;
    private int? status;
    private string? requestId;
    private ProviderRateLimit? rateLimit;
    private bool recorded;

    public UsageAttempt(string providerId, ChatRequest request, string? entrypoint, bool downgraded, IUsageRecorder? recorder)
    {
        this.providerId = providerId;
        this.request = request;
        this.entrypoint = entrypoint;
        this.downgraded = downgraded;
        this.recorder = recorder;
    }

    /// <summary>The response headers arrived: keep what only they know, since the body may still fail.</summary>
    public void Observe(HttpResponseMessage received)
    {
        response = received;
        status = (int)received.StatusCode;
        requestId = received.Headers.TryGetValues("request-id", out var values) ? values.FirstOrDefault() : null;
        rateLimit = AnthropicRateLimits.Read(received.Headers);
    }

    public void Completed(TokenUsage reported) => usage = reported;

    /// <summary>A failure that ends this attempt: recorded with its status, then the caller rethrows.</summary>
    public void Failed(Exception exception)
    {
        if (exception is ProviderException provider)
        {
            status ??= provider.StatusCode;
        }

        Record(UsageRecord.Error);
    }

    private void Record(string outcome)
    {
        if (recorded || recorder is not { } sink)
        {
            recorded = true;
            return;
        }

        recorded = true;
        sink.Record(new UsageRecord(
            DateTimeOffset.UtcNow,
            request.SessionId,
            providerId,
            request.Model.ProviderId is { Length: > 0 } ? request.Model.ProviderId : providerId,
            request.Model.Model,
            entrypoint,
            request.Cache?.Ttl,
            downgraded,
            outcome,
            status,
            usage,
            (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds,
            requestId,
            rateLimit));
    }

    /// <summary>Closed at the end of the stream, which is also where a completed attempt gets its one record.</summary>
    public void Close() => Record(UsageRecord.Ok);
}
