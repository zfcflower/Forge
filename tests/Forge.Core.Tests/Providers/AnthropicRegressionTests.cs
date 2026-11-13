using Forge.Abstractions;
using Forge.Core.Providers;
using Xunit;

namespace Forge.Core.Tests.Providers;

public class AnthropicRegressionTests
{
    [Fact]
    public async Task System_messages_are_folded_into_the_top_level_system_once_and_never_sent_as_messages()
    {
        var (provider, handler) = TestData.AnthropicProvider();

        var request = TestData.Request(
            messages:
            [
                new ChatMessage(MessageRole.System, [new TextBlock("sys-note")]),
                ChatMessage.User("a"),
                ChatMessage.Assistant("b"),
                ChatMessage.User("c"),
            ],
            cache: new CacheHint(CacheSystemAndTools: false, CacheThroughMessageIndex: 3));

        await provider.CollectAsync(request);

        var body = handler.Last.Body!;
        Assert.Equal(1, body.Split("sys-note").Length - 1);

        var root = handler.Last.Root;
        Assert.Equal("system-prompt\n\nsys-note", root.GetProperty("system")[0].GetProperty("text").GetString());

        var messages = root.GetProperty("messages");
        Assert.Equal(3, messages.GetArrayLength());
        Assert.Equal(
            new[] { "user", "assistant", "user" },
            Enumerable.Range(0, 3).Select(i => messages[i].GetProperty("role").GetString()));

        Assert.False(messages[0].GetProperty("content")[0].TryGetProperty("cache_control", out _));
        Assert.False(messages[1].GetProperty("content")[0].TryGetProperty("cache_control", out _));
        Assert.Equal("c", messages[2].GetProperty("content")[0].GetProperty("text").GetString());
        Assert.Equal("ephemeral", messages[2].GetProperty("content")[0].GetProperty("cache_control").GetProperty("type").GetString());
    }

    [Fact]
    public async Task Tool_results_lead_the_user_turn_and_cache_control_lands_on_its_last_block()
    {
        var (provider, handler) = TestData.AnthropicProvider();

        var request = TestData.Request(
            messages:
            [
                ChatMessage.User("go"),
                new ChatMessage(MessageRole.Assistant, [new ToolCallBlock("toolu_1", "read_file", """{"path":"a"}""")]),
                new ChatMessage(MessageRole.User,
                [
                    new TextBlock("after the result"),
                    new ToolResultBlock("toolu_1", "contents"),
                ]),
            ],
            cache: new CacheHint(CacheSystemAndTools: false, CacheThroughMessageIndex: 2));

        await provider.CollectAsync(request);

        var messages = handler.Last.Root.GetProperty("messages");
        Assert.Equal(3, messages.GetArrayLength());

        var content = messages[2].GetProperty("content");
        Assert.Equal(2, content.GetArrayLength());
        Assert.Equal("tool_result", content[0].GetProperty("type").GetString());
        Assert.Equal("toolu_1", content[0].GetProperty("tool_use_id").GetString());
        Assert.False(content[0].TryGetProperty("cache_control", out _));
        Assert.Equal("text", content[1].GetProperty("type").GetString());
        Assert.Equal("after the result", content[1].GetProperty("text").GetString());
        Assert.Equal("ephemeral", content[1].GetProperty("cache_control").GetProperty("type").GetString());
    }

    [Fact]
    public async Task Redacted_thinking_is_parsed_as_an_opaque_block_and_replayed_as_redacted_thinking()
    {
        var handler = new FakeHttpMessageHandler()
            .EnqueueSse(string.Concat(
                TestData.WithEvent("message_start", """{"type":"message_start","message":{"usage":{"input_tokens":2,"output_tokens":0}}}"""),
                TestData.WithEvent("content_block_start", """{"type":"content_block_start","index":0,"content_block":{"type":"redacted_thinking","data":"ENC=="}}"""),
                TestData.WithEvent("content_block_stop", """{"type":"content_block_stop","index":0}"""),
                TestData.WithEvent("content_block_start", """{"type":"content_block_start","index":1,"content_block":{"type":"text","text":""}}"""),
                TestData.WithEvent("content_block_delta", """{"type":"content_block_delta","index":1,"delta":{"type":"text_delta","text":"ok"}}"""),
                TestData.WithEvent("content_block_stop", """{"type":"content_block_stop","index":1}"""),
                TestData.WithEvent("message_delta", """{"type":"message_delta","delta":{"stop_reason":"end_turn"},"usage":{"output_tokens":1}}"""),
                TestData.WithEvent("message_stop", """{"type":"message_stop"}""")))
            .EnqueueSse(TestData.WithEvent("message_stop", """{"type":"message_stop"}"""));
        var provider = new AnthropicProvider(TestData.AnthropicConfig(), handler.CreateClient());

        var completed = (await provider.CollectAsync(TestData.Request())).Completed();

        Assert.Equal(
            new ContentBlock[] { new ThinkingBlock(string.Empty, "redacted:ENC=="), new TextBlock("ok") },
            completed.Message.Content);

        await provider.CollectAsync(TestData.Request(messages:
        [
            ChatMessage.User("question"),
            new ChatMessage(MessageRole.Assistant,
            [
                new ThinkingBlock("unsigned reasoning"),
                new ThinkingBlock(string.Empty, "redacted:ENC=="),
                new TextBlock("ok"),
            ]),
        ]));

        var content = handler.Last.Root.GetProperty("messages")[1].GetProperty("content");
        Assert.Equal(2, content.GetArrayLength());
        Assert.Equal("redacted_thinking", content[0].GetProperty("type").GetString());
        Assert.Equal("ENC==", content[0].GetProperty("data").GetString());
        Assert.False(content[0].TryGetProperty("signature", out _));
        Assert.False(content[0].TryGetProperty("thinking", out _));
        Assert.Equal("text", content[1].GetProperty("type").GetString());
    }

    [Fact]
    public async Task Non_object_chunk_is_a_non_retryable_malformed_payload()
    {
        var handler = new FakeHttpMessageHandler().EnqueueSse(TestData.Sse("[]"));
        var provider = new AnthropicProvider(TestData.AnthropicConfig(), handler.CreateClient());

        var exception = await ProviderAssertions.AssertRejectedAsNonRetryableAsync(provider);
        Assert.Contains("malformed", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unparseable_chunk_is_a_non_retryable_malformed_payload()
    {
        var handler = new FakeHttpMessageHandler().EnqueueSse(TestData.WithEvent("content_block_delta", "{\"type\":"));
        var provider = new AnthropicProvider(TestData.AnthropicConfig(), handler.CreateClient());

        await ProviderAssertions.AssertRejectedAsNonRetryableAsync(provider);
    }

    [Fact]
    public async Task Message_start_without_a_message_object_is_a_non_retryable_malformed_payload()
    {
        var handler = new FakeHttpMessageHandler().EnqueueSse(string.Concat(
            TestData.WithEvent("message_start", """{"type":"message_start"}"""),
            TestData.WithEvent("message_stop", """{"type":"message_stop"}""")));
        var provider = new AnthropicProvider(TestData.AnthropicConfig(), handler.CreateClient());

        await ProviderAssertions.AssertRejectedAsNonRetryableAsync(provider);
    }
}
