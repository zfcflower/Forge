using System.Text.Json;
using Forge.Abstractions;
using Xunit;

namespace Forge.HostClient.Tests;

public sealed class ExtensionHostClientToolRegistryTests
{
    private const string EchoSchema = """{"type":"object","properties":{"text":{"type":"string"}}}""";

    [Fact]
    public async Task Register_builds_one_proxy_per_declaration_grouped_by_plugin()
    {
        await using var host = new FakeHost();
        await using var client = ClientFactory.Create(host);
        await client.StartConnectedAsync(host);

        await host.NotifyClientAsync("tools.register", Register("alpha",
            Declaration("search", "Searches the workspace", EchoSchema, 0),
            Declaration("write", "Writes a file", null, 1)));
        await host.SyncAsync();

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
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Blank_input_schema_is_exposed_as_an_empty_object_schema(string? schema)
    {
        await using var host = new FakeHost();
        await using var client = ClientFactory.Create(host);
        await client.StartConnectedAsync(host);

        await host.NotifyClientAsync("tools.register", Register("alpha", Declaration("noargs", "d", schema, 0)));
        await host.SyncAsync();

        Assert.Equal("{}", Assert.Single(client.Tools).Definition.InputSchemaJson);
    }

    [Theory]
    [InlineData("0", ToolSideEffect.ReadOnly)]
    [InlineData("1", ToolSideEffect.Write)]
    [InlineData("2", ToolSideEffect.Execute)]
    [InlineData("7", ToolSideEffect.Execute)]
    [InlineData("-1", ToolSideEffect.Execute)]
    [InlineData("\"ReadOnly\"", ToolSideEffect.ReadOnly)]
    [InlineData("\"write\"", ToolSideEffect.Write)]
    [InlineData("\"EXECUTE\"", ToolSideEffect.Execute)]
    [InlineData("\"Bogus\"", ToolSideEffect.Execute)]
    [InlineData("null", ToolSideEffect.Execute)]
    [InlineData("{}", ToolSideEffect.Execute)]
    public async Task Side_effect_accepts_ordinal_or_name_and_unknown_values_map_to_Execute(string rawSideEffect, ToolSideEffect expected)
    {
        await using var host = new FakeHost();
        await using var client = ClientFactory.Create(host);
        await client.StartConnectedAsync(host);

        await host.NotifyClientAsync("tools.register",
            $$"""{"pluginId":"alpha","tools":[{"name":"t","description":"d","inputSchemaJson":"{}","sideEffect":{{rawSideEffect}}}]}""");
        await host.SyncAsync();

        Assert.Equal(expected, Assert.Single(client.Tools).SideEffect);
    }

    [Fact]
    public async Task Missing_side_effect_maps_to_Execute()
    {
        await using var host = new FakeHost();
        await using var client = ClientFactory.Create(host);
        await client.StartConnectedAsync(host);

        await host.NotifyClientAsync("tools.register",
            """{"pluginId":"alpha","tools":[{"name":"t","description":"d","inputSchemaJson":"{}"}]}""");
        await host.SyncAsync();

        Assert.Equal(ToolSideEffect.Execute, Assert.Single(client.Tools).SideEffect);
    }

    [Fact]
    public async Task Registering_a_new_plugin_fires_ToolsChanged_once_and_keeps_other_plugins()
    {
        await using var host = new FakeHost();
        await using var client = ClientFactory.Create(host);
        await client.StartConnectedAsync(host);
        var changes = new EventLog<int>();
        client.ToolsChanged += () => changes.Add(1);

        await host.NotifyClientAsync("tools.register", Register("alpha", Declaration("a1", "d", null, 0)));
        await host.NotifyClientAsync("tools.register", Register("beta", Declaration("b1", "d", null, 0), Declaration("b2", "d", null, 0)));
        await host.SyncAsync();

        Assert.Equal(2, changes.Snapshot().Count);
        Assert.Equal(new[] { "a1", "b1", "b2" }, client.Tools.Select(tool => tool.Definition.Name).OrderBy(name => name, StringComparer.Ordinal).ToArray());
        Assert.Equal(new[] { "alpha", "beta" }, client.ToolsByPlugin.Keys.OrderBy(key => key, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task Re_registering_a_plugin_replaces_its_previous_set_and_fires_ToolsChanged()
    {
        await using var host = new FakeHost();
        await using var client = ClientFactory.Create(host);
        await client.StartConnectedAsync(host);
        var changes = new EventLog<int>();
        client.ToolsChanged += () => changes.Add(1);
        await host.NotifyClientAsync("tools.register", Register("alpha", Declaration("old", "d", null, 0), Declaration("older", "d", null, 0)));
        await host.SyncAsync();
        var previous = client.Tools;

        await host.NotifyClientAsync("tools.register", Register("alpha", Declaration("fresh", "d", null, 1)));
        await host.SyncAsync();

        Assert.Equal(2, changes.Snapshot().Count);
        Assert.Equal("fresh", Assert.Single(client.Tools).Definition.Name);
        Assert.Equal(new[] { "fresh" }, client.ToolsByPlugin["alpha"].Select(tool => tool.Definition.Name).ToArray());
        Assert.Equal(2, previous.Count);
    }

    [Fact]
    public async Task Identical_re_registration_does_not_fire_ToolsChanged()
    {
        await using var host = new FakeHost();
        await using var client = ClientFactory.Create(host);
        await client.StartConnectedAsync(host);
        var changes = new EventLog<int>();
        client.ToolsChanged += () => changes.Add(1);
        var payload = Register("alpha", Declaration("echo", "d", EchoSchema, 0));
        await host.NotifyClientAsync("tools.register", payload);
        await host.SyncAsync();

        await host.NotifyClientAsync("tools.register", payload);
        await host.SyncAsync();

        Assert.Single(changes.Snapshot());
    }

    [Fact]
    public async Task Unregister_removes_the_plugin_tools_and_fires_ToolsChanged()
    {
        await using var host = new FakeHost();
        await using var client = ClientFactory.Create(host);
        await client.StartConnectedAsync(host);
        var changes = new EventLog<int>();
        client.ToolsChanged += () => changes.Add(1);
        await host.NotifyClientAsync("tools.register", Register("alpha", Declaration("a1", "d", null, 0)));
        await host.NotifyClientAsync("tools.register", Register("beta", Declaration("b1", "d", null, 0)));
        await host.SyncAsync();

        await host.NotifyClientAsync("tools.unregister", """{"pluginId":"alpha"}""");
        await host.SyncAsync();

        Assert.Equal(3, changes.Snapshot().Count);
        Assert.Equal("b1", Assert.Single(client.Tools).Definition.Name);
        Assert.False(client.ToolsByPlugin.ContainsKey("alpha"));
    }

    [Fact]
    public async Task Unregister_of_an_unknown_plugin_fires_nothing()
    {
        await using var host = new FakeHost();
        await using var client = ClientFactory.Create(host);
        await client.StartConnectedAsync(host);
        await host.NotifyClientAsync("tools.register", Register("alpha", Declaration("a1", "d", null, 0)));
        await host.SyncAsync();
        var changes = new EventLog<int>();
        client.ToolsChanged += () => changes.Add(1);

        await host.NotifyClientAsync("tools.unregister", """{"pluginId":"ghost"}""");
        await host.SyncAsync();

        Assert.Empty(changes.Snapshot());
        Assert.Equal("a1", Assert.Single(client.Tools).Definition.Name);
    }

    [Fact]
    public async Task Registering_an_empty_tool_list_removes_the_plugin()
    {
        await using var host = new FakeHost();
        await using var client = ClientFactory.Create(host);
        await client.StartConnectedAsync(host);
        var changes = new EventLog<int>();
        client.ToolsChanged += () => changes.Add(1);
        await host.NotifyClientAsync("tools.register", Register("alpha", Declaration("a1", "d", null, 0)));
        await host.SyncAsync();

        await host.NotifyClientAsync("tools.register", """{"pluginId":"alpha","tools":[]}""");
        await host.SyncAsync();

        Assert.Equal(2, changes.Snapshot().Count);
        Assert.Empty(client.Tools);
        Assert.Empty(client.ToolsByPlugin);
    }

    [Fact]
    public async Task Empty_registration_for_an_unknown_plugin_fires_nothing()
    {
        await using var host = new FakeHost();
        await using var client = ClientFactory.Create(host);
        await client.StartConnectedAsync(host);
        var changes = new EventLog<int>();
        client.ToolsChanged += () => changes.Add(1);

        await host.NotifyClientAsync("tools.register", """{"pluginId":"ghost","tools":[]}""");
        await host.SyncAsync();

        Assert.Empty(changes.Snapshot());
        Assert.Empty(client.Tools);
    }

    [Fact]
    public async Task A_tools_snapshot_taken_before_a_change_is_not_modified_by_it()
    {
        await using var host = new FakeHost();
        await using var client = ClientFactory.Create(host);
        await client.StartConnectedAsync(host);
        await host.NotifyClientAsync("tools.register", Register("alpha", Declaration("a1", "d", null, 0)));
        await host.SyncAsync();
        var before = client.Tools;

        await host.NotifyClientAsync("tools.register", Register("beta", Declaration("b1", "d", null, 0)));
        await host.NotifyClientAsync("tools.unregister", """{"pluginId":"alpha"}""");
        await host.SyncAsync();

        Assert.Equal(new[] { "a1" }, before.Select(tool => tool.Definition.Name).ToArray());
        Assert.Equal(new[] { "b1" }, client.Tools.Select(tool => tool.Definition.Name).ToArray());
    }

    [Fact]
    public async Task Invalid_entries_are_dropped_while_valid_siblings_are_kept()
    {
        await using var host = new FakeHost();
        await using var client = ClientFactory.Create(host);
        await client.StartConnectedAsync(host);

        await host.NotifyClientAsync("tools.register", """
            {"pluginId":"alpha","tools":[
              {"name":"first","description":"d","inputSchemaJson":"{}","sideEffect":0},
              null,
              {"description":"no name","sideEffect":0},
              {"name":"","description":"empty name","sideEffect":0},
              {"name":"second","description":"d","inputSchemaJson":"{}","sideEffect":1}
            ]}
            """);
        await host.SyncAsync();

        Assert.Equal(new[] { "first", "second" }, client.Tools.Select(tool => tool.Definition.Name).ToArray());
        Assert.Equal(ToolSideEffect.Write, client.Tools[1].SideEffect);
    }

    [Theory]
    [InlineData("""{"tools":[{"name":"t","sideEffect":0}]}""")]
    [InlineData("""{"pluginId":"","tools":[{"name":"t","sideEffect":0}]}""")]
    [InlineData("""{"pluginId":5,"tools":[{"name":"t","sideEffect":0}]}""")]
    [InlineData("""{"pluginId":"alpha","tools":"not-an-array"}""")]
    [InlineData("""{"pluginId":"alpha","tools":[{"name":"","sideEffect":0}]}""")]
    [InlineData("""{"pluginId":"alpha","tools":[]}""")]
    [InlineData("""[1,2,3]""")]
    [InlineData("\"text\"")]
    [InlineData("null")]
    [InlineData("{}")]
    public async Task Malformed_registration_payloads_are_ignored_and_the_client_keeps_running(string payload)
    {
        await using var host = new FakeHost();
        await using var client = ClientFactory.Create(host);
        await client.StartConnectedAsync(host);
        var changes = new EventLog<int>();
        client.ToolsChanged += () => changes.Add(1);

        await host.NotifyClientAsync("tools.register", payload);
        await host.SyncAsync();

        Assert.Empty(changes.Snapshot());
        Assert.Empty(client.Tools);
        Assert.Equal(HostStatus.Running, client.Status);

        await host.NotifyClientAsync("tools.register", Register("alpha", Declaration("after", "d", null, 0)));
        await host.SyncAsync();
        Assert.Equal("after", Assert.Single(client.Tools).Definition.Name);
    }

    [Theory]
    [InlineData("""{"pluginId":5}""")]
    [InlineData("""{"pluginId":""}""")]
    [InlineData("""{"version":"1.0"}""")]
    [InlineData("""[]""")]
    [InlineData("null")]
    public async Task Malformed_unregister_payloads_are_ignored(string payload)
    {
        await using var host = new FakeHost();
        await using var client = ClientFactory.Create(host);
        await client.StartConnectedAsync(host);
        await host.NotifyClientAsync("tools.register", Register("alpha", Declaration("a1", "d", null, 0)));
        await host.SyncAsync();

        await host.NotifyClientAsync("tools.unregister", payload);
        await host.SyncAsync();

        Assert.Equal("a1", Assert.Single(client.Tools).Definition.Name);
        Assert.Equal(HostStatus.Running, client.Status);
    }

    [Fact]
    public async Task Unknown_notification_operations_are_ignored()
    {
        await using var host = new FakeHost();
        await using var client = ClientFactory.Create(host);
        await client.StartConnectedAsync(host);
        var changes = new EventLog<int>();
        client.ToolsChanged += () => changes.Add(1);

        await host.NotifyClientAsync("tools.rename", Register("alpha", Declaration("a1", "d", null, 0)));
        await host.SyncAsync();

        Assert.Empty(changes.Snapshot());
        Assert.Empty(client.Tools);
        Assert.Equal(HostStatus.Running, client.Status);
    }

    [Fact]
    public async Task Declaration_with_a_wrongly_typed_field_does_not_drop_its_valid_siblings()
    {
        await using var host = new FakeHost();
        await using var client = ClientFactory.Create(host);
        await client.StartConnectedAsync(host);

        await host.NotifyClientAsync("tools.register", """
            {"pluginId":"alpha","tools":[
              {"name":"valid","description":"d","inputSchemaJson":"{}","sideEffect":0},
              {"name":5,"description":"wrong type","sideEffect":0}
            ]}
            """);
        await host.SyncAsync();

        Assert.Equal(new[] { "valid" }, client.Tools.Select(tool => tool.Definition.Name).ToArray());
    }

    [Fact]
    public async Task A_wrongly_typed_description_keeps_the_declaration_with_an_empty_description()
    {
        await using var host = new FakeHost();
        await using var client = ClientFactory.Create(host);
        await client.StartConnectedAsync(host);

        await host.NotifyClientAsync("tools.register", """
            {"pluginId":"alpha","tools":[
              {"name":"odd","description":5,"inputSchemaJson":"{}","sideEffect":0},
              {"name":"valid","description":"d","sideEffect":1}
            ]}
            """);
        await host.SyncAsync();

        Assert.Equal(new[] { "odd", "valid" }, client.Tools.Select(tool => tool.Definition.Name).ToArray());
        Assert.Equal(string.Empty, client.Tools[0].Definition.Description);
        Assert.Equal(ToolSideEffect.Write, client.Tools[1].SideEffect);
    }

    private static string Register(string pluginId, params string[] declarations) =>
        $$"""{"pluginId":"{{pluginId}}","tools":[{{string.Join(',', declarations)}}]}""";

    private static string Declaration(string name, string description, string? schema, int sideEffect)
    {
        var schemaJson = schema is null ? "null" : JsonSerializer.Serialize(schema);
        return $$"""{"name":"{{name}}","description":"{{description}}","inputSchemaJson":{{schemaJson}},"sideEffect":{{sideEffect}}}""";
    }
}
