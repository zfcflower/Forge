using System.Text.Json;
using Forge.Abstractions;
using Forge.Core.Storage;
using Xunit;

namespace Forge.Core.Tests.Storage;

/// <summary>Text-and-image tool results: the flattened text, validation, element-wise equality and the stored shape.</summary>
public sealed class ToolResultBlocksTests
{
    // 3000 bytes of payload: exactly 4000 base64 characters, shown as 3 KB.
    private static readonly string Base64 = Convert.ToBase64String(new byte[3000]);

    private static ImageBlock Image() => new("image/png", Base64);

    private static ToolResultBlock Mixed(string callId = "c1", bool isError = false) =>
        new(callId, [new TextBlock("before"), Image(), new TextBlock("after")], isError);

    [Fact]
    public void A_single_text_block_flattens_to_the_same_string_instance()
    {
        var text = new string('a', 40);

        Assert.Same(text, new ToolResult([new TextBlock(text)]).Content);
        Assert.Same(text, new ToolResultBlock("c", [new TextBlock(text)]).Content);
        Assert.Same(text, new ToolResult(text).Content);
    }

    [Fact]
    public void Several_blocks_flatten_one_per_line_with_the_image_as_a_placeholder()
    {
        var result = new ToolResult([new TextBlock("before"), Image(), new TextBlock("after")]);

        Assert.Equal("before\n[image: image/png, 3 KB]\nafter", result.Content);
        Assert.DoesNotContain(Base64, result.Content, StringComparison.Ordinal);
        Assert.Equal(result.Content, new ToolResultBlock("c", result.Blocks).Content);
    }

    [Fact]
    public void An_image_alone_and_two_texts_flatten_without_stray_separators()
    {
        Assert.Equal("[image: image/png, 3 KB]", new ToolResult([Image()]).Content);
        Assert.Equal("a\nb", new ToolResult([new TextBlock("a"), new TextBlock("b")]).Content);
    }

    [Fact]
    public void An_empty_block_list_flattens_to_the_empty_string()
    {
        Assert.Equal(string.Empty, new ToolResult([]).Content);
        Assert.Equal(string.Empty, new ToolResultBlock("c", []).Content);
        Assert.Equal(string.Empty, new ToolResult(string.Empty).Content);
    }

    public static TheoryData<ContentBlock> ForbiddenBlocks => new()
    {
        new ThinkingBlock("hm"),
        new ToolCallBlock("id", "name", "{}"),
        new ToolResultBlock("inner", "nested"),
        new FileBlock("application/pdf", "AAAA"),
        new ArtifactRefBlock("a", "d", 1),
    };

    [Theory]
    [MemberData(nameof(ForbiddenBlocks))]
    public void Anything_but_text_and_image_is_rejected(ContentBlock forbidden)
    {
        Assert.Throws<ArgumentException>(() => new ToolResult([new TextBlock("ok"), forbidden]));
        Assert.Throws<ArgumentException>(() => new ToolResultBlock("c", [forbidden, Image()]));
    }

    [Fact]
    public void A_null_list_or_a_null_entry_is_rejected()
    {
        Assert.Throws<ArgumentNullException>(() => new ToolResult((IReadOnlyList<ContentBlock>)null!));
        Assert.Throws<ArgumentNullException>(() => new ToolResultBlock("c", (IReadOnlyList<ContentBlock>)null!));
        Assert.Throws<ArgumentException>(() => new ToolResult([null!]));
    }

    [Fact]
    public void A_with_expression_on_a_tool_result_validates_the_replacement_blocks()
    {
        var result = new ToolResult([new TextBlock("old")], true);

        Assert.Throws<ArgumentException>(() => result with { Blocks = [new ThinkingBlock("x")] });
        Assert.Throws<ArgumentNullException>(() => result with { Blocks = null! });

        var replaced = result with { Blocks = [new TextBlock("new"), Image()] };
        Assert.Equal("new\n[image: image/png, 3 KB]", replaced.Content);
        Assert.True(replaced.IsError);
        Assert.Equal("old", result.Content);
    }

    [Fact]
    public void A_with_expression_on_a_tool_result_block_validates_the_replacement_blocks()
    {
        var block = new ToolResultBlock("c9", "old", true);

        Assert.Throws<ArgumentException>(() => block with { Blocks = [new ToolCallBlock("i", "n", "{}")] });
        Assert.Throws<ArgumentNullException>(() => block with { Blocks = null! });

        var replaced = block with { Blocks = [Image()] };
        Assert.Equal("[image: image/png, 3 KB]", replaced.Content);
        Assert.Equal("c9", replaced.CallId);
        Assert.True(replaced.IsError);
        Assert.Equal("old", block.Content);
    }

    [Fact]
    public void Equal_results_built_separately_are_equal_and_hash_alike()
    {
        var left = new ToolResult([new TextBlock("t"), Image()], false);
        var right = new ToolResult([new TextBlock("t"), new ImageBlock("image/png", Base64)], false);

        Assert.Equal(left, right);
        Assert.True(left == right);
        Assert.Equal(left.GetHashCode(), right.GetHashCode());
        Assert.Equal(new ToolResult("same"), new ToolResult("same"));
        Assert.Equal(new ToolResult("same").GetHashCode(), new ToolResult("same").GetHashCode());
    }

    [Fact]
    public void Results_differing_in_a_block_the_order_the_count_or_the_error_flag_are_unequal()
    {
        var baseline = new ToolResult([new TextBlock("t"), Image()]);

        Assert.NotEqual(baseline, new ToolResult([new TextBlock("t"), new ImageBlock("image/png", "AAAA")]));
        Assert.NotEqual(baseline, new ToolResult([new TextBlock("t"), new ImageBlock("image/jpeg", Base64)]));
        Assert.NotEqual(baseline, new ToolResult([new TextBlock("T"), Image()]));
        Assert.NotEqual(baseline, new ToolResult([Image(), new TextBlock("t")]));
        Assert.NotEqual(baseline, new ToolResult([new TextBlock("t")]));
        Assert.NotEqual(baseline, new ToolResult([new TextBlock("t"), Image()], true));
        Assert.False(baseline.Equals(null));
    }

    [Fact]
    public void Equal_tool_result_blocks_are_equal_and_hash_alike()
    {
        Assert.Equal(Mixed(), Mixed());
        Assert.Equal(Mixed().GetHashCode(), Mixed().GetHashCode());
        Assert.Equal(new ToolResultBlock("c", "x", true), new ToolResultBlock("c", "x", true));
    }

    [Fact]
    public void Tool_result_blocks_differing_in_call_id_error_flag_or_blocks_are_unequal()
    {
        var baseline = Mixed();

        Assert.NotEqual(baseline, Mixed(callId: "other"));
        Assert.NotEqual(baseline, Mixed(isError: true));
        Assert.NotEqual(baseline, new ToolResultBlock("c1", [new TextBlock("before"), Image()]));
        Assert.NotEqual(baseline, new ToolResultBlock("c1", [new TextBlock("before"), new ImageBlock("image/png", "AAAA"), new TextBlock("after")]));
        Assert.False(baseline.Equals(null));
    }

    [Fact]
    public void A_plain_text_result_is_stored_without_a_blocks_property()
    {
        var message = new ChatMessage(MessageRole.Tool, [new ToolResultBlock("c1", "plain")]);

        var json = MessageJson.Serialize(message);

        using var document = JsonDocument.Parse(json);
        var stored = document.RootElement.GetProperty("content")[0];
        Assert.False(stored.TryGetProperty("blocks", out _));
        Assert.Equal("plain", stored.GetProperty("content").GetString());
        Assert.Equal(message.Content, MessageJson.Deserialize(json).Content);
    }

    [Fact]
    public void An_empty_result_round_trips()
    {
        var message = new ChatMessage(MessageRole.Tool, [new ToolResultBlock("c1", [])]);

        var json = MessageJson.Serialize(message);
        var back = Assert.IsType<ToolResultBlock>(Assert.Single(MessageJson.Deserialize(json).Content));

        Assert.Empty(back.Blocks);
        Assert.Equal(string.Empty, back.Content);
    }

    [Fact]
    public void Text_image_text_is_stored_with_blocks_and_flattened_content_and_round_trips()
    {
        var original = Mixed(isError: true);
        var message = new ChatMessage(MessageRole.Tool, [original]);

        var json = MessageJson.Serialize(message);

        using var document = JsonDocument.Parse(json);
        var stored = document.RootElement.GetProperty("content")[0];
        Assert.Equal("before\n[image: image/png, 3 KB]\nafter", stored.GetProperty("content").GetString());
        Assert.Equal(3, stored.GetProperty("blocks").GetArrayLength());
        Assert.True(stored.GetProperty("is_error").GetBoolean());
        var back = Assert.IsType<ToolResultBlock>(Assert.Single(MessageJson.Deserialize(json).Content));
        Assert.Equal(original, back);
        Assert.IsType<ImageBlock>(back.Blocks[1]);
        Assert.Equal(json, MessageJson.Serialize(MessageJson.Deserialize(json)));
    }

    [Fact]
    public void An_image_only_result_is_stored_with_blocks()
    {
        var original = new ToolResultBlock("c1", [Image()]);

        var json = MessageJson.Serialize(new ChatMessage(MessageRole.Tool, [original]));

        Assert.Contains("\"blocks\"", json, StringComparison.Ordinal);
        Assert.Equal(original, Assert.Single(MessageJson.Deserialize(json).Content));
    }

    [Fact]
    public void A_row_written_before_blocks_existed_reads_as_one_text_block()
    {
        const string Json = """{"role":"tool","content":[{"type":"tool_result","call_id":"c1","content":"old text","is_error":true}]}""";

        var block = Assert.IsType<ToolResultBlock>(Assert.Single(MessageJson.Deserialize(Json).Content));

        Assert.Equal("old text", Assert.IsType<TextBlock>(Assert.Single(block.Blocks)).Text);
        Assert.True(block.IsError);
        Assert.Equal("old text", block.Content);
    }

    [Fact]
    public void When_both_are_stored_blocks_win_over_content()
    {
        const string Json = """{"role":"tool","content":[{"type":"tool_result","call_id":"c1","content":"flat","blocks":[{"type":"text","text":"a"},{"type":"image","media_type":"image/png","data":"AAAA"}]}]}""";

        var block = Assert.IsType<ToolResultBlock>(Assert.Single(MessageJson.Deserialize(Json).Content));

        Assert.Equal(2, block.Blocks.Count);
        Assert.Equal("a\n[image: image/png, 1 KB]", block.Content);
    }

    [Theory]
    [InlineData("""{"type":"thinking","text":"hm"}""")]
    [InlineData("""{"type":"tool_call","id":"i","name":"n","arguments":"{}"}""")]
    [InlineData("""{"type":"tool_result","call_id":"x","content":"nested"}""")]
    [InlineData("""{"type":"mystery"}""")]
    public void A_stored_blocks_array_holding_anything_but_text_or_image_fails_loudly(string inner)
    {
        var json = $$"""{"role":"tool","content":[{"type":"tool_result","call_id":"c1","content":"x","blocks":[{{inner}}]}]}""";

        Assert.Throws<JsonException>(() => MessageJson.Deserialize(json));
    }

    [Fact]
    public void A_blocks_property_that_is_not_an_array_fails_loudly()
    {
        const string Json = """{"role":"tool","content":[{"type":"tool_result","call_id":"c1","content":"x","blocks":"nope"}]}""";

        Assert.Throws<JsonException>(() => MessageJson.Deserialize(Json));
    }

    [Fact]
    public void Search_and_full_text_of_an_image_tool_result_carry_the_placeholder_never_the_base64()
    {
        var message = new ChatMessage(MessageRole.Tool, [Mixed()]);

        var search = MessageJson.ExtractSearchText(message);
        var full = MessageJson.ExtractFullText(message);

        Assert.Contains("[image: image/png, 3 KB]", search, StringComparison.Ordinal);
        Assert.Contains("[image: image/png, 3 KB]", full, StringComparison.Ordinal);
        Assert.Contains("before", search, StringComparison.Ordinal);
        Assert.Contains("after", full, StringComparison.Ordinal);
        Assert.DoesNotContain(Base64, search, StringComparison.Ordinal);
        Assert.DoesNotContain(Base64, full, StringComparison.Ordinal);
    }
}
