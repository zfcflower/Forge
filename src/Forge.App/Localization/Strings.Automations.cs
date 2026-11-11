using System.Globalization;
using System.Runtime.CompilerServices;
using Forge.Abstractions;

namespace Forge.App.Localization;

/// <summary>
/// The automations table outside the settings page: built-in names, what the mailbox reports, notifications, the title-bar
/// entry, the session banner and the prompts automations send to the model.
/// <para>Keys: <c>automations.*</c> (the settings page keeps its own table). Model-facing prompts are resolved in the UI
/// language of the moment they are sent.</para>
/// </summary>
internal static class AutomationStrings
{
    // ---------------------------------------------------------------- mailbox
    public const string SessionBusyKey = "automations.session.busy";
    public const string SessionUnavailableKey = "automations.session.unavailable";
    public const string SessionClosedKey = "automations.session.closed";
    public const string TurnStoppedKey = "automations.session.turnStopped";
    public const string UserContinuedKey = "automations.session.userContinued";
    public const string OriginNoticeKey = "automations.session.origin";
    public const string CancelledByUserKey = "automations.session.cancelledByUser";
    public const string CreatedByAutomationKey = "automations.session.createdBy";

    // ---------------------------------------------------------------- built-ins
    public const string QuotaResumeNameKey = "automations.builtin.quotaResume";
    public const string QuotaResumeDescriptionKey = "automations.builtin.quotaResume.description";
    public const string CiChainNameKey = "automations.builtin.ciChain";
    public const string CiChainDescriptionKey = "automations.builtin.ciChain.description";
    public const string SessionWatchNameKey = "automations.builtin.sessionWatch";
    public const string SessionWatchDescriptionKey = "automations.builtin.sessionWatch.description";
    public const string HousekeepingNameKey = "automations.builtin.housekeeping";
    public const string HousekeepingDescriptionKey = "automations.builtin.housekeeping.description";

    // ---------------------------------------------------------------- generic run notifications
    public const string RunFailedTitleKey = "automations.run.failedTitle";
    public const string RunFailedBodyKey = "automations.run.failedBody";
    public const string RunWithdrawnBodyKey = "automations.run.withdrawnBody";
    public const string RunCompletedTitleKey = "automations.run.completedTitle";
    public const string RunCompletedBodyKey = "automations.run.completedBody";
    public const string RemindTitleKey = "automations.remind.title";
    public const string NoProjectKey = "automations.run.noProject";
    public const string NoTargetKey = "automations.run.noTarget";

    // ---------------------------------------------------------------- quota resume
    public const string QuotaWaitingTitleKey = "automations.quota.waitingTitle";
    public const string QuotaWaitingBodyKey = "automations.quota.waitingBody";
    public const string QuotaWaitingProbeBodyKey = "automations.quota.waitingProbeBody";
    public const string QuotaResumedTitleKey = "automations.quota.resumedTitle";
    public const string QuotaResumedBodyKey = "automations.quota.resumedBody";
    public const string QuotaContinuePromptKey = "automations.quota.continuePrompt";
    public const string QuotaAgainKey = "automations.quota.again";

    // ---------------------------------------------------------------- CI chain
    public const string CiFailedTitleKey = "automations.ci.failedTitle";
    public const string CiAnalysingBodyKey = "automations.ci.analysingBody";
    public const string CiPushedTitleKey = "automations.ci.pushedTitle";
    public const string CiPushedBodyKey = "automations.ci.pushedBody";
    public const string CiNotPushedBodyKey = "automations.ci.notPushedBody";
    public const string CiNotPushedForkBodyKey = "automations.ci.notPushedForkBody";
    public const string CiNoFixBodyKey = "automations.ci.noFixBody";
    public const string CiStoppedTitleKey = "automations.ci.stoppedTitle";
    public const string CiStoppedSameFailureKey = "automations.ci.stoppedSameFailure";
    public const string CiStoppedPushFailedKey = "automations.ci.stoppedPushFailed";
    public const string CiStoppedBudgetKey = "automations.ci.stoppedBudget";
    public const string CiStoppedBaseBranchKey = "automations.ci.stoppedBaseBranch";
    public const string CiStoppedUnsafePushKey = "automations.ci.stoppedUnsafePush";
    public const string CiRefuseDetachedKey = "automations.ci.refuseDetached";
    public const string CiRefuseMismatchKey = "automations.ci.refuseMismatch";
    public const string CiRefuseNotDescendedKey = "automations.ci.refuseNotDescended";
    public const string CiRefuseProtectedKey = "automations.ci.refuseProtected";
    public const string CiRefuseHeadKey = "automations.ci.refuseHead";
    public const string CiChecksGoneKey = "automations.ci.checksGone";
    public const string CiScoutTaskKey = "automations.ci.scoutTask";
    public const string CiFixPromptKey = "automations.ci.fixPrompt";
    public const string CiLogUnavailableKey = "automations.ci.logUnavailable";

    // ---------------------------------------------------------------- session watch
    public const string WatchStalledTitleKey = "automations.watch.stalledTitle";
    public const string WatchStalledBodyKey = "automations.watch.stalledBody";
    public const string WatchToolTitleKey = "automations.watch.toolTitle";
    public const string WatchToolBodyKey = "automations.watch.toolBody";
    public const string WatchApprovalTitleKey = "automations.watch.approvalTitle";
    public const string WatchApprovalBodyKey = "automations.watch.approvalBody";
    public const string WatchFinishedTitleKey = "automations.watch.finishedTitle";
    public const string WatchFinishedBodyKey = "automations.watch.finishedBody";

    // ---------------------------------------------------------------- housekeeping
    public const string HousekeepingSummaryKey = "automations.housekeeping.summary";

    // ---------------------------------------------------------------- title bar and session banner
    public const string TitleBarLabelKey = "automations.titleBar.label";
    public const string TitleBarTooltipKey = "automations.titleBar.tooltip";
    public const string BannerWaitingQuotaKey = "automations.banner.waitingQuota";
    public const string BannerWaitingQuotaProbeKey = "automations.banner.waitingQuotaProbe";
    public const string BannerSoonKey = "automations.banner.soon";
    public const string BannerInMinutesKey = "automations.banner.inMinutes";
    public const string BannerInHoursKey = "automations.banner.inHours";
    public const string BannerWaitingIdleKey = "automations.banner.waitingIdle";
    public const string BannerRunningKey = "automations.banner.running";
    public const string BannerCancelKey = "automations.banner.cancel";

    public static string SessionBusyText => Loc.Instance[SessionBusyKey];

    public static string SessionUnavailableText => Loc.Instance[SessionUnavailableKey];

    public static string SessionClosedText => Loc.Instance[SessionClosedKey];

    public static string TurnStoppedText => Loc.Instance[TurnStoppedKey];

    public static string UserContinuedText => Loc.Instance[UserContinuedKey];

    public static string CancelledByUserText => Loc.Instance[CancelledByUserKey];

    public static string NoProjectText => Loc.Instance[NoProjectKey];

    public static string NoTargetText => Loc.Instance[NoTargetKey];

    public static LocText OriginNotice(string origin) => new(OriginNoticeKey, origin);

    /// <summary>The shown name of an automation: built-ins are localized, user definitions keep their own name.</summary>
    public static string DisplayName(string automationId, string name) => automationId switch
    {
        BuiltInAutomations.QuotaResume => Loc.Instance[QuotaResumeNameKey],
        BuiltInAutomations.CiChain => Loc.Instance[CiChainNameKey],
        BuiltInAutomations.SessionWatch => Loc.Instance[SessionWatchNameKey],
        BuiltInAutomations.Housekeeping => Loc.Instance[HousekeepingNameKey],
        _ => name,
    };

    /// <summary>The description of a built-in; null for a user definition.</summary>
    public static string? BuiltInDescription(string automationId) => automationId switch
    {
        BuiltInAutomations.QuotaResume => Loc.Instance[QuotaResumeDescriptionKey],
        BuiltInAutomations.CiChain => Loc.Instance[CiChainDescriptionKey],
        BuiltInAutomations.SessionWatch => Loc.Instance[SessionWatchDescriptionKey],
        BuiltInAutomations.Housekeeping => Loc.Instance[HousekeepingDescriptionKey],
        _ => null,
    };

    public static LocText RunFailedTitle => new(RunFailedTitleKey);

    public static LocText RunFailedBody(string name, string message) => new(RunFailedBodyKey, name, message);

    public static LocText RunWithdrawnBody(string name, string reason) => new(RunWithdrawnBodyKey, name, reason);

    public static LocText RunCompletedTitle => new(RunCompletedTitleKey);

    public static LocText RunCompletedBody(string name, string session) => new(RunCompletedBodyKey, name, session);

    public static LocText RemindTitle(string name) => new(RemindTitleKey, name);

    public static LocText QuotaWaitingTitle => new(QuotaWaitingTitleKey);

    public static LocText QuotaWaitingBody(string session, string time) => new(QuotaWaitingBodyKey, session, time);

    public static LocText QuotaWaitingProbeBody(string session) => new(QuotaWaitingProbeBodyKey, session);

    public static LocText QuotaResumedTitle => new(QuotaResumedTitleKey);

    public static LocText QuotaResumedBody(string session) => new(QuotaResumedBodyKey, session);

    public static string QuotaContinuePrompt => Loc.Instance[QuotaContinuePromptKey];

    public static string QuotaAgain(string message) => Loc.Instance.Format(QuotaAgainKey, message);

    public static LocText CiFailedTitle => new(CiFailedTitleKey);

    public static LocText CiAnalysingBody(string pr, string session, string count) => new(CiAnalysingBodyKey, pr, session, count);

    public static LocText CiPushedTitle => new(CiPushedTitleKey);

    public static LocText CiPushedBody(string branch) => new(CiPushedBodyKey, branch);

    public static LocText CiNotPushedBody(string session) => new(CiNotPushedBodyKey, session);

    public static LocText CiNotPushedForkBody(string session) => new(CiNotPushedForkBodyKey, session);

    public static LocText CiNoFixBody(string session) => new(CiNoFixBodyKey, session);

    public static LocText CiStoppedTitle => new(CiStoppedTitleKey);

    public static string CiStoppedSameFailure => Loc.Instance[CiStoppedSameFailureKey];

    public static string CiStoppedPushFailed(string message) => Loc.Instance.Format(CiStoppedPushFailedKey, message);

    public static string CiStoppedBudget(string count) => Loc.Instance.Format(CiStoppedBudgetKey, count);

    public static string CiStoppedBaseBranch(string branch) => Loc.Instance.Format(CiStoppedBaseBranchKey, branch);

    public static string CiStoppedUnsafePush(string reason) => Loc.Instance.Format(CiStoppedUnsafePushKey, reason);

    public static string CiRefusal(Forge.App.Automations.CiPushRefusal refusal, string branch) => refusal switch
    {
        Forge.App.Automations.CiPushRefusal.DetachedHead => Loc.Instance[CiRefuseDetachedKey],
        Forge.App.Automations.CiPushRefusal.BranchMismatch => Loc.Instance.Format(CiRefuseMismatchKey, branch),
        Forge.App.Automations.CiPushRefusal.NotDescended => Loc.Instance.Format(CiRefuseNotDescendedKey, branch),
        Forge.App.Automations.CiPushRefusal.ProtectedBranch => Loc.Instance.Format(CiRefuseProtectedKey, branch),
        _ => Loc.Instance[CiRefuseHeadKey],
    };

    public static string CiChecksGone => Loc.Instance[CiChecksGoneKey];

    public static string CiScoutTask(string pr, string branch, string failures) => Loc.Instance.Format(CiScoutTaskKey, pr, branch, failures);

    public static string CiFixPrompt(string pr, string analysis) => Loc.Instance.Format(CiFixPromptKey, pr, analysis);

    public static string CiLogUnavailable(string message) => Loc.Instance.Format(CiLogUnavailableKey, message);

    public static LocText WatchStalledTitle => new(WatchStalledTitleKey);

    public static LocText WatchStalledBody(string session, string minutes) => new(WatchStalledBodyKey, session, minutes);

    public static LocText WatchToolTitle => new(WatchToolTitleKey);

    public static LocText WatchToolBody(string session, string minutes) => new(WatchToolBodyKey, session, minutes);

    public static LocText WatchApprovalTitle => new(WatchApprovalTitleKey);

    public static LocText WatchApprovalBody(string session, string minutes) => new(WatchApprovalBodyKey, session, minutes);

    public static LocText WatchFinishedTitle => new(WatchFinishedTitleKey);

    public static LocText WatchFinishedBody(string session) => new(WatchFinishedBodyKey, session);

    public static string HousekeepingSummary(string worktrees, string folders, string runs, string sessions) =>
        Loc.Instance.Format(HousekeepingSummaryKey, worktrees, folders, runs, sessions);

    public static string TitleBarTooltip(string running) => Loc.Instance.Format(TitleBarTooltipKey, running);

    public static string BannerWaitingQuota(string time, string remaining) => Loc.Instance.Format(BannerWaitingQuotaKey, time, remaining);

    /// <summary>Time left until a quota resume, rounded up to the minute: "shortly", "in about 5 min", "in about 2 h 05 min".</summary>
    public static string BannerRemaining(TimeSpan left)
    {
        if (left <= TimeSpan.FromSeconds(30))
        {
            return Loc.Instance[BannerSoonKey];
        }

        var minutes = (long)Math.Ceiling(left.TotalMinutes);
        return minutes < 60
            ? Loc.Instance.Format(BannerInMinutesKey, minutes.ToString(CultureInfo.InvariantCulture))
            : Loc.Instance.Format(
                BannerInHoursKey,
                (minutes / 60).ToString(CultureInfo.InvariantCulture),
                (minutes % 60).ToString("00", CultureInfo.InvariantCulture));
    }

    /// <summary>The session banner for an active run; the quota countdown is relative to <paramref name="now"/>.</summary>
    public static string Banner(AutomationRun run, DateTimeOffset now)
    {
        var name = DisplayName(run.AutomationId, run.AutomationName);
        return run.Status switch
        {
            AutomationRunStatus.WaitingQuota when run.ResumeAt is { } at => BannerWaitingQuota(TextFormat.ShortStamp(at), BannerRemaining(at - now)),
            AutomationRunStatus.WaitingQuota => BannerWaitingQuotaProbe,
            AutomationRunStatus.WaitingIdle => BannerWaitingIdle(name),
            _ => BannerRunning(name),
        };
    }

    public static string BannerWaitingQuotaProbe => Loc.Instance[BannerWaitingQuotaProbeKey];

    public static string BannerWaitingIdle(string name) => Loc.Instance.Format(BannerWaitingIdleKey, name);

    public static string BannerRunning(string name) => Loc.Instance.Format(BannerRunningKey, name);

    [ModuleInitializer]
    internal static void Initialize() => Register();

    /// <summary>Idempotent: registering the same key with the same texts twice is a no-op.</summary>
    internal static void Register()
    {
        Loc.Register(SessionBusyKey, "会话正在运行，暂不能接收自动化消息", "The session is busy and cannot take an automation message now");
        Loc.Register(SessionUnavailableKey, "会话已被接手、归档或删除", "The session was taken over, archived or removed");
        Loc.Register(SessionClosedKey, "会话标签已关闭，消息已撤回", "The session tab was closed; the message was withdrawn");
        Loc.Register(TurnStoppedKey, "这一轮被手动停止", "The turn was stopped by hand");
        Loc.Register(UserContinuedKey, "你已手动继续该会话", "You continued the session yourself");
        Loc.Register(OriginNoticeKey, "以下消息由自动化任务「{0}」发送", "The next message was sent by automation \"{0}\"");
        Loc.Register(CancelledByUserKey, "已手动取消", "Cancelled by you");
        Loc.Register(CreatedByAutomationKey, "由自动化任务创建", "Created by an automation");

        Loc.Register(QuotaResumeNameKey, "额度恢复后继续", "Resume after quota reset");
        Loc.Register(
            QuotaResumeDescriptionKey,
            "模型额度用尽时记下会话，额度恢复后自动发送“继续”。",
            "When a model's quota runs out, the session is continued automatically once the quota resets.");
        Loc.Register(CiChainNameKey, "CI 失败分析与修复", "CI failure analysis and fix");
        Loc.Register(
            CiChainDescriptionKey,
            "分支会话的 PR 检查失败时，Scout 只读分析日志，主 Agent 修复后推送到 PR 分支（从不推送基线分支）。",
            "When a branch session's PR checks fail, Scout analyses the logs read-only and the main agent fixes them and pushes to the PR branch (never the base branch).");
        Loc.Register(SessionWatchNameKey, "长任务看护", "Long task watch");
        Loc.Register(
            SessionWatchDescriptionKey,
            "长任务停滞、审批久等未处理或窗口在后台时一轮完成，都会提醒你。",
            "Notifies you when a long turn stalls, an approval waits too long, or a turn finishes while the window is in the background.");
        Loc.Register(HousekeepingNameKey, "周期清理", "Housekeeping");
        Loc.Register(
            HousekeepingDescriptionKey,
            "每天 04:00 清理失效的 worktree 记录、空的临时目录、旧的运行记录，并归档 7 天前由自动化创建的会话。",
            "Daily at 04:00: prunes stale worktree records, empty scratch folders and old run records, and archives automation-created sessions older than 7 days.");

        Loc.Register(RunFailedTitleKey, "自动化任务失败", "Automation failed");
        Loc.Register(RunFailedBodyKey, "「{0}」：{1}", "\"{0}\": {1}");
        Loc.Register(RunWithdrawnBodyKey, "「{0}」的消息已撤回：{1}", "The message of \"{0}\" was withdrawn: {1}");
        Loc.Register(RunCompletedTitleKey, "自动化任务完成", "Automation finished");
        Loc.Register(RunCompletedBodyKey, "「{0}」已完成，结果在会话「{1}」中。", "\"{0}\" finished; see session \"{1}\".");
        Loc.Register(RemindTitleKey, "提醒：{0}", "Reminder: {0}");
        Loc.Register(NoProjectKey, "找不到通用项目，无法创建临时会话", "No General project to create the scratch session in");
        Loc.Register(NoTargetKey, "目标会话不存在或不可用", "The target session does not exist or is unavailable");

        Loc.Register(QuotaWaitingTitleKey, "模型额度已用尽", "Model quota exhausted");
        Loc.Register(QuotaWaitingBodyKey, "「{0}」将在 {1} 自动继续。", "\"{0}\" continues automatically at {1}.");
        Loc.Register(QuotaWaitingProbeBodyKey, "「{0}」将在额度恢复后自动继续（定期探测）。", "\"{0}\" continues automatically once the quota is back (checked periodically).");
        Loc.Register(QuotaResumedTitleKey, "额度已恢复", "Quota restored");
        Loc.Register(QuotaResumedBodyKey, "「{0}」已自动继续。", "\"{0}\" continued automatically.");
        Loc.Register(
            QuotaContinuePromptKey,
            "模型额度已恢复。请从中断的地方继续完成之前的任务。",
            "The model quota has been restored. Continue the previous task from where it stopped.");
        Loc.Register(QuotaAgainKey, "额度再次用尽：{0}", "The quota ran out again: {0}");

        Loc.Register(CiFailedTitleKey, "CI 检查失败", "CI checks failed");
        Loc.Register(CiAnalysingBodyKey, "PR #{0}（{1}）有 {2} 项检查失败，Scout 正在分析。", "PR #{0} ({1}): {2} checks failed; Scout is analysing.");
        Loc.Register(CiPushedTitleKey, "已推送 CI 修复", "CI fix pushed");
        Loc.Register(CiPushedBodyKey, "修复已推送到 {0}，等待新的检查结果。", "The fix was pushed to {0}; waiting for the new checks.");
        Loc.Register(CiNotPushedForkBodyKey, "「{0}」的修复已提交在本地；该 PR 来自 fork，自动推送只推本仓库的分支，未推送。", "The fix in \"{0}\" is committed locally; the PR comes from a fork and auto-push only pushes branches of this repository.");
        Loc.Register(CiNotPushedBodyKey, "「{0}」的修复已提交在本地；该项目关闭了自动推送。", "The fix in \"{0}\" is committed locally; auto-push is off for this project.");
        Loc.Register(CiNoFixBodyKey, "「{0}」没有提交修复，请查看分析结果。", "\"{0}\" committed no fix; see the analysis.");
        Loc.Register(CiStoppedTitleKey, "CI 自动修复已停止", "CI auto-fix stopped");
        Loc.Register(CiStoppedSameFailureKey, "同样的失败再次出现，需要人工处理", "The same failure came back; it needs you");
        Loc.Register(CiStoppedPushFailedKey, "推送失败：{0}", "Push failed: {0}");
        Loc.Register(CiStoppedBudgetKey, "已达到 {0} 次自动修复上限", "Reached the limit of {0} automatic fixes");
        Loc.Register(CiStoppedBaseBranchKey, "PR 分支与基线分支 {0} 相同，不自动推送", "The PR branch is the base branch {0}; not pushing");
        Loc.Register(CiStoppedUnsafePushKey, "为避免推错分支，未自动推送：{0}", "Not pushing, to avoid pushing the wrong branch: {0}");
        Loc.Register(CiRefuseDetachedKey, "会话处于游离 HEAD", "the session is on a detached HEAD");
        Loc.Register(CiRefuseMismatchKey, "会话当前分支不是 PR 分支 {0}，也未跟踪 origin/{0}", "the session branch is not the PR branch {0} and does not track origin/{0}");
        Loc.Register(CiRefuseNotDescendedKey, "当前提交不是 PR 分支 {0} 头部提交的后代", "the current commit does not descend from the head of PR branch {0}");
        Loc.Register(CiRefuseProtectedKey, "{0} 是基线分支或仓库默认分支", "{0} is the base or the default branch of the repository");
        Loc.Register(CiRefuseHeadKey, "无法读取当前提交", "the current commit cannot be read");
        Loc.Register(CiChecksGoneKey, "失败的检查已不存在（可能已重跑或 PR 已更新）", "The failed checks are gone (re-run, or the PR moved on)");
        Loc.Register(
            CiScoutTaskKey,
            "PR #{0}（分支 {1}）的 CI 检查失败。失败的任务与日志末尾如下。\n请只读调查：定位根因（引用文件与行号），判断是代码问题、测试问题还是环境/偶发问题，并给出最小修复建议。不要修改任何文件。\n\n{2}",
            "The CI checks of PR #{0} (branch {1}) failed. The failed jobs and the tails of their logs follow.\nInvestigate read-only: find the root cause (cite files and lines), decide whether it is a code problem, a test problem or an environment/flaky problem, and propose the smallest fix. Do not change any file.\n\n{2}");
        Loc.Register(
            CiFixPromptKey,
            "PR #{0} 的 CI 检查失败。Scout 的分析如下：\n\n{1}\n\n请修复根因并提交（不要推送，推送由自动化处理）。如果判断是偶发或环境问题、无需改代码，请直接说明，不要提交。",
            "The CI checks of PR #{0} failed. Scout's analysis:\n\n{1}\n\nFix the root cause and commit (do not push; the automation pushes). If it is a flaky or environment problem that needs no code change, say so and do not commit.");
        Loc.Register(CiLogUnavailableKey, "（日志不可用：{0}）", "(log unavailable: {0})");

        Loc.Register(WatchStalledTitleKey, "长任务可能停滞", "A long turn may be stuck");
        Loc.Register(WatchStalledBodyKey, "「{0}」已 {1} 分钟没有进展。", "\"{0}\" has made no progress for {1} minutes.");
        Loc.Register(WatchToolTitleKey, "命令运行时间很长", "A command has been running a long time");
        Loc.Register(WatchToolBodyKey, "「{0}」里的命令已运行 {1} 分钟，Agent 仍在等它结束。", "A command in \"{0}\" has been running for {1} minutes; the agent is still waiting for it.");
        Loc.Register(WatchApprovalTitleKey, "审批仍在等待", "An approval is still waiting");
        Loc.Register(WatchApprovalBodyKey, "「{0}」的审批已等待 {1} 分钟。", "An approval in \"{0}\" has waited {1} minutes.");
        Loc.Register(WatchFinishedTitleKey, "后台任务已完成", "Background turn finished");
        Loc.Register(WatchFinishedBodyKey, "「{0}」这一轮已完成。", "\"{0}\" finished its turn.");

        Loc.Register(
            HousekeepingSummaryKey,
            "整理了 {0} 个仓库的 worktree 记录，删除空临时目录 {1} 个、运行记录 {2} 条，归档会话 {3} 个",
            "Pruned the worktree records of {0} repositories, removed {1} empty scratch folders and {2} run records, archived {3} sessions");

        Loc.Register(TitleBarLabelKey, "自动化任务", "Automations");
        Loc.Register(TitleBarTooltipKey, "自动化任务：{0} 个执行中（点击管理）", "Automations: {0} running (click to manage)");
        Loc.Register(BannerWaitingQuotaKey, "模型额度已用尽，将于 {0}（{1}）自动继续", "Model quota exhausted; continues automatically at {0} ({1})");
        Loc.Register(BannerSoonKey, "即将", "shortly");
        Loc.Register(BannerInMinutesKey, "约 {0} 分钟后", "in about {0} min");
        Loc.Register(BannerInHoursKey, "约 {0} 小时 {1} 分钟后", "in about {0} h {1} min");
        Loc.Register(BannerWaitingQuotaProbeKey, "模型额度已用尽，恢复后自动继续", "Model quota exhausted; continues once it is back");
        Loc.Register(BannerWaitingIdleKey, "自动化任务「{0}」将在会话空闲后发送消息", "Automation \"{0}\" sends its message once the session is idle");
        Loc.Register(BannerRunningKey, "自动化任务「{0}」执行中", "Automation \"{0}\" is running");
        Loc.Register(BannerCancelKey, "取消", "Cancel");
    }
}
