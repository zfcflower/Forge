using System.Runtime.CompilerServices;

namespace Forge.App.Localization;

/// <summary>
/// The handover table (lifecycle 6.2): the "take over?" question, the busy check, the handover dialog and every
/// failure the sequence can report.
/// <para>Keys: <c>lifecycle.handover.*</c>. Each key has a Chinese and an English text; parameterized texts are typed
/// accessors formatted through <see cref="Loc.Format"/>, parameterless ones properties that read <see cref="Loc.Instance"/>.</para>
/// </summary>
internal static class HandoverStrings
{
    // ---------------------------------------------------------------- entry
    public const string TitleKey = "lifecycle.handover.title";
    public const string OccupiedQuestionKey = "lifecycle.handover.occupiedQuestion";
    public const string TakeOverKey = "lifecycle.handover.takeOver";
    public const string UnavailableKey = "lifecycle.handover.unavailable";

    // ---------------------------------------------------------------- busy check
    public const string BusyMessageKey = "lifecycle.handover.busy.message";
    public const string WaitKey = "lifecycle.handover.busy.wait";
    public const string BusyTurnKey = "lifecycle.handover.busy.turn";
    public const string BusySubAgentsKey = "lifecycle.handover.busy.subAgents";
    public const string BusyQueuedKey = "lifecycle.handover.busy.queued";
    public const string BusyInterjectionsKey = "lifecycle.handover.busy.interjections";
    public const string BusyHandingOverKey = "lifecycle.handover.busy.handingOver";
    public const string BusyWorkspaceOperatingKey = "lifecycle.handover.busy.workspaceOperating";
    public const string BusyMergeConflictKey = "lifecycle.handover.busy.mergeConflict";

    // ---------------------------------------------------------------- dialog
    public const string IntroKey = "lifecycle.handover.intro";
    public const string UncommittedKeyBase = "lifecycle.handover.uncommitted";
    public const string CommitAsOldKey = "lifecycle.handover.commitAsOld";
    public const string CarryToNewKey = "lifecycle.handover.carryToNew";
    public const string RootCheckoutWarningKey = "lifecycle.handover.rootCheckoutWarning";
    public const string ModeLabelKey = "lifecycle.handover.mode.label";
    public const string ModeFullKey = "lifecycle.handover.mode.full";
    public const string ModeSummaryKey = "lifecycle.handover.mode.summary";
    public const string ModeBlankKey = "lifecycle.handover.mode.blank";
    public const string RunningKey = "lifecycle.handover.running";
    public const string StopKey = "lifecycle.handover.stop";

    // ---------------------------------------------------------------- results
    public const string CancelledKey = "lifecycle.handover.cancelled";
    public const string StatusFailedKey = "lifecycle.handover.statusFailed";
    public const string CommitFailedKey = "lifecycle.handover.commitFailed";
    public const string CommitLandedKey = "lifecycle.handover.commitLanded";
    public const string ContextFailedKey = "lifecycle.handover.contextFailed";
    public const string SummaryFailedKey = "lifecycle.handover.summaryFailed";
    public const string SummaryCancelledKey = "lifecycle.handover.summaryCancelled";
    public const string SwitchFailedKey = "lifecycle.handover.switchFailed";
    public const string SwitchFailedAfterCommitKey = "lifecycle.handover.switchFailedAfterCommit";
    public const string ReviewCopyFailedKey = "lifecycle.handover.reviewCopyFailed";

    public static string Title => Loc.Instance[TitleKey];

    public static string OccupiedQuestion(string holderTitle) => Loc.Instance.Format(OccupiedQuestionKey, holderTitle);

    public static string TakeOver => Loc.Instance[TakeOverKey];

    public static string Unavailable => Loc.Instance[UnavailableKey];

    public static string BusyMessage(string title, IReadOnlyList<string> reasons) =>
        Loc.Instance.Format(BusyMessageKey, title, string.Join(Environment.NewLine, reasons.Select(static reason => "• " + reason)));

    public static string Wait => Loc.Instance[WaitKey];

    public static string BusyTurn => Loc.Instance[BusyTurnKey];

    public static string BusySubAgents => Loc.Instance[BusySubAgentsKey];

    public static string BusyQueued => Loc.Instance[BusyQueuedKey];

    public static string BusyInterjections => Loc.Instance[BusyInterjectionsKey];

    public static string BusyHandingOver => Loc.Instance[BusyHandingOverKey];

    public static string BusyWorkspaceOperating => Loc.Instance[BusyWorkspaceOperatingKey];

    public static string BusyMergeConflict => Loc.Instance[BusyMergeConflictKey];

    public static string Intro(string oldTitle) => Loc.Instance.Format(IntroKey, oldTitle);

    public static string Uncommitted(int count) =>
        Loc.Instance.Format(LocText.PluralKey(UncommittedKeyBase, count), TextFormat.Number(count));

    public static string CommitAsOld(string oldTitle) => Loc.Instance.Format(CommitAsOldKey, oldTitle);

    public static string CarryToNew => Loc.Instance[CarryToNewKey];

    public static string RootCheckoutWarning => Loc.Instance[RootCheckoutWarningKey];

    public static string ModeLabel => Loc.Instance[ModeLabelKey];

    public static string ModeFull => Loc.Instance[ModeFullKey];

    public static string ModeSummary => Loc.Instance[ModeSummaryKey];

    public static string ModeBlank => Loc.Instance[ModeBlankKey];

    public static string Running => Loc.Instance[RunningKey];

    public static string Stop => Loc.Instance[StopKey];

    public static string Cancelled => Loc.Instance[CancelledKey];

    public static string StatusFailed(string reason) => Loc.Instance.Format(StatusFailedKey, reason);

    public static string CommitFailed(string reason) => Loc.Instance.Format(CommitFailedKey, reason);

    public static string CommitLanded(string sha) => Loc.Instance.Format(CommitLandedKey, ShortSha(sha));

    public static string ContextFailed(string reason) => Loc.Instance.Format(ContextFailedKey, reason);

    public static string SummaryFailed(string reason) => Loc.Instance.Format(SummaryFailedKey, reason);

    public static string SummaryCancelled => Loc.Instance[SummaryCancelledKey];

    public static string SwitchFailed(string reason) => Loc.Instance.Format(SwitchFailedKey, reason);

    public static string SwitchFailedAfterCommit(string sha, string reason) =>
        Loc.Instance.Format(SwitchFailedAfterCommitKey, ShortSha(sha), reason);

    public static string ReviewCopyFailed(string reason) => Loc.Instance.Format(ReviewCopyFailedKey, reason);

    private static string ShortSha(string sha) => sha.Length > 10 ? sha[..10] : sha;

    [ModuleInitializer]
    internal static void Initialize() => Register();

    /// <summary>Idempotent: registering the same key with the same texts twice is a no-op.</summary>
    internal static void Register()
    {
        Loc.Register(TitleKey, "接手", "Take over");
        Loc.Register(OccupiedQuestionKey, "该工作区已有活动会话“{0}”。是否接手?", "The workspace already has an active session, \"{0}\". Take it over?");
        Loc.Register(TakeOverKey, "接手…", "Take over…");
        Loc.Register(UnavailableKey, "此窗口无法显示接手对话框,未接手。", "This window cannot show the handover dialog; nothing was taken over.");

        Loc.Register(BusyMessageKey, "“{0}”尚未空闲,暂不能接手:\n{1}\n\n等它空闲后再试。", "\"{0}\" is not idle yet, so it cannot be taken over:\n{1}\n\nTry again once it is idle.");
        Loc.Register(WaitKey, "等待", "Wait");
        Loc.Register(BusyTurnKey, "有回合正在运行", "A turn is running");
        Loc.Register(BusySubAgentsKey, "有子 Agent 在运行或排队", "Sub-agents are running or waiting");
        Loc.Register(BusyQueuedKey, "有排队待发的消息", "Messages are queued");
        Loc.Register(BusyInterjectionsKey, "有尚未送达的插话", "Interjections are not delivered yet");
        Loc.Register(BusyHandingOverKey, "正在交接", "A handover is under way");
        Loc.Register(BusyWorkspaceOperatingKey, "工作区操作进行中", "A workspace operation is under way");
        Loc.Register(BusyMergeConflictKey, "合并冲突待处理", "A merge conflict is pending");

        Loc.Register(IntroKey, "“{0}”将进入历史,由新的会话接手此工作区。", "\"{0}\" moves to history and the workspace is taken over.");
        Loc.Register(UncommittedKeyBase + ".one", "工作区有 {0} 个未提交的改动(含未跟踪文件):", "The workspace has {0} uncommitted change (untracked files included):");
        Loc.Register(UncommittedKeyBase + ".other", "工作区有 {0} 个未提交的改动(含未跟踪文件):", "The workspace has {0} uncommitted changes (untracked files included):");
        Loc.Register(CommitAsOldKey, "以“{0}”的名义提交(交接提交)", "Commit them in the name of \"{0}\" (handover commit)");
        Loc.Register(CarryToNewKey, "划归接手的会话:不提交,它的第一次步骤提交会把它们收进去", "Hand them to the session taking over: no commit; its first step commit picks them up");
        Loc.Register(RootCheckoutWarningKey, "这是仓库根检出:你在此手工做的改动也会一起收进去。", "This is the repository's own checkout: your manual edits here are picked up too.");
        Loc.Register(ModeLabelKey, "新会话的上下文:", "Context of the new session:");
        Loc.Register(ModeFullKey, "完整继承(接续旧会话的上下文)", "Full inheritance (continue the old session's context)");
        Loc.Register(ModeSummaryKey, "交接摘要", "Handover summary");
        Loc.Register(ModeBlankKey, "空白", "Blank");
        Loc.Register(RunningKey, "正在接手…", "Taking over…");
        Loc.Register(StopKey, "停止", "Stop");

        Loc.Register(CancelledKey, "已取消,未接手。", "Cancelled; nothing was taken over.");
        Loc.Register(StatusFailedKey, "无法读取工作区改动,未接手:{0}", "Could not read the workspace changes; nothing was taken over: {0}");
        Loc.Register(CommitFailedKey, "交接提交失败,未接手:{0}", "The handover commit failed; nothing was taken over: {0}");
        Loc.Register(CommitLandedKey, "交接提交已落盘({0})。", "The handover commit is in ({0}).");
        Loc.Register(ContextFailedKey, "无法准备新会话的上下文,未接手:{0}", "Could not prepare the new session's context; nothing was taken over: {0}");
        Loc.Register(SummaryFailedKey, "交接摘要生成失败:{0}。可重试或改选空白。", "The handover summary could not be generated: {0}. Retry, or choose Blank.");
        Loc.Register(SummaryCancelledKey, "已停止生成交接摘要。可重试或改选空白。", "The handover summary was stopped. Retry, or choose Blank.");
        Loc.Register(SwitchFailedKey, "会话未切换:{0}", "The session was not switched: {0}");
        Loc.Register(SwitchFailedAfterCommitKey, "交接提交已落盘({0}),会话未切换:{1}", "The handover commit is in ({0}), but the session was not switched: {1}");
        Loc.Register(ReviewCopyFailedKey, "会话已切换,评审标记未复制:{0}", "The session was switched, but the review marks were not copied: {0}");
    }
}
