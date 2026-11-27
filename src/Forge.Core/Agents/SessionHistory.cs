using System.Text;
using Forge.Abstractions;
using Forge.Core.Storage;

namespace Forge.Core.Agents;

/// <summary>
/// Rebuilds an instance's context from the session store exactly as the compactor leaves it: the summary view
/// of the compacted past plus the raw messages after the last summarized sequence, never starting with a tool
/// result whose call was summarized away.
/// </summary>
internal static class SessionHistory
{
    internal static async Task<IReadOnlyList<AgentMessage>> LoadAsync(
        ISessionStore store,
        string sessionId,
        CancellationToken ct,
        ITurnLedgerStore? ledger = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        // The managed context first: a session the ledger has written for reads exactly what a live one reads - the fold
        // block plus one entry per finished turn - so reopening a tab and continuing in the same process see one history.
        // A session with no rows (pre-ledger, or a host without a ledger) keeps the summary view and the raw tail below.
        var result = new List<AgentMessage>();
        if (ledger is not null)
        {
            var rows = await ledger.ListAsync(sessionId, ct).ConfigureAwait(false);
            if (rows.Count > 0)
            {
                foreach (var message in ManagedContext.Build(rows))
                {
                    result.Add(new AgentMessage(message, 0));
                }

                // Everything stored after the last settled turn: receipts and interjections that arrived while the session
                // was idle. They belong to no entry yet, and without them a reopened tab would silently lose them.
                await foreach (var stored in store.ReadAsync(sessionId, ManagedContext.LastSeq(rows) + 1, null, ct).ConfigureAwait(false))
                {
                    if (stored.Message.Role == MessageRole.User)
                    {
                        result.Add(new AgentMessage(stored.Message, stored.Seq));
                    }
                }

                return result;
            }
        }

        await AppendLevelAsync(store, sessionId, result, ct).ConfigureAwait(false);
        return result;
    }

    /// <summary>
    /// One session's own context: the rendered summary view followed by the raw tail, without a leading orphaned tool
    /// result or a trailing unanswered tool call.
    /// </summary>
    private static async Task AppendLevelAsync(
        ISessionStore store,
        string sessionId,
        List<AgentMessage> result,
        CancellationToken ct)
    {
        var lastSeq = await store.LastSeqAsync(sessionId, ct).ConfigureAwait(false);
        if (lastSeq <= 0)
        {
            return;
        }

        var summaries = await store.GetSummariesAsync(sessionId, ct).ConfigureAwait(false);
        var view = SummaryLayers.Build(summaries, lastSeq);
        var recent = new List<AgentMessage>();
        await foreach (var message in store.ReadAsync(sessionId, view.RawFromSeq, lastSeq, ct).ConfigureAwait(false))
        {
            recent.Add(new AgentMessage(message.Message, message.Seq));
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
            result.Add(new AgentMessage(ChatMessage.User(summaryText), 0));
        }

        result.AddRange(recent);
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
