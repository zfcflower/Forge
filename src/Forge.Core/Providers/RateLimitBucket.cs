namespace Forge.Core.Providers;

/// <summary>
/// One budget: a token bucket whose state is advanced lazily from the clock and published through compare-exchange, so a
/// request never takes a lock and never allocates to ask whether it may leave. The numbers stay in whole 1/1024-token units
/// so a bucket can hold fractional credit without floating point in the published state.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why not an immutable state record per call.</b> The obvious shape for this — publish a new immutable state object and
/// swap it in with <see cref="Interlocked.CompareExchange{T}(ref T, T, T)"/> — allocates one object per authorized request.
/// That is acceptable for a throughput limiter and not acceptable here: the acceptance row of the measurements in
/// <c>tools/Forge.Bench</c> (<c>ratelimit.*</c>) is "0 bytes per acquire", and this runs on the thread that is about to
/// serialize a megabyte of prompt. So the state is two 64-bit words instead of one object: <see cref="tokens"/> (remaining
/// budget, fixed point) and <see cref="updatedAt"/> (when it was last refilled, in the injected
/// <see cref="TimeProvider"/>'s timestamp domain). Both are only ever written by compare-exchange, and the refill protocol
/// below keeps them consistent.
/// </para>
/// <para>
/// <b>The refill protocol.</b> A refiller first wins <see cref="updatedAt"/> (compare-exchange from the old stamp to now).
/// Only the winner may add credit, and it adds exactly the window it won; a loser re-reads the new stamp and folds its own
/// (later) window in, so no time is counted twice and none is dropped. The credit is clamped to the capacity, so a long idle
/// period simply fills the bucket.
/// </para>
/// <para>
/// <b>Why the clock is not read on every acquire.</b> A bucket with room does not need to know what time it is: refill credit
/// depends only on the elapsed window and the remaining budget, so skipping the refill below is the same arithmetic
/// postponed. Two consequences are worth stating because they look wrong at first sight: the fast path is a single atomic
/// operation and no clock read (a <see cref="TimeProvider.GetTimestamp"/> is a virtual call into the platform counter, tens of
/// nanoseconds), and a long stretch of consumption without a refill is harmless — the eventual refill credits the whole
/// window and clamps, exactly as it would have if it had run on every call.
/// </para>
/// <para>
/// <b>Why this and not <see cref="Lock"/></b> — measured, not assumed: see the <c>ratelimit.acquire.*</c> rows, whose baseline
/// is the same bucket behind one lock.
/// </para>
/// </remarks>
internal sealed class RateLimitBucket
{
    /// <summary>Fractional units per token: a refill smaller than one token is not lost.</summary>
    private const long Units = 1 << 10;

    private readonly TimeProvider time;

    /// <summary>Remaining budget in <see cref="Units"/> of a token; only ever written by an atomic operation.</summary>
    private long tokens;

    /// <summary>When <see cref="tokens"/> was last brought up to date, in <see cref="TimeProvider.GetTimestamp"/> units.</summary>
    private long updatedAt;

    /// <summary>Ceiling of <see cref="tokens"/>; replaced only by <see cref="Configure"/> and calibration.</summary>
    private long capacity;

    /// <summary>Credit added per second, in <see cref="Units"/> of a token; zero means the bucket never refills.</summary>
    private long perSecond;

    /// <param name="time">The clock; a test moves it instead of waiting in real time.</param>
    /// <param name="capacityTokens">The most the bucket can hold, in tokens.</param>
    /// <param name="perSecondTokens">How much credit one second adds, in tokens (may be fractional).</param>
    public RateLimitBucket(TimeProvider time, double capacityTokens, double perSecondTokens)
    {
        ArgumentNullException.ThrowIfNull(time);
        this.time = time;
        capacity = ToUnits(capacityTokens);
        perSecond = ToUnits(perSecondTokens);
        tokens = capacity;
        updatedAt = time.GetTimestamp();
    }

    /// <summary>The bucket's ceiling in whole tokens, for a caller that must know whether a single request can ever fit.</summary>
    public long CapacityTokens => Volatile.Read(ref capacity) / Units;

    /// <summary>Remaining budget in whole tokens, after folding in the refill that has accrued since the last touch.</summary>
    public long RemainingTokens
    {
        get
        {
            var now = time.GetTimestamp();
            var current = Volatile.Read(ref tokens);
            var at = Volatile.Read(ref updatedAt);
            var rate = Volatile.Read(ref perSecond);
            if (rate <= 0 || now <= at)
            {
                return current / Units;
            }

            var credit = (long)(time.GetElapsedTime(at, now).TotalSeconds * rate);
            return Math.Min(Volatile.Read(ref capacity), current + Math.Max(0, credit)) / Units;
        }
    }

    /// <summary>
    /// Replaces the ceiling and the refill rate (a settings change or a calibration from response headers). Credit already
    /// earned is kept but clamped to the new ceiling, so a lowered limit takes effect on the next request instead of after
    /// the old budget drains.
    /// </summary>
    public void Configure(double capacityTokens, double perSecondTokens)
    {
        Volatile.Write(ref capacity, ToUnits(capacityTokens));
        Volatile.Write(ref perSecond, ToUnits(perSecondTokens));
        Trim();
    }

    /// <summary>
    /// Sets the remaining budget to what the provider said it has (a response header's <c>remaining</c>). Not additive: the
    /// server's number is the truth, and being generous about it is exactly how a client walks into a rejection.
    /// </summary>
    /// <remarks>
    /// The refill stamp moves with it. The server's count already includes whatever its own window added up to the moment it
    /// answered, so a later low-water refill must not re-credit that window — and it would: the lazy refill folds in everything
    /// since the last stamp, which after a quiet period is the whole window. The stamp is advanced before the count is written,
    /// so a refill racing this call can only see the new stamp (a lost credit here is the point; the server's number replaces
    /// it) and never the new count under the old window.
    /// </remarks>
    public void SetRemaining(double remainingTokens)
    {
        AdvanceStamp(time.GetTimestamp());
        SetTokens(ToUnits(remainingTokens));
    }

    /// <summary>Adds credit, or with a negative delta takes it back (clamped to the ceiling and to zero).</summary>
    public void Adjust(double deltaTokens)
    {
        var delta = ToDelta(deltaTokens);
        while (delta != 0)
        {
            var current = Volatile.Read(ref tokens);
            var next = Clamp(current + delta);
            if (next == current || Interlocked.CompareExchange(ref tokens, next, current) == current)
            {
                return;
            }
        }
    }

    /// <summary>
    /// Takes <paramref name="costTokens"/> out of the bucket if it is there. A refused request consumes nothing, so a caller
    /// that waits and retries is charged once.
    /// </summary>
    /// <param name="costTokens">What the request costs; zero only checks that the bucket is open.</param>
    /// <param name="retryAfter">
    /// How long the bucket is missing; zero when it is not, <see cref="TimeSpan.MaxValue"/> when it adds nothing back.
    /// </param>
    public RateLimitDecision Take(long costTokens, out TimeSpan retryAfter)
    {
        var cost = costTokens * Units;
        var ceiling = Volatile.Read(ref capacity);
        if (cost > ceiling)
        {
            // A single request bigger than the whole budget: no amount of waiting makes room for it, and refusing it would
            // break long contexts outright. The caller sends it and the provider decides; the bucket is spent for it.
            Drain();
            retryAfter = TimeSpan.Zero;
            return RateLimitDecision.ExceedsCapacity;
        }

        // Refill only when the answer could change: above the low-water mark the bucket has room for this request either
        // way, so the clock read is postponed instead of paid on every call.
        if (Volatile.Read(ref tokens) - cost < ceiling / 8)
        {
            Refill(time.GetTimestamp());
        }

        // The balance is never negative, not even for an instant: the cost is subtracted by one compare-exchange that only
        // succeeds when the whole cost is there. (Draining first and handing the cost back on refusal let another request
        // see a negative balance and wait for credit that was only borrowed, and let Adjust's clamp turn the refund into
        // credit that never existed.) The uncontended success path is still one read and one atomic.
        while (true)
        {
            var current = Volatile.Read(ref tokens);
            if (current < cost)
            {
                var rate = Volatile.Read(ref perSecond);
                if (rate <= 0)
                {
                    retryAfter = TimeSpan.MaxValue;
                    return RateLimitDecision.NeverRefills;
                }

                // Refill is linear, so the wait for the deficit is exact until the bucket starts being shared again.
                var seconds = (cost - current) / (double)rate;
                retryAfter = seconds >= TimeSpan.MaxValue.TotalSeconds
                    ? TimeSpan.MaxValue
                    : TimeSpan.FromSeconds(seconds);
                return RateLimitDecision.Wait;
            }

            if (cost == 0 || Interlocked.CompareExchange(ref tokens, current - cost, current) == current)
            {
                retryAfter = TimeSpan.Zero;
                return RateLimitDecision.Authorized;
            }
        }
    }

    /// <summary>Empties the bucket (what an over-capacity request spends); the refill stamp is untouched.</summary>
    private void Drain()
    {
        while (true)
        {
            var current = Volatile.Read(ref tokens);
            if (current <= 0 || Interlocked.CompareExchange(ref tokens, 0, current) == current)
            {
                return;
            }
        }
    }

    /// <summary>Brings <see cref="tokens"/> up to date for a window that ends at <paramref name="now"/>.</summary>
    private void Refill(long now)
    {
        while (true)
        {
            var at = Volatile.Read(ref updatedAt);
            if (now <= at)
            {
                return;
            }

            var rate = Volatile.Read(ref perSecond);
            if (rate <= 0)
            {
                return;
            }

            var credit = (long)(time.GetElapsedTime(at, now).TotalSeconds * rate);
            if (credit <= 0)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref updatedAt, now, at) != at)
            {
                // Another thread won this window and is adding its credit; try again against the stamp it published.
                continue;
            }

            var ceiling = Volatile.Read(ref capacity);
            while (true)
            {
                var current = Volatile.Read(ref tokens);
                var next = Math.Min(ceiling, current + credit);
                if (next == current || Interlocked.CompareExchange(ref tokens, next, current) == current)
                {
                    return;
                }
            }
        }
    }

    /// <summary>
    /// Brings the remaining budget down to the ceiling (after a calibration or a settings change).
    /// </summary>
    private void Trim()
    {
        while (true)
        {
            var current = Volatile.Read(ref tokens);
            var ceiling = Volatile.Read(ref capacity);
            if (current <= ceiling)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref tokens, ceiling, current) == current)
            {
                return;
            }
        }
    }

    /// <summary>Moves the refill stamp forward to <paramref name="now"/> if it is behind; never backwards.</summary>
    private void AdvanceStamp(long now)
    {
        while (true)
        {
            var at = Volatile.Read(ref updatedAt);
            if (at >= now || Interlocked.CompareExchange(ref updatedAt, now, at) == at)
            {
                return;
            }
        }
    }

    private void SetTokens(long value)
    {
        var clamped = Clamp(value);
        while (true)
        {
            var current = Volatile.Read(ref tokens);
            if (Interlocked.CompareExchange(ref tokens, clamped, current) == current)
            {
                return;
            }
        }
    }

    private long Clamp(long value)
    {
        var ceiling = Volatile.Read(ref capacity);
        return value < 0 ? 0 : value > ceiling ? ceiling : value;
    }

    private static long ToUnits(double tokens) =>
        tokens <= 0 ? 0 : (long)Math.Min(long.MaxValue / Units, Math.Ceiling(tokens * Units));

    /// <summary>
    /// A signed adjustment in <see cref="Units"/>; unlike a capacity or a rate it may be negative, which is how a settled charge
    /// is taken back when the answer cost more than the estimate. Rounding instead of ceiling keeps a one-unit refund from
    /// growing into a token.
    /// </summary>
    private static long ToDelta(double tokens)
    {
        if (!double.IsFinite(tokens))
        {
            return 0;
        }

        var scaled = Math.Round(tokens * Units);
        return scaled >= long.MaxValue ? long.MaxValue : scaled <= long.MinValue ? long.MinValue : (long)scaled;
    }
}
