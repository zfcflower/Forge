using System.Text;
using Forge.Abstractions;

namespace Forge.App.ViewModels;

/// <summary>Rebuilds a transcript from the stored messages of one session; the read is off the UI thread, the mapping on it.</summary>
internal static class TranscriptReplay
{
    public static Task<List<StoredMessage>> ReadAsync(ISessionStore store, string sessionId, CancellationToken ct) =>
        Task.Run(
            async () =>
            {
                var messages = new List<StoredMessage>();
                await foreach (var stored in store.ReadAsync(sessionId, 1, null, ct).ConfigureAwait(false))
                {
                    messages.Add(stored);
                }

                return messages;
            },
            ct);

    /// <summary>Replays <paramref name="messages"/> into <paramref name="transcript"/> and returns the usage they carried.</summary>
    public static TokenUsage Apply(TranscriptViewModel transcript, IReadOnlyList<StoredMessage> messages)
    {
        var inputTokens = 0;
        var outputTokens = 0;
        foreach (var stored in messages)
        {
            Apply(transcript, stored.Message, stored.At);
            if (stored.Usage is { } usage && (usage.InputTokens > 0 || usage.OutputTokens > 0))
            {
                transcript.Current?.AddUsage(usage, StopReason.EndTurn);
                inputTokens += usage.InputTokens;
                outputTokens += usage.OutputTokens;
            }
        }

        transcript.EndTurn(null, null);
        return new TokenUsage(inputTokens, outputTokens);
    }

    private static void Apply(TranscriptViewModel transcript, ChatMessage message, DateTimeOffset at)
    {
        switch (message.Role)
        {
            case MessageRole.User:
                var text = TextOf(message);
                if (text.Length > 0)
                {
                    transcript.AddUserMessage(text, at);
                    transcript.BeginTurn(at, closePrevious: true);
                }

                break;
            case MessageRole.Assistant:
                var card = transcript.Current ?? transcript.BeginTurn(at);
                var content = TextOf(message);
                if (content.Length > 0)
                {
                    card.AppendText(content);
                }

                foreach (var block in message.Content)
                {
                    if (block is ToolCallBlock call)
                    {
                        card.AddTool(new ToolCall(call.Id, call.Name, call.ArgumentsJson));
                    }
                }

                break;
            case MessageRole.Tool:
                var target = transcript.Current;
                if (target is null)
                {
                    break;
                }

                foreach (var block in message.Content)
                {
                    if (block is ToolResultBlock result)
                    {
                        target.CompleteTool(
                            new ToolCall(result.CallId, string.Empty, string.Empty),
                            new ToolResult(result.Content, result.IsError));
                    }
                }

                target.Finish(null, null);
                break;
            default:
                break;
        }
    }

    private static string TextOf(ChatMessage message)
    {
        var builder = new StringBuilder();
        foreach (var block in message.Content)
        {
            if (block is TextBlock text)
            {
                builder.Append(text.Text);
            }
        }

        return builder.ToString();
    }
}
