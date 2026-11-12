using System.Runtime.CompilerServices;

namespace Forge.App.Localization;

/// <summary>
/// The parallel fork, merge-back and sync table (lifecycle 6.5, 6.6): the menu entries, the "forked from" row label,
/// the fork dialog, the merge / sync dialog and every failure they report.
/// <para>Keys: <c>lifecycle.fork.*</c> and <c>lifecycle.merge.*</c>. Each key has a Chinese and an English text;
/// parameterized texts are typed accessors formatted through <see cref="Loc.Format"/>.</para>
/// </summary>
internal static class ForkStrings
{
    // ---------------------------------------------------------------- fork: menu and row
    public const string MenuForkKey = "lifecycle.fork.menu";
    public const string MenuForkInheritKey = "lifecycle.fork.menuInherit";
    public const string ForkedFromKey = "lifecycle.fork.forkedFrom";

    // ---------------------------------------------------------------- fork: dialog
    public const string TitleKey = "lifecycle.fork.title";
    public const string IntroKey = "lifecycle.fork.intro";
    public const string BranchLabelKey = "lifecycle.fork.branchLabel";
    public const string UncommittedKeyBase = "lifecycle.fork.uncommitted";
    public const string CommitFirstKey = "lifecycle.fork.commitFirst";
    public const string ForkAnywayKey = "lifecycle.fork.forkAnyway";
    public const string NotCarriedKeyBase = "lifecycle.fork.notCarried";
    public const string SourceBusyKey = "lifecycle.fork.sourceBusy";
    public const string NoSessionKey = "lifecycle.fork.noSession";
    public const string SparseKey = "lifecycle.fork.sparse";
    public const string ColdBuildKey = "lifecycle.fork.coldBuild";
    public const string ConfirmKey = "lifecycle.fork.confirm";
    public const string RunningKey = "lifecycle.fork.running";

    // ---------------------------------------------------------------- fork: refusals and failures
    public const string NotRepositoryKey = "lifecycle.fork.notRepository";
    public const string DetachedKey = "lifecycle.fork.detached";
    public const string UnbornKey = "lifecycle.fork.unborn";
    public const string ReadFailedKey = "lifecycle.fork.readFailed";
    public const string BranchRequiredKey = "lifecycle.fork.branchRequired";
    public const string BusyNowKey = "lifecycle.fork.busyNow";
    public const string SourceChangedKey = "lifecycle.fork.sourceChanged";
    public const string CommitFailedKey = "lifecycle.fork.commitFailed";
    public const string CreateFailedKey = "lifecycle.fork.createFailed";
    public const string SessionFailedKey = "lifecycle.fork.sessionFailed";
    public const string CancelledKey = "lifecycle.fork.cancelled";
    public const string UnavailableKey = "lifecycle.fork.unavailable";

    // ---------------------------------------------------------------- merge back / sync
    public const string MenuMergeBackKey = "lifecycle.merge.menuMergeBack";
    public const string MenuSyncKey = "lifecycle.merge.menuSync";
    public const string MenuAbortKey = "lifecycle.merge.menuAbort";
    public const string MergeBackTitleKey = "lifecycle.merge.mergeBackTitle";
    public const string SyncTitleKey = "lifecycle.merge.syncTitle";
    public const string MergeBackIntroKey = "lifecycle.merge.mergeBackIntro";
    public const string SyncIntroKey = "lifecycle.merge.syncIntro";
    public const string CommitsKeyBase = "lifecycle.merge.commits";
    public const string MoreCommitsKeyBase = "lifecycle.merge.moreCommits";
    public const string AlreadyMergedKey = "lifecycle.merge.alreadyMerged";
    public const string UpToDateKey = "lifecycle.merge.upToDate";
    public const string TargetMissingKey = "lifecycle.merge.targetMissing";
    public const string PushForkKey = "lifecycle.merge.pushFork";
    public const string PushedKey = "lifecycle.merge.pushed";
    public const string PushFailedKey = "lifecycle.merge.pushFailed";
    public const string SourceMissingKey = "lifecycle.merge.sourceMissing";
    public const string ForkMissingKey = "lifecycle.merge.forkMissing";
    public const string PreviewFailedKey = "lifecycle.merge.previewFailed";
    public const string BusyKey = "lifecycle.merge.busy";
    public const string MergeKey = "lifecycle.merge.merge";
    public const string SyncKey = "lifecycle.merge.sync";
    public const string MergedKey = "lifecycle.merge.merged";
    public const string ConflictKey = "lifecycle.merge.conflict";
    public const string LaterKey = "lifecycle.merge.later";
    public const string DirtyRefusedKey = "lifecycle.merge.dirtyRefused";
    public const string FailedKey = "lifecycle.merge.failed";
    public const string TargetMovedKey = "lifecycle.merge.targetMoved";
    public const string AbortedKey = "lifecycle.merge.aborted";
    public const string AbortFailedKey = "lifecycle.merge.abortFailed";
    public const string OfferRemoveKey = "lifecycle.merge.offerRemove";
    public const string KeepForkKey = "lifecycle.merge.keepFork";
    public const string RemoveFailedKey = "lifecycle.merge.removeFailed";

    public static string MenuFork => Loc.Instance[MenuForkKey];

    public static string MenuForkInherit => Loc.Instance[MenuForkInheritKey];

    public static string ForkedFrom(string branch) => Loc.Instance.Format(ForkedFromKey, branch);

    public static string Title => Loc.Instance[TitleKey];

    public static string Intro(string branch, string sha) => Loc.Instance.Format(IntroKey, branch, TextFormat.ShortSha(sha));

    public static string BranchLabel => Loc.Instance[BranchLabelKey];

    public static string Uncommitted(int count) => Loc.Instance.FormatPlural(UncommittedKeyBase, count);

    public static string CommitFirst(string title) => Loc.Instance.Format(CommitFirstKey, title);

    public static string ForkAnyway => Loc.Instance[ForkAnywayKey];

    public static string NotCarried(int count) => Loc.Instance.FormatPlural(NotCarriedKeyBase, count);

    public static string SourceBusy(string title) => Loc.Instance.Format(SourceBusyKey, title);

    public static string NoSession => Loc.Instance[NoSessionKey];

    public static string Sparse => Loc.Instance[SparseKey];

    public static string ColdBuild => Loc.Instance[ColdBuildKey];

    public static string Confirm => Loc.Instance[ConfirmKey];

    public static string Running => Loc.Instance[RunningKey];

    public static string NotRepository => Loc.Instance[NotRepositoryKey];

    public static string Detached => Loc.Instance[DetachedKey];

    public static string Unborn => Loc.Instance[UnbornKey];

    public static string ReadFailed(string reason) => Loc.Instance.Format(ReadFailedKey, reason);

    public static string BranchRequired => Loc.Instance[BranchRequiredKey];

    public static string BusyNow(string title) => Loc.Instance.Format(BusyNowKey, title);

    public static string SourceChanged(string title) => Loc.Instance.Format(SourceChangedKey, title);

    public static string CommitFailed(string reason) => Loc.Instance.Format(CommitFailedKey, reason);

    public static string CreateFailed(string reason) => Loc.Instance.Format(CreateFailedKey, reason);

    public static string SessionFailed(string reason) => Loc.Instance.Format(SessionFailedKey, reason);

    public static string Cancelled => Loc.Instance[CancelledKey];

    public static string Unavailable => Loc.Instance[UnavailableKey];

    public static string MenuMergeBack(string branch) => Loc.Instance.Format(MenuMergeBackKey, branch);

    public static string MenuSync(string branch) => Loc.Instance.Format(MenuSyncKey, branch);

    public static string MenuAbort => Loc.Instance[MenuAbortKey];

    public static string MergeBackTitle => Loc.Instance[MergeBackTitleKey];

    public static string SyncTitle => Loc.Instance[SyncTitleKey];

    public static string MergeBackIntro(string fork, string source, string target) =>
        Loc.Instance.Format(MergeBackIntroKey, fork, source, target);

    public static string SyncIntro(string source, string fork) => Loc.Instance.Format(SyncIntroKey, source, fork);

    public static string Commits(int count) => Loc.Instance.FormatPlural(CommitsKeyBase, count);

    public static string MoreCommits(int count) => Loc.Instance.FormatPlural(MoreCommitsKeyBase, count);

    public static string AlreadyMerged(string fork, string source) => Loc.Instance.Format(AlreadyMergedKey, fork, source);

    public static string UpToDate(string fork, string source) => Loc.Instance.Format(UpToDateKey, fork, source);

    public static string TargetMissing(string source, string fork) => Loc.Instance.Format(TargetMissingKey, source, fork);

    public static string PushFork(string fork) => Loc.Instance.Format(PushForkKey, fork);

    public static string Pushed(string fork) => Loc.Instance.Format(PushedKey, fork);

    public static string PushFailed(string reason) => Loc.Instance.Format(PushFailedKey, reason);

    public static string SourceMissing(string source) => Loc.Instance.Format(SourceMissingKey, source);

    public static string ForkMissing(string fork) => Loc.Instance.Format(ForkMissingKey, fork);

    public static string PreviewFailed(string reason) => Loc.Instance.Format(PreviewFailedKey, reason);

    public static string Busy(string title, IReadOnlyList<string> reasons) =>
        Loc.Instance.Format(BusyKey, title, string.Join(Environment.NewLine, reasons.Select(static reason => "• " + reason)));

    public static string Merge => Loc.Instance[MergeKey];

    public static string Sync => Loc.Instance[SyncKey];

    public static string Merged(string revision, string target) => Loc.Instance.Format(MergedKey, revision, target);

    public static string Conflict(string target) => Loc.Instance.Format(ConflictKey, target);

    public static string Later => Loc.Instance[LaterKey];

    public static string DirtyRefused(string detail) => Loc.Instance.Format(DirtyRefusedKey, detail);

    public static string Failed(string reason) => Loc.Instance.Format(FailedKey, reason);

    public static string TargetMoved(string target, string branch) => Loc.Instance.Format(TargetMovedKey, target, branch);

    public static string Aborted => Loc.Instance[AbortedKey];

    public static string AbortFailed(string reason) => Loc.Instance.Format(AbortFailedKey, reason);

    public static string OfferRemove => Loc.Instance[OfferRemoveKey];

    public static string KeepFork => Loc.Instance[KeepForkKey];

    public static string RemoveFailed(string reason) => Loc.Instance.Format(RemoveFailedKey, reason);

    [ModuleInitializer]
    internal static void Initialize() => Register();

    /// <summary>Idempotent: registering the same key with the same texts twice is a no-op.</summary>
    internal static void Register()
    {
        Loc.Register(MenuForkKey, "并行分叉…", "Parallel fork…");
        Loc.Register(MenuForkInheritKey, "并行分叉(继承此会话)…", "Parallel fork (inherit this session)…");
        Loc.Register(ForkedFromKey, "⑂ 分叉自 {0}", "⑂ forked from {0}");

        Loc.Register(TitleKey, "并行分叉", "Parallel fork");
        Loc.Register(IntroKey, "从“{0}”的提交 {1} 分叉出新的工作区与会话;源会话照常继续。", "Fork a new workspace and session from \"{0}\" at {1}; the source session goes on as before.");
        Loc.Register(BranchLabelKey, "新分支名:", "New branch name:");
        Loc.Register(UncommittedKeyBase + ".one", "源工作区有 {0} 个未提交的改动(含未跟踪文件):", "The source workspace has {0} uncommitted change (untracked files included):");
        Loc.Register(UncommittedKeyBase + ".other", "源工作区有 {0} 个未提交的改动(含未跟踪文件):", "The source workspace has {0} uncommitted changes (untracked files included):");
        Loc.Register(CommitFirstKey, "先以“{0}”的名义提交,再分叉", "Commit them in the name of \"{0}\" first, then fork");
        Loc.Register(ForkAnywayKey, "照样分叉(基于最后一次提交)", "Fork anyway (from the last commit)");
        Loc.Register(NotCarriedKeyBase + ".one", "未提交的 {0} 个文件不会带过去。", "The {0} uncommitted file is not carried over.");
        Loc.Register(NotCarriedKeyBase + ".other", "未提交的 {0} 个文件不会带过去。", "The {0} uncommitted files are not carried over.");
        Loc.Register(SourceBusyKey, "“{0}”正忙,只能基于最后一次提交分叉。", "\"{0}\" is busy, so the fork can only start from the last commit.");
        Loc.Register(NoSessionKey, "此工作区没有活动会话,新会话从空白开始。", "This workspace has no active session; the new session starts blank.");
        Loc.Register(SparseKey, "源工作区是稀疏检出,新工作区沿用同样的配置。", "The source workspace is a sparse checkout; the new workspace uses the same configuration.");
        Loc.Register(ColdBuildKey, "新工作区不复制 obj/,需要冷构建一次(大仓库可能要几分钟)。待办、计划与评审标记不会复制。", "The new workspace does not copy obj/ and needs one cold build (minutes for a large repository). Todos, plans and review marks are not copied.");
        Loc.Register(ConfirmKey, "分叉", "Fork");
        Loc.Register(RunningKey, "正在分叉…", "Forking…");

        Loc.Register(NotRepositoryKey, "该目录不是 git 仓库,无法分叉。", "This folder is not a git repository, so it cannot be forked.");
        Loc.Register(DetachedKey, "源工作区处于游离 HEAD,无法分叉。", "The source workspace has a detached HEAD, so it cannot be forked.");
        Loc.Register(UnbornKey, "源分支还没有任何提交,无法分叉。", "The source branch has no commit yet, so it cannot be forked.");
        Loc.Register(ReadFailedKey, "无法读取源工作区,未分叉:{0}", "Could not read the source workspace; nothing was forked: {0}");
        Loc.Register(BranchRequiredKey, "请填写有效的分支名。", "Enter a valid branch name.");
        Loc.Register(SourceChangedKey, "“{0}”已不是此工作区的活动会话(已被接手或归档),未分叉。请在刷新后的树上重新分叉。", "\"{0}\" is no longer the active session of this workspace (taken over or archived); nothing was forked. Fork again from the refreshed tree.");
        Loc.Register(BusyNowKey, "“{0}”已开始忙,不能先提交;请改选“照样分叉”或稍后再试。", "\"{0}\" became busy, so nothing can be committed first; choose \"Fork anyway\" or try again later.");
        Loc.Register(CommitFailedKey, "分叉前的提交失败,未分叉:{0}", "The commit before the fork failed; nothing was forked: {0}");
        Loc.Register(CreateFailedKey, "无法创建工作区,未分叉:{0}", "Could not create the workspace; nothing was forked: {0}");
        Loc.Register(SessionFailedKey, "新会话未能建立,已撤回新建的工作区:{0}", "The new session could not be created, so the new workspace was taken back: {0}");
        Loc.Register(CancelledKey, "已取消,未分叉。", "Cancelled; nothing was forked.");
        Loc.Register(UnavailableKey, "此窗口无法显示分叉对话框,未分叉。", "This window cannot show the fork dialog; nothing was forked.");

        Loc.Register(MenuMergeBackKey, "并回到 {0}…", "Merge back into {0}…");
        Loc.Register(MenuSyncKey, "从 {0} 同步…", "Sync from {0}…");
        Loc.Register(MenuAbortKey, "中止合并", "Abort the merge");
        Loc.Register(MergeBackTitleKey, "并回", "Merge back");
        Loc.Register(SyncTitleKey, "同步", "Sync");
        Loc.Register(MergeBackIntroKey, "把“{0}”合并进“{1}”(在 {2} 执行,--no-ff)。", "Merge \"{0}\" into \"{1}\" (run in {2}, --no-ff).");
        Loc.Register(SyncIntroKey, "把“{0}”合并进“{1}”(在分叉工作区执行,--no-ff)。", "Merge \"{0}\" into \"{1}\" (run in the fork workspace, --no-ff).");
        Loc.Register(CommitsKeyBase + ".one", "将带入 {0} 个提交:", "{0} commit comes along:");
        Loc.Register(CommitsKeyBase + ".other", "将带入 {0} 个提交:", "{0} commits come along:");
        Loc.Register(MoreCommitsKeyBase + ".one", "…另有 {0} 个", "… and {0} more");
        Loc.Register(MoreCommitsKeyBase + ".other", "…另有 {0} 个", "… and {0} more");
        Loc.Register(AlreadyMergedKey, "“{0}”已并回“{1}”,无需执行。", "\"{0}\" is already merged into \"{1}\"; nothing to do.");
        Loc.Register(UpToDateKey, "“{0}”已包含“{1}”的全部提交,已是最新。", "\"{0}\" already has every commit of \"{1}\"; it is up to date.");
        Loc.Register(TargetMissingKey, "没有工作区检出“{0}”(未检出、已改名或已删除),无法在本地并回。可以推送分叉分支“{1}”,再对其开 PR。", "No workspace has \"{0}\" checked out (not checked out, renamed or deleted), so it cannot be merged back locally. You can push the fork branch \"{1}\" and open a PR for it.");
        Loc.Register(PushForkKey, "推送“{0}”", "Push \"{0}\"");
        Loc.Register(PushedKey, "已推送“{0}”,可在远端对其开 PR。", "Pushed \"{0}\"; open a PR for it on the remote.");
        Loc.Register(PushFailedKey, "推送失败:{0}", "The push failed: {0}");
        Loc.Register(SourceMissingKey, "源分支“{0}”已不存在。", "The source branch \"{0}\" no longer exists.");
        Loc.Register(ForkMissingKey, "分叉分支“{0}”已不存在。", "The fork branch \"{0}\" no longer exists.");
        Loc.Register(PreviewFailedKey, "无法读取要合并的提交:{0}", "Could not read the commits to merge: {0}");
        Loc.Register(BusyKey, "“{0}”尚未空闲,暂不能合并:\n{1}\n\n等它空闲后再试。", "\"{0}\" is not idle yet, so nothing can be merged:\n{1}\n\nTry again once it is idle.");
        Loc.Register(MergeKey, "并回", "Merge back");
        Loc.Register(SyncKey, "同步", "Sync");
        Loc.Register(MergedKey, "已把“{0}”合并进“{1}”。", "Merged \"{0}\" into \"{1}\".");
        Loc.Register(ConflictKey, "合并出现冲突,停在 {0}。到该工作区处理冲突文件(改动面板会列出)并提交;在那之前该工作区的会话不能开新回合。也可以现在中止合并。", "The merge stopped on conflicts in {0}. Resolve the conflicted files there (the changes panel lists them) and commit; until then that workspace's session cannot start a turn. You can also abort the merge now.");
        Loc.Register(LaterKey, "稍后处理", "Resolve later");
        Loc.Register(DirtyRefusedKey, "git 拒绝合并,因为会覆盖目标工作区的本地改动:\n{0}", "git refused to merge because it would overwrite local changes in the target workspace:\n{0}");
        Loc.Register(FailedKey, "合并失败:{0}", "The merge failed: {0}");
        Loc.Register(TargetMovedKey, "“{0}”已不再检出“{1}”,未合并。请重新打开合并。", "\"{0}\" no longer has \"{1}\" checked out; nothing was merged. Open the merge again.");
        Loc.Register(AbortedKey, "已中止合并,工作区回到合并前的状态。", "The merge was aborted; the workspace is back where it was.");
        Loc.Register(AbortFailedKey, "中止合并失败:{0}", "Could not abort the merge: {0}");
        Loc.Register(OfferRemoveKey, "归档分叉会话并移除其工作区", "Archive the fork session and remove its workspace");
        Loc.Register(KeepForkKey, "保留", "Keep");
        Loc.Register(RemoveFailedKey, "已并回,但分叉工作区未移除:{0}", "Merged back, but the fork workspace was not removed: {0}");
    }
}
