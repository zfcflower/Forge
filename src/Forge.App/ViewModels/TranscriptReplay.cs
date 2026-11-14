using System.Text;
using Forge.Abstractions;
using Forge.Core.Storage;

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
        var cacheReadTokens = 0;
        var cacheWriteTokens = 0;
        foreach (var stored in messages)
        {
            Apply(transcript, stored.Message, stored.At);
            if (stored.Usage is { } usage && (usage.InputTokens > 0 || usage.OutputTokens > 0))
            {
                transcript.Current?.AddUsage(usage, StopReason.EndTurn);
                inputTokens += usage.InputTokens;
                outputTokens += usage.OutputTokens;

                // Cache tokens are part of the stored usage and drive both the cache-hit ratio and the cost estimate, so a
                // reopened session has to replay them too; dropping them would report a 0% hit rate on a real number.
                cacheReadTokens += usage.CacheReadTokens;
                cacheWriteTokens += usage.CacheWriteTokens;
            }
        }

        transcript.EndTurn(null, null);
        return new TokenUsage(inputTokens, outputTokens, cacheReadTokens, cacheWriteTokens);
    }

    private static void Apply(TranscriptViewModel transcript, ChatMessage message, DateTimeOffset at)
    {
        switch (message.Role)
        {
            case MessageRole.User:
                var text = TextOf(message, skipAttachments: true);
                var attachments = AttachmentsOf(message);
                if (text.Length > 0 || attachments.Count > 0)
                {
                    transcript.AddUserMessage(text, at, attachments);
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
                            new ToolResult(result.Blocks, result.IsError));
                    }
                }

                target.Finish(null, null);
                break;
            default:
                break;
        }
    }

    /// <summary>The attachments of a user message: images, files, and text files inlined in the attachment shape.</summary>
    private static List<ContentBlock> AttachmentsOf(ChatMessage message)
    {
        List<ContentBlock>? attachments = null;
        foreach (var block in message.Content)
        {
            if (block is ImageBlock or FileBlock || TextAttachment.IsAttachment(block))
            {
                (attachments ??= []).Add(block);
            }
        }

        return attachments ?? [];
    }

    private static string TextOf(ChatMessage message, bool skipAttachments = false)
    {
        var builder = new StringBuilder();
        foreach (var block in message.Content)
        {
            if (block is TextBlock text && !(skipAttachments && TextAttachment.IsAttachment(block)))
            {
                builder.Append(text.Text);
            }
        }

        return builder.ToString();
    }
}
