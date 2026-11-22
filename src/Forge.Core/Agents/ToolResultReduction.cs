using System.Text.Json;
using Forge.Abstractions;

namespace Forge.Core.Agents;

/// <summary>
/// What one reduction pass returned: the projected history (null when nothing was worth cropping), how many tokens it
/// actually released, and where those tokens came from — by the tool that produced them.
/// </summary>
internal sealed class Reduction
{
    public List<AgentMessage>? Messages { get; set; }

    public int ReleasedTokens { get; set; }

    public int DroppedResults { get; set; }

    public int ReadTokens { get; set; }

    public int BashTokens { get; set; }

    public int ImageTokens { get; set; }

    public int OtherTokens { get; set; }
}

/// <summary>
/// The zero-cost rung of context compaction: old tool results become receipts, so the summarizer is only asked when the
/// receipts are not enough. Pure over the messages it is given — no store, no model call — and idempotent, because a
/// rendered receipt is always shorter than <see cref="ContextReceipt.MinCroppableChars"/> and so never a candidate again.
/// The inherited prefix is untouchable (it is what makes a fork's prompt cache reusable), and the newest
/// <paramref name="keepRecentTokens"/> stay verbatim whatever happens. The most it can release is everything outside
/// that window: when the inherited prefix and the summary themselves exceed the line, the caller falls through to the
/// summarizer and the existing fruitless/Stalled bookkeeping.
/// </summary>
internal static class ToolResultReduction
{
    internal static Reduction? Apply(
        IReadOnlyList<AgentMessage> messages,
        int keepRecentTokens,
        int releaseTokens,
        ITokenCounter counter)
    {
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(counter);
        ArgumentOutOfRangeException.ThrowIfNegative(keepRecentTokens);
        if (releaseTokens <= 0)
        {
            return null;
        }

        var guard = GuardIndex(messages, keepRecentTokens, counter);
        if (guard <= 0)
        {
            return null;
        }

        // A receipt names the tool and its target, so the calls the results answer are collected up front.
        var calls = CallsById(messages, guard);
        var candidates = new List<Candidate>();
        for (var index = 0; index < guard; index++)
        {
            var entry = messages[index];
            if (entry.Inherited || entry.Message.Role != MessageRole.Tool)
            {
                continue;
            }

            foreach (var block in entry.Message.Content)
            {
                // A pure-image block has no text at all and is still worth cropping: images are a flat cost each.
                if (block is not ToolResultBlock { IsError: false } result)
                {
                    continue;
                }

                var text = TextOf(result);
                var images = result.Blocks.OfType<ImageBlock>().Count();
                if (text.Length <= ContextReceipt.MinCroppableChars && images == 0)
                {
                    continue;
                }

                var tokens = counter.Count(text) + (long)images * ApproximateTokenCounter.ImageTokens;
                candidates.Add(new Candidate(result, entry.Seq, (int)Math.Min(int.MaxValue, tokens)));
            }
        }

        if (candidates.Count == 0)
        {
            return null;
        }

        // Greedy: biggest release first, the oldest message breaking ties, until the requested tokens are covered.
        var chosen = new HashSet<ToolResultBlock>(ReferenceEqualityComparer.Instance);
        var covered = 0L;
        foreach (var candidate in candidates.OrderByDescending(static candidate => candidate.Tokens)
            .ThenBy(static candidate => candidate.Seq))
        {
            chosen.Add(candidate.Block);
            covered += candidate.Tokens;
            if (covered >= releaseTokens)
            {
                break;
            }
        }

        var counting = new Reduction();
        var projected = new List<AgentMessage>(messages.Count);
        for (var index = 0; index < messages.Count; index++)
        {
            var entry = messages[index];
            if (index >= guard || entry.Message.Role != MessageRole.Tool || !ContainsChosen(entry.Message, chosen))
            {
                projected.Add(entry);
                continue;
            }

            projected.Add(new AgentMessage(
                new ChatMessage(entry.Message.Role, Replaced(entry.Message, chosen, calls, entry.Seq, counter, counting)),
                entry.Seq,
                entry.Inherited));
        }

        var released = Count(counter, messages) - Count(counter, projected);
        if (released <= 0)
        {
            return null;
        }

        counting.Messages = projected;
        counting.ReleasedTokens = released;
        return counting;
    }

    /// <summary>Everything older than the window that holds the newest <paramref name="keepRecentTokens"/> worth of messages; 0 when that window holds the whole history.</summary>
    private static int GuardIndex(IReadOnlyList<AgentMessage> messages, int keepRecentTokens, ITokenCounter counter)
    {
        long accumulated = 0;
        var single = new ChatMessage[1];
        for (var index = messages.Count - 1; index >= 0; index--)
        {
            single[0] = messages[index].Message;
            accumulated += counter.Count(single);
            if (accumulated >= keepRecentTokens)
            {
                return index;
            }
        }

        return 0;
    }

    /// <summary>Tool calls by result id, from the assistant messages ahead of the guard.</summary>
    private static Dictionary<string, (string Name, string ArgumentsJson)> CallsById(
        IReadOnlyList<AgentMessage> messages,
        int guard)
    {
        var calls = new Dictionary<string, (string, string)>(StringComparer.Ordinal);
        for (var index = 0; index < guard; index++)
        {
            if (messages[index].Message.Role != MessageRole.Assistant)
            {
                continue;
            }

            foreach (var block in messages[index].Message.Content)
            {
                if (block is ToolCallBlock call)
                {
                    calls[call.Id] = (call.Name, call.ArgumentsJson);
                }
            }
        }

        return calls;
    }

    private static bool ContainsChosen(ChatMessage message, HashSet<ToolResultBlock> chosen) =>
        message.Content.Any(chosen.Contains);

    /// <summary>
    /// The message with every chosen result block rebuilt around its receipts; the counting fields grow alongside.
    /// The outer <see cref="ToolResultBlock"/> and its <see cref="ToolResultBlock.CallId"/> stay as they were — providers
    /// require every call to keep its result — and only the inner blocks are replaced.
    /// </summary>
    private static IReadOnlyList<ContentBlock> Replaced(
        ChatMessage message,
        HashSet<ToolResultBlock> chosen,
        Dictionary<string, (string Name, string ArgumentsJson)> calls,
        long seq,
        ITokenCounter counter,
        Reduction counting)
    {
        var blocks = new List<ContentBlock>(message.Content.Count);
        foreach (var block in message.Content)
        {
            if (block is not ToolResultBlock result || !chosen.Contains(result))
            {
                blocks.Add(block);
                continue;
            }

            blocks.Add(new ToolResultBlock(result.CallId, Inner(result, calls, seq, counter, counting), result.IsError));
        }

        return blocks;
    }

    /// <summary>The result's inner blocks with its text and images replaced by receipts; everything else stays.</summary>
    private static IReadOnlyList<ContentBlock> Inner(
        ToolResultBlock result,
        Dictionary<string, (string Name, string ArgumentsJson)> calls,
        long seq,
        ITokenCounter counter,
        Reduction counting)
    {
        var text = TextOf(result);
        var images = result.Blocks.OfType<ImageBlock>().Count();
        var inner = new List<ContentBlock>(result.Blocks.Count);
        if (text.Length > ContextReceipt.MinCroppableChars)
        {
            calls.TryGetValue(result.CallId, out var call);
            var name = call.Name ?? "tool";
            var parsed = ParseArguments(call.ArgumentsJson ?? "{}");
            var receipt = string.Equals(name, "read", StringComparison.Ordinal)
                ? ContextReceipt.Read(parsed.Path, parsed.Start, parsed.End, text.Length, seq)
                : ContextReceipt.Command(name, parsed.Command, text.Length, seq);
            inner.Add(new TextBlock(ContextReceipt.Cropped(text, receipt)));
            counting.DroppedResults++;
            AccumulateText(counting, name, counter.Count(text));
        }
        else
        {
            // A short text is kept verbatim: replacing it would cost more receipt than it saves.
            foreach (var block in result.Blocks)
            {
                if (block is TextBlock kept)
                {
                    inner.Add(kept);
                }
            }
        }

        for (var image = 0; image < images; image++)
        {
            inner.Add(new TextBlock(ContextReceipt.Image()));
            counting.DroppedResults++;
        }

        if (images > 0)
        {
            counting.ImageTokens += images * ApproximateTokenCounter.ImageTokens;
        }

        return inner;
    }

    private static void AccumulateText(Reduction counting, string name, int tokens)
    {
        if (string.Equals(name, "read", StringComparison.Ordinal))
        {
            counting.ReadTokens += tokens;
        }
        else if (string.Equals(name, "bash", StringComparison.Ordinal))
        {
            counting.BashTokens += tokens;
        }
        else
        {
            counting.OtherTokens += tokens;
        }
    }

    private static int Count(ITokenCounter counter, IReadOnlyList<AgentMessage> messages)
    {
        var list = new List<ChatMessage>(messages.Count);
        foreach (var entry in messages)
        {
            list.Add(entry.Message);
        }

        return counter.Count(list);
    }

    private static string TextOf(ToolResultBlock result) =>
        string.Concat(result.Blocks.OfType<TextBlock>().Select(static block => block.Text));

    /// <summary>The parts of a call's arguments a receipt shows: the path and line window of a read, the command line.</summary>
    private static (string Path, int Start, int End, string Command) ParseArguments(string argumentsJson)
    {
        try
        {
            using var document = JsonDocument.Parse(argumentsJson);
            var root = document.RootElement;
            var path = root.TryGetProperty("path", out var pathElement) && pathElement.ValueKind == JsonValueKind.String
                ? pathElement.GetString() ?? string.Empty
                : string.Empty;
            var start = 1;
            if (root.TryGetProperty("offset", out var offset) && offset.ValueKind == JsonValueKind.Number &&
                offset.TryGetInt32(out var first) && first > 1)
            {
                start = first;
            }

            var end = 0;
            if (root.TryGetProperty("limit", out var limit) && limit.ValueKind == JsonValueKind.Number &&
                limit.TryGetInt32(out var count) && count > 0)
            {
                end = (int)Math.Min(int.MaxValue, (long)start + count - 1);
            }

            var command = string.Empty;
            if (root.TryGetProperty("command", out var commandElement) && commandElement.ValueKind == JsonValueKind.String)
            {
                command = commandElement.GetString() ?? string.Empty;
            }

            return (path, start, end, command);
        }
        catch (JsonException)
        {
            return (string.Empty, 1, 0, string.Empty);
        }
    }

    private readonly record struct Candidate(ToolResultBlock Block, long Seq, int Tokens);
}
