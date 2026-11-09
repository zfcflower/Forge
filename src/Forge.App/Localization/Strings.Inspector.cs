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
    // ---------------------------------------------------------------- files panel
    public const string FilesAll = "files.all";
    public const string FilesAllName = "files.all.name";
    public const string FilesAllCountNamePattern = "files.all.countName";
    public const string FilesChanged = "files.changed";
    public const string FilesChangedName = "files.changed.name";
    public const string FilesChangedCountNamePattern = "files.changed.countName";
    public const string FilesChangedListName = "files.changedList.name";
    public const string FilesTreeName = "files.tree.name";
    public const string FilesName = "files.name";
    public const string FilesLoading = "files.loading";
    public const string FilesOpenInNewTab = "files.openInNewTab";
    public const string FilesRefresh = "files.refresh";
    public const string FilesRefreshName = "files.refresh.name";
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
    public const string EditorLspPrefix = "files.editorLsp.";
    public const string EditorLspNone = "files.editorLsp.none";
    public const string EditorLspNoResponse = "files.editorLsp.noResponse";
    public const string EditorLspUnsupported = "files.editorLsp.unsupported";
    public const string EditorLspFeatureCompletion = "files.editorLsp.feature.completion";
    public const string EditorLspFeatureHover = "files.editorLsp.feature.hover";
    public const string EditorLspFeatureDefinition = "files.editorLsp.feature.definition";
    public const string EditorLspSnippetsConverted = "files.editorLsp.snippetsConverted";
    public const string EditorLspInvalidItems = "files.editorLsp.invalidItems";
    public const string EditorLspInvalidDiagnostics = "files.editorLsp.invalidDiagnostics";
    public const string EditorLspApplyFailed = "files.editorLsp.applyFailed";
    public const string EditorLspDefinitionNone = "files.editorLsp.definition.none";
    public const string EditorLspDefinitionMultiple = "files.editorLsp.definition.multiple";
    public const string EditorLspDefinitionRemote = "files.editorLsp.definition.remote";
    public const string EditorLspBadLocation = "files.editorLsp.badLocation";
    public const string EditorLspSeverityError = "files.editorLsp.severity.error";
    public const string EditorLspSeverityWarning = "files.editorLsp.severity.warning";
    public const string EditorLspSeverityInformation = "files.editorLsp.severity.information";
    public const string EditorLspSeverityHint = "files.editorLsp.severity.hint";
    public const string FilesLspStopped = "files.lsp.stopped";
    public const string FilesLspServer = "files.lsp.server";
    public const string FilesLspLoading = "files.lsp.loading";
    public const string FilesEditorHighlightFailed = "files.editor.highlight.failed";
    public const string FilesEditorHighlightTooLarge = "files.editor.highlight.large";
    public const string FilesEditorDesynced = "files.editor.desynced";
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
    public const string ChangesAll = "changes.all";
    public const string ChangesByTurn = "changes.byTurn";
    public const string ChangesUncommitted = "changes.uncommitted";
    public const string ChangesNoSession = "changes.noSession";
    public const string ChangesNoChanges = "changes.noChanges";
    public const string ChangesNoTextualChange = "changes.noTextualChange";
    public const string ChangesRefresh = "changes.refresh";
    public const string ChangesRefreshName = "changes.refresh.name";
    public const string ChangesPreviousFile = "changes.previousFile";
    public const string ChangesNextFile = "changes.nextFile";
    public const string ChangesOpenInEditor = "changes.openInEditor";
    public const string ChangesCopyPath = "changes.copyPath";
    public const string ChangesRowsName = "changes.rows.name";
    public const string ChangesSummaryPattern = "changes.summary";
    public const string ChangesSummaryFilesBase = "changes.summary.files";
    public const string ChangesTurnsName = "changes.turns.name";
    public const string ChangesCompareBranchLabelPattern = "changes.compareBranch.label";
    public const string ChangesCompareBranchPlaceholder = "changes.compareBranch.placeholder";
    public const string ChangesLocalBranches = "changes.compareBranch.local";
    public const string ChangesRemoteBranches = "changes.compareBranch.remote";
    public const string ChangesNoBranches = "changes.compareBranch.none";
    public const string ChangesNoTurns = "changes.turns.none";
    public const string ChangesNoCurrentBranch = "changes.compareBranch.noCurrentBranch";
    public const string ChangesNoCommonHistory = "changes.compareBranch.noCommonHistory";
    public const string ChangesTurnPattern = "changes.turn";
    public const string ChangesFileHeaderPattern = "changes.fileHeader";

    // ---------------------------------------------------------------- todo
    public const string TodoNoSession = "todo.noSession";
    public const string TodoEmpty = "todo.empty";
    public const string TodoAddPlaceholder = "todo.addPlaceholder";
    public const string TodoNewTaskName = "todo.newTask.name";
    public const string TodoDoneSection = "todo.doneSection";
    public const string TodoGroupStepsPattern = "todo.groupSteps";
    public const string TodoGroupProgressPattern = "todo.groupProgress";
    public const string TodoSourceUser = "todo.source.user";
    public const string TodoExpand = "todo.expand";
    public const string TodoCollapse = "todo.collapse";
    public const string TodoStatusPending = "todo.status.pending";
    public const string TodoStatusInProgress = "todo.status.inProgress";
    public const string TodoStatusPaused = "todo.status.paused";
    public const string TodoStatusDone = "todo.status.done";
    public const string TodoDelete = "todo.delete";

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
        Loc.Register(FilesAllCountNamePattern, "{0}（{1}）", "{0} ({1})");
        Loc.Register(FilesChangedCountNamePattern, "{0}（{1}）", "{0} ({1})");
        Loc.Register(FilesTreeName, "全部文件树", "All files tree");
        Loc.Register(FilesName, "文件", "Files");
        Loc.Register(FilesLoading, "读取中…", "Loading…");
        Loc.Register(FilesOpenInNewTab, "在新标签页打开", "Open in new tab");
        Loc.Register(FilesRefresh, "刷新", "Refresh");
        Loc.Register(FilesRefreshName, "刷新文件", "Refresh files");
        Loc.Register(FilesTabTitlePattern, "{0}", "{0}");
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
        Loc.Register(EditorLspNone, "此文件没有可用的语言服务器：已检查 PATH、VS Code/Cursor 的扩展与安装目录，未找到该语言的服务器，也没有在 languages.json 里配置。Forge 不会自动下载或安装语言服务器。", "No language server is available for this file: PATH, the VS Code/Cursor extension and installation folders were checked, nothing serves this language and languages.json configures none. Forge never downloads or installs a language server.");
        Loc.Register(EditorLspNoResponse, "语言服务器没有响应。", "The language server did not respond.");
        Loc.Register(EditorLspUnsupported, "语言服务器不支持{0}。", "The language server does not support {0}.");
        Loc.Register(EditorLspFeatureCompletion, "代码补全", "code completion");
        Loc.Register(EditorLspFeatureHover, "悬停提示", "hover information");
        Loc.Register(EditorLspFeatureDefinition, "跳转定义", "go to definition");
        Loc.Register(EditorLspSnippetsConverted, "{0} 个代码片段补全已按纯文本插入（不提供占位符跳转）。", "{0} snippet completions were inserted as plain text (no placeholder navigation).");
        Loc.Register(EditorLspInvalidItems, "已忽略 {0} 个格式或编辑位置无效的补全项。", "Ignored {0} completion items with an invalid format or edit range.");
        Loc.Register(EditorLspInvalidDiagnostics, "已忽略 {0} 条位置或级别无效的诊断。", "Ignored {0} diagnostics with an invalid range or severity.");
        Loc.Register(EditorLspApplyFailed, "补全未应用：文档已变化，编辑位置不再对应。", "Completion not applied: the document changed and the edit no longer lines up.");
        Loc.Register(EditorLspDefinitionNone, "未找到定义。", "No definition found.");
        Loc.Register(EditorLspDefinitionMultiple, "找到 {0} 个定义，已跳转到第一个。", "Found {0} definitions; jumped to the first.");
        Loc.Register(EditorLspDefinitionRemote, "定义不在本地文件中：{0}", "The definition is not in a local file: {0}");
        Loc.Register(EditorLspBadLocation, "语言服务器返回了无效的位置。", "The language server returned an invalid location.");
        Loc.Register(EditorLspSeverityError, "错误", "Error");
        Loc.Register(EditorLspSeverityWarning, "警告", "Warning");
        Loc.Register(EditorLspSeverityInformation, "信息", "Info");
        Loc.Register(EditorLspSeverityHint, "提示", "Hint");
        Loc.Register(FilesLspStopped, "语言服务未能启动，编辑不受影响。", "The language server could not start; editing is unaffected.");
        Loc.Register(FilesLspServer, "语言服务：{0}（来源：{1}）", "Language server: {0} (from {1})");
        Loc.Register(FilesLspLoading, "语言服务正在加载项目，加载完成后才有诊断、悬停与跳转…", "The language server is still loading the project; diagnostics, hover and navigation arrive when it is done…");
        Loc.Register(FilesEditorHighlightFailed, "语法高亮已停用：{0}", "Syntax highlighting disabled: {0}");
        Loc.Register(FilesEditorHighlightTooLarge, "文件超过 1,048,576 个字符，语法高亮已停用。", "The file exceeds 1,048,576 characters; syntax highlighting is disabled.");
        Loc.Register(FilesEditorDesynced, "编辑器与文档不同步，已停用编辑和保存，本地文本仍保留。请先复制需要的内容；重新载入文件会丢弃本地修改。", "The editor is out of sync with the document. Editing and saving are disabled and the local text is kept. Copy what you need first; reloading the file discards the local changes.");
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

        Loc.Register(ChangesAll, "全部改动", "All changes");
        Loc.Register(ChangesByTurn, "按轮次", "By turn");
        Loc.Register(ChangesUncommitted, "未提交改动", "Uncommitted changes");
        Loc.Register(ChangesNoSession, "没有打开的会话", "No open session");
        Loc.Register(ChangesNoChanges, "没有改动。", "No changes.");
        Loc.Register(ChangesNoTextualChange, "（没有文本改动：二进制或仅权限变化）", "(No textual change: binary or permissions only)");
        Loc.Register(ChangesRefresh, "刷新", "Refresh");
        Loc.Register(ChangesRefreshName, "刷新改动", "Refresh changes");
        Loc.Register(ChangesPreviousFile, "上一个文件", "Previous file");
        Loc.Register(ChangesNextFile, "下一个文件", "Next file");
        Loc.Register(ChangesOpenInEditor, "在编辑器中打开", "Open in editor");
        Loc.Register(ChangesCopyPath, "复制路径", "Copy path");
        Loc.Register(ChangesRowsName, "改动内容", "Change contents");
        Loc.Register(ChangesSummaryPattern, "{0} · {1}", "{0} · {1}");
        Loc.Register(ChangesSummaryFilesBase + ".one", "{0} 个文件", "{0} file");
        Loc.Register(ChangesSummaryFilesBase + ".other", "{0} 个文件", "{0} files");
        Loc.Register(ChangesTurnsName, "轮次", "Turns");
        Loc.Register(ChangesCompareBranchLabelPattern, "对比 {0}", "Compare with {0}");
        Loc.Register(ChangesCompareBranchPlaceholder, "选择对比分支", "Choose a comparison branch");
        Loc.Register(ChangesLocalBranches, "本地分支", "Local branches");
        Loc.Register(ChangesRemoteBranches, "远端分支", "Remote branches");
        Loc.Register(ChangesNoBranches, "没有可比较的分支。", "No comparison branch is available.");
        Loc.Register(ChangesNoTurns, "没有已完成的轮次。", "No completed turns.");
        Loc.Register(ChangesNoCurrentBranch, "当前工作区不在分支上，无法比较分支。", "The current worktree is not on a branch, so it cannot be compared.");
        Loc.Register(ChangesNoCommonHistory, "所选分支与当前分支没有共同历史。", "The selected branch and current branch have no common history.");
        Loc.Register(ChangesTurnPattern, "第 {0} 轮 · {1}", "Turn {0} · {1}");
        Loc.Register(ChangesFileHeaderPattern, "{0}  +{1} −{2}", "{0}  +{1} −{2}");

        Loc.Register(TodoNoSession, "没有打开的会话", "No open session");
        Loc.Register(TodoEmpty, "还没有任务", "No tasks yet");
        Loc.Register(TodoAddPlaceholder, "为 agent 排队一项任务", "Queue a task for the agent");
        Loc.Register(TodoNewTaskName, "新任务", "New task");
        Loc.Register(TodoDoneSection, "完成", "Done");
        Loc.Register(TodoGroupStepsPattern, "{0} 个步骤", "{0} steps");
        Loc.Register(TodoGroupProgressPattern, "{0}/{1}", "{0}/{1}");
        Loc.Register(TodoSourceUser, "手动添加", "Added by hand");
        Loc.Register(TodoExpand, "展开", "Expand");
        Loc.Register(TodoCollapse, "收起", "Collapse");
        Loc.Register(TodoStatusPending, "未开始", "Not started");
        Loc.Register(TodoStatusInProgress, "进行中", "In progress");
        Loc.Register(TodoStatusPaused, "已暂停", "Paused");
        Loc.Register(TodoStatusDone, "已完成", "Done");
        Loc.Register(TodoDelete, "删除", "Delete");

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

    /// <summary>The "全部" tab's accessible name with its badge count: "全部文件（348）" / "All files (348)". The number is
    /// an ungrouped identifier, matching the badge it stands in for.</summary>
    public static string FilesAllCountName(int count) =>
        Loc.Instance.Format(FilesAllCountNamePattern, Loc.Instance[FilesAllName], TextFormat.Id(count));

    /// <summary>The "已改动" tab's accessible name with its badge count: "已改动文件（12）" / "Changed files (12)".</summary>
    public static string FilesChangedCountName(int count) =>
        Loc.Instance.Format(FilesChangedCountNamePattern, Loc.Instance[FilesChangedName], TextFormat.Id(count));

    /// <summary>"12 个文件 · 按轮次/未提交改动/全部改动".</summary>
    public static string ChangesSummary(int files, bool byTurn, bool allChanges = false) =>
        Loc.Instance.Format(
            ChangesSummaryPattern,
            LocText.Plural(ChangesSummaryFilesBase, files, TextFormat.Number(files)),
            Loc.Instance[allChanges ? ChangesAll : byTurn ? ChangesByTurn : ChangesUncommitted]);

    /// <summary>"对比 origin/main" / "Compare with origin/main".</summary>
    public static string ChangesCompareBranchLabel(string branch) =>
        Loc.Instance.Format(ChangesCompareBranchLabelPattern, branch);

    /// <summary>The preview tab's title: the file name (or the generic word before one is loaded). The tab's leading
    /// mark is the icon column (<c>TabHeaderView</c> draws the file tree's document), so the title carries no glyph.</summary>
    public static string FileTabTitle(string name) => Loc.Instance.Format(FilesTabTitlePattern, name);

    /// <summary>"第 3 轮 · 12:30:45" for one entry of the by-turn picker.</summary>
    public static LocText ChangeTurnLabel(int turn, DateTimeOffset startedAt) =>
        new(ChangesTurnPattern, TextFormat.Number(turn), TextFormat.Clock(startedAt));

    /// <summary>One diff file header: the path and its added/removed line counts.</summary>
    public static string FileDiffHeader(string path, int added, int removed) =>
        Loc.Instance.Format(ChangesFileHeaderPattern, path, TextFormat.Number(added), TextFormat.Number(removed));

    /// <summary>"1/3": how many of a group's steps are done, out of all of them (grouped counts).</summary>
    public static string TodoProgress(int done, int total) =>
        Loc.Instance.Format(TodoGroupProgressPattern, TextFormat.Id(done), TextFormat.Id(total));

    /// <summary>"4 个步骤" / "4 steps": the step count of a folded group in the done section.</summary>
    public static LocText TodoGroupSteps(int count) =>
        new(TodoGroupStepsPattern, TextFormat.Number(count));

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
    public static LocText FileCopyFailed(string message) => new(FilesCopyFailed, LocText.Literal(message));
}
