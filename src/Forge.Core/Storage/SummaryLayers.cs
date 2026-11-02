using Forge.Abstractions;

namespace Forge.Core.Storage;

/// <summary>Inclusive sequence range handled by one summary.</summary>
public sealed record SummaryRange(long FromSeq, long ToSeq);

/// <summary>
/// The context a model sees for a compacted session: an optional layer-2 session summary, the live
/// layer-1 stage summaries, and the raw messages that start after the last summarized sequence.
/// </summary>
public sealed record ContextView(
    SummaryRecord? SessionSummary,
    IReadOnlyList<SummaryRecord> StageSummaries,
    long LastSummarizedSeq)
{
    public long RawFromSeq => LastSummarizedSeq + 1;
}

/// <summary>
/// Work required to stay inside a token budget. <see cref="Promote"/> is the oldest still-raw range
/// that must become a new layer-1 summary; <see cref="Fold"/> holds the oldest layer-1 records that
/// must be rolled into a new layer-2 record covering <see cref="FoldRange"/>. Existing records are
/// never rewritten: a later record with a wider range supersedes them.
/// </summary>
public sealed record CompactionPlan(
    SummaryRange? Promote,
    IReadOnlyList<SummaryRecord> Fold,
    SummaryRange? FoldRange)
{
    public static CompactionPlan None { get; } = new(null, [], null);

    public bool IsIdle => Promote is null && Fold.Count == 0;

    /// <summary>
    /// True when the context is over budget and this plan cannot reduce it any further: the caller has
    /// to accept the overshoot instead of planning another round.
    /// </summary>
    public bool CannotSatisfy { get; init; }
}

/// <summary>Pure planning helpers over stored <see cref="SummaryRecord"/> layers.</summary>
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
    /// Decides what to compact next. <paramref name="messages"/> is the raw tail (ascending); only
    /// messages after the last summarized sequence are considered. <paramref name="countTokens"/> is
    /// applied to the message text and to summary text. A promoted range never splits a tool
    /// interaction: an assistant message with tool calls and the tool results answering it are either
    /// summarized together or kept raw together, so the raw tail never starts with an orphan result.
    /// Stage summaries are folded when they outnumber <paramref name="maxStageSummaries"/> or when
    /// summary text alone exceeds <paramref name="tokenBudget"/>.
    /// </summary>
    public static CompactionPlan Plan(
        IReadOnlyList<SummaryRecord> summaries,
        IReadOnlyList<StoredMessage> messages,
        long lastSeq,
        int tokenBudget,
        Func<string, int> countTokens,
        int maxStageSummaries = 8)
    {
        ArgumentNullException.ThrowIfNull(summaries);
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(countTokens);
        ArgumentOutOfRangeException.ThrowIfNegative(tokenBudget);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxStageSummaries, 1);

        var view = Build(summaries, lastSeq);
        var summaryTokens = 0;
        if (view.SessionSummary is not null)
        {
            summaryTokens += Count(countTokens, view.SessionSummary.Text);
        }

        foreach (var stage in view.StageSummaries)
        {
            summaryTokens += Count(countTokens, stage.Text);
        }

        var raw = new List<StoredMessage>(messages.Count);
        var rawTokens = 0;
        foreach (var message in messages)
        {
            if (message.Seq <= view.LastSummarizedSeq || message.Seq > lastSeq)
            {
                continue;
            }

            raw.Add(message);
            rawTokens += Count(countTokens, MessageJson.ExtractFullText(message.Message));
        }

        raw.Sort(static (left, right) => left.Seq.CompareTo(right.Seq));

        SummaryRange? promote = null;
        var excess = summaryTokens + rawTokens - tokenBudget;
        if (excess > 0 && raw.Count > 1)
        {
            var accumulated = 0;
            var end = 0;
            while (end < raw.Count - 1 && accumulated < excess)
            {
                accumulated += Count(countTokens, MessageJson.ExtractFullText(raw[end].Message));
                end++;
            }

            while (end < raw.Count - 1 && !IsInteractionBoundary(raw, end))
            {
                end++;
            }

            if (IsInteractionBoundary(raw, end))
            {
                promote = new SummaryRange(raw[0].Seq, raw[end - 1].Seq);
            }
        }

        var fold = new List<SummaryRecord>();
        SummaryRange? foldRange = null;
        var stages = view.StageSummaries;
        var foldCount = 0;
        if (stages.Count > maxStageSummaries)
        {
            foldCount = stages.Count - maxStageSummaries;
        }
        else if (summaryTokens > tokenBudget && stages.Count > 0)
        {
            foldCount = 1;
        }

        if (foldCount > 0)
        {
            for (var index = 0; index < foldCount; index++)
            {
                fold.Add(stages[index]);
            }

            foldRange = new SummaryRange(view.SessionSummary?.FromSeq ?? fold[0].FromSeq, fold[^1].ToSeq);
        }

        if (promote is null && fold.Count == 0)
        {
            return excess > 0 ? CompactionPlan.None with { CannotSatisfy = true } : CompactionPlan.None;
        }

        return new CompactionPlan(promote, fold, foldRange);
    }

    /// <summary>Text a new layer-1 record built from <paramref name="promote"/> would summarize.</summary>
    public static IReadOnlyList<StoredMessage> Slice(IReadOnlyList<StoredMessage> messages, SummaryRange range)
    {
        ArgumentNullException.ThrowIfNull(messages);
        var slice = new List<StoredMessage>((int)Math.Min(range.ToSeq - range.FromSeq + 1, int.MaxValue));
        foreach (var message in messages)
        {
            if (message.Seq >= range.FromSeq && message.Seq <= range.ToSeq)
            {
                slice.Add(message);
            }
        }

        return slice;
    }

    private static int Count(Func<string, int> countTokens, string text) =>
        text.Length == 0 ? 0 : Math.Max(0, countTokens(text));

    private static bool IsInteractionBoundary(IReadOnlyList<StoredMessage> messages, int index) =>
        messages[index].Message.Role != MessageRole.Tool;
}
