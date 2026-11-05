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
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        var lastSeq = await store.LastSeqAsync(sessionId, ct).ConfigureAwait(false);
        if (lastSeq == 0)
        {
            return [];
        }

        var view = SummaryLayers.Build(await store.GetSummariesAsync(sessionId, ct).ConfigureAwait(false), lastSeq);
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
        if (summaryText.Length == 0)
        {
            return recent;
        }

        var rebuilt = new List<AgentMessage>(recent.Count + 1) { new(ChatMessage.User(summaryText), 0) };
        rebuilt.AddRange(recent);
        return rebuilt;
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
