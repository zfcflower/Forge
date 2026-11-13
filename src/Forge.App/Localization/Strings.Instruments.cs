using System.Globalization;
using System.Runtime.CompilerServices;
using Forge.Abstractions;
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
    public const string ContextPercentKey = "session.strip.context.percent";
    public const string ContextTokensKey = "session.strip.context.tokens";
    public const string ContextTooltipKey = "session.strip.context.tooltip";
    public const string ContextTooltipUnknownKey = "session.strip.context.tooltip.unknown";
    public const string ContextTooltipPendingKey = "session.strip.context.tooltip.pending";
    public const string ContextCompactNameKey = "session.strip.context.compact";
    public const string CacheReadKey = "session.strip.cacheRead";
    public const string UsageNoneKey = "session.strip.usage.none";

    // The provider's account windows. Shown only when the provider reported them with the last response; the numbers are
    // the server's own, so nothing here estimates or extrapolates.
    public const string QuotaFiveHourKey = "session.strip.quota.fiveHour";
    public const string QuotaWeeklyKey = "session.strip.quota.weekly";
    public const string QuotaFiveHourNameKey = "session.strip.quota.fiveHour.name";
    public const string QuotaWeeklyNameKey = "session.strip.quota.weekly.name";
    public const string QuotaWindowKey = "session.strip.quota.window";
    public const string QuotaWindowUnknownKey = "session.strip.quota.window.unknown";
    public const string QuotaResetKey = "session.strip.quota.reset";
    public const string QuotaUsageKey = "session.strip.quota.usage";
    public const string QuotaTooltipKey = "session.strip.quota.tooltip";
    public const string QuotaTooltipWindowKey = "session.strip.quota.tooltip.window";
    public const string QuotaBindingKey = "session.strip.quota.binding";
    public const string QuotaBindingUnknownKey = "session.strip.quota.binding.unknown";
    public const string QuotaOverageKey = "session.strip.quota.overage";
    public const string QuotaNoResetKey = "session.strip.quota.noReset";
    public const string QuotaSoonKey = "session.strip.quota.soon";
    public const string QuotaOverageAllowedKey = "session.strip.quota.overage.allowed";
    public const string QuotaOverageWarningKey = "session.strip.quota.overage.warning";
    public const string QuotaOverageRejectedKey = "session.strip.quota.overage.rejected";
    public const string QuotaOverageUnknownKey = "session.strip.quota.overage.unknown";
    public const string QuotaWindowsPrefixKey = "session.strip.quota.windows";
    public const string QuotaThrottlePrefixKey = "session.strip.quota.throttle";
    public const string QuotaThrottleRequestsKey = "session.strip.quota.throttle.requests";
    public const string QuotaThrottleTokensKey = "session.strip.quota.throttle.tokens";
    public const string QuotaThrottleTooltipKey = "session.strip.quota.throttle.tooltip";
    public const string CacheTtlKey = "session.strip.cache.ttl";
    public const string CacheTtlTooltipKey = "session.strip.cache.ttl.tooltip";

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


    public static string UsageNone => Loc.Instance[UsageNoneKey];
    /// <summary>"62k/170k · 36%": the context over the usable budget, then the percentage.</summary>
    public static string UsageContext(string used, string usable, string percent) =>
        Loc.Instance.Format(UsageContextKey, used, usable, percent);

    /// <summary>"0%": the gauge's label before the first request reported a context size.</summary>
    public static string ContextPercent(string percent) => Loc.Instance.Format(ContextPercentKey, percent);

    /// <summary>"62k": the context size alone, when the model's window is unknown.</summary>
    public static string ContextTokens(string used) => Loc.Instance.Format(ContextTokensKey, used);

    public static string ContextTooltip(string used, string usable, string percent, string trigger, string triggerPercent) =>
        Loc.Instance.Format(ContextTooltipKey, used, usable, percent, trigger, triggerPercent);

    public static string ContextTooltipUnknown(string used) => Loc.Instance.Format(ContextTooltipUnknownKey, used);

    public static string ContextTooltipPending => Loc.Instance[ContextTooltipPendingKey];

    /// <summary>The gauge button's accessible name: what clicking it does.</summary>
    public static string ContextCompactName => Loc.Instance[ContextCompactNameKey];

    /// <summary>"RC 98.5" — the cache-read hit rate in the strip, one decimal; the gauge beside it carries the percentage.</summary>
    public static string CacheRead(string ratio) => Loc.Instance.Format(CacheReadKey, ratio);

    // ---------------------------------------------------------------- account windows (quota)

    public static string QuotaFiveHour => Loc.Instance[QuotaFiveHourKey];

    public static string QuotaWeekly => Loc.Instance[QuotaWeeklyKey];

    public static string QuotaWindow(string label, string percent) => Loc.Instance.Format(QuotaWindowKey, label, percent);

    public static string QuotaWindowUnknown(string label) => Loc.Instance.Format(QuotaWindowUnknownKey, label);

    public static string QuotaReset(string remaining) => Loc.Instance.Format(QuotaResetKey, remaining);

    public static string QuotaUsage(string fiveHour, string weekly) => Loc.Instance.Format(QuotaUsageKey, fiveHour, weekly);

    public static string QuotaTooltip(string fiveHour, string weekly, string binding, string overage) =>
        Loc.Instance.Format(QuotaTooltipKey, fiveHour, weekly, binding, overage);

    public static string QuotaTooltipWindow(string label, string percent, string reset) =>
        Loc.Instance.Format(QuotaTooltipWindowKey, label, percent, reset);

    public static string QuotaBinding(string window) => Loc.Instance.Format(QuotaBindingKey, window);

    public static string QuotaBindingUnknown => Loc.Instance[QuotaBindingUnknownKey];

    public static string QuotaOverage(string state) => Loc.Instance.Format(QuotaOverageKey, state);

    /// <summary>The word that says which of the two things the reading is: a spending window or a short-term rate limit.</summary>
    public static string QuotaWindowsPrefix => Loc.Instance[QuotaWindowsPrefixKey];

    public static string QuotaThrottlePrefix => Loc.Instance[QuotaThrottlePrefixKey];

    public static string QuotaThrottleRequests(string used, string limit) =>
        Loc.Instance.Format(QuotaThrottleRequestsKey, used, limit);

    public static string QuotaThrottleTokens(string used, string limit) =>
        Loc.Instance.Format(QuotaThrottleTokensKey, used, limit);

    public static string QuotaThrottleTooltip(string requests, string tokens, string reset) =>
        Loc.Instance.Format(QuotaThrottleTooltipKey, requests, tokens, reset);

    public static string CacheTtlLabel(string ttl) => Loc.Instance.Format(CacheTtlKey, ttl);

    public static string CacheTtlTooltip(string ttl) => Loc.Instance.Format(CacheTtlTooltipKey, ttl);

    public static string QuotaNoReset => Loc.Instance[QuotaNoResetKey];

    public static string QuotaOverageState(RateLimitStatus status) => status switch
    {
        RateLimitStatus.Allowed => Loc.Instance[QuotaOverageAllowedKey],
        RateLimitStatus.AllowedWarning => Loc.Instance[QuotaOverageWarningKey],
        RateLimitStatus.Rejected => Loc.Instance[QuotaOverageRejectedKey],
        _ => Loc.Instance[QuotaOverageUnknownKey],
    };

    public static string QuotaWindowName(RateLimitWindow window) => window switch
    {
        RateLimitWindow.FiveHour => Loc.Instance[QuotaFiveHourNameKey],
        _ => Loc.Instance[QuotaWeeklyNameKey],
    };

    /// <summary>
    /// A countdown to a window reset, compact because the strip has one line: <c>47m</c>, <c>2h41m</c>, <c>3d</c>. Latin
    /// units in both languages on purpose — the strip is a readout, and these units are the ones people read on quota
    /// meters everywhere. Rounded up, so "soon" never means "already passed".
    /// </summary>
    public static string QuotaCountdown(TimeSpan left)
    {
        if (left <= TimeSpan.FromSeconds(5))
        {
            return Loc.Instance[QuotaSoonKey];
        }

        // Seconds before minutes: a rate limit that rolls over in twelve seconds is exactly the case where "soon" would be the
        // least useful thing to print.
        var seconds = (long)Math.Ceiling(left.TotalSeconds);
        if (seconds < 90)
        {
            return seconds.ToString(CultureInfo.InvariantCulture) + "s";
        }

        if (left >= TimeSpan.FromDays(1))
        {
            return ((long)Math.Ceiling(left.TotalDays)).ToString(CultureInfo.InvariantCulture) + "d";
        }

        var minutes = (long)Math.Ceiling(left.TotalMinutes);
        return minutes < 60
            ? minutes.ToString(CultureInfo.InvariantCulture) + "m"
            : (minutes / 60).ToString(CultureInfo.InvariantCulture) + "h"
              + (minutes % 60).ToString("00", CultureInfo.InvariantCulture) + "m";
    }

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
        Loc.Register(UsageContextKey, "{0}/{1} · {2}%", "{0}/{1} · {2}%");
        Loc.Register(ContextPercentKey, "{0}%", "{0}%");
        Loc.Register(ContextTokensKey, "{0}", "{0}");
        Loc.Register(
            ContextTooltipKey,
            "上下文 {0} / 可用 {1}（{2}%），到 {3}（{4}%）自动压缩。点击立即压缩",
            "Context {0} of {1} usable ({2}%); compacts automatically at {3} ({4}%). Click to compact now");
        Loc.Register(
            ContextTooltipUnknownKey,
            "上下文 {0}；模型窗口未知，不显示百分比。点击立即压缩",
            "Context {0}; the model's window is unknown, so there is no percentage. Click to compact now");
        Loc.Register(
            ContextTooltipPendingKey,
            "上下文用量在下一次模型请求后显示。点击立即压缩",
            "Context usage appears after the next model request. Click to compact now");
        Loc.Register(ContextCompactNameKey, "压缩上下文", "Compact context");
        Loc.Register(CacheReadKey, "RC {0}%", "RC {0}%");
        Loc.Register(UsageNoneKey, "暂无用量", "No usage yet");

        // The account windows, as the provider reported them with the last response.
        Loc.Register(QuotaFiveHourKey, "5h", "5h");
        Loc.Register(QuotaWeeklyKey, "周", "7d");
        Loc.Register(QuotaFiveHourNameKey, "5 小时窗口", "5-hour window");
        Loc.Register(QuotaWeeklyNameKey, "周额度", "weekly window");
        Loc.Register(QuotaWindowKey, "{0} {1}%", "{0} {1}%");
        Loc.Register(QuotaWindowUnknownKey, "{0} —", "{0} —");
        Loc.Register(QuotaResetKey, "{0} 后重置", "resets in {0}");
        Loc.Register(QuotaUsageKey, "{0}   {1}", "{0}   {1}");
        Loc.Register(QuotaTooltipKey, "{0} · {1} · {2} · {3}", "{0} · {1} · {2} · {3}");
        Loc.Register(QuotaTooltipWindowKey, "{0} {1}%（{2}）", "{0} {1}% ({2})");
        Loc.Register(QuotaBindingKey, "当前限制 {0}", "binding: {0}");
        Loc.Register(QuotaBindingUnknownKey, "当前限制未知", "binding unknown");
        Loc.Register(QuotaOverageKey, "超额通道 {0}", "overage {0}");
        Loc.Register(QuotaNoResetKey, "无重置时间", "no reset time");
        Loc.Register(QuotaSoonKey, "即将", "soon");
        Loc.Register(QuotaOverageAllowedKey, "允许", "allowed");
        Loc.Register(QuotaOverageWarningKey, "允许，接近上限", "allowed, near the limit");
        Loc.Register(QuotaOverageRejectedKey, "已关闭", "off");
        Loc.Register(QuotaOverageUnknownKey, "未知", "unknown");
        // Two different meters share this segment, so each says which one it is in one word.
        Loc.Register(QuotaWindowsPrefixKey, "额度", "quota");
        Loc.Register(QuotaThrottlePrefixKey, "限流", "rate limit");
        Loc.Register(QuotaThrottleRequestsKey, "每分钟请求 {0}/{1}", "requests/min {0}/{1}");
        Loc.Register(QuotaThrottleTokensKey, "每分钟令牌 {0}/{1}", "tokens/min {0}/{1}");
        Loc.Register(CacheTtlKey, "缓存 {0}", "cache {0}");
        Loc.Register(
            CacheTtlTooltipKey,
            "提示词缓存 {0}：一小时档写入按 2 倍输入价、五分钟档 1.25 倍，读取都是 0.1 倍且读一次就续命。auto 档在会话停顿超过 5 分钟后才升到一小时（那时缓存本来就已过期，升档不多付一次写入）",
            "Prompt cache {0}: an hour costs 2× the input price to write against 1.25× for five minutes, while a read costs 0.1× either way and refreshes the entry. auto moves to the hour only after a pause longer than five minutes, when the short entry has expired anyway");
        Loc.Register(
            QuotaThrottleTooltipKey,
            "限流（短期限流，不是订阅额度窗口）：请求 {0}，令牌 {1}；{2}",
            "Rate limit (short-term throttling, not a subscription window): requests {0}, tokens {1}; {2}");

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
