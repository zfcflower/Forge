using Forge.Abstractions;
using Forge.App.Agents;
using Forge.App.Composition;
using Forge.App.Preferences;
using Forge.App.Services;
using Forge.Core.Tools.Web;
using Forge.HostClient;
using Forge.IO.Tools;
using Forge.Mcp;
using Forge.Workspace;
using Xunit;
using Loopback = Forge.App.Tests.Mcp.LoopbackMcpTransport;
using ScriptedServer = Forge.App.Tests.Mcp.ScriptedMcpServer;

namespace Forge.App.Tests.Agents;

/// <summary>
/// The <c>run_code</c> script surface the session runtime collects (wave W14): candidates are the plugin tools plus
/// every read-only file, web and MCP tool; the entry point exists as soon as anything is callable; the git and PR
/// families and every write or execute tool stay out even when they are read-only elsewhere; and the visible-tool list
/// from the <c>tools</c> section applies live, without rebuilding the runtime.
/// </summary>
public sealed class SessionRuntimeCodeModeTests : IDisposable
{
    private readonly string root = TestTempFiles.NewDirectory("forge-runtime-code-mode-");

    public void Dispose() => TestTempFiles.DeleteTree(root);

    [Fact]
    public async Task BuildTools_offers_run_code_from_read_only_file_tools_alone()
    {
        await using var rig = NewRig(services => services.FileTools = new FileTools().All);

        var tools = rig.Runtime.BuildTools();

        Assert.Contains(tools, static tool => string.Equals(tool.Definition.Name, "run_code", StringComparison.Ordinal));
        // No plugin tools were registered; the read-only file tools are the whole surface.
        Assert.Equal(["find", "grep", "ls", "read"], CallableNames(rig.Runtime));
    }

    [Fact]
    public async Task The_surface_carries_the_read_only_file_and_web_tools_and_nothing_else()
    {
        await using var rig = NewRig(services =>
        {
            services.FileTools = new FileTools().All;
            services.WebTools = () => [new WebFetchTool(new HttpClient())];
        });

        Assert.Equal(["find", "grep", "ls", "read", "web_fetch"], CallableNames(rig.Runtime));
    }

    [Fact]
    public async Task Narrowing_the_visible_list_reaches_the_surface_without_a_restart()
    {
        var visible = "read, ls, find, grep";
        await using var rig = NewRig(services =>
        {
            services.FileTools = new FileTools().All;
            services.CodeMode = () => new CodeModeSettings(8, 40_000, 20_000, CodeModeSettings.ParseTools(visible));
        });

        Assert.Equal(["find", "grep", "ls", "read"], CallableNames(rig.Runtime));

        // The same runtime, no rebuild: the next description reflects the narrowed list.
        visible = "read";

        Assert.Equal(["read"], CallableNames(rig.Runtime));
    }

    [Fact]
    public async Task Git_tools_stay_out_of_the_script_surface_even_when_read_only()
    {
        await using var rig = NewRig(
            services => services.FileTools = new FileTools().All,
            git: new GitTools(new GitCli(), new GitToolSettings(), new GitReviewLedger()));

        var tools = rig.Runtime.BuildTools();
        var names = CallableNames(rig.Runtime);

        // git_status is read-only, but the git family is not script surface at all.
        Assert.DoesNotContain("git_status", names);
        Assert.Equal(["find", "grep", "ls", "read"], names);
        Assert.Contains(tools, static tool => string.Equals(tool.Definition.Name, "git_status", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Only_read_only_mcp_tools_join_the_script_surface()
    {
        TestTempFiles.WriteFile(
            root,
            "global/mcp.json",
            """{"mcpServers":{"srv":{"command":"fake-srv","readOnlyTools":["readable"]}}}""");
        await using var preferences = new PreferencesStore(Path.Combine(root, "settings.json"));
        var script = new ScriptedServer([("readable", "Reads things."), ("writeable", "Writes things.")]);
        await using var mcp = McpServices.Create(
            new McpConfigStore(Path.Combine(root, "global", "mcp.json"), Path.Combine(root, "ws")),
            preferences,
            null,
            null,
            server => new Loopback(server.Name) { Responder = script.Respond });
        var state = await mcp.Manager.StartAsync("srv", CancellationToken.None);
        Assert.Equal(McpServerStatus.Ready, state.Status);

        await using var rig = NewRig(services => services.Mcp = mcp);

        // Both adapters reach the model's own surface; only the read-only one is scriptable.
        Assert.Equal(["srv__readable", "srv__writeable"], rig.Runtime.McpToolsForTest().Select(static tool => tool.Definition.Name).ToArray());
        Assert.Equal(["srv__readable"], CallableNames(rig.Runtime));
    }

    [Fact]
    public async Task A_tool_the_user_switched_off_leaves_the_script_surface_and_its_description()
    {
        IReadOnlyList<string> disabled = [];
        await using var rig = NewRig(services =>
        {
            services.FileTools = new FileTools().All;
            services.DisabledTools = () => disabled;
        });
        Assert.Equal(["find", "grep", "ls", "read"], CallableNames(rig.Runtime));

        disabled = ["grep"];

        // The live registry and the next description both drop it, without the host being rebuilt.
        Assert.Equal(["find", "ls", "read"], CallableNames(rig.Runtime));
        rig.Runtime.BuildTools();
        var surface = rig.Runtime.ScriptSurface!;
        Assert.DoesNotContain(surface.Tools, static tool => tool.Definition.Name == "grep");
        Assert.False(surface.TryGet("grep", out _));
        Assert.True(surface.TryGet("read", out _));

        disabled = [];
        Assert.True(surface.TryGet("grep", out _));
    }

    [Fact]
    public async Task Names_outside_the_candidates_are_unknown_to_the_script_registry()
    {
        await using var rig = NewRig(
            services =>
            {
                services.FileTools = new FileTools().All;
                services.PullRequestTools = [new StubTool("pr_view", ToolSideEffect.ReadOnly)];
            },
            git: new GitTools(new GitCli(), new GitToolSettings(), new GitReviewLedger()));

        rig.Runtime.BuildTools();
        var surface = rig.Runtime.ScriptSurface!;

        // The session itself holds all of them; the script registry resolves none of them, as for a name that does not exist.
        foreach (var name in new[] { "bash", "write", "edit", "git_status", "git_sync", "pr_view", "extension_create", "no_such_tool" })
        {
            Assert.False(surface.TryGet(name, out _), name);
        }

        Assert.Equal(["find", "grep", "ls", "read"], surface.Tools.Select(static tool => tool.Definition.Name).Order().ToArray());
        Assert.Contains(rig.Runtime.BuildTools(), static tool => tool.Definition.Name == "pr_view");
    }

    [Fact]
    public async Task The_web_search_tool_joins_the_script_surface_as_a_read_only_web_tool()
    {
        await using var rig = NewRig(services =>
        {
            services.FileTools = new FileTools().All;
            services.WebTools = () => [new WebFetchTool(new HttpClient()), new WebSearchTool(() => null)];
        });

        Assert.Equal(["find", "grep", "ls", "read", "web_fetch", "web_search"], CallableNames(rig.Runtime));
    }

    [Fact]
    public async Task DescribeTools_lists_run_code_exactly_when_the_surface_has_something_to_call()
    {
        // No plugin tool is registered, but the file tools are callable, so run_code exists and the catalog must say so.
        await using (var withFiles = NewRig(services => services.FileTools = new FileTools().All))
        {
            await withFiles.TurnAsync();
            var row = Assert.Single(withFiles.Runtime.DescribeTools(), static d => d.Name == "run_code");
            Assert.Equal(ToolGroups.Extensions, row.Group);
            Assert.Contains(withFiles.Runtime.BuildTools(), static tool => tool.Definition.Name == "run_code");
        }

        // Nothing to call: no run_code in the loop and none in the catalog.
        await using var empty = NewRig(_ => { });
        await empty.TurnAsync();
        Assert.DoesNotContain(empty.Runtime.DescribeTools(), static d => d.Name == "run_code");
        Assert.DoesNotContain(empty.Runtime.BuildTools(), static tool => tool.Definition.Name == "run_code");
    }

    /// <summary>A runtime over an extension host client that was never connected, so it holds no plugin tools.</summary>
    private SessionRuntimeRig NewRig(Action<AgentRuntimeServices> configure, GitTools? git = null) =>
        new(root, configure, new ExtensionHostClient(new HostConnection { PipeName = "forge-code-mode-unstarted" }), git);

    /// <summary>The run_code tool of the session's collected surface.</summary>
    private static ITool RunCode(SessionRuntime runtime) =>
        Assert.Single(runtime.BuildTools(), static tool => string.Equals(tool.Definition.Name, "run_code", StringComparison.Ordinal));

    /// <summary>The name list the run_code description ends with, as the script's Tools.List() would report it.</summary>
    private static string[] CallableNames(SessionRuntime runtime)
    {
        var description = RunCode(runtime).Definition.Description;
        var header = description.IndexOf("Tools (", StringComparison.Ordinal);
        Assert.True(header >= 0, "the run_code description carries no tool list");
        var names = description[(description.IndexOf('\n', header) + 1)..].TrimEnd().TrimStart();
        return names.Replace("\n  ", string.Empty, StringComparison.Ordinal).Split(", ");
    }

    /// <summary>A tool with a name and a declared side effect; the surface tests never invoke one.</summary>
    private sealed class StubTool(string name, ToolSideEffect sideEffect) : ITool
    {
        public ToolDefinition Definition { get; } = new(name, name, "{}");

        public ToolSideEffect SideEffect { get; } = sideEffect;

        public ValueTask<ToolResult> InvokeAsync(ToolCall call, ToolContext context, CancellationToken ct) =>
            throw new NotSupportedException("A stub is never invoked.");
    }
}
