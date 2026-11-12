using Forge.Abstractions;
using Forge.Core.Agents;
using Xunit;

namespace Forge.Core.Tests.Agents;

public class AgentLoopTests
{
    private static string Workspace => Path.GetTempPath();

    [Fact]
    public async Task SimpleTurn_streams_text_and_persists_the_exchange()
    {
        var provider = new FakeProvider((request, index) =>
        [
            new ThinkingDelta("thinking"),
            new TextDelta("Hel"),
            new TextDelta("lo"),
            new MessageCompleted(ChatMessage.Assistant("Hello"), new TokenUsage(7, 3), StopReason.EndTurn),
        ]);
        var host = AgentTestHost.Create(provider);
        var root = host.CreateRoot("session", Workspace);

        var events = await AgentTestHost.CollectAsync(host.RunAsync(root, "hi"));

        Assert.Equal("Hello", string.Concat(events.OfType<AgentTextDelta>().Select(delta => delta.Text)));
        Assert.Equal("thinking", Assert.Single(events.OfType<AgentThinkingDelta>()).Text);
        var completed = Assert.Single(events.OfType<AgentTurnCompleted>());
        Assert.Equal(StopReason.EndTurn, completed.Stop);
        Assert.Equal(new TokenUsage(7, 3), completed.Usage);
        Assert.Empty(events.OfType<AgentFailed>());
        Assert.Equal(new[] { MessageRole.User, MessageRole.Assistant }, root.History.Select(message => message.Role).ToArray());
        Assert.Equal("hi", Turn.Text(root.History[0]));
        Assert.Equal("Hello", Turn.Text(root.History[1]));
    }

    [Fact]
    public async Task ToolCalls_run_and_are_written_back_as_one_tool_message()
    {
        var provider = new FakeProvider((request, index) => index == 0
            ? Turn.Call(("call-1", "echo", """{"text":"ping"}"""), ("call-2", "echo", """{"text":"pong"}"""))
            : Turn.Say("done"));
        var host = AgentTestHost.Create(provider, [FakeTool.Echo("echo")]);
        var (root, events) = await AgentTestHost.RunRootAsync(host);

        Assert.Empty(events.OfType<AgentFailed>());
        Assert.Equal(2, events.OfType<AgentToolStarted>().Count());
        Assert.Equal(2, events.OfType<AgentToolFinished>().Count());
        Assert.Equal(
            new[] { MessageRole.User, MessageRole.Assistant, MessageRole.Tool, MessageRole.Assistant },
            root.History.Select(message => message.Role).ToArray());

        var results = root.History[2].Content.OfType<ToolResultBlock>().ToArray();
        Assert.Equal(new[] { "call-1", "call-2" }, results.Select(result => result.CallId).ToArray());
        Assert.All(results, result => Assert.Equal("pong", result.Content));
        Assert.Contains(provider.Requests[0].Tools, tool => tool.Name == "echo");
        Assert.Contains(provider.Requests[1].Messages, message => message.Role == MessageRole.Tool);
        Assert.Equal("done", Turn.Text(root.History[^1]));
    }

    [Fact]
    public async Task ReadOnlyCalls_overlap_while_write_calls_stay_sequential()
    {
        var recorder = new ConcurrencyRecorder();
        var tools = new ITool[]
        {
            Timed("write-first", ToolSideEffect.Write, "w1"),
            Timed("read-a", ToolSideEffect.ReadOnly, "a"),
            Timed("read-b", ToolSideEffect.ReadOnly, "b"),
            Timed("write-last", ToolSideEffect.Write, "w2"),
        };

        var provider = new FakeProvider((request, index) => index == 0
            ? Turn.Call(
                ("1", "write-first", "{}"),
                ("2", "read-a", "{}"),
                ("3", "read-b", "{}"),
                ("4", "write-last", "{}"))
            : Turn.Say("done"));
        var host = AgentTestHost.Create(provider, tools, policy: new DefaultApprovalPolicy { TrustedDirectories = [Workspace] });
        var (root, events) = await AgentTestHost.RunRootAsync(host);

        Assert.Empty(events.OfType<AgentFailed>());
        Assert.Equal(2, recorder.PeakReaders);
        Assert.Equal(1, recorder.PeakWriters);

        var timeline = recorder.Timeline.ToArray();
        Assert.Equal("+write-first", timeline[0]);
        Assert.Equal("-write-first", timeline[1]);
        Assert.Contains("+read-a", timeline[2..4]);
        Assert.Contains("+read-b", timeline[2..4]);
        Assert.Contains("-read-a", timeline[4..6]);
        Assert.Contains("-read-b", timeline[4..6]);
        Assert.Equal("+write-last", timeline[6]);
        Assert.Equal("-write-last", timeline[7]);

        var results = root.History[2].Content.OfType<ToolResultBlock>().Select(result => result.Content).ToArray();
        Assert.Equal(new[] { "w1", "a", "b", "w2" }, results);

        FakeTool Timed(string name, ToolSideEffect sideEffect, string reply) =>
            new(name, sideEffect, async (_, _, ct) =>
            {
                await recorder.RunAsync(name, sideEffect, ct);
                return new ToolResult(reply);
            });
    }

    [Fact]
    public async Task DeniedCalls_become_error_results_and_never_invoke_the_tool()
    {
        var invoked = 0;
        var tool = new FakeTool("write-file", ToolSideEffect.Write, (_, _, _) =>
        {
            Interlocked.Increment(ref invoked);
            return new ValueTask<ToolResult>(new ToolResult("written"));
        });
        var policy = new DefaultApprovalPolicy
        {
            ToolRules = new Dictionary<string, ApprovalMode>(StringComparer.Ordinal) { ["write-file"] = ApprovalMode.Deny },
        };
        var provider = AgentTestScripts.OneToolCallThenSay("write-file", "ok", "call-1");
        var host = AgentTestHost.Create(provider, [tool], policy: policy);
        var (root, events) = await AgentTestHost.RunRootAsync(host);

        Assert.Equal(0, invoked);
        Assert.Single(events.OfType<AgentApprovalDenied>());
        var result = Assert.Single(root.History[2].Content.OfType<ToolResultBlock>());
        Assert.True(result.IsError);
        Assert.Contains("denied", result.Content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AskWithoutHandler_is_denied()
    {
        var provider = AgentTestScripts.OneToolCallThenSay("write-file", "ok", "call-1");
        var host = AgentTestHost.Create(provider, [FakeTool.Echo("write-file", ToolSideEffect.Write)]);
        var (root, events) = await AgentTestHost.RunRootAsync(host);

        Assert.Single(events.OfType<AgentApprovalDenied>());
        Assert.True(Assert.Single(root.History[2].Content.OfType<ToolResultBlock>()).IsError);
    }

    [Fact]
    public async Task ApprovalHandler_is_serialized_across_concurrent_calls()
    {
        var gate = new ApprovalGate();
        var tools = new ITool[]
        {
            new FakeTool("read-a", ToolSideEffect.ReadOnly, async (_, _, ct) =>
            {
                await Task.Delay(30, ct);
                return new ToolResult("a");
            }),
            new FakeTool("read-b", ToolSideEffect.ReadOnly, async (_, _, ct) =>
            {
                await Task.Delay(30, ct);
                return new ToolResult("b");
            }),
        };
        var provider = new FakeProvider((request, index) => index == 0
            ? Turn.Call(("1", "read-a", "{}"), ("2", "read-b", "{}"))
            : Turn.Say("ok"));
        var host = AgentTestHost.Create(
            provider,
            tools,
            policy: new DefaultApprovalPolicy { AutoAllowReadOnly = false },
            handler: gate);
        var (root, events) = await AgentTestHost.RunRootAsync(host);

        Assert.Empty(events.OfType<AgentFailed>());
        Assert.Equal(1, gate.Peak);
        Assert.Equal(2, gate.Requests);
        Assert.Equal(2, root.History[2].Content.OfType<ToolResultBlock>().Count(result => !result.IsError));
    }

    [Fact]
    public async Task Cancellation_fills_every_pending_call_with_a_cancelled_result()
    {
        using var cancellation = new CancellationTokenSource();
        var store = new InMemorySessionStore();
        var session = await store.CreateSessionAsync(Workspace, "cancel", CancellationToken.None);
        var provider = new FakeProvider((request, index) => Turn.Call(
            ("call-1", "hang", "{}"),
            ("call-2", "hang", "{}"),
            ("call-3", "hang", "{}")));
        var tool = new FakeTool("hang", ToolSideEffect.ReadOnly, async (_, _, ct) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return new ToolResult("never");
        });
        var host = AgentTestHost.Create(provider, [tool], store: store);
        var root = host.CreateRoot(session.Id, Workspace);

        var events = new List<AgentEvent>();
        await foreach (var value in host.RunAsync(root, "go", cancellation.Token))
        {
            events.Add(value);
            if (value is AgentToolStarted)
            {
                await cancellation.CancelAsync();
            }
        }

        Assert.Empty(events.OfType<AgentFailed>());
        var toolMessage = root.History[^1];
        Assert.Equal(MessageRole.Tool, toolMessage.Role);
        var results = toolMessage.Content.OfType<ToolResultBlock>().ToArray();
        Assert.Equal(3, results.Length);
        Assert.All(results, result => Assert.Equal("Cancelled.", result.Content));
        Assert.All(results, result => Assert.True(result.IsError));
        Assert.Equal("Cancelled.", store.Appended(session.Id)[^1].Content.OfType<ToolResultBlock>().First().Content);
    }

    [Fact]
    public async Task LargeToolOutput_moves_into_the_artifact_store()
    {
        var artifacts = new InMemoryArtifactStore();
        var payload = new string('x', 500);
        var provider = AgentTestScripts.OneToolCallThenSay("big", "ok", "call-1");
        var options = new AgentOptions { ArtifactThresholdChars = 100, ArtifactPreviewChars = 20 };
        var host = AgentTestHost.Create(
            provider,
            [new FakeTool("big", ToolSideEffect.ReadOnly, (_, _, _) => new ValueTask<ToolResult>(new ToolResult(payload)))],
            options: options,
            artifacts: artifacts);
        var root = host.CreateRoot("session", Workspace);

        await AgentTestHost.CollectAsync(host.RunAsync(root, "go"));

        var result = Assert.Single(root.History[2].Content.OfType<ToolResultBlock>());
        Assert.StartsWith(new string('x', 20), result.Content);
        Assert.Contains("500 bytes", result.Content, StringComparison.Ordinal);
        Assert.Contains("read_artifact", result.Content, StringComparison.Ordinal);
        Assert.DoesNotContain(payload, result.Content, StringComparison.Ordinal);

        var start = result.Content.IndexOf('\'') + 1;
        var id = result.Content[start..result.Content.IndexOf('\'', start)];
        Assert.Equal(payload, artifacts.Text(id));
    }

    [Fact]
    public async Task MaxTurns_is_enforced()
    {
        var provider = new FakeProvider((request, index) => Turn.Call(("call", "echo", "{}")));
        var host = AgentTestHost.Create(provider, [FakeTool.Echo("echo")], options: new AgentOptions { MaxTurns = 2 });
        var (root, events) = await AgentTestHost.RunRootAsync(host);

        var failure = Assert.Single(events.OfType<AgentFailed>());
        Assert.Contains("did not finish within 2 turns", failure.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ThrowingTools_become_error_results_without_failing_the_run()
    {
        var provider = new FakeProvider((request, index) => index == 0
            ? Turn.Call(("call-1", "boom", "{}"), ("call-2", "echo", "{}"))
            : Turn.Say("done"));
        var tools = new ITool[]
        {
            new FakeTool("boom", ToolSideEffect.ReadOnly, (_, _, _) => throw new InvalidOperationException("kaboom")),
            FakeTool.Echo("echo"),
        };
        var host = AgentTestHost.Create(provider, tools);
        var (root, events) = await AgentTestHost.RunRootAsync(host);

        Assert.Empty(events.OfType<AgentFailed>());
        var results = root.History[2].Content.OfType<ToolResultBlock>().ToArray();
        Assert.True(results[0].IsError);
        Assert.Contains("kaboom", results[0].Content, StringComparison.Ordinal);
        Assert.False(results[1].IsError);
        Assert.Equal("done", Turn.Text(root.History[^1]));
    }

    [Fact]
    public async Task Abandoning_the_enumeration_cancels_the_run()
    {
        var provider = new HangingProvider();
        var host = AgentTestHost.Create(provider);
        var root = host.CreateRoot("session", Workspace);

        var enumerator = host.RunAsync(root, "go").GetAsyncEnumerator();
        Assert.True(await enumerator.MoveNextAsync());
        Assert.IsType<AgentTextDelta>(enumerator.Current);

        await enumerator.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(new[] { MessageRole.User }, root.History.Select(message => message.Role).ToArray());
    }

    private sealed class HangingProvider : IChatProvider
    {
        public string Id => "hanging";

        public async IAsyncEnumerable<ChatStreamEvent> StreamAsync(
            ChatRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            yield return new TextDelta("first");
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            yield return new MessageCompleted(ChatMessage.Assistant("late"), TokenUsage.Empty, StopReason.EndTurn);
        }
    }

    private sealed class ApprovalGate : IApprovalHandler
    {
        private int inside;
        private int peak;
        private int requests;

        public int Peak => Volatile.Read(ref peak);

        public int Requests => Volatile.Read(ref requests);

        public async ValueTask<ApprovalDecision> RequestAsync(ApprovalRequest request, CancellationToken ct)
        {
            Interlocked.Increment(ref requests);
            var current = Interlocked.Increment(ref inside);
            try
            {
                var observed = Volatile.Read(ref peak);
                while (current > observed && Interlocked.CompareExchange(ref peak, current, observed) != observed)
                {
                    observed = Volatile.Read(ref peak);
                }

                await Task.Delay(20, ct);
                return ApprovalDecision.Allow;
            }
            finally
            {
                Interlocked.Decrement(ref inside);
            }
        }
    }

    [Fact]
    public async Task ProviderFailure_surfaces_as_AgentFailed()
    {
        var provider = new FakeProvider((Func<ChatRequest, int, IReadOnlyList<ChatStreamEvent>>)((request, index) =>
            throw new ProviderException("upstream down", retryable: true)));
        var host = AgentTestHost.Create(provider);
        var (root, events) = await AgentTestHost.RunRootAsync(host);

        var failure = Assert.Single(events.OfType<AgentFailed>());
        Assert.Equal("root", failure.AgentId);
        Assert.Contains("upstream down", failure.Error, StringComparison.Ordinal);
        Assert.Equal(new[] { MessageRole.User }, root.History.Select(message => message.Role).ToArray());
    }
}
