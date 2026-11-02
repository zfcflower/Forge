using System.Text.Json;
using Forge.Abstractions;
using Forge.Core.Storage;
using Xunit;

namespace Forge.Core.Tests.Storage;

public class MessageJsonTests
{
    [Fact]
    public void Serialization_is_deterministic_and_round_trips()
    {
        var message = new ChatMessage(MessageRole.Tool,
        [
            new ThinkingBlock("why", "sig"),
            new ToolCallBlock("id", "name", "{}"),
            new ToolResultBlock("id", "out", true),
            new ArtifactRefBlock("a", "desc", 7),
            new TextBlock("t"),
        ]);

        var first = MessageJson.Serialize(message);
        var second = MessageJson.Serialize(message);

        Assert.Equal(first, second);
        Assert.Equal(first, MessageJson.Serialize(MessageJson.Deserialize(first)));
        Assert.Equal(message.Content, MessageJson.Deserialize(first).Content);
    }

    [Fact]
    public void Optional_fields_are_omitted_from_the_wire_format()
    {
        var json = MessageJson.Serialize(new ChatMessage(MessageRole.User,
        [
            new ThinkingBlock("thought"),
            new ToolResultBlock("call", "out"),
        ]));

        Assert.Equal("""{"role":"user","content":[{"type":"thinking","text":"thought"},{"type":"tool_result","call_id":"call","content":"out"}]}""", json);
    }

    [Fact]
    public void Usage_round_trips_and_zero_fields_are_explicit()
    {
        var json = MessageJson.SerializeUsage(new TokenUsage(1, 2, 3, 4));

        Assert.Equal("""{"input":1,"output":2,"cache_read":3,"cache_write":4}""", json);
        Assert.Equal(new TokenUsage(1, 2, 3, 4), MessageJson.ParseUsage(json));
        Assert.Null(MessageJson.SerializeUsage(null));
        Assert.Null(MessageJson.ParseUsage(null));
        Assert.Equal(TokenUsage.Empty, MessageJson.ParseUsage("{}"));
    }

    [Fact]
    public void Unknown_block_types_fail_loudly()
    {
        Assert.Throws<JsonException>(() => MessageJson.Deserialize("""{"role":"user","content":[{"type":"audio","data":"x"}]}"""));
        Assert.Throws<JsonException>(() => MessageJson.Deserialize("""{"role":"robot","content":[]}"""));
        Assert.Throws<JsonException>(() => MessageJson.Deserialize("[]"));
    }

    [Fact]
    public void Search_text_covers_prose_reasoning_and_tool_output_only()
    {
        var message = new ChatMessage(MessageRole.Assistant,
        [
            new TextBlock("text"),
            new ThinkingBlock("thinking"),
            new ToolCallBlock("call", "tool", "{\"secret\":1}"),
            new ToolResultBlock("call", "result"),
            new ArtifactRefBlock("artifact", "description", 1),
        ]);

        var search = MessageJson.ExtractSearchText(message);

        Assert.Contains("text", search, StringComparison.Ordinal);
        Assert.Contains("thinking", search, StringComparison.Ordinal);
        Assert.Contains("result", search, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", search, StringComparison.Ordinal);
        Assert.DoesNotContain("description", search, StringComparison.Ordinal);
        Assert.Contains("secret", MessageJson.ExtractFullText(message), StringComparison.Ordinal);
        Assert.Equal(string.Empty, MessageJson.ExtractSearchText(new ChatMessage(MessageRole.User, [])));
    }
}
