using System.Net;
using System.Text.Json;
using Forge.Abstractions;
using Forge.Core.Providers;
using Xunit;

namespace Forge.Core.Tests.Providers;

public class OpenAiCompatibleProviderTests
{
    [Fact]
    public async Task Streams_text_thinking_usage_and_stop_reason()
    {
        var handler = new FakeHttpMessageHandler().EnqueueSse(TestData.Sse(
            """{"id":"c1","choices":[{"index":0,"delta":{"role":"assistant","content":"Hel"}}]}""",
            """{"id":"c1","choices":[{"index":0,"delta":{"reasoning_content":"plan "}}]}""",
            """{"id":"c1","choices":[{"index":0,"delta":{"content":"lo"}}]}""",
            """{"id":"c1","choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}""",
            """{"id":"c1","choices":[],"usage":{"prompt_tokens":100,"completion_tokens":20,"prompt_tokens_details":{"cached_tokens":64}}}""",
            "[DONE]"));

        var provider = new OpenAiCompatibleProvider(TestData.OpenAiConfig(), handler.CreateClient());

        var events = await provider.CollectAsync(TestData.Request());

        Assert.Equal("Hello", events.Text());
        Assert.Equal("plan ", events.Thinking());

        var completed = events.Completed();
        Assert.Equal(StopReason.EndTurn, completed.Stop);
        Assert.Equal(new TokenUsage(100, 20, 64, 0), completed.Usage);
        Assert.Equal(2, completed.Message.Content.Count);
        Assert.Equal(new ThinkingBlock("plan "), completed.Message.Content[0]);
        Assert.Equal(new TextBlock("Hello"), completed.Message.Content[1]);
        Assert.Equal(MessageRole.Assistant, completed.Message.Role);

        var request = handler.Last;
        Assert.Equal("https://api.example.test/v1/chat/completions", request.Uri.ToString());
        Assert.Equal("Bearer sk-test", request.Header("Authorization"));
        Assert.Equal("application/json", request.ContentHeaders["Content-Type"].Split(';')[0]);
    }

    [Fact]
    public async Task Assembles_tool_calls_from_fragmented_arguments()
    {
        var handler = new FakeHttpMessageHandler().EnqueueSse(TestData.Sse(
            """{"choices":[{"index":0,"delta":{"role":"assistant","tool_calls":[{"index":0,"id":"call_a","type":"function","function":{"name":"read_file","arguments":""}}]}}]}""",
            """{"choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"function":{"arguments":"{\"path\":"}}]}}]}""",
            """{"choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"function":{"arguments":"\"a.txt\"}"}}]}}]}""",
            """{"choices":[{"index":0,"delta":{},"finish_reason":"tool_calls"}]}""",
            "[DONE]"));

        var provider = new OpenAiCompatibleProvider(TestData.OpenAiConfig(), handler.CreateClient());

        var events = await provider.CollectAsync(TestData.Request());

        events.AssertFragmentedReadFileCall("call_a");
    }

    [Fact]
    public async Task Assembles_parallel_tool_calls_in_index_order()
    {
        var handler = new FakeHttpMessageHandler().EnqueueSse(TestData.Sse(
            """{"choices":[{"index":0,"delta":{"tool_calls":[{"index":1,"id":"call_b","function":{"name":"write_file","arguments":"{}"}}]}}]}""",
            """{"choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"id":"call_a","function":{"name":"read_file","arguments":"{}"}}]}}]}""",
            """{"choices":[{"index":0,"delta":{},"finish_reason":"tool_calls"}]}""",
            "[DONE]"));

        var provider = new OpenAiCompatibleProvider(TestData.OpenAiConfig(), handler.CreateClient());

        var completed = (await provider.CollectAsync(TestData.Request())).Completed();

        Assert.Equal("call_a", Assert.IsType<ToolCallBlock>(completed.Message.Content[0]).Id);
        Assert.Equal("call_b", Assert.IsType<ToolCallBlock>(completed.Message.Content[1]).Id);
    }

    [Fact]
    public async Task Serialises_request_with_stable_property_order()
    {
        var handler = new FakeHttpMessageHandler()
            .EnqueueSse(TestData.Sse("[DONE]"))
            .EnqueueSse(TestData.Sse("[DONE]"));

        var provider = new OpenAiCompatibleProvider(TestData.OpenAiConfig(), handler.CreateClient());
        var request = TestData.Request(
            model: new ModelSettings("gpt-test", ThinkingEffort.High, 1024),
            tools: [TestData.ReadFileTool()],
            messages:
            [
                ChatMessage.User("hi"),
                new ChatMessage(MessageRole.Assistant,
                [
                    new ToolCallBlock("call_a", "read_file", """{"path":"a.txt"}"""),
                ]),
                new ChatMessage(MessageRole.Tool, [new ToolResultBlock("call_a", "contents")]),
            ]);

        await provider.CollectAsync(request);
        await provider.CollectAsync(request);

        Assert.Equal(handler.Requests[0].Body, handler.Requests[1].Body);

        var body = handler.Last.Body!;
        Assert.True(body.IndexOf("\"model\":\"gpt-test\"", StringComparison.Ordinal) < body.IndexOf("\"messages\"", StringComparison.Ordinal));
        Assert.True(body.IndexOf("\"messages\"", StringComparison.Ordinal) < body.IndexOf("\"tools\"", StringComparison.Ordinal));

        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;

        Assert.Equal("high", root.GetProperty("reasoning_effort").GetString());
        Assert.True(root.GetProperty("stream").GetBoolean());
        Assert.True(root.GetProperty("stream_options").GetProperty("include_usage").GetBoolean());

        var messages = root.GetProperty("messages");
        Assert.Equal(4, messages.GetArrayLength());

        Assert.Equal("system", messages[0].GetProperty("role").GetString());
        Assert.Equal("system-prompt", messages[0].GetProperty("content").GetString());

        Assert.Equal("user", messages[1].GetProperty("role").GetString());
        Assert.Equal("hi", messages[1].GetProperty("content").GetString());

        var assistant = messages[2];
        Assert.Equal("assistant", assistant.GetProperty("role").GetString());
        Assert.Equal(JsonValueKind.Null, assistant.GetProperty("content").ValueKind);
        var toolCall = assistant.GetProperty("tool_calls")[0];
        Assert.Equal("call_a", toolCall.GetProperty("id").GetString());
        Assert.Equal("function", toolCall.GetProperty("type").GetString());
        Assert.Equal("read_file", toolCall.GetProperty("function").GetProperty("name").GetString());
        Assert.Equal("""{"path":"a.txt"}""", toolCall.GetProperty("function").GetProperty("arguments").GetString());

        Assert.Equal("tool", messages[3].GetProperty("role").GetString());
        Assert.Equal("call_a", messages[3].GetProperty("tool_call_id").GetString());
        Assert.Equal("contents", messages[3].GetProperty("content").GetString());

        var tool = root.GetProperty("tools")[0];
        Assert.Equal("function", tool.GetProperty("type").GetString());
        var definition = tool.GetProperty("function");
        Assert.Equal("read_file", definition.GetProperty("name").GetString());
        Assert.Equal("Reads a file", definition.GetProperty("description").GetString());
        Assert.Equal("object", definition.GetProperty("parameters").GetProperty("type").GetString());
        Assert.Equal("path", definition.GetProperty("parameters").GetProperty("required")[0].GetString());
    }

    [Fact]
    public async Task Omits_reasoning_effort_and_tools_when_unused()
    {
        var (provider, handler) = TestData.OpenAiProvider("[DONE]");

        await provider.CollectAsync(TestData.Request(model: new ModelSettings("gpt-test", ThinkingEffort.Off)));

        var root = handler.Last.Root;
        Assert.False(root.TryGetProperty("reasoning_effort", out _));
        Assert.False(root.TryGetProperty("tools", out _));
    }

    [Fact]
    public async Task Max_clamps_to_xhigh_because_the_wire_has_no_higher_step()
    {
        var handler = new FakeHttpMessageHandler()
            .EnqueueSse(TestData.Sse("[DONE]"))
            .EnqueueSse(TestData.Sse("[DONE]"));
        var provider = new OpenAiCompatibleProvider(TestData.OpenAiConfig(), handler.CreateClient());

        await provider.CollectAsync(TestData.Request(model: new ModelSettings("gpt-test", ThinkingEffort.XHigh)));
        var xhigh = handler.Last.Root.GetProperty("reasoning_effort").GetString();

        await provider.CollectAsync(TestData.Request(model: new ModelSettings("gpt-test", ThinkingEffort.Max)));
        var max = handler.Last.Root.GetProperty("reasoning_effort").GetString();

        Assert.Equal("xhigh", xhigh);
        Assert.Equal(xhigh, max);
    }

    [Theory]
    [InlineData(ThinkingEffort.Minimal, "minimal")]
    [InlineData(ThinkingEffort.Low, "low")]
    [InlineData(ThinkingEffort.Medium, "medium")]
    [InlineData(ThinkingEffort.High, "high")]
    [InlineData(ThinkingEffort.XHigh, "xhigh")]
    [InlineData(ThinkingEffort.Max, "xhigh")]
    public async Task Maps_thinking_effort_to_reasoning_effort(ThinkingEffort effort, string expected)
    {
        var (provider, handler) = TestData.OpenAiProvider("[DONE]");

        await provider.CollectAsync(TestData.Request(model: new ModelSettings("gpt-test", effort)));

        Assert.Equal(expected, handler.Last.Root.GetProperty("reasoning_effort").GetString());
    }

    [Fact]
    public async Task Maps_retryable_status_with_retry_after()
    {
        var handler = new FakeHttpMessageHandler().EnqueueStatus(HttpStatusCode.TooManyRequests, "slow down", TimeSpan.FromSeconds(7));
        var provider = new OpenAiCompatibleProvider(TestData.OpenAiConfig(), handler.CreateClient());

        var exception = await Assert.ThrowsAsync<ProviderException>(
            () => provider.CollectAsync(TestData.Request()));

        Assert.True(exception.Retryable);
        Assert.Equal(429, exception.StatusCode);
        Assert.Contains("slow down", exception.Message, StringComparison.Ordinal);
        Assert.True(ProviderErrors.TryGetRetryAfter(exception, out var delay));
        Assert.Equal(TimeSpan.FromSeconds(7), delay);
    }

    [Fact]
    public async Task Maps_client_error_as_non_retryable()
    {
        var handler = new FakeHttpMessageHandler().EnqueueStatus(HttpStatusCode.BadRequest, "bad request");
        var provider = new OpenAiCompatibleProvider(TestData.OpenAiConfig(), handler.CreateClient());

        var exception = await ProviderAssertions.AssertRejectedAsNonRetryableAsync(provider);
        Assert.Equal(400, exception.StatusCode);
    }

    [Fact]
    public async Task Reports_truncated_stream_as_retryable()
    {
        var content = TestData.Sse("""{"choices":[{"index":0,"delta":{"content":"partial"}}]}""");
        var handler = new FakeHttpMessageHandler().Enqueue(() => FakeHttpMessageHandler.StreamResponse(
            new ChunkedStream([System.Text.Encoding.UTF8.GetBytes(content)])));

        var provider = new OpenAiCompatibleProvider(TestData.OpenAiConfig(), handler.CreateClient());

        var exception = await Assert.ThrowsAsync<ProviderException>(() => provider.CollectAsync(TestData.Request()));
        Assert.True(exception.Retryable);
    }

    [Fact]
    public async Task Propagates_cancellation_mid_stream()
    {
        var content = TestData.Sse("""{"choices":[{"index":0,"delta":{"content":"one"}}]}""");
        var handler = new FakeHttpMessageHandler().Enqueue(() => FakeHttpMessageHandler.StreamResponse(
            new HangingStream(System.Text.Encoding.UTF8.GetBytes(content))));

        var provider = new OpenAiCompatibleProvider(TestData.OpenAiConfig(), handler.CreateClient());
        using var cts = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var evt in provider.StreamAsync(TestData.Request(), cts.Token))
            {
                Assert.IsType<TextDelta>(evt);
                await cts.CancelAsync();
            }
        });
    }
}
