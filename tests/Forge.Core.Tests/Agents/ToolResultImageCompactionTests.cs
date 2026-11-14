using Forge.Abstractions;
using Forge.Core.Agents;
using Xunit;

namespace Forge.Core.Tests.Agents;

/// <summary>A tool result carrying an image: the summarizer prompt gets the standard placeholder, never the base64.</summary>
public sealed class ToolResultImageCompactionTests
{
    private const string Workspace = "D:/work/tool-image";
    private static readonly CancellationToken Ct = CancellationToken.None;

    [Fact]
    public async Task A_tool_result_image_reaches_the_summarizer_as_a_placeholder()
    {
        var data = Convert.ToBase64String(new byte[3_000]);
        var store = new InMemorySessionStore();
        var provider = new FakeProvider((request, _) =>
            request.SystemPrompt == SummaryCompactor.SummarizerPrompt ? Turn.Say("S") : Turn.Say("ok"));
        var compactor = new SummaryCompactor(store, _ => provider, new ApproximateTokenCounter());
        var host = AgentTestHost.Create(provider, store: store, compactor: compactor);

        var session = await store.CreateSessionAsync(Workspace, "screenshot", Ct);
        await store.AppendAsync(session.Id, new ChatMessage(MessageRole.Assistant,
        [
            new TextBlock("taking a screenshot"),
            new ToolCallBlock("c1", "screenshot", "{}"),
        ]), null, Ct);
        await store.AppendAsync(session.Id, new ChatMessage(MessageRole.Tool,
            [new ToolResultBlock("c1", [new TextBlock("here it is"), new ImageBlock("image/png", data)])]), null, Ct);
        await store.AppendAsync(session.Id, ChatMessage.User("tail"), null, Ct);
        var root = await host.ResumeRootAsync(session.Id, Workspace, Ct);

        await compactor.CompactAsync(root, new CompactionRequest(1, 10, CompactionReason.Manual), Ct);

        var request = Assert.Single(provider.Requests, call => call.SystemPrompt == SummaryCompactor.SummarizerPrompt);
        var prompt = Turn.Text(Assert.Single(request.Messages));
        Assert.Contains("[Assistant]: taking a screenshot", prompt, StringComparison.Ordinal);
        Assert.Contains("[Assistant tool calls]: screenshot()", prompt, StringComparison.Ordinal);
        Assert.Contains("[Tool result]: here it is\n[image: image/png, 3 KB]", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain(data, prompt, StringComparison.Ordinal);
    }
}
