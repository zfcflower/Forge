using System.Text;
using Forge.Abstractions;
using Forge.Core.Storage;

namespace Forge.Core.Agents;

public sealed record CompactionResult(int TokensBefore, int TokensAfter);

/// <summary>Rewrites an instance's in-memory context to fit a token budget without losing stored data.</summary>
public interface IContextCompactor
{
    /// <summary>Compacts one instance; null means nothing was worth compacting.</summary>
    Task<CompactionResult?> CompactAsync(AgentInstance instance, int thresholdTokens, CancellationToken ct);
}

public sealed class NoopCompactor : IContextCompactor
{
    public static NoopCompactor Instance { get; } = new();

    public Task<CompactionResult?> CompactAsync(AgentInstance instance, int thresholdTokens, CancellationToken ct) =>
        Task.FromResult<CompactionResult?>(null);
}

/// <summary>
/// Layered compaction over <see cref="SummaryLayers"/>: the oldest still-raw messages become a new
/// stage summary, stage summaries beyond the cap fold into a new session summary, and the in-memory
/// context is rebuilt as [summary view] + the raw messages that were not summarized. Stored summaries
/// are only ever appended, and summarized originals stay in the session store for retrieval.
/// </summary>
public sealed class SummaryCompactor : IContextCompactor
{
    public const string SummarizerPrompt =
        """
        You compress a coding agent's conversation so it can continue without the dropped messages.

        Keep: the task and its constraints, decisions taken and why, files and symbols touched, facts established by tool output, open threads and the next step.
        Drop: narration of exploration, repeated or superseded tool output, and anything the repository already records.

        Answer with the summary alone: under 300 words, plain prose, no headings, no preamble.
        """;

    private const int MaxMessageCharsInRequest = 6_000;
    private const int MaxHistoryViewChars = 400_000;

    private readonly ISessionStore store;
    private readonly Func<ModelSettings, IChatProvider> providers;
    private readonly ITokenCounter tokens;
    private readonly double targetRatio;
    private readonly int maxStageSummaries;

    public SummaryCompactor(
        ISessionStore store,
        Func<ModelSettings, IChatProvider> providers,
        ITokenCounter tokens,
        double targetRatio = 0.5,
        int maxStageSummaries = 8)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(targetRatio, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(targetRatio, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxStageSummaries, 1);
        this.store = store;
        this.providers = providers;
        this.tokens = tokens;
        this.targetRatio = targetRatio;
        this.maxStageSummaries = maxStageSummaries;
    }

    public async Task<CompactionResult?> CompactAsync(AgentInstance instance, int thresholdTokens, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentOutOfRangeException.ThrowIfNegative(thresholdTokens);
        if (instance.Messages.Count == 0)
        {
            return null;
        }

        var before = tokens.Count(instance.History);
        var lastSeq = await store.LastSeqAsync(instance.SessionId, ct).ConfigureAwait(false);
        if (lastSeq == 0)
        {
            return null;
        }

        var summaries = await store.GetSummariesAsync(instance.SessionId, ct).ConfigureAwait(false);
        var view = SummaryLayers.Build(summaries, lastSeq);
        var raw = await ReadRawAsync(instance.SessionId, view.RawFromSeq, lastSeq, ct).ConfigureAwait(false);
        if (raw.Count < 2)
        {
            return null;
        }

        // Full inheritance keeps the inherited prefix whole (it is what makes the parent's prompt cache reusable), so only
        // this session's own messages are summarized and they get the budget the inherited part leaves; when the inherited
        // part alone fills it, compaction still bounds the own part down to its last interaction.
        var budget = Math.Max(1, (int)(thresholdTokens * targetRatio) - InheritedTokens(instance));
        var plan = SummaryLayers.Plan(summaries, raw, lastSeq, budget, tokens.Count, maxStageSummaries);
        if (plan.IsIdle)
        {
            return null;
        }

        var wrote = false;
        if (plan.Promote is { } promote)
        {
            var range = ExtendOverToolResults(raw, promote);
            var slice = SummaryLayers.Slice(raw, range);
            var text = await SummarizeAsync(instance, Render(slice), ct).ConfigureAwait(false);
            if (text.Length > 0)
            {
                await store.PutSummaryAsync(
                    new SummaryRecord(instance.SessionId, SummaryLayers.StageLayer, range.FromSeq, range.ToSeq, text, DateTimeOffset.UtcNow),
                    ct).ConfigureAwait(false);
                wrote = true;
            }
        }

        if (plan.FoldRange is { } foldRange && plan.Fold.Count > 0)
        {
            var folded = await SummarizeAsync(instance, RenderFold(view, plan.Fold), ct).ConfigureAwait(false);
            if (folded.Length > 0)
            {
                await store.PutSummaryAsync(
                    new SummaryRecord(instance.SessionId, SummaryLayers.SessionLayer, foldRange.FromSeq, foldRange.ToSeq, folded, DateTimeOffset.UtcNow),
                    ct).ConfigureAwait(false);
                wrote = true;
            }
        }

        if (!wrote)
        {
            return null;
        }

        var updated = SummaryLayers.Build(await store.GetSummariesAsync(instance.SessionId, ct).ConfigureAwait(false), lastSeq);
        instance.ReplaceHistory(Rebuild(instance, updated));
        return new CompactionResult(before, tokens.Count(instance.History));
    }

    private async Task<List<StoredMessage>> ReadRawAsync(string sessionId, long fromSeq, long lastSeq, CancellationToken ct)
    {
        var raw = new List<StoredMessage>();
        if (fromSeq > lastSeq)
        {
            return raw;
        }

        await foreach (var message in store.ReadAsync(sessionId, fromSeq, lastSeq, ct).ConfigureAwait(false))
        {
            raw.Add(message);
        }

        return raw;
    }

    private static SummaryRange ExtendOverToolResults(List<StoredMessage> raw, SummaryRange range)
    {
        var index = raw.FindLastIndex(message => message.Seq == range.ToSeq);
        if (index < 0)
        {
            return range;
        }

        var end = index;
        while (end + 1 < raw.Count && raw[end + 1].Message.Role == MessageRole.Tool)
        {
            end++;
        }

        return end == index ? range : new SummaryRange(range.FromSeq, raw[end].Seq);
    }

    private int InheritedTokens(AgentInstance instance)
    {
        List<ChatMessage>? inherited = null;
        foreach (var entry in instance.Messages)
        {
            if (entry.Inherited)
            {
                (inherited ??= []).Add(entry.Message);
            }
        }

        return inherited is null ? 0 : tokens.Count(inherited);
    }

    /// <summary>[inherited prefix, unchanged] + [own summary view] + [own messages after the last summarized sequence].</summary>
    private static List<AgentMessage> Rebuild(AgentInstance instance, ContextView view)
    {
        var inherited = new List<AgentMessage>();
        var recent = new List<AgentMessage>();
        foreach (var entry in instance.Messages)
        {
            if (entry.Inherited)
            {
                inherited.Add(entry);
            }
            else if (entry.Seq > view.LastSummarizedSeq)
            {
                recent.Add(entry);
            }
        }

        while (recent.Count > 0 && recent[0].Message.Role == MessageRole.Tool)
        {
            recent.RemoveAt(0);
        }

        var rebuilt = new List<AgentMessage>(inherited.Count + recent.Count + 1);
        rebuilt.AddRange(inherited);
        var summaryText = SessionHistory.RenderSummaries(view);
        if (summaryText.Length > 0)
        {
            rebuilt.Add(new AgentMessage(ChatMessage.User(summaryText), 0));
        }

        rebuilt.AddRange(recent);
        return rebuilt;
    }

    private static void AppendSection(StringBuilder builder, string heading, string text)
    {
        if (builder.Length > 0)
        {
            builder.Append("\n\n");
        }

        builder.Append("## ").Append(heading).Append("\n\n").Append(text);
    }

    private static string Render(IEnumerable<StoredMessage> messages)
    {
        var builder = new StringBuilder(4096);
        foreach (var message in messages)
        {
            if (builder.Length >= MaxHistoryViewChars)
            {
                builder.Append("\n\n[remaining messages omitted for length]");
                break;
            }

            var text = MessageJson.ExtractFullText(message.Message);
            if (text.Length > MaxMessageCharsInRequest)
            {
                text = string.Concat(text.AsSpan(0, MaxMessageCharsInRequest), "…");
            }

            builder.Append('#').Append(message.Seq).Append(' ').Append(MessageJson.RoleName(message.Message.Role)).Append(": ").Append(text).Append('\n');
        }

        return builder.ToString();
    }

    private static string RenderFold(ContextView view, IReadOnlyList<SummaryRecord> folded)
    {
        var builder = new StringBuilder(2048);
        if (view.SessionSummary is { } session)
        {
            AppendSection(builder, "Current session summary", session.Text);
        }

        foreach (var record in folded)
        {
            AppendSection(builder, $"Stage summary #{record.FromSeq}-#{record.ToSeq}", record.Text);
        }

        return builder.ToString();
    }

    private async Task<string> SummarizeAsync(AgentInstance instance, string transcript, CancellationToken ct)
    {
        if (transcript.Length == 0)
        {
            return string.Empty;
        }

        var provider = providers(instance.Definition.Model);
        var request = new ChatRequest(
            SummarizerPrompt,
            [],
            [ChatMessage.User(transcript)],
            instance.Definition.Model with { Thinking = ThinkingEffort.Off });
        var builder = new StringBuilder(1024);
        await foreach (var chunk in provider.StreamAsync(request, ct).ConfigureAwait(false))
        {
            switch (chunk)
            {
                case TextDelta text:
                    builder.Append(text.Text);
                    break;
                case MessageCompleted completed:
                    if (builder.Length == 0)
                    {
                        foreach (var block in completed.Message.Content)
                        {
                            if (block is TextBlock text)
                            {
                                builder.Append(text.Text);
                            }
                        }
                    }

                    break;
            }
        }

        return builder.ToString().Trim();
    }
}
