using System.Runtime.CompilerServices;

namespace Forge.App.Localization;

/// <summary>
/// The git feature's own string table (forge-app spec, "偏好、文案与设置分区"): the inspector's Git tab, its branch and
/// remote operations with their recovery hints, and the <c>git</c> settings rows. Keys are namespaced <c>git.*</c> and
/// every text is registered from a module initializer, so no shared table (and no parallel feature) has to be edited.
/// <para>Texts that carry a value are format strings read with <see cref="Loc.Format"/>; the placeholders are the
/// arguments named in each key's comment.</para>
/// </summary>
internal static class GitStrings
{
    // ---------------------------------------------------------------- tab and settings section

    public const string TabTitle = "git.tab.title";

    public const string SettingsSectionTitle = "git.settings.section";
    public const string SettingsPullStrategy = "git.settings.pullStrategy";
    public const string SettingsPullStrategyFastForward = "git.settings.pullStrategy.ffOnly";
    public const string SettingsPullStrategyRebase = "git.settings.pullStrategy.rebase";
    public const string SettingsPullStrategyMerge = "git.settings.pullStrategy.merge";
    public const string SettingsNetworkTimeout = "git.settings.networkTimeout";
    public const string SettingsNetworkTimeoutHint = "git.settings.networkTimeoutHint";
    public const string SettingsStepCommits = "git.settings.stepCommits";
    public const string SettingsStepCommitsHint = "git.settings.stepCommitsHint";
    public const string SettingsChecksPoll = "git.settings.checksPoll";
    public const string SettingsChecksMaxWait = "git.settings.checksMaxWait";
    public const string SettingsChecksHint = "git.settings.checksHint";
    public const string SettingsNotifications = "git.settings.notifications";
    public const string SettingsNotificationsHint = "git.settings.notificationsHint";
    public const string SettingsGhPath = "git.settings.ghPath";
    public const string SettingsGhPathHint = "git.settings.ghPathHint";

    // ---------------------------------------------------------------- labels of the panel

    public const string LabelBranch = "git.label.branch";
    public const string LabelUpstream = "git.label.upstream";
    public const string LabelAheadBehind = "git.label.aheadBehind";
    public const string LabelRemotes = "git.label.remotes";
    public const string LabelBranches = "git.label.branches";
    public const string LabelNewBranch = "git.label.newBranch";
    public const string LabelConflicts = "git.label.conflicts";
    public const string LabelResult = "git.label.result";
    public const string LabelPullRequest = "git.label.pullRequest";
    public const string LabelChecks = "git.label.checks";
    public const string LabelReview = "git.label.review";

    // ---------------------------------------------------------------- commands of the panel

    public const string ButtonRefresh = "git.button.refresh";
    public const string ButtonFetch = "git.button.fetch";
    public const string ButtonPull = "git.button.pull";
    public const string ButtonPush = "git.button.push";
    public const string ButtonCreateBranch = "git.button.createBranch";
    public const string ButtonSwitchBranch = "git.button.switchBranch";
    public const string ButtonSetUpstream = "git.button.setUpstream";
    public const string ButtonDeleteBranch = "git.button.deleteBranch";
    public const string ButtonAbort = "git.button.abort";
    public const string ButtonContinue = "git.button.continue";
    public const string ButtonWaitChecks = "git.button.waitChecks";
    public const string ButtonCancelWait = "git.button.cancelWait";

    // ---------------------------------------------------------------- state texts

    public const string NoRepository = "git.state.noRepository";
    public const string Busy = "git.state.busy";

    /// <summary>Format: <c>{0}</c> is the short commit hash.</summary>
    public const string DetachedHead = "git.state.detached";

    public const string NoUpstream = "git.state.noUpstream";

    /// <summary>Format: <c>{0}</c> is the upstream name.</summary>
    public const string UpstreamGone = "git.state.upstreamGone";

    /// <summary>Format: <c>{0}</c> is commits ahead, <c>{1}</c> commits behind.</summary>
    public const string AheadBehind = "git.state.aheadBehind";

    public const string OperationRebase = "git.operation.rebase";
    public const string OperationMerge = "git.operation.merge";
    public const string OperationCherryPick = "git.operation.cherryPick";
    public const string OperationRevert = "git.operation.revert";

    // ---------------------------------------------------------------- failures raised by the panel itself

    public const string ErrorNoRepository = "git.error.noRepository";
    public const string ErrorBranchNameRequired = "git.error.branchNameRequired";
    public const string ErrorSelectBranch = "git.error.selectBranch";

    /// <summary>Format: <c>{0}</c> is the current branch name.</summary>
    public const string ErrorCannotDeleteCurrent = "git.error.cannotDeleteCurrent";

    public const string ErrorUpstreamRequired = "git.error.upstreamRequired";

    /// <summary>Format: <c>{0}</c> is the exception message.</summary>
    public const string ErrorGitFailed = "git.error.gitFailed";

    /// <summary>Format: <c>{0}</c> is the branch name.</summary>
    public const string ResultBranchDeleted = "git.result.branchDeleted";

    // ---------------------------------------------------------------- confirmation of a destructive action

    public const string ConfirmButton = "git.confirm.button";

    public const string ConfirmDeleteTitle = "git.confirm.deleteBranch.title";

    /// <summary>Format: <c>{0}</c> is the branch name.</summary>
    public const string ConfirmDeleteMessage = "git.confirm.deleteBranch.message";

    // ---------------------------------------------------------------- new session: the caller-chosen branch name

    /// <summary>Title of the prompt that asks for the new session's branch name.</summary>
    public const string WorktreeBranchPromptTitle = "git.worktree.branchPromptTitle";

    /// <summary>Format: <c>{0}</c> is the branch name; the reason (git's branch naming rules) is part of the text.</summary>
    public const string WorktreeBranchInvalid = "git.worktree.branchInvalid";

    /// <summary>Format: <c>{0}</c> is the branch name, <c>{1}</c> the reason.</summary>
    public const string WorktreeBranchUnavailable = "git.worktree.branchUnavailable";

    // ---------------------------------------------------------------- step commits shown on the TODO rows

    /// <summary>The marker on a done TODO row whose step commit failed; the reason is the row's tooltip and second line.</summary>
    public const string TodoCommitFailed = "git.todo.commitFailed";

    /// <summary>The retry button of a failed step commit (visible text, tooltip and accessible name).</summary>
    public const string TodoCommitRetry = "git.todo.commitRetry";

    // ---------------------------------------------------------------- the branch's pull request

    /// <summary>Format: <c>{0}</c> is the pull request number, <c>{1}</c> its title.</summary>
    public const string PullRequestHeader = "git.pr.header";

    public const string PullRequestStateOpen = "git.pr.state.open";
    public const string PullRequestStateDraft = "git.pr.state.draft";
    public const string PullRequestStateClosed = "git.pr.state.closed";
    public const string PullRequestStateMerged = "git.pr.state.merged";
    public const string PullRequestNone = "git.pr.none";
    public const string PullRequestLoading = "git.pr.loading";

    /// <summary>"Install and sign in to gh"; the platform's own detail is shown under it.</summary>
    public const string PullRequestGhNotReady = "git.pr.ghNotReady";

    /// <summary>Format: <c>{0}</c> is the reason the platform gave.</summary>
    public const string PullRequestReadFailed = "git.pr.readFailed";

    // ---------------------------------------------------------------- waiting for the checks

    public const string WatchWaiting = "git.watch.waiting";

    /// <summary>Format: <c>{0}</c> checks passed, <c>{1}</c> failed or cancelled, <c>{2}</c> still pending.</summary>
    public const string WatchProgress = "git.watch.progress";

    public const string WatchNoChecksYet = "git.watch.noChecksYet";

    /// <summary>Format: <c>{0}</c> is the platform's raw merge state text (CLEAN, BEHIND, ...).</summary>
    public const string WatchMergeState = "git.watch.mergeState";

    public const string WatchCancelled = "git.watch.cancelled";

    public const string OutcomeReady = "git.watch.outcome.ready";
    public const string OutcomeReadyWithHooks = "git.watch.outcome.readyWithHooks";

    /// <summary>Format: <c>{0}</c> is the number of failed checks, <c>{1}</c> their names.</summary>
    public const string OutcomeChecksFailed = "git.watch.outcome.checksFailed";

    public const string OutcomeBehind = "git.watch.outcome.behind";
    public const string OutcomeDirty = "git.watch.outcome.dirty";
    public const string OutcomeBlocked = "git.watch.outcome.blocked";
    public const string OutcomeUnstable = "git.watch.outcome.unstable";
    public const string OutcomeDraft = "git.watch.outcome.draft";
    public const string OutcomeNoCi = "git.watch.outcome.noCi";
    public const string OutcomeNotOpen = "git.watch.outcome.notOpen";

    /// <summary>Format: <c>{0}</c> is how many minutes the wait lasted.</summary>
    public const string OutcomeTimedOut = "git.watch.outcome.timedOut";

    /// <summary>Format: <c>{0}</c> is the reason the platform gave.</summary>
    public const string OutcomeFailed = "git.watch.outcome.failed";

    /// <summary>Title of the system notification; format: <c>{0}</c> is the pull request number.</summary>
    public const string NotifyTitle = "git.notify.title";

    // ---------------------------------------------------------------- status bar indicator of the checks

    public const string ChecksRunning = "git.checks.running";
    public const string ChecksPassed = "git.checks.passed";
    public const string ChecksFailed = "git.checks.failed";
    public const string ChecksAttention = "git.checks.attention";
    public const string ChecksNone = "git.checks.none";

    // ---------------------------------------------------------------- review threads of the pull request

    /// <summary>Format: <c>{0}</c> is the number of threads, <c>{1}</c> how many of them are unresolved.</summary>
    public const string ReviewSummary = "git.review.summary";

    public const string ReviewLoading = "git.review.loading";
    public const string ReviewEmpty = "git.review.empty";

    /// <summary>Format: <c>{0}</c> is the reason the platform gave.</summary>
    public const string ReviewReadFailed = "git.review.readFailed";

    public const string ReviewResolved = "git.review.resolved";
    public const string ReviewUnresolved = "git.review.unresolved";
    public const string ReviewOutdated = "git.review.outdated";

    /// <summary>Format: <c>{0}</c> is the number of replies after the first comment.</summary>
    public const string ReviewReplies = "git.review.replies";

    /// <summary>A thread that is anchored to no file (a comment on the pull request as a whole).</summary>
    public const string ReviewNoFile = "git.review.noFile";

    public const string ReviewUnknownAuthor = "git.review.unknownAuthor";

    /// <summary>Format: <c>{0}</c> is the commit the agent cited.</summary>
    public const string ReviewVerdictApply = "git.review.verdict.apply";

    public const string ReviewVerdictPushBack = "git.review.verdict.pushBack";
    public const string ReviewVerdictClarify = "git.review.verdict.clarify";

    // ---------------------------------------------------------------- recovery hints, one per failure kind

    public const string HintAuthentication = "git.hint.authentication";
    public const string HintNonFastForward = "git.hint.nonFastForward";
    public const string HintConflict = "git.hint.conflict";
    public const string HintRemoteNotFound = "git.hint.remoteNotFound";
    public const string HintNoUpstream = "git.hint.noUpstream";
    public const string HintDirtyWorkTree = "git.hint.dirtyWorkTree";
    public const string HintNetwork = "git.hint.network";
    public const string HintTimedOut = "git.hint.timedOut";
    public const string HintOther = "git.hint.unclassified";

    /// <summary>Registers this table before any code of the assembly runs (module initializer, AOT safe).</summary>
    [ModuleInitializer]
    internal static void Initialize() => Register();

    /// <summary>Idempotent: registering the same key with the same texts twice is a no-op.</summary>
    internal static void Register()
    {
        Loc.Register(TabTitle, "Git", "Git");

        Loc.Register(SettingsSectionTitle, "Git", "Git");
        Loc.Register(SettingsPullStrategy, "拉取方式", "Pull strategy");
        Loc.Register(SettingsPullStrategyFastForward, "仅快进", "Fast-forward only");
        Loc.Register(SettingsPullStrategyRebase, "变基", "Rebase");
        Loc.Register(SettingsPullStrategyMerge, "合并", "Merge");
        Loc.Register(SettingsNetworkTimeout, "网络超时（秒）", "Network timeout (seconds)");
        Loc.Register(
            SettingsNetworkTimeoutHint,
            "fetch、pull 与 push 的最长等待时间，10–600 秒；改动对下一次操作生效。",
            "Longest wait for fetch, pull and push, 10–600 seconds; a change applies to the next operation.");
        Loc.Register(SettingsStepCommits, "步骤提交", "Step commits");
        Loc.Register(
            SettingsStepCommitsHint,
            "TODO 条目标记为完成时，把工作树里所有未提交的改动提交到会话分支；提交说明取自条目的提交标题与摘要。关闭后不再自动提交。",
            "When a TODO item is marked done, commit every uncommitted change in the worktree to the session branch; the message comes from the item's commit subject and summary. Turn off to stop committing automatically.");

        Loc.Register(SettingsChecksPoll, "检查轮询间隔（秒）", "Checks poll interval (seconds)");
        Loc.Register(SettingsChecksMaxWait, "检查最长等待（分钟）", "Checks maximum wait (minutes)");
        Loc.Register(
            SettingsChecksHint,
            "“等待检查”每隔多少秒向 GitHub 查询一次（10–300 秒），最长等待多少分钟（1–180 分钟）；改动对下一次等待生效。",
            "How often waiting for checks asks GitHub (10–300 seconds) and the longest it keeps waiting (1–180 minutes); a change applies to the next wait.");
        Loc.Register(SettingsNotifications, "系统通知", "System notifications");
        Loc.Register(
            SettingsNotificationsHint,
            "等待检查结束时发出一条系统通知。Windows 上通知的发送方显示为 Windows PowerShell（未打包的程序没有自己的应用标识）。",
            "Raise one system notification when waiting for checks ends. On Windows the sender shows as Windows PowerShell (an unpackaged program has no application identity of its own).");
        Loc.Register(SettingsGhPath, "gh 可执行文件", "gh executable");
        Loc.Register(
            SettingsGhPathHint,
            "GitHub CLI 的完整路径；留空则使用 PATH 中的 gh。改动对下一次 gh 调用生效，无需重启。",
            "Full path of the GitHub CLI; leave empty to use gh from PATH. A change applies to the next gh call, no restart needed.");

        Loc.Register(LabelBranch, "分支", "Branch");
        Loc.Register(LabelUpstream, "上游", "Upstream");
        Loc.Register(LabelAheadBehind, "超前/落后", "Ahead/behind");
        Loc.Register(LabelRemotes, "远程", "Remotes");
        Loc.Register(LabelBranches, "本地分支", "Local branches");
        Loc.Register(LabelNewBranch, "新建分支或上游名称", "New branch or upstream name");
        Loc.Register(LabelConflicts, "冲突文件", "Conflicted files");
        Loc.Register(LabelResult, "最近结果", "Last result");
        Loc.Register(LabelPullRequest, "拉取请求", "Pull request");
        Loc.Register(LabelChecks, "检查", "Checks");
        Loc.Register(LabelReview, "评审", "Review");

        Loc.Register(ButtonRefresh, "刷新", "Refresh");
        Loc.Register(ButtonFetch, "抓取", "Fetch");
        Loc.Register(ButtonPull, "拉取", "Pull");
        Loc.Register(ButtonPush, "推送", "Push");
        Loc.Register(ButtonCreateBranch, "新建", "Create");
        Loc.Register(ButtonSwitchBranch, "切换", "Switch");
        Loc.Register(ButtonSetUpstream, "设置上游", "Set upstream");
        Loc.Register(ButtonDeleteBranch, "删除", "Delete");
        Loc.Register(ButtonAbort, "中止", "Abort");
        Loc.Register(ButtonContinue, "继续", "Continue");
        Loc.Register(ButtonWaitChecks, "等待检查", "Wait for checks");
        Loc.Register(ButtonCancelWait, "取消等待", "Cancel waiting");

        Loc.Register(NoRepository, "当前会话不在 Git 仓库中。", "This session is not in a Git repository.");
        Loc.Register(Busy, "处理中…", "Working…");
        Loc.Register(DetachedHead, "（分离 HEAD 于 {0}）", "(detached at {0})");
        Loc.Register(NoUpstream, "没有上游", "no upstream");
        Loc.Register(UpstreamGone, "{0}（远程已删除）", "{0} (gone)");
        Loc.Register(AheadBehind, "↑{0} ↓{1}", "↑{0} ↓{1}");
        Loc.Register(OperationRebase, "变基进行中", "Rebase in progress");
        Loc.Register(OperationMerge, "合并进行中", "Merge in progress");
        Loc.Register(OperationCherryPick, "拣选进行中", "Cherry-pick in progress");
        Loc.Register(OperationRevert, "还原进行中", "Revert in progress");

        Loc.Register(ErrorNoRepository, "没有可用的 Git 仓库。", "No Git repository is available.");
        Loc.Register(ErrorBranchNameRequired, "先输入分支名。", "Type a branch name first.");
        Loc.Register(ErrorSelectBranch, "先选择一个分支。", "Select a branch first.");
        Loc.Register(ErrorCannotDeleteCurrent, "不能删除当前分支 {0}。", "The current branch {0} cannot be deleted.");
        Loc.Register(ErrorUpstreamRequired, "输入上游名称，或先配置 origin 远程。", "Type an upstream name, or configure an origin remote first.");
        Loc.Register(ErrorGitFailed, "Git 调用失败：{0}", "Git failed: {0}");
        Loc.Register(ResultBranchDeleted, "已删除分支 {0}。", "Deleted branch {0}.");

        Loc.Register(ConfirmButton, "确定", "Confirm");
        Loc.Register(ConfirmDeleteTitle, "删除分支", "Delete branch");
        Loc.Register(
            ConfirmDeleteMessage,
            "确定删除本地分支 {0}？未合并的提交会丢失。",
            "Delete the local branch {0}? Unmerged commits are lost.");

        Loc.Register(WorktreeBranchPromptTitle, "新会话的分支名", "Branch name for the new session");
        Loc.Register(
            WorktreeBranchInvalid,
            "分支名 {0} 无效：不符合 Git 的分支命名规则（例如不能含空格、~、^、:、?、*、[ 或 ..）。",
            "The branch name {0} is invalid: it breaks Git's branch naming rules (for example no spaces, ~, ^, :, ?, *, [ or ..).");
        Loc.Register(
            WorktreeBranchUnavailable,
            "无法创建分支 {0}：{1}",
            "The branch {0} could not be created: {1}");

        Loc.Register(TodoCommitFailed, "提交失败", "Commit failed");
        Loc.Register(TodoCommitRetry, "重试提交", "Retry commit");

        Loc.Register(PullRequestHeader, "#{0} {1}", "#{0} {1}");
        Loc.Register(PullRequestStateOpen, "打开", "Open");
        Loc.Register(PullRequestStateDraft, "草稿", "Draft");
        Loc.Register(PullRequestStateClosed, "已关闭", "Closed");
        Loc.Register(PullRequestStateMerged, "已合并", "Merged");
        Loc.Register(PullRequestNone, "当前分支还没有拉取请求。", "This branch has no pull request yet.");
        Loc.Register(PullRequestLoading, "正在读取拉取请求…", "Reading the pull request…");
        Loc.Register(PullRequestGhNotReady, "需要安装并登录 gh", "Install and sign in to gh");
        Loc.Register(PullRequestReadFailed, "读取拉取请求失败：{0}", "Could not read the pull request: {0}");

        Loc.Register(WatchWaiting, "正在等待检查…", "Waiting for checks…");
        Loc.Register(WatchProgress, "通过 {0} · 失败 {1} · 进行中 {2}", "{0} passed · {1} failed · {2} pending");
        Loc.Register(WatchNoChecksYet, "尚未报告任何检查。", "No checks reported yet.");
        Loc.Register(WatchMergeState, "合并状态：{0}", "Merge state: {0}");
        Loc.Register(WatchCancelled, "已取消等待。", "Waiting was cancelled.");
        Loc.Register(
            OutcomeReady,
            "检查全部通过，可以合并（Forge 不会替你合并）。",
            "All checks passed; the pull request is ready to merge (Forge never merges for you).");
        Loc.Register(
            OutcomeReadyWithHooks,
            "检查全部通过，但合并时仍会运行服务端钩子，可能被拒绝。",
            "All checks passed, but server-side hooks still run when merging and can reject it.");
        Loc.Register(OutcomeChecksFailed, "{0} 个检查失败：{1}", "{0} check(s) failed: {1}");
        Loc.Register(
            OutcomeBehind,
            "检查已结束，但基础分支有本分支缺少的提交，需要先同步。",
            "The checks finished, but the base branch has commits this branch lacks; sync it first.");
        Loc.Register(
            OutcomeDirty,
            "分支与基础分支有冲突，需要先解决冲突。",
            "The branch conflicts with its base branch; resolve the conflicts first.");
        Loc.Register(
            OutcomeBlocked,
            "检查已结束，但仍有未满足的合并要求（例如必需的评审）。",
            "The checks finished, but a merge requirement is unmet (for example a required review).");
        Loc.Register(
            OutcomeUnstable,
            "可以合并，但有非必需的状态未通过。",
            "Mergeable, but a non-required status is not passing.");
        Loc.Register(
            OutcomeDraft,
            "检查已结束，但拉取请求仍是草稿。",
            "The checks finished, but the pull request is still a draft.");
        Loc.Register(
            OutcomeNoCi,
            "多次读取都没有任何检查：该拉取请求没有 CI。",
            "No checks were reported on several polls in a row: this pull request has no CI.");
        Loc.Register(
            OutcomeNotOpen,
            "拉取请求已合并或关闭，无需再等待。",
            "The pull request was merged or closed; there is nothing left to wait for.");
        Loc.Register(OutcomeTimedOut, "等待了 {0} 分钟，检查仍未结束。", "Still not settled after waiting {0} minute(s).");
        Loc.Register(OutcomeFailed, "无法读取检查：{0}", "Could not read the checks: {0}");
        Loc.Register(NotifyTitle, "Forge：拉取请求 #{0}", "Forge: pull request #{0}");

        Loc.Register(ChecksRunning, "CI …", "CI …");
        Loc.Register(ChecksPassed, "CI ✓", "CI ✓");
        Loc.Register(ChecksFailed, "CI ✗", "CI ✗");
        Loc.Register(ChecksAttention, "CI !", "CI !");
        Loc.Register(ChecksNone, "CI –", "CI –");

        Loc.Register(ReviewSummary, "{0} 条评审线程，{1} 条未解决", "{0} thread(s), {1} unresolved");
        Loc.Register(ReviewLoading, "正在读取评审线程…", "Reading the review threads…");
        Loc.Register(ReviewEmpty, "没有评审线程。", "No review threads.");
        Loc.Register(ReviewReadFailed, "读取评审线程失败：{0}", "Could not read the review threads: {0}");
        Loc.Register(ReviewResolved, "已解决", "Resolved");
        Loc.Register(ReviewUnresolved, "未解决", "Unresolved");
        Loc.Register(ReviewOutdated, "已过时", "Outdated");
        Loc.Register(ReviewReplies, "回复 {0}", "Replies {0}");
        Loc.Register(ReviewNoFile, "（整个拉取请求）", "(whole pull request)");
        Loc.Register(ReviewUnknownAuthor, "（未知）", "(unknown)");
        Loc.Register(ReviewVerdictApply, "结论：已采纳（{0}）", "Verdict: applied ({0})");
        Loc.Register(ReviewVerdictPushBack, "结论：已反驳", "Verdict: pushed back");
        Loc.Register(ReviewVerdictClarify, "结论：待澄清", "Verdict: needs clarification");

        Loc.Register(
            HintAuthentication,
            "凭据缺失或被拒绝：先在终端完成 Git 登录，或改用 SSH 密钥。",
            "Credentials are missing or rejected: sign in to Git in a terminal, or use an SSH key.");
        Loc.Register(
            HintNonFastForward,
            "远程有本地没有的提交：先拉取，或在设置里改用变基/合并后再推送。",
            "The remote has commits you do not: pull first, or switch the pull strategy to rebase/merge and push again.");
        Loc.Register(
            HintConflict,
            "有冲突：解决冲突文件并暂存后点“继续”，或点“中止”放弃本次操作。",
            "There are conflicts: resolve and stage the files, then Continue, or Abort the operation.");
        Loc.Register(
            HintRemoteNotFound,
            "远程不存在：检查远程地址，或先添加远程。",
            "The remote does not exist: check its URL or add a remote first.");
        Loc.Register(
            HintNoUpstream,
            "当前分支没有上游：设置上游，或用推送（首次推送会自动设置）。",
            "The branch has no upstream: set one, or push (the first push sets it).");
        Loc.Register(
            HintDirtyWorkTree,
            "工作树有未提交改动：先提交或暂存。",
            "The working tree has uncommitted changes: commit or stash them first.");
        Loc.Register(
            HintNetwork,
            "网络不可达：检查连接或代理后重试。",
            "The network is unreachable: check the connection or proxy and retry.");
        Loc.Register(
            HintTimedOut,
            "操作超时：可在 Git 设置分区加大网络超时。",
            "The operation timed out: raise the network timeout in the Git settings.");
        Loc.Register(HintOther, "查看下方的 Git 输出。", "See the Git output below.");
    }
}
