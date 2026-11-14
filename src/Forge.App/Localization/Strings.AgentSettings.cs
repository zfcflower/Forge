using System.Runtime.CompilerServices;

namespace Forge.App.Localization;

/// <summary>
/// The two runtime dials of the agent sessions (wave W1): the <c>agents</c> section over the sub-agent cap and spawn switch,
/// and the <c>idle-archive</c> section over the housekeeping archive step. Keys are namespaced <c>agents.</c> and
/// <c>idleArchive.</c>; the window and the cap are numbers the user types, so only the labels and hints are text.
/// </summary>
internal static class AgentSettingsStrings
{
    /// <summary>Title of the <c>agents</c> section (sub-agent cap and switch).</summary>
    public const string AgentsSectionTitleKey = "agents.section";

    public const string AgentsMaxParallelKey = "agents.maxParallel";

    public const string AgentsMaxParallelHintKey = "agents.maxParallelHint";

    public const string AgentsSubAgentsKey = "agents.subAgents";

    public const string AgentsSubAgentsHintKey = "agents.subAgentsHint";

    /// <summary>Title of the <c>idle-archive</c> section (housekeeping's archive step).</summary>
    public const string IdleArchiveSectionTitleKey = "idleArchive.section";

    public const string IdleArchiveEnabledKey = "idleArchive.enabled";

    public const string IdleArchiveEnabledHintKey = "idleArchive.enabledHint";

    public const string IdleArchiveDaysKey = "idleArchive.days";

    public const string IdleArchiveDaysHintKey = "idleArchive.daysHint";

    public const string IdleArchiveScopeKey = "idleArchive.scope";

    public const string IdleArchiveScopeHintKey = "idleArchive.scopeHint";

    /// <summary>Scope token <c>automation</c>: only root sessions an automation created.</summary>
    public const string IdleArchiveScopeAutomationKey = "idleArchive.scopeAutomation";

    /// <summary>Scope token <c>all</c>: every root session.</summary>
    public const string IdleArchiveScopeAllKey = "idleArchive.scopeAll";

    public static string AgentsSectionTitle => Loc.Instance[AgentsSectionTitleKey];

    public static string AgentsMaxParallel => Loc.Instance[AgentsMaxParallelKey];

    public static string AgentsMaxParallelHint => Loc.Instance[AgentsMaxParallelHintKey];

    public static string AgentsSubAgents => Loc.Instance[AgentsSubAgentsKey];

    public static string AgentsSubAgentsHint => Loc.Instance[AgentsSubAgentsHintKey];

    public static string IdleArchiveSectionTitle => Loc.Instance[IdleArchiveSectionTitleKey];

    public static string IdleArchiveEnabled => Loc.Instance[IdleArchiveEnabledKey];

    public static string IdleArchiveEnabledHint => Loc.Instance[IdleArchiveEnabledHintKey];

    public static string IdleArchiveDays => Loc.Instance[IdleArchiveDaysKey];

    public static string IdleArchiveDaysHint => Loc.Instance[IdleArchiveDaysHintKey];

    public static string IdleArchiveScope => Loc.Instance[IdleArchiveScopeKey];

    public static string IdleArchiveScopeHint => Loc.Instance[IdleArchiveScopeHintKey];

    /// <summary>Label of the <c>automation</c> scope option.</summary>
    public static string IdleArchiveScopeAutomation => Loc.Instance[IdleArchiveScopeAutomationKey];

    /// <summary>Label of the <c>all</c> scope option.</summary>
    public static string IdleArchiveScopeAll => Loc.Instance[IdleArchiveScopeAllKey];

    [ModuleInitializer]
    internal static void Register()
    {
        Loc.Register(AgentsSectionTitleKey, "子 Agent 运行", "Sub-agent runtime");
        Loc.Register(AgentsMaxParallelKey, "同时运行的 Agent 上限", "Agents running at once");
        Loc.Register(
            AgentsMaxParallelHintKey,
            "按派生树计数：正在对话或跑工具的子 Agent 才算占位，等待子结果的不占。改完对正在运行的会话下一回合生效。",
            "Counted over the spawn tree: a sub-agent occupies a slot while it streams a turn or runs tools, not while it waits for its own children. A change reaches a running session at its next turn.");
        Loc.Register(AgentsSubAgentsKey, "允许派生 Agent", "Allow spawning sub-agents");
        Loc.Register(
            AgentsSubAgentsHintKey,
            "关掉后模型不再拿到 spawn_agent；已经在跑的子 Agent 仍可继续、跟随与停止。",
            "Off takes spawn_agent out of the model's tools; the sub-agents already running can still be steered, followed up and stopped.");

        Loc.Register(IdleArchiveSectionTitleKey, "闲置会话归档", "Idle session archiving");
        Loc.Register(IdleArchiveEnabledKey, "自动归档闲置会话", "Archive idle sessions automatically");
        Loc.Register(
            IdleArchiveEnabledHintKey,
            "每日凌晨的清理任务按这里的窗口与范围归档；关闭后仍会清理 worktree 记录、空暂存目录与运行历史。",
            "The daily housekeeping run archives by the window and scope below; with it off, the run still prunes worktree records, empty scratch folders and run history.");
        Loc.Register(IdleArchiveDaysKey, "闲置天数", "Idle days");
        Loc.Register(
            IdleArchiveDaysHintKey,
            "最后一次活动超过这个天数才会归档；正在打开、子会话、以及周任务复用的分支会话永远不归档。",
            "A session is archived once its last activity is older than this; open sessions, sub-sessions and the branches a weekly automation reuses are never archived.");
        Loc.Register(IdleArchiveScopeKey, "范围", "Scope");
        Loc.Register(
            IdleArchiveScopeHintKey,
            "默认只归档自动化创建的根会话，避免动到你自己的工作。",
            "By default only automation-created root sessions are archived, so your own work is left alone.");
        Loc.Register(IdleArchiveScopeAutomationKey, "仅自动化创建的会话", "Automation-created sessions");
        Loc.Register(IdleArchiveScopeAllKey, "全部根会话", "All root sessions");
    }
}
