using Forge.Abstractions;
using Forge.App.ViewModels;
using Forge.Core.Providers;
using Xunit;

namespace Forge.App.Tests;

/// <summary>
/// A stored tool result holding text and an image replays through the real transcript: the row shows the flattened text
/// (the image as its placeholder, never the base64). The row does not expose the blocks themselves, so that is what is pinned.
/// </summary>
[Collection("forge-app-ui")]
public sealed class TranscriptReplayToolImageTests
{
    private static readonly DateTimeOffset T0 = new(2025, 6, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly string Base64 = Convert.ToBase64String(new byte[3000]);

    [Fact]
    public async Task A_stored_text_and_image_tool_result_replays_as_text_with_an_image_placeholder()
    {
        var rows = await ReplayAsync(new ToolResultBlock("call-1", [new TextBlock("shot taken"), new ImageBlock("image/png", Base64)]));

        var row = Assert.Single(rows);
        Assert.Equal("✓", row.Status);
        Assert.Equal("shot taken\n[image: image/png, 3 KB]", row.Result);
        Assert.DoesNotContain(Base64, row.Result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_stored_error_result_with_an_image_keeps_its_error_state()
    {
        var rows = await ReplayAsync(new ToolResultBlock("call-1", [new ImageBlock("image/png", Base64)], true));

        var row = Assert.Single(rows);
        Assert.True(row.IsError);
        Assert.Equal("✗", row.Status);
        Assert.Equal("[image: image/png, 3 KB]", row.Result);
    }

    private static async Task<List<ToolRowViewModel>> ReplayAsync(ToolResultBlock stored) =>
        await SharedUi.Session.Dispatch(
            () =>
            {
                var transcript = new TranscriptViewModel(collapseOlderTurns: false);
                TranscriptReplay.Apply(
                    transcript,
                    [
                        Stored(new ChatMessage(MessageRole.User, [new TextBlock("go")]), 1),
                        Stored(new ChatMessage(MessageRole.Assistant, [new TextBlock("working"), new ToolCallBlock("call-1", "shot", "{}")]), 2),
                        Stored(new ChatMessage(MessageRole.Tool, [stored]), 3),
                    ]);
                return transcript.Items.OfType<TurnCardViewModel>().SelectMany(card => card.Blocks.OfType<ToolRowViewModel>()).ToList();
            },
            CancellationToken.None);

    private static StoredMessage Stored(ChatMessage message, long seq) =>
        new(seq, message, null, T0.AddMinutes(seq));
}
