using System.Collections.Frozen;
using Forge.Abstractions;
using Forge.Core.Agents;
using Forge.App.Providers;
using Forge.Core.Tools;

namespace Forge.App.Agents;

/// <summary>
/// The product's whole tool catalog: what each tool is called, where it sits on the <c>tools</c> settings page and whether the
/// user may switch it off. One enumeration for both readers — the settings page lists it, the resource window sizes the
/// session's registered tools against it — so a new tool is added in one place and both stay right.
/// <para>The catalog is a property of the installation, not of a session: a tool whose enabling condition is app-wide (a
/// plugin host, a configured web backend, a provider catalog) is listed exactly while that condition holds, and one some
/// sessions never get (git and pull requests, the spec tools) is listed anyway — because the switch it carries is global. What
/// a <em>particular</em> session registered is the resource window's business.</para>
/// </summary>
internal static class ToolCatalog
{
    /// <summary>The extension host's script proxy; a built-in that exists while a script has something to call (its candidate
    /// set is non-empty). The <c>tools</c> page lists it whenever the extension host is there, because that switch is global.</summary>
    public const string RunCode = "run_code";

    /// <summary>The <c>ask_user</c> tool (registered by Core when the session has a question handler).</summary>
    public const string AskUser = "ask_user";

    /// <summary>
    /// Core's own built-in tools in the order Core registers them, with the group each one is shown under.
    /// </summary>
    private static readonly (string Name, string Group)[] CoreTools =
    [
        (SearchHistoryTool.Name, ToolGroups.Retrieval),
        (ReadHistoryTool.Name, ToolGroups.Retrieval),
        (ReadArtifactTool.Name, ToolGroups.Retrieval),
        (TodoTool.Name, ToolGroups.Session),
        (AskUser, ToolGroups.Session),
        (SetTitleTool.Name, ToolGroups.Session),
        (SpawnAgentTool.Name, ToolGroups.Agents),
        (SubAgentSteerTool.Name, ToolGroups.Agents),
        (SubAgentFollowUpTool.Name, ToolGroups.Agents),
        (SubAgentStopTool.Name, ToolGroups.Agents),
        (Forge.Abstractions.SessionReplies.ToolName, ToolGroups.Session),
        (RequestReviewTool.Name, ToolGroups.Agents),
    ];

    /// <summary>
    /// Tools another feature cannot run without: <c>read_artifact</c> is how the model reads back output the loop moved into
    /// the artifact store, the sub-agent control tools are how a parent reaches the children it already spawned
    /// (switching off <c>spawn_agent</c> here is the way to stop
    /// spawning), and <c>skill</c> is how the orchestration's skill index is made good on - the root prompt tells the model
    /// to load a skill by name, so leaving the tool switchable would point it at a tool that answers "unknown tool". A
    /// stored switch for one of them is ignored.
    /// </summary>
    private static readonly FrozenSet<string> Required = FrozenSet.Create(
        StringComparer.Ordinal,
        [
            ReadArtifactTool.Name, SubAgentSteerTool.Name, SubAgentFollowUpTool.Name, SubAgentStopTool.Name, SkillTool.Name,
            // The one channel between a root and the sessions the user drove under it (forge-core "session messaging"): switching it
            // off would leave "the user @-mentioned the session" with no way to act on it.
            Forge.Abstractions.SessionReplies.ToolName,
        ]);

    private static readonly string[] ExtensionTools =
    [
        ExtensionToolNames.List, ExtensionToolNames.Create, ExtensionToolNames.Update, ExtensionToolNames.Disable,
        ExtensionToolNames.Delete,
    ];

    /// <summary>The names Core's own built-in tools use, plus <c>run_code</c>: a plugin tool may not take one of them on the
    /// <c>run_code</c> script surface.</summary>
    public static IEnumerable<string> CoreNames { get; } = [.. CoreTools.Select(static tool => tool.Name), RunCode];

    /// <summary>
    /// Every tool this installation can register, each with where it belongs and whether the user may switch it off. Built
    /// from the composition's own services, so it needs no session and no host: the order is Core's built-ins first (they
    /// shadow a same-named tool from anywhere else), then the app's tool families, then the extension host's tools and
    /// finally MCP — the same precedence the session's tool surface applies.
    /// </summary>
    public static IReadOnlyList<ToolDescriptor> Describe(AgentRuntimeServices services)
    {
        ArgumentNullException.ThrowIfNull(services);
        var descriptors = new List<ToolDescriptor>(CoreTools.Length + 64);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (name, group) in CoreTools)
        {
            Add(name, ToolOrigin.BuiltIn, group, canDisable: !Required.Contains(name));
        }

        foreach (var tool in services.FileTools)
        {
            Add(tool.Definition.Name, ToolOrigin.BuiltIn, ToolGroups.Files);
        }

        if (services.Git is { } git)
        {
            foreach (var tool in git.All)
            {
                Add(tool.Definition.Name, ToolOrigin.BuiltIn, ToolGroups.Git);
            }
        }

        foreach (var tool in services.PullRequestTools)
        {
            Add(tool.Definition.Name, ToolOrigin.BuiltIn, ToolGroups.PullRequests);
        }

        // The web tools are an installation fact, not a session one: web_fetch is offered always and web_search joins it
        // while a search backend is configured (WebServices.CreateTools decides both).
        if (services.WebTools is { } webTools)
        {
            foreach (var tool in webTools())
            {
                Add(tool.Definition.Name, ToolOrigin.BuiltIn, ToolGroups.Web);
            }
        }

        // The spec-graph tools only appear in a workspace that has specs, which is a property of the session's directory: the
        // entry is still listed, because switching spec_graph off is a decision about the product, not about one folder.
        foreach (var tool in services.SpecTools.All)
        {
            Add(tool.Definition.Name, ToolOrigin.BuiltIn, ToolGroups.Retrieval);
        }

        Add(SkillTool.Name, ToolOrigin.BuiltIn, ToolGroups.Session, canDisable: !Required.Contains(SkillTool.Name));
        if (services.Providers is not null)
        {
            Add(ModelsTool.Name, ToolOrigin.BuiltIn, ToolGroups.Agents);
        }

        if (services.HostClient is { } client)
        {
            if (services.Approvals?.Extensions is not null)
            {
                foreach (var name in ExtensionTools)
                {
                    Add(name, ToolOrigin.BuiltIn, ToolGroups.Extensions);
                }
            }

            Add(RunCode, ToolOrigin.BuiltIn, ToolGroups.Extensions);

            foreach (var tool in client.Tools)
            {
                Add(tool.Definition.Name, ToolOrigin.Extension, ToolGroups.Extensions, canDisable: false);
            }
        }

        // Every ready MCP tool, not only the ones one turn offers: the Tools section is where a user looks to see what a
        // server provides, and a trimmed list there would read as a missing tool.
        if (services.Mcp is { } mcp)
        {
            foreach (var adapter in mcp.Manager.Tools)
            {
                Add(adapter.ExposedName, ToolOrigin.Mcp, adapter.ServerName, canDisable: false, source: adapter.ServerName);
            }
        }

        return descriptors;

        // The first registration of a name wins, exactly as the tool surface drops a later same-named tool.
        void Add(string name, ToolOrigin origin, string group, bool canDisable = true, string? source = null)
        {
            if (seen.Add(name))
            {
                descriptors.Add(new ToolDescriptor(name, origin, group, source, canDisable));
            }
        }
    }

    /// <summary>Whether a stored switch for <paramref name="name"/> takes effect.</summary>
    public static bool CanDisable(string name) => !Required.Contains(name);

    /// <summary>The stored switches minus the names that may not be switched off.</summary>
    public static IEnumerable<string> EffectiveDisabled(IReadOnlyList<string> stored)
    {
        foreach (var name in stored)
        {
            if (CanDisable(name))
            {
                yield return name;
            }
        }
    }
}
