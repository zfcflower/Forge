using Forge.Abstractions;
using Forge.Core.Providers;
using Xunit;

namespace Forge.Core.Tests.Providers;

public class OpenAiCompatibleRegressionTests
{
    [Fact]
    public async Task Max_output_tokens_is_sent_as_max_completion_tokens_only_when_configured()
    {
        var handler = new FakeHttpMessageHandler()
            .EnqueueSse(TestData.Sse("[DONE]"))
            .EnqueueSse(TestData.Sse("[DONE]"))
            .EnqueueSse(TestData.Sse("[DONE]"));
        var provider = new OpenAiCompatibleProvider(TestData.OpenAiConfig(), handler.CreateClient());
        var capped = TestData.Request(model: new ModelSettings("gpt-test", ThinkingEffort.Off, 512));

        await provider.CollectAsync(capped);
        await provider.CollectAsync(capped);

        Assert.Equal(handler.Requests[0].Body, handler.Requests[1].Body);
        var root = handler.Requests[0].Root;
        Assert.Equal(512, root.GetProperty("max_completion_tokens").GetInt32());
        Assert.False(root.TryGetProperty("max_tokens", out _));

        await provider.CollectAsync(TestData.Request(model: new ModelSettings("gpt-test")));

        Assert.False(handler.Last.Root.TryGetProperty("max_completion_tokens", out _));
        Assert.False(handler.Last.Root.TryGetProperty("max_tokens", out _));
    }

    [Fact]
    public async Task Tool_results_follow_the_tool_calls_before_the_user_text_they_arrived_with()
    {
        var (provider, handler) = TestData.OpenAiProvider("[DONE]");

        var request = TestData.Request(messages:
        [
            ChatMessage.User("read both"),
            new ChatMessage(MessageRole.Assistant,
            [
                new ToolCallBlock("call_a", "read_file", """{"path":"a"}"""),
                new ToolCallBlock("call_b", "read_file", """{"path":"b"}"""),
            ]),
            new ChatMessage(MessageRole.User,
            [
                new TextBlock("also note this"),
                new ToolResultBlock("call_a", "A"),
                new ToolResultBlock("call_b", "B"),
            ]),
        ]);

        await provider.CollectAsync(request);

        var messages = handler.Last.Root.GetProperty("messages");
        Assert.Equal(6, messages.GetArrayLength());
        Assert.Equal(
            new[] { "system", "user", "assistant", "tool", "tool", "user" },
            Enumerable.Range(0, 6).Select(i => messages[i].GetProperty("role").GetString()));
        Assert.Equal("call_a", messages[3].GetProperty("tool_call_id").GetString());
        Assert.Equal("A", messages[3].GetProperty("content").GetString());
        Assert.Equal("call_b", messages[4].GetProperty("tool_call_id").GetString());
        Assert.Equal("B", messages[4].GetProperty("content").GetString());
        Assert.Equal("also note this", messages[5].GetProperty("content").GetString());
    }

    [Fact]
    public async Task Non_object_chunk_is_a_non_retryable_malformed_payload()
    {
        var handler = new FakeHttpMessageHandler().EnqueueSse(TestData.Sse("[1,2,3]", "[DONE]"));
        var provider = new OpenAiCompatibleProvider(TestData.OpenAiConfig(), handler.CreateClient());

        var exception = await ProviderAssertions.AssertRejectedAsNonRetryableAsync(provider);
        Assert.Contains("malformed", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unparseable_chunk_is_a_non_retryable_malformed_payload()
    {
        var handler = new FakeHttpMessageHandler().EnqueueSse(TestData.Sse("{not json", "[DONE]"));
        var provider = new OpenAiCompatibleProvider(TestData.OpenAiConfig(), handler.CreateClient());

        var exception = await ProviderAssertions.AssertRejectedAsNonRetryableAsync(provider);
        Assert.IsAssignableFrom<System.Text.Json.JsonException>(exception.InnerException);
    }

    [Fact]
    public async Task Choices_that_are_not_an_array_are_a_non_retryable_malformed_payload()
    {
        var handler = new FakeHttpMessageHandler().EnqueueSse(TestData.Sse("""{"choices":"oops"}""", "[DONE]"));
        var provider = new OpenAiCompatibleProvider(TestData.OpenAiConfig(), handler.CreateClient());

        await ProviderAssertions.AssertRejectedAsNonRetryableAsync(provider);
    }

    [Fact]
    public async Task Choice_entries_that_are_not_objects_are_a_non_retryable_malformed_payload()
    {
        var handler = new FakeHttpMessageHandler().EnqueueSse(TestData.Sse("""{"choices":["oops"]}""", "[DONE]"));
        var provider = new OpenAiCompatibleProvider(TestData.OpenAiConfig(), handler.CreateClient());

        await ProviderAssertions.AssertRejectedAsNonRetryableAsync(provider);
    }
}
