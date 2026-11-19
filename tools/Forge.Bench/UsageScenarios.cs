using Forge.Abstractions;
using Forge.App.Agents;
using Forge.Core.Storage;

namespace Forge.Bench;

/// <summary>
/// The turn-metrics paths. <c>messagejson</c> is the per-request usage JSON every persisted assistant message carries —
/// the one hot serialization the feature touches; <c>plain</c> must stay byte-identical for providers without a 1-hour
/// cache write, <c>cache1h</c> carries the fifth field the old serialization dropped (its roundtrip verdict failing was
/// that defect, not a broken bench). <c>recorder.enqueue</c> times row build + one <c>Record()</c> per turn end — the
/// whole hot-path cost, target zero allocation on the calling thread — over a store whose writes complete instantly, so
/// the number isolates enqueue from SQLite latency. <c>recorder.store-write</c> measures the real single-row upsert on
/// WAL: the background price, which must stay orders of magnitude off the user path. Run with <c>--filter usage</c>.
/// </summary>
internal static class UsageScenarios
{
    public static IEnumerable<BenchScenario> Create(Fixtures fixtures)
    {
        yield return Roundtrip("usage.messagejson.plain", cacheWrite1h: 0);
        yield return Roundtrip("usage.messagejson.cache1h", cacheWrite1h: 9_876);
        yield return RecorderEnqueue();
        yield return RecorderStoreWrite();
    }

    private static BenchScenario Roundtrip(string name, int cacheWrite1h) =>
        new(
            name,
            "usage",
            Heavy: false,
            PrepareAsync: _ =>
            {
                var usage = new TokenUsage(
                    InputTokens: 37_412,
                    OutputTokens: 2_517,
                    CacheReadTokens: 31_502,
                    CacheWriteTokens: 2_104,
                    CacheWrite1hTokens: cacheWrite1h);
                return Task.FromResult(new BenchCase
                {
                    Warmup = 200,
                    Iterations = 2_000,
                    Note = $"cacheWrite1h={cacheWrite1h}; one serialize+parse per persisted assistant message",
                    Run = _ =>
                    {
                        var json = MessageJson.SerializeUsage(usage);
                        var parsed = MessageJson.ParseUsage(json);
                        var equal = parsed is { } value && value == usage;
                        return Task.FromResult(new OpResult(
                            Items: 1,
                            Signature: equal ? 1 : 0,
                            Observation: equal ? "roundtrip equal" : "roundtrip lost a field"));
                    },
                    Check = comparison => comparison.Implementation.Signature == 1
                        ? new Verdict("serialize→parse returns the same five token kinds", "signature=1", true)
                        : new Verdict("serialize→parse returns the same five token kinds", "signature=0 (a field was dropped)", false),
                });
            });

    private static BenchScenario RecorderEnqueue() =>
        new(
            "usage.recorder.enqueue",
            "usage",
            Heavy: false,
            PrepareAsync: async cancellationToken =>
            {
                var recorder = new TurnMetricsRecorder(new NullMetricsStore(), _ => { });
                await recorder.Probed.ConfigureAwait(false);
                var started = new DateTimeOffset(2026, 11, 18, 6, 0, 0, TimeSpan.Zero);
                var done = Task.FromResult(new OpResult(Items: 1, Signature: 1));
                return new BenchCase
                {
                    Warmup = 1_000,
                    Iterations = 100_000,
                    Note = "row build + one Record() per turn end; the hot-path contract (target <1µs, 0 B/op on the calling thread). 100k back-to-back turns is far beyond real cadence, so a full channel dropping is by-design behaviour, not a failure",
                    Run = _ =>
                    {
                        recorder.Record(new TurnMetricsRow(
                            "bench",
                            7,
                            started,
                            started.AddSeconds(46),
                            TurnMetricStatus.Completed,
                            "anthropic",
                            "claude-sonnet-4-5",
                            null,
                            37_412,
                            2_517,
                            31_502,
                            2_104,
                            9_876,
                            6));
                        return done;
                    },
                    Check = comparison => new Verdict(
                        "one Record stays a sub-microsecond, never-throwing call",
                        $"p50={comparison.Implementation.P50Ms * 1000:0.##}µs < 10µs, alloc={comparison.Implementation.AllocatedBytesPerOp} B/op",
                        comparison.Implementation.P50Ms < 0.01),
                };
            });

    private static BenchScenario RecorderStoreWrite() =>
        new(
            "usage.recorder.store-write",
            "usage",
            Heavy: false,
            PrepareAsync: async cancellationToken =>
            {
                var directory = Path.Combine(Path.GetTempPath(), "forge-bench-turnmetrics-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(directory);

                // The session store owns the schema: opening one on a fresh directory runs the ladder, and with it the
                // V1 build script that carries turn_metrics. A cheap read forces the lazy initialization.
                await using (var sessionStore = new SqliteSessionStore(directory))
                {
                    await sessionStore.LastSeqAsync("bench", cancellationToken).ConfigureAwait(false);
                }

                var store = new SqliteTurnMetricsStore(directory);
                var started = new DateTimeOffset(2026, 11, 18, 6, 0, 0, TimeSpan.Zero);
                var turn = 0;
                return new BenchCase
                {
                    Warmup = 5,
                    Iterations = 200,
                    ProcessWideAllocation = true,
                    Note = "the real single-row BEGIN IMMEDIATE upsert on WAL/NORMAL — the background price, off the user path; each iteration inserts a fresh turn",
                    Run = async _ =>
                    {
                        await store.WriteAsync(
                            new TurnMetric(
                                "bench",
                                Interlocked.Increment(ref turn),
                                started,
                                started.AddSeconds(46),
                                TurnMetricStatus.Completed,
                                "anthropic",
                                "claude-sonnet-4-5",
                                null,
                                37_412,
                                2_517,
                                31_502,
                                2_104,
                                9_876,
                                6),
                            CancellationToken.None).ConfigureAwait(false);
                        return new OpResult(Items: 1, Signature: 1);
                    },
                    Check = comparison =>
                    {
                        var p50 = comparison.Implementation.P50Ms;
                        store.DisposeAsync().AsTask().GetAwaiter().GetResult();
                        try
                        {
                            Directory.Delete(directory, true);
                        }
                        catch (IOException)
                        {
                            // A locked file in the temp directory is not worth failing a bench over.
                        }

                        return new Verdict(
                            "one upsert stays a background-priced write",
                            $"p50={p50:0.###} ms < 5 ms",
                            p50 < 5);
                    },
                };
            });

    /// <summary>
    /// An in-memory stand-in whose writes complete instantly: it isolates the enqueue measurement (the hot-path claim)
    /// from SQLite write latency, which <c>recorder.store-write</c> measures separately against the real store.
    /// </summary>
    private sealed class NullMetricsStore : ITurnMetricsStore
    {
        public Task<bool> ExistsAsync(CancellationToken ct) => Task.FromResult(true);

        public Task WriteAsync(TurnMetric metric, CancellationToken ct) => Task.CompletedTask;

        public Task<IReadOnlyList<TurnMetric>> ListAsync(string sessionId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<TurnMetric>>(Array.Empty<TurnMetric>());

        public Task<int> MaxTurnAsync(string sessionId, CancellationToken ct) => Task.FromResult(0);

        public ValueTask DisposeAsync() => default;

        public void Dispose()
        {
        }
    }
}
