using Forge.Abstractions;
using Forge.App.Agents;
using Forge.App.ViewModels.Settings;
using Forge.Core.Agents;
using Forge.Core.Storage;
using Xunit;

namespace Forge.App.Tests;

/// <summary>
/// The <c>tools</c> page's catalog is the installation's, not a session's: <see cref="ToolCatalog.Describe"/> reads the
/// composition's services, so it lists the product's tools with no tab open and before any turn has built a host — the page
/// used to go blank in exactly that state. What a session registered stays the resource window's reading, sized against this
/// same catalog.
/// </summary>
public sealed class ToolCatalogTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "forge-tool-catalog-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
            // A pooled handle can outlive the test; the temp directory is the OS's problem then.
        }
    }

    private sealed class ProbeTool(string name, ToolSideEffect effect = ToolSideEffect.ReadOnly) : ITool
    {
        public ToolDefinition Definition { get; } = new(name, "probe", "{}");

        public ToolSideEffect SideEffect => effect;

        public ValueTask<ToolResult> InvokeAsync(ToolCall call, ToolContext context, CancellationToken ct) =>
            ValueTask.FromResult(new ToolResult("ok"));
    }

    /// <summary>Services with no session behind them: no extension host, no git, no MCP, no web services and no providers.</summary>
    private AgentRuntimeServices Services(ISessionStore store) =>
        new(
            ProviderFactory: _ => throw new InvalidOperationException("no provider is built in this test"),
            HostClient: null,
            Store: store,
            Catalog: (ISessionCatalog)store,
            Todos: new SqliteTodoStore(directory),
            Model: new ModelSettings("test-model"))
        {
            FileTools = [new ProbeTool("read"), new ProbeTool("bash", ToolSideEffect.Execute)],
        };

    [Fact]
    public async Task The_catalog_is_a_property_of_the_installation_not_of_a_session()
    {
        Directory.CreateDirectory(directory);
        await using var store = new SqliteSessionStore(directory);

        var descriptors = ToolCatalog.Describe(Services(store));
        var byName = descriptors.ToDictionary(static descriptor => descriptor.Name, StringComparer.Ordinal);

        // The app's own tool families, named from the services rather than from a host that ran a turn.
        Assert.Equal(ToolGroups.Files, byName["read"].Group);
        Assert.Equal(ToolGroups.Files, byName["bash"].Group);

        // Core's built-ins: a static table, so one missing name is a page with a row silently gone.
        Assert.Equal(ToolGroups.Session, byName["todo"].Group);
        Assert.Equal(ToolGroups.Session, byName["set_title"].Group);
        Assert.Equal(ToolGroups.Agents, byName["request_review"].Group);
        Assert.Equal(ToolOrigin.BuiltIn, byName["spawn_agent"].Origin);

        // The spec tools were missing from the page for as long as this list was hand-copied per session.
        Assert.Equal(ToolGroups.Retrieval, byName["spec_get"].Group);
        Assert.Equal(ToolGroups.Retrieval, byName["spec_graph"].Group);
        Assert.Equal(ToolGroups.Retrieval, byName["spec_validate"].Group);

        // Nothing that this installation cannot register: no extension host means no plugin tools, no run_code and no
        // extension authoring tools, and no web services means no web group at all.
        Assert.DoesNotContain(descriptors, static descriptor => descriptor.Origin == ToolOrigin.Extension);
        Assert.DoesNotContain("run_code", byName.Keys);
        Assert.DoesNotContain(descriptors, static descriptor => descriptor.Group == ToolGroups.Web);
    }

    [Fact]
    public async Task Web_tools_are_listed_exactly_as_the_web_services_offer_them()
    {
        Directory.CreateDirectory(directory);
        await using var store = new SqliteSessionStore(directory);

        // web_fetch is offered with or without a search backend; the page lists what the factory returns rather than
        // deciding for itself, so the group can never claim a tool the session would not get.
        var services = Services(store);
        Assert.DoesNotContain(ToolCatalog.Describe(services), static descriptor => descriptor.Group == ToolGroups.Web);

        services.WebTools = () => [new ProbeTool("web_fetch")];
        var byName = ToolCatalog.Describe(services).ToDictionary(static descriptor => descriptor.Name, StringComparer.Ordinal);

        Assert.Equal(ToolGroups.Web, byName["web_fetch"].Group);
        Assert.Equal(ToolOrigin.BuiltIn, byName["web_fetch"].Origin);
    }

    [Fact]
    public async Task The_page_lists_the_file_group_first_and_sorts_every_group()
    {
        Directory.CreateDirectory(directory);
        await using var store = new SqliteSessionStore(directory);

        var groups = ToolsSettingsSectionViewModel.Build(ToolCatalog.Describe(Services(store)), preferences: null);

        // The fixed order starts with the file group, and rows inside a group are ordinal by name.
        Assert.Equal(["bash", "read"], groups[0].Tools.Select(static row => row.Name));
        Assert.All(groups, static group => Assert.NotEmpty(group.Tools));

        // The spec tools land in the retrieval group, and only there.
        var retrieval = groups.Where(static group => group.Tools.Any(static row => row.Name == "spec_get")).ToList();
        Assert.Single(retrieval);
        Assert.Contains(retrieval[0].Tools, static row => row.Name == "spec_validate");
    }

    [Fact]
    public async Task A_tool_another_feature_needs_carries_no_switch()
    {
        Directory.CreateDirectory(directory);
        await using var store = new SqliteSessionStore(directory);

        var byName = ToolCatalog.Describe(Services(store)).ToDictionary(static descriptor => descriptor.Name, StringComparer.Ordinal);

        // read_artifact is how the model reads back a spilled result, the sub-agent control tools are its only handle on
        // the children it already spawned, and skill is how the orchestration's index is made good on: switching any of
        // them off leaves a feature with no entry point, so the page offers no switch.
        Assert.False(byName["read_artifact"].CanDisable);
        Assert.False(byName["subagent_stop"].CanDisable);
        Assert.False(byName["skill"].CanDisable);

        // Everything else on the page is the user's to switch off, and the stored switch is what the next turn applies.
        Assert.True(byName["read"].CanDisable);
        Assert.True(byName["todo"].CanDisable);
        Assert.True(byName["spawn_agent"].CanDisable);
    }
}
