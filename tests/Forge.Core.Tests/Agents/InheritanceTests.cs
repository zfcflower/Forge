using System.Text;
using System.Text.Json;
using Forge.Abstractions;
using Forge.Core.Agents;
using Forge.Core.Providers;
using Forge.Core.Storage;
using Forge.Core.Tests.Providers;
using Xunit;

namespace Forge.Core.Tests.Agents;

/// <summary>Full inheritance (section 8.2): the child's context starts with the parent's last request, byte for byte.</summary>
public class InheritanceTests
{
    private const string Workspace = "D:/work/inherit";
    private const string Note = "[Session handover] inheritance note";

    private static string[] Wire(ChatRequest request) => [.. request.Messages.Select(MessageJson.Serialize)];

    private static string[] Wire(IReadOnlyList<ChatMessage> messages) => [.. messages.Select(MessageJson.Serialize)];

    private static async Task RunAsync(AgentHost host, AgentInstance root, string text)
    {
        var events = await AgentTestHost.CollectAsync(host.RunAsync(root, text));
        Assert.DoesNotContain(events, e => e is AgentFailed);
    }

    private static string OpenAiText(string text) => TestData.Sse(
        "{\"id\":\"c\",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"content\":" + JsonSerializer.Serialize(text) + "}}]}",
        """{"id":"c","choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}""",
        """{"id":"c","choices":[],"usage":{"prompt_tokens":10,"completion_tokens":2}}""",
        "[DONE]");

    private static string OpenAiToolCall(string id, string name) => TestData.Sse(
        "{\"id\":\"c\",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"tool_calls\":[{\"index\":0,\"id\":\"" + id
            + "\",\"type\":\"function\",\"function\":{\"name\":\"" + name + "\",\"arguments\":\"{}\"}}]}}]}",
        """{"id":"c","choices":[{"index":0,"delta":{},"finish_reason":"tool_calls"}]}""",
        "[DONE]");

    [Fact]
    public async Task First_child_request_repeats_the_parents_last_request_on_the_wire()
    {
        var store = new LineageTestStore();
        var handler = new FakeHttpMessageHandler()
            .EnqueueSse(OpenAiToolCall("call_1", "echo"))
            .EnqueueSse(OpenAiText("done one"))
            .EnqueueSse(OpenAiText("done two"))
            .EnqueueSse(OpenAiText("child answer"));
        var provider = new OpenAiCompatibleProvider(TestData.OpenAiConfig(), handler.CreateClient());
        var host = AgentTestHost.Create(provider, tools: [FakeTool.Echo("echo")], store: store);

        var parent = await store.CreateSessionAsync(Workspace, "parent", CancellationToken.None);
        var parentRoot = host.CreateRoot(parent.Id, Workspace);
        await RunAsync(host, parentRoot, "task");
        await RunAsync(host, parentRoot, "more");
        var parentLast = handler.Requests[2].Root;

        var child = await store.InheritAsync(parent);
        await store.AppendAsync(child.Id, ChatMessage.User(Note), null, CancellationToken.None);
        var childRoot = await host.ResumeRootAsync(child.Id, Workspace, CancellationToken.None);
        await RunAsync(host, childRoot, "continue");
        var childFirst = handler.Requests[3].Root;

        var parentMessages = parentLast.GetProperty("messages").EnumerateArray().Select(m => m.GetRawText()).ToArray();
        var childMessages = childFirst.GetProperty("messages").EnumerateArray().Select(m => m.GetRawText()).ToArray();
        Assert.True(childMessages.Length > parentMessages.Length);
        Assert.Equal(parentMessages, childMessages[..parentMessages.Length]);
        Assert.Equal(parentLast.GetProperty("tools").GetRawText(), childFirst.GetProperty("tools").GetRawText());
        Assert.Contains("done two", childMessages[parentMessages.Length], StringComparison.Ordinal);
        Assert.Contains(Note, string.Concat(childMessages[(parentMessages.Length + 1)..]), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Multi_level_inheritance_keeps_every_level_byte_identical()
    {
        var store = new LineageTestStore();
        var provider = new FakeProvider((request, index) => Turn.Say("answer " + index));
        var host = AgentTestHost.Create(provider, store: store);

        var grand = await store.CreateSessionAsync(Workspace, "grand", CancellationToken.None);
        var grandRoot = host.CreateRoot(grand.Id, Workspace);
        await RunAsync(host, grandRoot, "g1");
        await RunAsync(host, grandRoot, "g2");

        var parent = await store.InheritAsync(grand);
        await store.AppendAsync(parent.Id, ChatMessage.User(Note + " 1"), null, CancellationToken.None);
        var parentRoot = await host.ResumeRootAsync(parent.Id, Workspace, CancellationToken.None);
        await RunAsync(host, parentRoot, "p1");
        await RunAsync(host, parentRoot, "p2");
        var parentLast = Wire(provider.Requests[^1]);

        var child = await store.InheritAsync(parent);
        Assert.Equal(grand.Id, child.LineageRootId);
        await store.AppendAsync(child.Id, ChatMessage.User(Note + " 2"), null, CancellationToken.None);
        var childRoot = await host.ResumeRootAsync(child.Id, Workspace, CancellationToken.None);
        await RunAsync(host, childRoot, "c1");
        var childFirst = Wire(provider.Requests[^1]);

        Assert.Equal(parentLast, childFirst[..parentLast.Length]);
        var texts = provider.Requests[^1].Messages.Select(Turn.Text).ToArray();
        Assert.Equal(
            ["g1", "answer 0", "g2", "answer 1", Note + " 1", "p1", "answer 2", "p2", "answer 3", Note + " 2", "c1"],
            texts);

        // The grandparent's level is cut at the parent's inheritance point: its later messages never show up.
        await RunAsync(host, grandRoot, "g3 after the handover");
        var reloaded = await host.ResumeRootAsync(child.Id, Workspace, CancellationToken.None);
        Assert.DoesNotContain(reloaded.History, m => Turn.Text(m).Contains("g3", StringComparison.Ordinal));
        Assert.Equal(childFirst[..^1], Wire(reloaded.History)[..(childFirst.Length - 1)]);
    }

    [Fact]
    public async Task Parent_that_compacted_is_inherited_with_its_summary_view()
    {
        var store = new LineageTestStore();
        var answer = new string('a', 800);
        var provider = new FakeProvider((request, index) =>
            request.SystemPrompt == SummaryCompactor.SummarizerPrompt ? Turn.Say("PARENT-SUMMARY") : Turn.Say(answer, TokenUsage.Empty));
        var options = CompactionWindows.Legacy(200, maxTurns: 4);
        var host = AgentTestHost.Create(provider, options: options, store: store);

        var parent = await store.CreateSessionAsync(Workspace, "parent", CancellationToken.None);
        var parentRoot = host.CreateRoot(parent.Id, Workspace);
        for (var turn = 1; turn <= 4; turn++)
        {
            await RunAsync(host, parentRoot, "turn " + turn);
        }

        Assert.NotEmpty(await store.GetSummariesAsync(parent.Id, CancellationToken.None));
        var parentLast = Wire(provider.Requests.Last(r => r.SystemPrompt != SummaryCompactor.SummarizerPrompt));
        Assert.Contains("PARENT-SUMMARY", parentLast[0], StringComparison.Ordinal);

        var child = await store.InheritAsync(parent);
        var childHost = AgentTestHost.Create(provider, store: store);
        var childRoot = await childHost.ResumeRootAsync(child.Id, Workspace, CancellationToken.None);
        await RunAsync(childHost, childRoot, "continue");
        var childFirst = Wire(provider.Requests[^1]);

        Assert.Equal(parentLast, childFirst[..parentLast.Length]);
    }

    [Fact]
    public async Task Inherited_level_uses_the_resume_trimming_and_ignores_later_parent_summaries()
    {
        var store = new LineageTestStore();
        var ct = CancellationToken.None;
        var parent = await store.CreateSessionAsync(Workspace, "parent", ct);
        await store.AppendAsync(parent.Id, ChatMessage.User("one"), null, ct);
        await store.AppendAsync(parent.Id, new ChatMessage(MessageRole.Assistant, [new ToolCallBlock("t1", "echo", "{}")]), null, ct);
        await store.AppendAsync(parent.Id, new ChatMessage(MessageRole.Tool, [new ToolResultBlock("t1", "pong")]), null, ct);
        await store.AppendAsync(parent.Id, ChatMessage.Assistant("two"), null, ct);
        await store.AppendAsync(parent.Id, ChatMessage.User("three"), null, ct);
        await store.AppendAsync(parent.Id, new ChatMessage(MessageRole.Assistant, [new ToolCallBlock("t2", "echo", "{}")]), null, ct);
        await store.PutSummaryAsync(new SummaryRecord(parent.Id, SummaryLayers.StageLayer, 1, 2, "STAGE-1-2", store.Tick()), ct);

        var host = AgentTestHost.Create(new FakeProvider((_, _) => Turn.Say("x")), store: store);
        var parentResumed = Wire((await host.ResumeRootAsync(parent.Id, Workspace, ct)).History);
        // Leading orphaned tool result and the trailing unanswered call are both dropped.
        Assert.Equal(3, parentResumed.Length);

        var child = await store.InheritAsync(parent);
        store.Tick();
        await store.PutSummaryAsync(new SummaryRecord(parent.Id, SummaryLayers.StageLayer, 3, 5, "LATER-STAGE", store.Tick()), ct);
        await store.PutSummaryAsync(new SummaryRecord(parent.Id, SummaryLayers.SessionLayer, 1, 6, "LATER-SESSION", store.Tick()), ct);
        await store.AppendAsync(child.Id, ChatMessage.User(Note), null, ct);

        var childHistory = Wire((await host.ResumeRootAsync(child.Id, Workspace, ct)).History);
        Assert.Equal(parentResumed, childHistory[..parentResumed.Length]);
        Assert.Equal(parentResumed.Length + 1, childHistory.Length);
        Assert.DoesNotContain(childHistory, line => line.Contains("LATER", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Summary_beyond_the_cut_is_ignored_even_when_older_than_the_child()
    {
        var store = new LineageTestStore();
        var ct = CancellationToken.None;
        var parent = await store.CreateSessionAsync(Workspace, "parent", ct);
        for (var index = 1; index <= 4; index++)
        {
            await store.AppendAsync(parent.Id, index % 2 == 1 ? ChatMessage.User("u" + index) : ChatMessage.Assistant("a" + index), null, ct);
        }

        // Child inherits up to #2; a session summary over #1-#4 already exists but reaches past the cut.
        await store.PutSummaryAsync(new SummaryRecord(parent.Id, SummaryLayers.SessionLayer, 1, 4, "TOO-FAR", store.Tick()), ct);
        var child = await store.CreateAsync(
            new SessionCreate(Workspace, "child") { InheritsFromSessionId = parent.Id, InheritsUpToSeq = 2, Origin = SessionOrigin.Full },
            ct);

        var host = AgentTestHost.Create(new FakeProvider((_, _) => Turn.Say("x")), store: store);
        var history = (await host.ResumeRootAsync(child.Id, Workspace, ct)).History;
        Assert.Equal(["u1", "a2"], history.Select(Turn.Text));
    }

    [Fact]
    public async Task Cyclic_lineage_fails_the_resume_instead_of_looping()
    {
        var store = new LineageTestStore();
        var ct = CancellationToken.None;
        var first = await store.CreateSessionAsync(Workspace, "a", ct);
        var second = await store.CreateAsync(
            new SessionCreate(Workspace, "b") { InheritsFromSessionId = first.Id, InheritsUpToSeq = 0 },
            ct);
        var self = await store.CreateAsync(new SessionCreate(Workspace, "self"), ct);
        var looping = await store.CreateAsync(
            new SessionCreate(Workspace, "loop") { InheritsFromSessionId = "missing-parent", InheritsUpToSeq = 3 },
            ct);
        var host = AgentTestHost.Create(new FakeProvider((_, _) => Turn.Say("x")), store: store);

        // A missing parent contributes nothing; an empty parent too.
        Assert.Empty((await host.ResumeRootAsync(looping.Id, Workspace, ct)).History);
        Assert.Empty((await host.ResumeRootAsync(second.Id, Workspace, ct)).History);

        var cyclic = await store.CreateAsync(
            new SessionCreate(Workspace, "cycle") { InheritsFromSessionId = self.Id, InheritsUpToSeq = 1 },
            ct);
        store.Overwrite(self with { InheritsFromSessionId = cyclic.Id, InheritsUpToSeq = 1 });
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.ResumeRootAsync(cyclic.Id, Workspace, ct));
    }

    [Fact]
    public async Task Read_artifact_reaches_an_artifact_stored_by_the_parent()
    {
        var store = new LineageTestStore();
        var artifacts = new InMemoryArtifactStore();
        var ct = CancellationToken.None;
        var parent = await store.CreateSessionAsync(Workspace, "parent", ct);
        var artifact = await artifacts.PutAsync(parent.Id, "big-output", Encoding.UTF8.GetBytes("PARENT-ARTIFACT-BODY"), ct);
        await store.AppendAsync(parent.Id, ChatMessage.User("look"), null, ct);
        await store.AppendAsync(parent.Id, ChatMessage.Assistant("stored as " + artifact.Id), null, ct);
        var child = await store.InheritAsync(parent);

        var provider = new FakeProvider((request, index) => index == 0
            ? Turn.Call(("r1", ReadArtifactTool.Name, $$"""{"artifact_id":"{{artifact.Id}}"}"""))
            : Turn.Say("read"));
        var host = AgentTestHost.Create(provider, store: store, artifacts: artifacts);
        var root = await host.ResumeRootAsync(child.Id, Workspace, ct);
        await RunAsync(host, root, "read the artifact");

        var result = provider.Requests[1].Messages[^1].Content.OfType<ToolResultBlock>().Single();
        Assert.False(result.IsError);
        Assert.Contains("PARENT-ARTIFACT-BODY", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Child_compaction_summarizes_only_its_own_messages_and_keeps_the_inherited_prefix()
    {
        var store = new LineageTestStore();
        var ct = CancellationToken.None;
        var parent = await store.CreateSessionAsync(Workspace, "parent", ct);
        await store.AppendAsync(parent.Id, ChatMessage.User("parent question"), null, ct);
        await store.AppendAsync(parent.Id, ChatMessage.Assistant(new string('p', 400)), null, ct);
        var child = await store.InheritAsync(parent);

        var answer = new string('c', 800);
        var summarizerInputs = new List<string>();
        var provider = new FakeProvider((request, index) =>
        {
            if (request.SystemPrompt == SummaryCompactor.SummarizerPrompt)
            {
                lock (summarizerInputs)
                {
                    summarizerInputs.Add(Turn.LastUserText(request));
                }

                return Turn.Say("CHILD-SUMMARY");
            }

            return Turn.Say(answer, TokenUsage.Empty);
        });
        var host = AgentTestHost.Create(provider, options: CompactionWindows.Legacy(300, maxTurns: 4), store: store);
        var root = await host.ResumeRootAsync(child.Id, Workspace, ct);
        var inherited = Wire(root.History);
        Assert.Equal(2, inherited.Length);

        var compacted = 0;
        for (var turn = 1; turn <= 4; turn++)
        {
            var events = await AgentTestHost.CollectAsync(host.RunAsync(root, "child turn " + turn));
            compacted += events.OfType<AgentCompacted>().Count();
        }

        Assert.True(compacted > 0);
        Assert.Empty(await store.GetSummariesAsync(parent.Id, ct));
        var own = await store.GetSummariesAsync(child.Id, ct);
        Assert.NotEmpty(own);
        Assert.All(own, summary => Assert.Equal(child.Id, summary.SessionId));
        Assert.All(summarizerInputs, input => Assert.DoesNotContain("parent question", input, StringComparison.Ordinal));

        var last = Wire(provider.Requests.Last(r => r.SystemPrompt != SummaryCompactor.SummarizerPrompt));
        Assert.Equal(inherited, last[..inherited.Length]);
        Assert.Contains("CHILD-SUMMARY", last[inherited.Length], StringComparison.Ordinal);

        // A restart rebuilds the same shape: inherited prefix, then the child's own summary view and tail.
        var resumed = Wire((await host.ResumeRootAsync(child.Id, Workspace, ct)).History);
        Assert.Equal(inherited, resumed[..inherited.Length]);
        Assert.Contains("CHILD-SUMMARY", resumed[inherited.Length], StringComparison.Ordinal);
    }
}
