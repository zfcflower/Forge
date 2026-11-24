using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using Forge.Abstractions;
using Forge.Core.Agents;

namespace Forge.Core.Providers;

/// <summary>
/// Retries only failures that happen before the first event reaches the caller, so a consumer never observes duplicated
/// output. Every attempt also spends the provider's local budget first (<see cref="ProviderRateLimiter"/>), which is the one
/// place in the request path where the budget can be charged exactly once per attempt — a retry is another request.
/// <para>
/// The ladder depends on why the attempt failed, because the two cases have nothing in common: a dropped connection comes
/// back in milliseconds, while an endpoint that answers 429 "overloaded" is shedding load for tens of seconds and a
/// half-second of backoff only burns the attempts. So a transport or server-side failure keeps a short ladder (three
/// attempts), and a 429 walks <see cref="OverloadDelays"/> (five attempts, about half a minute), with one cap on the total
/// wait a request may spend between attempts (<see cref="MaxRetryBudget"/>) so the retry path is never frozen longer than
/// that. Every wait is announced as a <see cref="RetryWait"/> before it is slept; the caller gives up loudly instead of
/// sleeping past the cap. (A wait on our own budget is a different delay with its own, longer ceiling —
/// <c>_maxRetryAfter</c>: a refilling bucket is not the endpoint refusing us.)
/// </para>
/// </summary>
public sealed class RetryingChatProvider : IChatProvider
{
    private static readonly TimeSpan DefaultBaseDelay = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan DefaultMaxDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DefaultMaxRetryAfter = TimeSpan.FromMinutes(5);

    /// <summary>
    /// The wait before each further attempt after a 429, in order: 1s, 3s, 8s, 20s. Chosen against what the endpoint actually
    /// does — a gateway shedding load recovers in seconds to tens of seconds, so the ladder has to outlast the first few
    /// seconds instead of spending every attempt inside them, and its last step still has to fit under
    /// <see cref="MaxRetryBudget"/> together with the ones before it.
    /// </summary>
    private static readonly TimeSpan[] OverloadDelays =
    [
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(3),
        TimeSpan.FromSeconds(8),
        TimeSpan.FromSeconds(20),
    ];

    /// <summary>
    /// How long one request may spend asleep between attempts. Past it the wait stops being jitter and becomes a spent
    /// window: the turn is handed back with the failure (and the server's own delay) instead of being frozen, which is also
    /// what keeps the automation flow's quota handling on the one path it already knows.
    /// </summary>
    private static readonly TimeSpan MaxRetryBudget = TimeSpan.FromSeconds(60);

    /// <summary>
    /// A wait shorter than this is not reported as a <see cref="BudgetWait"/> event: sub-second hiccups explain nothing
    /// the user can act on and would only flicker a line onto the running turn. It is a noise threshold for the UI, not a
    /// setting — nobody tunes it, so it stays a private constant.
    /// </summary>
    private static readonly TimeSpan MinReportedBudgetWait = TimeSpan.FromSeconds(3);

    private readonly IChatProvider _inner;
    private readonly int _maxAttempts;
    private readonly TimeSpan _baseDelay;
    private readonly TimeSpan _maxDelay;
    private readonly TimeSpan _maxRetryAfter;
    private readonly Func<TimeSpan, CancellationToken, ValueTask> _delay;
    private readonly Func<TimeSpan, TimeSpan> _jitter;
    private readonly TimeProvider _time;
    private readonly ProviderRateLimiter? _rateLimiter;
    private readonly ITokenCounter _tokens;

    /// <param name="inner">The endpoint; its <see cref="IChatProvider.Id"/> names the budget the request is charged to.</param>
    /// <param name="delayAsync">How to wait; defaults to a delay on <paramref name="timeProvider"/>'s clock.</param>
    /// <param name="timeProvider">
    /// The clock for backoff, for the quota reading of a failure, and for budget waits. A caller may move it instead of waiting.
    /// </param>
    /// <param name="rateLimiter">
    /// The budget to acquire from before each attempt. Null (the default) charges nothing: a caller that has no shell
    /// wiring — an embedded host — must not inherit a process-wide ceiling it never asked for.
    /// </param>
    public RetryingChatProvider(
        IChatProvider inner,
        int maxAttempts = 3,
        TimeSpan? baseDelay = null,
        TimeSpan? maxDelay = null,
        Func<TimeSpan, CancellationToken, ValueTask>? delayAsync = null,
        Func<TimeSpan, TimeSpan>? jitter = null,
        TimeSpan? maxRetryAfter = null,
        TimeProvider? timeProvider = null,
        ProviderRateLimiter? rateLimiter = null,
        ITokenCounter? tokenCounter = null)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxAttempts, 1);

        _inner = inner;
        _maxAttempts = maxAttempts;
        _baseDelay = baseDelay ?? DefaultBaseDelay;
        _maxDelay = maxDelay ?? DefaultMaxDelay;
        _maxRetryAfter = maxRetryAfter ?? DefaultMaxRetryAfter;
        _time = timeProvider ?? TimeProvider.System;
        _rateLimiter = rateLimiter;
        _tokens = tokenCounter ?? new ApproximateTokenCounter();
        _delay = delayAsync ?? DefaultDelayAsync;
        _jitter = jitter ?? FullJitter;
    }

    public string Id => _inner.Id;

    public int MaxAttempts => _maxAttempts;

    public async IAsyncEnumerable<ChatStreamEvent> StreamAsync(
        ChatRequest request,
        [EnumeratorCancellation] CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        // The estimate is charged before each attempt and reconciled with the usage the answer reports, so a retry pays for
        // itself instead of hiding behind a reservation made once. With no limiter, or with the budget switched off, nobody
        // reads the number, so the walk over the messages (an enumerator per message) is not paid at all.
        var reserved = _rateLimiter is { Policy.Enabled: true } ? EstimateTokens(request) : 0;
        var charged = 0;
        var slept = TimeSpan.Zero;

        for (var attempt = 1; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            // Waits until the provider's budget has room for one request of `reserved` tokens; inlined rather than a
            // helper because the wait must be yielded from this iterator as a <see cref="BudgetWait"/> event *before* the
            // sleep it describes, and a helper cannot yield. The semantics are the old ones: Authorized (charged),
            // Disabled (nothing charged), ExceedsCapacity and NeverRefills all mean "send it" — the first two are
            // permission, and the last two are shapes in which waiting helps nobody; a wait longer than
            // <see cref="_maxRetryAfter"/> — or waits that would add up to more than that — is not slept through but
            // reported as a retryable 429 carrying the delay, which is the shape the quota flow already understands (a
            // provider asking for tens of minutes is a spent window, not jitter); bounding the total also makes this loop
            // terminate whatever the clock does: a bucket that keeps reporting a short wait against a clock that never
            // advances would otherwise spin here forever. `charged` ends up holding what the budget actually took for this
            // attempt, which is what <see cref="Settle"/> reconciles; zero when nothing was.
            charged = 0;
            if (_rateLimiter is not null)
            {
                var waited = TimeSpan.Zero;
                var reported = false;
                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    var decision = _rateLimiter.Acquire(_inner.Id, reserved, out var retryAfter, out charged);
                    if (decision != RateLimitDecision.Wait)
                    {
                        break;
                    }

                    var need = retryAfter > TimeSpan.Zero ? retryAfter : TimeSpan.FromMilliseconds(1);
                    if (need > _maxRetryAfter || waited + need > _maxRetryAfter)
                    {
                        // What is still missing, not what has already been slept: the delay a caller reads is "how long
                        // until a request can go", and adding the time this loop already spent would push the reset past
                        // the moment the budget returns.
                        throw BudgetExhausted(need);
                    }

                    // At most one report per attempt, and only for a wait that clears the noise threshold: most waits
                    // are ordinary bookkeeping. The record is allocated here and nowhere else, so a path with no wait
                    // never pays for it. A retry is another attempt and may report its own wait.
                    if (!reported && need >= MinReportedBudgetWait)
                    {
                        reported = true;
                        yield return new BudgetWait(need);
                    }

                    await _delay(need, ct).ConfigureAwait(false);
                    waited += need;
                }
            }

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
                    if (enumerator.Current is MessageCompleted completed)
                    {
                        Settle(charged, completed.Usage, completed.RateLimit);
                    }

                    yield return enumerator.Current;
                }
            }
            finally
            {
                await enumerator.DisposeAsync().ConfigureAwait(false);
            }

            // A refused attempt still spent the provider's tokens, and its headers are the only place the real budget shows.
            // They are read whatever happens next - the last attempt, a failure that is not retried, a spent quota - because
            // the hold a rejected window implies matters most exactly when this request gives up: the next one (a new turn,
            // an automation resume) must not walk into the same refusal.
            if (failure is ProviderException { RateLimit: { } reading })
            {
                _rateLimiter?.Observe(_inner.Id, reading, _time.GetUtcNow());
            }

            var attempts = Attempts(failure);
            if (failure is null || yielded || attempt >= attempts || !IsRetryable(failure, ct) || IsQuota(failure))
            {
                throw Rethrow(failure ?? new ProviderException($"{_inner.Id}: stream ended unexpectedly.", retryable: true));
            }

            if (!TryDelay(attempt, failure, slept, out var delay))
            {
                // The provider asks for longer than this request may sleep (or the ladder itself would run past the budget):
                // the failure keeps its own Retry-After, so whoever reads it — the footnote, the quota classification, the
                // automation that resumes the session — sees the server's number rather than a wait that never happened.
                throw Rethrow(failure);
            }

            yield return new RetryWait(delay, attempt + 1, attempts, ReasonOf(failure));
            await _delay(delay, ct).ConfigureAwait(false);
            slept += delay;
        }
    }

    /// <summary>A spent quota (not a short rate limit) never comes back within a retry; the automation flow waits for it instead.</summary>
    private bool IsQuota(Exception failure) =>
        failure is ProviderException provider
        && ProviderQuota.Classify(provider, new ModelSettings(string.Empty), _time.GetUtcNow()) is not null;

    private static bool IsRetryable(Exception failure, CancellationToken ct) => failure switch
    {
        ProviderException provider => provider.Retryable,
        OperationCanceledException => false,
        HttpRequestException or IOException or TimeoutException => !ct.IsCancellationRequested,
        _ => false,
    };

    /// <summary>
    /// How many attempts this failure is worth: a 429 walks the whole overload ladder, everything else keeps the short one
    /// the caller configured.
    /// </summary>
    private int Attempts(Exception? failure) =>
        failure is not null && IsOverload(ReasonOf(failure)) ? OverloadDelays.Length + 1 : _maxAttempts;

    /// <summary>Server-side load shedding and per-minute limits share the long ladder; both are the same problem to us.</summary>
    private static bool IsOverload(RetryReason reason) => reason is RetryReason.Overloaded or RetryReason.RateLimited;

    /// <summary>
    /// Reads a failure into the four words the caller shows. The 429 split is the provider's own wording (`ProviderQuota`
    /// already owns that vocabulary): "rate limit / per minute" is the account's minute budget, anything else on a 429 is
    /// the endpoint shedding load.
    /// </summary>
    private static RetryReason ReasonOf(Exception failure) => failure switch
    {
        ProviderException { StatusCode: 429 } provider =>
            ProviderQuota.IsRateWording(ProviderErrors.MatchText(provider)) ? RetryReason.RateLimited : RetryReason.Overloaded,
        ProviderException { StatusCode: null } => RetryReason.Transport,
        ProviderException => RetryReason.Server,
        _ => RetryReason.Transport,
    };

    /// <summary>
    /// Reports what the answer actually cost and what its headers said about the account's budget.
    /// </summary>
    private void Settle(int chargedTokens, TokenUsage usage, ProviderRateLimit? reading)
    {
        if (_rateLimiter is null)
        {
            return;
        }

        // The settle is input + output because that is what a token-per-minute budget meters: the OpenAI family counts
        // both sides of a request, so the budget must settle against the same sum. The estimate charged before the
        // request counts input only (the answer's size is unknown until it exists); this is where both sides are
        // corrected. An answer without an output count (OutputTokens 0) settles on the input alone, exactly as before.
        //
        // InputTokens stays the complete metered input: every adapter reports cache reads and writes inside it (Anthropic
        // sums the three, the OpenAI shapes pass prompt_tokens/input_tokens through), which is also why SessionUsageMath
        // subtracts the cached parts again to get the *billed* remainder. Adding them here would charge a cached
        // conversation twice - the one workload an agent runs all day. A provider that reports no usage at all leaves the
        // estimate standing instead of refunding it: a missing number is not a free request.
        var actual = usage.InputTokens + usage.OutputTokens;
        if (actual > 0)
        {
            _rateLimiter.Settle(_inner.Id, chargedTokens, actual);
        }

        // The answer is the proof that the request was accepted, so a "rejected" window in the same headers (an overage
        // allowance carrying the account past its spent window) must not hold the next one.
        _rateLimiter.Observe(_inner.Id, reading, _time.GetUtcNow(), answered: true);
    }

    /// <summary>
    /// The request's prompt size in tokens, through the same counter the rest of the agent uses: the system prompt, the
    /// messages and the tool definitions are all sent (and billed) on every attempt.
    /// </summary>
    private int EstimateTokens(ChatRequest request)
    {
        long total = _tokens.Count(request.SystemPrompt) + _tokens.Count(request.Messages);
        foreach (var tool in request.Tools)
        {
            total += _tokens.Count(tool.Name) + _tokens.Count(tool.Description) + _tokens.Count(tool.InputSchemaJson);
        }

        return (int)Math.Min(int.MaxValue, total);
    }

    /// <summary>"Nothing may be sent to this provider for a while", in the shape the retry and quota layers already read.</summary>
    private ProviderException BudgetExhausted(TimeSpan retryAfter)
    {
        var failure = new ProviderException(
            $"{_inner.Id}: the local request budget has no room for another attempt for {Format(retryAfter)}.",
            retryable: true,
            statusCode: 429);
        failure.Data[ProviderErrors.RetryAfterKey] = retryAfter;
        return failure;
    }

    private static string Format(TimeSpan span) => span.TotalHours >= 1
        ? $"{span.TotalHours:0.#} h"
        : span.TotalMinutes >= 1
            ? $"{span.TotalMinutes:0.#} min"
            : $"{span.TotalSeconds:0.#} s";

    /// <summary>
    /// How long to wait before the next attempt, or false when that wait is longer than this request may sleep.
    /// <para>
    /// The server's own <c>Retry-After</c> wins over any ladder — it is the only number that is not a guess — but it is also
    /// subject to the budget: asking for two minutes is not jitter, so the request gives up instead of going quiet for that
    /// long. Without one, a 429 walks <see cref="OverloadDelays"/> and everything else doubles from the base delay, both
    /// jittered so two sessions refused in the same instant do not come back in the same instant.
    /// </para>
    /// </summary>
    private bool TryDelay(int attempt, Exception failure, TimeSpan slept, out TimeSpan delay)
    {
        if (failure is ProviderException provider
            && ProviderErrors.TryGetRetryAfter(provider, out var retryAfter)
            && retryAfter > TimeSpan.Zero)
        {
            delay = retryAfter > _maxRetryAfter ? _maxRetryAfter : retryAfter;
            return delay <= MaxRetryBudget - slept;
        }

        var reason = ReasonOf(failure);
        var ceiling = IsOverload(reason)
            ? OverloadDelays[Math.Min(attempt - 1, OverloadDelays.Length - 1)]
            : Exponential(attempt);
        delay = _jitter(ceiling);
        return delay <= MaxRetryBudget - slept;
    }

    private TimeSpan Exponential(int attempt)
    {
        var baseMilliseconds = _baseDelay.TotalMilliseconds;
        var maxMilliseconds = _maxDelay.TotalMilliseconds;
        var milliseconds = baseMilliseconds * Math.Pow(2, attempt - 1);
        if (double.IsNaN(milliseconds) || milliseconds > maxMilliseconds)
        {
            milliseconds = maxMilliseconds;
        }

        return milliseconds < 0 ? TimeSpan.Zero : TimeSpan.FromMilliseconds(milliseconds);
    }

    /// <summary>
    /// The default jitter: <c>[0, ceiling]</c> uniformly ("full jitter"). Bounded-by-half spread would keep every client of a
    /// refused window arriving inside a window that is still half the ceiling long, which is exactly the pile-up a jittered
    /// retry exists to avoid.
    /// </summary>
    private static TimeSpan FullJitter(TimeSpan ceiling) =>
        ceiling <= TimeSpan.Zero ? TimeSpan.Zero : ceiling * Random.Shared.NextDouble();

    /// <summary>The default wait: on the injected clock, so a caller that moved the clock is not left sleeping in real time.</summary>
    private ValueTask DefaultDelayAsync(TimeSpan delay, CancellationToken ct) =>
        new(Task.Delay(delay, _time, ct));

    private static Exception Rethrow(Exception exception)
    {
        ExceptionDispatchInfo.Capture(exception).Throw();
        return exception;
    }
}
