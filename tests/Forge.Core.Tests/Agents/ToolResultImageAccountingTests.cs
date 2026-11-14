using Forge.Abstractions;
using Forge.Core.Agents;
using Xunit;

namespace Forge.Core.Tests.Agents;

/// <summary>An image nested in a tool result: the token estimate counts it like a top-level image.</summary>
public sealed class ToolResultImageAccountingTests
{
    private static readonly string Payload = new('x', 400);

    [Fact]
    public void A_tool_result_with_an_image_costs_more_than_the_same_text_alone()
    {
        var counter = new ApproximateTokenCounter();
        var plain = new ChatMessage(MessageRole.Tool, [new ToolResultBlock("c1", Payload)]);
        var withImage = new ChatMessage(MessageRole.Tool,
            [new ToolResultBlock("c1", [new TextBlock(Payload), new ImageBlock("image/png", "AAAA")])]);

        Assert.True(counter.Count([withImage]) > counter.Count([plain]));
    }

    [Fact]
    public void A_nested_image_costs_the_flat_image_estimate_like_a_top_level_image()
    {
        var counter = new ApproximateTokenCounter();
        var imageOnly = new ChatMessage(MessageRole.Tool, [new ToolResultBlock("c1", [new ImageBlock("image/png", "AAAA")])]);

        Assert.True(counter.Count([imageOnly]) >= ApproximateTokenCounter.ImageTokens);
    }
}
