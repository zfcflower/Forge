using System.Runtime.CompilerServices;

namespace Forge.App.Localization;

/// <summary>
/// The <c>keymap</c> string table: the keyboard-shortcut settings section and the display name of every rebindable action.
/// Keys are namespaced with <c>keymap.</c> and every key is registered from <see cref="Initialize"/>, so no shared file lists
/// this table. The action display names live here too, so the section and the catalog share one source of truth; the gesture
/// text itself is formatted by <c>Input.KeymapGesture</c>, never spelled out in a string.
/// <para>Every declared constant is a key (<c>*Key</c>) and every text the page shows is a static property that resolves
/// through <see cref="Loc"/> at call time. A section view model that read a constant instead would render the key itself
/// (<c>keymap.hint</c>) in the page, which is exactly what a raw-key read looks like on screen.</para>
/// </summary>
internal static class KeymapStrings
{
    // ---- section ----

    public const string SettingsTitleKey = "keymap.settings.title";
    public const string SearchPlaceholderKey = "keymap.search.placeholder";
    public const string HintKey = "keymap.hint";
    public const string ScopeWindowKey = "keymap.scope.window";
    public const string ScopeEditorKey = "keymap.scope.editor";
    public const string ScopeViewKey = "keymap.scope.view";
    public const string DefaultGesturePatternKey = "keymap.defaultGesture";
    public const string ResetKey = "keymap.reset";
    public const string ResetAllKey = "keymap.resetAll";
    public const string RebindKey = "keymap.rebind";
    public const string CapturePromptKey = "keymap.capture.prompt";
    public const string CaptureConfirmKey = "keymap.capture.confirm";
    public const string CaptureCancelKey = "keymap.capture.cancel";
    public const string ConflictPatternKey = "keymap.conflict";

    // ---- window actions ----

    public const string ActionNewTerminalKey = "keymap.action.window.newTerminal";
    public const string ActionOpenFilePaletteKey = "keymap.action.window.openFilePalette";
    public const string ActionOpenCommandPaletteKey = "keymap.action.window.openCommandPalette";
    public const string ActionOpenSearchPaletteKey = "keymap.action.window.openSearchPalette";

    // ---- editor menu actions ----

    public const string ActionEditorUndoKey = "keymap.action.editor.undo";
    public const string ActionEditorRedoKey = "keymap.action.editor.redo";
    public const string ActionEditorCutKey = "keymap.action.editor.cut";
    public const string ActionEditorCopyKey = "keymap.action.editor.copy";
    public const string ActionEditorPasteKey = "keymap.action.editor.paste";
    public const string ActionEditorSelectAllKey = "keymap.action.editor.selectAll";
    public const string ActionEditorToggleFoldKey = "keymap.action.editor.toggleFold";
    public const string ActionEditorFoldAllKey = "keymap.action.editor.foldAll";
    public const string ActionEditorUnfoldAllKey = "keymap.action.editor.unfoldAll";
    public const string ActionEditorFindKey = "keymap.action.editor.find";
    public const string ActionEditorReplaceKey = "keymap.action.editor.replace";
    public const string ActionEditorCodeActionKey = "keymap.action.editor.codeAction";
    public const string ActionEditorRenameKey = "keymap.action.editor.rename";

    // ---- view actions ----

    public const string ActionSaveDocumentKey = "keymap.action.view.saveDocument";
    public const string ActionNextUnviewedChangeKey = "keymap.action.view.nextUnviewedChange";
    public const string ActionLspCompletionKey = "keymap.action.view.lspCompletion";
    public const string ActionLspReferencesKey = "keymap.action.view.lspReferences";

    /// <summary>Registers this table before any code of the assembly runs (module initializer, AOT safe).</summary>
    [ModuleInitializer]
    internal static void Initialize() => Register();

    // ---- section texts, resolved at call time so a language switch re-reads them ----

    public static string SearchPlaceholder => Loc.Instance[SearchPlaceholderKey];

    public static string Hint => Loc.Instance[HintKey];

    public static string ScopeWindow => Loc.Instance[ScopeWindowKey];

    public static string ScopeEditor => Loc.Instance[ScopeEditorKey];

    public static string ScopeView => Loc.Instance[ScopeViewKey];

    public static string ResetAll => Loc.Instance[ResetAllKey];

    public static string CapturePrompt => Loc.Instance[CapturePromptKey];

    public static string CaptureConfirm => Loc.Instance[CaptureConfirmKey];

    public static string CaptureCancel => Loc.Instance[CaptureCancelKey];

    /// <summary>Idempotent: registering the same key with the same texts twice is a no-op.</summary>
    internal static void Register()
    {
        Loc.Register(SettingsTitleKey, "键盘快捷键", "Keyboard shortcuts");
        Loc.Register(SearchPlaceholderKey, "搜索动作或快捷键", "Search actions or shortcuts");
        Loc.Register(
            HintKey,
            "选择一个动作重新绑定快捷键；冲突会在确认前提示。改绑立即生效。",
            "Choose an action to rebind its shortcut; conflicts are reported before you confirm. Changes apply immediately.");
        Loc.Register(ScopeWindowKey, "窗口", "Window");
        Loc.Register(ScopeEditorKey, "编辑器", "Editor");
        Loc.Register(ScopeViewKey, "视图", "View");
        Loc.Register(DefaultGesturePatternKey, "默认 {0}", "Default {0}");
        Loc.Register(ResetKey, "重置", "Reset");
        Loc.Register(ResetAllKey, "全部重置", "Reset all");
        Loc.Register(RebindKey, "改绑", "Rebind");
        Loc.Register(CapturePromptKey, "按下新的按键组合…", "Press the new key combination…");
        Loc.Register(CaptureConfirmKey, "确认", "Confirm");
        Loc.Register(CaptureCancelKey, "取消", "Cancel");
        Loc.Register(ConflictPatternKey, "与 {0} 冲突。", "Conflicts with {0}.");

        Loc.Register(ActionNewTerminalKey, "新建终端", "New terminal");
        Loc.Register(ActionOpenFilePaletteKey, "搜索文件", "Search files");
        Loc.Register(ActionOpenCommandPaletteKey, "命令面板", "Command palette");
        Loc.Register(ActionOpenSearchPaletteKey, "在文件中搜索文本", "Search text in files");

        Loc.Register(ActionEditorUndoKey, "撤销", "Undo");
        Loc.Register(ActionEditorRedoKey, "重做", "Redo");
        Loc.Register(ActionEditorCutKey, "剪切", "Cut");
        Loc.Register(ActionEditorCopyKey, "复制", "Copy");
        Loc.Register(ActionEditorPasteKey, "粘贴", "Paste");
        Loc.Register(ActionEditorSelectAllKey, "全选", "Select all");
        Loc.Register(ActionEditorToggleFoldKey, "折叠/展开当前块", "Toggle fold");
        Loc.Register(ActionEditorFoldAllKey, "全部折叠", "Fold all");
        Loc.Register(ActionEditorUnfoldAllKey, "全部展开", "Unfold all");
        Loc.Register(ActionEditorFindKey, "查找", "Find");
        Loc.Register(ActionEditorReplaceKey, "替换", "Replace");
        Loc.Register(ActionEditorCodeActionKey, "代码操作", "Code actions");
        Loc.Register(ActionEditorRenameKey, "重命名符号", "Rename symbol");

        Loc.Register(ActionSaveDocumentKey, "保存文件", "Save file");
        Loc.Register(ActionNextUnviewedChangeKey, "下一个未查看改动", "Next unviewed change");
        Loc.Register(ActionLspCompletionKey, "代码补全", "Code completion");
        Loc.Register(ActionLspReferencesKey, "查找引用", "Find references");
    }

    /// <summary>The action's localized display name, resolved at call time so a language switch re-reads it.</summary>
    public static string ActionName(string displayNameKey) => Loc.Instance[displayNameKey];

    /// <summary>How the default gesture is labelled next to an overridden one; the gesture text is passed in, already formatted.</summary>
    public static string DefaultGesture(string gesture) => Loc.Instance.Format(DefaultGesturePatternKey, gesture);

    /// <summary>The conflict notice naming the action that already uses the candidate gesture.</summary>
    public static string Conflict(string actionName) => Loc.Instance.Format(ConflictPatternKey, actionName);
}
