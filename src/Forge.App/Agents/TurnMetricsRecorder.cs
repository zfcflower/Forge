using System.Threading.Channels;
using Forge.Abstractions;
using Forge.App.Localization;
using Forge.App.Logging;
using Forge.Core.Storage;
using ZLogger;

namespace Forge.App.Agents;

/// <summary>
/// One turn's metrics on their way to the <c>turn_metrics</c> table: value copies and string references only, so the turn
/// end hands the row over with one channel write and no allocation at all. The store's own <see cref="TurnMetric"/> record
/// is built from it on the writer's thread, never on the turn's.
/// </summary>
internal readonly struct TurnMetricsRow
{
    public TurnMetricsRow(
        string sessionId,
        int turn,
        DateTimeOffset startedAt,
        DateTimeOffset endedAt,
        TurnMetricStatus status,
        string? provider = null,
        string? model = null,
        string? error = null,
        int? inputTokens = null,
        int? outputTokens = null,
        int? cacheReadTokens = null,
        int? cacheWriteTokens = null,
        int? cacheWrite1hTokens = null,
        int? toolCalls = null)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        SessionId = sessionId;
        Turn = turn;
        StartedAt = startedAt;
        EndedAt = endedAt;
        Status = status;
        Provider = provider;
        Model = model;
        Error = error;
        InputTokens = inputTokens;
        OutputTokens = outputTokens;
        CacheReadTokens = cacheReadTokens;
        CacheWriteTokens = cacheWriteTokens;
        CacheWrite1hTokens = cacheWrite1hTokens;
        ToolCalls = toolCalls;
    }

    public string SessionId { get; }

    public int Turn { get; }

    public DateTimeOffset StartedAt { get; }

    public DateTimeOffset EndedAt { get; }

    public TurnMetricStatus Status { get; }

    public string? Provider { get; }

    public string? Model { get; }

    public string? Error { get; }

    public int? InputTokens { get; }

    public int? OutputTokens { get; }

    public int? CacheReadTokens { get; }

    public int? CacheWriteTokens { get; }

    public int? CacheWrite1hTokens { get; }

    public int? ToolCalls { get; }

    /// <summary>
    /// How long the model spent streaming this turn's answers, in milliseconds — the difference of the root instance's
    /// stream counter between the turn start and the turn end. Always written, whatever the outcome: unlike the token
    /// columns, stream time is never unknown. A turn that streamed nothing (no output at all, a non-streaming fallback,
    /// a failure before the first increment) records zero, and the wall clock is never a substitute.
    /// </summary>
    public long StreamMs { get; init; }

    /// <summary>The store's shape, built on the writer's thread (one small record per row, off the turn).</summary>
    public TurnMetric ToMetric() => new(
        SessionId,
        Turn,
        StartedAt,
        EndedAt,
        Status,
        Provider,
        Model,
        Error,
        InputTokens,
        OutputTokens,
        CacheReadTokens,
        CacheWriteTokens,
        CacheWrite1hTokens,
        ToolCalls,
        TimeSpan.FromMilliseconds(StreamMs));
}

/// <summary>
/// The mapping from what a turn end knows in memory to the row it records: which outcome it was, which error text the user
/// saw, the root agent's token difference and how long the model spent streaming. Kept apart from the session so the rules
/// (failure means every token column stays null, an unreported turn is not a zero, stream time is always written) are
/// testable without a turn.
/// </summary>
internal static class TurnMetricsRowFactory
{
    /// <summary>
    /// Builds one row. <paramref name="rootInstanceUsage"/> is the root agent's instance counter at the turn's end and
    /// <paramref name="turnStartUsage"/> the snapshot taken when the turn began: their difference is this turn's tokens,
    /// null when the provider reported nothing (an unreported turn stays unknown, never zero).
    /// <paramref name="rootStreamElapsed"/> and <paramref name="turnStartElapsed"/> are the same pair of snapshots for the
    /// model's streaming time, and their difference is written on every row: stream time is measured per model call and
    /// accumulates over the turn's calls, so a turn that streamed nothing records zero and a failed turn still records what
    /// it streamed before it failed. <paramref name="error"/> is the same text the turn card shows and
    /// <paramref name="rootFailure"/> the agent's own failure message, which reaches the App only as event text (see
    /// <see cref="BuildStatus"/>); <paramref name="toolCalls"/> is null for a turn the App never watched (a delivery Core
    /// drove itself), which counts no tools rather than claiming zero.
    /// <para>Stream time has no "unknown": a row whose <c>stream_ms</c> would be missing means the adapter never measured
    /// its calls, which is an adapter defect to be fixed and pinned by tests — never a reason to fall back to the turn's
    /// wall clock here (that clock includes queueing, approvals, tool runs and git snapshots).</para>
    /// <para>This is the entry for a turn the App watched run: the outcome is derived from <paramref name="error"/> and
    /// <paramref name="rootFailure"/>. A caller that already knows the outcome uses <see cref="BuildForOutcome"/>.</para>
    /// </summary>
    public static TurnMetricsRow Build(
        string sessionId,
        int turn,
        DateTimeOffset startedAt,
        DateTimeOffset endedAt,
        LocText? error,
        string? rootFailure,
        TokenUsage rootInstanceUsage,
        TokenUsage turnStartUsage,
        TimeSpan rootStreamElapsed,
        TimeSpan turnStartElapsed,
        int? toolCalls,
        string? provider,
        string? model) =>
        BuildRow(
            sessionId,
            turn,
            startedAt,
            endedAt,
            BuildStatus(error, rootFailure),
            BuildError(error, rootFailure),
            rootInstanceUsage,
            turnStartUsage,
            rootStreamElapsed,
            turnStartElapsed,
            toolCalls,
            provider,
            model);

    /// <summary>
    /// Builds one row from the outcome Core reported through the delivery seam: a turn Core drove itself has no
    /// <see cref="LocText"/> in hand and no view to read one from, only the status its receipt carries, so the status is
    /// handed over instead of derived — a failed or stopped delivery must not read as a completed turn.
    /// <paramref name="error"/> is what came with that outcome and null when it came with nothing: the App never makes an
    /// error up. The token columns follow the same rule as <see cref="Build"/> (a turn that did not complete measured no
    /// tokens worth keeping), and the streaming time is written whatever the outcome.
    /// </summary>
    public static TurnMetricsRow BuildForOutcome(
        string sessionId,
        int turn,
        DateTimeOffset startedAt,
        DateTimeOffset endedAt,
        TurnMetricStatus status,
        string? error,
        TokenUsage rootInstanceUsage,
        TokenUsage turnStartUsage,
        TimeSpan rootStreamElapsed,
        TimeSpan turnStartElapsed,
        int? toolCalls,
        string? provider,
        string? model) =>
        BuildRow(
            sessionId,
            turn,
            startedAt,
            endedAt,
            status,
            error,
            rootInstanceUsage,
            turnStartUsage,
            rootStreamElapsed,
            turnStartElapsed,
            toolCalls,
            provider,
            model);

    /// <summary>
    /// The row status of an outcome Core reported through the delivery seam. <see cref="SessionReplyStatus"/> has exactly the
    /// three endings a delivery has; <see cref="TurnMetricStatus.Limit"/> stays unreachable through this seam for the same
    /// reason it is unreachable in <see cref="BuildStatus"/> (Core reports a limit as a plain failure). A member added to that
    /// enum throws here instead of reading as a completed turn.
    /// </summary>
    public static TurnMetricStatus StatusOf(SessionReplyStatus status) => status switch
    {
        SessionReplyStatus.Completed => TurnMetricStatus.Completed,
        SessionReplyStatus.Failed => TurnMetricStatus.Failed,
        SessionReplyStatus.Stopped => TurnMetricStatus.Stopped,
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "A reply status added to the enum must be mapped here explicitly."),
    };

    /// <summary>The one row shape both entry points share: an outcome already decided, and a token block only a completed turn fills.</summary>
    private static TurnMetricsRow BuildRow(
        string sessionId,
        int turn,
        DateTimeOffset startedAt,
        DateTimeOffset endedAt,
        TurnMetricStatus status,
        string? error,
        TokenUsage rootInstanceUsage,
        TokenUsage turnStartUsage,
        TimeSpan rootStreamElapsed,
        TimeSpan turnStartElapsed,
        int? toolCalls,
        string? provider,
        string? model)
    {
        int? input = null, output = null, cacheRead = null, cacheWrite = null, cacheWrite1h = null;
        if (status is TurnMetricStatus.Completed)
        {
            // The turn's tokens are the instance counter's difference since the turn began — two snapshots taken on the
            // event thread, immune to when the UI coalescer happens to flush AgentTurnCompleted. A turn the provider
            // reported nothing for keeps every column null (unknown is never zero), and a failed turn keeps them null
            // whatever usage reached the counter before the failure.
            input = Math.Max(0, rootInstanceUsage.InputTokens - turnStartUsage.InputTokens);
            output = Math.Max(0, rootInstanceUsage.OutputTokens - turnStartUsage.OutputTokens);
            cacheRead = Math.Max(0, rootInstanceUsage.CacheReadTokens - turnStartUsage.CacheReadTokens);
            cacheWrite = Math.Max(0, rootInstanceUsage.CacheWriteTokens - turnStartUsage.CacheWriteTokens);
            cacheWrite1h = Math.Max(0, rootInstanceUsage.CacheWrite1hTokens - turnStartUsage.CacheWrite1hTokens);
            if (input is 0 && output is 0 && cacheRead is 0 && cacheWrite is 0 && cacheWrite1h is 0)
            {
                input = output = cacheRead = cacheWrite = cacheWrite1h = null;
            }
        }

        return new TurnMetricsRow(
            sessionId,
            turn,
            startedAt.ToUniversalTime(),
            endedAt.ToUniversalTime(),
            status,
            provider,
            model,
            error,
            input,
            output,
            cacheRead,
            cacheWrite,
            cacheWrite1h,
            toolCalls)
        {
            // Written for every outcome, outside the token block above: the two snapshots are always taken, they are
            // clamped because a counter that went backwards never means negative streaming, and nothing here reads a
            // clock (the adapter measured the stream, this only differences it).
            StreamMs = Math.Max(0, (long)(rootStreamElapsed - turnStartElapsed).TotalMilliseconds),
        };
    }

    /// <summary>
    /// The outcome the row records. A stop the user asked for is <see cref="TurnMetricStatus.Stopped"/> whatever else the
    /// turn reported; any other error is a failure, and an agent failure reaches here as its <c>AgentFailed</c> text because
    /// Core reports the exception as an event instead of throwing it at the session.
    /// <para><see cref="TurnMetricStatus.Limit"/> is never produced: <c>AgentTurnLimitException</c> is caught inside
    /// <c>AgentHost.RunAsync</c> like every other failure and arrives as an <c>AgentFailed</c> string with nothing that tells
    /// it apart from any other message. Matching that sentence would bind the App to a Core-internal format string, so a turn
    /// that hit the limit is recorded as <see cref="TurnMetricStatus.Failed"/> — the honest reading of what the App knows.</para>
    /// </summary>
    public static TurnMetricStatus BuildStatus(LocText? error, string? rootFailure)
    {
        if (error is { IsEmpty: false } failure)
        {
            return failure.Equals(SessionStrings.Stopped) ? TurnMetricStatus.Stopped : TurnMetricStatus.Failed;
        }

        return string.IsNullOrEmpty(rootFailure) ? TurnMetricStatus.Completed : TurnMetricStatus.Failed;
    }

    /// <summary>The text the user already saw for this turn, reused by reference; null for a turn that went through.</summary>
    public static string? BuildError(LocText? error, string? rootFailure) =>
        error is { IsEmpty: false } failure ? failure.Resolve() : rootFailure;
}

/// <summary>
/// The App's per-turn metrics writer: a bounded queue plus one background task, so a turn end never waits on, queries or
/// formats anything for the sake of its own metrics.
/// <para><b>Contract.</b> <see cref="Record"/> never throws and never awaits; a database without the <c>turn_metrics</c> table,
/// a full queue and a failing write all end the same way — the row is dropped (a warning once, then silently). The turn is
/// never the thing that pays for its metrics (the same best-effort contract as <c>IUsageRecorder</c>). The writer finishes
/// the queued rows within a two-second budget on <see cref="DisposeAsync"/> and abandons the rest, so closing the app never
/// waits on metrics either.</para>
/// <para><b>Cost of one <see cref="Record"/>.</b> A null/existence check on a cached flag, then
/// <see cref="ChannelWriter{T}.TryWrite"/>: one struct copy, no allocation (target: 0 B, under a microsecond), no lock taken
/// by the caller, no closure. The row was built from values the turn already holds, so the parameter conversion
/// (<see cref="TurnMetric"/>, SQL parameters, status text) all happens on the writer's thread.</para>
/// </summary>
internal sealed class TurnMetricsRecorder : IAsyncDisposable
{
    /// <summary>How many turns may wait for the database before rows start being dropped.</summary>
    public const int Capacity = 64;

    /// <summary>How long <see cref="DisposeAsync"/> may spend finishing queued rows; the rest are abandoned.</summary>
    private static readonly TimeSpan DrainBudget = TimeSpan.FromSeconds(2);

    /// <summary>
    /// After abandoning, how long to let the write already in flight finish. It is longer than the store's busy timeout (2s),
    /// so the write has ended, one way or the other, before the caller disposes the store; only a wedged database ever
    /// spends it.
    /// </summary>
    private static readonly TimeSpan InFlightGrace = TimeSpan.FromSeconds(3);

    private readonly ITurnMetricsStore store;
    private readonly Action<string>? diagnostic;
    private readonly Channel<TurnMetricsRow> queue;
    private readonly TaskCompletionSource probeDone = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task writer;

    /// <summary>
    /// True until the one existence probe answers false, and for good after a true. Optimistic on purpose: a turn that ends
    /// before the probe returns (a quick first turn right after startup) queues its row like any other, and the writer drops
    /// the queue only if the probe then says there is no table.
    /// </summary>
    private volatile bool enabled = true;

    private int dropped;
    private int dropReported;
    private int failureReported;
    private int abandoned;
    private int disposed;

    /// <param name="store">Where the rows go; the recorder only ever calls <see cref="ITurnMetricsStore.ExistsAsync"/> and
    /// <see cref="ITurnMetricsStore.WriteAsync"/>, and never on the caller's thread.</param>
    /// <param name="diagnostic">Receives every line instead of the usage log (a host without a log ring).</param>
    public TurnMetricsRecorder(ITurnMetricsStore store, Action<string>? diagnostic = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        this.store = store;
        this.diagnostic = diagnostic;
        queue = Channel.CreateBounded<TurnMetricsRow>(new BoundedChannelOptions(Capacity)
        {
            // TryWrite is the only entry point, so the caller never waits: a full queue refuses the row instead.
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });
        writer = Task.Run(WriteAsync);
    }

    /// <summary>False once the database is known to lack the table: the turn end reads it to skip building a row at all.</summary>
    public bool Enabled => enabled;

    /// <summary>
    /// Settles when the one existence probe has answered (or failed): after this, <see cref="Enabled"/> is final. A test
    /// awaits it instead of polling; the App never reads it.
    /// </summary>
    internal Task Probed => probeDone.Task;

    /// <summary>How many rows were dropped so far (queue full, or a closed recorder). Diagnostics only.</summary>
    internal int Dropped => Volatile.Read(ref dropped);

    /// <summary>
    /// Queues one turn's row. Synchronous, never throws, never awaits, allocates nothing: a recorder whose table is missing,
    /// a full queue or a recorder already closed just drops the row.
    /// </summary>
    public void Record(TurnMetricsRow row)
    {
        if (!enabled || Volatile.Read(ref disposed) != 0)
        {
            return;
        }

        if (!queue.Writer.TryWrite(row))
        {
            NoteDropped();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        queue.Writer.TryComplete();
        try
        {
            var drained = await Task.WhenAny(writer, Task.Delay(DrainBudget)).ConfigureAwait(false);
            if (drained != writer)
            {
                // Abandoning is deliberate: queued rows are the app's least valuable data, and a shutdown must not wait on
                // them. The writer stops at its next row boundary and whatever is still in the store write fails silently.
                Volatile.Write(ref abandoned, 1);
                Report($"closing with turn metrics still queued (capacity {Capacity}); the rest are abandoned");
                await Task.WhenAny(writer, Task.Delay(InFlightGrace)).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Nothing a metrics writer does may reach the shutdown path.
            Report($"stopping the turn metrics writer failed: {exception.GetType().Name}: {exception.Message}");
        }
    }

    /// <summary>
    /// The one background task: probe the table once, then write row by row. Every failure is swallowed here — a broken
    /// database is reported once and then only costs the rows it cannot take.
    /// </summary>
    private async Task WriteAsync()
    {
        enabled = await ProbeAsync().ConfigureAwait(false);
        probeDone.TrySetResult();
        if (!enabled)
        {
            queue.Writer.TryComplete();
            return;
        }

        try
        {
            await foreach (var row in queue.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                if (Volatile.Read(ref abandoned) != 0)
                {
                    return;
                }

                try
                {
                    await store.WriteAsync(row.ToMetric(), CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    NoteWriteFailure(exception);
                }
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            NoteWriteFailure(exception);
        }
    }

    /// <summary>
    /// The cached existence check, run once when the recorder is built. A probe that cannot answer counts as "no table": the
    /// feature bypasses itself rather than retrying a database that is already unhappy at startup.
    /// </summary>
    private async Task<bool> ProbeAsync()
    {
        try
        {
            return await store.ExistsAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Report($"turn metrics unavailable ({exception.GetType().Name}: {exception.Message}); every turn's row is dropped");
            return false;
        }
    }

    /// <summary>
    /// A full queue means the database is not keeping up, so the metric is the first thing to give: counted, and reported
    /// exactly once — a warning per dropped row would drown the log in the very situation it reports.
    /// </summary>
    private void NoteDropped()
    {
        Interlocked.Increment(ref dropped);
        if (Interlocked.Exchange(ref dropReported, 1) != 0)
        {
            return;
        }

        Report($"turn metrics row dropped: the writer is {Capacity} rows behind; further drops are counted and stay silent");
    }

    /// <summary>One line per process for the same reason as <see cref="NoteDropped"/>; the rows behind it are lost silently.</summary>
    private void NoteWriteFailure(Exception exception)
    {
        if (Interlocked.Exchange(ref failureReported, 1) != 0)
        {
            return;
        }

        Report($"turn metrics write failed: {exception.GetType().Name}: {exception.Message}; further failures stay silent");
    }

    private void Report(string message)
    {
        if (diagnostic is not null)
        {
            diagnostic(message);
            return;
        }

        AppLogs.Usage.ZLogWarning($"{message}");
    }
}
