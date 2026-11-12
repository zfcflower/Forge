using Forge.Abstractions;

namespace Forge.Core.Storage;

/// <summary>Inclusive sequence range handled by one summary.</summary>
public sealed record SummaryRange(long FromSeq, long ToSeq);

/// <summary>
/// The context a model sees for a compacted session: an optional layer-2 session summary, the live
/// layer-1 stage summaries, and the raw messages that start after the last summarized sequence. The current
/// compactor only writes layer-2 records that cover the session from its start, so a view it leaves behind has no
/// stage summaries; layer-1 records come from sessions compacted by earlier versions and are still read.
/// </summary>
public sealed record ContextView(
    SummaryRecord? SessionSummary,
    IReadOnlyList<SummaryRecord> StageSummaries,
    long LastSummarizedSeq)
{
    public long RawFromSeq => LastSummarizedSeq + 1;
}

/// <summary>
/// Where a compaction cuts the still-raw tail (indices into the raw list, ascending). Messages before
/// <see cref="KeepFrom"/> are summarized and the rest stays raw. When the kept tail starts inside an interaction
/// (a split turn), <see cref="TurnStart"/> is the index of the user message that opened it: messages before it are
/// the history part and <c>[TurnStart, KeepFrom)</c> is the turn prefix, summarized separately; otherwise it is -1.
/// </summary>
public readonly record struct CompactionCut(int KeepFrom, int TurnStart)
{
    public bool IsSplitTurn => TurnStart >= 0;

    /// <summary>End (exclusive) of the history part: everything summarized as ordinary history.</summary>
    public int HistoryEnd => IsSplitTurn ? TurnStart : KeepFrom;
}

/// <summary>Pure helpers over stored <see cref="SummaryRecord"/> layers and the raw tail.</summary>
public static class SummaryLayers
{
    public const int SessionLayer = 2;
    public const int StageLayer = 1;

    /// <summary>
    /// Layer-2 summary with the widest range wins; stage summaries it already covers are dropped, so
    /// folding is idempotent without rewriting or deleting prior rows.
    /// </summary>
    public static ContextView Build(IReadOnlyList<SummaryRecord> summaries, long lastSeq)
    {
        ArgumentNullException.ThrowIfNull(summaries);
        SummaryRecord? session = null;
        foreach (var summary in summaries)
        {
            if (summary.Layer != SessionLayer)
            {
                continue;
            }

            if (session is null ||
                summary.ToSeq > session.ToSeq ||
                (summary.ToSeq == session.ToSeq && summary.At >= session.At))
            {
                session = summary;
            }
        }

        var covered = session?.ToSeq ?? 0;
        var stages = new List<SummaryRecord>(summaries.Count);
        foreach (var summary in summaries)
        {
            if (summary.Layer != StageLayer || summary.ToSeq <= covered || summary.ToSeq > lastSeq)
            {
                continue;
            }

            stages.Add(summary);
        }

        stages.Sort(static (left, right) =>
        {
            var byRange = left.FromSeq.CompareTo(right.FromSeq);
            return byRange != 0 ? byRange : left.ToSeq.CompareTo(right.ToSeq);
        });

        var lastSummarized = covered;
        foreach (var stage in stages)
        {
            lastSummarized = Math.Max(lastSummarized, stage.ToSeq);
        }

        return new ContextView(session, stages, Math.Clamp(lastSummarized, 0L, Math.Max(lastSeq, 0L)));
    }

    /// <summary>
    /// Chooses the cut of the raw tail <paramref name="raw"/> (ascending, all unsummarized): the longest suffix that
    /// starts at a valid cut point and costs no more than max(<paramref name="keepRecentTokens"/>,
    /// <paramref name="rawBudget"/>) stays raw, the rest is summarized. A valid cut point is a user or assistant message
    /// other than the first one, never a tool result, so a tool call and its results are never separated and the raw
    /// tail never starts with an orphan result. When no suffix fits, the cut keeps only the newest valid suffix. Null
    /// when the whole tail already fits or there is no valid cut point at all. A cut at an assistant message after a
    /// user message in <paramref name="raw"/> is a split turn (<see cref="CompactionCut.TurnStart"/>).
    /// </summary>
    public static CompactionCut? PlanCut(
        IReadOnlyList<StoredMessage> raw,
        int rawBudget,
        int keepRecentTokens,
        Func<ChatMessage, int> countTokens)
    {
        ArgumentNullException.ThrowIfNull(raw);
        ArgumentNullException.ThrowIfNull(countTokens);
        ArgumentOutOfRangeException.ThrowIfNegative(keepRecentTokens);
        if (raw.Count < 2)
        {
            return null;
        }

        var goal = (long)Math.Max(keepRecentTokens, rawBudget);
        var suffix = new long[raw.Count + 1];
        for (var index = raw.Count - 1; index >= 0; index--)
        {
            suffix[index] = suffix[index + 1] + Math.Max(0, countTokens(raw[index].Message));
        }

        if (suffix[0] <= goal)
        {
            return null;
        }

        var keepFrom = -1;
        var newestValid = -1;
        for (var index = 1; index < raw.Count; index++)
        {
            if (!IsCutPoint(raw[index].Message))
            {
                continue;
            }

            newestValid = index;
            if (keepFrom < 0 && suffix[index] <= goal)
            {
                keepFrom = index;
            }
        }

        if (keepFrom < 0)
        {
            keepFrom = newestValid;
        }

        if (keepFrom < 0)
        {
            return null;
        }

        var turnStart = -1;
        if (raw[keepFrom].Message.Role == MessageRole.Assistant)
        {
            for (var index = keepFrom - 1; index >= 0; index--)
            {
                if (raw[index].Message.Role == MessageRole.User)
                {
                    turnStart = index;
                    break;
                }
            }
        }

        return new CompactionCut(keepFrom, turnStart);
    }

    private static bool IsCutPoint(ChatMessage message) =>
        message.Role is MessageRole.User or MessageRole.Assistant;
}
