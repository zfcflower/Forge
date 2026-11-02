using System.Text;
using Forge.Abstractions;
using Forge.Core.Agents;
using Xunit;

namespace Forge.Core.Tests.Agents;

public class RetrievalToolTests
{
    private static string Workspace => Path.GetTempPath();

    [Fact]
    public async Task SearchHistory_returns_sequence_numbers_and_snippets()
    {
        var store = new InMemorySessionStore();
        var session = await store.CreateSessionAsync(Workspace, "search", CancellationToken.None);
        await store.AppendAsync(session.Id, ChatMessage.User("alpha needle here"), null, CancellationToken.None);
        await store.AppendAsync(session.Id, ChatMessage.Assistant("nothing to see"), null, CancellationToken.None);
        var tool = new SearchHistoryTool(store);

        var result = await tool.InvokeAsync(
            new ToolCall("call", SearchHistoryTool.Name, """{"query":"needle"}"""),
            new ToolContext(session.Id, Workspace),
            CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Contains("#1", result.Content, StringComparison.Ordinal);
        Assert.Contains("needle", result.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("#2", result.Content, StringComparison.Ordinal);
        Assert.Equal(ToolSideEffect.ReadOnly, tool.SideEffect);
    }

    [Fact]
    public async Task SearchHistory_reports_an_empty_result_and_rejects_bad_arguments()
    {
        var store = new InMemorySessionStore();
        var session = await store.CreateSessionAsync(Workspace, "search", CancellationToken.None);
        var tool = new SearchHistoryTool(store);

        var empty = await tool.InvokeAsync(
            new ToolCall("call", SearchHistoryTool.Name, """{"query":"missing"}"""),
            new ToolContext(session.Id, Workspace),
            CancellationToken.None);
        Assert.False(empty.IsError);
        Assert.Contains("No stored message", empty.Content, StringComparison.Ordinal);

        var invalid = await tool.InvokeAsync(
            new ToolCall("call", SearchHistoryTool.Name, "{}"),
            new ToolContext(session.Id, Workspace),
            CancellationToken.None);
        Assert.True(invalid.IsError);
    }

    [Fact]
    public async Task ReadHistory_returns_the_requested_range()
    {
        var store = new InMemorySessionStore();
        var session = await store.CreateSessionAsync(Workspace, "history", CancellationToken.None);
        await store.AppendAsync(session.Id, ChatMessage.User("first"), null, CancellationToken.None);
        await store.AppendAsync(session.Id, ChatMessage.Assistant("second"), null, CancellationToken.None);
        await store.AppendAsync(session.Id, ChatMessage.User("third"), null, CancellationToken.None);
        var tool = new ReadHistoryTool(store);

        var result = await tool.InvokeAsync(
            new ToolCall("call", ReadHistoryTool.Name, """{"from_seq":2,"to_seq":3}"""),
            new ToolContext(session.Id, Workspace),
            CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Contains("#2 assistant: second", result.Content, StringComparison.Ordinal);
        Assert.Contains("#3 user: third", result.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("first", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadArtifact_returns_the_requested_window()
    {
        var store = new InMemoryArtifactStore();
        var payload = Encoding.UTF8.GetBytes("0123456789");
        var info = await store.PutAsync("session", "output", payload, CancellationToken.None);
        var tool = new ReadArtifactTool(store);

        var result = await tool.InvokeAsync(
            new ToolCall("call", ReadArtifactTool.Name, $$"""{"artifact_id":"{{info.Id}}","offset":3,"length":4}"""),
            new ToolContext("session", Workspace),
            CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Contains("3456", result.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("0123", result.Content, StringComparison.Ordinal);

        var missing = await tool.InvokeAsync(
            new ToolCall("call", ReadArtifactTool.Name, """{"artifact_id":"nope"}"""),
            new ToolContext("session", Workspace),
            CancellationToken.None);
        Assert.True(missing.IsError);
    }

    [Fact]
    public void Schemas_are_stable_json()
    {
        var artifacts = new InMemoryArtifactStore();
        var store = new InMemorySessionStore();
        ITool[] tools =
        [
            new ReadArtifactTool(artifacts),
            new SearchHistoryTool(store),
            new ReadHistoryTool(store),
        ];

        Assert.Equal(
            new[] { "read_artifact", "read_history", "search_history" },
            tools.Select(tool => tool.Definition.Name).Order(StringComparer.Ordinal).ToArray());
        Assert.All(tools, tool =>
        {
            Assert.StartsWith("{\"type\":\"object\"", tool.Definition.InputSchemaJson, StringComparison.Ordinal);
            Assert.Equal(ToolSideEffect.ReadOnly, tool.SideEffect);
        });
    }
}
