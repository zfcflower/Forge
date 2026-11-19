using System.Text;
using Forge.Abstractions;
using Forge.App.Agents;
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
        var cacheWrite1hTokens = 0;
        foreach (var stored in messages)
        {
            Apply(transcript, stored.Message, stored.At);
            if (stored.Usage is { } usage && (usage.InputTokens > 0 || usage.OutputTokens > 0))
            {
                transcript.Current?.AddUsage(usage, StopReason.EndTurn);
                inputTokens += usage.InputTokens;
                outputTokens += usage.OutputTokens;

                // Cache tokens are part of the stored usage and drive both the cache-hit ratio and the cost estimate, so a
                // reopened session has to replay them too; dropping them would report a 0% hit rate on a real number. The
                // hour-lived cache write is its own column for the same reason: it is priced at twice the input rate and is
                // not part of InputTokens, so a sum that skipped it would under-report the session's spend.
                cacheReadTokens += usage.CacheReadTokens;
                cacheWriteTokens += usage.CacheWriteTokens;
                cacheWrite1hTokens += usage.CacheWrite1hTokens;
            }
        }

        transcript.EndTurn(null, null);
        return new TokenUsage(inputTokens, outputTokens, cacheReadTokens, cacheWriteTokens, cacheWrite1hTokens);
    }

    private static void Apply(TranscriptViewModel transcript, ChatMessage message, DateTimeOffset at)
    {
        switch (message.Role)
        {
            case MessageRole.User:
                var text = OrchestrationDirective.Strip(TextOf(message, skipAttachments: true));
                var attachments = AttachmentsOf(message);
                if (text.Length > 0 || attachments.Count > 0)
                {
                    transcript.AddUserMessage(text, at, attachments);
                    transcript.BeginTurn(at, closePrevious: true);
                }

                break;
            case MessageRole.Assistant:
                var card = transcript.Current ?? transcript.BeginTurn(at);

                // The content is replayed in its stored order: a provider records the reasoning of a request before the answer
                // of that request, and a reopened session must show the thinking rows the live one showed.
                foreach (var block in message.Content)
                {
                    switch (block)
                    {
                        case TextBlock piece when piece.Text.Length > 0:
                            card.AppendText(piece.Text);
                            break;
                        case ThinkingBlock thinking when thinking.Text.Length > 0:
                            card.AppendThinking(thinking.Text);
                            break;
                        case ToolCallBlock call:
                            card.AddTool(new ToolCall(call.Id, call.Name, call.ArgumentsJson));
                            break;
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
