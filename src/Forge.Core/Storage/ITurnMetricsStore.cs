using Forge.Abstractions;

namespace Forge.Core.Storage;

/// <summary>
/// How a main-session turn ended. The storage layer persists the lowercase name as text; a failed turn keeps its row with
/// every token column null — unknown stays unknown, it is never written as zero.
/// </summary>
public enum TurnMetricStatus
{
    /// <summary>The turn ran to a normal stop (an <c>AgentTurnCompleted</c> fired).</summary>
    Completed,

    /// <summary>The user stopped the turn.</summary>
    Stopped,

    /// <summary>The turn failed with an error.</summary>
    Failed,

    /// <summary>The turn exceeded the agent's turn limit (<c>AgentTurnLimitException</c>).</summary>
    Limit,
}

/// <summary>
/// One main-session turn's persisted metrics: the boundaries no stored message can reconstruct (start, end, outcome, who
/// ran it, how long the model spent streaming) plus this turn's token snapshot in the turn card's own arithmetic — the
/// root agent's instance-counter difference, all five token kinds, null on every column a failed or unreported turn never
/// measured. <see cref="TurnMetric.StreamElapsed"/> is never null and never a fallback: every model call produces it
/// (first output increment to stream end, accumulated over the turn's calls), zero for a turn that streamed nothing —
/// a missing measurement is an adapter defect. Derived quantities (cost, cache-hit ratio) are never stored; readers
/// compute them. <see cref="LastSeq"/> is the turn's exact stream position — the seq of the last message the turn
/// persisted — and the one key a replay pairs this row by: seqs are immutable, monotonic and written by the same
/// writer as the messages, so they cannot drift the way a counted turn number does (stored user messages exist that
/// never opened a live turn: receipts, interjections). Null on rows recorded before the column existed; such rows pair
/// with nothing and read "—", never with a guess.
/// </summary>
public sealed record TurnMetric(
    string SessionId,
    int Turn,
    DateTimeOffset StartedAt,
    DateTimeOffset EndedAt,
    TurnMetricStatus Status,
    string? Provider = null,
    string? Model = null,
    string? Error = null,
    int? InputTokens = null,
    int? OutputTokens = null,
    int? CacheReadTokens = null,
    int? CacheWriteTokens = null,
    int? CacheWrite1hTokens = null,
    int? ToolCalls = null,
    TimeSpan StreamElapsed = default,
    ThinkingEffort? Effort = null,
    long? LastSeq = null);

/// <summary>
/// Per-turn metrics of main sessions, keyed by <c>(session_id, turn)</c> — the table records the number the transcript
/// computed, it never issues turn numbers of its own. The App side writes it from a best-effort background recorder that
/// never blocks or fails a turn; nothing here may join the session-history write batch. Rows are not copied on handover
/// or fork: metrics belong to the session that measured them, and a copied row would collide with the copy's own turn
/// numbering. <see cref="PurgeAsync"/>-style session deletion removes them with every other session table.
/// </summary>
public interface ITurnMetricsStore : IAsyncDisposable, IDisposable
{
    /// <summary>
    /// True when the database carries the <c>turn_metrics</c> table. A database from before this feature has none and the
    /// whole feature bypasses to a no-op; the caller caches the answer.
    /// </summary>
    Task<bool> ExistsAsync(CancellationToken ct);

    /// <summary>Upserts one turn's row; idempotent on <c>(session_id, turn)</c> so a retried write cannot duplicate.</summary>
    Task WriteAsync(TurnMetric metric, CancellationToken ct);

    /// <summary>Every recorded turn of the session in turn order; empty (not an error) when the table is missing.</summary>
    Task<IReadOnlyList<TurnMetric>> ListAsync(string sessionId, CancellationToken ct);

    /// <summary>
    /// The session's highest recorded turn number, 0 when none. Turn numbering takes the MAX over this and
    /// <c>turn_checkpoints</c>, so a general (non-git) session continues its numbering after the window reopens.
    /// </summary>
    Task<int> MaxTurnAsync(string sessionId, CancellationToken ct);
}
