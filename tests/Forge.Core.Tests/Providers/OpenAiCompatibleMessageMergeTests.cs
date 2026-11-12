using Forge.Abstractions;
using Forge.Core.Providers;
using Xunit;

namespace Forge.Core.Tests.Providers;

public class OpenAiCompatibleMessageMergeTests
{
    [Fact]
    public async Task Merges_consecutive_user_messages_with_blank_line_between_paragraphs()
    {
        var (provider, handler) = TestData.OpenAiProvider("[DONE]");

        var request = TestData.Request(messages:
        [
            ChatMessage.User("first"),
            ChatMessage.User("second"),
            ChatMessage.User("third"),
        ]);

        await provider.CollectAsync(request);

        var messages = handler.Last.Root.GetProperty("messages");
        Assert.Equal(2, messages.GetArrayLength());
        Assert.Equal("system", messages[0].GetProperty("role").GetString());
        Assert.Equal("user", messages[1].GetProperty("role").GetString());
        Assert.Equal("first\n\nsecond\n\nthird", messages[1].GetProperty("content").GetString());
    }

    [Fact]
    public async Task Keeps_tool_results_separate_and_emits_them_before_merged_user_text()
    {
        var (provider, handler) = TestData.OpenAiProvider("[DONE]");

        var request = TestData.Request(messages:
        [
            new ChatMessage(MessageRole.User,
            [
                new TextBlock("first"),
                new ToolResultBlock("call_a", "A"),
            ]),
            new ChatMessage(MessageRole.User,
            [
                new TextBlock("second"),
                new ToolResultBlock("call_b", "B"),
            ]),
        ]);

        await provider.CollectAsync(request);

        var messages = handler.Last.Root.GetProperty("messages");
        Assert.Equal(4, messages.GetArrayLength());
        Assert.Equal(
            new[] { "system", "tool", "tool", "user" },
            Enumerable.Range(0, 4).Select(i => messages[i].GetProperty("role").GetString()));

        Assert.Equal("call_a", messages[1].GetProperty("tool_call_id").GetString());
        Assert.Equal("A", messages[1].GetProperty("content").GetString());
        Assert.Equal("call_b", messages[2].GetProperty("tool_call_id").GetString());
        Assert.Equal("B", messages[2].GetProperty("content").GetString());

        Assert.Equal("user", messages[3].GetProperty("role").GetString());
        Assert.Equal("first\n\nsecond", messages[3].GetProperty("content").GetString());
    }

    [Fact]
    public async Task Does_not_merge_user_and_tool_messages()
    {
        var (provider, handler) = TestData.OpenAiProvider("[DONE]");

        var request = TestData.Request(messages:
        [
            ChatMessage.User("read it"),
            new ChatMessage(MessageRole.Assistant, [new ToolCallBlock("call_a", "read_file", """{"path":"a"}""")]),
            new ChatMessage(MessageRole.Tool, [new ToolResultBlock("call_a", "A")]),
            ChatMessage.User("thanks"),
        ]);

        await provider.CollectAsync(request);

        var messages = handler.Last.Root.GetProperty("messages");
        Assert.Equal(5, messages.GetArrayLength());
        Assert.Equal(
            new[] { "system", "user", "assistant", "tool", "user" },
            Enumerable.Range(0, 5).Select(i => messages[i].GetProperty("role").GetString()));
        Assert.Equal("read it", messages[1].GetProperty("content").GetString());
        Assert.Equal("call_a", messages[3].GetProperty("tool_call_id").GetString());
        Assert.Equal("A", messages[3].GetProperty("content").GetString());
        Assert.Equal("thanks", messages[4].GetProperty("content").GetString());
    }

    [Fact]
    public async Task Merges_assistant_text_and_tool_calls_in_order()
    {
        var (provider, handler) = TestData.OpenAiProvider("[DONE]");

        var request = TestData.Request(messages:
        [
            ChatMessage.User("go"),
            new ChatMessage(MessageRole.Assistant,
            [
                new TextBlock("plan"),
                new ToolCallBlock("call_a", "read_file", """{"path":"a"}"""),
            ]),
            new ChatMessage(MessageRole.Assistant,
            [
                new ToolCallBlock("call_b", "write_file", """{"path":"b"}"""),
                new TextBlock("done"),
            ]),
        ]);

        await provider.CollectAsync(request);

        var messages = handler.Last.Root.GetProperty("messages");
        Assert.Equal(3, messages.GetArrayLength());
        var assistant = messages[2];
        Assert.Equal("assistant", assistant.GetProperty("role").GetString());
        Assert.Equal("plan\n\ndone", assistant.GetProperty("content").GetString());

        var toolCalls = assistant.GetProperty("tool_calls");
        Assert.Equal(2, toolCalls.GetArrayLength());
        Assert.Equal("call_a", toolCalls[0].GetProperty("id").GetString());
        Assert.Equal("read_file", toolCalls[0].GetProperty("function").GetProperty("name").GetString());
        Assert.Equal("call_b", toolCalls[1].GetProperty("id").GetString());
        Assert.Equal("write_file", toolCalls[1].GetProperty("function").GetProperty("name").GetString());
    }

    [Fact]
    public async Task Omits_empty_messages_when_merging()
    {
        var (provider, handler) = TestData.OpenAiProvider("[DONE]");

        var request = TestData.Request(messages:
        [
            new ChatMessage(MessageRole.Assistant, []),
            ChatMessage.User("hi"),
            new ChatMessage(MessageRole.User, []),
            ChatMessage.User("again"),
        ]);

        await provider.CollectAsync(request);

        var messages = handler.Last.Root.GetProperty("messages");
        Assert.Equal(2, messages.GetArrayLength());
        Assert.Equal("system", messages[0].GetProperty("role").GetString());
        Assert.Equal("user", messages[1].GetProperty("role").GetString());
        Assert.Equal("hi\n\nagain", messages[1].GetProperty("content").GetString());
    }

    [Fact]
    public async Task Emits_only_tool_message_when_user_content_has_only_tool_results()
    {
        var (provider, handler) = TestData.OpenAiProvider("[DONE]");

        var request = TestData.Request(messages:
        [
            ChatMessage.User("go"),
            new ChatMessage(MessageRole.Assistant, [new ToolCallBlock("call_a", "read_file", """{"path":"a"}""")]),
            new ChatMessage(MessageRole.User, [new ToolResultBlock("call_a", "A")]),
        ]);

        await provider.CollectAsync(request);

        var messages = handler.Last.Root.GetProperty("messages");
        Assert.Equal(4, messages.GetArrayLength());
        Assert.Equal(
            new[] { "system", "user", "assistant", "tool" },
            Enumerable.Range(0, 4).Select(i => messages[i].GetProperty("role").GetString()));
        Assert.Equal("call_a", messages[3].GetProperty("tool_call_id").GetString());
        Assert.Equal("A", messages[3].GetProperty("content").GetString());
    }

    [Fact]
    public async Task Serialises_merged_history_identically_across_calls()
    {
        var handler = new FakeHttpMessageHandler()
            .EnqueueSse(TestData.Sse("[DONE]"))
            .EnqueueSse(TestData.Sse("[DONE]"));

        var provider = new OpenAiCompatibleProvider(TestData.OpenAiConfig(), handler.CreateClient());
        var request = TestData.Request(messages:
        [
            ChatMessage.User("first"),
            ChatMessage.User("second"),
            new ChatMessage(MessageRole.Assistant,
            [
                new TextBlock("plan"),
                new ToolCallBlock("call_a", "read_file", """{"path":"a"}"""),
            ]),
            new ChatMessage(MessageRole.Tool, [new ToolResultBlock("call_a", "A")]),
            ChatMessage.User("thanks"),
        ]);

        await provider.CollectAsync(request);
        await provider.CollectAsync(request);

        Assert.Equal(handler.Requests[0].Body, handler.Requests[1].Body);
    }
}
