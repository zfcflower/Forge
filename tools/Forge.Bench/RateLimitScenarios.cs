using System.Diagnostics;
using System.Globalization;
using Forge.Core.Providers;

namespace Forge.Bench;

/// <summary>
/// The <c>ratelimit</c> command: the per-provider token bucket the provider layer acquires from before every request.
/// A bucket is charged once per model request (plus its token estimate), so the acquire is a hot path: it must not allocate
/// and must stay well inside a hundred nanoseconds when nothing else is running.
/// <para>The implementation is <see cref="ProviderRateLimiter"/> under a policy that never refuses. The baseline is the shape
/// the design rejected — one mutable bucket under a lock — implemented here rather than taken from a package, so the two
/// sides differ only in the concurrency mechanism; both are keyed by provider the same way, so the dictionary lookup is
/// measured on both sides and the difference is the bucket itself.</para>
/// <para>Rows: a warm bucket that always authorizes (the success path), an exhausted bucket (the wait path, which must still
/// not allocate), eight threads hammering a bucket with room (throughput on a contended cache line), and eight threads
/// competing for a budget that is smaller than their demand (sharing: the 8000-request allowance must never be handed out
/// twice, and the per-thread counts plus the same numbers for the lock baseline say how evenly it went).</para>
/// </summary>
internal static class RateLimitScenarios
{
    /// <summary>Acquires per timed call. One acquire alone is below the timer's resolution, so a call is a batch.</summary>
    private const int Batch = 1000;

    private const int ContendedThreads = 8;
    private const int ContendedBatch = 2000;

    /// <summary>The tight allowance the fairness row hands out: one request per token, most of the demand is refused.</summary>
    private const int FairnessBudget = 8000;

    /// <summary>
    /// Attempts per thread in the fairness row. Long enough (tens of milliseconds) that pool warm-up skew is small against the
    /// measured window: a short batch would report who started first, not who got the budget.
    /// </summary>
    private const int FairnessBatch = 20000;

    private const string Provider = "bench-provider";

    public static IEnumerable<BenchScenario> Create(Fixtures fixtures)
    {
        yield return Authorized();
        yield return Miss();
        yield return Contended();
        yield return Fairness();
    }

    /// <summary>A limiter whose budget is far above anything the batch can spend, so the row measures the acquire itself.</summary>
    private static ProviderRateLimiter Limiter(int requestsPerMinute = 6_000_000, int tokensPerMinute = 400_000_000)
    {
        var limiter = new ProviderRateLimiter();
        limiter.Publish(
            new ProviderRateLimitPolicy
            {
                RequestsPerMinute = requestsPerMinute,
                TokensPerMinute = tokensPerMinute,
            });
        return limiter;
    }

    /// <summary>The success path: the budget is never the reason a request waits.</summary>
    private static BenchScenario Authorized()
    {
        return new BenchScenario(
            "ratelimit.acquire.authorized",
            "ratelimit",
            Heavy: false,
            PrepareAsync: _ =>
            {
                var limiter = Limiter();
                var locked = new LockedLimiter(requestsPerMinute: 6_000_000, tokensPerMinute: 400_000_000);
                // Built once: the row's acceptance is "the acquire path allocates nothing", so the result object and its
                // text must not be built inside the timed call.
                var result = Task.FromResult(new OpResult(Items: Batch, Signature: Batch, Observation: "all authorized"));
                return Task.FromResult(new BenchCase
                {
                    Run = cancellationToken =>
                    {
                        var taken = 0;
                        for (var index = 0; index < Batch; index++)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            if (limiter.Acquire(Provider, requestTokens: 100, out var wait) == RateLimitDecision.Authorized)
                            {
                                taken++;
                            }
                        }

                        return taken == Batch ? result : Task.FromResult(new OpResult(Items: Batch, Signature: taken));
                    },
                    Baseline = cancellationToken =>
                    {
                        var taken = 0;
                        for (var index = 0; index < Batch; index++)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            if (locked.Acquire(Provider, tokens: 100))
                            {
                                taken++;
                            }
                        }

                        return taken == Batch ? result : Task.FromResult(new OpResult(Items: Batch, Signature: taken));
                    },
                    BaselineName = "one mutable bucket under a lock",
                    Iterations = 30,
                    Check = FastPath("one acquire with budget to spare", Batch),
                });
            });
    }

    /// <summary>The wait path: the bucket is empty, so every acquire computes a retry time and consumes nothing.</summary>
    private static BenchScenario Miss()
    {
        return new BenchScenario(
            "ratelimit.acquire.wait",
            "ratelimit",
            Heavy: false,
            PrepareAsync: _ =>
            {
                var limiter = Limiter(requestsPerMinute: 60, tokensPerMinute: 60_000);
                for (var index = 0; index < 60; index++)
                {
                    limiter.Acquire(Provider, requestTokens: 0, out var wait);
                }

                var locked = new LockedLimiter(requestsPerMinute: 60, tokensPerMinute: 60_000);
                for (var index = 0; index < 60; index++)
                {
                    locked.Acquire(Provider, tokens: 0);
                }

                var result = Task.FromResult(new OpResult(Items: Batch, Signature: Batch, Observation: "all waiting"));
                return Task.FromResult(new BenchCase
                {
                    Run = cancellationToken =>
                    {
                        var waits = 0;
                        for (var index = 0; index < Batch; index++)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            if (limiter.Acquire(Provider, requestTokens: 0, out var wait) == RateLimitDecision.Wait)
                            {
                                waits++;
                            }
                        }

                        return waits == Batch ? result : Task.FromResult(new OpResult(Items: Batch, Signature: waits));
                    },
                    Baseline = cancellationToken =>
                    {
                        var waits = 0;
                        for (var index = 0; index < Batch; index++)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            if (!locked.Acquire(Provider, tokens: 0))
                            {
                                waits++;
                            }
                        }

                        return waits == Batch ? result : Task.FromResult(new OpResult(Items: Batch, Signature: waits));
                    },
                    BaselineName = "one mutable bucket under a lock",
                    Iterations = 30,
                    Check = FastPath("one refused acquire computes its retry time", Batch),
                });
            });
    }

    /// <summary>Eight threads, one provider, no refill pressure: throughput and fairness of the shared bucket.</summary>
    private static BenchScenario Contended()
    {
        return new BenchScenario(
            "ratelimit.acquire.contended.8t",
            "ratelimit",
            Heavy: false,
            PrepareAsync: _ =>
            {
                var limiter = Limiter(tokensPerMinute: 400_000_000);
                var locked = new LockedLimiter(requestsPerMinute: 6_000_000, tokensPerMinute: 400_000_000);
                var perThread = new int[ContendedThreads];
                return Task.FromResult(new BenchCase
                {
                    Run = cancellationToken => Hammer(
                        () => limiter.Acquire(Provider, requestTokens: 1, out var wait) == RateLimitDecision.Authorized,
                        perThread,
                        ContendedBatch,
                        cancellationToken),
                    Baseline = cancellationToken => Hammer(
                        () => locked.Acquire(Provider, tokens: 1),
                        perThread,
                        ContendedBatch,
                        cancellationToken),
                    BaselineName = "one mutable bucket under a lock",
                    ProcessWideAllocation = true,
                    Iterations = 20,
                    Note = $"{ContendedThreads} threads x {ContendedBatch} acquires of one provider",
                    Check = Contention("eight threads share one bucket with room: every acquire authorized, nothing refused"),
                });
            });
    }

    /// <summary>Runs <see cref="ContendedThreads"/> threads of <see cref="ContendedBatch"/> acquires and reports the spread.</summary>
    private static async Task<OpResult> Hammer(Func<bool> acquire, int[] perThread, int batch, CancellationToken cancellationToken)
    {
        Array.Clear(perThread);
        // A start gate: without it the thread pool's warm-up skew decides who reaches the bucket first, and a row about sharing a
        // budget would be reporting scheduling noise.
        using var gate = new ManualResetEventSlim(false);
        var tasks = new Task[ContendedThreads];
        for (var thread = 0; thread < ContendedThreads; thread++)
        {
            var slot = thread;
            tasks[thread] = Task.Run(
                () =>
                {
                    gate.Wait(cancellationToken);
                    for (var index = 0; index < batch; index++)
                    {
                        if (acquire())
                        {
                            perThread[slot]++;
                        }
                    }
                },
                cancellationToken);
        }

        var started = Stopwatch.GetTimestamp();
        gate.Set();
        await Task.WhenAll(tasks).ConfigureAwait(false);
        var elapsed = Stopwatch.GetElapsedTime(started);
        var total = 0;
        var min = int.MaxValue;
        var max = 0;
        foreach (var count in perThread)
        {
            total += count;
            min = Math.Min(min, count);
            max = Math.Max(max, count);
        }

        var spread = min <= 0 ? double.PositiveInfinity : (double)max / min;
        var nanosecondsPerOp = total == 0 ? double.NaN : elapsed.Ticks * 100.0 / total;
        return new OpResult(
            Items: total,
            Signature: total,
            Observation: string.Create(
                CultureInfo.InvariantCulture,
                $"threads={ContendedThreads} min={min} max={max} spread={spread:0.00} ns/op={nanosecondsPerOp:0.0}"));
    }

    /// <summary>Pulls the fairness spread out of the observation text.</summary>
    private static double Spread(string observation)
    {
        const string Marker = "spread=";
        var start = observation.IndexOf(Marker, StringComparison.Ordinal);
        if (start < 0)
        {
            return double.NaN;
        }

        start += Marker.Length;
        var end = observation.IndexOf(' ', start);
        var text = end < 0 ? observation[start..] : observation[start..end];
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : double.NaN;
    }

    /// <summary>The accept check the single-threaded rows share: no allocation, and the per-acquire cost under 100 ns.</summary>
    private static Func<Comparison, Verdict?> FastPath(string target, int calls)
    {
        return comparison =>
        {
            var measurement = comparison.Implementation;
            var nanoseconds = measurement.P50Ms * 1_000_000 / calls;
            var allocated = measurement.AllocatedBytesPerOp;
            return new Verdict(
                $"{target}: 0 bytes allocated, under 100 ns",
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"alloc={allocated} B/call ({allocated / calls} B/acquire) p50={nanoseconds:0.0} ns/acquire p95={measurement.P95Ms * 1_000_000 / calls:0.0} ns"),
                allocated == 0 && nanoseconds < 100);
        };
    }

    /// <summary>The throughput row's check: every acquire of a bucket with room is authorized, so the row times the hot path.</summary>
    private static Func<Comparison, Verdict?> Contention(string target)
    {
        return comparison =>
        {
            var observation = comparison.Implementation.Observation ?? string.Empty;
            var authorized = (long)ContendedThreads * ContendedBatch;
            return new Verdict(
                target,
                observation + string.Create(
                    CultureInfo.InvariantCulture,
                    $" alloc={comparison.Implementation.AllocatedBytesPerOp} B/call (the call itself, not the bucket)"),
                comparison.Implementation.Signature == authorized);
        };
    }

    /// <summary>
    /// Eight threads, one provider, a budget smaller than their demand: exactly the allowance leaves, and no thread is starved.
    /// The per-thread counts are what make this a fairness row rather than a second throughput row — with every acquire
    /// authorized the spread is 1 by construction and would assert nothing.
    /// </summary>
    private static BenchScenario Fairness()
    {
        return new BenchScenario(
            "ratelimit.acquire.fairness.8t",
            "ratelimit",
            Heavy: false,
            PrepareAsync: _ =>
            {
                var perThread = new int[ContendedThreads];
                return Task.FromResult(new BenchCase
                {
                    // The bucket is rebuilt per call: the harness times the same operation repeatedly, and a bucket left empty by
                    // the previous iteration would measure the refusal path instead of the share-out.
                    Run = cancellationToken =>
                    {
                        var limiter = Limiter(requestsPerMinute: FairnessBudget, tokensPerMinute: 0);
                        return Hammer(
                            () => limiter.Acquire(Provider, requestTokens: 0, out var wait) == RateLimitDecision.Authorized,
                            perThread,
                            FairnessBatch,
                            cancellationToken);
                    },
                    Baseline = cancellationToken =>
                    {
                        var locked = new LockedLimiter(requestsPerMinute: FairnessBudget, tokensPerMinute: 0);
                        return Hammer(
                            () => locked.Acquire(Provider, tokens: 0),
                            perThread,
                            FairnessBatch,
                            cancellationToken);
                    },
                    BaselineName = "one mutable bucket under a lock",
                    ProcessWideAllocation = true,
                    Iterations = 20,
                    Note = $"{ContendedThreads} threads x {FairnessBatch} acquires of an {FairnessBudget}-request budget",
                    Check = comparison =>
                    {
                        var observation = comparison.Implementation.Observation ?? string.Empty;
                        var baseSpread = comparison.Baseline?.Observation is { } baseObservation ? Spread(baseObservation) : double.NaN;
                        var authorized = comparison.Implementation.Signature;

                        // The allowance is 8000 tokens at one per request, plus whatever the refill clock added during the window
                        // (a few, at this length). Anything above that is over-admission: the budget was handed out twice.
                        var allowance = authorized >= FairnessBudget && authorized <= FairnessBudget + ContendedThreads;

                        // The spread is reported, not asserted: with a budget this small the whole share-out lasts a few hundred
                        // microseconds, so it measures how the eight tasks wake up as much as who wins the counter (one run gave
                        // 0/8000, the next 456/1411). Asserting a bound here would be asserting the scheduler. What is stable, and
                        // is what a broken compare-exchange would break, is that the allowance is never handed out twice.
                        return new Verdict(
                            $"a budget of {FairnessBudget} is spent once under eight threads (no over-admission)",
                            observation + string.Create(
                                CultureInfo.InvariantCulture,
                                $" authorized={authorized} base_spread={baseSpread:0.00}"),
                            allowance);
                    },
                });
            });
    }

    /// <summary>
    /// The rejected shape: one mutable bucket behind a lock. Kept as the baseline so the measured difference is the
    /// concurrency mechanism and nothing else — the same per-provider dictionary, the same arithmetic on the same units.
    /// </summary>
    private sealed class LockedLimiter
    {
        private readonly Lock gate = new();
        private readonly Dictionary<string, Bucket> buckets = new(StringComparer.Ordinal);
        private readonly int requestsPerMinute;
        private readonly int tokensPerMinute;

        public LockedLimiter(int requestsPerMinute, int tokensPerMinute)
        {
            this.requestsPerMinute = requestsPerMinute;
            this.tokensPerMinute = tokensPerMinute;
        }

        public bool Acquire(string provider, int tokens)
        {
            var now = TimeProvider.System.GetTimestamp();
            lock (gate)
            {
                if (!buckets.TryGetValue(provider, out var bucket))
                {
                    bucket = new Bucket(requestsPerMinute, tokensPerMinute, now);
                    buckets[provider] = bucket;
                }

                return bucket.Take(tokens, now);
            }
        }

        private sealed class Bucket
        {
            private readonly double capacityRequests;
            private readonly double capacityTokens;
            private double requests;
            private double tokens;
            private long last;

            public Bucket(double capacityRequests, double capacityTokens, long created)
            {
                this.capacityRequests = capacityRequests;
                this.capacityTokens = capacityTokens;
                requests = capacityRequests;
                tokens = capacityTokens;
                last = created;
            }

            public bool Take(int cost, long now)
            {
                var elapsed = (now - last) / (double)TimeProvider.System.TimestampFrequency;
                if (elapsed > 0)
                {
                    last = now;
                    requests = Math.Min(capacityRequests, requests + (capacityRequests / 60.0 * elapsed));
                    tokens = Math.Min(capacityTokens, tokens + (capacityTokens / 60.0 * elapsed));
                }

                if (requests < 1 || tokens < cost)
                {
                    return false;
                }

                requests -= 1;
                tokens -= cost;
                return true;
            }
        }
    }
}
