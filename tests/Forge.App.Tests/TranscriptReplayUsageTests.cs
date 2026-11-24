using Forge.Abstractions;
using Forge.App.ViewModels;
using Xunit;

namespace Forge.App.Tests;

/// <summary>
/// Replaying a stored session sums the same five columns the live strip accumulates; the hour-lived cache write is the one
/// that used to be dropped, which under-reported a reopened session's spend (it is priced apart and is not part of the input).
/// </summary>
public sealed class TranscriptReplayUsageTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 18, 1, 0, 0, TimeSpan.Zero);

    private static List<StoredMessage> Session(params TokenUsage[] usages)
    {
        var messages = new List<StoredMessage> { new(1, ChatMessage.User("hi"), null, At) };
        long seq = 2;
        foreach (var usage in usages)
        {
            messages.Add(new StoredMessage(seq++, ChatMessage.Assistant("hello"), usage, At));
        }

        messages.Add(new StoredMessage(seq, ChatMessage.User("again"), null, At));
        return messages;
    }

    [Fact]
    public void The_hour_lived_cache_write_is_summed_with_the_other_four_columns()
    {
        var total = TranscriptReplay.Apply(
            new TranscriptViewModel(collapseOlderTurns: false),
            Session(new TokenUsage(1000, 200, 5000, 142, 42), new TokenUsage(500, 100, 0, 0, 8)),
            []);

        Assert.Equal(1500, total.InputTokens);
        Assert.Equal(300, total.OutputTokens);
        Assert.Equal(5000, total.CacheReadTokens);
        Assert.Equal(142, total.CacheWriteTokens);
        Assert.Equal(50, total.CacheWrite1hTokens);
    }

    [Fact]
    public void A_stored_row_without_the_hour_lived_key_sums_to_zero()
    {
        var total = TranscriptReplay.Apply(
            new TranscriptViewModel(collapseOlderTurns: false),
            Session(new TokenUsage(1000, 200, 5000, 142)),
            []);

        Assert.Equal(142, total.CacheWriteTokens);
        Assert.Equal(0, total.CacheWrite1hTokens);
    }
}
