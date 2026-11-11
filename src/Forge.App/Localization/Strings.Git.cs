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

    // ---------------------------------------------------------------- the files panel's git row

    /// <summary>Tooltip of the fetch button: the read-only verb.</summary>
    public const string RowFetchHint = "git.row.fetchHint";

    /// <summary>Format: <c>{0}</c> is the pull strategy, <c>{1}</c> the upstream it integrates.</summary>
    public const string RowPullHint = "git.row.pullHint";

    /// <summary>Format: <c>{0}</c> strategy, <c>{1}</c> upstream, <c>{2}</c> how many commits behind.</summary>
    public const string RowPullHintBehind = "git.row.pullHintBehind";

    /// <summary>Format: <c>{0}</c> how many commits ahead, <c>{1}</c> the branch name the push lands on.</summary>
    public const string RowPushHint = "git.row.pushHint";

    /// <summary>Format: <c>{0}</c> is the same-named remote branch the first push creates.</summary>
    public const string RowPushHintFirst = "git.row.pushHintFirst";

    public const string RowNoRemote = "git.row.noRemote";

    /// <summary>
    /// Tooltip of the branch label: format <c>{0}</c> branch, <c>{1}</c> the same-named branch a push lands on, <c>{2}</c> upstream,
    /// <c>{3}</c> the short head commit. The one place "pushes to" and "upstream" are visible side by side.)
    /// </summary>
    public const string RowBranchHint = "git.row.branchHint";

    /// <summary>
    /// Accessible name of the whole row: format <c>{0}</c> branch, <c>{1}</c> upstream, <c>{2}</c> ahead, <c>{3}</c>
    /// behind. The arrows and colours are not read out, so the numbers have to be in the name.
    /// </summary>
    public const string RowAutomationName = "git.row.automationName";

    /// <summary>Opens the failure's raw git output (stderr) under the row.</summary>
    public const string RowDetails = "git.row.details";

    /// <summary>Why a detached head cannot be pushed.</summary>
    public const string PushDetached = "git.push.detached";


    // ---------------------------------------------------------------- the 提交目标 row

    /// <summary>The split button's label before any pull request exists.</summary>
    public const string ShipTarget = "git.ship.target";

    public const string ShipCompose = "git.ship.compose";
    public const string ShipOpenInBrowser = "git.ship.browser";
    public const string ShipNoRemote = "git.ship.noRemote";
    public const string ShipNoPlatform = "git.ship.noPlatform";
    public const string ShipNoDialog = "git.ship.noDialog";
    public const string ShipPublishFirst = "git.ship.publishFirst";
    public const string ShipReading = "git.ship.reading";
    public const string ShipAlreadyExists = "git.ship.alreadyExists";
    public const string ShipNoPullRequest = "git.ship.noPullRequest";

    /// <summary>Format: <c>{0}</c> is the new pull request's number.</summary>
    public const string ShipCreated = "git.ship.created";

    public const string PrStateOpen = "git.pr.state.open";
    public const string PrStateDraft = "git.pr.state.draft";
    public const string PrStateMerged = "git.pr.state.merged";
    public const string PrStateClosed = "git.pr.state.closed";

    /// <summary>The publish pill on the 提交目标 row (first push + upstream).</summary>
    public const string ShipPublish = "git.ship.publish";

    public const string ShipDialogTitle = "git.ship.dialog.title";
    public const string ShipDialogTitleLabel = "git.ship.dialog.titleLabel";
    public const string ShipDialogBodyLabel = "git.ship.dialog.bodyLabel";
    public const string ShipDialogDraft = "git.ship.dialog.draft";
    public const string ShipDialogCreate = "git.ship.dialog.create";
    public const string ShipDialogCancel = "git.ship.dialog.cancel";

    public const string ShipMergeLocally = "git.ship.mergeLocally";
    public const string ShipMergePullRequest = "git.ship.mergePullRequest";
    public const string ShipDeleteRemoteBranch = "git.ship.deleteRemote";
    public const string ShipDeleteRemoteHint = "git.ship.deleteRemoteHint";
    public const string ShipDeleteRemoteNotYet = "git.ship.deleteRemoteNotYet";
    public const string ShipDeleteRemoteTitle = "git.ship.deleteRemoteTitle";
    public const string ShipDeleteRemoteMessage = "git.ship.deleteRemoteMessage";
    public const string ShipNoProjectRoot = "git.ship.noProjectRoot";
    public const string ShipNoDefaultBranch = "git.ship.noDefaultBranch";
    public const string ShipAlreadyOnBranch = "git.ship.alreadyOnBranch";
    public const string ShipAlreadyMerged = "git.ship.alreadyMerged";
    public const string ShipRootDirty = "git.ship.rootDirty";
    public const string ShipRootElsewhere = "git.ship.rootElsewhere";

    /// <summary>The remote readout on the ship row: not published yet / N to push / in sync / behind.</summary>
    public const string RemoteNotPublished = "git.ship.remote.notPublished";
    public const string RemoteToPush = "git.ship.remote.toPush";
    public const string RemoteInSync = "git.ship.remote.inSync";
    public const string RemoteBehind = "git.ship.remote.behind";

    /// <summary>Format: <c>{0}</c> the shared branch the push lands on.</summary>
    public const string PushSharedTitle = "git.ship.push.sharedTitle";

    /// <summary>Format: <c>{0}</c> the shared branch, <c>{1}</c> how many commits.</summary>
    public const string PushSharedMessage = "git.ship.push.sharedMessage";

    public const string PushSharedMessageFirst = "git.ship.push.sharedMessageFirst";
    public const string ShipMergeCommitTitle = "git.ship.mergeCommitTitle";
    public const string ShipMergeCommitMessage = "git.ship.mergeCommitMessage";
    public const string ShipMergedNumber = "git.ship.mergedNumber";
    public const string ShipMergeDraft = "git.ship.mergeDraft";
    public const string ShipMerged = "git.ship.merged";
    public const string ShipClosed = "git.ship.closed";
    public const string ShipMergeBehind = "git.ship.mergeBehind";
    public const string ShipMergeDirty = "git.ship.mergeDirty";
    public const string ShipMergeBlocked = "git.ship.mergeBlocked";
    public const string ShipMergeUnknown = "git.ship.mergeUnknown";

    public const string ShipMergeDialogTitle = "git.ship.mergeDialog.title";
    public const string ShipMergeDialogBody = "git.ship.mergeDialog.body";
    public const string ShipMergeDialogDelete = "git.ship.mergeDialog.delete";
    public const string ShipMergeDialogGo = "git.ship.mergeDialog.go";
    public const string ShipMergeDialogSquash = "git.ship.mergeDialog.squash";
    public const string ShipMergeDialogMerge = "git.ship.mergeDialog.merge";
    public const string ShipMergeDialogRebase = "git.ship.mergeDialog.rebase";

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

        Loc.Register(ShipTarget, "提交目标", "Send to");
        Loc.Register(ShipCompose, "打开 PR…", "Open a pull request…");
        Loc.Register(ShipOpenInBrowser, "在浏览器打开", "Open in browser");
        Loc.Register(ShipNoRemote, "没有配置远程", "No remote is configured");
        Loc.Register(ShipNoPlatform, "这个版本没有连接 GitHub", "This build is not connected to GitHub");
        Loc.Register(ShipNoDialog, "这个窗口无法弹出对话框", "This window cannot show the dialog");
        Loc.Register(ShipPublishFirst, "先发布这个分支（首次推送顺便设上游）", "Publish the branch first (the first push sets the upstream)");
        Loc.Register(ShipReading, "正在读取拉取请求…", "Reading the pull request…");
        Loc.Register(ShipAlreadyExists, "这个分支已经有拉取请求", "This branch already has a pull request");
        Loc.Register(ShipNoPullRequest, "当前分支还没有拉取请求", "This branch has no pull request yet");
        Loc.Register(ShipCreated, "已打开 PR #{0}。", "Opened pull request #{0}.");
        Loc.Register(PrStateOpen, "打开", "Open");
        Loc.Register(PrStateDraft, "草稿", "Draft");
        Loc.Register(PrStateMerged, "已合并", "Merged");
        Loc.Register(PrStateClosed, "已关闭", "Closed");
        Loc.Register(ShipPublish, "发布", "Publish");
        Loc.Register(ShipMergeLocally, "在本地合并到 {0}", "Merge into {0} locally");
        Loc.Register(ShipMergePullRequest, "合并 PR…", "Merge the pull request…");
        Loc.Register(ShipDeleteRemoteBranch, "删除远程分支…", "Delete the remote branch…");
        Loc.Register(ShipDeleteRemoteHint, "只删平台上的分支，本地分支与提交都留着", "Deletes the branch on the platform only; the local branch stays");
        Loc.Register(ShipDeleteRemoteNotYet, "PR 合并后才能删除远程分支", "The remote branch can go once the pull request is merged");
        Loc.Register(ShipDeleteRemoteTitle, "删除 {0}/{1}？", "Delete {0}/{1}?");
        Loc.Register(ShipDeleteRemoteMessage, "分支会从平台上删除；已合并的提交不受影响。", "The branch is deleted on the platform; the merged commits stay.");
        Loc.Register(ShipNoProjectRoot, "这个会话不在项目里，找不到根检出", "This session has no project root checkout");
        Loc.Register(ShipNoDefaultBranch, "读不到仓库的默认分支", "The repository's default branch could not be read");
        Loc.Register(ShipAlreadyOnBranch, "当前就在 {0} 上", "This session is already on {0}");
        Loc.Register(ShipAlreadyMerged, "{0} 已经在 {1} 里了。", "{0} is already in {1}.");
        Loc.Register(ShipRootDirty, "根检出（{0}）有 {1} 个未提交改动：先提交或暂存再合并。", "The root checkout ({0}) has {1} uncommitted change(s); commit or stash them first.");
        Loc.Register(RemoteNotPublished, "未发布", "Not published");
        Loc.Register(RemoteToPush, "{0} 个提交待推送", "{0} to push");
        Loc.Register(RemoteInSync, "与 origin 同步", "In sync with origin");
        Loc.Register(RemoteBehind, "落后 origin {0} 个提交", "{0} behind origin");
        Loc.Register(
            PushSharedTitle,
            "推送到 {0}？",
            "Push to {0}?");
        Loc.Register(
            PushSharedMessage,
            "这会把这 {1} 个提交直接推到 {0}，它是共享分支——不会有拉取请求先评审。",
            "This pushes those {1} commits straight to {0}, the shared branch — no pull request reviews them first.");
        Loc.Register(
            PushSharedMessageFirst,
            "这会把当前分支直接推到 {0}，它是共享分支——不会有拉取请求先评审。",
            "This pushes the branch straight to {0}, the shared branch — no pull request reviews them first.");
        Loc.Register(ShipRootElsewhere, "根检出（{0}）当前在 {1} 上，不是 {2}：先在根检出切到 {2} 再合并。", "The root checkout ({0}) is on {1}, not {2}: switch it to {2} before merging.");
        Loc.Register(ShipMergeCommitTitle, "合并到 {0} 需要一个合并提交", "Merging into {0} needs a merge commit");
        Loc.Register(ShipMergeCommitMessage, "{0} 与 {1} 已经分叉，无法快进：将创建一个合并提交。继续？", "{0} and {1} have diverged, so no fast-forward is possible: a merge commit will be created. Continue?");
        Loc.Register(ShipMergedNumber, "已合并 PR #{0}。", "Merged pull request #{0}.");
        Loc.Register(ShipMergeDialogTitle, "合并 PR 到 {0}？", "Merge the pull request into {0}?");
        Loc.Register(ShipMergeDialogBody, "GitHub 会合并此拉取请求；这个工作区保留它本地的 {0} 分支。", "GitHub merges the pull request; this workspace keeps its local branch {0}.");
        Loc.Register(ShipMergeDialogDelete, "在 GitHub 上删除 {0}", "Delete {0} on GitHub");
        Loc.Register(ShipMergeDialogGo, "合并", "Merge");
        Loc.Register(ShipMergeDialogSquash, "压缩成一个提交", "Squash into one commit");
        Loc.Register(ShipMergeDialogMerge, "创建合并提交", "Create a merge commit");
        Loc.Register(ShipMergeDialogRebase, "变基并合并", "Rebase and merge");

        Loc.Register(ShipMergeDraft, "草稿 PR 不能合并", "A draft pull request cannot be merged");
        Loc.Register(ShipMerged, "这个 PR 已经合并", "This pull request is already merged");
        Loc.Register(ShipClosed, "这个 PR 已关闭", "This pull request is closed");
        Loc.Register(ShipMergeBehind, "PR 落后于基线：先在浏览器里更新分支", "The pull request is behind its base: update it in the browser first");
        Loc.Register(ShipMergeDirty, "PR 与基线有冲突：先在浏览器里解决", "The pull request conflicts with its base: resolve it in the browser first");
        Loc.Register(ShipMergeBlocked, "还有未满足的合并要求（例如必需的评审）", "A merge requirement is unmet (for example a required review)");
        Loc.Register(ShipMergeUnknown, "平台还没算出能否合并", "The platform has not computed mergeability yet");

        Loc.Register(ShipDialogTitle, "打开拉取请求", "Open a pull request");
        Loc.Register(ShipDialogTitleLabel, "标题", "Title");
        Loc.Register(ShipDialogBodyLabel, "说明", "Description");
        Loc.Register(ShipDialogDraft, "作为草稿打开", "Open as a draft");
        Loc.Register(ShipDialogCreate, "创建", "Create");
        Loc.Register(ShipDialogCancel, "取消", "Cancel");

        Loc.Register(RowFetchHint, "抓取：只看远端，不动本地分支", "Fetch: read the remote only, never touch the local branch");
        Loc.Register(
            RowPullHint,
            "拉取：按{0}并入 {1}",
            "Pull: integrate {1} with {0}");
        Loc.Register(
            RowPullHintBehind,
            "拉取：按{0}并入 {1}（落后 {2} 个提交）",
            "Pull: integrate {1} with {0} ({2} commit(s) behind)");
        Loc.Register(RowPushHint, "推送：把 {0} 个提交推到同名远端分支 {1}", "Push: send {0} commit(s) to the same-named remote branch {1}");
        Loc.Register(
            RowPushHintFirst,
            "推送：首次推送会创建同名远端分支 {0} 并设为上游",
            "Push: the first push creates the same-named remote branch {0} and sets it as the upstream");
        Loc.Register(RowNoRemote, "没有配置远程", "No remote is configured");
        Loc.Register(
            RowBranchHint,
            "分支 {0} · 推送到同名远端分支 {1} · 上游 {2} · HEAD {3}",
            "Branch {0} · pushes to the same-named remote branch {1} · upstream {2} · HEAD {3}");
        Loc.Register(
            RowAutomationName,
            "分支 {0}，上游 {1}，领先 {2}，落后 {3}",
            "Branch {0}, upstream {1}, {2} ahead, {3} behind");
        Loc.Register(RowDetails, "详情", "Details");
        Loc.Register(PushDetached, "分离 HEAD 上不能推送", "A detached head cannot be pushed");

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
            "当前分支没有上游：推送一次即可建立跟踪（首次推送会自动设置上游）。",
            "The branch has no upstream: push once and tracking is created (the first push sets it).");
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
