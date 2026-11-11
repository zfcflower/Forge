using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using Forge.Abstractions;

namespace Forge.Core.Providers;

/// <summary>
/// Retries only failures that happen before the first event reaches the caller, so a consumer never
/// observes duplicated output.
/// </summary>
public sealed class RetryingChatProvider : IChatProvider
{
    private static readonly TimeSpan DefaultBaseDelay = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan DefaultMaxDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DefaultMaxRetryAfter = TimeSpan.FromMinutes(5);

    private readonly IChatProvider _inner;
    private readonly int _maxAttempts;
    private readonly TimeSpan _baseDelay;
    private readonly TimeSpan _maxDelay;
    private readonly TimeSpan _maxRetryAfter;
    private readonly Func<TimeSpan, CancellationToken, ValueTask> _delay;
    private readonly Func<TimeSpan, TimeSpan> _jitter;

    public RetryingChatProvider(
        IChatProvider inner,
        int maxAttempts = 3,
        TimeSpan? baseDelay = null,
        TimeSpan? maxDelay = null,
        Func<TimeSpan, CancellationToken, ValueTask>? delayAsync = null,
        Func<TimeSpan, TimeSpan>? jitter = null,
        TimeSpan? maxRetryAfter = null)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxAttempts, 1);

        _inner = inner;
        _maxAttempts = maxAttempts;
        _baseDelay = baseDelay ?? DefaultBaseDelay;
        _maxDelay = maxDelay ?? DefaultMaxDelay;
        _maxRetryAfter = maxRetryAfter ?? DefaultMaxRetryAfter;
        _delay = delayAsync ?? DefaultDelayAsync;
        _jitter = jitter ?? EqualJitter;
    }

    public string Id => _inner.Id;

    public int MaxAttempts => _maxAttempts;

    public async IAsyncEnumerable<ChatStreamEvent> StreamAsync(
        ChatRequest request,
        [EnumeratorCancellation] CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        for (var attempt = 1; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            var enumerator = _inner.StreamAsync(request, ct).GetAsyncEnumerator(ct);
            var yielded = false;
            Exception? failure = null;
            try
            {
                while (true)
                {
                    bool moved;
                    try
                    {
                        moved = await enumerator.MoveNextAsync().ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        moved = false;
                        failure = ex;
                    }

                    if (failure is not null)
                    {
                        break;
                    }

                    if (!moved)
                    {
                        yield break;
                    }

                    yielded = true;
                    yield return enumerator.Current;
                }
            }
            finally
            {
                await enumerator.DisposeAsync().ConfigureAwait(false);
            }

            if (failure is null || yielded || attempt >= _maxAttempts || !IsRetryable(failure, ct) || IsQuota(failure))
            {
                throw Rethrow(failure ?? new ProviderException($"{_inner.Id}: stream ended unexpectedly.", retryable: true));
            }

            await _delay(ComputeDelay(attempt, failure), ct).ConfigureAwait(false);
        }
    }

    /// <summary>A spent quota (not a short rate limit) never comes back within a retry; the automation flow waits for it instead.</summary>
    private static bool IsQuota(Exception failure) =>
        failure is ProviderException provider
        && ProviderQuota.Classify(provider, new ModelSettings(string.Empty), DateTimeOffset.UtcNow) is not null;

    private static bool IsRetryable(Exception failure, CancellationToken ct) => failure switch
    {
        ProviderException provider => provider.Retryable,
        OperationCanceledException => false,
        HttpRequestException or IOException or TimeoutException => !ct.IsCancellationRequested,
        _ => false,
    };

    private TimeSpan ComputeDelay(int attempt, Exception failure)
    {
        if (failure is ProviderException provider
            && ProviderErrors.TryGetRetryAfter(provider, out var retryAfter)
            && retryAfter > TimeSpan.Zero)
        {
            return retryAfter > _maxRetryAfter ? _maxRetryAfter : retryAfter;
        }

        var baseMilliseconds = _baseDelay.TotalMilliseconds;
        var maxMilliseconds = _maxDelay.TotalMilliseconds;
        var milliseconds = baseMilliseconds * Math.Pow(2, attempt - 1);
        if (double.IsNaN(milliseconds) || milliseconds > maxMilliseconds)
        {
            milliseconds = maxMilliseconds;
        }

        if (milliseconds < 0)
        {
            milliseconds = 0;
        }

        return _jitter(TimeSpan.FromMilliseconds(milliseconds));
    }

    private static TimeSpan EqualJitter(TimeSpan ceiling)
    {
        if (ceiling <= TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        var half = ceiling / 2;
        return half + (half * Random.Shared.NextDouble());
    }

    private static async ValueTask DefaultDelayAsync(TimeSpan delay, CancellationToken ct)
    {
        await Task.Delay(delay, ct).ConfigureAwait(false);
    }

    private static Exception Rethrow(Exception exception)
    {
        ExceptionDispatchInfo.Capture(exception).Throw();
        return exception;
    }
}
