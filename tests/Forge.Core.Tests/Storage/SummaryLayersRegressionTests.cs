using Forge.Abstractions;
using Forge.Core.Storage;
using Xunit;

namespace Forge.Core.Tests.Storage;

public class SummaryLayersRegressionTests
{
    private static readonly DateTimeOffset Clock = new(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly Func<string, int> Chars = static text => text.Length;

    [Fact]
    public void Single_tool_call_is_promoted_together_with_its_result()
    {
        var messages = new List<StoredMessage>
        {
            Stored(1, ChatMessage.User(new string('u', 100))),
            Stored(2, new ChatMessage(MessageRole.Assistant, [new ToolCallBlock("c1", "tool", string.Empty)])),
            Stored(3, new ChatMessage(MessageRole.Tool, [new ToolResultBlock("c1", "rr")])),
            Stored(4, ChatMessage.User("newest")),
        };

        var plan = SummaryLayers.Plan([], messages, 4, 9, Chars);

        Assert.Equal(new SummaryRange(1, 3), plan.Promote);
        Assert.False(plan.CannotSatisfy);
    }

    [Fact]
    public void Parallel_tool_calls_are_promoted_with_every_result_and_never_split()
    {
        var messages = new List<StoredMessage>
        {
            Stored(1, ChatMessage.User(new string('u', 100))),
            Stored(2, new ChatMessage(MessageRole.Assistant,
            [
                new ToolCallBlock("c1", "a", string.Empty),
                new ToolCallBlock("c2", "b", string.Empty),
            ])),
            Stored(3, new ChatMessage(MessageRole.Tool, [new ToolResultBlock("c1", "x")])),
            Stored(4, new ChatMessage(MessageRole.Tool, [new ToolResultBlock("c2", "y")])),
            Stored(5, ChatMessage.User("newest")),
        };

        var plan = SummaryLayers.Plan([], messages, 5, 9, Chars);

        Assert.Equal(new SummaryRange(1, 4), plan.Promote);
    }

    [Fact]
    public void Newest_tool_result_without_a_following_user_message_is_kept_whole()
    {
        var messages = new List<StoredMessage>
        {
            Stored(1, ChatMessage.User(new string('u', 100))),
            Stored(2, new ChatMessage(MessageRole.Assistant, [new ToolCallBlock("c1", "tool", string.Empty)])),
            Stored(3, new ChatMessage(MessageRole.Tool, [new ToolResultBlock("c1", "rr")])),
        };

        var plan = SummaryLayers.Plan([], messages, 3, 0, Chars);

        Assert.Null(plan.Promote);
        Assert.Empty(plan.Fold);
        Assert.True(plan.CannotSatisfy);
    }

    [Fact]
    public void Over_budget_stage_summaries_fold_only_the_oldest_one_below_the_count_limit()
    {
        var summaries = new List<SummaryRecord>
        {
            new("s", 1, 1, 2, new string('a', 40), Clock),
            new("s", 1, 3, 4, new string('b', 40), Clock),
            new("s", 1, 5, 6, new string('c', 40), Clock),
        };
        var messages = new List<StoredMessage> { Stored(7, ChatMessage.User("abc")) };

        var plan = SummaryLayers.Plan(summaries, messages, 7, 100, Chars, maxStageSummaries: 8);

        var folded = Assert.Single(plan.Fold);
        Assert.Equal(new SummaryRange(1, 2), plan.FoldRange);
        Assert.Equal(1, folded.FromSeq);
        Assert.Equal(2, folded.ToSeq);
        Assert.Equal(new string('a', 40), folded.Text);
        Assert.Null(plan.Promote);
        Assert.False(plan.CannotSatisfy);
    }

    [Fact]
    public void Stage_summaries_within_budget_and_count_limit_are_not_folded()
    {
        var summaries = new List<SummaryRecord>
        {
            new("s", 1, 1, 2, new string('a', 40), Clock),
            new("s", 1, 3, 4, new string('b', 40), Clock),
        };
        var messages = new List<StoredMessage> { Stored(5, ChatMessage.User("abc")) };

        var plan = SummaryLayers.Plan(summaries, messages, 5, 100, Chars, maxStageSummaries: 8);

        Assert.True(plan.IsIdle);
        Assert.Empty(plan.Fold);
        Assert.Null(plan.FoldRange);
    }

    [Fact]
    public void Within_budget_is_none_and_not_cannot_satisfy()
    {
        var messages = new List<StoredMessage> { Stored(1, ChatMessage.User("hi")), Stored(2, ChatMessage.User("there")) };

        var plan = SummaryLayers.Plan([], messages, 2, 1000, Chars);

        Assert.True(plan.IsIdle);
        Assert.False(plan.CannotSatisfy);
    }

    [Fact]
    public void Over_budget_with_nothing_promotable_is_cannot_satisfy_rather_than_none()
    {
        var messages = new List<StoredMessage> { Stored(1, ChatMessage.User(new string('x', 50))) };

        var plan = SummaryLayers.Plan([], messages, 1, 10, Chars);

        Assert.True(plan.IsIdle);
        Assert.Null(plan.Promote);
        Assert.True(plan.CannotSatisfy);
    }

    [Fact]
    public void Oversized_session_summary_with_no_stage_to_fold_cannot_be_satisfied()
    {
        var summaries = new List<SummaryRecord> { new("s", 2, 1, 5, new string('s', 50), Clock) };
        var messages = new List<StoredMessage> { Stored(6, ChatMessage.User("abc")) };

        var plan = SummaryLayers.Plan(summaries, messages, 6, 10, Chars);

        Assert.True(plan.IsIdle);
        Assert.True(plan.CannotSatisfy);
    }

    [Fact]
    public void Over_budget_with_a_promotable_range_is_not_cannot_satisfy()
    {
        var messages = new List<StoredMessage>
        {
            Stored(1, ChatMessage.User(new string('x', 50))),
            Stored(2, ChatMessage.User("tail")),
        };

        var plan = SummaryLayers.Plan([], messages, 2, 10, Chars);

        Assert.Equal(new SummaryRange(1, 1), plan.Promote);
        Assert.False(plan.CannotSatisfy);
    }

    private static StoredMessage Stored(long seq, ChatMessage message) => new(seq, message, null, Clock.AddMinutes(seq));
}
