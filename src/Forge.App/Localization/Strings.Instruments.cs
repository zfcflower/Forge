using System.Runtime.CompilerServices;
using Forge.App.ViewModels;

namespace Forge.App.Localization;

/// <summary>
/// Texts of the composer's instrument strip, the project dropdown and the four read-only settings sections that the
/// counters open. The numbers inside them are formatted by the view models (<see cref="TextFormat"/> only groups plain
/// counts); the sentence patterns live here, so a language switch re-derives every label.
/// </summary>
internal static class InstrumentsStrings
{
    public const string ApprovalsLabelKey = "session.strip.approvals";
    public const string SkillsLabelKey = "session.strip.skills";
    public const string McpLabelKey = "session.strip.mcp";
    public const string ToolsLabelKey = "session.strip.tools";
    public const string CountPairKey = "session.strip.count.pair";
    public const string CountUnknownKey = "session.strip.count.unknown";
    public const string MenuEntryKey = "session.strip.menuEntry";

    public const string ApprovalsTooltipKey = "session.strip.approvals.tooltip";
    public const string ApprovalsUnavailableKey = "session.strip.approvals.unavailable";
    public const string SkillsTooltipKey = "session.strip.skills.tooltip";
    public const string McpTooltipKey = "session.strip.mcp.tooltip";
    public const string ToolsTooltipKey = "session.strip.tools.tooltip";
    public const string ToolsTooltipUnknownKey = "session.strip.tools.tooltip.unknown";

    public const string UsageTokensKey = "session.strip.usage.tokens";
    public const string UsageWindowKey = "session.strip.usage.window";
    public const string UsageCacheReadKey = "session.strip.usage.cacheRead";
    public const string UsageCacheWriteKey = "session.strip.usage.cacheWrite";
    public const string UsageCacheKey = "session.strip.usage.cache";
    public const string UsageContextKey = "session.strip.usage.context";
    public const string CacheReadKey = "session.strip.cacheRead";
    public const string UsageNoneKey = "session.strip.usage.none";

    public const string WorkspaceTooltipKey = "session.strip.workspace.tooltip";
    public const string ProjectVsCodeKey = "session.strip.project.vscode";
    public const string WorkspaceVsCodeKey = "session.strip.workspace.vscode";
    public const string ProjectExplorerKey = "session.strip.project.explorer";
    public const string WorkspaceExplorerKey = "session.strip.workspace.explorer";
    public const string ResourcesKey = "session.strip.resources";
    public const string ResourcesTooltipKey = "session.strip.resources.tooltip";

    public const string ApprovalsSectionTitleKey = "settings.approvals.title";
    public const string SkillsSectionTitleKey = "settings.skills.title";
    public const string McpSectionTitleKey = "settings.mcp.title";
    public const string ToolsSectionTitleKey = "settings.tools.title";

    public const string ApprovalsSectionHintKey = "settings.approvals.hint";
    public const string ApprovalsSectionEmptyKey = "settings.approvals.empty";
    public const string ApprovalsSectionUnavailableKey = "settings.approvals.unavailable";
    public const string SkillsSectionHintKey = "settings.skills.hint";
    public const string SkillsSectionEmptyKey = "settings.skills.empty";
    public const string McpSectionHintKey = "settings.mcp.hint";
    public const string McpSectionEmptyKey = "settings.mcp.empty";
    public const string McpSectionEnabledKey = "settings.mcp.enabled";
    public const string McpSectionDisabledKey = "settings.mcp.disabled";
    public const string ToolsSectionHintKey = "settings.tools.hint";
    public const string ToolsSectionEmptyKey = "settings.tools.empty";
    public const string ToolsSectionNoSessionKey = "settings.tools.noSession";
    public const string ToolsInventoryKey = "settings.tools.inventory";
    public const string ToolsGroupFilesKey = "settings.tools.group.files";
    public const string ToolsGroupGitKey = "settings.tools.group.git";
    public const string ToolsGroupPullRequestsKey = "settings.tools.group.pr";
    public const string ToolsGroupWebKey = "settings.tools.group.web";
    public const string ToolsGroupSessionKey = "settings.tools.group.session";
    public const string ToolsGroupAgentsKey = "settings.tools.group.agents";
    public const string ToolsGroupRetrievalKey = "settings.tools.group.retrieval";
    public const string ToolsGroupExtensionsKey = "settings.tools.group.extensions";
    public const string ToolsGroupExtensionToolsKey = "settings.tools.group.extensionTools";
    public const string ToolsGroupMcpKey = "settings.tools.group.mcp";
    public const string ToolsRequiredKey = "settings.tools.required";
    public const string ToolsFollowsSourceKey = "settings.tools.followsSource";

    [ModuleInitializer]
    internal static void Initialize() => Register();

    public static string ApprovalsLabel => Loc.Instance[ApprovalsLabelKey];

    public static string SkillsLabel => Loc.Instance[SkillsLabelKey];

    public static string McpLabel => Loc.Instance[McpLabelKey];

    public static string ToolsLabel => Loc.Instance[ToolsLabelKey];

    public static string CountPair(int used, int total) => Loc.Instance.FormatCounts(CountPairKey, used, total);

    public static string CountUnknown(int used) =>
        Loc.Instance.Format(CountUnknownKey, TextFormat.Number(used));

    /// <summary>One overflow-menu row: the counter's own label and its pair, so the row reads as the button does.</summary>
    public static string MenuEntry(string label, string count) => Loc.Instance.Format(MenuEntryKey, label, count);

    public static string ApprovalsTooltip(int pending, int records) =>
        Loc.Instance.FormatCounts(ApprovalsTooltipKey, pending, records);

    public static string ApprovalsUnavailable => Loc.Instance[ApprovalsUnavailableKey];

    public static string SkillsTooltip(int used, int discovered) =>
        Loc.Instance.FormatCounts(SkillsTooltipKey, used, discovered);

    public static string McpTooltip(int ready, int configured) =>
        Loc.Instance.FormatCounts(McpTooltipKey, ready, configured);

    public static string ToolsTooltip(int used, int available) =>
        Loc.Instance.FormatCounts(ToolsTooltipKey, used, available);

    public static string ToolsTooltipUnknown(int used) =>
        Loc.Instance.Format(ToolsTooltipUnknownKey, TextFormat.Number(used));

    public static string UsageTokens(string input, string output) =>
        Loc.Instance.Format(UsageTokensKey, input, output);

    public static string UsageWindow(string window) => Loc.Instance.Format(UsageWindowKey, window);

    public static string UsageCacheRead(string tokens) => Loc.Instance.Format(UsageCacheReadKey, tokens);

    public static string UsageCacheWrite(string tokens) => Loc.Instance.Format(UsageCacheWriteKey, tokens);

    public static string UsageCache(string ratio) => Loc.Instance.Format(UsageCacheKey, ratio);

    public static string UsageContext(string percent, string window) =>
        Loc.Instance.Format(UsageContextKey, percent, window);

    public static string UsageNone => Loc.Instance[UsageNoneKey];

    /// <summary>"RC 98.5" — the cache-read hit rate in the strip, one decimal; the gauge beside it carries the percentage.</summary>
    public static string CacheRead(string ratio) => Loc.Instance.Format(CacheReadKey, ratio);

    public static string WorkspaceTooltip(string path) => Loc.Instance.Format(WorkspaceTooltipKey, path);

    public static string ProjectVsCode => Loc.Instance[ProjectVsCodeKey];

    public static string WorkspaceVsCode => Loc.Instance[WorkspaceVsCodeKey];

    public static string ProjectExplorer => Loc.Instance[ProjectExplorerKey];

    public static string WorkspaceExplorer => Loc.Instance[WorkspaceExplorerKey];

    public static string Resources => Loc.Instance[ResourcesKey];

    public static string ResourcesTooltip => Loc.Instance[ResourcesTooltipKey];

    /// <summary>The value of a section title key; <c>SettingsSection</c> wants the <c>...Key</c> constant, not this.</summary>
    public static string ApprovalsSectionTitle => Loc.Instance[ApprovalsSectionTitleKey];

    public static string SkillsSectionTitle => Loc.Instance[SkillsSectionTitleKey];

    public static string McpSectionTitle => Loc.Instance[McpSectionTitleKey];

    public static string ToolsSectionTitle => Loc.Instance[ToolsSectionTitleKey];

    public static string ApprovalsSectionHint => Loc.Instance[ApprovalsSectionHintKey];

    public static string ApprovalsSectionEmpty => Loc.Instance[ApprovalsSectionEmptyKey];

    public static string ApprovalsSectionUnavailable => Loc.Instance[ApprovalsSectionUnavailableKey];

    public static string SkillsSectionHint => Loc.Instance[SkillsSectionHintKey];

    public static string SkillsSectionEmpty => Loc.Instance[SkillsSectionEmptyKey];

    public static string McpSectionHint => Loc.Instance[McpSectionHintKey];

    public static string McpSectionEmpty => Loc.Instance[McpSectionEmptyKey];

    public static string McpSectionEnabled => Loc.Instance[McpSectionEnabledKey];

    public static string McpSectionDisabled => Loc.Instance[McpSectionDisabledKey];

    public static string ToolsSectionHint => Loc.Instance[ToolsSectionHintKey];

    public static string ToolsSectionEmpty => Loc.Instance[ToolsSectionEmptyKey];

    public static string ToolsSectionNoSession => Loc.Instance[ToolsSectionNoSessionKey];

    /// <summary>Title of a built-in group (<see cref="Forge.Abstractions.ToolGroups"/>); an unknown group shows its id.</summary>
    public static string ToolsGroup(string group) => group switch
    {
        Forge.Abstractions.ToolGroups.Files => Loc.Instance[ToolsGroupFilesKey],
        Forge.Abstractions.ToolGroups.Git => Loc.Instance[ToolsGroupGitKey],
        Forge.Abstractions.ToolGroups.PullRequests => Loc.Instance[ToolsGroupPullRequestsKey],
        Forge.Abstractions.ToolGroups.Web => Loc.Instance[ToolsGroupWebKey],
        Forge.Abstractions.ToolGroups.Session => Loc.Instance[ToolsGroupSessionKey],
        Forge.Abstractions.ToolGroups.Agents => Loc.Instance[ToolsGroupAgentsKey],
        Forge.Abstractions.ToolGroups.Retrieval => Loc.Instance[ToolsGroupRetrievalKey],
        Forge.Abstractions.ToolGroups.Extensions => Loc.Instance[ToolsGroupExtensionsKey],
        _ => group,
    };

    public static string ToolsGroupExtensionTools => Loc.Instance[ToolsGroupExtensionToolsKey];

    public static string ToolsGroupMcp => Loc.Instance[ToolsGroupMcpKey];

    public static string ToolsRequired => Loc.Instance[ToolsRequiredKey];

    public static string ToolsFollowsSource => Loc.Instance[ToolsFollowsSourceKey];

    public static string ToolsInventory(int plugins, int tools, int commands) =>
        Loc.Instance.Format(
            ToolsInventoryKey,
            TextFormat.Number(plugins),
            TextFormat.Number(tools),
            TextFormat.Number(commands));

    private static void Register()
    {
        Loc.Register(ApprovalsLabelKey, "审批", "Approvals");
        Loc.Register(SkillsLabelKey, "技能", "Skills");
        Loc.Register(McpLabelKey, "MCP", "MCP");
        Loc.Register(ToolsLabelKey, "工具", "Tools");
        Loc.Register(CountPairKey, "{0}/{1}", "{0}/{1}");
        Loc.Register(CountUnknownKey, "{0}/?", "{0}/?");
        Loc.Register(MenuEntryKey, "{0}  {1}", "{0}  {1}");

        Loc.Register(
            ApprovalsTooltipKey,
            "本会话待审 {0} 条，审批库共 {1} 条记录",
            "{0} waiting in this session, {1} records in the approvals database");
        Loc.Register(ApprovalsUnavailableKey, "审批库不可读", "The approvals database cannot be read");
        Loc.Register(
            SkillsTooltipKey,
            "本会话用过 {0} 个技能，工作区与用户目录共发现 {1} 个",
            "{0} skills used in this session, {1} discovered for this workspace and the user directory");
        Loc.Register(McpTooltipKey, "{0} 台就绪 / {1} 台已配置", "{0} ready / {1} configured");
        Loc.Register(
            ToolsTooltipKey,
            "本会话调用过 {0} 种工具，可用 {1} 种",
            "{0} tools called in this session, {1} available");
        Loc.Register(
            ToolsTooltipUnknownKey,
            "本会话调用过 {0} 种工具，可用数要等本会话跑完第一轮才知道",
            "{0} tools called in this session; the available count appears after the first turn");

        Loc.Register(UsageTokensKey, "↑{0} ↓{1}", "↑{0} ↓{1}");
        // The tooltip is a readout, not prose: one capital per number (C context window, R cache read, W cache write,
        // RC cache-read ratio), so it reads the same in both interface languages.
        Loc.Register(UsageWindowKey, "C {0}", "C {0}");
        Loc.Register(UsageCacheReadKey, "R {0}", "R {0}");
        Loc.Register(UsageCacheWriteKey, "W {0}", "W {0}");
        Loc.Register(UsageCacheKey, "RC {0}", "RC {0}");
        Loc.Register(UsageContextKey, "{0}/{1}", "{0}/{1}");
        Loc.Register(CacheReadKey, "RC {0}%", "RC {0}%");
        Loc.Register(UsageNoneKey, "暂无用量", "No usage yet");

        Loc.Register(WorkspaceTooltipKey, "工作区：{0}", "Workspace: {0}");
        Loc.Register(ProjectVsCodeKey, "在 VS Code 中打开项目", "Open project in VS Code");
        Loc.Register(WorkspaceVsCodeKey, "在 VS Code 中打开工作区", "Open workspace in VS Code");
        Loc.Register(ProjectExplorerKey, "在资源管理器中打开项目", "Reveal project in Explorer");
        Loc.Register(WorkspaceExplorerKey, "在资源管理器中打开工作区", "Reveal workspace in Explorer");
        Loc.Register(ResourcesKey, "资源", "Resources");
        Loc.Register(
            ResourcesTooltipKey,
            "会话资源：审批、技能、MCP 与工具的计数，点开可分别查看",
            "Session resources: approvals, skills, MCP and tools - open to see each");

        Loc.Register(ApprovalsSectionTitleKey, "审批", "Approvals");
        Loc.Register(SkillsSectionTitleKey, "技能", "Skills");
        Loc.Register(McpSectionTitleKey, "MCP", "MCP");
        Loc.Register(ToolsSectionTitleKey, "工具", "Tools");

        Loc.Register(
            ApprovalsSectionHintKey,
            "插件版本的审批记录（只读）。待审卡片在会话右下角，批准与撤销由插件面板负责。",
            "Approval records per plugin version (read-only). Pending cards live in the session's bottom-right corner.");
        Loc.Register(ApprovalsSectionEmptyKey, "审批库里还没有记录", "The approvals database holds no records yet");
        Loc.Register(ApprovalsSectionUnavailableKey, "审批库不可读，无法列出记录", "The approvals database cannot be read");

        Loc.Register(
            SkillsSectionHintKey,
            "工作区 .github/skills、.forge/skills 与用户目录 skills 下发现的技能（只读）。",
            "Skills discovered in the workspace's .github/skills and .forge/skills and in the user directory's skills (read-only).");
        Loc.Register(SkillsSectionEmptyKey, "没有发现技能", "No skills discovered");

        Loc.Register(
            McpSectionHintKey,
            "mcp.json 里的服务器及其当前状态（只读）。",
            "The servers in mcp.json and their current state (read-only).");
        Loc.Register(McpSectionEmptyKey, "没有配置 MCP 服务器", "No MCP server configured");
        Loc.Register(McpSectionEnabledKey, "已启用", "Enabled");
        Loc.Register(McpSectionDisabledKey, "已停用", "Disabled");

        Loc.Register(
            ToolsSectionHintKey,
            "当前会话注册的工具。内置工具可逐个关闭，从下一回合起对主 Agent 与子 Agent 生效；扩展工具随插件启停，MCP 工具随服务器启停。",
            "The tools this session registers. Built-in tools can be switched off one by one; the change applies to the main agent and its sub-agents from the next turn. Extension tools follow their plugin, MCP tools their server.");
        Loc.Register(ToolsSectionEmptyKey, "没有可列出的工具", "No tools to list");
        Loc.Register(ToolsSectionNoSessionKey, "没有活动会话", "No active session");
        Loc.Register(
            ToolsInventoryKey,
            "应用内扩展宿主：插件 {0} · 工具 {1} · 命令 {2}",
            "Extension host: {0} plugins · {1} tools · {2} commands");
        Loc.Register(ToolsGroupFilesKey, "内置 · 文件与命令", "Built-in · Files & shell");
        Loc.Register(ToolsGroupGitKey, "内置 · Git", "Built-in · Git");
        Loc.Register(ToolsGroupPullRequestsKey, "内置 · 拉取请求与评审", "Built-in · Pull requests & review");
        Loc.Register(ToolsGroupWebKey, "内置 · 网络", "Built-in · Web");
        Loc.Register(ToolsGroupSessionKey, "内置 · 会话", "Built-in · Session");
        Loc.Register(ToolsGroupAgentsKey, "内置 · 子 Agent", "Built-in · Sub-agents");
        Loc.Register(ToolsGroupRetrievalKey, "内置 · 历史与产物", "Built-in · History & artifacts");
        Loc.Register(ToolsGroupExtensionsKey, "内置 · 扩展管理", "Built-in · Extension management");
        Loc.Register(ToolsGroupExtensionToolsKey, "扩展工具（插件）", "Extension tools (plugins)");
        Loc.Register(ToolsGroupMcpKey, "MCP 工具", "MCP tools");
        Loc.Register(ToolsRequiredKey, "必需，不能关闭", "Required, cannot be switched off");
        Loc.Register(ToolsFollowsSourceKey, "随插件或服务器启停", "Follows its plugin or server");

    }
}
