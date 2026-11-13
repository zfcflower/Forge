using Forge.Abstractions;
using Forge.Core.Agents;
using Forge.Core.Prompts;
using Xunit;

namespace Forge.Core.Tests.Agents;

public class CacheDisciplineTests
{
    private static string Workspace => Path.GetTempPath();

    private static readonly PromptLayers Layers = new("base system", "global agent", "project agent");

    private static readonly string[] ScoutTools = ["read", "grep"];

    private static readonly AgentDefinition Scout =
        new("Scout", "scout kind", "scout role", new ModelSettings("scout-model", ThinkingEffort.Low), ScoutTools);

    private static string PrefixFor(string? role) =>
        new PromptComposer().Compose(new PromptInput(Layers, role, null)).StablePrefix;

    private static ITool[] Tools() =>
    [
        FakeTool.Echo("read"),
        FakeTool.Echo("grep"),
        FakeTool.Echo("write-file", ToolSideEffect.Write),
    ];

    [Fact]
    public async Task SystemPrompt_is_the_stable_prefix_only_and_tools_are_sorted()
    {
        var provider = AgentTestScripts.OneToolCallThenSay("read", "done", "1");
        var host = AgentTestHost.Create(provider, Tools(), layers: Layers);
        var root = host.CreateRoot("session", Workspace);

        await AgentTestHost.CollectAsync(host.RunAsync(root, "go"));

        var requests = provider.Requests;
        Assert.Equal(2, requests.Count);
        var expected = PrefixFor(null);
        Assert.All(requests, request => Assert.Equal(expected, request.SystemPrompt));

        var names = requests[0].Tools.Select(tool => tool.Name).ToArray();
        Assert.Equal(names.OrderBy(name => name, StringComparer.Ordinal).ToArray(), names);
        Assert.Contains("spawn_agent", names);

        Assert.Null(requests[0].Cache!.CacheThroughMessageIndex);
        Assert.Equal(requests[1].Messages.Count - 2, requests[1].Cache!.CacheThroughMessageIndex);
        Assert.True(requests[1].Cache!.CacheSystemAndTools);
    }

    [Fact]
    public async Task DynamicContext_is_a_trailing_user_message_that_never_enters_history()
    {
        var provider = AgentTestScripts.OneToolCallThenSay("read", "done", "1");
        var host = AgentTestHost.Create(provider, Tools(), layers: Layers, dynamicContext: () => "DYNAMIC-MARKER");
        var root = host.CreateRoot("session", Workspace);

        await AgentTestHost.CollectAsync(host.RunAsync(root, "go"));

        var requests = provider.Requests;
        Assert.All(requests, request =>
        {
            var last = request.Messages[^1];
            Assert.Equal(MessageRole.User, last.Role);
            Assert.Equal("DYNAMIC-MARKER", Turn.Text(last));
        });
        Assert.All(root.History, message => Assert.DoesNotContain("DYNAMIC-MARKER", Turn.Text(message), StringComparison.Ordinal));
        Assert.Equal("go", Turn.Text(root.History[0]));
        Assert.Equal(4, root.History.Count);
        Assert.Equal(2, requests[0].Messages.Count);
        Assert.Equal(4, requests[1].Messages.Count);
    }

    [Fact]
    public async Task DynamicContext_message_is_last_and_stays_out_of_history_and_store()
    {
        var store = new InMemorySessionStore();
        var session = await store.CreateSessionAsync(Workspace, "dynamic", CancellationToken.None);
        var provider = AgentTestScripts.OneToolCallThenSay("read", "done", "1");
        var host = AgentTestHost.Create(
            provider,
            Tools(),
            layers: Layers,
            store: store,
            dynamicContext: () => "DYNAMIC-MARKER");
        var root = host.CreateRoot(session.Id, Workspace);

        await AgentTestHost.CollectAsync(host.RunAsync(root, "go"));

        var persisted = store.Appended(session.Id);
        Assert.Equal(4, persisted.Count);
        Assert.All(persisted, message => Assert.DoesNotContain("DYNAMIC-MARKER", Turn.Text(message), StringComparison.Ordinal));

        Assert.Null(provider.Requests[0].Cache!.CacheThroughMessageIndex);

        var request = provider.Requests[^1];
        Assert.Equal(
            new[] { MessageRole.User, MessageRole.Assistant, MessageRole.Tool, MessageRole.User },
            request.Messages.Select(message => message.Role).ToArray());
        Assert.Equal("DYNAMIC-MARKER", Turn.Text(request.Messages[^1]));
        Assert.Equal(persisted.Take(3).Select(Turn.Text), request.Messages.Take(3).Select(Turn.Text));
        Assert.Equal(request.Messages.Count - 3, request.Cache!.CacheThroughMessageIndex);
        Assert.DoesNotContain(
            request.Messages.Take(request.Cache.CacheThroughMessageIndex!.Value + 1),
            message => Turn.Text(message).Contains("DYNAMIC-MARKER", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TurnCounters_never_change_the_prefix()
    {
        var provider = new FakeProvider((request, index) => index is 0 or 1
            ? Turn.Call(($"{index}", "read", "{}"))
            : Turn.Say("done"));
        var host = AgentTestHost.Create(provider, Tools(), layers: Layers, dynamicContext: () => $"{Guid.NewGuid()}");
        var root = host.CreateRoot("session", Workspace);

        await AgentTestHost.CollectAsync(host.RunAsync(root, "go"));

        var prefixes = provider.Requests.Select(request => request.SystemPrompt).Distinct(StringComparer.Ordinal).ToArray();
        Assert.Single(prefixes);
        var toolSets = provider.Requests.Select(request => string.Join(',', request.Tools.Select(tool => tool.Name))).Distinct().ToArray();
        Assert.Single(toolSets);
    }

    [Fact]
    public async Task Instances_of_one_kind_share_a_byte_identical_prefix_and_tool_set()
    {
        var provider = new FakeProvider((request, index) => Turn.Say("done"));
        var host = AgentTestHost.Create(provider, Tools(), layers: Layers, agents: [Scout]);

        var first = host.CreateRoot("session-a", Workspace, "Scout", id: "scout-a");
        var second = host.CreateRoot("session-b", Workspace, "Scout", id: "scout-b");
        await AgentTestHost.CollectAsync(host.RunAsync(first, "one"));
        await AgentTestHost.CollectAsync(host.RunAsync(second, "two"));

        var requests = provider.Requests;
        Assert.Equal(2, requests.Count);
        Assert.Equal(PrefixFor(Scout.Prompt), requests[0].SystemPrompt);
        Assert.Equal(requests[0].SystemPrompt, requests[1].SystemPrompt);
        Assert.Equal(
            requests[0].Tools.Select(tool => tool.Name).ToArray(),
            requests[1].Tools.Select(tool => tool.Name).ToArray());

        var names = requests[0].Tools.Select(tool => tool.Name).ToArray();
        Assert.Contains("read", names);
        Assert.Contains("grep", names);
        Assert.DoesNotContain("write-file", names);
    }

    [Fact]
    public void Different_kinds_get_different_tool_sets()
    {
        var host = AgentTestHost.Create(new FakeProvider((request, index) => Turn.Say("done")), Tools(), layers: Layers, agents: [Scout]);
        var scout = host.CreateRoot("session", Workspace, "Scout");
        var root = host.CreateRoot("session", Workspace);

        var registry = host.Tools;
        var scoutTools = registry.Select(scout.Definition.ToolNames, canSpawn: true, SubAgentTools.Names);
        var rootTools = registry.Select(root.Definition.ToolNames, canSpawn: true, SubAgentTools.Names);

        Assert.Equal(
            new[] { "grep", "read", "spawn_agent", "subagent_followup", "subagent_steer", "subagent_stop" },
            scoutTools.Definitions.Select(definition => definition.Name).ToArray());
        Assert.Contains("write-file", rootTools.Definitions.Select(definition => definition.Name));
    }

    [Fact]
    public async Task SubAgents_of_one_kind_share_the_parent_independent_prefix()
    {
        var provider = new FakeProvider((request, index) =>
        {
            if (request.Messages.Count == 1 && Turn.LastUserText(request) == "go")
            {
                return Turn.Call(
                    ("call-1", "spawn_agent", """{"kind":"Scout","task":"one"}"""),
                    ("call-2", "spawn_agent", """{"kind":"Scout","task":"two"}"""));
            }

            return Turn.LastUserText(request) is "one" or "two" ? Turn.Say("child") : Turn.Say("done");
        });
        var host = AgentTestHost.Create(provider, Tools(), layers: Layers, agents: [Scout]);
        var root = host.CreateRoot("session", Workspace);

        await AgentTestHost.CollectAsync(host.RunAsync(root, "go"));

        var childPrefixes = provider.Requests
            .Where(request => Turn.LastUserText(request) is "one" or "two")
            .Select(request => request.SystemPrompt)
            .ToArray();
        Assert.Equal(2, childPrefixes.Length);
        Assert.All(childPrefixes, prefix => Assert.Equal(PrefixFor(Scout.Prompt), prefix));
    }
}
