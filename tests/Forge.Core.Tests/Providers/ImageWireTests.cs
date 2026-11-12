using System.Text.Json;
using Forge.Abstractions;
using Forge.Core.Providers;
using Xunit;

namespace Forge.Core.Tests.Providers;

/// <summary>An attached <see cref="ImageBlock"/> in each of the three wire protocols.</summary>
public sealed class ImageWireTests
{
    private const string Data = "iVBORw0KGgo=";
    private static readonly ImageBlock Png = new("image/png", Data);
    private static readonly ImageBlock Jpeg = new("image/jpeg", "/9j/4AAQ");

    private static ChatMessage UserWithImages(string text, params ImageBlock[] images) =>
        new(MessageRole.User, [new TextBlock(text), .. images]);

    // ------------------------------------------------------------------ Anthropic

    [Fact]
    public async Task Anthropic_writes_a_base64_image_block_in_the_user_turn_in_order()
    {
        var handler = new FakeHttpMessageHandler().EnqueueSse(TestData.WithEvent("message_stop", """{"type":"message_stop"}"""));
        var provider = new AnthropicProvider(TestData.AnthropicConfig(), handler.CreateClient());

        await provider.CollectAsync(TestData.Request(messages: [UserWithImages("what is this?", Png, Jpeg)]));

        var content = Assert.Single(handler.Last.Root.GetProperty("messages").EnumerateArray()).GetProperty("content");
        Assert.Equal(3, content.GetArrayLength());
        Assert.Equal("text", content[0].GetProperty("type").GetString());
        Assert.Equal("what is this?", content[0].GetProperty("text").GetString());

        var first = content[1];
        Assert.Equal("image", first.GetProperty("type").GetString());
        var source = first.GetProperty("source");
        Assert.Equal("base64", source.GetProperty("type").GetString());
        Assert.Equal("image/png", source.GetProperty("media_type").GetString());
        Assert.Equal(Data, source.GetProperty("data").GetString());
        Assert.Equal("image/jpeg", content[2].GetProperty("source").GetProperty("media_type").GetString());
    }

    [Fact]
    public async Task Anthropic_drops_an_image_block_outside_a_user_message()
    {
        var handler = new FakeHttpMessageHandler().EnqueueSse(TestData.WithEvent("message_stop", """{"type":"message_stop"}"""));
        var provider = new AnthropicProvider(TestData.AnthropicConfig(), handler.CreateClient());

        await provider.CollectAsync(TestData.Request(messages:
        [
            ChatMessage.User("hi"),
            new ChatMessage(MessageRole.Assistant, [new TextBlock("reply"), Png]),
        ]));

        var body = handler.Last.Body!;
        Assert.DoesNotContain("\"image\"", body, StringComparison.Ordinal);
        Assert.DoesNotContain(Data, body, StringComparison.Ordinal);
        var assistant = handler.Last.Root.GetProperty("messages")[1].GetProperty("content");
        Assert.Equal(1, assistant.GetArrayLength());
    }

    // ------------------------------------------------------------------ OpenAI-compatible

    [Fact]
    public async Task OpenAi_compatible_switches_a_user_turn_with_images_to_a_content_array()
    {
        var handler = new FakeHttpMessageHandler().EnqueueSse(TestData.Sse("[DONE]"));
        var provider = new OpenAiCompatibleProvider(TestData.OpenAiConfig(), handler.CreateClient());

        await provider.CollectAsync(TestData.Request(messages: [UserWithImages("look", Png, Jpeg)]));

        var user = handler.Last.Root.GetProperty("messages")[1];
        Assert.Equal("user", user.GetProperty("role").GetString());
        var content = user.GetProperty("content");
        Assert.Equal(JsonValueKind.Array, content.ValueKind);
        Assert.Equal(3, content.GetArrayLength());
        Assert.Equal("text", content[0].GetProperty("type").GetString());
        Assert.Equal("look", content[0].GetProperty("text").GetString());
        Assert.Equal("image_url", content[1].GetProperty("type").GetString());
        Assert.Equal("data:image/png;base64," + Data, content[1].GetProperty("image_url").GetProperty("url").GetString());
        Assert.Equal("data:image/jpeg;base64,/9j/4AAQ", content[2].GetProperty("image_url").GetProperty("url").GetString());
    }

    [Fact]
    public async Task OpenAi_compatible_keeps_the_plain_string_form_for_a_text_only_user_turn()
    {
        var handler = new FakeHttpMessageHandler().EnqueueSse(TestData.Sse("[DONE]"));
        var provider = new OpenAiCompatibleProvider(TestData.OpenAiConfig(), handler.CreateClient());

        await provider.CollectAsync(TestData.Request(messages: [ChatMessage.User("hello")]));

        Assert.Contains("{\"role\":\"user\",\"content\":\"hello\"}", handler.Last.Body!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpenAi_compatible_merges_consecutive_user_turns_and_collects_all_their_images()
    {
        var handler = new FakeHttpMessageHandler().EnqueueSse(TestData.Sse("[DONE]"));
        var provider = new OpenAiCompatibleProvider(TestData.OpenAiConfig(), handler.CreateClient());

        await provider.CollectAsync(TestData.Request(messages:
        [
            ChatMessage.User("first"),
            new ChatMessage(MessageRole.User, [Png]),
        ]));

        var messages = handler.Last.Root.GetProperty("messages");
        Assert.Equal(2, messages.GetArrayLength());
        var content = messages[1].GetProperty("content");
        Assert.Equal(2, content.GetArrayLength());
        Assert.Equal("first", content[0].GetProperty("text").GetString());
        Assert.Equal("image_url", content[1].GetProperty("type").GetString());
    }

    [Fact]
    public async Task OpenAi_compatible_sends_an_image_only_user_turn_without_a_text_part()
    {
        var handler = new FakeHttpMessageHandler().EnqueueSse(TestData.Sse("[DONE]"));
        var provider = new OpenAiCompatibleProvider(TestData.OpenAiConfig(), handler.CreateClient());

        await provider.CollectAsync(TestData.Request(messages: [new ChatMessage(MessageRole.User, [Png])]));

        var content = handler.Last.Root.GetProperty("messages")[1].GetProperty("content");
        var part = Assert.Single(content.EnumerateArray());
        Assert.Equal("image_url", part.GetProperty("type").GetString());
    }

    [Fact]
    public async Task OpenAi_compatible_never_puts_an_assistant_image_on_the_wire()
    {
        var handler = new FakeHttpMessageHandler().EnqueueSse(TestData.Sse("[DONE]"));
        var provider = new OpenAiCompatibleProvider(TestData.OpenAiConfig(), handler.CreateClient());

        await provider.CollectAsync(TestData.Request(messages:
        [
            ChatMessage.User("hi"),
            new ChatMessage(MessageRole.Assistant, [new TextBlock("reply"), Png]),
        ]));

        Assert.DoesNotContain(Data, handler.Last.Body!, StringComparison.Ordinal);
        Assert.Equal("reply", handler.Last.Root.GetProperty("messages")[2].GetProperty("content").GetString());
    }

    // ------------------------------------------------------------------ Responses

    [Fact]
    public async Task Responses_writes_input_text_then_input_image_parts_in_one_user_item()
    {
        var handler = new FakeHttpMessageHandler().EnqueueSse(TestData.Sse(Completed));
        var provider = new OpenAiResponsesProvider(ResponsesConfig(), handler.CreateClient());

        await provider.CollectAsync(TestData.Request(messages: [UserWithImages("look", Png)]));

        var item = Assert.Single(handler.Last.Root.GetProperty("input").EnumerateArray());
        Assert.Equal("user", item.GetProperty("role").GetString());
        var content = item.GetProperty("content");
        Assert.Equal(2, content.GetArrayLength());
        Assert.Equal("input_text", content[0].GetProperty("type").GetString());
        Assert.Equal("look", content[0].GetProperty("text").GetString());
        Assert.Equal("input_image", content[1].GetProperty("type").GetString());
        Assert.Equal("data:image/png;base64," + Data, content[1].GetProperty("image_url").GetString());
    }

    [Fact]
    public async Task Responses_sends_an_image_only_user_turn_and_skips_assistant_images()
    {
        var handler = new FakeHttpMessageHandler().EnqueueSse(TestData.Sse(Completed));
        var provider = new OpenAiResponsesProvider(ResponsesConfig(), handler.CreateClient());

        await provider.CollectAsync(TestData.Request(messages:
        [
            new ChatMessage(MessageRole.User, [Jpeg]),
            new ChatMessage(MessageRole.Assistant, [new TextBlock("reply"), Png]),
        ]));

        var input = handler.Last.Root.GetProperty("input");
        Assert.Equal(2, input.GetArrayLength());
        var image = Assert.Single(input[0].GetProperty("content").EnumerateArray());
        Assert.Equal("input_image", image.GetProperty("type").GetString());
        Assert.DoesNotContain(Data, handler.Last.Body!, StringComparison.Ordinal);
    }

    private const string Completed =
        "{\"type\":\"response.completed\",\"response\":{\"id\":\"resp_1\",\"status\":\"completed\",\"usage\":{\"input_tokens\":1,\"output_tokens\":1}}}";

    private static ProviderConfig ResponsesConfig() =>
        new("responses", ProviderFactory.OpenAiResponsesKind, "https://api.example.test/v1", "sk-test");
}
