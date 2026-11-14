using System.Collections.Frozen;
using Forge.Abstractions;
using Forge.Core.Agents;
using Forge.Core.Tools;

namespace Forge.App.Agents;

/// <summary>
/// Where the Core built-in tools sit on the Tools settings page, and which of them the user may switch off. The App-side
/// built-ins (file, git, PR, web, extension authoring) are classified by the list they come from in
/// <see cref="SessionRuntime.DescribeTools"/>, so only Core's own names need a table here.
/// </summary>
internal static class ToolCatalog
{
    /// <summary>The extension host's script proxy; a built-in that only exists while a plugin registered tools.</summary>
    public const string RunCode = "run_code";

    /// <summary>The <c>ask_user</c> tool (registered by Core when the session has a question handler).</summary>
    public const string AskUser = "ask_user";

    /// <summary>
    /// Tools another feature cannot run without: <c>read_artifact</c> is how the model reads back output the loop moved into
    /// the artifact store, and the sub-agent control tools are how a parent reaches the children it already spawned
    /// (switching off <c>spawn_agent</c>, here or through the <c>agents</c> section's sub-agent switch, is the way to stop
    /// spawning). A stored switch for one of them is ignored.
    /// </summary>
    private static readonly FrozenSet<string> Required = FrozenSet.Create(
        StringComparer.Ordinal,
        [ReadArtifactTool.Name, SubAgentSteerTool.Name, SubAgentFollowUpTool.Name, SubAgentStopTool.Name]);

    private static readonly FrozenDictionary<string, string> CoreGroups = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [SearchHistoryTool.Name] = ToolGroups.Retrieval,
        [ReadHistoryTool.Name] = ToolGroups.Retrieval,
        [ReadArtifactTool.Name] = ToolGroups.Retrieval,
        [TodoTool.Name] = ToolGroups.Session,
        [AskUser] = ToolGroups.Session,
        [SpawnAgentTool.Name] = ToolGroups.Agents,
        [SubAgentSteerTool.Name] = ToolGroups.Agents,
        [SubAgentFollowUpTool.Name] = ToolGroups.Agents,
        [SubAgentStopTool.Name] = ToolGroups.Agents,
        [RequestReviewTool.Name] = ToolGroups.Agents,
    }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>A Core built-in tool's catalog entry; an unknown Core name lands in the session group.</summary>
    public static ToolDescriptor DescribeCore(string name) =>
        new(name, ToolOrigin.BuiltIn, CoreGroups.GetValueOrDefault(name, ToolGroups.Session), CanDisable: !Required.Contains(name));

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
