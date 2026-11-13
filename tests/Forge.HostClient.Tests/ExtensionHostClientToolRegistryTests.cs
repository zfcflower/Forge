using Forge.Abstractions;
using Forge.Ipc;
using Forge.Ipc.Protocol;
using Xunit;
using Sdk = Forge.Plugin.Sdk;

namespace Forge.HostClient.Tests;

public sealed class ExtensionHostClientToolRegistryTests
{
    private const string EchoSchema = """{"type":"object","properties":{"text":{"type":"string"}}}""";
    private const string Register = Sdk.ExtensionProtocol.ToolsRegister;
    private const string Unregister = Sdk.ExtensionProtocol.ToolsUnregister;

    [Fact]
    public async Task Register_builds_one_proxy_per_declaration_grouped_by_plugin()
    {
        await using var host = new FakeHost();
        await using var client = await ClientFactory.StartAsync(host);

        await host.NotifyAndSyncAsync(Register, HostWire.Tools("alpha",
            Declaration("search", "Searches the workspace", EchoSchema, Sdk.ToolSideEffect.ReadOnly),
            Declaration("write", "Writes a file", "", Sdk.ToolSideEffect.Write)));

        Assert.Equal(2, client.Tools.Count);
        var search = Assert.IsType<ExtensionHostTool>(client.Tools.Single(tool => tool.Definition.Name == "search"));
        Assert.Equal(new ToolDefinition("search", "Searches the workspace", EchoSchema), search.Definition);
        Assert.Equal(ToolSideEffect.ReadOnly, search.SideEffect);
        Assert.Equal("alpha", search.PluginId);
        Assert.Equal("alpha:search", search.ToString());

        var write = Assert.IsType<ExtensionHostTool>(client.Tools.Single(tool => tool.Definition.Name == "write"));
        Assert.Equal(new ToolDefinition("write", "Writes a file", "{}"), write.Definition);
        Assert.Equal(ToolSideEffect.Write, write.SideEffect);

        var group = client.ToolsByPlugin["alpha"];
        Assert.Equal(new[] { "search", "write" }, group.Select(tool => tool.Definition.Name).ToArray());
        Assert.Equal(client.Tools.Cast<object>(), group.Cast<object>());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Blank_input_schema_is_exposed_as_an_empty_object_schema(string schema)
    {
        await using var host = new FakeHost();
        await using var client = await ClientFactory.StartAsync(host);

        await host.NotifyAndSyncAsync(Register, HostWire.Tools("alpha", Declaration("noargs", "d", schema)));

        Assert.Equal("{}", Assert.Single(client.Tools).Definition.InputSchemaJson);
    }

    [Theory]
    [InlineData(0, ToolSideEffect.ReadOnly)]
    [InlineData(1, ToolSideEffect.Write)]
    [InlineData(2, ToolSideEffect.Execute)]
    [InlineData(7, ToolSideEffect.Execute)]
    [InlineData(-1, ToolSideEffect.Execute)]
    public async Task Side_effect_ordinals_map_and_undefined_values_map_to_Execute(int rawSideEffect, ToolSideEffect expected)
    {
        await using var host = new FakeHost();
        await using var client = await ClientFactory.StartAsync(host);

        await host.NotifyAndSyncAsync(Register,
            HostWire.Tools("alpha", Declaration("t", "d", "{}", (Sdk.ToolSideEffect)rawSideEffect)));

        Assert.Equal(expected, Assert.Single(client.Tools).SideEffect);
    }

    [Fact]
    public async Task Registering_a_new_plugin_fires_ToolsChanged_once_and_keeps_other_plugins()
    {
        await using var host = new FakeHost();
        await using var client = await ClientFactory.StartAsync(host);
        var changes = client.TrackToolsChanged();

        await host.NotifyClientAsync(Register, HostWire.Tools("alpha", Declaration("a1")));
        await host.NotifyClientAsync(Register, HostWire.Tools("beta", Declaration("b1"), Declaration("b2")));
        await host.SyncAsync();

        Assert.Equal(2, changes.Snapshot().Count);
        Assert.Equal(new[] { "a1", "b1", "b2" }, client.Tools.Select(tool => tool.Definition.Name).OrderBy(name => name, StringComparer.Ordinal).ToArray());
        Assert.Equal(new[] { "alpha", "beta" }, client.ToolsByPlugin.Keys.OrderBy(key => key, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task Re_registering_a_plugin_replaces_its_previous_set_and_fires_ToolsChanged()
    {
        await using var host = new FakeHost();
        await using var client = await ClientFactory.StartAsync(host);
        var changes = client.TrackToolsChanged();
        await host.NotifyAndSyncAsync(Register, HostWire.Tools("alpha", Declaration("old"), Declaration("older")));
        var previous = client.Tools;

        await host.NotifyAndSyncAsync(Register,
            HostWire.Tools("alpha", Declaration("fresh", sideEffect: Sdk.ToolSideEffect.Write)));

        Assert.Equal(2, changes.Snapshot().Count);
        Assert.Equal("fresh", Assert.Single(client.Tools).Definition.Name);
        Assert.Equal(new[] { "fresh" }, client.ToolsByPlugin["alpha"].Select(tool => tool.Definition.Name).ToArray());
        Assert.Equal(2, previous.Count);
    }

    [Fact]
    public async Task Identical_re_registration_does_not_fire_ToolsChanged()
    {
        await using var host = new FakeHost();
        await using var client = await ClientFactory.StartAsync(host);
        var changes = client.TrackToolsChanged();
        var payload = HostWire.Tools("alpha", Declaration("echo", "d", EchoSchema));
        await host.NotifyAndSyncAsync(Register, payload);

        await host.NotifyAndSyncAsync(Register, payload);

        Assert.Single(changes.Snapshot());
    }

    [Fact]
    public async Task Unregister_removes_the_plugin_tools_and_fires_ToolsChanged()
    {
        await using var host = new FakeHost();
        await using var client = await ClientFactory.StartAsync(host);
        var changes = client.TrackToolsChanged();
        await host.NotifyClientAsync(Register, HostWire.Tools("alpha", Declaration("a1")));
        await host.NotifyClientAsync(Register, HostWire.Tools("beta", Declaration("b1")));
        await host.SyncAsync();

        await host.NotifyAndSyncAsync(Unregister, HostWire.Plugin("alpha"));

        Assert.Equal(3, changes.Snapshot().Count);
        Assert.Equal("b1", Assert.Single(client.Tools).Definition.Name);
        Assert.False(client.ToolsByPlugin.ContainsKey("alpha"));
    }

    [Fact]
    public async Task Unregister_of_an_unknown_plugin_fires_nothing()
    {
        await using var host = new FakeHost();
        await using var client = await ClientFactory.StartAsync(host);
        await host.NotifyAndSyncAsync(Register, HostWire.Tools("alpha", Declaration("a1")));
        var changes = client.TrackToolsChanged();

        await host.NotifyAndSyncAsync(Unregister, HostWire.Plugin("ghost"));

        Assert.Empty(changes.Snapshot());
        Assert.Equal("a1", Assert.Single(client.Tools).Definition.Name);
    }

    [Fact]
    public async Task Registering_an_empty_tool_list_removes_the_plugin()
    {
        await using var host = new FakeHost();
        await using var client = await ClientFactory.StartAsync(host);
        var changes = client.TrackToolsChanged();
        await host.NotifyAndSyncAsync(Register, HostWire.Tools("alpha", Declaration("a1")));

        await host.NotifyAndSyncAsync(Register, HostWire.Tools("alpha"));

        Assert.Equal(2, changes.Snapshot().Count);
        Assert.Empty(client.Tools);
        Assert.Empty(client.ToolsByPlugin);
    }

    [Fact]
    public async Task Empty_registration_for_an_unknown_plugin_fires_nothing()
    {
        await using var host = new FakeHost();
        await using var client = await ClientFactory.StartAsync(host);
        var changes = client.TrackToolsChanged();

        await host.NotifyAndSyncAsync(Register, HostWire.Tools("ghost"));

        Assert.Empty(changes.Snapshot());
        Assert.Empty(client.Tools);
    }

    [Fact]
    public async Task A_tools_snapshot_taken_before_a_change_is_not_modified_by_it()
    {
        await using var host = new FakeHost();
        await using var client = await ClientFactory.StartAsync(host);
        await host.NotifyAndSyncAsync(Register, HostWire.Tools("alpha", Declaration("a1")));
        var before = client.Tools;

        await host.NotifyClientAsync(Register, HostWire.Tools("beta", Declaration("b1")));
        await host.NotifyClientAsync(Unregister, HostWire.Plugin("alpha"));
        await host.SyncAsync();

        Assert.Equal(new[] { "a1" }, before.Select(tool => tool.Definition.Name).ToArray());
        Assert.Equal(new[] { "b1" }, client.Tools.Select(tool => tool.Definition.Name).ToArray());
    }

    [Fact]
    public async Task Invalid_entries_are_dropped_while_valid_siblings_are_kept()
    {
        await using var host = new FakeHost();
        await using var client = await ClientFactory.StartAsync(host);

        // The nil entry cannot be written through the typed list, so the list goes out raw; a nil entry decodes as a
        // null declaration, which is dropped like the unnamed one.
        await host.NotifyAndSyncAsync(Register, RawTools("alpha",
            Pack(Declaration("first", "d", "{}")),
            [0xc0],
            Pack(Declaration("", "empty name")),
            Pack(Declaration("second", "d", "{}", Sdk.ToolSideEffect.Write))));

        Assert.Equal(new[] { "first", "second" }, client.Tools.Select(tool => tool.Definition.Name).ToArray());
        Assert.Equal(ToolSideEffect.Write, client.Tools[1].SideEffect);
    }

    [Theory]
    [InlineData("nil plugin id")]
    [InlineData("empty plugin id")]
    [InlineData("numeric plugin id")]
    [InlineData("tools not a list")]
    [InlineData("only an empty name")]
    [InlineData("empty tool list")]
    [InlineData("number list")]
    [InlineData("text")]
    [InlineData("nil")]
    [InlineData("empty array")]
    [InlineData("no payload")]
    public async Task Malformed_registration_payloads_are_ignored_and_the_client_keeps_running(string shape)
    {
        await using var host = new FakeHost();
        await using var client = await ClientFactory.StartAsync(host);
        var changes = client.TrackToolsChanged();

        await host.NotifyAndSyncAsync(Register, MalformedRegistration(shape));

        Assert.Empty(changes.Snapshot());
        Assert.Empty(client.Tools);
        Assert.Equal(HostStatus.Running, client.Status);

        await host.NotifyAndSyncAsync(Register, HostWire.Tools("alpha", Declaration("after")));
        Assert.Equal("after", Assert.Single(client.Tools).Definition.Name);
    }

    [Theory]
    [InlineData("numeric plugin id")]
    [InlineData("empty plugin id")]
    [InlineData("nil plugin id")]
    [InlineData("empty array")]
    [InlineData("nil")]
    [InlineData("no payload")]
    public async Task Malformed_unregister_payloads_are_ignored(string shape)
    {
        await using var host = new FakeHost();
        await using var client = await ClientFactory.StartAsync(host);
        await host.NotifyAndSyncAsync(Register, HostWire.Tools("alpha", Declaration("a1")));

        await host.NotifyAndSyncAsync(Unregister, MalformedUnregister(shape));

        Assert.Equal("a1", Assert.Single(client.Tools).Definition.Name);
        Assert.Equal(HostStatus.Running, client.Status);
    }

    [Fact]
    public async Task Unknown_notification_operations_are_ignored()
    {
        await using var host = new FakeHost();
        await using var client = await ClientFactory.StartAsync(host);
        var changes = client.TrackToolsChanged();

        await host.NotifyAndSyncAsync("tools.rename", HostWire.Tools("alpha", Declaration("a1")));

        Assert.Empty(changes.Snapshot());
        Assert.Empty(client.Tools);
        Assert.Equal(HostStatus.Running, client.Status);
    }

    [Theory]
    [InlineData("numeric name")]
    [InlineData("nil name")]
    [InlineData("nil description")]
    [InlineData("nil schema")]
    public async Task A_declaration_with_a_wrongly_typed_or_nil_field_makes_the_registration_undecodable_and_it_is_ignored(
        string shape)
    {
        await using var host = new FakeHost();
        await using var client = await ClientFactory.StartAsync(host);
        var changes = client.TrackToolsChanged();

        // ["alpha", [["valid","d","{}",0], <bad entry>]]: every member of a declaration is non-nullable and typed, so
        // one bad entry makes the whole registration undecodable; nothing of it is applied and the problem is diagnosed.
        await host.NotifyAndSyncAsync(Register, RawTools("alpha", Pack(Declaration("valid", "d", "{}")), shape switch
        {
            "numeric name" => Raw([0x94], Pack(5), Pack("wrong type"), Pack("{}"), Pack(0)).Bytes.ToArray(),
            "nil name" => RawDeclaration(null, "no name", "{}", 0),
            "nil description" => RawDeclaration("odd", null, "{}", 0),
            "nil schema" => RawDeclaration("odd", "d", null, 0),
            _ => throw new ArgumentOutOfRangeException(nameof(shape), shape, null),
        }));

        Assert.Empty(changes.Snapshot());
        Assert.Empty(client.Tools);
        Assert.Equal(HostStatus.Running, client.Status);
        Assert.Contains(client.Diagnostics, entry => entry.Contains(
            "tools.register carried no usable plugin id or tool list", StringComparison.Ordinal));
    }

    private static Sdk.ToolDeclaration Declaration(
        string name,
        string description = "d",
        string schema = "{}",
        Sdk.ToolSideEffect sideEffect = Sdk.ToolSideEffect.ReadOnly) =>
        new(name, description, schema, sideEffect);

    /// <summary>A <see cref="Sdk.ToolDeclaration"/> as raw MessagePack (a fixarray of four); a null field is written as
    /// nil, which the typed record refuses to serialize.</summary>
    private static byte[] RawDeclaration(string? name, string? description, string? schema, int sideEffect) =>
        Raw([0x94], PackOrNil(name), PackOrNil(description), PackOrNil(schema), Pack(sideEffect)).Bytes.ToArray();

    /// <summary>A <see cref="ToolsRegisterNotification"/> whose tool list is the given raw entries.</summary>
    private static RawPayload RawTools(string pluginId, params byte[][] declarations)
    {
        Assert.InRange(declarations.Length, 0, 15);
        return Raw([[0x92], Pack(pluginId), [(byte)(0x90 | declarations.Length)], .. declarations]);
    }

    private static byte[] PackOrNil(string? value) => value is null ? [0xc0] : Pack(value);

    private static RawPayload MalformedRegistration(string shape) => shape switch
    {
        "nil plugin id" => Raw([0x92, 0xc0, 0x91], Pack(Declaration("t"))),
        "empty plugin id" => HostWire.Payload(HostWire.Tools("", Declaration("t"))),
        "numeric plugin id" => Raw([0x92], Pack(5), [0x91], Pack(Declaration("t"))),
        "tools not a list" => Raw([0x92], Pack("alpha"), Pack("not-an-array")),
        "only an empty name" => HostWire.Payload(HostWire.Tools("alpha", Declaration(""))),
        "empty tool list" => HostWire.Payload(HostWire.Tools("alpha")),
        "number list" => HostWire.Payload(new[] { 1, 2, 3 }),
        "text" => HostWire.Payload("text"),
        "nil" => HostWire.Nil,
        "empty array" => Raw([0x90]),
        "no payload" => RawPayload.None,
        _ => throw new ArgumentOutOfRangeException(nameof(shape), shape, null),
    };

    private static RawPayload MalformedUnregister(string shape) => shape switch
    {
        "numeric plugin id" => Raw([0x91], Pack(5)),
        "empty plugin id" => HostWire.Payload(HostWire.Plugin("")),
        "nil plugin id" => Raw([0x91, 0xc0]),
        "empty array" => Raw([0x90]),
        "nil" => HostWire.Nil,
        "no payload" => RawPayload.None,
        _ => throw new ArgumentOutOfRangeException(nameof(shape), shape, null),
    };

    private static byte[] Pack<T>(T value) => IpcSerializer.Serialize(value);

    private static RawPayload Raw(params byte[][] parts) => new(parts.SelectMany(part => part).ToArray());
}
