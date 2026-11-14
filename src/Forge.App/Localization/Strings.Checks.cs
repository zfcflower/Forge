using System.Runtime.CompilerServices;

namespace Forge.App.Localization;

/// <summary>
/// The pull-request checks panel's own string table (wave W5): the panel chrome, one row per check, the verdict of the
/// latest reading, the failure log of one check and the hand-off to the agent. Keys are namespaced <c>checks.*</c> and
/// every text is registered from a module initializer, so no shared table has to be edited by another feature.
/// <para>Texts that carry a value are format strings read with <see cref="Loc.Format"/>; the placeholders are the
/// arguments named in each key's comment. The agent-facing text of a hand-off is English and lives outside this table
/// (<c>Git.PrChecksFixMessage</c>): it is model input, not UI.</para>
/// </summary>
internal static class ChecksStrings
{
    // ---------------------------------------------------------------- panel chrome

    /// <summary>The panel title.</summary>
    public const string Title = "checks.title";

    /// <summary>The pull request the panel shows: <c>#{0}</c>.</summary>
    public const string Number = "checks.number";

    public const string ButtonRefresh = "checks.button.refresh";
    public const string ButtonClose = "checks.button.close";

    /// <summary>Takes a draft pull request out of draft (<c>gh pr ready</c>).</summary>
    public const string ButtonMarkReady = "checks.button.markReady";

    /// <summary>Hands the failing checks, with their log tails, to the open session's agent.</summary>
    public const string ButtonFix = "checks.button.fix";

    public const string ButtonOpenPullRequest = "checks.button.openPullRequest";

    /// <summary>Reads the failed job log of the row into the row itself.</summary>
    public const string ButtonLog = "checks.button.log";
    public const string ButtonHideLog = "checks.button.hideLog";

    /// <summary>Opens one check's own page on the platform (its row's "details" button).</summary>
    public const string ButtonDetails = "checks.button.details";

    // ---------------------------------------------------------------- verdict of the latest reading

    /// <summary>Checks still running: nothing is decided yet.</summary>
    public const string VerdictWaiting = "checks.verdict.waiting";

    public const string VerdictReady = "checks.verdict.ready";
    public const string VerdictReadyWithHooks = "checks.verdict.readyWithHooks";
    public const string VerdictFailed = "checks.verdict.failed";
    public const string VerdictBehind = "checks.verdict.behind";
    public const string VerdictDirty = "checks.verdict.dirty";
    public const string VerdictBlocked = "checks.verdict.blocked";
    public const string VerdictUnstable = "checks.verdict.unstable";
    public const string VerdictDraft = "checks.verdict.draft";
    public const string VerdictNoCi = "checks.verdict.noCi";
    public const string VerdictNotOpen = "checks.verdict.notOpen";
    public const string VerdictUnknown = "checks.verdict.unknown";

    // ---------------------------------------------------------------- counts

    /// <summary>Checks that passed, skipped and neutral counted: <c>{0}</c>.</summary>
    public const string CountPassed = "checks.count.passed";

    /// <summary>Checks that failed or were cancelled: <c>{0}</c>.</summary>
    public const string CountFailed = "checks.count.failed";

    /// <summary>Checks still running: <c>{0}</c>.</summary>
    public const string CountPending = "checks.count.pending";

    /// <summary>Checks the platform reported as skipped, neutral or stale: <c>{0}</c>.</summary>
    public const string CountSkipped = "checks.count.skipped";

    /// <summary>The separator between two counts (<c>·</c>).</summary>
    public const string CountSeparator = "checks.count.separator";

    // ---------------------------------------------------------------- one row

    public const string BucketPass = "checks.bucket.pass";
    public const string BucketFail = "checks.bucket.fail";
    public const string BucketPending = "checks.bucket.pending";
    public const string BucketSkipped = "checks.bucket.skipped";
    public const string BucketCancelled = "checks.bucket.cancelled";

    /// <summary>A state this build cannot classify, so it never counts as passed.</summary>
    public const string BucketUnknown = "checks.bucket.unknown";

    /// <summary>The merge state as the platform texted it: <c>{0}</c>.</summary>
    public const string DetailMergeState = "checks.detail.mergeState";

    /// <summary>The review decision as the platform texted it: <c>{0}</c>.</summary>
    public const string DetailReview = "checks.detail.review";

    // ---------------------------------------------------------------- empty, error and read state

    public const string EmptyTitle = "checks.empty.title";
    public const string EmptyBody = "checks.empty.body";

    /// <summary>A failed read: <c>{0}</c> is the platform's own message.</summary>
    public const string ReadFailed = "checks.read.failed";

    /// <summary>When the reading on screen was taken: <c>{0}</c> is a wall-clock time.</summary>
    public const string ReadAt = "checks.read.at";

    /// <summary>A read is in flight.</summary>
    public const string ReadWaiting = "checks.read.waiting";

    /// <summary>The panel stopped polling because the configured maximum wait elapsed: <c>{0}</c> is the minutes.</summary>
    public const string WaitTimeout = "checks.read.timeout";

    // ---------------------------------------------------------------- failure log of one check

    public const string LogLoading = "checks.log.loading";
    public const string LogEmpty = "checks.log.empty";

    /// <summary>The log could not be read: <c>{0}</c> is the platform's message.</summary>
    public const string LogFailed = "checks.log.failed";

    /// <summary>The log was cut: <c>{0}</c> is the number of lines kept.</summary>
    public const string LogTailKept = "checks.log.tailKept";

    /// <summary>This check has no workflow job behind it (an external status context), so there is no log to read.</summary>
    public const string LogNone = "checks.log.none";

    // ---------------------------------------------------------------- handing the failures to the agent

    /// <summary>The turn ran: the agent received the failures.</summary>
    public const string FixSent = "checks.fix.sent";

    /// <summary>The transcript notice of a hand-off turn (the origin line above the message the agent gets).</summary>
    public const string HandOffOrigin = "checks.fix.origin";

    /// <summary>The session was busy, so the prompt waits in its composer.</summary>
    public const string FixQueued = "checks.fix.queued";

    public const string FixNoSession = "checks.fix.noSession";
    public const string FixNothing = "checks.fix.nothing";

    /// <summary>Marking the pull request ready succeeded.</summary>
    public const string MarkReadyDone = "checks.markReady.done";

    /// <summary>Marking it ready failed: <c>{0}</c> is the platform's message.</summary>
    public const string MarkReadyFailed = "checks.markReady.failed";

    /// <summary>A hand-off or mark-ready that threw instead of answering: <c>{0}</c> is the reason.</summary>
    public const string ActionFailed = "checks.action.failed";

    /// <summary>Registers this table before any code of the assembly runs (module initializer, AOT safe).</summary>
    [ModuleInitializer]
    internal static void Initialize() => Register();

    /// <summary>Idempotent: registering the same key with the same texts twice is a no-op.</summary>
    internal static void Register()
    {
        Loc.Register(Title, "PR 检查", "Pull request checks");
        Loc.Register(Number, "#{0}", "#{0}");
        Loc.Register(ButtonRefresh, "刷新", "Refresh");
        Loc.Register(ButtonClose, "关闭", "Close");
        Loc.Register(ButtonMarkReady, "标记可评审", "Mark ready");
        Loc.Register(ButtonFix, "让 agent 修", "Have the agent fix it");
        Loc.Register(ButtonOpenPullRequest, "在浏览器打开", "Open in browser");
        Loc.Register(ButtonLog, "失败日志", "Failure log");
        Loc.Register(ButtonHideLog, "收起日志", "Hide log");
        Loc.Register(ButtonDetails, "详情", "Details");

        Loc.Register(VerdictWaiting, "等待检查结束", "Waiting for the checks");
        Loc.Register(VerdictReady, "全部通过,可以合并", "Everything passed; it can be merged");
        Loc.Register(
            VerdictReadyWithHooks,
            "全部通过;合并时仍会执行 pre-receive 钩子,可能被拒绝",
            "Everything passed; pre-receive hooks still run at merge and can reject it");
        Loc.Register(VerdictFailed, "有检查失败", "Some checks failed");
        Loc.Register(VerdictBehind, "基础分支已前进,需要先同步", "The base branch moved ahead; sync first");
        Loc.Register(VerdictDirty, "与基础分支冲突", "It conflicts with the base branch");
        Loc.Register(VerdictBlocked, "有要求未满足(例如必需的评审)", "A requirement is unmet (a required review, for example)");
        Loc.Register(VerdictUnstable, "有非必需的状态未通过", "A non-required status is not passing");
        Loc.Register(VerdictDraft, "这是草稿 PR,标记可评审后才会通知评审人", "This is a draft; mark it ready to notify the reviewers");
        Loc.Register(VerdictNoCi, "这个 PR 没有检查", "This pull request has no checks");
        Loc.Register(VerdictNotOpen, "PR 已合并或关闭", "The pull request was merged or closed");
        Loc.Register(VerdictUnknown, "状态未知", "The state is not determined");

        Loc.Register(CountPassed, "通过 {0}", "passed {0}");
        Loc.Register(CountFailed, "失败 {0}", "failed {0}");
        Loc.Register(CountPending, "运行中 {0}", "running {0}");
        Loc.Register(CountSkipped, "跳过 {0}", "skipped {0}");
        Loc.Register(CountSeparator, " · ", " · ");

        Loc.Register(BucketPass, "通过", "pass");
        Loc.Register(BucketFail, "失败", "failed");
        Loc.Register(BucketPending, "运行中", "running");
        Loc.Register(BucketSkipped, "跳过", "skipped");
        Loc.Register(BucketCancelled, "已取消", "cancelled");
        Loc.Register(BucketUnknown, "未知", "unknown");
        Loc.Register(DetailMergeState, "合并状态 {0}", "merge state {0}");
        Loc.Register(DetailReview, "评审 {0}", "review {0}");

        Loc.Register(EmptyTitle, "这个 PR 还没有检查", "This pull request has no checks yet");
        Loc.Register(
            EmptyBody,
            "新的推送可能还没有注册工作流;面板每次刷新都会重新读取。",
            "A fresh head may not have registered its workflows yet; every refresh reads again.");
        Loc.Register(ReadFailed, "读取失败:{0}", "Read failed: {0}");
        Loc.Register(ReadAt, "读取于 {0}", "read at {0}");
        Loc.Register(ReadWaiting, "正在读取…", "Reading…");
        Loc.Register(
            WaitTimeout,
            "已等待 {0} 分钟仍未结束,按「刷新」继续等待。",
            "Still running after {0} minutes; press Refresh to keep waiting.");

        Loc.Register(LogLoading, "正在读取日志…", "Reading the log…");
        Loc.Register(LogEmpty, "日志为空", "The log is empty");
        Loc.Register(LogFailed, "无法读取日志:{0}", "Could not read the log: {0}");
        Loc.Register(LogTailKept, "日志很长,只显示末尾部分", "The log is long; only its tail is shown");
        Loc.Register(LogNone, "这项检查没有可读取的作业日志", "This check has no job log to read");

        Loc.Register(FixSent, "已交给当前会话的 agent", "Handed to the agent of the open session");
        Loc.Register(HandOffOrigin, "PR 检查", "Pull request checks");
        Loc.Register(FixQueued, "会话正忙,提示已放进输入框", "The session is busy; the prompt is in its composer");
        Loc.Register(FixNoSession, "没有可交付的会话", "There is no open session to hand it to");
        Loc.Register(FixNothing, "没有失败的检查", "No check failed");
        Loc.Register(MarkReadyDone, "已标记为可评审", "Marked ready for review");
        Loc.Register(MarkReadyFailed, "标记失败:{0}", "Could not mark it ready: {0}");
        Loc.Register(ActionFailed, "操作失败:{0}", "It did not work: {0}");
    }
}
