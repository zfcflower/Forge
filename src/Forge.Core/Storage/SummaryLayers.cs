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
}
