using System.Text.Json;
using Forge.Abstractions;
using Forge.Core.Agents;
using Forge.Core.Providers;
using Xunit;

namespace Forge.Core.Tests.Providers;

/// <summary>
/// A tool result that produced a picture, in each wire protocol. Every protocol has its own answer: Anthropic and the
/// Responses API carry the image inside the tool result, while a Chat Completions <c>tool</c> message is text-only and the
/// picture follows in its own user turn. A text-only result must keep writing exactly the bytes it wrote before.
/// </summary>
public sealed class ToolImageWireTests
{
    private const string Data = "iVBORw0KGgo=";
    private static readonly ImageBlock Png = new("image/png", Data);

    private static ChatMessage ToolTurn(ToolResultBlock result) => new(MessageRole.Tool, [result]);

    private static IReadOnlyList<ChatMessage> CallThenTool(ToolResultBlock result) =>
    [
        ChatMessage.User("take a screenshot"),
        new ChatMessage(MessageRole.Assistant, [new ToolCallBlock("call-1", "screenshot", "{}")]),
        ToolTurn(result),
    ];

    private static ToolResultBlock WithImage(string text = "captured 1280x720") =>
        new("call-1", [new TextBlock(text), Png]);

    private static ToolResultBlock TextOnly() => new("call-1", "captured nothing");

    // ------------------------------------------------------------------ Anthropic

    [Fact]
    public async Task Anthropic_writes_a_tool_result_with_an_image_as_a_content_array()
    {
        var (provider, handler) = TestData.AnthropicProvider();

        await provider.CollectAsync(TestData.Request(messages: CallThenTool(WithImage())));

        var result = FindToolResult(handler.Last.Root);
        var content = result.GetProperty("content");
        Assert.Equal(JsonValueKind.Array, content.ValueKind);
        Assert.Equal(2, content.GetArrayLength());
        Assert.Equal("text", content[0].GetProperty("type").GetString());
        Assert.Equal("captured 1280x720", content[0].GetProperty("text").GetString());
        Assert.Equal("image", content[1].GetProperty("type").GetString());
        var source = content[1].GetProperty("source");
        Assert.Equal("base64", source.GetProperty("type").GetString());
        Assert.Equal("image/png", source.GetProperty("media_type").GetString());
        Assert.Equal(Data, source.GetProperty("data").GetString());
    }

    [Fact]
    public async Task Anthropic_keeps_a_text_only_tool_result_a_plain_string()
    {
        var (provider, handler) = TestData.AnthropicProvider();

        await provider.CollectAsync(TestData.Request(messages: CallThenTool(TextOnly())));

        var result = FindToolResult(handler.Last.Root);
        var content = result.GetProperty("content");
        Assert.Equal(JsonValueKind.String, content.ValueKind);
        Assert.Equal("captured nothing", content.GetString());
    }

    [Fact]
    public async Task Anthropic_writes_the_placeholder_when_the_turn_forbids_tool_images()
    {
        var (provider, handler) = TestData.AnthropicProvider();

        await provider.CollectAsync(TestData.Request(messages: CallThenTool(WithImage())) with { IncludeToolImages = false });

        var result = FindToolResult(handler.Last.Root);
        var content = result.GetProperty("content");
        Assert.Equal(JsonValueKind.String, content.ValueKind);
        Assert.Contains("[image: image/png]", content.GetString()!, StringComparison.Ordinal);
        Assert.DoesNotContain(Data, handler.Last.Body!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Anthropic_keeps_is_error_on_a_tool_result_that_carries_an_image()
    {
        var (provider, handler) = TestData.AnthropicProvider();

        await provider.CollectAsync(TestData.Request(messages:
            CallThenTool(new ToolResultBlock("call-1", [new TextBlock("failed"), Png], IsError: true))));

        var result = FindToolResult(handler.Last.Root);
        Assert.True(result.GetProperty("is_error").GetBoolean());
        Assert.Equal(JsonValueKind.Array, result.GetProperty("content").ValueKind);
    }

    private static JsonElement FindToolResult(JsonElement root)
    {
        foreach (var message in root.GetProperty("messages").EnumerateArray())
        {
            if (message.GetProperty("content").ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var block in message.GetProperty("content").EnumerateArray())
            {
                if (block.GetProperty("type").GetString() == "tool_result")
                {
                    return block;
                }
            }
        }

        throw new InvalidOperationException("no tool_result block was written");
    }

    // ------------------------------------------------------------------ Responses

    [Fact]
    public async Task Responses_writes_a_tool_result_with_an_image_as_an_output_array()
    {
        var (provider, handler) = TestData.ResponsesProvider();

        await provider.CollectAsync(TestData.Request(messages: CallThenTool(WithImage())));

        var output = FindFunctionCallOutput(handler.Last.Root);
        var items = output.GetProperty("output");
        Assert.Equal(JsonValueKind.Array, items.ValueKind);
        Assert.Equal(2, items.GetArrayLength());
        Assert.Equal("input_text", items[0].GetProperty("type").GetString());
        Assert.Equal("captured 1280x720", items[0].GetProperty("text").GetString());
        Assert.Equal("input_image", items[1].GetProperty("type").GetString());
        Assert.Equal("data:image/png;base64," + Data, items[1].GetProperty("image_url").GetString());
    }

    [Fact]
    public async Task Responses_keeps_a_text_only_tool_result_a_plain_string()
    {
        var (provider, handler) = TestData.ResponsesProvider();

        await provider.CollectAsync(TestData.Request(messages: CallThenTool(TextOnly())));

        var output = FindFunctionCallOutput(handler.Last.Root);
        Assert.Equal(JsonValueKind.String, output.GetProperty("output").ValueKind);
    }

    [Fact]
    public async Task Responses_writes_the_placeholder_when_the_turn_forbids_tool_images()
    {
        var (provider, handler) = TestData.ResponsesProvider();

        await provider.CollectAsync(TestData.Request(messages: CallThenTool(WithImage())) with { IncludeToolImages = false });

        var output = FindFunctionCallOutput(handler.Last.Root);
        var value = output.GetProperty("output");
        Assert.Equal(JsonValueKind.String, value.ValueKind);
        Assert.Contains("[image: image/png]", value.GetString()!, StringComparison.Ordinal);
        Assert.DoesNotContain(Data, handler.Last.Body!, StringComparison.Ordinal);
    }

    private static JsonElement FindFunctionCallOutput(JsonElement root)
    {
        foreach (var item in root.GetProperty("input").EnumerateArray())
        {
            if (item.TryGetProperty("type", out var type) && type.GetString() == "function_call_output")
            {
                return item;
            }
        }

        throw new InvalidOperationException("no function_call_output item was written");
    }

    // ------------------------------------------------------------------ Chat Completions

    [Fact]
    public async Task Chat_completions_follows_a_tool_result_with_an_image_by_a_user_turn_carrying_it()
    {
        var (provider, handler) = TestData.OpenAiProvider("[DONE]");

        await provider.CollectAsync(TestData.Request(messages: CallThenTool(WithImage())));

        var messages = handler.Last.Root.GetProperty("messages").EnumerateArray().ToList();
        var tool = Assert.Single(messages, static m => m.GetProperty("role").GetString() == "tool");
        Assert.Equal("call-1", tool.GetProperty("tool_call_id").GetString());
        Assert.Equal("captured 1280x720\n[image: image/png]", tool.GetProperty("content").GetString());

        var followUp = messages[^1];
        Assert.Equal("user", followUp.GetProperty("role").GetString());
        var content = followUp.GetProperty("content");
        Assert.Equal(2, content.GetArrayLength());
        Assert.Equal("text", content[0].GetProperty("type").GetString());
        Assert.Equal("image_url", content[1].GetProperty("type").GetString());
        Assert.Equal("data:image/png;base64," + Data, content[1].GetProperty("image_url").GetProperty("url").GetString());
    }

    [Fact]
    public async Task Chat_completions_writes_one_tool_message_and_nothing_else_for_a_text_only_result()
    {
        var (provider, handler) = TestData.OpenAiProvider("[DONE]");

        await provider.CollectAsync(TestData.Request(messages: CallThenTool(TextOnly())));

        var messages = handler.Last.Root.GetProperty("messages").EnumerateArray().ToList();
        Assert.Equal("tool", messages[^1].GetProperty("role").GetString());
        Assert.DoesNotContain("image_url", handler.Last.Body!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Chat_completions_writes_no_image_turn_when_the_turn_forbids_tool_images()
    {
        var (provider, handler) = TestData.OpenAiProvider("[DONE]");

        await provider.CollectAsync(TestData.Request(messages: CallThenTool(WithImage())) with { IncludeToolImages = false });

        var messages = handler.Last.Root.GetProperty("messages").EnumerateArray().ToList();
        Assert.Equal("tool", messages[^1].GetProperty("role").GetString());
        Assert.Contains("[image: image/png]", messages[^1].GetProperty("content").GetString()!, StringComparison.Ordinal);
        Assert.DoesNotContain("image_url", handler.Last.Body!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Chat_completions_carries_the_images_of_several_results_in_one_user_turn()
    {
        var (provider, handler) = TestData.OpenAiProvider("[DONE]");

        await provider.CollectAsync(TestData.Request(messages:
        [
            ChatMessage.User("two shots"),
            new ChatMessage(MessageRole.Assistant,
            [
                new ToolCallBlock("call-1", "screenshot", "{}"),
                new ToolCallBlock("call-2", "screenshot", "{}"),
            ]),
            new ChatMessage(MessageRole.Tool,
            [
                new ToolResultBlock("call-1", [new TextBlock("first"), Png]),
                new ToolResultBlock("call-2", [new TextBlock("second"), new ImageBlock("image/png", "AAAA")]),
            ]),
        ]));

        var messages = handler.Last.Root.GetProperty("messages").EnumerateArray().ToList();
        Assert.Equal(2, messages.Count(static m => m.GetProperty("role").GetString() == "tool"));
        var followUp = messages[^1];
        Assert.Equal("user", followUp.GetProperty("role").GetString());
        Assert.Equal(3, followUp.GetProperty("content").GetArrayLength());
    }
}
