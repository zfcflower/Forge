using Avalonia.Headless;
using Forge.Abstractions;
using Forge.App.Localization;
using Forge.App.ViewModels;
using Xunit;

namespace Forge.App.Tests;

/// <summary>
/// Pins <see cref="TranscriptReplay"/> mapping: stored user/assistant text, tool calls/results and usage
/// become the expected turn cards — one card per assistant turn, ✓/✗ tool rows, and "—" where the provider
/// reported no usage.
/// </summary>
public sealed class TranscriptReplayTests
{
    private static readonly DateTimeOffset T0 = new(2025, 6, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task User_and_assistant_text_produce_ordered_bubbles_and_one_card_per_turn()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));
        var result = await session.Dispatch(
            () =>
            {
                var transcript = new TranscriptViewModel(collapseOlderTurns: false);
                var usage = TranscriptReplay.Apply(
                    transcript,
                    [
                        Stored(UserText("first question"), null, 1),
                        Stored(AssistantText("first answer"), new TokenUsage(10, 5), 2),
                        Stored(UserText("second question"), null, 3),
                        Stored(AssistantText("second answer"), null, 4),
                    ]);
                return (Items: transcript.Items.ToList(), Usage: usage);
            },
            CancellationToken.None);

        Assert.Equal(4, result.Items.Count);
        var firstUser = Assert.IsType<UserMessageViewModel>(result.Items[0]);
        var firstCard = Assert.IsType<TurnCardViewModel>(result.Items[1]);
        var secondUser = Assert.IsType<UserMessageViewModel>(result.Items[2]);
        var secondCard = Assert.IsType<TurnCardViewModel>(result.Items[3]);

        Assert.Equal("first question", firstUser.Text);
        Assert.Equal("second question", secondUser.Text);
        Assert.Equal(1, firstCard.Round);
        Assert.Equal(2, secondCard.Round);

        // The provider reported usage for the first turn only (elapsed time is always
        // unknown for replayed cards, so only the token segment is asserted here).
        Assert.True(firstCard.HasUsage);
        Assert.Contains("输入 10 · 输出 5", firstCard.Header);
        Assert.Contains("输入 — · 输出 —", secondCard.Header);
        Assert.False(secondCard.HasUsage);

        Assert.Equal(new TokenUsage(10, 5), result.Usage);
    }

    [Fact]
    public async Task Tool_calls_and_results_map_to_rows_with_checkmarks()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));
        var cards = await session.Dispatch(
            () =>
            {
                var transcript = new TranscriptViewModel(collapseOlderTurns: false);
                TranscriptReplay.Apply(
                    transcript,
                    [
                        Stored(UserText("run it"), null, 1),
                        Stored(AssistantWithCalls("working", ("call-ok", "read", """{"file":"a.txt"}""")), null, 2),
                        Stored(ToolResult("call-ok", "file contents", isError: false), null, 3),
                        Stored(UserText("now this"), null, 4),
                        Stored(AssistantWithCalls("retrying", ("call-bad", "write", """{"file":"b.txt"}""")), null, 5),
                        Stored(ToolResult("call-bad", "denied", isError: true), null, 6),
                    ]);
                return transcript.Items.OfType<TurnCardViewModel>().ToList();
            },
            CancellationToken.None);

        Assert.Equal(2, cards.Count);
        var okRow = Assert.Single(cards[0].Blocks.OfType<ToolRowViewModel>());
        Assert.Equal("read", okRow.Name);
        Assert.Equal("✓", okRow.Status);
        Assert.Equal("file contents", okRow.Result);
        Assert.False(okRow.IsError);

        var badRow = Assert.Single(cards[1].Blocks.OfType<ToolRowViewModel>());
        Assert.Equal("write", badRow.Name);
        Assert.Equal("✗", badRow.Status);
        Assert.Equal("denied", badRow.Result);
        Assert.True(badRow.IsError);
    }

    [Fact]
    public async Task Zero_usage_is_shown_as_a_dash()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));
        var card = await session.Dispatch(
            () =>
            {
                var transcript = new TranscriptViewModel(collapseOlderTurns: false);
                TranscriptReplay.Apply(
                    transcript,
                    [
                        Stored(UserText("hi"), null, 1),
                        Stored(AssistantText("hello"), new TokenUsage(0, 0), 2),
                    ]);
                return transcript.Items.OfType<TurnCardViewModel>().Single();
            },
            CancellationToken.None);

        Assert.False(card.HasUsage);
        Assert.Contains(TextFormat.None, card.Header);
    }

    [Fact]
    public async Task Assistant_turns_without_a_new_user_message_share_one_card()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));
        var items = await session.Dispatch(
            () =>
            {
                var transcript = new TranscriptViewModel(collapseOlderTurns: false);
                TranscriptReplay.Apply(
                    transcript,
                    [
                        Stored(UserText("go"), null, 1),
                        Stored(AssistantText("part one"), null, 2),
                        Stored(AssistantWithCalls("part two", ("c1", "grep", "{}")), null, 3),
                        Stored(ToolResult("c1", "hit", isError: false), null, 4),
                    ]);
                return transcript.Items.ToList();
            },
            CancellationToken.None);

        Assert.Equal(2, items.Count);
        var card = Assert.IsType<TurnCardViewModel>(items[1]);
        Assert.Single(card.Blocks.OfType<TurnTextViewModel>());
        Assert.Single(card.Blocks.OfType<ToolRowViewModel>());
    }

    private static StoredMessage Stored(ChatMessage message, TokenUsage? usage, long seq) =>
        new(seq, message, usage, T0.AddMinutes(seq));

    private static ChatMessage UserText(string text) =>
        new(MessageRole.User, [new TextBlock(text)]);

    private static ChatMessage AssistantText(string text) =>
        new(MessageRole.Assistant, [new TextBlock(text)]);

    private static ChatMessage AssistantWithCalls(string text, params (string Id, string Name, string Args)[] calls)
    {
        var blocks = new List<ContentBlock> { new TextBlock(text) };
        foreach (var (id, name, args) in calls)
        {
            blocks.Add(new ToolCallBlock(id, name, args));
        }

        return new(MessageRole.Assistant, blocks);
    }

    private static ChatMessage ToolResult(string callId, string content, bool isError) =>
        new(MessageRole.Tool, [new ToolResultBlock(callId, content, isError)]);
}
