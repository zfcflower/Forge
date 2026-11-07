using System.Runtime.CompilerServices;

namespace Forge.App.Localization;

/// <summary>
/// The inspector's table: the files panel, the preview, the changes document, TODO, review and the spec tree.
/// <para>Keys: <c>files.*</c>, <c>changes.*</c>, <c>todo.*</c>, <c>review.*</c>, <c>spec.*</c>. Each key has a Chinese and an English text; parameterized texts are exposed as typed accessor
/// methods (formatted through <see cref="Loc.Format"/>), parameterless ones as properties that read
/// <see cref="Loc.Instance"/>, so callers never cache a resolved string across a language switch.</para>
/// <para>Texts that are stored now and shown later (a read failure, an unreadable diff, a git status description) are
/// returned as a <see cref="LocText"/> and resolved when they are displayed: paths and exception messages are
/// <see cref="LocText.Literal"/> arguments and are never translated. Count-dependent nouns have a <c>.one</c>/<c>.other</c>
/// pair (<c>*Base</c> here), so English says "1 file" and "2 files" while Chinese keeps one wording; a format string with
/// placeholders is a <c>*Pattern</c>. Numbers are pre-formatted by <see cref="TextFormat"/> (grouped counts, ungrouped
/// line numbers), never by the caller.</para>
/// </summary>
internal static class InspectorStrings
{
    /// <summary>The preview tab title's leading mark; a symbol, so it is never translated.</summary>
    public const string FileTabGlyph = "▤";

    // ---------------------------------------------------------------- files panel
    public const string FilesAll = "files.all";
    public const string FilesAllName = "files.all.name";
    public const string FilesChanged = "files.changed";
    public const string FilesChangedName = "files.changed.name";
    public const string FilesChangedListName = "files.changedList.name";
    public const string FilesTreeName = "files.tree.name";
    public const string FilesName = "files.name";
    public const string FilesLoading = "files.loading";
    public const string FilesOpenInNewTab = "files.openInNewTab";
    public const string FilesRefresh = "files.refresh";
    public const string FilesRefreshName = "files.refresh.name";
    public const string FilesNoSession = "files.noSession";
    public const string FilesSummaryPattern = "files.summary";
    public const string FilesSummaryChangesBase = "files.summary.changes";
    public const string FilesSummaryFilesBase = "files.summary.files";
    public const string FilesTabTitlePattern = "files.tabTitle";
    public const string FilesMissing = "files.missing";
    public const string FilesTooLarge = "files.tooLarge";
    public const string FilesTruncated = "files.truncated";
    public const string FilesWatchUnavailable = "files.watchUnavailable";
    public const string FilesStatusWorkingTree = "files.status.workingTree";
    public const string FilesStatusStaged = "files.status.staged";
    public const string FilesStatusStagedAndUnstaged = "files.status.stagedAndUnstaged";
    public const string FilesBinary = "files.binary";
    public const string FilesUnsaved = "files.unsaved";
    public const string FilesSaveFailed = "files.saveFailed";
    public const string FilesLspUnavailable = "files.lsp.unavailable";
    public const string FilesLspStopped = "files.lsp.stopped";
    public const string FilesSave = "files.save";
    public const string FilesWrap = "files.wrap";
    public const string FilesEdit = "files.edit";
    public const string FilesCopy = "files.copy";
    public const string FilesCopyFailed = "files.copyFailed";
    public const string FilesBusy = "files.busy";
    public const string FilesDiscardTitle = "files.discard.title";
    public const string FilesDiscardMessage = "files.discard.message";
    public const string FilesDiscardConfirm = "files.discard.confirm";
    public const string FilesArchiveTitle = "files.archive.title";
    public const string FilesArchiveMessage = "files.archive.message";
    public const string FilesArchiveConfirm = "files.archive.confirm";

    // ---------------------------------------------------------------- changes document
    public const string ChangesByTurn = "changes.byTurn";
    public const string ChangesUncommitted = "changes.uncommitted";
    public const string ChangesNoSession = "changes.noSession";
    public const string ChangesNoChanges = "changes.noChanges";
    public const string ChangesNoTextualChange = "changes.noTextualChange";
    public const string ChangesRefresh = "changes.refresh";
    public const string ChangesRefreshName = "changes.refresh.name";
    public const string ChangesRowsName = "changes.rows.name";
    public const string ChangesSummaryPattern = "changes.summary";
    public const string ChangesSummaryFilesBase = "changes.summary.files";
    public const string ChangesTurnsName = "changes.turns.name";
    public const string ChangesTurnPattern = "changes.turn";
    public const string ChangesFileHeaderPattern = "changes.fileHeader";

    // ---------------------------------------------------------------- todo
    public const string TodoNoSession = "todo.noSession";
    public const string TodoEmpty = "todo.empty";
    public const string TodoSummaryPattern = "todo.summary";
    public const string TodoNewTaskPlaceholder = "todo.newTaskPlaceholder";
    public const string TodoNewTaskName = "todo.newTask.name";
    public const string TodoAdd = "todo.add";
    public const string TodoDone = "todo.done";
    public const string TodoMoveUp = "todo.moveUp";
    public const string TodoMoveDown = "todo.moveDown";
    public const string TodoDelete = "todo.delete";
    public const string TodoClearDone = "todo.clearDone";

    // ---------------------------------------------------------------- review
    public const string ReviewNoSession = "review.noSession";
    public const string ReviewSummaryBase = "review.summary";
    public const string ReviewLinePattern = "review.line";
    public const string ReviewLineRangePattern = "review.lineRange";
    public const string ReviewStatusPattern = "review.status";
    public const string ReviewResolved = "review.resolved";
    public const string ReviewUnresolved = "review.unresolved";
    public const string ReviewSent = "review.sent";
    public const string ReviewDraft = "review.draft";

    // ---------------------------------------------------------------- spec tree
    public const string SpecNoProject = "spec.noProject";
    public const string SpecCountBase = "spec.count";
    public const string SpecRefresh = "spec.refresh";
    public const string SpecRefreshName = "spec.refresh.name";
    public const string SpecTreeName = "spec.tree.name";

    /// <summary>Registers this table before any code of the assembly runs (module initializer, AOT safe).</summary>
    [ModuleInitializer]
    internal static void Initialize() => Register();

    /// <summary>Idempotent: registering the same key with the same texts twice is a no-op.</summary>
    internal static void Register()
    {
        Loc.Register(FilesAll, "全部", "All");
        Loc.Register(FilesAllName, "全部文件", "All files");
        Loc.Register(FilesChanged, "已改动", "Changed");
        Loc.Register(FilesChangedName, "已改动文件", "Changed files");
        Loc.Register(FilesChangedListName, "已改动文件列表", "Changed files list");
        Loc.Register(FilesTreeName, "全部文件树", "All files tree");
        Loc.Register(FilesName, "文件", "Files");
        Loc.Register(FilesLoading, "读取中…", "Loading…");
        Loc.Register(FilesOpenInNewTab, "在新标签页打开", "Open in new tab");
        Loc.Register(FilesRefresh, "刷新", "Refresh");
        Loc.Register(FilesRefreshName, "刷新文件", "Refresh files");
        Loc.Register(FilesNoSession, "没有打开的会话", "No open session");
        Loc.Register(FilesSummaryPattern, "{0} · {1}", "{0} · {1}");
        Loc.Register(FilesSummaryChangesBase + ".one", "{0} 个改动", "{0} change");
        Loc.Register(FilesSummaryChangesBase + ".other", "{0} 个改动", "{0} changes");
        Loc.Register(FilesSummaryFilesBase + ".one", "{0} 个文件", "{0} file");
        Loc.Register(FilesSummaryFilesBase + ".other", "{0} 个文件", "{0} files");
        Loc.Register(FilesTabTitlePattern, "{0} {1}", "{0} {1}");
        Loc.Register(FilesMissing, "文件不存在：{0}", "File not found: {0}");
        Loc.Register(FilesTooLarge, "文件过大，无法显示：{0} 字节", "File too large to display: {0} bytes");
        Loc.Register(FilesTruncated, "只显示前 {0} 行。", "Only the first {0} lines are shown.");
        Loc.Register(FilesWatchUnavailable, "文件监视不可用：{0}", "File watching unavailable: {0}");
        Loc.Register(FilesStatusWorkingTree, "工作区", "Working tree");
        Loc.Register(FilesStatusStaged, "已暂存", "Staged");
        Loc.Register(FilesStatusStagedAndUnstaged, "已暂存并有未暂存改动", "Staged and unstaged");
        Loc.Register(FilesBinary, "不是文本文件：{0}", "Not a text file: {0}");
        Loc.Register(FilesUnsaved, "有未保存的修改，已取消重新加载。", "Unsaved edits; reload cancelled.");
        Loc.Register(FilesSaveFailed, "保存失败：{0}", "Save failed: {0}");
        Loc.Register(FilesLspUnavailable, "语言服务不可用：{0}", "Language server unavailable: {0}");
        Loc.Register(FilesLspStopped, "语言服务未能启动，编辑不受影响。", "The language server could not start; editing is unaffected.");
        Loc.Register(FilesSave, "保存", "Save");
        Loc.Register(FilesWrap, "换行", "Wrap");
        Loc.Register(FilesEdit, "编辑", "Edit");
        Loc.Register(FilesCopy, "复制", "Copy");
        Loc.Register(FilesCopyFailed, "复制失败：{0}", "Copy failed: {0}");
        Loc.Register(FilesBusy, "文件正在保存，请稍后再试", "The file is being saved, try again in a moment");
        Loc.Register(FilesDiscardTitle, "放弃未保存的修改？", "Discard unsaved edits?");
        Loc.Register(FilesDiscardMessage, "{0} 有未保存的修改，继续将丢失这些修改。", "{0} has unsaved edits that will be lost.");
        Loc.Register(FilesDiscardConfirm, "放弃修改", "Discard");
        Loc.Register(FilesArchiveTitle, "归档会话？", "Archive the session?");
        Loc.Register(FilesArchiveMessage, "{0} 有未保存的修改。归档不会保存也不会丢弃它们，文件会继续保持打开。", "{0} has unsaved edits. Archiving neither saves nor discards them; the file stays open.");
        Loc.Register(FilesArchiveConfirm, "仍然归档", "Archive anyway");

        Loc.Register(ChangesByTurn, "按轮次", "By turn");
        Loc.Register(ChangesUncommitted, "未提交改动", "Uncommitted");
        Loc.Register(ChangesNoSession, "没有打开的会话", "No open session");
        Loc.Register(ChangesNoChanges, "没有改动。", "No changes.");
        Loc.Register(ChangesNoTextualChange, "（没有文本改动：二进制或仅权限变化）", "(No textual change: binary or permissions only)");
        Loc.Register(ChangesRefresh, "刷新", "Refresh");
        Loc.Register(ChangesRefreshName, "刷新改动", "Refresh changes");
        Loc.Register(ChangesRowsName, "改动内容", "Change contents");
        Loc.Register(ChangesSummaryPattern, "{0} · {1}", "{0} · {1}");
        Loc.Register(ChangesSummaryFilesBase + ".one", "{0} 个文件", "{0} file");
        Loc.Register(ChangesSummaryFilesBase + ".other", "{0} 个文件", "{0} files");
        Loc.Register(ChangesTurnsName, "轮次", "Turns");
        Loc.Register(ChangesTurnPattern, "第 {0} 轮 · {1}", "Turn {0} · {1}");
        Loc.Register(ChangesFileHeaderPattern, "{0}  +{1} −{2}", "{0}  +{1} −{2}");

        Loc.Register(TodoNoSession, "没有打开的会话", "No open session");
        Loc.Register(TodoEmpty, "还没有任务", "No tasks yet");
        Loc.Register(TodoSummaryPattern, "{0}/{1} 完成", "{0}/{1} done");
        Loc.Register(TodoNewTaskPlaceholder, "添加任务…", "Add a task…");
        Loc.Register(TodoNewTaskName, "新任务", "New task");
        Loc.Register(TodoAdd, "添加任务", "Add task");
        Loc.Register(TodoDone, "完成", "Done");
        Loc.Register(TodoMoveUp, "上移", "Move up");
        Loc.Register(TodoMoveDown, "下移", "Move down");
        Loc.Register(TodoDelete, "删除", "Delete");
        Loc.Register(TodoClearDone, "清除已完成", "Clear done");

        Loc.Register(ReviewNoSession, "没有打开的会话", "No open session");
        Loc.Register(ReviewSummaryBase + ".one", "{0} 条意见", "{0} comment");
        Loc.Register(ReviewSummaryBase + ".other", "{0} 条意见", "{0} comments");
        Loc.Register(ReviewLinePattern, "L{0}", "L{0}");
        Loc.Register(ReviewLineRangePattern, "L{0}–{1}", "L{0}–{1}");
        Loc.Register(ReviewStatusPattern, "{0} · {1}", "{0} · {1}");
        Loc.Register(ReviewResolved, "已解决", "Resolved");
        Loc.Register(ReviewUnresolved, "未解决", "Unresolved");
        Loc.Register(ReviewSent, "已发送", "Sent");
        Loc.Register(ReviewDraft, "草稿", "Draft");

        Loc.Register(SpecNoProject, "没有打开的项目", "No project open");
        Loc.Register(SpecCountBase + ".one", "{0} 份规格", "{0} spec");
        Loc.Register(SpecCountBase + ".other", "{0} 份规格", "{0} specs");
        Loc.Register(SpecRefresh, "刷新", "Refresh");
        Loc.Register(SpecRefreshName, "刷新规格树", "Refresh spec tree");
        Loc.Register(SpecTreeName, "规格树", "Spec tree");
    }

    /// <summary>"3 个改动 · 12 个文件" / "3 changes · 12 files".</summary>
    public static string FileSummary(int changed, int files) =>
        Loc.Instance.Format(
            FilesSummaryPattern,
            LocText.Plural(FilesSummaryChangesBase, changed, TextFormat.Number(changed)),
            LocText.Plural(FilesSummaryFilesBase, files, TextFormat.Number(files)));

    /// <summary>"12 个文件 · 按轮次" / "12 files · By turn".</summary>
    public static string ChangesSummary(int files, bool byTurn) =>
        Loc.Instance.Format(
            ChangesSummaryPattern,
            LocText.Plural(ChangesSummaryFilesBase, files, TextFormat.Number(files)),
            Loc.Instance[byTurn ? ChangesByTurn : ChangesUncommitted]);

    /// <summary>The preview tab's title: the mark followed by the file name (or the generic word before one is loaded).</summary>
    public static string FileTabTitle(string name) => Loc.Instance.Format(FilesTabTitlePattern, FileTabGlyph, name);

    /// <summary>"第 3 轮 · 12:30:45" for one entry of the by-turn picker.</summary>
    public static LocText ChangeTurnLabel(int turn, DateTimeOffset startedAt) =>
        new(ChangesTurnPattern, TextFormat.Number(turn), TextFormat.Clock(startedAt));

    /// <summary>One diff file header: the path and its added/removed line counts.</summary>
    public static string FileDiffHeader(string path, int added, int removed) =>
        Loc.Instance.Format(ChangesFileHeaderPattern, path, TextFormat.Number(added), TextFormat.Number(removed));

    /// <summary>"3/8 完成" / "3/8 done".</summary>
    public static LocText TodoSummary(int done, int total) =>
        new(TodoSummaryPattern, TextFormat.Number(done), TextFormat.Number(total));

    /// <summary>"1 条意见" / "1 comment", "3 条意见" / "3 comments".</summary>
    public static LocText ReviewSummary(int comments) =>
        LocText.Plural(ReviewSummaryBase, comments, TextFormat.Number(comments));

    /// <summary>"L12" or "L12–14": line numbers are identifiers, never grouped.</summary>
    public static string ReviewLine(int start, int end) =>
        start == end
            ? Loc.Instance.Format(ReviewLinePattern, TextFormat.Id(start))
            : Loc.Instance.Format(ReviewLineRangePattern, TextFormat.Id(start), TextFormat.Id(end));

    /// <summary>"已解决 · 已发送" / "Resolved · Sent".</summary>
    public static string ReviewStatus(bool resolved, bool sent) =>
        Loc.Instance.Format(
            ReviewStatusPattern,
            Loc.Instance[resolved ? ReviewResolved : ReviewUnresolved],
            Loc.Instance[sent ? ReviewSent : ReviewDraft]);

    /// <summary>"12 份规格" / "12 specs".</summary>
    public static LocText SpecCount(int count) => LocText.Plural(SpecCountBase, count, TextFormat.Number(count));

    /// <summary>The git status description of a changed file; shown as the row's tooltip.</summary>
    public static LocText ChangeDescription(bool staged, bool unstaged) =>
        new(staged && unstaged ? FilesStatusStagedAndUnstaged : staged ? FilesStatusStaged : FilesStatusWorkingTree);

    /// <summary>"文件不存在：<c>path</c>" — the path itself is not translated.</summary>
    public static LocText FileMissing(string path) => new(FilesMissing, LocText.Literal(path));

    /// <summary>"文件过大，无法显示：<c>bytes</c> 字节" / "File too large to display: <c>bytes</c> bytes".</summary>
    public static LocText FileTooLarge(long bytes) => new(FilesTooLarge, TextFormat.Number(bytes));

    /// <summary>"只显示前 <c>lines</c> 行。" / "Only the first <c>lines</c> lines are shown.".</summary>
    public static LocText LinesTruncated(int lines) => new(FilesTruncated, TextFormat.Number(lines));

    /// <summary>"文件监视不可用：<c>message</c>" — the OS message is inserted as is.</summary>
    public static LocText WatchUnavailable(string message) => new(FilesWatchUnavailable, LocText.Literal(message));
    public static LocText FileBinary(string path) => new(FilesBinary, LocText.Literal(path));
    public static string FileDiscardMessage(string name) => Loc.Instance.Format(FilesDiscardMessage, name);
    public static string FileArchiveMessage(string name) => Loc.Instance.Format(FilesArchiveMessage, name);
}
