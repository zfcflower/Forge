using Forge.Abstractions;
using Forge.Ipc;
using Xunit;

namespace Forge.HostClient.Tests;

/// <summary>Non-UI contribution snapshots: registration, replacement, generation tagging, revocation,
/// name-clash resolution, malformed payloads and SDK validation.</summary>
public sealed class ContributionSnapshotTests
{
    [Fact]
    public async Task Register_builds_every_non_tool_view()
    {
        await using var host = new FakeHost();
        await using var client = ClientFactory.Create(host);
        await client.StartConnectedAsync(host);

        await host.NotifyClientAsync("contributions.register", Contributions("alpha",
            """{"name":"hi","description":"says hi","argumentHint":"who"}""",
            """{"name":"guide","description":"how to","body":"# Steps"}""",
            """{"name":"helper","description":"helps","prompt":"Be helpful.","model":{"model":"m","thinking":1,"maxOutputTokens":64},"toolNames":["search"]}""",
            """{"id":"tone"}"""));
        await host.SyncAsync();

        var command = Assert.Single(client.Commands);
        Assert.Equal("hi", command.Definition.Name);
        Assert.Equal("alpha:hi", command.ToString());
        var workflow = Assert.Single(client.Workflows);
        Assert.Equal(new WorkflowDefinition("guide", "how to", "# Steps"), workflow);
        var agent = Assert.Single(client.Agents);
        Assert.Equal("helper", agent.Name);
        Assert.Equal("Be helpful.", agent.Prompt);
        Assert.Equal(ThinkingEffort.Low, agent.Model.Thinking);
        Assert.Equal(["search"], agent.ToolNames);
        var prompt = Assert.Single(client.PromptContributors);
        Assert.Equal(new PluginPromptReference("alpha", "tone"), prompt);
    }

    [Fact]
    public async Task Replace_removes_what_the_new_snapshot_omits()
    {
        await using var host = new FakeHost();
        await using var client = ClientFactory.Create(host);
        await client.StartConnectedAsync(host);
        var changes = new EventLog<int>();
        client.ContributionsChanged += () => changes.Add(1);
        await host.NotifyClientAsync("contributions.register", Contributions("alpha",
            """{"name":"old","description":"d","argumentHint":""}""",
            """{"name":"w","description":"d","body":"b"}""",
            null, null));
        await host.SyncAsync();

        await host.NotifyClientAsync("contributions.register", Contributions("alpha",
            """{"name":"fresh","description":"d","argumentHint":""}""",
            null, null, null));
        await host.SyncAsync();

        Assert.Equal("fresh", Assert.Single(client.Commands).Definition.Name);
        Assert.Empty(client.Workflows);
        Assert.Equal(2, changes.Snapshot().Count);
    }

    [Fact]
    public async Task Same_set_registration_updates_the_generation_without_an_event_or_new_proxies()
    {
        await using var host = new FakeHost();
        await using var client = ClientFactory.Create(host);
        await client.StartConnectedAsync(host);
        var payload = Contributions("alpha",
            """{"name":"hi","description":"d","argumentHint":""}""",
            null, null, null);
        await host.NotifyClientAsync("contributions.register", payload);
        await host.SyncAsync();
        var changes = new EventLog<int>();
        client.ContributionsChanged += () => changes.Add(1);
        var before = client.Commands;

        await host.NotifyClientAsync("contributions.register", payload);
        await host.SyncAsync();

        Assert.Empty(changes.Snapshot());
        Assert.Same(before, client.Commands);
    }

    [Fact]
    public async Task Unregister_removes_every_non_tool_contribution_of_that_plugin_only()
    {
        await using var host = new FakeHost();
        await using var client = ClientFactory.Create(host);
        await client.StartConnectedAsync(host);
        var changes = new EventLog<int>();
        client.ContributionsChanged += () => changes.Add(1);
        await host.NotifyClientAsync("contributions.register", Contributions("alpha",
            """{"name":"a","description":"d","argumentHint":""}""", null, null, null));
        await host.NotifyClientAsync("contributions.register", Contributions("beta",
            """{"name":"b","description":"d","argumentHint":""}""", null, null, """{"id":"p"}"""));
        await host.SyncAsync();

        await host.NotifyClientAsync("contributions.unregister", """{"pluginId":"alpha"}""");
        await host.SyncAsync();

        Assert.Equal("b", Assert.Single(client.Commands).Definition.Name);
        Assert.Equal("beta", Assert.Single(client.PromptContributors).PluginId);
        Assert.Equal(3, changes.Snapshot().Count);
    }

    [Fact]
    public async Task Unregister_of_an_unknown_plugin_fires_no_event()
    {
        await using var host = new FakeHost();
        await using var client = ClientFactory.Create(host);
        await client.StartConnectedAsync(host);
        await host.NotifyClientAsync("contributions.register", Contributions("alpha",
            """{"name":"a","description":"d","argumentHint":""}""", null, null, null));
        await host.SyncAsync();
        var changes = new EventLog<int>();
        client.ContributionsChanged += () => changes.Add(1);

        await host.NotifyClientAsync("contributions.unregister", """{"pluginId":"ghost"}""");
        await host.SyncAsync();

        Assert.Empty(changes.Snapshot());
        Assert.Equal("a", Assert.Single(client.Commands).Definition.Name);
    }

    [Fact]
    public async Task Reconnect_without_re_registration_drops_stale_contributions_with_one_event()
    {
        await using var host = new FakeHost();
        await using var client = ClientFactory.Create(host);
        var statuses = new EventLog<(HostStatus Status, string? Detail)>();
        client.StatusChanged += (status, detail) => statuses.Add((status, detail));
        await client.StartConnectedAsync(host);
        await host.NotifyClientAsync("contributions.register", Contributions("alpha",
            """{"name":"a","description":"d","argumentHint":""}""", null, null, """{"id":"p"}"""));
        await host.SyncAsync();
        Assert.Single(client.Commands);

        await host.DropConnectionAsync();
        await statuses.UntilAsync(seen => seen.Any(e => e.Status == HostStatus.Failed));
        var changes = new EventLog<int>();
        client.ContributionsChanged += () => changes.Add(1);
        using var limit = TestLimit.Create();

        var accepting = host.AcceptAsync();
        await client.StartAsync(limit.Token).Bounded();
        await accepting.Bounded();

        Assert.Equal(HostStatus.Running, client.Status);
        Assert.Empty(client.Commands);
        Assert.Empty(client.PromptContributors);
        Assert.Single(changes.Snapshot());
    }

    [Fact]
    public async Task Revoked_proxy_is_stale_while_a_new_registration_is_usable()
    {
        await using var host = new FakeHost();
        host.On("plugin.command", (_, _) => throw new FakeHostError("unknown_command", "command 'hi' is not installed"));
        await using var client = ClientFactory.Create(
            host, approval: (_, _) => ValueTask.FromResult(ApprovalDecision.Allow));
        await client.StartConnectedAsync(host);
        using var limit = TestLimit.Create();
        await host.NotifyClientAsync("contributions.register", Contributions("alpha",
            """{"name":"hi","description":"d","argumentHint":""}""", null, null, null));
        await host.SyncAsync();
        var stale = Assert.Single(client.Commands);
        var directory = BoundRoots.AbsoluteDir("revoke");
        var context = new CommandContext("sess", directory);

        await host.NotifyClientAsync("contributions.unregister", """{"pluginId":"alpha"}""");
        await host.SyncAsync();
        Assert.Empty(client.Commands);

        var staleResult = await stale.ExecuteAsync("args", context, limit.Token);
        Assert.True(staleResult.IsError);
        Assert.Contains("alpha", staleResult.Output);
        Assert.Contains("hi", staleResult.Output);

        host.On("plugin.command", (_, _) => Task.FromResult(Json.Element("""{"output":"fresh-ok"}""")));
        await host.NotifyClientAsync("contributions.register", Contributions("alpha",
            """{"name":"hi","description":"d","argumentHint":""}""", null, null, null));
        await host.SyncAsync();
        var current = Assert.Single(client.Commands);

        Assert.NotSame(stale, current);
        var fresh = await current.ExecuteAsync("args", context, limit.Token);
        Assert.Equal(new CommandResult("fresh-ok", null, false), fresh);
    }

    [Fact]
    public async Task Command_name_clash_is_deterministic_first_plugin_id_wins_with_a_diagnostic()
    {
        await using var host = new FakeHost();
        await using var client = ClientFactory.Create(host);
        await client.StartConnectedAsync(host);
        await host.NotifyClientAsync("contributions.register", Contributions("beta",
            """{"name":"dup","description":"from beta","argumentHint":""}""", null, null, null));
        await host.NotifyClientAsync("contributions.register", Contributions("alpha",
            """{"name":"dup","description":"from alpha","argumentHint":""}""", null, null, null));
        await host.SyncAsync();

        var winner = Assert.Single(client.Commands);
        Assert.Equal("alpha", ((ExtensionHostCommand)winner).PluginId);
        Assert.Equal("from alpha", winner.Definition.Description);
        BoundRoots.AssertDiagnostic(client, "clashes");
        BoundRoots.AssertDiagnostic(client, "'beta'");
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("\"text\"")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"pluginId\":\"\",\"contributions\":{}}")]
    [InlineData("{\"pluginId\":\"alpha\",\"contributions\":5}")]
    [InlineData("{\"pluginId\":\"alpha\",\"contributions\":[]}")]
    public async Task Malformed_complete_snapshot_is_atomically_refused_and_keeps_the_previous_state(string payload)
    {
        await using var host = new FakeHost();
        await using var client = ClientFactory.Create(host);
        await client.StartConnectedAsync(host);
        await host.NotifyClientAsync("contributions.register", Contributions("alpha",
            """{"name":"kept","description":"d","argumentHint":""}""", null, null, null));
        await host.SyncAsync();
        var changes = new EventLog<int>();
        client.ContributionsChanged += () => changes.Add(1);

        await host.NotifyClientAsync("contributions.register", payload);
        await host.SyncAsync();

        Assert.Equal("kept", Assert.Single(client.Commands).Definition.Name);
        Assert.Empty(changes.Snapshot());
        Assert.Equal(HostStatus.Running, client.Status);
        Assert.NotEmpty(client.Diagnostics);
    }

    [Fact]
    public async Task Malformed_entries_are_dropped_while_valid_siblings_survive()
    {
        await using var host = new FakeHost();
        await using var client = ClientFactory.Create(host);
        await client.StartConnectedAsync(host);

        await host.NotifyClientAsync("contributions.register",
            "{\"pluginId\":\"alpha\",\"contributions\":{"
            + "\"commands\":[{\"name\":\"ok\",\"description\":\"d\",\"argumentHint\":\"\"},{\"description\":\"no name\"},{\"name\":\"\"}],"
            + "\"workflows\":[{\"name\":\"w\",\"description\":\"d\",\"body\":\"steps\"},{\"name\":\"nobody\",\"description\":\"d\"}],"
            + "\"agents\":[{\"name\":\"a\",\"description\":\"d\",\"prompt\":\"p\",\"model\":{\"model\":\"m\",\"thinking\":1}}],"
            + "\"promptContributors\":[{\"id\":\"p1\"},{}]"
            + "}}");
        await host.SyncAsync();

        Assert.Equal("ok", Assert.Single(client.Commands).Definition.Name);
        Assert.Equal("w", Assert.Single(client.Workflows).Name);
        Assert.Equal("a", Assert.Single(client.Agents).Name);
        Assert.Equal(new PluginPromptReference("alpha", "p1"), Assert.Single(client.PromptContributors));
        Assert.True(client.Diagnostics.Count >= 4);
    }

    [Fact]
    public async Task Unknown_agent_thinking_is_dropped_with_a_diagnostic_not_coerced()
    {
        await using var host = new FakeHost();
        await using var client = ClientFactory.Create(host);
        await client.StartConnectedAsync(host);

        await host.NotifyClientAsync("contributions.register",
            "{\"pluginId\":\"alpha\",\"contributions\":{"
            + "\"agents\":["
            + "{\"name\":\"bad\",\"description\":\"d\",\"prompt\":\"p\",\"model\":{\"model\":\"m\",\"thinking\":99}},"
            + "{\"name\":\"good\",\"description\":\"d\",\"prompt\":\"p\",\"model\":{\"model\":\"m\",\"thinking\":2}}"
            + "]}}");
        await host.SyncAsync();

        var agent = Assert.Single(client.Agents);
        Assert.Equal("good", agent.Name);
        Assert.Equal(ThinkingEffort.Medium, agent.Model.Thinking);
        BoundRoots.AssertDiagnostic(client, "bad");
    }

    [Fact]
    public async Task Panels_are_acknowledged_but_not_registered_as_runnable_views()
    {
        await using var host = new FakeHost();
        await using var client = ClientFactory.Create(host);
        await client.StartConnectedAsync(host);

        await host.NotifyClientAsync("contributions.register",
            "{\"pluginId\":\"alpha\",\"contributions\":{"
            + "\"commands\":[{\"name\":\"c\",\"description\":\"d\",\"argumentHint\":\"\"}],"
            + "\"panels\":[{\"id\":\"p\",\"title\":\"t\",\"schema\":{\"kind\":\"text\",\"definition\":{}}}]"
            + "}}");
        await host.SyncAsync();

        Assert.Equal("c", Assert.Single(client.Commands).Definition.Name);
        Assert.Empty(client.Workflows);
        Assert.Empty(client.Agents);
        BoundRoots.AssertDiagnostic(client, "panel");
    }

    [Fact]
    public async Task Reserved_run_code_identity_cannot_register_tools_or_contributions()
    {
        await using var host = new FakeHost();
        await using var client = ClientFactory.Create(host);
        await client.StartConnectedAsync(host);

        await host.NotifyClientAsync("tools.register",
            """{"pluginId":"$run_code","tools":[{"name":"evil","description":"d","inputSchemaJson":"{}","sideEffect":0}]}""");
        await host.NotifyClientAsync("contributions.register",
            "{\"pluginId\":\"$run_code\",\"contributions\":{"
            + "\"commands\":[{\"name\":\"evil\",\"description\":\"d\",\"argumentHint\":\"\"}]}}");
        await host.SyncAsync();

        Assert.Empty(client.Tools);
        Assert.Empty(client.Commands);
        Assert.Equal(2, client.Diagnostics.Count(entry => entry.Contains("$run_code", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Tool_named_run_code_is_ignored_while_its_sibling_survives()
    {
        await using var host = new FakeHost();
        await using var client = ClientFactory.Create(host);
        await client.StartConnectedAsync(host);

        await host.NotifyClientAsync("tools.register",
            """{"pluginId":"alpha","tools":[{"name":"run_code","description":"shadow","inputSchemaJson":"{}","sideEffect":0},{"name":"real","description":"d","inputSchemaJson":"{}","sideEffect":0}]}""");
        await host.SyncAsync();

        Assert.Equal("real", Assert.Single(client.Tools).Definition.Name);
        BoundRoots.AssertDiagnostic(client, "run_code");
    }

    [Fact]
    public async Task Malformed_unregister_payloads_are_ignored()
    {
        await using var host = new FakeHost();
        await using var client = ClientFactory.Create(host);
        await client.StartConnectedAsync(host);
        await host.NotifyClientAsync("contributions.register", Contributions("alpha",
            """{"name":"a","description":"d","argumentHint":""}""", null, null, null));
        await host.SyncAsync();

        foreach (var payload in new[] { """{"pluginId":5}""", """{"pluginId":""}""", """{"other":"x"}""", "[]", "null" })
        {
            await host.NotifyClientAsync("contributions.unregister", payload);
        }

        await host.SyncAsync();

        Assert.Equal("a", Assert.Single(client.Commands).Definition.Name);
        Assert.Equal(HostStatus.Running, client.Status);
    }

    private static string Contributions(
        string pluginId, string? command, string? workflow, string? agent, string? prompt)
    {
        var parts = new List<string>();
        if (command is not null)
        {
            parts.Add("\"commands\":[" + command + "]");
        }

        if (workflow is not null)
        {
            parts.Add("\"workflows\":[" + workflow + "]");
        }

        if (agent is not null)
        {
            parts.Add("\"agents\":[" + agent + "]");
        }

        if (prompt is not null)
        {
            parts.Add("\"promptContributors\":[" + prompt + "]");
        }

        return "{\"pluginId\":\"" + pluginId + "\",\"contributions\":{" + string.Join(',', parts) + "}}";
    }
}
