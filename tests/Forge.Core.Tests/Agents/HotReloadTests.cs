using Forge.Abstractions;
using Forge.Core.Agents;
using Forge.Core.Prompts;
using Xunit;

namespace Forge.Core.Tests.Agents;

/// <summary>Hot reload of external tools: same-turn visibility, removal, snapshot reuse, builtin shadowing, child whitelists, concurrency.</summary>
public sealed class HotReloadTests
{
    private static string Workspace => Path.GetTempPath();

    private static void AssertSortedOrdinal(string[] names) =>
        Assert.Equal(names.OrderBy(name => name, StringComparer.Ordinal).ToArray(), names);

    private static void AssertHistoryLegallyPaired(AgentInstance root)
    {
        var history = root.History;
        for (var index = 0; index < history.Count; index++)
        {
            var calls = history[index].Content.OfType<ToolCallBlock>().ToArray();
            if (calls.Length == 0)
            {
                continue;
            }

            Assert.True(
                index + 1 < history.Count && history[index + 1].Role == MessageRole.Tool,
                $"Assistant message {index} has {calls.Length} calls but no tool message follows it.");
            var results = history[index + 1].Content.OfType<ToolResultBlock>().ToArray();
            Assert.Equal(calls.Length, results.Length);
            Assert.Equal(
                calls.Select(call => call.Id).ToArray(),
                results.Select(result => result.CallId).ToArray());
        }
    }

    [Fact]
    public async Task Tool_published_mid_turn_is_visible_and_callable_in_the_next_request()
    {
        AgentHost? hostRef = null;
        var hotCalls = 0;
        var hot = new FakeTool(
            "hot_b",
            ToolSideEffect.ReadOnly,
            (_, _, _) =>
            {
                Interlocked.Increment(ref hotCalls);
                return new ValueTask<ToolResult>(new ToolResult("b-ok"));
            });
        var swapper = new FakeTool(
            "hot_a",
            ToolSideEffect.ReadOnly,
            (_, _, _) =>
            {
                hostRef!.ReplaceExternalTools([hot]);
                return new ValueTask<ToolResult>(new ToolResult("a-ok"));
            });
        var provider = new FakeProvider((request, index) => index switch
        {
            0 => Turn.Call(("c1", "hot_a", "{}")),
            1 => Turn.Call(("c2", "hot_b", "{}")),
            _ => Turn.Say("done"),
        });
        var host = AgentTestHost.Create(provider, [swapper, FakeTool.Echo("old_tool")]);
        hostRef = host;
        var root = host.CreateRoot("session", Workspace);

        var events = await AgentTestHost.CollectAsync(host.RunAsync(root, "go"));

        Assert.Empty(events.OfType<AgentFailed>());
        Assert.Equal(3, provider.Requests.Count);
        var next = provider.Requests[1];
        Assert.Contains(next.Tools, tool => tool.Name == "hot_b");
        Assert.DoesNotContain(next.Tools, tool => tool.Name == "hot_a");
        Assert.DoesNotContain(next.Tools, tool => tool.Name == "old_tool");
        Assert.Equal(1, hotCalls);
        var contents = root.History.SelectMany(message => message.Content.OfType<ToolResultBlock>())
            .Select(result => result.Content).ToArray();
        Assert.Contains("a-ok", contents);
        Assert.Contains("b-ok", contents);
        AssertHistoryLegallyPaired(root);
    }

    [Fact]
    public async Task Calling_a_removed_tool_is_an_error_and_history_stays_paired()
    {
        AgentHost? hostRef = null;
        var provider = new FakeProvider((request, index) =>
        {
            if (index == 0)
            {
                hostRef!.ReplaceExternalTools([]);
                return Turn.Call(("c1", "victim", "{}"));
            }

            return Turn.Say("done");
        });
        var host = AgentTestHost.Create(provider, [FakeTool.Echo("victim")]);
        hostRef = host;
        var root = host.CreateRoot("session", Workspace);

        var events = await AgentTestHost.CollectAsync(host.RunAsync(root, "go"));

        Assert.Empty(events.OfType<AgentFailed>());
        var result = Assert.Single(root.History.SelectMany(message => message.Content.OfType<ToolResultBlock>()));
        Assert.True(result.IsError);
        Assert.Equal("Unknown tool 'victim'.", result.Content);
        AssertHistoryLegallyPaired(root);
        Assert.Equal(
            new[] { MessageRole.User, MessageRole.Assistant, MessageRole.Tool, MessageRole.Assistant },
            root.History.Select(message => message.Role).ToArray());
    }

    [Fact]
    public async Task Steady_requests_share_one_tool_array_and_a_replacement_resorts_it()
    {
        AgentHost? hostRef = null;
        FakeTool? readRef = null;
        var reads = 0;
        readRef = new FakeTool(
            "m_read",
            ToolSideEffect.ReadOnly,
            (_, _, _) =>
            {
                if (Interlocked.Increment(ref reads) == 2)
                {
                    hostRef!.ReplaceExternalTools([readRef!, FakeTool.Echo("aaa_new")]);
                }

                return new ValueTask<ToolResult>(new ToolResult("r"));
            });
        var provider = new FakeProvider((request, index) => index switch
        {
            0 => Turn.Call(("c1", "m_read", "{}")),
            1 => Turn.Call(("c2", "m_read", "{}")),
            _ => Turn.Say("done"),
        });
        var host = AgentTestHost.Create(provider, [readRef!]);
        hostRef = host;
        var root = host.CreateRoot("session", Workspace);

        var events = await AgentTestHost.CollectAsync(host.RunAsync(root, "go"));

        Assert.Empty(events.OfType<AgentFailed>());
        Assert.Equal(3, provider.Requests.Count);
        Assert.Same(provider.Requests[0].Tools, provider.Requests[1].Tools);
        Assert.NotSame(provider.Requests[1].Tools, provider.Requests[2].Tools);
        foreach (var request in provider.Requests)
        {
            AssertSortedOrdinal(request.Tools.Select(tool => tool.Name).ToArray());
        }

        Assert.DoesNotContain(provider.Requests[0].Tools, tool => tool.Name == "aaa_new");
        Assert.Contains(provider.Requests[2].Tools, tool => tool.Name == "aaa_new");
        AssertHistoryLegallyPaired(root);
    }

    [Fact]
    public async Task External_tool_with_a_builtin_name_never_shadows_the_builtin()
    {
        var provider = new FakeProvider((request, index) => index == 0
            ? Turn.Call(("c1", "todo", """{"operation":"list"}"""), ("c2", "spawn_agent", """{"kind":"Nope","task":"x"}"""))
            : Turn.Say("done"));
        var host = new AgentHost(new AgentHostOptions
        {
            Providers = _ => provider,
            Tools =
            [
                new FakeTool("todo", ToolSideEffect.ReadOnly, (_, _, _) => new ValueTask<ToolResult>(new ToolResult("fake-todo"))),
                new FakeTool("spawn_agent", ToolSideEffect.ReadOnly, (_, _, _) => new ValueTask<ToolResult>(new ToolResult("fake-spawn"))),
            ],
            Layers = new PromptLayers("test system", null, null),
            Todos = new StubTodoStore(),
        });
        var root = host.CreateRoot("session", Workspace);

        var events = await AgentTestHost.CollectAsync(host.RunAsync(root, "go"));

        Assert.Empty(events.OfType<AgentFailed>());
        Assert.True(host.Tools.TryGet("todo", out var todo));
        Assert.IsType<TodoTool>(todo);
        Assert.True(host.Tools.TryGet("spawn_agent", out var spawn));
        Assert.IsType<SpawnAgentTool>(spawn);
        Assert.Equal(1, host.Tools.Tools.Count(tool => tool.Definition.Name == "todo"));
        Assert.Equal(1, host.Tools.Tools.Count(tool => tool.Definition.Name == "spawn_agent"));

        var results = root.History.SelectMany(message => message.Content.OfType<ToolResultBlock>()).ToArray();
        Assert.Equal(2, results.Length);
        Assert.Contains("checklist", results[0].Content, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("fake-todo", results[0].Content, StringComparison.Ordinal);
        Assert.Contains("Unknown agent kind", results[1].Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Child_rebuilds_from_its_own_allow_list_while_root_sees_the_new_tool()
    {
        AgentHost? hostRef = null;
        FakeTool? readRef = null;
        FakeTool? grepRef = null;
        FakeTool? findRef = null;
        FakeTool? lsRef = null;
        var hotNew = FakeTool.Echo("hot_new");
        var reads = 0;
        readRef = new FakeTool(
            "read",
            ToolSideEffect.ReadOnly,
            (_, _, _) =>
            {
                if (Interlocked.Increment(ref reads) == 1)
                {
                    hostRef!.ReplaceExternalTools([readRef!, grepRef!, findRef!, lsRef!, hotNew]);
                }

                return new ValueTask<ToolResult>(new ToolResult("read-ok"));
            });
        grepRef = FakeTool.Echo("grep");
        findRef = FakeTool.Echo("find");
        lsRef = FakeTool.Echo("ls");
        var provider = new FakeProvider((request, index) =>
        {
            if (request.SystemPrompt.Contains("You scout code and report findings.", StringComparison.Ordinal))
            {
                return ToolRounds(request) == 0 ? Turn.Call(("c1", "read", "{}")) : Turn.Say("child-done");
            }

            return ToolRounds(request) == 0
                ? Turn.Call(("call-1", "spawn_agent", """{"kind":"Scout","task":"scout-task"}"""))
                : Turn.Say("parent-done");
        });
        var host = AgentTestHost.Create(provider, [readRef!, grepRef!, findRef!, lsRef!], agents: [BuiltInAgents.Scout]);
        hostRef = host;
        var root = host.CreateRoot("session", Workspace);

        var events = await AgentTestHost.CollectAsync(host.RunAsync(root, "go"));

        Assert.Empty(events.OfType<AgentFailed>());
        var childRequests = provider.Requests
            .Where(request => request.SystemPrompt.Contains("You scout code and report findings.", StringComparison.Ordinal))
            .ToArray();
        Assert.Equal(2, childRequests.Length);
        foreach (var child in childRequests)
        {
            Assert.DoesNotContain(child.Tools, tool => tool.Name == "hot_new");
            Assert.Contains(child.Tools, tool => tool.Name == "read");
            Assert.Contains(child.Tools, tool => tool.Name == "spawn_agent");
            AssertSortedOrdinal(child.Tools.Select(tool => tool.Name).ToArray());
        }

        var rootLast = provider.Requests[^1];
        Assert.Contains(rootLast.Tools, tool => tool.Name == "hot_new");
        AssertHistoryLegallyPaired(root);
        Assert.Contains(
            root.History.SelectMany(message => message.Content.OfType<ToolResultBlock>()),
            result => result.Content.Contains("child-done", StringComparison.Ordinal));
    }

    [Fact]
    public void Host_tools_returns_the_current_snapshot()
    {
        var host = AgentTestHost.Create(
            new FakeProvider((request, index) => Turn.Say("done")),
            [FakeTool.Echo("snap_a")]);

        Assert.Same(host.Tools, host.Tools);
        Assert.Contains(host.Tools.Tools, tool => tool.Definition.Name == "snap_a");

        host.ReplaceExternalTools([FakeTool.Echo("snap_b")]);

        Assert.Contains(host.Tools.Tools, tool => tool.Definition.Name == "snap_b");
        Assert.DoesNotContain(host.Tools.Tools, tool => tool.Definition.Name == "snap_a");
    }

    [Fact]
    public async Task Concurrent_replacements_keep_versions_monotonic_and_snapshots_paired()
    {
        var surface = new ToolSurface([]);
        var toolA = FakeTool.Echo("concur_a");
        var toolB = FakeTool.Echo("concur_b");
        const int Writers = 4;
        const int Readers = 4;
        const int WritesEach = 50;
        const int ReadsEach = 500;
        using var start = new Barrier(Writers + Readers);
        var tasks = new List<Task>();

        for (var writer = 0; writer < Writers; writer++)
        {
            var parity = writer;
            tasks.Add(Task.Run(() =>
            {
                Assert.True(start.SignalAndWait(TimeSpan.FromSeconds(10)));
                for (var round = 0; round < WritesEach; round++)
                {
                    surface.ReplaceExternal([(parity + round) % 2 == 0 ? toolA : toolB]);
                }
            }));
        }

        for (var reader = 0; reader < Readers; reader++)
        {
            tasks.Add(Task.Run(() =>
            {
                Assert.True(start.SignalAndWait(TimeSpan.FromSeconds(10)));
                var lastVersion = -1;
                for (var round = 0; round < ReadsEach; round++)
                {
                    var version = surface.Version;
                    var names = surface.Tools.Tools.Select(tool => tool.Definition.Name).ToArray();
                    Assert.True(version >= lastVersion, $"version went backwards: {lastVersion} -> {version}");
                    lastVersion = version;
                    Assert.True(names.Length <= 1, $"mixed snapshot: {string.Join(",", names)}");
                    if (names.Length == 1)
                    {
                        Assert.True(names[0] is "concur_a" or "concur_b", $"foreign tool: {names[0]}");
                    }

                    AssertSortedOrdinal(names);
                }
            }));
        }

        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(surface.Version >= Writers * WritesEach, $"expected at least {Writers * WritesEach} versions, saw {surface.Version}");
    }

    [Fact]
    public async Task Replacements_during_a_live_run_leave_every_request_paired()
    {
        AgentHost? hostRef = null;
        using var entered = new ManualResetEventSlim(false);
        using var released = new ManualResetEventSlim(false);
        FakeTool? gateRef = null;
        gateRef = new FakeTool(
            "gate_tool",
            ToolSideEffect.ReadOnly,
            (_, _, ct) =>
            {
                entered.Set();
                released.Wait(ct);
                return new ValueTask<ToolResult>(new ToolResult("through"));
            });
        var provider = new FakeProvider((request, index) => index == 0
            ? Turn.Call(("c1", "gate_tool", "{}"))
            : Turn.Say("done"));
        var host = AgentTestHost.Create(provider, [gateRef!]);
        hostRef = host;
        var root = host.CreateRoot("session", Workspace);

        var run = AgentTestHost.CollectAsync(host.RunAsync(root, "go"));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10)), "tool body never started");

        var churn = Task.Run(() =>
        {
            for (var round = 0; round < 20; round++)
            {
                hostRef!.ReplaceExternalTools([gateRef!, FakeTool.Echo(round % 2 == 0 ? "flux_0" : "flux_1")]);
            }
        });
        await churn.WaitAsync(TimeSpan.FromSeconds(10));
        released.Set();
        var events = await run.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Empty(events.OfType<AgentFailed>());
        Assert.Equal(2, provider.Requests.Count);
        foreach (var request in provider.Requests)
        {
            var external = request.Tools.Select(tool => tool.Name).Where(name =>
                name is "gate_tool" or "flux_0" or "flux_1").ToArray();
            Assert.Contains("gate_tool", external);
            Assert.False(
                external.Contains("flux_0") && external.Contains("flux_1"),
                $"mixed snapshot: {string.Join(",", external)}");
        }

        AssertHistoryLegallyPaired(root);
    }

    private static int ToolRounds(ChatRequest request) =>
        request.Messages.Count(message => message.Role == MessageRole.Tool);

    private sealed class StubTodoStore : ITodoStore
    {
        public event Action<string>? Changed;

        public Task<IReadOnlyList<TodoItem>> ListAsync(string sessionId, CancellationToken ct)
        {
            Changed?.Invoke(sessionId);
            return Task.FromResult<IReadOnlyList<TodoItem>>([]);
        }

        public Task<TodoItem> AddAsync(string sessionId, string title, string? note, string? afterId, CancellationToken ct) =>
            throw new NotImplementedException();

        public Task<TodoItem?> UpdateAsync(string id, string? title, TodoStatus? status, string? note, CancellationToken ct) =>
            throw new NotImplementedException();

        public Task<bool> RemoveAsync(string id, CancellationToken ct) =>
            throw new NotImplementedException();

        public Task<bool> MoveAsync(string id, int order, CancellationToken ct) =>
            throw new NotImplementedException();
    }
}
