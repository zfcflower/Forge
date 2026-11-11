using System.Text;
using Forge.Abstractions;
using Forge.Core.Storage;

namespace Forge.Core.Agents;

/// <summary>
/// Rebuilds an instance's context from the session store exactly as the compactor leaves it: the summary view
/// of the compacted past plus the raw messages after the last summarized sequence, never starting with a tool
/// result whose call was summarized away.
/// A session that inherits (full inheritance, <see cref="SessionInfo.InheritsFromSessionId"/>) starts with its parent's
/// context cut at <see cref="SessionInfo.InheritsUpToSeq"/>, built by the same function, recursively for every level; the
/// parent's messages are referenced, never copied, and come back flagged <see cref="AgentMessage.Inherited"/>.
/// </summary>
internal static class SessionHistory
{
    /// <summary>Longest inheritance chain followed before the lineage is treated as corrupt.</summary>
    internal const int MaxInheritanceDepth = 64;

    internal static async Task<IReadOnlyList<AgentMessage>> LoadAsync(
        ISessionStore store,
        string sessionId,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        var result = new List<AgentMessage>();
        var info = await store.GetSessionAsync(sessionId, ct).ConfigureAwait(false);
        if (info is not null)
        {
            await AppendInheritedAsync(store, info, result, [sessionId], ct).ConfigureAwait(false);
        }

        await AppendLevelAsync(store, sessionId, upToSeq: null, notAfter: null, inherited: false, result, ct).ConfigureAwait(false);
        return result;
    }

    /// <summary>The ancestors' part of <paramref name="child"/>'s context, oldest level first.</summary>
    private static async Task AppendInheritedAsync(
        ISessionStore store,
        SessionInfo child,
        List<AgentMessage> result,
        HashSet<string> visited,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(child.InheritsFromSessionId) || child.InheritsUpToSeq is not { } upToSeq)
        {
            return;
        }

        var parentId = child.InheritsFromSessionId;
        if (!visited.Add(parentId) || visited.Count > MaxInheritanceDepth)
        {
            throw new InvalidOperationException(
                $"Session '{child.Id}' has a cyclic or too deep inheritance chain (at '{parentId}').");
        }

        if (await store.GetSessionAsync(parentId, ct).ConfigureAwait(false) is { } parent)
        {
            await AppendInheritedAsync(store, parent, result, visited, ct).ConfigureAwait(false);
        }

        // Summaries the parent writes after the child was created (it was resumed and compacted later) must not change
        // what the child inherited, so the parent's level is rebuilt from the summaries that existed at that moment.
        await AppendLevelAsync(store, parentId, upToSeq, child.CreatedAt, inherited: true, result, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// One session's own context, optionally cut at <paramref name="upToSeq"/>: the rendered summary view followed by
    /// the raw tail, without a leading orphaned tool result or a trailing unanswered tool call.
    /// </summary>
    private static async Task AppendLevelAsync(
        ISessionStore store,
        string sessionId,
        long? upToSeq,
        DateTimeOffset? notAfter,
        bool inherited,
        List<AgentMessage> result,
        CancellationToken ct)
    {
        var lastSeq = await store.LastSeqAsync(sessionId, ct).ConfigureAwait(false);
        if (upToSeq is { } cut)
        {
            lastSeq = Math.Min(lastSeq, cut);
        }

        if (lastSeq <= 0)
        {
            return;
        }

        var summaries = await store.GetSummariesAsync(sessionId, ct).ConfigureAwait(false);
        if (upToSeq is not null || notAfter is not null)
        {
            summaries = Visible(summaries, lastSeq, notAfter);
        }

        var view = SummaryLayers.Build(summaries, lastSeq);
        var recent = new List<AgentMessage>();
        await foreach (var message in store.ReadAsync(sessionId, view.RawFromSeq, lastSeq, ct).ConfigureAwait(false))
        {
            recent.Add(new AgentMessage(message.Message, message.Seq, inherited));
        }

        while (recent.Count > 0 && recent[0].Message.Role == MessageRole.Tool)
        {
            recent.RemoveAt(0);
        }

        while (recent.Count > 0 && IsUnansweredToolTurn(recent[^1].Message))
        {
            recent.RemoveAt(recent.Count - 1);
        }

        var summaryText = RenderSummaries(view);
        if (summaryText.Length > 0)
        {
            result.Add(new AgentMessage(ChatMessage.User(summaryText), 0, inherited));
        }

        result.AddRange(recent);
    }

    /// <summary>Summaries that lie entirely within the cut and existed no later than <paramref name="notAfter"/>.</summary>
    private static List<SummaryRecord> Visible(IReadOnlyList<SummaryRecord> summaries, long lastSeq, DateTimeOffset? notAfter)
    {
        var visible = new List<SummaryRecord>(summaries.Count);
        foreach (var summary in summaries)
        {
            if (summary.ToSeq <= lastSeq && (notAfter is not { } limit || summary.At <= limit))
            {
                visible.Add(summary);
            }
        }

        return visible;
    }

    /// <summary>The compacted past as one user message, worded like the messages the compactor leaves in context.</summary>
    internal static string RenderSummaries(ContextView view)
    {
        ArgumentNullException.ThrowIfNull(view);
        var builder = new StringBuilder(2048);
        if (view.SessionSummary is { } session)
        {
            AppendSection(builder, $"Session summary (messages #{session.FromSeq}-#{session.ToSeq})", session.Text);
        }

        foreach (var stage in view.StageSummaries)
        {
            AppendSection(builder, $"Earlier conversation (messages #{stage.FromSeq}-#{stage.ToSeq})", stage.Text);
        }

        if (builder.Length == 0)
        {
            return string.Empty;
        }

        builder.Append("\n\nEarlier messages are still in the session store: use search_history, read_history or read_artifact to retrieve them.");
        return builder.ToString();
    }

    /// <summary>An assistant turn whose tool results were never persisted, left behind by a crash mid-turn.</summary>
    private static bool IsUnansweredToolTurn(ChatMessage message)
    {
        if (message.Role != MessageRole.Assistant)
        {
            return false;
        }

        foreach (var block in message.Content)
        {
            if (block is ToolCallBlock)
            {
                return true;
            }
        }

        return false;
    }

    private static void AppendSection(StringBuilder builder, string heading, string text)
    {
        if (builder.Length > 0)
        {
            builder.Append("\n\n");
        }

        builder.Append("## ").Append(heading).Append("\n\n").Append(text);
    }
}
