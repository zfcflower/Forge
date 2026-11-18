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
        Loc.Register(AgentsSectionTitleKey, "对话与子 Agent", "Chat & sub-agents");
        Loc.Register("agents.groupAgents", "子 Agent 运行", "Sub-agent runtime");
        Loc.Register("agents.groupReview", "评审", "Review");
        Loc.Register("agents.groupChat", "对话", "Chat");
        Loc.Register(
            "agents.review.intro",
            "评审是让一个独立的评审会话检查 Agent 做完的改动。下面四项互相独立；pr_create 的闸门（评审通过、验证非空）是固定的，不受这些开关影响。",
            "A review has an independent reviewer session check what the agent changed. The four settings below are independent; the pr_create gates (approved review, non-empty verification) are fixed and not affected by them.");
        Loc.Register("agents.review.tool", "提供评审工具", "Offer the review tool");
        Loc.Register(
            "agents.review.toolHint",
            "开：Agent 拿到 request_review 工具，可自行发起评审。关：不提供该工具，步骤闸门也随之失效（没有评审就无从要求）。改后对新建会话生效，已开的会话保持原样。",
            "On: agents get the request_review tool and can start a review themselves. Off: the tool is not offered and the step gate stops applying (nothing to require without it). Applies to new sessions; open sessions keep their tools.");
        Loc.Register("agents.review.stepRequired", "完成步骤必须先评审通过", "Require an approved review to finish a step");
        Loc.Register(
            "agents.review.stepRequiredHint",
            "开：把有代码改动的 todo 步骤标为完成前，必须有针对当前改动的通过评审，且 verification 不能是“未验证”。关：Agent 可以不经评审直接完成步骤。每次尝试完成时读取，立即生效。",
            "On: a todo step with code changes cannot be completed without an approving review of those changes and a verification that is not \"not verified\". Off: the agent may complete steps without review. Read on every completion attempt, so it applies at once.");
        Loc.Register("agents.review.autoFix", "评审意见自动修复", "Fix review findings automatically");
        Loc.Register(
            "agents.review.autoFixHint",
            "开：评审提出问题后，Agent 一次性修完所有问题并重新评审，直到通过或达到轮数上限。关：Agent 只向你汇报评审意见并等待你的指示，不自行修改。立即生效。",
            "On: after a review finds problems the agent fixes them all in one pass and re-reviews, until it approves or the round limit is reached. Off: the agent only reports the findings to you and waits, changing nothing on its own. Applies at once.");
        Loc.Register("agents.review.maxRounds", "自动修复轮数上限", "Auto-fix round limit");
        Loc.Register(
            "agents.review.maxRoundsHint",
            "同一步骤“修复 → 重新评审”最多自动进行几轮（1–10）。达到上限后 Agent 必须向你说明情况并询问怎么办，不再继续循环。仅在开启自动修复时有效。",
            "How many fix-and-re-review rounds one step may run automatically (1–10). At the limit the agent must explain the situation and ask you how to proceed instead of looping on. Only used while auto-fix is on.");
        Loc.Register(AgentsMaxParallelKey, "同时运行的 Agent 上限", "Agents running at once");
        Loc.Register(
            AgentsMaxParallelHintKey,
            "按派生树计数：正在对话或跑工具的子 Agent 才算占位，等待子结果的不占。改完对正在运行的会话下一回合生效。",
            "Counted over the spawn tree: a sub-agent occupies a slot while it streams a turn or runs tools, not while it waits for its own children. A change reaches a running session at its next turn.");

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
