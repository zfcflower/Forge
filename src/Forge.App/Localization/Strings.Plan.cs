using System.Runtime.CompilerServices;

namespace Forge.App.Localization;

/// <summary>
/// The plan panel (wave W7): the overlay that shows one session's plan - the plan summary, its steps with their
/// verification, the step to work on next, the replaced summary versions and the actions that leave the panel.
/// <para>Keys: <c>plan.*</c>. A step's status text and the group step count reuse the <c>todo.*</c> keys of the TODO
/// panel, because both surfaces name the same four states.</para>
/// </summary>
internal static class PlanStrings
{
    // ---------------------------------------------------------------- the panel itself
    public const string Title = "plan.title";
    public const string ButtonTooltip = "plan.tooltip";
    public const string Close = "plan.close";
    public const string NoSession = "plan.noSession";
    public const string Refresh = "plan.refresh";

    // ---------------------------------------------------------------- the plan summary
    public const string SummaryTitle = "plan.summary";
    public const string SummaryEmpty = "plan.summary.empty";
    public const string SummaryUpdatedPattern = "plan.summary.updated";
    public const string RevisionBannerPattern = "plan.revision.banner";
    public const string RevisionBack = "plan.revision.back";

    // ---------------------------------------------------------------- the steps
    public const string StepsTitle = "plan.steps";
    public const string StepsEmpty = "plan.steps.empty";
    public const string ProgressPattern = "plan.progress";
    public const string OtherSteps = "plan.otherSteps";
    public const string VerificationPattern = "plan.verification";
    public const string StepSummaryPattern = "plan.step.summary";
    public const string NextTitle = "plan.next";
    public const string NextNone = "plan.next.none";
    public const string Handoff = "plan.handoff";
    public const string HandoffSentPattern = "plan.handoff.sent";
    public const string HandoffQueuedPattern = "plan.handoff.queued";
    public const string HandoffBlocked = "plan.handoff.blocked";
    public const string HandoffReadOnly = "plan.handoff.readOnly";
    public const string HandoffUnavailable = "plan.handoff.unavailable";

    // ---------------------------------------------------------------- the actions
    public const string Copy = "plan.copy";
    public const string Export = "plan.export";
    public const string ExportTitle = "plan.export.title";
    public const string ExportFileType = "plan.export.fileType";
    public const string ExportDonePattern = "plan.export.done";
    public const string ExportFailedPattern = "plan.export.failed";
    public const string OpenPullRequest = "plan.pr";
    public const string OpenPullRequestHint = "plan.pr.hint";

    // ---------------------------------------------------------------- the revision history
    public const string RevisionsTitle = "plan.revisions";
    public const string RevisionsEmpty = "plan.revisions.empty";
    public const string RevisionsCountPattern = "plan.revisions.count";
    public const string RevisionView = "plan.revisions.view";

    // ---------------------------------------------------------------- the status line
    public const string Loading = "plan.status.loading";
    public const string FailedPattern = "plan.status.failed";

    /// <summary>"Updated {0}" under the plan summary; the argument is a short local timestamp.</summary>
    public static LocText SummaryUpdated(string stamp) => new(SummaryUpdatedPattern, stamp);

    /// <summary>"Viewing a revision · replaced {0}" above the summary while an old version is shown.</summary>
    public static LocText RevisionBanner(string stamp) => new(RevisionBannerPattern, stamp);

    /// <summary>"{0}/{1} done" of the whole plan.</summary>
    public static LocText Progress(long done, long total) => new(ProgressPattern, TextFormat.Number(done), TextFormat.Number(total));

    /// <summary>"Verification: {0}" of one step; the argument is the agent's own text and is never translated.</summary>
    public static LocText Verification(string text) => new(VerificationPattern, text);

    /// <summary>"Changes: {0}" of one step.</summary>
    public static LocText StepSummary(string text) => new(StepSummaryPattern, text);

    /// <summary>"Handed to the agent: {0}".</summary>
    public static LocText HandoffSent(string step) => new(HandoffSentPattern, step);

    /// <summary>"Queued for after this turn: {0}".</summary>
    public static LocText HandoffQueued(string step) => new(HandoffQueuedPattern, step);

    /// <summary>"Exported to {0}".</summary>
    public static LocText ExportDone(string path) => new(ExportDonePattern, path);

    /// <summary>"Export failed: {0}".</summary>
    public static LocText ExportFailed(string reason) => new(ExportFailedPattern, reason);

    /// <summary>"{0} versions".</summary>
    public static LocText RevisionCount(long count) => new(RevisionsCountPattern, TextFormat.Number(count));

    /// <summary>"Could not read the plan: {0}".</summary>
    public static LocText Failed(string reason) => new(FailedPattern, reason);

    /// <summary>
    /// The message the panel hands to the agent for one step: the step's id and group so the agent can find it in the
    /// checklist, plus the reminder that finishing the step means marking it done with its summary and verification.
    /// The step's own text is data, never translated.
    /// </summary>
    public static string HandoffPrompt(string title, string id, string? groupTitle, string? note)
    {
        var text = $"Finish step \"{title}\" (todo id {id}";
        text += groupTitle is { Length: > 0 } ? $", group \"{groupTitle}\"): " : "): ";
        text += "do the work it describes, then mark it done with the todo tool and record its summary and verification.";
        if (note is { Length: > 0 })
        {
            text += $" The step's note: {note}";
        }

        return text;
    }

    /// <summary>Idempotent: registering the same key with the same texts twice is a no-op.</summary>
    internal static void Register()
    {
        Loc.Register(Title, "计划", "Plan");
        Loc.Register(ButtonTooltip, "计划摘要、步骤与修订历史", "Plan summary, steps and revision history");
        Loc.Register(Close, "关闭计划面板", "Close the plan panel");
        Loc.Register(NoSession, "没有打开的会话,所以没有计划可看。", "No session is open, so there is no plan to show.");
        Loc.Register(Refresh, "重新读取", "Read again");

        Loc.Register(SummaryTitle, "计划摘要", "Plan summary");
        Loc.Register(
            SummaryEmpty,
            "这个会话还没有计划摘要。agent 用 todo 的 plan_summary_set 写入,它同时是 PR 正文的开头。",
            "This session has no plan summary yet. The agent writes it with todo plan_summary_set, and it becomes the start of the pull request body.");
        Loc.Register(SummaryUpdatedPattern, "更新于 {0}", "Updated {0}");
        Loc.Register(RevisionBannerPattern, "正在看修订 · 该版本被替换于 {0}", "Viewing a revision · replaced {0}");
        Loc.Register(RevisionBack, "回到当前摘要", "Back to the current summary");

        Loc.Register(StepsTitle, "步骤", "Steps");
        Loc.Register(StepsEmpty, "这个会话还没有步骤。", "This session has no steps yet.");
        Loc.Register(ProgressPattern, "{0}/{1} 已完成", "{0}/{1} done");
        Loc.Register(OtherSteps, "其他步骤", "Other steps");
        Loc.Register(VerificationPattern, "验证:{0}", "Verification: {0}");
        Loc.Register(StepSummaryPattern, "改动:{0}", "Changes: {0}");
        Loc.Register(NextTitle, "下一步", "Next step");
        Loc.Register(NextNone, "没有未完成的步骤了。", "No unfinished step is left.");
        Loc.Register(Handoff, "让 agent 收尾这一步", "Have the agent finish this step");
        Loc.Register(HandoffSentPattern, "已交给 agent:{0}", "Handed to the agent: {0}");
        Loc.Register(HandoffQueuedPattern, "已排队,本回合结束后发送:{0}", "Queued for after this turn: {0}");
        Loc.Register(
            HandoffBlocked,
            "输入框里有还没发送的内容。先发送或清空它,再让 agent 收尾这一步。",
            "The composer holds unsent text. Send or clear it first, then hand the step to the agent.");
        Loc.Register(
            HandoffReadOnly,
            "这是只读会话,不能再给 agent 发消息。",
            "This session is read-only, so the agent cannot be sent a message.");
        Loc.Register(
            HandoffUnavailable,
            "agent 现在接不了这一步:会话正在交接、压缩,或在等别的东西。稍后再试。",
            "The agent cannot take this step right now: the session is handing over, compacting or waiting for something else. Try again in a moment.");

        Loc.Register(Copy, "复制 Markdown", "Copy Markdown");
        Loc.Register(Export, "导出 Markdown…", "Export Markdown…");
        Loc.Register(ExportTitle, "导出计划", "Export the plan");
        Loc.Register(ExportFileType, "Markdown 文件", "Markdown file");
        Loc.Register(ExportDonePattern, "已导出到 {0}", "Exported to {0}");
        Loc.Register(ExportFailedPattern, "导出失败:{0}", "Export failed: {0}");
        Loc.Register(OpenPullRequest, "从计划开 PR…", "Open a pull request from this plan…");
        Loc.Register(
            OpenPullRequestHint,
            "这条分支要已推送、且还没有 PR,才能从这里开 PR。",
            "The branch has to be pushed and have no pull request yet, so one can be opened from here.");

        Loc.Register(RevisionsTitle, "修订历史", "Revision history");
        Loc.Register(RevisionsEmpty, "还没有被替换过的版本。", "No version has been replaced yet.");
        Loc.Register(RevisionsCountPattern, "{0} 个版本", "{0} versions");
        Loc.Register(RevisionView, "查看这个版本", "View this version");

        Loc.Register(Loading, "正在读取计划…", "Reading the plan…");
        Loc.Register(FailedPattern, "读不到这个计划:{0}", "Could not read this plan: {0}");
    }

    [ModuleInitializer]
    internal static void Initialize() => Register();
}
