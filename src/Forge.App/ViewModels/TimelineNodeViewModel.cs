using System.Collections.Frozen;
using Forge.App.Agents;
using Forge.App.Localization;
using Forge.Core.Agents;
using Forge.Core.Tools;
using Forge.Core.Tools.Web;
using Forge.Mcp;
using Forge.Workspace;

namespace Forge.App.ViewModels;

/// <summary>
/// What one timeline node stands for. The chat transcript draws one node on the vertical axis for every event it already
/// holds; the kind only decides the icon and its readable name, never the business data of the row.
/// </summary>
internal enum TimelineNodeKind
{
    /// <summary>A user message.</summary>
    User,

    /// <summary>A run of assistant reply text.</summary>
    Assistant,

    /// <summary>Reserved for a reasoning block. The session drops <c>AgentThinkingDelta</c> today, so no row produces it.</summary>
    Thinking,

    /// <summary>A tool that reads a file, a spec or a repository object.</summary>
    ReadFile,

    /// <summary>A tool that looks for things: a file, a match, a session.</summary>
    Search,

    /// <summary>A tool that writes or edits something.</summary>
    EditFile,

    /// <summary>A shell command or a script run.</summary>
    Command,

    /// <summary>A network call: the web, a git remote, the pull-request platform.</summary>
    Network,

    /// <summary>An MCP server tool.</summary>
    McpTool,

    /// <summary>A sub-agent: spawning, steering, stopping, or reviewing with one.</summary>
    SubAgent,

    /// <summary>The plan and its steps.</summary>
    Plan,

    /// <summary>An informational note inside the transcript.</summary>
    Notice,

    /// <summary>A failure: a red note, a turn footnote, or a notice that failed.</summary>
    Error,

    /// <summary>An approval that was denied.</summary>
    Approval,

    /// <summary>A tool that fits no other kind.</summary>
    Tool,

    /// <summary>The turn header: a node-less connector the axis passes through.</summary>
    Turn,
}

/// <summary>How a node currently reads: the regular/filled variant and the colour both follow this.</summary>
internal enum TimelineNodeState
{
    /// <summary>Nothing is happening: an outline icon in the secondary colour.</summary>
    Idle,

    /// <summary>A call or a stream is still running: a filled icon in the accent colour, pulsing unless motion is reduced.</summary>
    Running,

    /// <summary>The call failed: a filled icon in the danger colour.</summary>
    Failed,

    /// <summary>Something was refused or needs attention: a filled icon in the warning colour.</summary>
    Warning,
}

/// <summary>
/// The pure half of the timeline: which kind a row is, which icon key and readable-name key it wears, and which state it
/// is in. No Avalonia type and no visual tree is touched here, so the whole mapping is unit-testable; the control resolves
/// the icon key against <c>Resources/Icons.axaml</c> and the name key against <c>Loc</c>.
/// <para>Keys are constants of <c>TimelineStrings</c> and of the icon dictionary; a key that is missing shows up as an
/// unregistered key in the localization tests and as an empty icon in the view tests, not as a silent blank node.</para>
/// </summary>
internal static class TimelineNodeMap
{
    private static readonly FrozenDictionary<string, TimelineNodeKind> ToolKinds =
        new Dictionary<string, TimelineNodeKind>(StringComparer.Ordinal)
        {
            // File and repository reads.
            ["read"] = TimelineNodeKind.ReadFile,
            [ReadArtifactTool.Name] = TimelineNodeKind.ReadFile,
            [ReadHistoryTool.Name] = TimelineNodeKind.ReadFile,
            [GitTools.StatusName] = TimelineNodeKind.ReadFile,
            [GitTools.LogName] = TimelineNodeKind.ReadFile,
            [GitTools.DiffName] = TimelineNodeKind.ReadFile,
            ["spec_get"] = TimelineNodeKind.ReadFile,
            ["spec_graph"] = TimelineNodeKind.ReadFile,
            ["spec_validate"] = TimelineNodeKind.ReadFile,

            // Searches.
            ["ls"] = TimelineNodeKind.Search,
            ["find"] = TimelineNodeKind.Search,
            ["grep"] = TimelineNodeKind.Search,
            [SearchHistoryTool.Name] = TimelineNodeKind.Search,
            ["spec_grep"] = TimelineNodeKind.Search,
            ["spec_search"] = TimelineNodeKind.Search,

            // Writes and edits.
            ["edit"] = TimelineNodeKind.EditFile,
            ["write"] = TimelineNodeKind.EditFile,
            ["spec_create"] = TimelineNodeKind.EditFile,
            ["spec_update"] = TimelineNodeKind.EditFile,
            ["spec_delete"] = TimelineNodeKind.EditFile,

            // Commands.
            ["bash"] = TimelineNodeKind.Command,
            [ToolCatalog.RunCode] = TimelineNodeKind.Command,

            // Network calls: the web, a remote, the pull-request platform.
            [WebFetchTool.Name] = TimelineNodeKind.Network,
            [WebSearchTool.Name] = TimelineNodeKind.Network,
            [GitTools.FetchName] = TimelineNodeKind.Network,
            [GitTools.SyncName] = TimelineNodeKind.Network,
            [PrTools.ViewName] = TimelineNodeKind.Network,
            [PrTools.CreateName] = TimelineNodeKind.Network,
            [PrTools.SyncName] = TimelineNodeKind.Network,
            [PrTools.EditName] = TimelineNodeKind.Network,
            [PrTools.WorkflowName] = TimelineNodeKind.Network,
            [PrCheckTools.ChecksName] = TimelineNodeKind.Network,
            [PrCheckTools.CiLogName] = TimelineNodeKind.Network,
            [PrCommentTools.ListName] = TimelineNodeKind.Network,
            [PrCommentTools.ReplyName] = TimelineNodeKind.Network,
            [PrCommentTools.ResolveName] = TimelineNodeKind.Network,
            [PrCommentTools.ReviewListName] = TimelineNodeKind.Network,
            [PrCommentTools.ReviewResolveName] = TimelineNodeKind.Network,
            [ScreenshotTools.ScreenshotsName] = TimelineNodeKind.Network,

            // MCP: the search helper, plus every server tool by its naming separator (see KindForTool).
            [McpToolSearchTool.Name] = TimelineNodeKind.McpTool,

            // Sub-agents and the review that runs one.
            [SpawnAgentTool.Name] = TimelineNodeKind.SubAgent,
            [SubAgentSteerTool.Name] = TimelineNodeKind.SubAgent,
            [SubAgentFollowUpTool.Name] = TimelineNodeKind.SubAgent,
            [SubAgentStopTool.Name] = TimelineNodeKind.SubAgent,
            [RequestReviewTool.Name] = TimelineNodeKind.SubAgent,

            // The plan.
            [TodoTool.Name] = TimelineNodeKind.Plan,

            // A tool that asks the user something reads as a note on the axis.
            [AskUserTool.Name] = TimelineNodeKind.Notice,
        }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>
    /// The kind of one tool call, by its name. A name the table does not know is classified by the few prefixes that are
    /// systematic, and falls back to <see cref="TimelineNodeKind.Tool"/>; an MCP server tool is recognised by the
    /// <c>server__tool</c> separator of <see cref="McpToolNaming"/> (the row carries no origin, and a wrong guess only
    /// changes an icon).
    /// </summary>
    public static TimelineNodeKind KindForTool(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return TimelineNodeKind.Tool;
        }

        if (ToolKinds.TryGetValue(name, out var kind))
        {
            return kind;
        }

        if (name.Contains(McpToolNaming.Separator, StringComparison.Ordinal))
        {
            return TimelineNodeKind.McpTool;
        }

        return name.StartsWith("pr_", StringComparison.Ordinal)
            || name.StartsWith("review_", StringComparison.Ordinal)
            || name.StartsWith("extension_", StringComparison.Ordinal)
            || name.StartsWith("spec_", StringComparison.Ordinal)
            ? TimelineNodeKind.Network
            : TimelineNodeKind.Tool;
    }

    /// <summary>
    /// The state of one node: a failure wins over a run, and a denied approval is a warning rather than a failure (it is an
    /// expected answer, not a broken call).
    /// </summary>
    public static TimelineNodeState StateOf(TimelineNodeKind kind, bool running, bool failing) =>
        failing ? (kind == TimelineNodeKind.Approval ? TimelineNodeState.Warning : TimelineNodeState.Failed)
        : running ? TimelineNodeState.Running
        : TimelineNodeState.Idle;

    /// <summary>The kind whose icon a row shows: a failed notice wears the error triangle, every other kind keeps its own icon.</summary>
    public static TimelineNodeKind EffectiveKind(TimelineNodeKind kind, TimelineNodeState state) =>
        kind == TimelineNodeKind.Notice && state != TimelineNodeState.Idle ? TimelineNodeKind.Error : kind;

    /// <summary>The resource key of this node's icon; the filled variant is its own key in <c>Icons.axaml</c>.</summary>
    public static string IconKey(TimelineNodeKind kind, bool filled) => filled ? FilledIcon(kind) : OutlineIcon(kind);

    /// <summary>
    /// The outline side. The pre-existing outline of search, edit, globe, plug, shield and wrench was a coordinate-rounded
    /// replay of the FIS path (or, for the globe, hand-drawn), so a pair that has to be the same glyph at two weights uses the
    /// explicitly added <c>*Regular</c> keys; bot's existing outline was byte-identical to the FIS regular and is reused.
    /// </summary>
    private static string OutlineIcon(TimelineNodeKind kind) => kind switch
    {
        TimelineNodeKind.User => "IconPerson",
        TimelineNodeKind.Assistant or TimelineNodeKind.Turn => "IconChat",
        TimelineNodeKind.Thinking => "IconLightbulb",
        TimelineNodeKind.ReadFile => "IconDocument",
        TimelineNodeKind.Search => "IconSearchRegular",
        TimelineNodeKind.EditFile => "IconEditRegular",
        TimelineNodeKind.Command => "IconCode",
        TimelineNodeKind.Network => "IconGlobeRegular",
        TimelineNodeKind.McpTool => "IconPlugRegular",
        TimelineNodeKind.SubAgent => "IconBot",
        TimelineNodeKind.Plan => "IconTaskList",
        TimelineNodeKind.Notice => "IconInfo",
        TimelineNodeKind.Error => "IconWarning",
        TimelineNodeKind.Approval => "IconShieldCheckRegular",
        _ => "IconWrenchRegular",
    };

    private static string FilledIcon(TimelineNodeKind kind) => kind switch
    {
        TimelineNodeKind.User => "IconPersonFilled",
        TimelineNodeKind.Assistant or TimelineNodeKind.Turn => "IconChatFilled",
        TimelineNodeKind.Thinking => "IconLightbulbFilled",
        TimelineNodeKind.ReadFile => "IconDocumentFilled",
        TimelineNodeKind.Search => "IconSearchFilled",
        TimelineNodeKind.EditFile => "IconEditFilled",
        TimelineNodeKind.Command => "IconCodeFilled",
        TimelineNodeKind.Network => "IconGlobeFilled",
        TimelineNodeKind.McpTool => "IconPlugFilled",
        TimelineNodeKind.SubAgent => "IconBotFilled",
        TimelineNodeKind.Plan => "IconTaskListFilled",
        TimelineNodeKind.Notice => "IconInfoFilled",
        TimelineNodeKind.Error => "IconWarningFilled",
        TimelineNodeKind.Approval => "IconShieldCheckFilled",
        _ => "IconWrenchFilled",
    };

    /// <summary>The localized readable name of this node's kind.</summary>
    public static string NameKey(TimelineNodeKind kind) => kind switch
    {
        TimelineNodeKind.User => TimelineStrings.NameUser,
        TimelineNodeKind.Assistant or TimelineNodeKind.Turn => TimelineStrings.NameAssistant,
        TimelineNodeKind.Thinking => TimelineStrings.NameThinking,
        TimelineNodeKind.ReadFile => TimelineStrings.NameReadFile,
        TimelineNodeKind.Search => TimelineStrings.NameSearch,
        TimelineNodeKind.EditFile => TimelineStrings.NameEditFile,
        TimelineNodeKind.Command => TimelineStrings.NameCommand,
        TimelineNodeKind.Network => TimelineStrings.NameNetwork,
        TimelineNodeKind.McpTool => TimelineStrings.NameMcp,
        TimelineNodeKind.SubAgent => TimelineStrings.NameSubAgent,
        TimelineNodeKind.Plan => TimelineStrings.NamePlan,
        TimelineNodeKind.Notice => TimelineStrings.NameNotice,
        TimelineNodeKind.Error => TimelineStrings.NameError,
        TimelineNodeKind.Approval => TimelineStrings.NameApproval,
        _ => TimelineStrings.NameTool,
    };

    /// <summary>The localized word for a non-idle state, or an empty string while the node is idle.</summary>
    public static string StateKey(TimelineNodeState state) => state switch
    {
        TimelineNodeState.Running => TimelineStrings.StateRunning,
        TimelineNodeState.Failed => TimelineStrings.StateFailed,
        TimelineNodeState.Warning => TimelineStrings.StateWarning,
        _ => string.Empty,
    };
}