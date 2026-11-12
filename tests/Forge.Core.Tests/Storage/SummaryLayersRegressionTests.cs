using Forge.Abstractions;
using Forge.Core.Storage;
using Xunit;

namespace Forge.Core.Tests.Storage;

/// <summary>The cut of the raw tail: what stays verbatim, where it may start, and when a turn is split.</summary>
public class SummaryLayersRegressionTests
{
    private static readonly DateTimeOffset Clock = new(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly Func<ChatMessage, int> Chars = static message => MessageJson.ExtractFullText(message).Length;

    [Fact]
    public void A_tail_within_the_budget_is_not_cut()
    {
        var raw = Raw(User(40), Assistant(40));

        Assert.Null(SummaryLayers.PlanCut(raw, rawBudget: 80, keepRecentTokens: 10, Chars));
        Assert.Null(SummaryLayers.PlanCut(raw, rawBudget: 0, keepRecentTokens: 80, Chars));
    }

    [Fact]
    public void Fewer_than_two_messages_are_never_cut()
    {
        Assert.Null(SummaryLayers.PlanCut(Raw(User(500)), 0, 0, Chars));
        Assert.Null(SummaryLayers.PlanCut([], 0, 0, Chars));
    }

    [Fact]
    public void The_longest_suffix_within_the_budget_stays_raw()
    {
        var raw = Raw(User(100), Assistant(100), User(10), Assistant(10));

        var cut = SummaryLayers.PlanCut(raw, rawBudget: 25, keepRecentTokens: 5, Chars);

        Assert.Equal(new CompactionCut(2, -1), cut);
    }

    [Fact]
    public void Keep_recent_raises_what_stays_raw_above_a_smaller_budget()
    {
        var raw = Raw(User(100), Assistant(100), User(10), Assistant(10));

        Assert.Equal(3, SummaryLayers.PlanCut(raw, rawBudget: -50, keepRecentTokens: 10, Chars)!.Value.KeepFrom);
        Assert.Equal(2, SummaryLayers.PlanCut(raw, rawBudget: -50, keepRecentTokens: 20, Chars)!.Value.KeepFrom);
        Assert.Equal(1, SummaryLayers.PlanCut(raw, rawBudget: -50, keepRecentTokens: 120, Chars)!.Value.KeepFrom);
    }

    [Fact]
    public void The_cut_never_lands_on_a_tool_result_so_a_call_and_its_results_stay_together()
    {
        // Indices: 0 user, 1 assistant with two calls, 2 and 3 their results, 4 assistant.
        var raw = Raw(
            User(100),
            Call("c1", "c2"),
            Result("c1", 30),
            Result("c2", 30),
            Assistant(5));

        // A budget of 40 would fit the suffix starting at the second result (35), but a result is no cut point.
        var cut = SummaryLayers.PlanCut(raw, rawBudget: 40, keepRecentTokens: 1, Chars)!.Value;

        Assert.Equal(4, cut.KeepFrom);
        Assert.Equal(MessageRole.Assistant, raw[cut.KeepFrom].Message.Role);
        Assert.NotEqual(MessageRole.Tool, raw[cut.KeepFrom].Message.Role);
    }

    [Fact]
    public void A_cut_at_an_assistant_message_inside_one_interaction_is_a_split_turn()
    {
        // One user request followed by a long tool loop: no user message to cut at after the first one.
        var raw = Raw(
            User(20),
            Call("c1"),
            Result("c1", 400),
            Call("c2"),
            Result("c2", 400),
            Call("c3"),
            Result("c3", 30),
            Assistant(20));

        var cut = SummaryLayers.PlanCut(raw, rawBudget: 100, keepRecentTokens: 10, Chars)!.Value;

        Assert.True(cut.IsSplitTurn);
        Assert.Equal(0, cut.TurnStart);
        Assert.Equal(5, cut.KeepFrom);
        Assert.Equal(0, cut.HistoryEnd);
        Assert.Equal(MessageRole.Assistant, raw[cut.KeepFrom].Message.Role);
    }

    [Fact]
    public void A_split_turn_keeps_the_earlier_interactions_as_history()
    {
        var raw = Raw(User(50), Assistant(50), User(20), Call("c1"), Result("c1", 400), Assistant(30));

        var cut = SummaryLayers.PlanCut(raw, rawBudget: 40, keepRecentTokens: 1, Chars)!.Value;

        Assert.Equal(new CompactionCut(5, 2), cut);
        Assert.Equal(2, cut.HistoryEnd);
    }

    [Fact]
    public void A_cut_at_a_user_message_is_not_a_split_turn()
    {
        var raw = Raw(User(20), Call("c1"), Result("c1", 400), Assistant(20), User(10), Assistant(10));

        var cut = SummaryLayers.PlanCut(raw, rawBudget: 25, keepRecentTokens: 1, Chars)!.Value;

        Assert.Equal(new CompactionCut(4, -1), cut);
        Assert.False(cut.IsSplitTurn);
    }

    [Fact]
    public void A_tail_continuing_an_already_split_turn_is_not_split_again()
    {
        // The raw tail starts inside a turn whose user message was summarized by an earlier compaction.
        var raw = Raw(Call("c1"), Result("c1", 400), Call("c2"), Result("c2", 30), Assistant(10));

        var cut = SummaryLayers.PlanCut(raw, rawBudget: 50, keepRecentTokens: 1, Chars)!.Value;

        Assert.Equal(new CompactionCut(2, -1), cut);
    }

    [Fact]
    public void When_no_suffix_fits_only_the_newest_valid_suffix_stays_raw()
    {
        var raw = Raw(User(100), Assistant(100), User(500));

        Assert.Equal(new CompactionCut(2, -1), SummaryLayers.PlanCut(raw, rawBudget: 10, keepRecentTokens: 10, Chars));
    }

    [Fact]
    public void Without_a_cut_point_after_the_first_message_nothing_is_cut()
    {
        var raw = Raw(Call("c1"), Result("c1", 400));

        Assert.Null(SummaryLayers.PlanCut(raw, rawBudget: 0, keepRecentTokens: 1, Chars));
    }

    private static ChatMessage User(int length) => ChatMessage.User(new string('u', length));

    private static ChatMessage Assistant(int length) => ChatMessage.Assistant(new string('a', length));

    private static ChatMessage Call(params string[] ids) =>
        new(MessageRole.Assistant, [.. ids.Select(static id => (ContentBlock)new ToolCallBlock(id, "tool", string.Empty))]);

    private static ChatMessage Result(string id, int length) =>
        new(MessageRole.Tool, [new ToolResultBlock(id, new string('r', length))]);

    private static List<StoredMessage> Raw(params ChatMessage[] messages)
    {
        var raw = new List<StoredMessage>(messages.Length);
        for (var index = 0; index < messages.Length; index++)
        {
            raw.Add(new StoredMessage(index + 1, messages[index], null, Clock.AddMinutes(index)));
        }

        return raw;
    }
}
