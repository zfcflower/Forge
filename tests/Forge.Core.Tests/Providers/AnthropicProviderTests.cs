using System.Net;
using System.Text.Json;
using Forge.Abstractions;
using Forge.Core.Providers;
using Xunit;

namespace Forge.Core.Tests.Providers;

public class AnthropicProviderTests
{
    [Fact]
    public async Task Streams_thinking_signature_text_and_usage()
    {
        var handler = new FakeHttpMessageHandler().EnqueueSse(string.Concat(
            TestData.WithEvent("message_start", """{"type":"message_start","message":{"id":"msg_1","usage":{"input_tokens":11,"cache_creation_input_tokens":7,"cache_read_input_tokens":100,"output_tokens":1}}}"""),
            TestData.WithEvent("content_block_start", """{"type":"content_block_start","index":0,"content_block":{"type":"thinking","thinking":"","signature":""}}"""),
            TestData.WithEvent("content_block_delta", """{"type":"content_block_delta","index":0,"delta":{"type":"thinking_delta","thinking":"Let me "}}"""),
            TestData.WithEvent("content_block_delta", """{"type":"content_block_delta","index":0,"delta":{"type":"signature_delta","signature":"SIG"}}"""),
            TestData.WithEvent("content_block_stop", """{"type":"content_block_stop","index":0}"""),
            TestData.WithEvent("content_block_start", """{"type":"content_block_start","index":1,"content_block":{"type":"text","text":""}}"""),
            TestData.WithEvent("content_block_delta", """{"type":"content_block_delta","index":1,"delta":{"type":"text_delta","text":"Hi"}}"""),
            TestData.WithEvent("content_block_stop", """{"type":"content_block_stop","index":1}"""),
            TestData.WithEvent("message_delta", """{"type":"message_delta","delta":{"stop_reason":"end_turn"},"usage":{"output_tokens":42}}"""),
            TestData.WithEvent("message_stop", """{"type":"message_stop"}""")));

        var provider = new AnthropicProvider(TestData.AnthropicConfig(), handler.CreateClient());

        var events = await provider.CollectAsync(TestData.Request());

        Assert.Equal("Let me ", events.Thinking());
        Assert.Equal("Hi", events.Text());

        var completed = events.Completed();
        Assert.Equal(StopReason.EndTurn, completed.Stop);
        Assert.Equal(new TokenUsage(118, 42, 100, 7), completed.Usage);
        Assert.Equal(
            new ContentBlock[] { new ThinkingBlock("Let me ", "SIG"), new TextBlock("Hi") },
            completed.Message.Content);

        var request = handler.Last;
        Assert.Equal("https://api.anthropic.test/v1/messages", request.Uri.ToString());
        Assert.Equal("ak-test", request.Header("x-api-key"));
        Assert.Equal("2023-06-01", request.Header("anthropic-version"));
    }

    [Fact]
    public async Task Streams_tool_use_with_fragmented_input_json()
    {
        var handler = new FakeHttpMessageHandler().EnqueueSse(string.Concat(
            TestData.WithEvent("message_start", """{"type":"message_start","message":{"id":"msg_2","usage":{"input_tokens":5,"output_tokens":0}}}"""),
            TestData.WithEvent("content_block_start", """{"type":"content_block_start","index":0,"content_block":{"type":"tool_use","id":"toolu_1","name":"read_file","input":{}}}"""),
            TestData.WithEvent("content_block_delta", """{"type":"content_block_delta","index":0,"delta":{"type":"input_json_delta","partial_json":"{\"pa"}}"""),
            TestData.WithEvent("content_block_delta", """{"type":"content_block_delta","index":0,"delta":{"type":"input_json_delta","partial_json":"th\":\"a.txt\"}"}}"""),
            TestData.WithEvent("content_block_stop", """{"type":"content_block_stop","index":0}"""),
            TestData.WithEvent("message_delta", """{"type":"message_delta","delta":{"stop_reason":"tool_use"},"usage":{"output_tokens":19}}"""),
            TestData.WithEvent("message_stop", """{"type":"message_stop"}""")));

        var provider = new AnthropicProvider(TestData.AnthropicConfig(), handler.CreateClient());

        var events = await provider.CollectAsync(TestData.Request(tools: [TestData.ReadFileTool()]));

        events.AssertFragmentedReadFileCall("toolu_1");
    }

    [Fact]
    public async Task Places_cache_breakpoints_on_system_tools_and_message_index()
    {
        var (provider, handler) = TestData.AnthropicProvider();

        var request = TestData.Request(
            tools: [TestData.ReadFileTool(), new ToolDefinition("write_file", "Writes", """{"type":"object"}""")],
            messages:
            [
                ChatMessage.User("first"),
                ChatMessage.Assistant("second"),
                ChatMessage.User("third"),
            ],
            cache: new CacheHint(CacheSystemAndTools: true, CacheThroughMessageIndex: 2));

        await provider.CollectAsync(request);

        var root = handler.Last.Root;

        var system = root.GetProperty("system");
        Assert.Equal(JsonValueKind.Array, system.ValueKind);
        Assert.Equal("system-prompt", system[0].GetProperty("text").GetString());
        Assert.Equal("ephemeral", system[0].GetProperty("cache_control").GetProperty("type").GetString());

        var tools = root.GetProperty("tools");
        Assert.Equal(2, tools.GetArrayLength());
        Assert.False(tools[0].TryGetProperty("cache_control", out _));
        Assert.Equal("ephemeral", tools[1].GetProperty("cache_control").GetProperty("type").GetString());

        var messages = root.GetProperty("messages");
        Assert.Equal(3, messages.GetArrayLength());
        Assert.False(messages[0].GetProperty("content")[0].TryGetProperty("cache_control", out _));
        Assert.False(messages[1].GetProperty("content")[0].TryGetProperty("cache_control", out _));
        Assert.Equal("ephemeral", messages[2].GetProperty("content")[0].GetProperty("cache_control").GetProperty("type").GetString());
    }

    [Fact]
    public async Task Places_cache_breakpoint_on_last_block_of_merged_message()
    {
        var (provider, handler) = TestData.AnthropicProvider();

        var request = TestData.Request(
            messages:
            [
                ChatMessage.User("first"),
                new ChatMessage(MessageRole.Assistant, [new ToolCallBlock("toolu_1", "read_file", "{}")]),
                new ChatMessage(MessageRole.Tool, [new ToolResultBlock("toolu_1", "contents")]),
                ChatMessage.User("follow up"),
            ],
            cache: new CacheHint(CacheSystemAndTools: false, CacheThroughMessageIndex: 3));

        await provider.CollectAsync(request);

        var messages = handler.Last.Root.GetProperty("messages");
        Assert.Equal(3, messages.GetArrayLength());
        Assert.Equal("user", messages[0].GetProperty("role").GetString());
        Assert.Equal("assistant", messages[1].GetProperty("role").GetString());
        Assert.Equal("user", messages[2].GetProperty("role").GetString());

        var merged = messages[2].GetProperty("content");
        Assert.Equal(2, merged.GetArrayLength());
        Assert.Equal("tool_result", merged[0].GetProperty("type").GetString());
        Assert.False(merged[0].TryGetProperty("cache_control", out _));
        Assert.Equal("ephemeral", merged[1].GetProperty("cache_control").GetProperty("type").GetString());
    }

    [Fact]
    public async Task Maps_thinking_effort_to_budget_and_keeps_max_tokens_above_it()
    {
        var handler = new FakeHttpMessageHandler()
            .EnqueueSse(TestData.WithEvent("message_stop", """{"type":"message_stop"}"""))
            .EnqueueSse(TestData.WithEvent("message_stop", """{"type":"message_stop"}"""))
            .EnqueueSse(TestData.WithEvent("message_stop", """{"type":"message_stop"}"""))
            .EnqueueSse(TestData.WithEvent("message_stop", """{"type":"message_stop"}"""))
            .EnqueueSse(TestData.WithEvent("message_stop", """{"type":"message_stop"}"""));

        var provider = new AnthropicProvider(TestData.AnthropicConfig(), handler.CreateClient());

        await provider.CollectAsync(TestData.Request(model: new ModelSettings("claude", ThinkingEffort.Low)));
        var low = handler.Last.Root;
        Assert.Equal(2048, low.GetProperty("thinking").GetProperty("budget_tokens").GetInt32());
        Assert.Equal("enabled", low.GetProperty("thinking").GetProperty("type").GetString());
        Assert.True(low.GetProperty("max_tokens").GetInt32() > 2048);

        await provider.CollectAsync(TestData.Request(model: new ModelSettings("claude", ThinkingEffort.Medium)));
        Assert.Equal(8192, handler.Last.Root.GetProperty("thinking").GetProperty("budget_tokens").GetInt32());

        await provider.CollectAsync(TestData.Request(model: new ModelSettings("claude", ThinkingEffort.High)));
        var high = handler.Last.Root;
        Assert.Equal(24576, high.GetProperty("thinking").GetProperty("budget_tokens").GetInt32());
        Assert.True(high.GetProperty("max_tokens").GetInt32() > 24576);

        await provider.CollectAsync(TestData.Request(model: new ModelSettings("claude", ThinkingEffort.Off, 4096)));
        var off = handler.Last.Root;
        Assert.False(off.TryGetProperty("thinking", out _));
        Assert.Equal(4096, off.GetProperty("max_tokens").GetInt32());
    }

    [Fact]
    public async Task Maps_the_outer_thinking_steps_to_budgets_above_the_existing_ones()
    {
        var handler = new FakeHttpMessageHandler()
            .EnqueueSse(TestData.WithEvent("message_stop", """{"type":"message_stop"}"""))
            .EnqueueSse(TestData.WithEvent("message_stop", """{"type":"message_stop"}"""))
            .EnqueueSse(TestData.WithEvent("message_stop", """{"type":"message_stop"}"""));

        var provider = new AnthropicProvider(TestData.AnthropicConfig(), handler.CreateClient());

        await provider.CollectAsync(TestData.Request(model: new ModelSettings("claude", ThinkingEffort.Minimal)));
        var minimal = handler.Last.Root;
        Assert.Equal(1024, minimal.GetProperty("thinking").GetProperty("budget_tokens").GetInt32());
        Assert.True(minimal.GetProperty("max_tokens").GetInt32() > 1024);

        await provider.CollectAsync(TestData.Request(model: new ModelSettings("claude", ThinkingEffort.XHigh)));
        var xhigh = handler.Last.Root;
        Assert.Equal(32768, xhigh.GetProperty("thinking").GetProperty("budget_tokens").GetInt32());
        Assert.True(xhigh.GetProperty("max_tokens").GetInt32() > 32768);

        await provider.CollectAsync(TestData.Request(model: new ModelSettings("claude", ThinkingEffort.Max)));
        var max = handler.Last.Root;
        Assert.Equal(49152, max.GetProperty("thinking").GetProperty("budget_tokens").GetInt32());
        Assert.True(max.GetProperty("max_tokens").GetInt32() > 49152);
    }

    [Fact]
    public async Task Does_not_send_unsigned_thinking_blocks_back()
    {
        var (provider, handler) = TestData.AnthropicProvider();

        var request = TestData.Request(messages:
        [
            ChatMessage.User("hi"),
            new ChatMessage(MessageRole.Assistant,
            [
                new ThinkingBlock("unsigned"),
                new ThinkingBlock("signed", "SIG"),
                new TextBlock("answer"),
            ]),
        ]);

        await provider.CollectAsync(request);

        var content = handler.Last.Root.GetProperty("messages")[1].GetProperty("content");
        Assert.Equal(2, content.GetArrayLength());
        Assert.Equal("thinking", content[0].GetProperty("type").GetString());
        Assert.Equal("signed", content[0].GetProperty("thinking").GetString());
        Assert.Equal("SIG", content[0].GetProperty("signature").GetString());
        Assert.Equal("answer", content[1].GetProperty("text").GetString());
    }

    [Fact]
    public async Task Sends_tool_results_in_user_role_messages()
    {
        var (provider, handler) = TestData.AnthropicProvider();

        var request = TestData.Request(messages:
        [
            ChatMessage.User("read it"),
            new ChatMessage(MessageRole.Assistant, [new ToolCallBlock("toolu_9", "read_file", """{"path":"a.txt"}""")]),
            new ChatMessage(MessageRole.Tool, [new ToolResultBlock("toolu_9", "denied", IsError: true)]),
        ]);

        await provider.CollectAsync(request);

        var messages = handler.Last.Root.GetProperty("messages");
        Assert.Equal(3, messages.GetArrayLength());

        var assistant = messages[1];
        Assert.Equal("assistant", assistant.GetProperty("role").GetString());
        var toolUse = assistant.GetProperty("content")[0];
        Assert.Equal("tool_use", toolUse.GetProperty("type").GetString());
        Assert.Equal("toolu_9", toolUse.GetProperty("id").GetString());
        Assert.Equal("a.txt", toolUse.GetProperty("input").GetProperty("path").GetString());

        var toolResult = messages[2];
        Assert.Equal("user", toolResult.GetProperty("role").GetString());
        var block = toolResult.GetProperty("content")[0];
        Assert.Equal("tool_result", block.GetProperty("type").GetString());
        Assert.Equal("toolu_9", block.GetProperty("tool_use_id").GetString());
        Assert.Equal("denied", block.GetProperty("content").GetString());
        Assert.True(block.GetProperty("is_error").GetBoolean());
    }

    [Fact]
    public async Task Serialises_request_identically_across_calls()
    {
        var handler = new FakeHttpMessageHandler()
            .EnqueueSse(TestData.WithEvent("message_stop", """{"type":"message_stop"}"""))
            .EnqueueSse(TestData.WithEvent("message_stop", """{"type":"message_stop"}"""));

        var provider = new AnthropicProvider(TestData.AnthropicConfig(), handler.CreateClient());
        var request = TestData.Request(
            model: new ModelSettings("claude", ThinkingEffort.Medium),
            tools: [TestData.ReadFileTool()],
            messages: [ChatMessage.User("hello"), ChatMessage.Assistant("world")],
            cache: new CacheHint(true, 1));

        await provider.CollectAsync(request);
        await provider.CollectAsync(request);

        Assert.Equal(handler.Requests[0].Body, handler.Requests[1].Body);
    }

    [Fact]
    public async Task Maps_retryable_stream_error_event()
    {
        var handler = new FakeHttpMessageHandler().EnqueueSse(string.Concat(
            TestData.WithEvent("message_start", """{"type":"message_start","message":{"usage":{"input_tokens":3}}}"""),
            TestData.WithEvent("error", """{"type":"error","error":{"type":"overloaded_error","message":"Overloaded"}}""")));

        var provider = new AnthropicProvider(TestData.AnthropicConfig(), handler.CreateClient());

        var exception = await Assert.ThrowsAsync<ProviderException>(() => provider.CollectAsync(TestData.Request()));
        Assert.True(exception.Retryable);
        Assert.Contains("Overloaded", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Maps_overloaded_http_status_as_retryable()
    {
        var handler = new FakeHttpMessageHandler().EnqueueStatus(HttpStatusCode.ServiceUnavailable, "overloaded");
        var provider = new AnthropicProvider(TestData.AnthropicConfig(), handler.CreateClient());

        var exception = await Assert.ThrowsAsync<ProviderException>(() => provider.CollectAsync(TestData.Request()));

        Assert.True(exception.Retryable);
        Assert.Equal(503, exception.StatusCode);
    }

    [Fact]
    public async Task Reports_stream_without_message_stop_as_truncated()
    {
        var handler = new FakeHttpMessageHandler().EnqueueSse(string.Concat(
            TestData.WithEvent("message_start", """{"type":"message_start","message":{"usage":{"input_tokens":3}}}"""),
            TestData.WithEvent("content_block_start", """{"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}"""),
            TestData.WithEvent("content_block_delta", """{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"cut"}}""")));

        var provider = new AnthropicProvider(TestData.AnthropicConfig(), handler.CreateClient());

        var exception = await Assert.ThrowsAsync<ProviderException>(() => provider.CollectAsync(TestData.Request()));
        Assert.True(exception.Retryable);
    }
}
