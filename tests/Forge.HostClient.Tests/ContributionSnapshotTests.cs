using Forge.Abstractions;
using Forge.Ipc;
using Xunit;
using Sdk = Forge.Plugin.Sdk;

namespace Forge.HostClient.Tests;

/// <summary>Non-UI contribution snapshots: registration, replacement, generation tagging, revocation,
/// name-clash resolution, malformed payloads and SDK validation.</summary>
public sealed class ContributionSnapshotTests
{
    private const string Register = Sdk.ExtensionProtocol.ContributionsRegister;
    private const string Unregister = Sdk.ExtensionProtocol.ContributionsUnregister;

    [Fact]
    public async Task Register_builds_every_non_tool_view()
    {
        await using var host = new FakeHost();
        await using var client = await ClientFactory.StartAsync(host);

        await RegisterAsync(host, "alpha",
            new Sdk.CommandDeclaration("hi", "says hi", "who"),
            new Sdk.WorkflowDeclaration("guide", "how to", "# Steps"),
            new Sdk.AgentDeclaration(
                "helper", "helps", "Be helpful.", new Sdk.AgentModelSettings("m", Sdk.AgentThinkingEffort.Low, 64), ["search"]),
            new Sdk.PromptContributorDeclaration("tone"));

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
        await using var client = await ClientFactory.StartAsync(host);
        var changes = client.TrackContributionsChanged();
        await RegisterAsync(host, "alpha", Command("old"), new Sdk.WorkflowDeclaration("w", "d", "b"), null, null);

        await RegisterAsync(host, "alpha", Command("fresh"), null, null, null);

        Assert.Equal("fresh", Assert.Single(client.Commands).Definition.Name);
        Assert.Empty(client.Workflows);
        Assert.Equal(2, changes.Snapshot().Count);
    }

    [Fact]
    public async Task Same_set_registration_updates_the_generation_without_an_event_or_new_proxies()
    {
        await using var host = new FakeHost();
        await using var client = await ClientFactory.StartAsync(host);
        var payload = Contributions("alpha", Command("hi"), null, null, null);
        await host.NotifyAndSyncAsync(Register, payload);
        var changes = client.TrackContributionsChanged();
        var before = client.Commands;

        await host.NotifyAndSyncAsync(Register, payload);

        Assert.Empty(changes.Snapshot());
        Assert.Same(before, client.Commands);
    }

    [Fact]
    public async Task Unregister_removes_every_non_tool_contribution_of_that_plugin_only()
    {
        await using var host = new FakeHost();
        await using var client = await ClientFactory.StartAsync(host);
        var changes = client.TrackContributionsChanged();
        await RegisterAsync(host, "alpha", Command("a"), null, null, null);
        await RegisterAsync(host, "beta", Command("b"), null, null, new Sdk.PromptContributorDeclaration("p"));

        await UnregisterAsync(host, "alpha");

        Assert.Equal("b", Assert.Single(client.Commands).Definition.Name);
        Assert.Equal("beta", Assert.Single(client.PromptContributors).PluginId);
        Assert.Equal(3, changes.Snapshot().Count);
    }

    [Fact]
    public async Task Unregister_of_an_unknown_plugin_fires_no_event()
    {
        await using var host = new FakeHost();
        await using var client = await ClientFactory.StartAsync(host);
        await RegisterAsync(host, "alpha", Command("a"), null, null, null);
        var changes = client.TrackContributionsChanged();

        await UnregisterAsync(host, "ghost");

        Assert.Empty(changes.Snapshot());
        Assert.Equal("a", Assert.Single(client.Commands).Definition.Name);
    }

    [Fact]
    public async Task Reconnect_without_re_registration_drops_stale_contributions_with_one_event()
    {
        await using var host = new FakeHost();
        await using var client = await host.StartTrackedAsync(out var statuses);
        await RegisterAsync(host, "alpha", Command("a"), null, null, new Sdk.PromptContributorDeclaration("p"));
        Assert.Single(client.Commands);

        await host.DropAndAwaitFailedAsync(statuses);
        var changes = client.TrackContributionsChanged();
        using var limit = TestLimit.Create();

        await client.ReconnectAsync(host, limit.Token);

        Assert.Equal(HostStatus.Running, client.Status);
        Assert.Empty(client.Commands);
        Assert.Empty(client.PromptContributors);
        Assert.Single(changes.Snapshot());
    }

    [Fact]
    public async Task Revoked_proxy_is_stale_while_a_new_registration_is_usable()
    {
        await using var host = new FakeHost();
        host.On(Sdk.ExtensionProtocol.PluginCommand, (_, _) => throw new FakeHostError("unknown_command", "command 'hi' is not installed"));
        await using var client = await ClientFactory.StartAsync(host, approval: Approvals.AllowAll);
        using var limit = TestLimit.Create();
        await RegisterAsync(host, "alpha", Command("hi"), null, null, null);
        var stale = Assert.Single(client.Commands);
        var directory = BoundRoots.AbsoluteDir("revoke");
        var context = new CommandContext("sess", directory);

        await UnregisterAsync(host, "alpha");
        Assert.Empty(client.Commands);

        var staleResult = await stale.ExecuteAsync("args", context, limit.Token);
        Assert.True(staleResult.IsError);
        Assert.Contains("alpha", staleResult.Output);
        Assert.Contains("hi", staleResult.Output);

        host.Answer(Sdk.ExtensionProtocol.PluginCommand, new Sdk.CommandOutput("fresh-ok"));
        await RegisterAsync(host, "alpha", Command("hi"), null, null, null);
        var current = Assert.Single(client.Commands);

        Assert.NotSame(stale, current);
        var fresh = await current.ExecuteAsync("args", context, limit.Token);
        Assert.Equal(new CommandResult("fresh-ok", null, false), fresh);
    }

    [Fact]
    public async Task Command_name_clash_is_deterministic_first_plugin_id_wins_with_a_diagnostic()
    {
        await using var host = new FakeHost();
        await using var client = await ClientFactory.StartAsync(host);
        await RegisterAsync(host, "beta", Command("dup", "from beta"), null, null, null);
        await RegisterAsync(host, "alpha", Command("dup", "from alpha"), null, null, null);

        var winner = Assert.Single(client.Commands);
        Assert.Equal("alpha", ((ExtensionHostCommand)winner).PluginId);
        Assert.Equal("from alpha", winner.Definition.Description);
        BoundRoots.AssertDiagnostic(client, "clashes");
        BoundRoots.AssertDiagnostic(client, "'beta'");
    }

    [Theory]
    [InlineData("empty array")]
    [InlineData("text")]
    [InlineData("nil")]
    [InlineData("map")]
    [InlineData("no payload")]
    [InlineData("empty plugin id")]
    [InlineData("numeric contributions")]
    [InlineData("text contributions")]
    [InlineData("nil command name")]
    public async Task Malformed_complete_snapshot_is_atomically_refused_and_keeps_the_previous_state(string shape)
    {
        await using var host = new FakeHost();
        await using var client = await ClientFactory.StartAsync(host);
        await RegisterAsync(host, "alpha", Command("kept"), null, null, null);
        var changes = client.TrackContributionsChanged();

        await host.NotifyAndSyncAsync(Register, MalformedSnapshot(shape));

        Assert.Equal("kept", Assert.Single(client.Commands).Definition.Name);
        Assert.Empty(changes.Snapshot());
        Assert.Equal(HostStatus.Running, client.Status);
        Assert.NotEmpty(client.Diagnostics);
    }

    [Fact]
    public async Task Malformed_entries_are_dropped_while_valid_siblings_survive()
    {
        await using var host = new FakeHost();
        await using var client = await ClientFactory.StartAsync(host);

        await host.NotifyAndSyncAsync(Register, HostWire.Contributions("alpha", new Sdk.PluginContributionSet
        {
            Commands = [Command("ok"), Command(""), Command("   ")],
            Workflows = [new Sdk.WorkflowDeclaration("w", "d", "steps"), new Sdk.WorkflowDeclaration("nobody", "d", "")],
            Agents = [new Sdk.AgentDeclaration("a", "d", "p", new Sdk.AgentModelSettings("m", Sdk.AgentThinkingEffort.Low))],
            PromptContributors = [new Sdk.PromptContributorDeclaration("p1"), new Sdk.PromptContributorDeclaration(" ")],
        }));

        Assert.Equal("ok", Assert.Single(client.Commands).Definition.Name);
        Assert.Equal("w", Assert.Single(client.Workflows).Name);
        Assert.Equal("a", Assert.Single(client.Agents).Name);
        Assert.Equal(new PluginPromptReference("alpha", "p1"), Assert.Single(client.PromptContributors));
        Assert.True(client.Diagnostics.Count >= 4);
    }

    [Fact]
    public async Task Nil_entries_are_dropped_while_valid_siblings_survive()
    {
        await using var host = new FakeHost();
        await using var client = await ClientFactory.StartAsync(host);

        // ["alpha", [[], [nil, ok], [nil], [nil], [nil, p1]]]: a nil entry cannot be written through the typed lists,
        // so the snapshot goes out raw; each nil decodes as a null declaration and is dropped on its own.
        await host.NotifyAndSyncAsync(Register, Raw(
            [0x92],
            Pack("alpha"),
            [0x95, 0x90, 0x92, 0xc0],
            Pack(Command("ok")),
            [0x91, 0xc0, 0x91, 0xc0, 0x92, 0xc0],
            Pack(new Sdk.PromptContributorDeclaration("p1"))));

        Assert.Equal("ok", Assert.Single(client.Commands).Definition.Name);
        Assert.Empty(client.Workflows);
        Assert.Empty(client.Agents);
        Assert.Equal(new PluginPromptReference("alpha", "p1"), Assert.Single(client.PromptContributors));
        Assert.True(client.Diagnostics.Count >= 4);
    }

    [Fact]
    public async Task Unknown_agent_thinking_is_dropped_with_a_diagnostic_not_coerced()
    {
        await using var host = new FakeHost();
        await using var client = await ClientFactory.StartAsync(host);

        await host.NotifyAndSyncAsync(Register, HostWire.Contributions("alpha", new Sdk.PluginContributionSet
        {
            Agents =
            [
                new Sdk.AgentDeclaration("bad", "d", "p", new Sdk.AgentModelSettings("m", (Sdk.AgentThinkingEffort)99)),
                new Sdk.AgentDeclaration("good", "d", "p", new Sdk.AgentModelSettings("m", Sdk.AgentThinkingEffort.Medium)),
            ],
        }));

        var agent = Assert.Single(client.Agents);
        Assert.Equal("good", agent.Name);
        Assert.Equal(ThinkingEffort.Medium, agent.Model.Thinking);
        BoundRoots.AssertDiagnostic(client, "bad");
    }

    [Fact]
    public async Task Panels_are_acknowledged_but_not_registered_as_runnable_views()
    {
        await using var host = new FakeHost();
        await using var client = await ClientFactory.StartAsync(host);

        await host.NotifyAndSyncAsync(Register, HostWire.Contributions("alpha", new Sdk.PluginContributionSet
        {
            Commands = [Command("c")],
            Panels = [new Sdk.PanelDeclaration("p", "t", new Sdk.UiSchema("text", "{}"))],
        }));

        Assert.Equal("c", Assert.Single(client.Commands).Definition.Name);
        Assert.Empty(client.Workflows);
        Assert.Empty(client.Agents);
        BoundRoots.AssertDiagnostic(client, "panel");
    }

    [Fact]
    public async Task Reserved_run_code_identity_cannot_register_tools_or_contributions()
    {
        await using var host = new FakeHost();
        await using var client = await ClientFactory.StartAsync(host);

        await host.NotifyAndSyncAsync(Sdk.ExtensionProtocol.ToolsRegister, HostWire.Tools("$run_code", HostWire.Tool("evil")));
        await host.NotifyAndSyncAsync(
            Register, HostWire.Contributions("$run_code", new Sdk.PluginContributionSet { Commands = [Command("evil")] }));

        Assert.Empty(client.Tools);
        Assert.Empty(client.Commands);
        Assert.Equal(2, client.Diagnostics.Count(entry => entry.Contains("$run_code", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Tool_named_run_code_is_ignored_while_its_sibling_survives()
    {
        await using var host = new FakeHost();
        await using var client = await ClientFactory.StartAsync(host);

        await host.NotifyAndSyncAsync(
            Sdk.ExtensionProtocol.ToolsRegister,
            HostWire.Tools("alpha", HostWire.Tool("run_code", description: "shadow"), HostWire.Tool("real")));

        Assert.Equal("real", Assert.Single(client.Tools).Definition.Name);
        BoundRoots.AssertDiagnostic(client, "run_code");
    }

    [Fact]
    public async Task Malformed_unregister_payloads_are_ignored()
    {
        await using var host = new FakeHost();
        await using var client = await ClientFactory.StartAsync(host);
        await RegisterAsync(host, "alpha", Command("a"), null, null, null);

        // [5], [""], [nil], [], nil and no payload: none names a plugin.
        RawPayload[] payloads =
        [
            Raw([0x91], Pack(5)),
            HostWire.Payload(HostWire.Plugin(string.Empty)),
            Raw([0x91, 0xc0]),
            Raw([0x90]),
            HostWire.Nil,
            RawPayload.None,
        ];
        foreach (var payload in payloads)
        {
            await host.NotifyClientAsync(Unregister, payload);
        }

        await host.SyncAsync();

        Assert.Equal("a", Assert.Single(client.Commands).Definition.Name);
        Assert.Equal(HostStatus.Running, client.Status);
    }

    /// <summary>Registers <paramref name="pluginId"/>'s contribution snapshot and waits until the client applied it.</summary>
    private static async Task RegisterAsync(
        FakeHost host,
        string pluginId,
        Sdk.CommandDeclaration? command,
        Sdk.WorkflowDeclaration? workflow,
        Sdk.AgentDeclaration? agent,
        Sdk.PromptContributorDeclaration? prompt) =>
        await host.NotifyAndSyncAsync(Register, Contributions(pluginId, command, workflow, agent, prompt));

    /// <summary>Removes every contribution of <paramref name="pluginId"/> and waits until the client applied it.</summary>
    private static async Task UnregisterAsync(FakeHost host, string pluginId) =>
        await host.NotifyAndSyncAsync(Unregister, HostWire.Plugin(pluginId));

    private static Sdk.CommandDeclaration Command(string name, string description = "d") => new(name, description, "");

    private static Sdk.PluginContributionsNotification Contributions(
        string pluginId,
        Sdk.CommandDeclaration? command,
        Sdk.WorkflowDeclaration? workflow,
        Sdk.AgentDeclaration? agent,
        Sdk.PromptContributorDeclaration? prompt) =>
        HostWire.Contributions(pluginId, new Sdk.PluginContributionSet
        {
            Commands = command is null ? [] : [command],
            Workflows = workflow is null ? [] : [workflow],
            Agents = agent is null ? [] : [agent],
            PromptContributors = prompt is null ? [] : [prompt],
        });

    /// <summary>A <c>contributions.register</c> payload that carries no usable snapshot.</summary>
    private static RawPayload MalformedSnapshot(string shape) => shape switch
    {
        "empty array" => Raw([0x90]),
        "text" => HostWire.Payload("text"),
        "nil" => HostWire.Nil,
        // An empty map: the notification is an array, so a map is not a notification at all.
        "map" => Raw([0x80]),
        "no payload" => RawPayload.None,
        "empty plugin id" => HostWire.Payload(HostWire.Contributions(string.Empty, new Sdk.PluginContributionSet())),
        "numeric contributions" => Raw([0x92], Pack("alpha"), Pack(5)),
        "text contributions" => Raw([0x92], Pack("alpha"), Pack("x")),
        // ["alpha", [[], [<bad command>]]]: declaration members are non-nullable, so one nil makes the snapshot
        // undecodable as a whole.
        "nil command name" => Raw([0x92], Pack("alpha"), [0x92, 0x90, 0x91, 0x93, 0xc0], Pack("d"), Pack("")),
        _ => throw new ArgumentOutOfRangeException(nameof(shape), shape, null),
    };

    private static byte[] Pack<T>(T value) => IpcSerializer.Serialize(value);

    private static RawPayload Raw(params byte[][] parts) => new(parts.SelectMany(part => part).ToArray());
}
