using System.Runtime.CompilerServices;

namespace Forge.App.Localization;

/// <summary>
/// The session and workspace lifecycle table: the read-only view of a non-active session, the turn-start checks, the
/// occupied-workspace dialog, the single-instance notice and the startup storage notices.
/// <para>Keys: <c>lifecycle.*</c>. Each key has a Chinese and an English text; parameterized texts are typed accessors
/// formatted through <see cref="Loc.Format"/>, parameterless ones properties that read <see cref="Loc.Instance"/>. Texts a
/// transcript keeps (notices) are <see cref="LocText"/> so a language switch re-renders them.</para>
/// </summary>
internal static class LifecycleStrings
{
    // ---------------------------------------------------------------- single instance
    public const string AlreadyRunningTitleKey = "lifecycle.alreadyRunning.title";
    public const string AlreadyRunningMessageKey = "lifecycle.alreadyRunning.message";

    // ---------------------------------------------------------------- read-only view
    public const string ReadOnlyArchivedKey = "lifecycle.readOnly.archived";
    public const string ReadOnlyDeletedKey = "lifecycle.readOnly.deleted";
    public const string ContinueSessionKey = "lifecycle.continueSession";
    public const string RestoreSessionKey = "lifecycle.restoreSession";
    public const string DirectoryMissingKey = "lifecycle.directoryMissing";
    public const string WorktreePrunableKey = "lifecycle.worktreePrunable";
    public const string WorktreeUnregisteredKey = "lifecycle.worktreeUnregistered";
    public const string ContinueBlockedKey = "lifecycle.continueBlocked";
    public const string PendingComposerKeyBase = "lifecycle.pendingComposer";
    public const string ContinueFailedKey = "lifecycle.continueFailed";
    public const string StartupFailedKey = "lifecycle.startupFailed";
    public const string TurnCheckFailedKey = "lifecycle.turnCheckFailed";

    // ---------------------------------------------------------------- turn-start checks
    public const string TakenOverKey = "lifecycle.turn.takenOver";
    public const string MergeConflictPendingKey = "lifecycle.turn.mergeConflict";
    public const string WorkspaceOperatingKey = "lifecycle.turn.workspaceOperating";

    // ---------------------------------------------------------------- occupied workspace
    public const string OccupiedTitleKey = "lifecycle.occupied.title";
    public const string OccupiedMessageKey = "lifecycle.occupied.message";
    public const string ViewHolderKey = "lifecycle.occupied.viewHolder";

    // ---------------------------------------------------------------- startup notices
    public const string NoticeTitleKey = "lifecycle.notice.title";
    public const string DedupeArchivedKeyBase = "lifecycle.notice.dedupeArchived";
    public const string HealDeletedKeyBase = "lifecycle.notice.healDeleted";

    public static string AlreadyRunningTitle => Loc.Instance[AlreadyRunningTitleKey];

    public static string AlreadyRunningMessage => Loc.Instance[AlreadyRunningMessageKey];

    public static string ReadOnlyArchived => Loc.Instance[ReadOnlyArchivedKey];

    public static string ReadOnlyDeleted => Loc.Instance[ReadOnlyDeletedKey];

    public static string ContinueSession => Loc.Instance[ContinueSessionKey];

    public static string RestoreSession => Loc.Instance[RestoreSessionKey];

    public static string DirectoryMissing => Loc.Instance[DirectoryMissingKey];

    /// <summary>The directory is there but git marks its worktree prunable (its <c>.git</c> link is broken); git's reason, if any.</summary>
    public static string WorktreePrunable(string? reason) =>
        Loc.Instance.Format(WorktreePrunableKey, string.IsNullOrWhiteSpace(reason) ? "-" : reason);

    /// <summary>The directory is there but the repository's worktree list does not name it.</summary>
    public static string WorktreeUnregistered => Loc.Instance[WorktreeUnregisteredKey];

    public static string ContinueBlocked(string reason) => Loc.Instance.Format(ContinueBlockedKey, reason);

    /// <summary>A status-bar problem, so it re-renders on a language switch.</summary>
    public static LocText ContinueFailed => new(ContinueFailedKey);

    public static LocText StartupFailed(string reason) => new(StartupFailedKey, reason);

    public static LocText TurnCheckFailed(string reason) => new(TurnCheckFailedKey, reason);

    public static string PendingComposer(int count) =>
        Loc.Instance.Format(LocText.PluralKey(PendingComposerKeyBase, count), TextFormat.Number(count));

    /// <summary>Kept by the transcript, so it re-renders on a language switch.</summary>
    public static LocText TakenOver => new(TakenOverKey);

    /// <summary>Kept by the transcript, so it re-renders on a language switch.</summary>
    public static LocText MergeConflictPending => new(MergeConflictPendingKey);

    /// <summary>Kept by the transcript, so it re-renders on a language switch.</summary>
    public static LocText WorkspaceOperating => new(WorkspaceOperatingKey);

    public static string OccupiedTitle => Loc.Instance[OccupiedTitleKey];

    public static string OccupiedMessage(string holderTitle) => Loc.Instance.Format(OccupiedMessageKey, holderTitle);

    public static string ViewHolder(string holderTitle) => Loc.Instance.Format(ViewHolderKey, holderTitle);

    public static string NoticeTitle => Loc.Instance[NoticeTitleKey];

    public static string DedupeArchived(long count) =>
        Loc.Instance.Format(LocText.PluralKey(DedupeArchivedKeyBase, count), TextFormat.Number(count));

    public static string HealDeleted(long count) =>
        Loc.Instance.Format(LocText.PluralKey(HealDeletedKeyBase, count), TextFormat.Number(count));

    [ModuleInitializer]
    internal static void Initialize() => Register();

    /// <summary>Idempotent: registering the same key with the same texts twice is a no-op.</summary>
    internal static void Register()
    {
        Loc.Register(AlreadyRunningTitleKey, "Forge", "Forge");
        Loc.Register(AlreadyRunningMessageKey, "Forge 已在运行。", "Forge is already running.");

        Loc.Register(ReadOnlyArchivedKey, "只读查看:这是历史会话。", "Read-only: this is a past session.");
        Loc.Register(ReadOnlyDeletedKey, "只读查看:这是已移除的会话。", "Read-only: this session was removed.");
        Loc.Register(ContinueSessionKey, "继续此会话", "Continue this session");
        Loc.Register(RestoreSessionKey, "恢复…", "Restore…");
        Loc.Register(DirectoryMissingKey, "目录缺失", "Folder missing");
        Loc.Register(WorktreePrunableKey, "git 已将此工作区标记为可清理:{0}", "Git marks this workspace prunable: {0}");
        Loc.Register(WorktreeUnregisteredKey, "git 工作区列表中没有此目录", "Git does not list this folder as a worktree");
        Loc.Register(ContinueBlockedKey, "继续此会话〔{0}〕", "Continue this session ({0})");
        Loc.Register(ContinueFailedKey, "无法继续此会话:它已被移除或不存在。", "This session cannot be continued: it was removed or no longer exists.");
        Loc.Register(TurnCheckFailedKey, "回合前无法确认会话状态,已照常开始:{0}", "Could not check the session's status before the turn; it started anyway: {0}");
        Loc.Register(StartupFailedKey, "启动时的会话整理未完成:{0}", "The startup session tidy-up did not finish: {0}");
        Loc.Register(PendingComposerKeyBase + ".one", "{0} 条未发送的消息已保留,继续此会话后可发送。", "{0} unsent message is kept; continue this session to send it.");
        Loc.Register(PendingComposerKeyBase + ".other", "{0} 条未发送的消息已保留,继续此会话后可发送。", "{0} unsent messages are kept; continue this session to send them.");

        Loc.Register(TakenOverKey, "该会话已被接手,已转为只读。", "This session was taken over; it is now read-only.");
        Loc.Register(MergeConflictPendingKey, "合并冲突待处理,解决后再继续。", "A merge conflict is pending; resolve it first.");
        Loc.Register(WorkspaceOperatingKey, "此工作区正在执行合并等操作,完成后再继续。", "This workspace is busy with an operation such as a merge; continue once it is done.");

        Loc.Register(OccupiedTitleKey, "工作区已被占用", "Workspace in use");
        Loc.Register(OccupiedMessageKey, "该工作区已有活动会话“{0}”。一个工作区同时只能有一个活动会话。", "The workspace already has an active session, \"{0}\". A workspace has one active session at a time.");
        Loc.Register(ViewHolderKey, "查看 {0}", "View {0}");

        Loc.Register(NoticeTitleKey, "会话已整理", "Sessions tidied up");
        Loc.Register(DedupeArchivedKeyBase + ".one", "同一工作区曾有多个活动会话,已将 {0} 个较旧的会话移入历史。", "A workspace had several active sessions; {0} older session was moved to history.");
        Loc.Register(DedupeArchivedKeyBase + ".other", "同一工作区曾有多个活动会话,已将 {0} 个较旧的会话移入历史。", "Workspaces had several active sessions; {0} older sessions were moved to history.");
        Loc.Register(HealDeletedKeyBase + ".one", "{0} 个会话的工作区目录已不存在,已标为已移除。", "The folder of {0} session no longer exists; it was marked as removed.");
        Loc.Register(HealDeletedKeyBase + ".other", "{0} 个会话的工作区目录已不存在,已标为已移除。", "The folders of {0} sessions no longer exist; they were marked as removed.");
    }
}
