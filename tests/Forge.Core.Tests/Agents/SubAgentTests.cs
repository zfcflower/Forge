using Forge.Abstractions;
using Forge.Core.Agents;
using Xunit;

namespace Forge.Core.Tests.Agents;

public class SubAgentTests
{
    private static string Workspace => Path.GetTempPath();

    private static readonly AgentDefinition Scout =
        new("Scout", "scout kind", "scout role", new ModelSettings("scout-model"), ["read"]);

    private static readonly AgentDefinition Worker =
        new("Worker", "worker kind", "worker role", new ModelSettings("worker-model"), ["read"]);

    private static AgentOptions Options(int maxParallel = 4, int maxDepth = 3, int maxTurns = 10) =>
        new() { MaxParallelSubAgents = maxParallel, MaxSubAgentDepth = maxDepth, MaxTurns = maxTurns };

    private static void Raise(ref int peak, int value)
    {
        var observed = Volatile.Read(ref peak);
        while (value > observed && Interlocked.CompareExchange(ref peak, value, observed) != observed)
        {
            observed = Volatile.Read(ref peak);
        }
    }

    private static string Spawn(string kind, string task, string? resumeId = null) =>
        resumeId is null
            ? $$"""{"kind":"{{kind}}","task":"{{task}}"}"""
            : $$"""{"kind":"{{kind}}","task":"{{task}}","resume_id":"{{resumeId}}"}""";

    private static int ToolRounds(ChatRequest request) =>
        request.Messages.Count(message => message.Role == MessageRole.Tool);

    private static string AgentId(string content)
    {
        const string marker = "[agent_id: ";
        var start = content.LastIndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"No agent id in '{content}'.");
        start += marker.Length;
        return content[start..content.IndexOf(']', start)];
    }

    [Fact]
    public async Task Spawn_returns_the_child_text_with_its_id()
    {
        var provider = new FakeProvider((request, index) =>
            request.Messages.Count == 1 && Turn.LastUserText(request) == "inspect"
                ? Turn.Say("child report")
                : index == 0
                    ? Turn.Call(("call-1", "spawn_agent", Spawn("Scout", "inspect")))
                    : Turn.Say("parent done"));
        var host = AgentTestHost.Create(provider, [FakeTool.Echo("read")], options: Options(), agents: [Scout]);
        var (root, events) = await AgentTestHost.RunRootAsync(host);

        Assert.Empty(events.OfType<AgentFailed>());
        var started = Assert.Single(events.OfType<AgentSubAgentStarted>());
        Assert.Equal("root", started.AgentId);
        Assert.Equal("Scout-1", started.ChildId);
        Assert.Equal("Scout", started.Kind);

        var result = Assert.Single(root.History[2].Content.OfType<ToolResultBlock>());
        Assert.False(result.IsError);
        Assert.Contains("child report", result.Content, StringComparison.Ordinal);
        Assert.Equal("Scout-1", AgentId(result.Content));

        var child = Assert.Single(host.SubAgents.Instances);
        Assert.Equal(1, child.Depth);
        Assert.Equal("Scout-1", child.Id);
        Assert.Contains(events.OfType<AgentTextDelta>(), delta => delta.AgentId == "Scout-1" && delta.Text == "child report");
        Assert.Contains(provider.Requests, request => request.SystemPrompt.Contains("scout role", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ChildGetsItsOwnSession()
    {
        var store = new InMemorySessionStore();
        var session = await store.CreateSessionAsync(Workspace, "root", CancellationToken.None);
        var provider = new FakeProvider((request, index) =>
            request.Messages.Count == 1 && Turn.LastUserText(request) == "inspect"
                ? Turn.Say("child report")
                : index == 0
                    ? Turn.Call(("call-1", "spawn_agent", Spawn("Scout", "inspect")))
                    : Turn.Say("parent done"));
        var host = AgentTestHost.Create(provider, [FakeTool.Echo("read")], options: Options(), agents: [Scout], store: store);
        var root = host.CreateRoot(session.Id, Workspace);

        await AgentTestHost.CollectAsync(host.RunAsync(root, "go"));

        var child = Assert.Single(host.SubAgents.Instances);
        Assert.NotEqual(session.Id, child.SessionId);
        Assert.Equal(4, store.Appended(session.Id).Count);
        Assert.Equal(2, store.Appended(child.SessionId).Count);
    }

    [Fact]
    public async Task DepthLimit_hides_the_spawn_tool_from_the_deepest_agent()
    {
        var provider = new FakeProvider((request, index) => Turn.HasTool(request, SpawnAgentTool.Name) && ToolRounds(request) == 0
            ? Turn.Call(("call", "spawn_agent", Spawn("Scout", "inspect")))
            : Turn.Say("leaf"));
        var host = AgentTestHost.Create(provider, [FakeTool.Echo("read")], options: Options(), agents: [Scout]);
        var (root, events) = await AgentTestHost.RunRootAsync(host);

        Assert.Empty(events.OfType<AgentFailed>());
        var instances = host.SubAgents.Instances;
        Assert.Equal(3, instances.Count);
        Assert.Equal(new[] { 1, 2, 3 }, instances.Select(instance => instance.Depth).Order().ToArray());

        var deepest = instances.Single(instance => instance.Depth == 3);
        Assert.Equal("leaf", Turn.Text(deepest.History[^1]));
        Assert.DoesNotContain(deepest.History, message => message.Content.OfType<ToolResultBlock>().Any());

        var started = events.OfType<AgentSubAgentStarted>().ToArray();
        Assert.Equal(3, started.Length);
        Assert.Equal(new[] { "root", "Scout-1", "Scout-2" }, started.Select(value => value.AgentId).ToArray());
    }

    [Fact]
    public async Task SpawnCalls_run_in_parallel_up_to_the_configured_bound()
    {
        var active = 0;
        var peak = 0;
        var provider = new FakeProvider(async (request, index, ct) =>
        {
            if (request.Messages.Count == 1 && Turn.LastUserText(request) == "go")
            {
                return Turn.Call(Enumerable.Range(1, 6)
                    .Select(number => ($"call-{number}", "spawn_agent", Spawn("Scout", $"task {number}")))
                    .ToArray());
            }

            if (Turn.LastUserText(request).StartsWith("task", StringComparison.Ordinal))
            {
                var current = Interlocked.Increment(ref active);
                try
                {
                    var observed = Volatile.Read(ref peak);
                    while (current > observed && Interlocked.CompareExchange(ref peak, current, observed) != observed)
                    {
                        observed = Volatile.Read(ref peak);
                    }

                    await Task.Delay(80, ct);
                }
                finally
                {
                    Interlocked.Decrement(ref active);
                }

                return Turn.Say("child done");
            }

            return Turn.Say("parent done");
        });
        var host = AgentTestHost.Create(provider, [FakeTool.Echo("read")], options: Options(maxParallel: 4), agents: [Scout]);
        var (root, events) = await AgentTestHost.RunRootAsync(host);

        Assert.Empty(events.OfType<AgentFailed>());
        Assert.InRange(peak, 2, 4);
        Assert.Equal(6, host.SubAgents.Instances.Count);
        var results = root.History[2].Content.OfType<ToolResultBlock>().ToArray();
        Assert.Equal(6, results.Length);
        Assert.All(results, result => Assert.False(result.IsError));
        Assert.Equal(6, results.Select(result => AgentId(result.Content)).Distinct().Count());
    }

    [Fact]
    public async Task TreeWideLimit_counts_every_running_agent_and_the_spawn_tree_still_completes()
    {
        var active = 0;
        var peak = 0;
        var provider = new FakeProvider(async (request, index, ct) =>
        {
            var current = Interlocked.Increment(ref active);
            try
            {
                Raise(ref peak, current);
                await Task.Delay(30, ct).ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Decrement(ref active);
            }

            if (request.SystemPrompt.Contains(Worker.Prompt, StringComparison.Ordinal))
            {
                return ToolRounds(request) == 0
                    ? Turn.Call(
                        ("worker-call-1", "spawn_agent", Spawn("Scout", "inspect one")),
                        ("worker-call-2", "spawn_agent", Spawn("Scout", "inspect two")),
                        ("worker-call-3", "spawn_agent", Spawn("Scout", "inspect three")))
                    : Turn.Say("worker report");
            }

            if (request.SystemPrompt.Contains(Scout.Prompt, StringComparison.Ordinal))
            {
                return Turn.Say("scout report");
            }

            return ToolRounds(request) == 0
                ? Turn.Call(Enumerable.Range(1, 4)
                    .Select(number => ($"call-{number}", "spawn_agent", Spawn("Worker", $"task {number}")))
                    .ToArray())
                : Turn.Say("root done");
        });
        var host = AgentTestHost.Create(
            provider,
            [FakeTool.Echo("read")],
            options: Options(maxParallel: 4),
            agents: [Scout, Worker]);
        var root = host.CreateRoot("session", Workspace);

        var events = await host.RunToCompletionAsync(root, timeoutSeconds: 20);
        Assert.InRange(peak, 2, 4);
        var instances = host.SubAgents.Instances;
        Assert.Equal(16, instances.Count);
        Assert.Equal(4, instances.Count(instance => instance.Definition.Name == "Worker"));
        Assert.Equal(12, instances.Count(instance => instance.Definition.Name == "Scout"));
        Assert.All(
            instances,
            instance => Assert.DoesNotContain(
                instance.History,
                message => message.Content.OfType<ToolResultBlock>().Any(result => result.IsError)));
    }

    [Fact]
    public async Task EightSiblingSpawns_stay_within_the_parallel_limit()
    {
        var active = 0;
        var peak = 0;
        var provider = new FakeProvider(async (request, index, ct) =>
        {
            if (request.Messages.Count == 1 && Turn.LastUserText(request) == "go")
            {
                return Turn.Call(Enumerable.Range(1, 8)
                    .Select(number => ($"call-{number}", "spawn_agent", Spawn("Scout", $"task {number}")))
                    .ToArray());
            }

            if (Turn.LastUserText(request).StartsWith("task", StringComparison.Ordinal))
            {
                var current = Interlocked.Increment(ref active);
                try
                {
                    Raise(ref peak, current);
                    await Task.Delay(60, ct).ConfigureAwait(false);
                }
                finally
                {
                    Interlocked.Decrement(ref active);
                }

                return Turn.Say("child done");
            }

            return Turn.Say("parent done");
        });
        var host = AgentTestHost.Create(provider, [FakeTool.Echo("read")], options: Options(maxParallel: 4), agents: [Scout]);
        var root = host.CreateRoot("session", Workspace);

        var events = await host.RunToCompletionAsync(root, timeoutSeconds: 20);
        Assert.Equal(4, host.Options.MaxParallelSubAgents);
        Assert.InRange(peak, 2, 4);
        Assert.Equal(8, host.SubAgents.Instances.Count);
        var results = root.History[2].Content.OfType<ToolResultBlock>().ToArray();
        Assert.Equal(8, results.Length);
        Assert.All(results, result => Assert.False(result.IsError));
        Assert.Equal(8, results.Select(result => AgentId(result.Content)).Distinct().Count());
    }

    [Fact]
    public async Task ResumeId_continues_the_same_instance_and_kind_mismatch_is_rejected()
    {
        var provider = new FakeProvider((request, index) =>
        {
            var last = Turn.LastUserText(request);
            if (last == "first task")
            {
                return Turn.Say("alpha");
            }

            if (last == "second task")
            {
                return Turn.Say("beta");
            }

            return ToolRounds(request) switch
            {
                0 => Turn.Call(("call-1", "spawn_agent", Spawn("Scout", "first task"))),
                1 => Turn.Call(("call-2", "spawn_agent", Spawn("Scout", "second task", "Scout-1"))),
                2 => Turn.Call(("call-3", "spawn_agent", Spawn("Worker", "second task", "Scout-1"))),
                _ => Turn.Say("parent done"),
            };
        });
        var worker = new AgentDefinition("Worker", "worker kind", "worker role", new ModelSettings("worker-model"), ["read"]);
        var host = AgentTestHost.Create(provider, [FakeTool.Echo("read")], options: Options(), agents: [Scout, worker]);
        var (root, events) = await AgentTestHost.RunRootAsync(host);

        Assert.Empty(events.OfType<AgentFailed>());
        var child = Assert.Single(host.SubAgents.Instances);
        Assert.Equal("Scout-1", child.Id);
        Assert.Equal(
            new[] { "first task", "alpha", "second task", "beta" },
            child.History.Select(message => Turn.Text(message)).ToArray());

        var results = root.History.SelectMany(message => message.Content.OfType<ToolResultBlock>()).ToArray();
        Assert.Equal(3, results.Length);
        Assert.Equal("Scout-1", AgentId(results[0].Content));
        Assert.Equal("Scout-1", AgentId(results[1].Content));
        Assert.Contains("beta", results[1].Content, StringComparison.Ordinal);
        Assert.True(results[2].IsError);
        Assert.Contains("not a Worker", results[2].Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Resuming_the_agent_that_is_spawning_is_rejected()
    {
        var provider = new FakeProvider((request, index) =>
        {
            if (request.Messages[^1].Role == MessageRole.Tool)
            {
                return Turn.Say("child finished");
            }

            return Turn.LastUserText(request) switch
            {
                "long task" => Turn.Call(("call-self", "spawn_agent", Spawn("Scout", "loop", "Scout-1"))),
                _ => Turn.Call(("call-1", "spawn_agent", Spawn("Scout", "long task"))),
            };
        });
        var host = AgentTestHost.Create(provider, [FakeTool.Echo("read")], options: Options(), agents: [Scout]);
        var (root, events) = await AgentTestHost.RunRootAsync(host);

        Assert.Empty(events.OfType<AgentFailed>());
        var child = Assert.Single(host.SubAgents.Instances);
        var rejection = Assert.Single(child.History[2].Content.OfType<ToolResultBlock>());
        Assert.True(rejection.IsError);
        Assert.Contains("still running", rejection.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnknownKind_is_an_error_result()
    {
        var provider = new FakeProvider((request, index) => index == 0
            ? Turn.Call(("call-1", "spawn_agent", Spawn("Nope", "task")))
            : Turn.Say("done"));
        var host = AgentTestHost.Create(provider, [FakeTool.Echo("read")], options: Options(), agents: [Scout]);
        var (root, events) = await AgentTestHost.RunRootAsync(host);

        Assert.Empty(events.OfType<AgentFailed>());
        Assert.Empty(host.SubAgents.Instances);
        var result = Assert.Single(root.History[2].Content.OfType<ToolResultBlock>());
        Assert.True(result.IsError);
        Assert.Contains("Unknown agent kind", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void SpawnToolDescription_lists_kinds_sorted()
    {
        var host = AgentTestHost.Create(new FakeProvider((request, index) => Turn.Say("done")), agents: [Scout]);
        var definition = host.Tools.Tools.Single(tool => tool.Definition.Name == SpawnAgentTool.Name).Definition;
        var kindLines = definition.Description
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(line => line.StartsWith("- ", StringComparison.Ordinal))
            .Select(line => line[2..line.IndexOf(':')])
            .ToArray();

        Assert.Equal(new[] { "Plan", "Reviewer", "Scout", "Worker" }, kindLines);
    }
}
