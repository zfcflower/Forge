using System.Text.Json;
using Forge.Abstractions;
using Forge.Core.Storage;
using Xunit;

namespace Forge.Core.Tests.Storage;

/// <summary>A tool result that carries an image: stored as the old flat shape plus a recursive <c>blocks</c> array.</summary>
public sealed class ToolResultImageStorageTests
{
    private static readonly string Base64 = Convert.ToBase64String(new byte[3000]);

    [Fact]
    public void A_text_and_image_result_round_trips_intact_and_lossless()
    {
        var message = new ChatMessage(MessageRole.Tool,
            [new ToolResultBlock("c1", [new TextBlock("see"), new ImageBlock("image/webp", Base64)], true)]);

        var json = MessageJson.Serialize(message);

        Assert.Equal(
            $$"""{"role":"tool","content":[{"type":"tool_result","call_id":"c1","content":"see\n[image: image/webp]","is_error":true,"blocks":[{"type":"text","text":"see"},{"type":"image","media_type":"image/webp","data":"{{Base64}}"}]}]}""",
            json);
        var restored = MessageJson.Deserialize(json);
        var result = Assert.IsType<ToolResultBlock>(Assert.Single(restored.Content));
        Assert.Equal("c1", result.CallId);
        Assert.True(result.IsError);
        Assert.Equal("see\n[image: image/webp]", result.Content);
        Assert.Equal(2, result.Blocks.Count);
        Assert.Equal("see", Assert.IsType<TextBlock>(result.Blocks[0]).Text);
        var image = Assert.IsType<ImageBlock>(result.Blocks[1]);
        Assert.Equal("image/webp", image.MediaType);
        Assert.Equal(Base64, image.Data);
        Assert.Equal(new ImageBlock("image/webp", Base64), Assert.Single(result.Images));
        // write → read → write is lossless.
        Assert.Equal(json, MessageJson.Serialize(restored));
    }

    [Fact]
    public void A_single_image_result_round_trips_with_its_blocks_array()
    {
        var message = new ChatMessage(MessageRole.Tool, [new ToolResultBlock("c1", [new ImageBlock("image/png", "AAAA")])]);

        var json = MessageJson.Serialize(message);

        Assert.Equal(
            """{"role":"tool","content":[{"type":"tool_result","call_id":"c1","content":"[image: image/png]","blocks":[{"type":"image","media_type":"image/png","data":"AAAA"}]}]}""",
            json);
        var restored = MessageJson.Deserialize(json);
        var result = Assert.IsType<ToolResultBlock>(Assert.Single(restored.Content));
        Assert.Equal("c1", result.CallId);
        Assert.False(result.IsError);
        Assert.Equal("[image: image/png]", result.Content);
        var image = Assert.IsType<ImageBlock>(Assert.Single(result.Blocks));
        Assert.Equal("image/png", image.MediaType);
        Assert.Equal("AAAA", image.Data);
        Assert.Equal(new ImageBlock("image/png", "AAAA"), Assert.Single(result.Images));
    }

    [Fact]
    public void A_text_only_result_keeps_the_old_wire_shape_without_a_blocks_property()
    {
        var json = MessageJson.Serialize(new ChatMessage(MessageRole.Tool, [new ToolResultBlock("call", "out", true)]));

        Assert.Equal("""{"role":"tool","content":[{"type":"tool_result","call_id":"call","content":"out","is_error":true}]}""", json);
        Assert.DoesNotContain("\"blocks\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void An_old_tool_result_json_without_blocks_reads_back_as_a_text_result()
    {
        var restored = MessageJson.Deserialize("""{"role":"tool","content":[{"type":"tool_result","call_id":"c1","content":"old"}]}""");

        var result = Assert.IsType<ToolResultBlock>(Assert.Single(restored.Content));
        Assert.Equal("c1", result.CallId);
        Assert.Equal("old", result.Content);
        Assert.False(result.IsError);
        Assert.Equal(new TextBlock("old"), Assert.Single(result.Blocks));
    }

    [Fact]
    public void An_unknown_nested_block_type_fails_loudly()
    {
        Assert.Throws<JsonException>(() => MessageJson.Deserialize(
            """{"role":"tool","content":[{"type":"tool_result","call_id":"c1","content":"x","blocks":[{"type":"audio","data":"x"}]}]}"""));
    }

    [Fact]
    public void Search_and_full_text_include_the_placeholder_of_a_nested_image_never_its_base64()
    {
        var message = new ChatMessage(MessageRole.Tool,
            [new ToolResultBlock("c1", [new TextBlock("result"), new ImageBlock("image/png", Base64)])]);

        var search = MessageJson.ExtractSearchText(message);
        var full = MessageJson.ExtractFullText(message);

        Assert.Equal("result\n[image: image/png]", search);
        Assert.Equal(search, full);
        Assert.DoesNotContain(Base64, search, StringComparison.Ordinal);
    }
}
