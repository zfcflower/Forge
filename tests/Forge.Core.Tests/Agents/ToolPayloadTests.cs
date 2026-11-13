using Forge.Abstractions;
using Xunit;

namespace Forge.Core.Tests.Agents;

/// <summary>The two text readings of a tool payload, which the providers and the storage layer both build on.</summary>
public sealed class ToolPayloadTests
{
    private const string Data = "iVBORw0KGgo=";

    [Fact]
    public void A_text_only_result_reads_the_same_both_ways_and_copies_nothing()
    {
        var single = new ToolResult("one line");

        Assert.Same(single.Text, single.Content);
        Assert.Equal("one line", single.Content);

        var two = new ToolResult([new TextBlock("first"), new TextBlock("second")]);
        Assert.Equal("first\nsecond", two.Text);
        Assert.Same(two.Text, two.Content);
    }

    [Fact]
    public void An_image_adds_a_placeholder_to_the_text_view_only()
    {
        var result = new ToolResult([new TextBlock("shot"), new ImageBlock("image/png", Data)]);

        Assert.Equal("shot", result.Text);
        Assert.Equal("shot\n[image: image/png]", result.Content);
        Assert.Equal(Data, Assert.Single(result.Images).Data);
    }

    [Fact]
    public void A_result_of_images_only_still_reads_as_text()
    {
        var result = new ToolResult([new ImageBlock("image/png", Data), new ImageBlock("image/jpeg", "AAAA")]);

        Assert.Equal(string.Empty, result.Text);
        Assert.Equal("[image: image/png]\n[image: image/jpeg]", result.Content);
        Assert.Equal(2, result.Images.Count);
    }

    [Fact]
    public void An_empty_result_reads_as_empty()
    {
        var result = new ToolResult([]);

        Assert.Empty(result.Blocks);
        Assert.Empty(result.Images);
        Assert.Equal(string.Empty, result.Text);
        Assert.Equal(string.Empty, result.Content);
    }

    [Fact]
    public void The_block_list_is_copied_so_a_caller_cannot_change_a_result_after_the_fact()
    {
        var source = new List<ContentBlock> { new TextBlock("a") };
        var result = new ToolResult(source);
        source.Add(new ImageBlock("image/png", Data));

        Assert.Single(result.Blocks);
        Assert.Empty(result.Images);
    }

    [Fact]
    public void A_tool_result_block_carries_the_same_two_readings()
    {
        var block = new ToolResultBlock("call-1", [new TextBlock("done"), new ImageBlock("image/png", Data)]);

        Assert.Equal("done", block.Text);
        Assert.Equal("done\n[image: image/png]", block.Content);
        Assert.Single(block.Images);
        Assert.Equal("call-1", block.CallId);
    }

    [Fact]
    public void The_text_constructor_still_builds_one_block()
    {
        var block = new ToolResultBlock("call-1", "plain", IsError: true);

        Assert.True(block.IsError);
        Assert.Equal("plain", Assert.IsType<TextBlock>(Assert.Single(block.Blocks)).Text);
        Assert.Empty(block.Images);
    }
}
