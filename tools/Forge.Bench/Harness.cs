using System.Diagnostics;
using System.Globalization;

namespace Forge.Bench;

/// <summary>What one timed call did: the bytes and items it moved, a semantic signature, and a note printed next to the row.</summary>
/// <param name="Bytes">Bytes processed by one call; drives the throughput column when the work is byte bound.</param>
/// <param name="Items">Files, processes or lines processed by one call; used when the work is item bound.</param>
/// <param name="Signature">
/// The number that makes two backends comparable: a match count, a byte count, an exit code. Rows that carry a parity
/// check compare the implementation's signature with the baseline's, so "same answer, different speed" is asserted
/// instead of assumed.
/// </param>
/// <param name="Observation">Free text shown in the row's note column, e.g. <c>matches=420 files=10000</c>.</param>
internal readonly record struct OpResult(long Bytes = 0, long Items = 0, long Signature = 0, string? Observation = null);

/// <summary>One prepared measurement: the operation to time, its optional baseline, and how many times to time it.</summary>
internal sealed record BenchCase
{
    /// <summary>The operation under measurement.</summary>
    public required Func<CancellationToken, Task<OpResult>> Run { get; init; }

    /// <summary>The same semantics through the direct BCL call, or <c>null</c> when this scenario has no baseline.</summary>
    public Func<CancellationToken, Task<OpResult>>? Baseline { get; init; }

    /// <summary>What the baseline is, in one phrase, for the comparison table.</summary>
    public string? BaselineName { get; init; }

    public int Warmup { get; init; } = Harness.DefaultWarmup;

    public int Iterations { get; init; } = Harness.DefaultIterations;

    /// <summary>
    /// The measured call awaits or runs on several threads, so allocation comes from the process-wide counter. A
    /// continuation after an IO await resumes on an IO thread, which is not the thread that called
    /// <see cref="GC.GetAllocatedBytesForCurrentThread"/> - that counter would report only the allocations made before the
    /// first await, and can even go backwards when the continuation lands on a busier thread. Only the synchronous walks
    /// read the per-thread counter.
    /// </summary>
    public bool ProcessWideAllocation { get; init; }

    /// <summary>Sample the working set on a timer while the iterations run, for work that releases memory before it returns.</summary>
    public bool WatchMemory { get; init; }

    /// <summary>A caveat that belongs next to the row, e.g. <c>n=5: 100 MiB per call</c>.</summary>
    public string? Note { get; init; }

    /// <summary>The acceptance check for this row, or <c>null</c> when the row only reports.</summary>
    public Func<Comparison, Verdict?>? Check { get; init; }
}

/// <summary>One entry of the scenario registry: a name, the command family it belongs to, and how to prepare it.</summary>
/// <param name="Heavy">Needs the big fixtures (100 MiB payloads, 100k files, seconds of child processes); <c>--quick</c> skips it.</param>
internal sealed record BenchScenario(
    string Name,
    string Command,
    bool Heavy,
    Func<CancellationToken, Task<BenchCase>> PrepareAsync);

/// <summary>One acceptance check: the requirement, what was measured, and whether it held.</summary>
internal sealed record Verdict(string Target, string Measured, bool Passed);

/// <summary>
/// Latency and counters of one measured operation. Latency is per call: no manual inner loops, so a call that allocates
/// a 100 MiB string is one sample.
/// </summary>
internal sealed record Measurement(
    int Iterations,
    double P50Ms,
    double P95Ms,
    double P99Ms,
    double MinMs,
    double MaxMs,
    long AllocatedBytesPerOp,
    int Gen0,
    int Gen1,
    int Gen2,
    long InitialWorkingSetBytes,
    long PeakWorkingSetBytes,
    long BytesPerOp,
    long ItemsPerOp,
    long Signature,
    bool ProcessWideAllocation,
    string? Observation)
{
    /// <summary>How much the working set grew, in MiB; negative when the GC gave memory back.</summary>
    public double WorkingSetDeltaMb => (PeakWorkingSetBytes - InitialWorkingSetBytes) / (1024.0 * 1024.0);

    public double PeakWorkingSetMb => PeakWorkingSetBytes / (1024.0 * 1024.0);

    /// <summary>Throughput taken from the median latency, so one slow iteration does not move the number.</summary>
    public string ThroughputText
    {
        get
        {
            if (P50Ms <= 0)
            {
                return "n/a";
            }

            var seconds = P50Ms / 1000.0;
            if (BytesPerOp > 0)
            {
                var perSecond = BytesPerOp / seconds;
                return perSecond >= 1024.0 * 1024 * 1024
                    ? $"{perSecond / (1024.0 * 1024 * 1024):0.00} GB/s"
                    : $"{perSecond / (1024.0 * 1024):0.0} MB/s";
            }

            var rate = ItemsPerOp > 0 ? ItemsPerOp / seconds : 1 / seconds;
            var unit = ItemsPerOp > 0 ? "items" : "ops";
            return rate switch
            {
                >= 1_000_000 => $"{rate / 1_000_000:0.0} M{unit}/s",
                >= 1_000 => $"{rate / 1_000:0.0}k {unit}/s",
                _ => $"{rate:0.0} {unit}/s",
            };
        }
    }
}

/// <summary>
/// One row's pair of measurements. <see cref="Implementation"/> and <see cref="Baseline"/> are measured alternately inside
/// the same loop, so a machine that drifts (a thermal step, an antivirus scan, another tenant of the machine) shifts both
/// halves together instead of biasing one of them. <see cref="PairedDeltaP50Ms"/> is the median of the per-iteration
/// differences, which is the number to read when the difference is small compared with the noise of one call - the
/// difference of two medians would carry that noise twice.
/// </summary>
internal sealed record Comparison(Measurement Implementation, Measurement? Baseline, double? PairedDeltaP50Ms)
{
    /// <summary>The paired delta as a share of the baseline median; <c>null</c> without a baseline or a positive median.</summary>
    public double? PairedDeltaShare =>
        PairedDeltaP50Ms is { } delta && Baseline is { P50Ms: > 0 } baseLine ? delta / baseLine.P50Ms : null;

    /// <summary>The implementation's median over the baseline's median: below 1 means the implementation is faster.</summary>
    public double? Ratio => Baseline is { P50Ms: > 0 } baseLine ? Implementation.P50Ms / baseLine.P50Ms : null;
}

/// <summary>
/// The timer. Warmup calls are run and thrown away, then every iteration is timed on its own with
/// <see cref="Stopwatch.GetTimestamp"/>; the samples are sorted for the percentiles.
/// <para>Percentiles are nearest rank (<c>index = ceil(p * n) - 1</c> on the ascending samples), not interpolated: with
/// 20 iterations p99 is the slowest sample, which the table shows as-is rather than smoothing it into a fiction.</para>
/// <para>Latency and the GC counters are collected in one interleaved pass (implementation call, then baseline call, per
/// iteration). Allocation is a second, short pass: <see cref="GC.GetTotalAllocatedBytes(bool)"/> with
/// <c>precise: true</c> suspends the runtime, so reading it around every timed call would perturb the very loop it
/// measures. The counters are read once around <see cref="AllocationCalls"/> calls per side and divided by the count.
/// GC collection counts are read around each call (they are cheap and do not suspend anything).</para>
/// <para>Peak memory is <see cref="Environment.WorkingSet"/>: sampled after every timed call and, for a row that asks for
/// it, every five milliseconds by a background thread. It includes the JIT, the runtime and the fixture pages - only its
/// order of magnitude is meaningful.</para>
/// </summary>
internal static class Harness
{
    public const int DefaultWarmup = 3;
    public const int DefaultIterations = 20;

    /// <summary>How many calls the allocation pass averages over.</summary>
    private const int AllocationCalls = 5;

    public static async Task<Comparison> RunAsync(BenchCase benchCase, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(benchCase);
        var implementation = benchCase.Run;
        var baseline = benchCase.Baseline;

        for (var index = 0; index < benchCase.Warmup; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await implementation(cancellationToken).ConfigureAwait(false);
            if (baseline is not null)
            {
                await baseline(cancellationToken).ConfigureAwait(false);
            }
        }

        var initial = Environment.WorkingSet;
        using var watcher = benchCase.WatchMemory ? new MemoryWatcher(initial) : null;
        var iterations = benchCase.Iterations;
        var implementationSamples = new double[iterations];
        var baselineSamples = baseline is null ? null : new double[iterations];
        var deltas = baseline is null ? null : new double[iterations];
        var implementationCounters = new CollectionCounters();
        var baselineCounters = new CollectionCounters();
        var implementationPeak = initial;
        var baselinePeak = initial;
        var implementationResult = default(OpResult);
        var baselineResult = default(OpResult);

        for (var index = 0; index < iterations; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            implementationResult = await TimedAsync(
                implementation,
                implementationSamples,
                index,
                implementationCounters,
                cancellationToken).ConfigureAwait(false);
            implementationPeak = Math.Max(implementationPeak, Environment.WorkingSet);
            if (baseline is not null)
            {
                baselineResult = await TimedAsync(baseline, baselineSamples!, index, baselineCounters, cancellationToken)
                    .ConfigureAwait(false);
                baselinePeak = Math.Max(baselinePeak, Environment.WorkingSet);
                deltas![index] = implementationSamples[index] - baselineSamples![index];
            }
        }

        // The watcher's high-water mark belongs to the timing loop; the allocation pass below would only add its own noise.
        var peak = Math.Max(implementationPeak, watcher?.Peak ?? 0);

        var implementationAllocation = await AllocatedPerCallAsync(
            implementation,
            benchCase.ProcessWideAllocation,
            cancellationToken).ConfigureAwait(false);
        var baselineAllocation = baseline is null
            ? 0
            : await AllocatedPerCallAsync(baseline, benchCase.ProcessWideAllocation, cancellationToken).ConfigureAwait(false);

        var implementationMeasurement = Build(
            implementationResult,
            implementationSamples,
            implementationCounters,
            implementationAllocation,
            initial,
            peak,
            benchCase.ProcessWideAllocation);
        var baselineMeasurement = baselineSamples is null
            ? null
            : Build(
                baselineResult,
                baselineSamples,
                baselineCounters,
                baselineAllocation,
                initial,
                baselinePeak,
                benchCase.ProcessWideAllocation);

        double? paired = null;
        if (deltas is not null)
        {
            var sorted = (double[])deltas.Clone();
            Array.Sort(sorted);
            paired = Percentile(sorted, 0.50);
        }

        return new Comparison(implementationMeasurement, baselineMeasurement, paired);
    }

    /// <summary>Times one call and folds its GC counters into <paramref name="counters"/>.</summary>
    private static async Task<OpResult> TimedAsync(
        Func<CancellationToken, Task<OpResult>> operation,
        double[] samples,
        int index,
        CollectionCounters counters,
        CancellationToken cancellationToken)
    {
        var gen0 = GC.CollectionCount(0);
        var gen1 = GC.CollectionCount(1);
        var gen2 = GC.CollectionCount(2);
        var start = Stopwatch.GetTimestamp();
        var result = await operation(cancellationToken).ConfigureAwait(false);
        samples[index] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        counters.Add(GC.CollectionCount(0) - gen0, GC.CollectionCount(1) - gen1, GC.CollectionCount(2) - gen2);
        return result;
    }

    /// <summary>Allocation of one call, from a single counter bracket around a short run of calls.</summary>
    private static async Task<long> AllocatedPerCallAsync(
        Func<CancellationToken, Task<OpResult>> operation,
        bool processWide,
        CancellationToken cancellationToken)
    {
        var calls = AllocationCalls;
        var before = AllocatedBytes(processWide);
        for (var index = 0; index < calls; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await operation(cancellationToken).ConfigureAwait(false);
        }

        return (AllocatedBytes(processWide) - before) / calls;
    }

    private static Measurement Build(
        OpResult result,
        double[] samples,
        CollectionCounters counters,
        long allocatedBytesPerOp,
        long initialWorkingSet,
        long peakWorkingSet,
        bool processWide)
    {
        var sorted = (double[])samples.Clone();
        Array.Sort(sorted);
        return new Measurement(
            samples.Length,
            Percentile(sorted, 0.50),
            Percentile(sorted, 0.95),
            Percentile(sorted, 0.99),
            sorted[0],
            sorted[^1],
            allocatedBytesPerOp,
            counters.Gen0,
            counters.Gen1,
            counters.Gen2,
            initialWorkingSet,
            peakWorkingSet,
            result.Bytes,
            result.Items,
            result.Signature,
            processWide,
            result.Observation);
    }

    /// <summary>Nearest-rank percentile over an ascending sample array: no interpolation, so every value is a real sample.</summary>
    public static double Percentile(double[] sortedSamples, double percentile)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sortedSamples.Length, 1);
        var rank = (int)Math.Ceiling(percentile * sortedSamples.Length);
        return sortedSamples[Math.Clamp(rank - 1, 0, sortedSamples.Length - 1)];
    }

    private static long AllocatedBytes(bool processWide) =>
        processWide ? GC.GetTotalAllocatedBytes(precise: true) : GC.GetAllocatedBytesForCurrentThread();

    /// <summary>Formats a millisecond count for the fixed-width table.</summary>
    public static string Milliseconds(double value) => value.ToString("0.000", CultureInfo.InvariantCulture);

    private sealed class CollectionCounters
    {
        public int Gen0 { get; private set; }

        public int Gen1 { get; private set; }

        public int Gen2 { get; private set; }

        public void Add(int gen0, int gen1, int gen2)
        {
            Gen0 += gen0;
            Gen1 += gen1;
            Gen2 += gen2;
        }
    }

    /// <summary>Samples the process working set from a dedicated thread, so an allocation that is released before the call returns still shows.</summary>
    private sealed class MemoryWatcher : IDisposable
    {
        private readonly Thread thread;
        private readonly CancellationTokenSource stop = new();
        private long peak;

        public MemoryWatcher(long initial)
        {
            peak = initial;
            thread = new Thread(Watch) { IsBackground = true, Name = "bench-memory" };
            thread.Start();
        }

        public long Peak => Volatile.Read(ref peak);

        public void Dispose()
        {
            stop.Cancel();
            thread.Join(millisecondsTimeout: 500);
            stop.Dispose();
        }

        private void Watch()
        {
            var wait = stop.Token.WaitHandle;
            while (true)
            {
                var current = Environment.WorkingSet;
                if (current > Volatile.Read(ref peak))
                {
                    Interlocked.Exchange(ref peak, current);
                }

                if (wait.WaitOne(5))
                {
                    return;
                }
            }
        }
    }
}
