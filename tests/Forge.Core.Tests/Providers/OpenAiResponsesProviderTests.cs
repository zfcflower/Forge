using System.Net;
using System.Text.Json;
using Forge.Abstractions;
using Forge.Core.Providers;
using Xunit;

namespace Forge.Core.Tests.Providers;

public class OpenAiResponsesProviderTests
{
    [Fact]
    public async Task Serialises_the_item_shaped_request()
    {
        var handler = new FakeHttpMessageHandler().EnqueueSse(TestData.Sse(Completed(10, 5)));
        var provider = new OpenAiResponsesProvider(Config(), handler.CreateClient());

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

        var recorded = handler.Last;
        Assert.Equal("https://api.example.test/v1/responses", recorded.Uri.ToString());
        Assert.Equal("Bearer sk-test", recorded.Header("Authorization"));
        Assert.Equal("application/json", recorded.ContentHeaders["Content-Type"].Split(';')[0]);

        var body = recorded.Body!;
        Assert.True(body.IndexOf("\"model\"", StringComparison.Ordinal) < body.IndexOf("\"instructions\"", StringComparison.Ordinal));
        Assert.True(body.IndexOf("\"instructions\"", StringComparison.Ordinal) < body.IndexOf("\"input\"", StringComparison.Ordinal));
        Assert.True(body.IndexOf("\"input\"", StringComparison.Ordinal) < body.IndexOf("\"tools\"", StringComparison.Ordinal));

        var root = recorded.Root;
        Assert.Equal("gpt-test", root.GetProperty("model").GetString());
        Assert.Equal("system-prompt", root.GetProperty("instructions").GetString());
        Assert.True(root.GetProperty("stream").GetBoolean());
        Assert.False(root.GetProperty("store").GetBoolean());
        Assert.Equal(1024, root.GetProperty("max_output_tokens").GetInt32());
        Assert.Equal("high", root.GetProperty("reasoning").GetProperty("effort").GetString());

        var input = root.GetProperty("input");
        Assert.Equal(3, input.GetArrayLength());

        var user = input[0];
        Assert.Equal("user", user.GetProperty("role").GetString());
        Assert.Equal("input_text", user.GetProperty("content")[0].GetProperty("type").GetString());
        Assert.Equal("hi", user.GetProperty("content")[0].GetProperty("text").GetString());

        var call = input[1];
        Assert.Equal("function_call", call.GetProperty("type").GetString());
        Assert.Equal("call_a", call.GetProperty("call_id").GetString());
        Assert.Equal("read_file", call.GetProperty("name").GetString());
        Assert.Equal("""{"path":"a.txt"}""", call.GetProperty("arguments").GetString());

        var result = input[2];
        Assert.Equal("function_call_output", result.GetProperty("type").GetString());
        Assert.Equal("call_a", result.GetProperty("call_id").GetString());
        Assert.Equal("contents", result.GetProperty("output").GetString());

        // A Responses function tool is flat: no nested "function" object as Chat Completions needs.
        var tool = root.GetProperty("tools")[0];
        Assert.Equal("function", tool.GetProperty("type").GetString());
        Assert.Equal("read_file", tool.GetProperty("name").GetString());
        Assert.Equal("Reads a file", tool.GetProperty("description").GetString());
        Assert.Equal("object", tool.GetProperty("parameters").GetProperty("type").GetString());
        Assert.Equal("path", tool.GetProperty("parameters").GetProperty("required")[0].GetString());
        Assert.False(tool.TryGetProperty("function", out _));
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
        var handler = new FakeHttpMessageHandler().EnqueueSse(TestData.Sse(Completed(1, 1)));
        var provider = new OpenAiResponsesProvider(Config(), handler.CreateClient());

        await provider.CollectAsync(TestData.Request(model: new ModelSettings("gpt-test", effort)));

        Assert.Equal(expected, handler.Last.Root.GetProperty("reasoning").GetProperty("effort").GetString());
    }

    [Fact]
    public async Task Omits_reasoning_tools_and_an_empty_prompt_when_unused()
    {
        var handler = new FakeHttpMessageHandler().EnqueueSse(TestData.Sse(Completed(1, 1)));
        var provider = new OpenAiResponsesProvider(Config(), handler.CreateClient());

        await provider.CollectAsync(TestData.Request(
            model: new ModelSettings("gpt-test", ThinkingEffort.Off),
            system: string.Empty));

        var root = handler.Last.Root;
        Assert.False(root.TryGetProperty("reasoning", out _));
        Assert.False(root.TryGetProperty("tools", out _));
        Assert.False(root.TryGetProperty("instructions", out _));
        Assert.False(root.TryGetProperty("max_output_tokens", out _));
    }

    [Fact]
    public async Task Sends_the_callers_headers_alongside_the_bearer_token()
    {
        var handler = new FakeHttpMessageHandler().EnqueueSse(TestData.Sse(Completed(1, 1)));
        var config = new ProviderConfig(
            "chatgpt",
            ProviderFactory.OpenAiResponsesKind,
            "https://chatgpt.com/backend-api",
            "session-token",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["chatgpt-account-id"] = "acct_1" });

        var provider = new OpenAiResponsesProvider(config, handler.CreateClient());

        await provider.CollectAsync(TestData.Request());

        var request = handler.Last;
        Assert.Equal("https://chatgpt.com/backend-api/responses", request.Uri.ToString());
        Assert.Equal("acct_1", request.Header("chatgpt-account-id"));
        Assert.Equal("Bearer session-token", request.Header("Authorization"));
    }

    [Fact]
    public async Task Streams_text_and_usage_from_the_terminal_event()
    {
        var handler = new FakeHttpMessageHandler().EnqueueSse(TestData.Sse(
            """{"type":"response.created","response":{"id":"resp_1","status":"in_progress"}}""",
            """{"type":"response.output_item.added","output_index":0,"item":{"type":"message","id":"msg_1","role":"assistant","content":[]}}""",
            """{"type":"response.output_text.delta","item_id":"msg_1","output_index":0,"delta":"Hel"}""",
            """{"type":"response.output_text.delta","item_id":"msg_1","output_index":0,"delta":"lo"}""",
            """{"type":"response.output_item.done","output_index":0,"item":{"type":"message","id":"msg_1","role":"assistant","content":[{"type":"output_text","text":"Hello"}]}}""",
            """{"type":"response.completed","response":{"id":"resp_1","status":"completed","usage":{"input_tokens":100,"output_tokens":20,"total_tokens":120,"input_tokens_details":{"cached_tokens":64}}}}"""));

        var provider = new OpenAiResponsesProvider(Config(), handler.CreateClient());

        var events = await provider.CollectAsync(TestData.Request());

        Assert.Equal("Hello", events.Text());

        var completed = events.Completed();
        Assert.Equal(StopReason.EndTurn, completed.Stop);
        Assert.Equal(new TokenUsage(100, 20, 64, 0), completed.Usage);
        Assert.Equal(new TextBlock("Hello"), Assert.Single(completed.Message.Content));
        Assert.Equal(MessageRole.Assistant, completed.Message.Role);
    }

    [Fact]
    public async Task Assembles_a_tool_call_from_argument_deltas()
    {
        var handler = new FakeHttpMessageHandler().EnqueueSse(TestData.Sse(
            """{"type":"response.output_item.added","output_index":0,"item":{"type":"function_call","id":"fc_1","call_id":"call_a","name":"read_file","arguments":""}}""",
            """{"type":"response.function_call_arguments.delta","item_id":"fc_1","output_index":0,"delta":"{\"path\":"}""",
            """{"type":"response.function_call_arguments.delta","item_id":"fc_1","output_index":0,"delta":"\"a.txt\"}"}""",
            """{"type":"response.output_item.done","output_index":0,"item":{"type":"function_call","id":"fc_1","call_id":"call_a","name":"read_file","arguments":"{\"path\":\"a.txt\"}"}}""",
            Completed(30, 7)));

        var provider = new OpenAiResponsesProvider(Config(), handler.CreateClient());

        var events = await provider.CollectAsync(TestData.Request());

        var started = Assert.Single(events.OfType<ToolCallStarted>());
        Assert.Equal("call_a", started.Id);
        Assert.Equal("read_file", started.Name);

        var deltas = events.OfType<ToolCallArgumentsDelta>().ToList();
        Assert.Equal(2, deltas.Count);
        Assert.All(deltas, d => Assert.Equal("call_a", d.Id));
        Assert.Equal("{\"path\":\"a.txt\"}", string.Concat(deltas.Select(d => d.Json)));

        var completed = events.Completed();
        Assert.Equal(StopReason.ToolUse, completed.Stop);
        Assert.Equal(
            new ToolCallBlock("call_a", "read_file", "{\"path\":\"a.txt\"}"),
            Assert.Single(completed.Message.Content));
    }

    [Fact]
    public async Task Emits_the_missing_tail_when_the_done_event_carries_more_than_the_deltas()
    {
        var handler = new FakeHttpMessageHandler().EnqueueSse(TestData.Sse(
            """{"type":"response.output_item.added","output_index":0,"item":{"type":"function_call","id":"fc_1","call_id":"call_a","name":"read_file","arguments":""}}""",
            """{"type":"response.function_call_arguments.delta","item_id":"fc_1","output_index":0,"delta":"{\"path\":"}""",
            """{"type":"response.function_call_arguments.done","item_id":"fc_1","output_index":0,"arguments":"{\"path\":\"a.txt\"}"}""",
            Completed(30, 7)));

        var provider = new OpenAiResponsesProvider(Config(), handler.CreateClient());

        var events = await provider.CollectAsync(TestData.Request());

        Assert.Equal("{\"path\":\"a.txt\"}", string.Concat(events.OfType<ToolCallArgumentsDelta>().Select(d => d.Json)));
        Assert.Equal(
            new ToolCallBlock("call_a", "read_file", "{\"path\":\"a.txt\"}"),
            Assert.Single(events.Completed().Message.Content));
    }

    [Fact]
    public async Task Maps_max_output_tokens_incompletion_to_max_tokens()
    {
        var handler = new FakeHttpMessageHandler().EnqueueSse(TestData.Sse(
            """{"type":"response.output_text.delta","item_id":"msg_1","output_index":0,"delta":"truncated"}""",
            """{"type":"response.incomplete","response":{"status":"incomplete","incomplete_details":{"reason":"max_output_tokens"},"usage":{"input_tokens":5,"output_tokens":4000}}}"""));

        var provider = new OpenAiResponsesProvider(Config(), handler.CreateClient());

        var completed = (await provider.CollectAsync(TestData.Request())).Completed();

        Assert.Equal(StopReason.MaxTokens, completed.Stop);
        Assert.Equal(new TokenUsage(5, 4000), completed.Usage);
    }

    [Fact]
    public async Task Maps_an_error_event_to_a_provider_error_with_the_server_message()
    {
        var handler = new FakeHttpMessageHandler().EnqueueSse(TestData.Sse(
            """{"type":"response.output_text.delta","item_id":"msg_1","output_index":0,"delta":"partial"}""",
            """{"type":"error","code":"rate_limit_exceeded","message":"slow down","sequence_number":3}"""));

        var provider = new OpenAiResponsesProvider(Config(), handler.CreateClient());

        var exception = await Assert.ThrowsAsync<ProviderException>(() => provider.CollectAsync(TestData.Request()));

        Assert.Contains("slow down", exception.Message, StringComparison.Ordinal);
        Assert.Contains("rate_limit_exceeded", exception.Message, StringComparison.Ordinal);
        Assert.True(exception.Retryable);
    }

    [Fact]
    public async Task Maps_a_failed_response_to_a_provider_error()
    {
        var handler = new FakeHttpMessageHandler().EnqueueSse(TestData.Sse(
            """{"type":"response.failed","response":{"status":"failed","error":{"code":"server_error","message":"model exploded"}}}"""));

        var provider = new OpenAiResponsesProvider(Config(), handler.CreateClient());

        var exception = await Assert.ThrowsAsync<ProviderException>(() => provider.CollectAsync(TestData.Request()));

        Assert.Contains("model exploded", exception.Message, StringComparison.Ordinal);
        Assert.True(exception.Retryable);
    }

    [Fact]
    public async Task Maps_a_json_error_body_on_a_400()
    {
        var handler = new FakeHttpMessageHandler().EnqueueStatus(
            HttpStatusCode.BadRequest,
            """{"error":{"message":"tools[0] is not valid","type":"invalid_request_error"}}""");

        var provider = new OpenAiResponsesProvider(Config(), handler.CreateClient());

        var exception = await Assert.ThrowsAsync<ProviderException>(() => provider.CollectAsync(TestData.Request()));

        Assert.False(exception.Retryable);
        Assert.Equal(400, exception.StatusCode);
        Assert.Contains("tools[0] is not valid", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reports_a_stream_without_a_terminal_event_as_retryable()
    {
        var content = TestData.Sse(
            """{"type":"response.output_text.delta","item_id":"msg_1","output_index":0,"delta":"partial"}""");
        var handler = new FakeHttpMessageHandler().Enqueue(() => FakeHttpMessageHandler.StreamResponse(
            new ChunkedStream([System.Text.Encoding.UTF8.GetBytes(content)])));

        var provider = new OpenAiResponsesProvider(Config(), handler.CreateClient());

        var exception = await Assert.ThrowsAsync<ProviderException>(() => provider.CollectAsync(TestData.Request()));
        Assert.True(exception.Retryable);
    }

    [Fact]
    public async Task Unparseable_chunk_is_a_non_retryable_malformed_payload()
    {
        var handler = new FakeHttpMessageHandler().EnqueueSse(TestData.Sse("{not json"));
        var provider = new OpenAiResponsesProvider(Config(), handler.CreateClient());

        var exception = await Assert.ThrowsAsync<ProviderException>(() => provider.CollectAsync(TestData.Request()));

        Assert.False(exception.Retryable);
        Assert.Contains("malformed", exception.Message, StringComparison.Ordinal);
        Assert.IsAssignableFrom<System.Text.Json.JsonException>(exception.InnerException);
    }

    [Fact]
    public async Task Non_object_chunk_is_a_non_retryable_malformed_payload()
    {
        // Valid JSON that is not an event: the adapter still has to refuse it instead of retrying a broken stream.
        var handler = new FakeHttpMessageHandler().EnqueueSse(TestData.Sse("[1,2]"));
        var provider = new OpenAiResponsesProvider(Config(), handler.CreateClient());

        var exception = await Assert.ThrowsAsync<ProviderException>(() => provider.CollectAsync(TestData.Request()));

        Assert.False(exception.Retryable);
        Assert.Contains("malformed", exception.Message, StringComparison.Ordinal);
        Assert.Contains("[1,2]", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_data_line_split_across_two_buffer_fills_still_parses()
    {
        // One SSE line longer than the reader's 8192-byte buffer, so the line has to be assembled across two fills of it.
        var text = new string('a', 9000);
        var content = TestData.Sse(
            "{\"type\":\"response.output_text.delta\",\"item_id\":\"msg_1\",\"output_index\":0,\"delta\":\""
            + text
            + "\"}",
            Completed(5, 5));
        var handler = new FakeHttpMessageHandler().Enqueue(() => FakeHttpMessageHandler.StreamResponse(
            new ChunkedStream([System.Text.Encoding.UTF8.GetBytes(content)])));

        var provider = new OpenAiResponsesProvider(Config(), handler.CreateClient());

        var events = await provider.CollectAsync(TestData.Request());

        Assert.Equal(text, events.Text());
        Assert.Equal(StopReason.EndTurn, events.Completed().Stop);
    }

    [Fact]
    public async Task Propagates_cancellation_mid_stream()
    {
        var content = TestData.Sse(
            """{"type":"response.output_text.delta","item_id":"msg_1","output_index":0,"delta":"one"}""");
        var handler = new FakeHttpMessageHandler().Enqueue(() => FakeHttpMessageHandler.StreamResponse(
            new HangingStream(System.Text.Encoding.UTF8.GetBytes(content))));

        var provider = new OpenAiResponsesProvider(Config(), handler.CreateClient());
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

    private static ProviderConfig Config(string baseUrl = "https://api.example.test/v1") =>
        new("responses", ProviderFactory.OpenAiResponsesKind, baseUrl, "sk-test");

    /// <summary>A terminal <c>response.completed</c> event carrying usage.</summary>
    private static string Completed(int inputTokens, int outputTokens) =>
        "{\"type\":\"response.completed\",\"response\":{\"id\":\"resp_1\",\"status\":\"completed\",\"usage\":{\"input_tokens\":"
        + inputTokens
        + ",\"output_tokens\":"
        + outputTokens
        + "}}}";
}
