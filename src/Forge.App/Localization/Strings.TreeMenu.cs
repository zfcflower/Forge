using System.Runtime.CompilerServices;

namespace Forge.App.Localization;

/// <summary>
/// The generated tree menus (lifecycle 5): the entries no earlier table owns, the reasons a disabled entry gives, the
/// read-only "sparse" mark, and the rename-branch (7.8), link-workspace (7.10) and sparse-checkout (7.9) dialogs.
/// <para>Keys: <c>lifecycle.menu.*</c>, <c>lifecycle.renameBranch.*</c>, <c>lifecycle.link.*</c> and
/// <c>lifecycle.sparse.*</c>. Entries an earlier table already names (rename, archive, fork, remove…) keep their keys.</para>
/// </summary>
internal static class TreeMenuStrings
{
    // ---------------------------------------------------------------- entries
    public const string LinkWorkspaceKey = "lifecycle.menu.linkWorkspace";
    public const string RenameProjectKey = "lifecycle.menu.renameProject";
    public const string ReplaceSessionKey = "lifecycle.menu.replaceSession";
    public const string RenameBranchKey = "lifecycle.menu.renameBranch";
    public const string ViewReadOnlyKey = "lifecycle.menu.viewReadOnly";
    public const string ContinueSessionKey = "lifecycle.menu.continueSession";

    // ---------------------------------------------------------------- reasons and warnings
    public const string BusyKey = "lifecycle.menu.reason.busy";
    public const string ReasonSeparatorKey = "lifecycle.menu.reason.separator";
    public const string HeadNotOnBranchKey = "lifecycle.menu.reason.headNotOnBranch";
    public const string HeadWarningKey = "lifecycle.menu.warning.headNotOnBranch";
    public const string MergeOrRebaseKey = "lifecycle.menu.reason.mergeOrRebase";
    public const string UnavailableKey = "lifecycle.menu.reason.unavailable";
    public const string SparseMarkKey = "lifecycle.menu.mark.sparse";

    // ---------------------------------------------------------------- rename branch (7.8)
    public const string RenameBranchTitleKey = "lifecycle.renameBranch.title";
    public const string RenameBranchUpstreamKey = "lifecycle.renameBranch.upstream";
    public const string RenameBranchContinueKey = "lifecycle.renameBranch.continue";
    public const string RenameBranchInvalidKey = "lifecycle.renameBranch.invalid";
    public const string RenameBranchTakenKey = "lifecycle.renameBranch.taken";
    public const string RenameBranchBusyKey = "lifecycle.renameBranch.busy";
    public const string RenameBranchMovedKey = "lifecycle.renameBranch.moved";
    public const string RenameBranchDefaultKey = "lifecycle.renameBranch.default";
    public const string RenameBranchFailedKey = "lifecycle.renameBranch.failed";
    public const string RenameBranchRolledBackKey = "lifecycle.renameBranch.rolledBack";
    public const string RenameBranchRollbackFailedKey = "lifecycle.renameBranch.rollbackFailed";

    // ---------------------------------------------------------------- link an existing workspace (7.10)
    public const string LinkTitleKey = "lifecycle.link.title";
    public const string LinkMessageKey = "lifecycle.link.message";
    public const string LinkNoneKey = "lifecycle.link.none";
    public const string LinkLockedKey = "lifecycle.link.locked";
    public const string LinkPrunableKey = "lifecycle.link.prunable";
    public const string LinkDetachedLabelKey = "lifecycle.link.detachedLabel";
    public const string LinkDetachedWarningKey = "lifecycle.link.detachedWarning";
    public const string LinkFailedKey = "lifecycle.link.failed";

    // ---------------------------------------------------------------- sparse checkout in the new-workspace dialog (7.9)
    public const string SparseOptionKey = "lifecycle.sparse.option";
    public const string SparseLoadingKey = "lifecycle.sparse.loading";
    public const string SparseNoneSelectedKey = "lifecycle.sparse.noneSelected";
    public const string SparseNoDirectoriesKey = "lifecycle.sparse.noDirectories";
    public const string SparseLoadFailedKey = "lifecycle.sparse.loadFailed";
    public const string SparseCreateFailedKey = "lifecycle.sparse.createFailed";

    public static string LinkWorkspace => Loc.Instance[LinkWorkspaceKey];

    public static string RenameProject => Loc.Instance[RenameProjectKey];

    public static string ReplaceSession(string holderTitle) => Loc.Instance.Format(ReplaceSessionKey, holderTitle);

    public static string RenameBranch => Loc.Instance[RenameBranchKey];

    public static string ViewReadOnly => Loc.Instance[ViewReadOnlyKey];

    public static string ContinueSession => Loc.Instance[ContinueSessionKey];

    /// <summary>Why an entry waits for a session: its title and its busy sources, one line.</summary>
    public static string Busy(string title, IReadOnlyList<string> reasons) =>
        Loc.Instance.Format(BusyKey, title, string.Join(Loc.Instance[ReasonSeparatorKey], reasons));

    public static string HeadNotOnBranch => Loc.Instance[HeadNotOnBranchKey];

    public static string HeadWarning => Loc.Instance[HeadWarningKey];

    public static string MergeOrRebase => Loc.Instance[MergeOrRebaseKey];

    public static string Unavailable => Loc.Instance[UnavailableKey];

    public static string SparseMark => Loc.Instance[SparseMarkKey];

    public static string RenameBranchTitle => Loc.Instance[RenameBranchTitleKey];

    public static string RenameBranchUpstream(string branch, string remote) =>
        Loc.Instance.Format(RenameBranchUpstreamKey, branch, remote);

    public static string RenameBranchContinue => Loc.Instance[RenameBranchContinueKey];

    public static string RenameBranchInvalid(string name) => Loc.Instance.Format(RenameBranchInvalidKey, name);

    public static string RenameBranchTaken(string name) => Loc.Instance.Format(RenameBranchTakenKey, name);

    public static string RenameBranchBusy(IReadOnlyList<string> reasons) =>
        Loc.Instance.Format(RenameBranchBusyKey, string.Join("\n", reasons.Select(static reason => "• " + reason)));

    public static string RenameBranchMoved(string branch) => Loc.Instance.Format(RenameBranchMovedKey, branch);

    public static string RenameBranchDefault(string branch) => Loc.Instance.Format(RenameBranchDefaultKey, branch);

    public static string RenameBranchFailed(string reason) => Loc.Instance.Format(RenameBranchFailedKey, reason);

    public static string RenameBranchRolledBack(string oldName, string reason) =>
        Loc.Instance.Format(RenameBranchRolledBackKey, oldName, reason);

    public static string RenameBranchRollbackFailed(string oldName, string newName, string reason, string rollbackReason) =>
        Loc.Instance.Format(RenameBranchRollbackFailedKey, oldName, newName, reason, rollbackReason);

    public static string LinkTitle => Loc.Instance[LinkTitleKey];

    public static string LinkMessage => Loc.Instance[LinkMessageKey];

    public static string LinkNone => Loc.Instance[LinkNoneKey];

    public static string LinkLocked(string? reason) =>
        Loc.Instance.Format(LinkLockedKey, string.IsNullOrWhiteSpace(reason) ? "-" : reason);

    public static string LinkPrunable(string? reason) =>
        Loc.Instance.Format(LinkPrunableKey, string.IsNullOrWhiteSpace(reason) ? "-" : reason);

    public static string LinkDetachedLabel => Loc.Instance[LinkDetachedLabelKey];

    public static string LinkDetachedWarning(string path) => Loc.Instance.Format(LinkDetachedWarningKey, path);

    public static string LinkFailed(string reason) => Loc.Instance.Format(LinkFailedKey, reason);

    public static string SparseOption => Loc.Instance[SparseOptionKey];

    public static string SparseLoading => Loc.Instance[SparseLoadingKey];

    public static string SparseNoneSelected => Loc.Instance[SparseNoneSelectedKey];

    public static string SparseNoDirectories => Loc.Instance[SparseNoDirectoriesKey];

    public static string SparseLoadFailed(string reason) => Loc.Instance.Format(SparseLoadFailedKey, reason);

    public static string SparseCreateFailed(string branch, string reason) => Loc.Instance.Format(SparseCreateFailedKey, branch, reason);

    [ModuleInitializer]
    internal static void Initialize() => Register();

    /// <summary>Idempotent: registering the same key with the same texts twice is a no-op.</summary>
    internal static void Register()
    {
        Loc.Register(LinkWorkspaceKey, "关联已有工作区…", "Link existing workspace…");
        Loc.Register(RenameProjectKey, "重命名项目", "Rename project");
        Loc.Register(ReplaceSessionKey, "新会话(替换 {0})…", "New session (replace {0})…");
        Loc.Register(RenameBranchKey, "重命名分支…", "Rename branch…");
        Loc.Register(ViewReadOnlyKey, "查看(只读)", "View (read-only)");
        Loc.Register(ContinueSessionKey, "继续此会话…", "Continue this session…");

        Loc.Register(ReasonSeparatorKey, "、", ", ");
        Loc.Register(BusyKey, "“{0}”尚未空闲:{1}", "\"{0}\" is not idle: {1}");
        Loc.Register(HeadNotOnBranchKey, "HEAD 不在分支上(游离 HEAD 或尚无提交)", "HEAD is not on a branch (detached or no commits yet)");
        Loc.Register(HeadWarningKey, "HEAD 不在分支上:步骤提交与交接提交会失败", "HEAD is not on a branch: step commits and handover commits will fail");
        Loc.Register(MergeOrRebaseKey, "合并或变基进行中", "A merge or rebase is in progress");
        Loc.Register(UnavailableKey, "此操作现在不可用", "This action is not available right now");
        Loc.Register(SparseMarkKey, "稀疏", "sparse");

        Loc.Register(RenameBranchTitleKey, "重命名分支", "Rename branch");
        Loc.Register(
            RenameBranchUpstreamKey,
            "分支 {0} 跟踪远程 {1}。远程分支不随之改名,PR 关联可能丢失。",
            "Branch {0} tracks the remote {1}. The remote branch is not renamed with it, so a pull request may lose its link.");
        Loc.Register(RenameBranchContinueKey, "仍要改名", "Rename anyway");
        Loc.Register(RenameBranchInvalidKey, "“{0}”不是合法的分支名,未改名。", "\"{0}\" is not a valid branch name; nothing was renamed.");
        Loc.Register(RenameBranchTakenKey, "已有名为“{0}”的分支,未改名。", "A branch named \"{0}\" already exists; nothing was renamed.");
        Loc.Register(RenameBranchBusyKey, "现在不能重命名分支:\n{0}", "The branch cannot be renamed now:\n{0}");
        Loc.Register(RenameBranchMovedKey, "工作区当前不在分支 {0} 上,未改名。", "The workspace is no longer on branch {0}; nothing was renamed.");
        Loc.Register(RenameBranchDefaultKey, "{0} 是仓库的默认分支,不在这里改名。", "{0} is the repository's default branch; it is not renamed here.");
        Loc.Register(RenameBranchFailedKey, "重命名分支失败:{0}", "Renaming the branch failed: {0}");
        Loc.Register(
            RenameBranchRolledBackKey,
            "会话记录更新失败({1}),分支已改回 {0}。",
            "Updating the session records failed ({1}); the branch was renamed back to {0}.");
        Loc.Register(
            RenameBranchRollbackFailedKey,
            "会话记录更新失败({2}),且未能把分支改回 {0}({3})。分支现在叫 {1},会话记录仍写着 {0}。",
            "Updating the session records failed ({2}) and renaming the branch back to {0} failed too ({3}). The branch is now {1}; the session records still say {0}.");

        Loc.Register(LinkTitleKey, "关联已有工作区", "Link existing workspace");
        Loc.Register(LinkMessageKey, "选择一个 git 工作区,在其中新建会话:", "Choose a git worktree to start a session in:");
        Loc.Register(LinkNoneKey, "没有可关联的工作区:除根检出外的工作区都已有 Forge 会话。", "There is no worktree to link: every worktree other than the root checkout already has a Forge session.");
        Loc.Register(LinkLockedKey, "已锁定:{0}", "Locked: {0}");
        Loc.Register(LinkPrunableKey, "可清理(目录已失效):{0}", "Prunable (the directory is stale): {0}");
        Loc.Register(LinkDetachedLabelKey, "游离 HEAD", "detached HEAD");
        Loc.Register(
            LinkDetachedWarningKey,
            "{0} 处于游离 HEAD:在其中的步骤提交与交接提交会失败。仍要关联?",
            "{0} has a detached HEAD: step commits and handover commits in it will fail. Link it anyway?");
        Loc.Register(LinkFailedKey, "关联工作区失败:{0}", "Linking the workspace failed: {0}");

        Loc.Register(SparseOptionKey, "稀疏检出(只检出所选顶层目录)", "Sparse checkout (only the chosen top-level folders)");
        Loc.Register(SparseLoadingKey, "正在读取顶层目录…", "Reading the top-level folders…");
        Loc.Register(SparseNoneSelectedKey, "稀疏检出至少要选一个目录", "Choose at least one folder for a sparse checkout");
        Loc.Register(SparseNoDirectoriesKey, "起点提交没有顶层目录,不能稀疏检出", "The start commit has no top-level folders to check out sparsely");
        Loc.Register(SparseLoadFailedKey, "读取顶层目录失败:{0}", "Reading the top-level folders failed: {0}");
        Loc.Register(
            SparseCreateFailedKey,
            "未能以稀疏检出新建分支 {0} 的工作区(稀疏检出需要 git 2.25 及以上):{1}",
            "Creating a sparse workspace for branch {0} failed (a sparse checkout needs git 2.25 or later): {1}");
    }
}
