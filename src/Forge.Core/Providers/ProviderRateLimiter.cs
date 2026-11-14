using System.Collections.Concurrent;
using Forge.Abstractions;

namespace Forge.Core.Providers;

/// <summary>What the local budget says about one request, so the caller knows whether to send, wait, or go anyway.</summary>
public enum RateLimitDecision
{
    /// <summary>There is room; the request may leave and the budget has been charged for it.</summary>
    Authorized,

    /// <summary>There is no room yet, and the caller was told how long is missing (or that it is unbounded).</summary>
    Wait,

    /// <summary>One request is larger than the whole budget, so no wait would make room. The request is sent anyway.</summary>
    ExceedsCapacity,

    /// <summary>The bucket is empty and adds nothing back (a window with no reset time). Waiting would never end.</summary>
    NeverRefills,

    /// <summary>The local budget is switched off; nothing was charged.</summary>
    Disabled,
}

/// <summary>
/// The per-provider request and token budget every model request is acquired from before it leaves, plus the spacing of
/// automated resumes. One instance serves the whole process (<see cref="Shared"/> is the one the shell publishes the user's
/// settings into); a test may build its own with its own <see cref="TimeProvider"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a budget at all.</b> Providers throttle per account per minute, and Forge can spend a minute's worth of requests in
/// seconds: a fan-out of sub-agents, an automation retrying a refused window, a session resumed while another is still
/// running. Being refused costs a round trip and, on some providers, a cooldown that starts over. Waiting a few hundred
/// milliseconds locally is cheaper than being told to wait by the server, and it is the difference between "slow" and
/// "stopped".
/// </para>
/// <para>
/// <b>Calibrated by the wire, not only by settings.</b> A provider that reports its own limits (<c>x-ratelimit-*</c>,
/// Anthropic's <c>anthropic-ratelimit-requests-*</c>/<c>-tokens-*</c>, already parsed into <see cref="ProviderThrottle"/>) is
/// telling us the truth about the account, which no setting can know: tier, plan, and what other clients of the same key have
/// already spent. <see cref="Observe"/> adopts those numbers — ceiling, remaining and reset — so the local budget converges on
/// the real one after the first response.
/// </para>
/// <para>
/// <b>One budget per provider, not one for the process.</b> Two providers are two accounts and two throttle windows; sharing a
/// budget between them would make one slow provider hold up an unrelated one, and would hide a per-account rejection behind a
/// global number. The numbers come from one policy (<see cref="Publish"/>) because nobody wants to fill in a per-provider
/// matrix; the split is in the buckets, which is where it matters.
/// </para>
/// <para>
/// <b>What a refusal is not.</b> A spent <em>quota</em> (hours, days) is not this layer's business: it is classified by
/// <see cref="ProviderQuota"/> and waited on by the automation flow. This layer only knows about minute-scale budgets, and when
/// it does hold a request it reports the wait — it never sleeps behind the caller's back.
/// </para>
/// </remarks>
public sealed class ProviderRateLimiter
{
    /// <summary>The process-wide limiter. Its policy is whatever the shell last published.</summary>
    public static ProviderRateLimiter Shared { get; } = new();

    private readonly ConcurrentDictionary<string, ProviderBuckets> buckets = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, AutomationSlot> automationSlots = new(StringComparer.Ordinal);
    private readonly TimeProvider time;
    private ProviderRateLimitPolicy policy;

    /// <param name="time">The clock; a test supplies one it can move instead of waiting in real time.</param>
    public ProviderRateLimiter(TimeProvider? time = null)
    {
        this.time = time ?? TimeProvider.System;
        policy = ProviderRateLimitPolicy.Default;
    }

    /// <summary>The policy in force. Reading it is one volatile load; there is no lock on the request path.</summary>
    public ProviderRateLimitPolicy Policy => Volatile.Read(ref policy);

    /// <summary>
    /// Publishes a new policy. Buckets that already exist are reconfigured in place — a lowered ceiling takes effect on the
    /// next request, and credit already earned is kept up to the new ceiling — so a settings change reaches running sessions
    /// without rebuilding any provider.
    /// </summary>
    public void Publish(ProviderRateLimitPolicy next)
    {
        ArgumentNullException.ThrowIfNull(next);
        var normalized = next.Normalized();
        Volatile.Write(ref policy, normalized);
        foreach (var (providerId, entry) in buckets)
        {
            // Each bucket follows the ceilings of its own provider: an override that appears, changes or is removed has to reach
            // the bucket that already exists, which is the whole point of reconfiguring in place instead of rebuilding providers.
            var (requests, tokens) = normalized.LimitsFor(providerId);
            entry.Requests.Configure(requests, requests / 60.0);
            entry.Tokens.Configure(tokens, tokens / 60.0);
        }
    }

    /// <summary>
    /// Asks permission for one request of <paramref name="requestTokens"/> estimated tokens. On
    /// <see cref="RateLimitDecision.Authorized"/> the budget has been charged; the caller reports the real token count back
    /// through <see cref="Settle"/> when the answer arrives. Any other decision charges nothing.
    /// </summary>
    public RateLimitDecision Acquire(string providerId, int requestTokens, out TimeSpan retryAfter)
    {
        ArgumentException.ThrowIfNullOrEmpty(providerId);
        retryAfter = TimeSpan.Zero;
        var current = Policy;
        if (!current.Enabled)
        {
            return RateLimitDecision.Disabled;
        }

        // The hold is read first and the clock only when one was ever set: an unheld request pays a field read, not a
        // platform counter call.
        var entry = Entry(providerId, current);
        var hold = entry.Hold.Remaining();
        if (hold > TimeSpan.Zero)
        {
            retryAfter = hold;
            return RateLimitDecision.Wait;
        }

        // The ceilings this provider actually has: its own override where it has one, else the globals. Skipping a dimension is
        // decided on those numbers, so an override of 0 really means "no limit here" rather than "whatever the global says".
        var (requestsPerMinute, tokensPerMinute) = current.LimitsFor(providerId);
        if (requestsPerMinute > 0)
        {
            var request = entry.Requests.Take(1, out retryAfter);
            if (request != RateLimitDecision.Authorized)
            {
                return request;
            }
        }

        if (tokensPerMinute <= 0 || requestTokens <= 0)
        {
            return RateLimitDecision.Authorized;
        }

        var tokens = entry.Tokens.Take(requestTokens, out retryAfter);
        if (tokens is RateLimitDecision.Authorized or RateLimitDecision.ExceedsCapacity)
        {
            return tokens;
        }

        // The request slot is handed back: holding it while waiting for tokens would make the wait longer and would charge
        // the caller for a request that never left.
        entry.Requests.Adjust(1);
        return tokens;
    }

    /// <summary>
    /// Reconciles the charge with what the answer actually cost: <paramref name="actualTokens"/> replaces the estimate, so a
    /// long prompt that was undercounted is paid for and an overcount is handed back. A no-op when they agree.
    /// </summary>
    public void Settle(string providerId, int reservedTokens, int actualTokens)
    {
        ArgumentException.ThrowIfNullOrEmpty(providerId);
        if (reservedTokens == actualTokens || !buckets.TryGetValue(providerId, out var entry))
        {
            return;
        }

        var delta = (double)reservedTokens - actualTokens;
        if (delta != 0 && Policy.LimitsFor(providerId).TokensPerMinute > 0)
        {
            entry.Tokens.Adjust(delta);
        }
    }

    /// <summary>
    /// Adopts what a response said about the account's minute-scale limits: the ceiling, what is left and when the window
    /// resets. Called with whatever a response carried, including the failure path — a 429's headers are the only place the
    /// real constraint is visible.
    /// </summary>
    /// <param name="providerId">Which budget to calibrate.</param>
    /// <param name="reading">The parsing of the response headers; null (no such headers) leaves the budget alone.</param>
    /// <param name="now">The instant the response was read, used to turn a reset time into a hold.</param>
    public void Observe(string providerId, ProviderRateLimit? reading, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrEmpty(providerId);
        if (reading is null)
        {
            return;
        }

        var entry = Entry(providerId, Policy);
        var hold = default(DateTimeOffset?);
        var sawRemaining = false;
        if (reading.Throttle is { } throttle)
        {
            Calibrate(entry.Requests, throttle.RequestLimit, throttle.RequestRemaining, throttle.RequestResetAt, now, ref hold, ref sawRemaining);
            Calibrate(entry.Tokens, throttle.TokenLimit, throttle.TokenRemaining, throttle.TokenResetAt, now, ref hold, ref sawRemaining);
        }

        // A rejected spending window is not this layer's to wait out, but its reset is a floor: nothing this account sends
        // can succeed before it, so the hold keeps the retry loop from burning its attempts on refusals.
        if (reading.IsRejected && reading.ResetAt is { } reset)
        {
            hold = hold is { } current && current > reset ? current : reset;
        }

        // Decided once for the whole reading: a dimension with room must not lift the hold a dimension without room just set,
        // which is what an unconditional release inside Calibrate would do (requests are read before tokens).
        if (hold is { } until)
        {
            entry.Hold.Set(until, now);
        }
        else if (sawRemaining)
        {
            entry.Hold.Release();
        }
    }

    /// <summary>
    /// Reserves the provider's next slot for an automated resume and returns how long the caller must wait for it. This is the
    /// spacing that used to live in the automation flow's own lock-protected dictionary, folded into this component so one
    /// provider's automatic traffic is one budget instead of two unrelated ones.
    /// </summary>
    /// <remarks>
    /// A reservation, not a rate: each call moves the provider's next free moment one gap further, so three resumes reserved
    /// in the same instant are spaced 0, gap and 2×gap. A token bucket would answer "gap from now" every time and would let a
    /// burst of waiting runs all fire together the moment the first wait expired.
    /// </remarks>
    public TimeSpan ReserveAutomationSlot(string providerId)
    {
        ArgumentException.ThrowIfNullOrEmpty(providerId);
        var gap = Policy.AutomationGap;
        if (gap <= TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        return automationSlots.GetOrAdd(providerId, CreateSlot).Reserve(gap);
    }

    /// <summary>Where one provider's two budgets and its hold live.</summary>
    private ProviderBuckets Entry(string providerId, ProviderRateLimitPolicy current)
    {
        // Read before write: GetOrAdd's factory is a delegate that would capture the policy and allocate on every request,
        // which is exactly what "0 bytes per acquire" rules out. Creation happens once per provider instead.
        if (buckets.TryGetValue(providerId, out var existing))
        {
            return existing;
        }

        var (requestsPerMinute, tokensPerMinute) = current.LimitsFor(providerId);
        var created = new ProviderBuckets(this, requestsPerMinute, tokensPerMinute);
        return buckets.TryAdd(providerId, created) ? created : buckets[providerId];
    }

    private AutomationSlot CreateSlot(string _) => new(time);

    /// <summary>Adopts one reported limit, its remaining count and its reset; reports any hold the reading implies.</summary>
    private void Calibrate(
        RateLimitBucket bucket,
        int? limit,
        int? remaining,
        DateTimeOffset? resetAt,
        DateTimeOffset now,
        ref DateTimeOffset? hold,
        ref bool sawRemaining)
    {
        if (limit is null && remaining is null)
        {
            return;
        }

        // The reported limit is a per-minute budget: that is what the header family means on the providers that send it. A
        // remaining count without a limit still tells us what is left right now.
        if (limit is { } ceiling)
        {
            bucket.Configure(ceiling, ceiling / 60.0);
        }

        if (remaining is { } left)
        {
            bucket.SetRemaining(left);
            sawRemaining = true;
        }

        if (remaining is 0 && resetAt is { } reset && reset > now)
        {
            // Nothing is left and the provider named the moment it comes back: waiting until then is exact, while trickling
            // one request per refill would just be refused again.
            hold = hold is { } current && current > reset ? current : reset;
        }
    }

    /// <summary>
    /// A "nothing leaves before this moment" marker. Holds come from a spending window, which covers requests and tokens
    /// alike, so they live beside the two buckets rather than inside one of them.
    /// </summary>
    private sealed class Hold(TimeProvider time)
    {
        private long until;

        public TimeSpan Remaining()
        {
            var stamp = Volatile.Read(ref until);
            if (stamp <= 0)
            {
                return TimeSpan.Zero;
            }

            var now = time.GetTimestamp();
            if (stamp <= now)
            {
                // The hold is over: clear the stamp once instead of leaving a positive value behind that makes every later
                // request read the clock again ("no hold, no clock read" is a promise the fast path is measured on).
                Interlocked.CompareExchange(ref until, 0, stamp);
                return TimeSpan.Zero;
            }

            return TimeSpan.FromSeconds((stamp - now) / (double)time.TimestampFrequency);
        }

        public void Set(DateTimeOffset reset, DateTimeOffset now)
        {
            var delay = reset - now;
            if (delay <= TimeSpan.Zero)
            {
                return;
            }

            var stamp = time.GetTimestamp() + (long)(delay.TotalSeconds * time.TimestampFrequency);
            while (true)
            {
                var current = Volatile.Read(ref until);
                if (current >= stamp || Interlocked.CompareExchange(ref until, stamp, current) == current)
                {
                    return;
                }
            }
        }

        public void Release() => Volatile.Write(ref until, 0);
    }

    /// <summary>One provider's two budgets and its hold.</summary>
    private sealed class ProviderBuckets
    {
        public ProviderBuckets(ProviderRateLimiter owner, int requestsPerMinute, int tokensPerMinute)
        {
            Requests = new RateLimitBucket(owner.time, requestsPerMinute, requestsPerMinute / 60.0);
            Tokens = new RateLimitBucket(owner.time, tokensPerMinute, tokensPerMinute / 60.0);
            Hold = new Hold(owner.time);
        }

        public RateLimitBucket Requests { get; }

        public RateLimitBucket Tokens { get; }

        public Hold Hold { get; }
    }

    /// <summary>
    /// One provider's automation spacing: the next moment a resume may fire. Compare-exchange on a single timestamp keeps it
    /// lock-free; the read-then-swap retries only when two resumes are reserved at the same instant.
    /// </summary>
    private sealed class AutomationSlot(TimeProvider time)
    {
        private long next;

        public TimeSpan Reserve(TimeSpan gap)
        {
            var now = time.GetTimestamp();
            var step = (long)(gap.TotalSeconds * time.TimestampFrequency);
            while (true)
            {
                var current = Volatile.Read(ref next);
                var slot = current > now ? current : now;
                if (Interlocked.CompareExchange(ref next, slot + step, current) == current)
                {
                    return TimeSpan.FromSeconds((slot - now) / (double)time.TimestampFrequency);
                }
            }
        }
    }
}
