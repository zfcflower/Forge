using System.Runtime.CompilerServices;

namespace Forge.App.Localization;

/// <summary>
/// The editor table: the settings section, the toolbar and context menus, and the notices the editor bridge reports.
/// <para>Keys are <c>editor.*</c>. Parameterized texts are typed accessor methods; parameterless ones are properties that
/// read <see cref="Loc.Instance"/>, so a language switch is picked up without caching.</para>
/// </summary>
internal static class EditorStrings
{
    public const string SectionTitle = "editor.settings.title";
    public const string SectionHint = "editor.settings.hint";
    public const string ShowLineNumbers = "editor.settings.lineNumbers";
    public const string WordWrap = "editor.settings.wordWrap";
    public const string ShowSpaces = "editor.settings.spaces";
    public const string ShowTabs = "editor.settings.tabs";
    public const string ShowEndOfLine = "editor.settings.endOfLine";
    public const string ShowControlCharacters = "editor.settings.controlCharacters";
    public const string ScrollBelowDocument = "editor.settings.scrollBelow";
    public const string HighlightCurrentLine = "editor.settings.currentLine";
    public const string Hyperlinks = "editor.settings.hyperlinks";
    public const string AutoFolding = "editor.settings.folding";
    public const string IndentationSize = "editor.settings.indentation";
    public const string TabsToSpaces = "editor.settings.tabsToSpaces";
    public const string ColumnRulers = "editor.settings.rulers";
    public const string ColumnRulersHint = "editor.settings.rulersHint";
    public const string Theme = "editor.settings.theme";
    public const string ThemeAuto = "editor.settings.theme.auto";
    public const string ThemeLight = "editor.settings.theme.light";
    public const string ThemeDark = "editor.settings.theme.dark";

    public const string MenuLabel = "editor.menu.label";
    public const string MenuDisplay = "editor.menu.display";
    public const string MenuFolding = "editor.menu.folding";
    public const string MenuCommands = "editor.menu.commands";
    public const string MenuFind = "editor.menu.find";
    public const string MenuReplace = "editor.menu.replace";
    public const string MenuToggleFold = "editor.menu.toggleFold";
    public const string MenuFoldAll = "editor.menu.foldAll";
    public const string MenuUnfoldAll = "editor.menu.unfoldAll";
    public const string MenuToUpper = "editor.menu.upper";
    public const string MenuToLower = "editor.menu.lower";
    public const string MenuToTitle = "editor.menu.titleCase";
    public const string MenuInvertCase = "editor.menu.invertCase";
    public const string MenuLeadingTabsToSpaces = "editor.menu.tabsToSpaces";
    public const string MenuLeadingSpacesToTabs = "editor.menu.spacesToTabs";
    public const string MenuDeleteLine = "editor.menu.deleteLine";
    public const string MenuIndentSelection = "editor.menu.indent";
    public const string MenuColumnSelection = "editor.menu.columnSelection";
    public const string MenuUndo = "editor.menu.undo";
    public const string MenuRedo = "editor.menu.redo";
    public const string MenuCut = "editor.menu.cut";
    public const string MenuCopy = "editor.menu.copy";
    public const string MenuPaste = "editor.menu.paste";
    public const string MenuSelectAll = "editor.menu.selectAll";

    public const string FoldTooLarge = "editor.fold.tooLarge";
    public const string LinkFailed = "editor.link.failed";
    public const string DiagnosticsCountPattern = "editor.diagnostics.count";

    [ModuleInitializer]
    internal static void Register()
    {
        Loc.Register(SectionTitle, "编辑器", "Editor");
        Loc.Register(
            SectionHint,
            "默认值对新打开的文件生效，改动会立即套用到所有已打开的编辑器并写入设置。选中一列请在编辑器里按住 Alt 拖拽（矩形选择），输入与删除会作用到整列；AvaloniaEdit 12 没有多光标实现。",
            "Defaults apply to newly opened files; a change reaches every open editor immediately and is written to settings. To edit a column, hold Alt and drag in the editor (rectangular selection): typing and deleting then apply to the whole column, because AvaloniaEdit 12 has no multi-caret implementation.");
        Loc.Register(ShowLineNumbers, "显示行号", "Line numbers");
        Loc.Register(WordWrap, "默认自动换行", "Wrap by default");
        Loc.Register(ShowSpaces, "显示空格", "Show spaces");
        Loc.Register(ShowTabs, "显示制表符", "Show tabs");
        Loc.Register(ShowEndOfLine, "显示行尾符", "Show line endings");
        Loc.Register(ShowControlCharacters, "显示控制字符", "Show control characters");
        Loc.Register(ScrollBelowDocument, "可滚动到文档末尾以下", "Scroll below the document");
        Loc.Register(HighlightCurrentLine, "高亮当前行", "Highlight current line");
        Loc.Register(Hyperlinks, "识别链接（Ctrl/Cmd + 点击）", "Links (Ctrl/Cmd + click)");
        Loc.Register(AutoFolding, "自动折叠", "Automatic folding");
        Loc.Register(IndentationSize, "缩进宽度", "Indent width");
        Loc.Register(TabsToSpaces, "Tab 插入空格", "Tab inserts spaces");
        Loc.Register(ColumnRulers, "列标尺", "Column rulers");
        Loc.Register(ColumnRulersHint, "以逗号分隔的列号，例如 80,120；留空不显示。", "Comma-separated columns, e.g. 80,120; empty draws none.");
        Loc.Register(Theme, "语法着色主题", "Syntax theme");
        Loc.Register(ThemeAuto, "跟随界面", "Follow the UI");
        Loc.Register(ThemeLight, "始终亮色", "Always light");
        Loc.Register(ThemeDark, "始终暗色", "Always dark");

        Loc.Register(MenuLabel, "编辑器选项", "Editor options");
        Loc.Register(MenuDisplay, "显示", "Display");
        Loc.Register(MenuFolding, "折叠", "Folding");
        Loc.Register(MenuCommands, "转换", "Transform");
        Loc.Register(MenuFind, "查找…", "Find…");
        Loc.Register(MenuReplace, "替换…", "Replace…");
        Loc.Register(MenuToggleFold, "折叠/展开当前块", "Toggle fold");
        Loc.Register(MenuFoldAll, "全部折叠", "Fold all");
        Loc.Register(MenuUnfoldAll, "全部展开", "Unfold all");
        Loc.Register(MenuToUpper, "转为大写", "To upper case");
        Loc.Register(MenuToLower, "转为小写", "To lower case");
        Loc.Register(MenuToTitle, "转为词首大写", "To title case");
        Loc.Register(MenuInvertCase, "反转大小写", "Invert case");
        Loc.Register(MenuLeadingTabsToSpaces, "前导 Tab 转空格", "Leading tabs to spaces");
        Loc.Register(MenuLeadingSpacesToTabs, "前导空格转 Tab", "Leading spaces to tabs");
        Loc.Register(MenuDeleteLine, "删除行", "Delete line");
        Loc.Register(MenuIndentSelection, "缩进选区", "Indent selection");
        Loc.Register(MenuColumnSelection, "列选择：Alt + 拖拽", "Column selection: Alt + drag");
        Loc.Register(MenuUndo, "撤销", "Undo");
        Loc.Register(MenuRedo, "重做", "Redo");
        Loc.Register(MenuCut, "剪切", "Cut");
        Loc.Register(MenuCopy, "复制", "Copy");
        Loc.Register(MenuPaste, "粘贴", "Paste");
        Loc.Register(MenuSelectAll, "全选", "Select all");

        Loc.Register(FoldTooLarge, "文件超过 {0} 行，折叠已停用。", "The file exceeds {0} lines; folding is disabled.");
        Loc.Register(LinkFailed, "无法打开链接：{0}", "The link could not be opened: {0}");
        Loc.Register(DiagnosticsCountPattern, "{0} 条诊断", "{0} diagnostics");
    }

    /// <summary>“The file exceeds {0} lines; folding is disabled.”</summary>
    public static LocText FoldTooLargeText(int lines) => new(FoldTooLarge, TextFormat.Number(lines));

    /// <summary>“The link could not be opened: {0}”.</summary>
    public static LocText LinkFailedText(string reason) => new(LinkFailed, LocText.Literal(reason));
}
