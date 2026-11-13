using System.Runtime.CompilerServices;

namespace Forge.App.Localization;

/// <summary>
/// The remove / restore / purge / close-project table (lifecycle 7.2-7.7): the menu entries, the row marks of recycled
/// and missing directories, the dialogs and every failure they report.
/// <para>Keys: <c>lifecycle.remove.*</c>, <c>lifecycle.restore.*</c>, <c>lifecycle.purge.*</c>,
/// <c>lifecycle.close.*</c>, <c>lifecycle.row.*</c> and <c>lifecycle.trash.*</c>. The trash's name follows the OS
/// (Recycle Bin / Trash) and is inserted into the texts that mention it.</para>
/// </summary>
internal static class RemovalStrings
{
    // ---------------------------------------------------------------- menu
    public const string MenuRemoveSessionKey = "lifecycle.remove.menuSession";
    public const string MenuRemoveWorkspaceKey = "lifecycle.remove.menuWorkspace";
    public const string MenuCloseProjectKey = "lifecycle.close.menu";
    public const string MenuRestoreKey = "lifecycle.restore.menu";
    public const string MenuRestoreWorkspaceKey = "lifecycle.restore.menuWorkspace";
    public const string MenuPurgeKey = "lifecycle.purge.menu";
    public const string MenuPurgeAllKey = "lifecycle.purge.menuAll";

    // ---------------------------------------------------------------- row marks
    public const string RowRecycledKey = "lifecycle.row.recycled";
    public const string RowMissingKey = "lifecycle.row.missing";
    public const string RowPrunableKey = "lifecycle.row.prunable";
    public const string RowUnregisteredKey = "lifecycle.row.unregistered";
    public const string RowUnverifiedKey = "lifecycle.row.unverified";
    public const string RowDirectoryRecycledKey = "lifecycle.row.directoryRecycled";
    public const string RowRemovedKey = "lifecycle.row.removed";

    // ---------------------------------------------------------------- trash name
    public const string TrashWindowsKey = "lifecycle.trash.windows";
    public const string TrashMacKey = "lifecycle.trash.mac";
    public const string TrashLinuxKey = "lifecycle.trash.linux";

    // ---------------------------------------------------------------- remove session / workspace
    public const string RemoveSessionTitleKey = "lifecycle.remove.sessionTitle";
    public const string RemoveWorkspaceTitleKey = "lifecycle.remove.workspaceTitle";
    public const string RemoveFailedTitleKey = "lifecycle.remove.failedTitle";
    public const string SessionQuestionKey = "lifecycle.remove.sessionQuestion";
    public const string SessionTrashKey = "lifecycle.remove.sessionTrash";
    public const string SessionKeptRootKey = "lifecycle.remove.sessionKeptRoot";
    public const string SessionKeptSharedKey = "lifecycle.remove.sessionKeptShared";
    public const string SessionMissingKey = "lifecycle.remove.sessionMissing";
    public const string WorkspaceTrashKey = "lifecycle.remove.workspaceTrash";
    public const string WorkspaceMissingKey = "lifecycle.remove.workspaceMissing";
    public const string WorkspaceActiveKey = "lifecycle.remove.workspaceActive";
    public const string ReasonsKey = "lifecycle.remove.reasons";
    public const string ChildrenKeyBase = "lifecycle.remove.children";
    public const string UnmergedForksKey = "lifecycle.remove.unmergedForks";
    public const string RemoveKey = "lifecycle.remove.remove";
    public const string RemoveAnywayKey = "lifecycle.remove.removeAnyway";
    public const string ArchiveAndRemoveKey = "lifecycle.remove.archiveAndRemove";
    public const string ArchiveAndRemoveAnywayKey = "lifecycle.remove.archiveAndRemoveAnyway";
    public const string BusyKey = "lifecycle.remove.busy";
    public const string TrashFailedKey = "lifecycle.remove.trashFailed";
    public const string RecordFailedKey = "lifecycle.remove.recordFailed";
    public const string RecordOnlyFailedKey = "lifecycle.remove.recordOnlyFailed";
    public const string ArchivedNotRemovedKey = "lifecycle.remove.archivedNotRemoved";
    public const string ArchiveFailedKey = "lifecycle.remove.archiveFailed";
    public const string ChangedKey = "lifecycle.remove.changed";
    public const string ReasonsGrewKey = "lifecycle.remove.reasonsGrew";
    public const string RootRefusedKey = "lifecycle.remove.rootRefused";
    public const string CheckFailedKey = "lifecycle.remove.checkFailed";
    public const string GuardPrunableKey = "lifecycle.remove.guardPrunable";
    public const string GuardUnregisteredKey = "lifecycle.remove.guardUnregistered";
    public const string GuardUnverifiedKey = "lifecycle.remove.guardUnverified";
    public const string WorktreeListEmptyKey = "lifecycle.remove.worktreeListEmpty";
    public const string GuardCheckFailedKey = "lifecycle.remove.guardCheckFailed";
    public const string GuardChangesUncheckedKey = "lifecycle.remove.guardChangesUnchecked";
    public const string CancelledKey = "lifecycle.remove.cancelled";
    public const string UnavailableKey = "lifecycle.remove.unavailable";

    // ---------------------------------------------------------------- restore
    public const string RestoreTitleKey = "lifecycle.restore.title";
    public const string RestoreFailedTitleKey = "lifecycle.restore.failedTitle";
    public const string BranchGoneKey = "lifecycle.restore.branchGone";
    public const string RebuildFailedKey = "lifecycle.restore.rebuildFailed";
    public const string RootMissingKey = "lifecycle.restore.rootMissing";
    public const string NotActivatedKey = "lifecycle.restore.notActivated";
    public const string NotActivatedCancelledKey = "lifecycle.restore.notActivatedCancelled";
    public const string RebuiltWorktreeKey = "lifecycle.restore.rebuiltWorktree";
    public const string WorkspaceRestoredKey = "lifecycle.restore.workspaceRestored";
    public const string WorkspacePresentKey = "lifecycle.restore.workspacePresent";

    // ---------------------------------------------------------------- purge
    public const string PurgeTitleKey = "lifecycle.purge.title";
    public const string PurgeQuestionKey = "lifecycle.purge.question";
    public const string PurgeAllQuestionKeyBase = "lifecycle.purge.questionAll";
    public const string PurgeConfirmKey = "lifecycle.purge.confirm";
    public const string PurgeRefusedKey = "lifecycle.purge.refused";
    public const string PurgePartialKey = "lifecycle.purge.partial";
    public const string PurgeNotRetriedKey = "lifecycle.purge.notRetried";
    public const string PurgeRetryFailedKey = "lifecycle.purge.retryFailed";
    public const string PurgeFailedKey = "lifecycle.purge.failed";

    // ---------------------------------------------------------------- close project
    public const string CloseTitleKey = "lifecycle.close.title";
    public const string CloseQuestionKey = "lifecycle.close.question";
    public const string CloseConfirmKey = "lifecycle.close.confirm";
    public const string CloseBusyKey = "lifecycle.close.busy";
    public const string CloseFailedKey = "lifecycle.close.failed";

    public static string MenuRemoveSession => Loc.Instance[MenuRemoveSessionKey];

    public static string MenuRemoveWorkspace => Loc.Instance[MenuRemoveWorkspaceKey];

    public static string MenuCloseProject => Loc.Instance[MenuCloseProjectKey];

    public static string MenuRestore => Loc.Instance[MenuRestoreKey];

    public static string MenuRestoreWorkspace => Loc.Instance[MenuRestoreWorkspaceKey];

    public static string MenuPurge => Loc.Instance[MenuPurgeKey];

    public static string MenuPurgeAll => Loc.Instance[MenuPurgeAllKey];

    public static string RowRecycled => Loc.Instance[RowRecycledKey];

    public static string RowMissing => Loc.Instance[RowMissingKey];

    public static string RowPrunable => Loc.Instance[RowPrunableKey];

    public static string RowUnregistered => Loc.Instance[RowUnregisteredKey];

    public static string RowUnverified => Loc.Instance[RowUnverifiedKey];

    public static string RowDirectoryRecycled => Loc.Instance[RowDirectoryRecycledKey];

    public static string RowRemoved => Loc.Instance[RowRemovedKey];

    /// <summary>The OS trash's name: Recycle Bin on Windows, Trash elsewhere.</summary>
    public static string TrashName => Loc.Instance[
        OperatingSystem.IsWindows() ? TrashWindowsKey : OperatingSystem.IsMacOS() ? TrashMacKey : TrashLinuxKey];

    public static string RemoveSessionTitle => Loc.Instance[RemoveSessionTitleKey];

    public static string RemoveWorkspaceTitle => Loc.Instance[RemoveWorkspaceTitleKey];

    public static string RemoveFailedTitle => Loc.Instance[RemoveFailedTitleKey];

    public static string SessionQuestion(string title) => Loc.Instance.Format(SessionQuestionKey, title);

    public static string SessionTrash(string directory) => Loc.Instance.Format(SessionTrashKey, directory, TrashName);

    public static string SessionKeptRoot => Loc.Instance[SessionKeptRootKey];

    public static string SessionKeptShared => Loc.Instance[SessionKeptSharedKey];

    public static string SessionMissing => Loc.Instance[SessionMissingKey];

    public static string WorkspaceTrash(string directory) => Loc.Instance.Format(WorkspaceTrashKey, directory, TrashName);

    public static string WorkspaceMissing => Loc.Instance[WorkspaceMissingKey];

    public static string WorkspaceActive(string title) => Loc.Instance.Format(WorkspaceActiveKey, title);

    public static string Reasons => Loc.Instance[ReasonsKey];

    public static string Children(int count) => Loc.Instance.FormatPlural(ChildrenKeyBase, count);

    public static string UnmergedForks(IEnumerable<string> branches) =>
        Loc.Instance.Format(UnmergedForksKey, string.Join(", ", branches));

    public static string Remove => Loc.Instance[RemoveKey];

    public static string RemoveAnyway => Loc.Instance[RemoveAnywayKey];

    public static string ArchiveAndRemove(string title) => Loc.Instance.Format(ArchiveAndRemoveKey, title);

    public static string ArchiveAndRemoveAnyway(string title) => Loc.Instance.Format(ArchiveAndRemoveAnywayKey, title);

    public static string Busy(string reasons) => Loc.Instance.Format(BusyKey, reasons);

    public static string TrashFailed(string reason) => Loc.Instance.Format(TrashFailedKey, TrashName, reason);

    public static string RecordFailed(string reason) => Loc.Instance.Format(RecordFailedKey, TrashName, reason);

    public static string RecordOnlyFailed(string reason) => Loc.Instance.Format(RecordOnlyFailedKey, reason);

    public static string ArchivedNotRemoved(string title, string reason) => Loc.Instance.Format(ArchivedNotRemovedKey, title, reason);

    public static string ArchiveFailed(string reason) => Loc.Instance.Format(ArchiveFailedKey, reason);

    public static string Changed => Loc.Instance[ChangedKey];

    public static string ReasonsGrew(IReadOnlyList<string> reasons) =>
        Loc.Instance.Format(ReasonsGrewKey, string.Join("\n", reasons.Select(static r => "• " + r)));

    public static string RootRefused(string directory) => Loc.Instance.Format(RootRefusedKey, directory);

    public static string CheckFailed(string reason) => Loc.Instance.Format(CheckFailedKey, reason);

    public static string GuardPrunable(string? reason) => Loc.Instance.FormatReason(GuardPrunableKey, reason);

    public static string GuardUnregistered => Loc.Instance[GuardUnregisteredKey];

    public static string GuardUnverified(string? reason) => Loc.Instance.FormatReason(GuardUnverifiedKey, reason);

    public static string WorktreeListEmpty => Loc.Instance[WorktreeListEmptyKey];

    /// <summary>The guard's git check failed outright: neither changes nor commits could be checked.</summary>
    public static string GuardCheckFailed(string? reason) => Loc.Instance.FormatReason(GuardCheckFailedKey, reason);

    /// <summary>Git could not run inside the worktree, but its commits were checked from the repository.</summary>
    public static string GuardChangesUnchecked(string? reason) => Loc.Instance.FormatReason(GuardChangesUncheckedKey, reason);

    public static string Cancelled => Loc.Instance[CancelledKey];

    public static string Unavailable => Loc.Instance[UnavailableKey];

    public static string RestoreTitle => Loc.Instance[RestoreTitleKey];

    public static string RestoreFailedTitle => Loc.Instance[RestoreFailedTitleKey];

    public static string BranchGone(string branch) => Loc.Instance.Format(BranchGoneKey, branch);

    public static string RebuildFailed(string directory, string reason) => Loc.Instance.Format(RebuildFailedKey, directory, reason);

    public static string RootMissing(string directory) => Loc.Instance.Format(RootMissingKey, directory);

    public static string NotActivated(string reason) => Loc.Instance.Format(NotActivatedKey, reason);

    public static string NotActivatedCancelled => Loc.Instance[NotActivatedCancelledKey];

    public static string RebuiltWorktree(string directory) => Loc.Instance.Format(RebuiltWorktreeKey, directory);

    public static string WorkspaceRestored(string directory) => Loc.Instance.Format(WorkspaceRestoredKey, directory);

    public static string WorkspacePresent(string directory) => Loc.Instance.Format(WorkspacePresentKey, directory);

    public static string PurgeTitle => Loc.Instance[PurgeTitleKey];

    public static string PurgeQuestion(string title) => Loc.Instance.Format(PurgeQuestionKey, title);

    public static string PurgeAllQuestion(int count) => Loc.Instance.FormatPlural(PurgeAllQuestionKeyBase, count);

    public static string PurgeConfirm => Loc.Instance[PurgeConfirmKey];

    public static string PurgeRefused(string title, IEnumerable<string> inheriting) =>
        Loc.Instance.Format(PurgeRefusedKey, title, string.Join(", ", inheriting));

    public static string PurgePartial(string reasons) => Loc.Instance.Format(PurgePartialKey, reasons);

    public static string PurgeNotRetried(string reason) => Loc.Instance.Format(PurgeNotRetriedKey, reason);

    public static string PurgeRetryFailed(string reasons) => Loc.Instance.Format(PurgeRetryFailedKey, reasons);

    public static string PurgeFailed(string title, string reason) => Loc.Instance.Format(PurgeFailedKey, title, reason);

    public static string CloseTitle => Loc.Instance[CloseTitleKey];

    public static string CloseQuestion(string name) => Loc.Instance.Format(CloseQuestionKey, name);

    public static string CloseConfirm => Loc.Instance[CloseConfirmKey];

    public static string CloseBusy(string reasons) => Loc.Instance.Format(CloseBusyKey, reasons);

    public static string CloseFailed(string reason) => Loc.Instance.Format(CloseFailedKey, reason);

    [ModuleInitializer]
    internal static void Initialize() => Register();

    /// <summary>Idempotent: registering the same key with the same texts twice is a no-op.</summary>
    internal static void Register()
    {
        Loc.Register(MenuRemoveSessionKey, "移除会话…", "Remove session…");
        Loc.Register(MenuRemoveWorkspaceKey, "移除工作区…", "Remove workspace…");
        Loc.Register(MenuCloseProjectKey, "关闭项目…", "Close project…");
        Loc.Register(MenuRestoreKey, "恢复…", "Restore…");
        Loc.Register(MenuRestoreWorkspaceKey, "恢复工作区…", "Restore workspace…");
        Loc.Register(MenuPurgeKey, "彻底删除…", "Delete permanently…");
        Loc.Register(MenuPurgeAllKey, "彻底删除全部会话…", "Delete all sessions permanently…");

        Loc.Register(RowRecycledKey, "已回收", "recycled");
        Loc.Register(RowMissingKey, "目录缺失", "directory missing");
        Loc.Register(RowPrunableKey, "git 标记为可清理", "prunable in git");
        Loc.Register(RowUnregisteredKey, "git 未登记", "not a git worktree");
        Loc.Register(RowUnverifiedKey, "git 状态未知", "git state unknown");
        Loc.Register(RowDirectoryRecycledKey, "目录已回收", "directory recycled");
        Loc.Register(RowRemovedKey, "已移除", "removed");

        Loc.Register(TrashWindowsKey, "回收站", "Recycle Bin");
        Loc.Register(TrashMacKey, "废纸篓", "Trash");
        Loc.Register(TrashLinuxKey, "回收站", "Trash");

        Loc.Register(RemoveSessionTitleKey, "移除会话", "Remove session");
        Loc.Register(RemoveWorkspaceTitleKey, "移除工作区", "Remove workspace");
        Loc.Register(RemoveFailedTitleKey, "移除失败", "Removal failed");
        Loc.Register(SessionQuestionKey, "移除会话“{0}”?", "Remove the session \"{0}\"?");
        Loc.Register(
            SessionTrashKey,
            "这是该目录的最后一个会话,目录 {0} 将进{1}。",
            "This is the directory's last session: {0} goes to the {1}.");
        Loc.Register(
            SessionKeptRootKey,
            "该目录不归 Forge 回收(仓库根检出或项目根):只移除会话记录,目录原样保留。",
            "Forge does not recycle this directory (the repository checkout or the project root): only the session record is removed, the directory stays.");
        Loc.Register(
            SessionKeptSharedKey,
            "该目录还有其它会话:目录原样保留,只移除此会话记录。",
            "Other sessions still use this directory: it stays, only this session's record is removed.");
        Loc.Register(
            SessionMissingKey,
            "目录已不存在:将清理 git 登记并移除会话记录。",
            "The directory is gone: its git registration is cleaned up and the session record is removed.");
        Loc.Register(
            WorkspaceTrashKey,
            "目录 {0} 将进{1},其上所有会话(含已归档)一并移除;分支保留。",
            "{0} goes to the {1} and all of its sessions (archived ones included) are removed; the branch is kept.");
        Loc.Register(
            WorkspaceMissingKey,
            "目录已不存在,将清理 git 登记并移除其会话记录。",
            "The directory no longer exists: its git registration is cleaned up and its session records are removed.");
        Loc.Register(WorkspaceActiveKey, "活动会话“{0}”会先归档。", "The active session \"{0}\" is archived first.");
        Loc.Register(ReasonsKey, "移除会丢失以下内容:", "Removing it discards:");
        Loc.Register(ChildrenKeyBase + ".one", "将一并移除 {0} 个子 Agent 会话。", "{0} sub-agent session is removed with it.");
        Loc.Register(ChildrenKeyBase + ".other", "将一并移除 {0} 个子 Agent 会话。", "{0} sub-agent sessions are removed with it.");
        Loc.Register(UnmergedForksKey, "以下分叉尚未并回:{0}", "These forks are not merged back yet: {0}");
        Loc.Register(RemoveKey, "移除", "Remove");
        Loc.Register(RemoveAnywayKey, "仍要移除", "Remove anyway");
        Loc.Register(ArchiveAndRemoveKey, "归档“{0}”并移除", "Archive \"{0}\" and remove");
        Loc.Register(ArchiveAndRemoveAnywayKey, "归档“{0}”并仍要移除", "Archive \"{0}\" and remove anyway");
        Loc.Register(BusyKey, "现在不能移除:{0}", "Cannot remove it now: {0}");
        Loc.Register(TrashFailedKey, "移进{0}失败,未做任何改动:{1}", "Moving it to the {0} failed; nothing was changed: {1}");
        Loc.Register(
            RecordFailedKey,
            "目录已进{0},但会话记录未更新:{1}。下次启动会自动补写。",
            "The directory is in the {0}, but the session record was not updated: {1}. The next start writes it.");
        Loc.Register(RecordOnlyFailedKey, "会话记录未更新:{0}", "The session record was not updated: {0}");
        Loc.Register(ArchivedNotRemovedKey, "已归档“{0}”,目录未移除:{1}", "\"{0}\" was archived, but the directory was not removed: {1}");
        Loc.Register(ArchiveFailedKey, "归档失败,未移除任何东西:{0}", "Archiving failed; nothing was removed: {0}");
        Loc.Register(
            ChangedKey,
            "打开对话框以来会话或工作区已变化,未做任何改动,请重试。",
            "The session or workspace changed since the dialog opened; nothing was changed. Try again.");
        Loc.Register(
            ReasonsGrewKey,
            "对话框打开期间出现了新的不能移除的原因,未做任何改动。请重新移除以查看:\n{0}",
            "New reasons against removing it appeared while the dialog was open; nothing was changed. Remove it again to review them:\n{0}");
        Loc.Register(
            RootRefusedKey,
            "{0} 是项目根目录,不能作为工作区移除。",
            "{0} is the project root; it cannot be removed as a workspace.");
        Loc.Register(CheckFailedKey, "无法检查能否移除:{0}", "Could not check whether it can be removed: {0}");
        Loc.Register(
            GuardPrunableKey,
            "git 已将此工作区标记为可清理({0}),无法检查其中未提交的改动",
            "Git marks this workspace prunable ({0}); its uncommitted changes cannot be checked");
        Loc.Register(
            GuardUnregisteredKey,
            "git 工作区列表中没有此目录,无法检查其中未提交的改动",
            "Git does not list this folder as a worktree; its uncommitted changes cannot be checked");
        Loc.Register(
            GuardUnverifiedKey,
            "无法读取 git 工作区列表({0}),未能确认此目录仍是 git 工作区",
            "Could not read Git's worktree list ({0}); this folder could not be confirmed as a worktree");
        Loc.Register(WorktreeListEmptyKey, "git 未列出任何工作区", "Git listed no worktrees");
        Loc.Register(
            GuardCheckFailedKey,
            "无法检查其中未提交的改动与未合入的提交:{0}",
            "Its uncommitted changes and unmerged commits could not be checked: {0}");
        Loc.Register(
            GuardChangesUncheckedKey,
            "无法检查其中未提交的改动:{0}(提交已从仓库根目录检查)",
            "Its uncommitted changes could not be checked: {0} (its commits were checked from the repository)");
        Loc.Register(CancelledKey, "已取消", "cancelled");
        Loc.Register(UnavailableKey, "此窗口无法显示确认对话框,未做任何改动。", "This window cannot show the confirmation dialog; nothing was changed.");

        Loc.Register(RestoreTitleKey, "恢复", "Restore");
        Loc.Register(RestoreFailedTitleKey, "恢复失败", "Restore failed");
        Loc.Register(BranchGoneKey, "分支 {0} 已不存在,无法恢复目录", "The branch {0} no longer exists; the directory cannot be restored");
        Loc.Register(RebuildFailedKey, "无法重建目录 {0}:{1}", "Could not rebuild {0}: {1}");
        Loc.Register(
            RootMissingKey,
            "目录 {0} 不归 Forge 重建(仓库根检出或项目根),请先让它重新可用。",
            "Forge does not rebuild {0} (the repository checkout or the project root); make it available again first.");
        Loc.Register(NotActivatedKey, "目录已重建,会话未恢复:{0}。可再次“恢复…”重试。", "The directory was rebuilt, but the session was not restored: {0}. Use \"Restore…\" to try again.");
        Loc.Register(
            NotActivatedCancelledKey,
            "目录已重建,会话未恢复。可再次“恢复…”重试。",
            "The directory was rebuilt, but the session was not restored. Use \"Restore…\" to try again.");
        Loc.Register(
            RebuiltWorktreeKey,
            "已按完整检出重建目录 {0};原稀疏检出配置无法恢复。",
            "{0} was rebuilt as a full checkout; its sparse checkout settings cannot be restored.");
        Loc.Register(
            WorkspaceRestoredKey,
            "目录 {0} 已重建(完整检出,稀疏配置无法恢复);其会话仍为已移除,请逐个“恢复…”。",
            "{0} was rebuilt (a full checkout; sparse settings cannot be restored). Its sessions stay removed: restore them one by one.");
        Loc.Register(
            WorkspacePresentKey,
            "目录 {0} 已存在,无需重建;其会话请逐个“恢复…”。",
            "{0} already exists and needs no rebuild; restore its sessions one by one.");

        Loc.Register(PurgeTitleKey, "彻底删除", "Delete permanently");
        Loc.Register(
            PurgeQuestionKey,
            "彻底删除“{0}”及其子会话的全部记录?此操作不可撤销。分支与回收站里的目录不受影响。",
            "Delete every record of \"{0}\" and its sub-agent sessions? This cannot be undone. Branches and directories in the trash are not touched.");
        Loc.Register(
            PurgeAllQuestionKeyBase + ".one",
            "彻底删除该行下 {0} 个已移除会话的全部记录?此操作不可撤销。",
            "Delete every record of the {0} removed session here? This cannot be undone.");
        Loc.Register(
            PurgeAllQuestionKeyBase + ".other",
            "彻底删除该行下 {0} 个已移除会话的全部记录?此操作不可撤销。",
            "Delete every record of the {0} removed sessions here? This cannot be undone.");
        Loc.Register(PurgeConfirmKey, "彻底删除", "Delete permanently");
        Loc.Register(PurgeRefusedKey, "“{0}”被以下会话继承,未删除:{1}", "\"{0}\" was kept: these sessions inherit from it: {1}");
        Loc.Register(
            PurgePartialKey,
            "记录已删除,部分附属文件未清理,下次启动会重试:{0}",
            "The records were deleted, but some attached files were not cleaned up; the next start tries again: {0}");
        Loc.Register(PurgeNotRetriedKey, "无法记下待重试的清理,不会自动重试:{0}", "the cleanup could not be noted for a retry and will not be retried: {0}");
        Loc.Register(
            PurgeRetryFailedKey,
            "此前彻底删除留下的附属文件仍未清理,下次启动再试:{0}",
            "Files left by an earlier permanent delete are still not cleaned up; the next start tries again: {0}");
        Loc.Register(PurgeFailedKey, "无法彻底删除“{0}”:{1}", "Could not delete \"{0}\": {1}");

        Loc.Register(CloseTitleKey, "关闭项目", "Close project");
        Loc.Register(
            CloseQuestionKey,
            "关闭项目“{0}”?只取消登记,不删除任何文件;重新添加同一路径后会话原样回来。",
            "Close the project \"{0}\"? It is only unregistered and no file is deleted; adding the same path again brings its sessions back.");
        Loc.Register(CloseConfirmKey, "关闭项目", "Close project");
        Loc.Register(CloseBusyKey, "现在不能关闭项目:{0}", "Cannot close the project now: {0}");
        Loc.Register(CloseFailedKey, "无法关闭项目:{0}", "Could not close the project: {0}");
    }
}
