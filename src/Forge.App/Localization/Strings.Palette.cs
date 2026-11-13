using System.Runtime.CompilerServices;

namespace Forge.App.Localization;

/// <summary>
/// The palette's string table: the five source words, the placeholders and empty states of each, the command catalog,
/// the preview headings, the footer hints and the <c>palette</c> settings section. Keys are namespaced with
/// <c>palette.</c> and every key is registered from <see cref="Initialize"/>, so no shared file lists this table.
/// </summary>
internal static class PaletteStrings
{
    // ---- sources ----

    public const string SourceFiles = "palette.source.files";
    public const string SourceContent = "palette.source.content";
    public const string SourceSessions = "palette.source.sessions";
    public const string SourceCommands = "palette.source.commands";
    public const string SourceSymbols = "palette.source.symbols";

    // ---- placeholders ----

    public const string PlaceholderFiles = "palette.placeholder.files";
    public const string PlaceholderContent = "palette.placeholder.content";
    public const string PlaceholderSessions = "palette.placeholder.sessions";
    public const string PlaceholderCommands = "palette.placeholder.commands";
    public const string PlaceholderSymbols = "palette.placeholder.symbols";

    // ---- empty states ----

    public const string EmptyFiles = "palette.empty.files";
    public const string EmptyContent = "palette.empty.content";
    public const string EmptyContentQuery = "palette.empty.contentQuery";
    public const string EmptySessions = "palette.empty.sessions";
    public const string EmptyCommands = "palette.empty.commands";
    public const string EmptySymbols = "palette.empty.symbols";

    // ---- notices ----

    public const string NoticeNoWorkspace = "palette.notice.noWorkspace";
    public const string NoticeNoDocument = "palette.notice.noDocument";
    public const string NoticeSymbolsUnavailable = "palette.notice.symbolsUnavailable";
    public const string NoticeSearchFailed = "palette.notice.searchFailed";
    public const string NoticeFilesFailed = "palette.notice.filesFailed";
    public const string NoticeSessionsFailed = "palette.notice.sessionsFailed";

    // ---- status ----

    public const string StatusSearching = "palette.status.searching";
    public const string StatusLoading = "palette.status.loading";
    public const string StatusTruncated = "palette.status.truncated";
    public const string StatusSymbolDocument = "palette.status.symbolDocument";

    // ---- preview ----

    public const string PreviewFile = "palette.preview.file";
    public const string PreviewContent = "palette.preview.content";
    public const string PreviewSession = "palette.preview.session";
    public const string PreviewCommand = "palette.preview.command";
    public const string PreviewSymbol = "palette.preview.symbol";
    public const string PreviewNotice = "palette.preview.notice";
    public const string PreviewEmpty = "palette.preview.empty";
    public const string PreviewAroundLine = "palette.preview.aroundLine";

    // ---- hints and accessibility ----

    public const string HintNavigate = "palette.hint.navigate";
    public const string HintOpen = "palette.hint.open";
    public const string HintClose = "palette.hint.close";
    public const string HintSources = "palette.hint.sources";
    public const string Shortcut = "palette.shortcut";
    public const string AutomationName = "palette.automation.name";

    // ---- symbol kind badges ----

    public const string SymbolKindType = "palette.symbol.type";
    public const string SymbolKindFunction = "palette.symbol.function";
    public const string SymbolKindMember = "palette.symbol.member";
    public const string SymbolKindVariable = "palette.symbol.variable";
    public const string SymbolKindOther = "palette.symbol.unknown";

    // ---- session badges ----

    public const string SessionArchived = "palette.badge.archived";

    // ---- command categories ----

    public const string CategoryNavigate = "palette.command.category.navigate";
    public const string CategoryView = "palette.command.category.view";
    public const string CategoryProject = "palette.command.category.project";
    public const string CategorySettings = "palette.command.category.settings";
    public const string CategoryGit = "palette.command.category.git";

    // ---- commands ----

    public const string CommandFiles = "palette.command.files";
    public const string CommandContent = "palette.command.content";
    public const string CommandSessions = "palette.command.sessions";
    public const string CommandSymbols = "palette.command.symbols";
    public const string CommandCommands = "palette.command.commands";
    public const string CommandChanges = "palette.command.changes";
    public const string CommandTerminal = "palette.command.terminal";
    public const string CommandToggleProjects = "palette.command.toggleProjects";
    public const string CommandToggleFiles = "palette.command.toggleFiles";
    public const string CommandTogglePreview = "palette.command.togglePreview";
    public const string CommandTodoTab = "palette.command.todoTab";
    public const string CommandReviewTab = "palette.command.reviewsTab";
    public const string CommandSpecsTab = "palette.command.specsTab";
    public const string CommandAddProject = "palette.command.addProject";
    public const string CommandRevealGeneral = "palette.command.revealGeneral";
    public const string CommandOpenSettings = "palette.command.openSettings";
    public const string CommandReloadSettings = "palette.command.reloadSettings";
    public const string CommandGitFetch = "palette.command.gitFetch";
    public const string CommandGitPull = "palette.command.gitPull";
    public const string CommandGitPush = "palette.command.gitPush";

    // ---- settings section ----

    public const string SettingsTitle = "palette.settings.title";
    public const string SettingsLimit = "palette.settings.limit";
    public const string SettingsLimitHint = "palette.settings.limitHint";
    public const string SettingsExclude = "palette.settings.exclude";
    public const string SettingsExcludeHint = "palette.settings.excludeHint";

    /// <summary>Registers this table before any code of the assembly runs (module initializer, AOT safe).</summary>
    [ModuleInitializer]
    internal static void Initialize() => Register();

    /// <summary>Idempotent: registering the same key with the same texts twice is a no-op.</summary>
    internal static void Register()
    {
        Loc.Register(SourceFiles, "文件", "Files");
        Loc.Register(SourceContent, "内容", "Text");
        Loc.Register(SourceSessions, "会话", "Sessions");
        Loc.Register(SourceCommands, "命令", "Commands");
        Loc.Register(SourceSymbols, "符号", "Symbols");

        Loc.Register(PlaceholderFiles, "按文件名或路径搜索（支持模糊匹配）", "Search files by name or path (fuzzy)");
        Loc.Register(PlaceholderContent, "在工作区文本中搜索", "Search the workspace text");
        Loc.Register(PlaceholderSessions, "按标题、项目或分支搜索会话", "Search sessions by title, project or branch");
        Loc.Register(PlaceholderCommands, "输入命令名", "Type a command name");
        Loc.Register(PlaceholderSymbols, "跳转到当前文件中的符号", "Jump to a symbol in the current file");

        Loc.Register(EmptyFiles, "没有匹配的文件", "No matching files");
        Loc.Register(EmptyContent, "没有匹配的内容", "No matching text");
        Loc.Register(EmptyContentQuery, "输入要搜索的文本", "Type the text to search for");
        Loc.Register(EmptySessions, "没有匹配的会话", "No matching sessions");
        Loc.Register(EmptyCommands, "没有匹配的命令", "No matching commands");
        Loc.Register(EmptySymbols, "没有匹配的符号", "No matching symbols");

        Loc.Register(NoticeNoWorkspace, "没有打开的会话，无法搜索文件。", "No session is open, so files cannot be searched.");
        Loc.Register(NoticeNoDocument, "没有打开的编辑器文件，无法列出符号。", "No editor file is open, so symbols cannot be listed.");
        Loc.Register(
            NoticeSymbolsUnavailable,
            "当前文件没有可用的语言服务器。",
            "No language server is available for this file.");
        Loc.Register(NoticeSearchFailed, "搜索失败：{0}", "The search failed: {0}");
        Loc.Register(NoticeFilesFailed, "读取文件列表失败：{0}", "Reading the file list failed: {0}");
        Loc.Register(NoticeSessionsFailed, "读取会话失败：{0}", "Reading sessions failed: {0}");

        Loc.Register(StatusSearching, "搜索中…", "Searching…");
        Loc.Register(StatusLoading, "读取中…", "Loading…");
        Loc.Register(StatusTruncated, "只显示前 {0} 项", "Showing the first {0}");
        Loc.Register(StatusSymbolDocument, "{0} 中的符号", "Symbols in {0}");

        Loc.Register(PreviewFile, "文件", "File");
        Loc.Register(PreviewContent, "匹配行", "Matching line");
        Loc.Register(PreviewSession, "会话", "Session");
        Loc.Register(PreviewCommand, "命令", "Command");
        Loc.Register(PreviewSymbol, "符号", "Symbol");
        Loc.Register(PreviewNotice, "提示", "Notice");
        Loc.Register(PreviewEmpty, "没有可预览的内容。", "Nothing to preview.");
        Loc.Register(PreviewAroundLine, "第 {0} 行附近", "Around line {0}");

        Loc.Register(HintNavigate, "↑↓ 选择", "↑↓ to choose");
        Loc.Register(HintOpen, "Enter 打开", "Enter to open");
        Loc.Register(HintClose, "Esc 关闭", "Esc to close");
        Loc.Register(HintSources, "> 命令 · # 内容 · : 会话 · @ 符号", "> commands · # text · : sessions · @ symbols");
        Loc.Register(Shortcut, "搜索文件（Ctrl+P）", "Search files (Ctrl+P)");
        Loc.Register(AutomationName, "命令面板", "Command palette");

        Loc.Register(SymbolKindType, "类型", "type");
        Loc.Register(SymbolKindFunction, "函数", "function");
        Loc.Register(SymbolKindMember, "成员", "member");
        Loc.Register(SymbolKindVariable, "变量", "variable");
        Loc.Register(SymbolKindOther, "符号", "symbol");

        Loc.Register(SessionArchived, "已归档", "archived");

        Loc.Register(CategoryNavigate, "跳转", "Go to");
        Loc.Register(CategoryView, "视图", "View");
        Loc.Register(CategoryProject, "项目", "Project");
        Loc.Register(CategorySettings, "设置", "Settings");
        Loc.Register(CategoryGit, "Git", "Git");

        Loc.Register(CommandFiles, "搜索文件…", "Search files…");
        Loc.Register(CommandContent, "在文件中搜索文本…", "Search text in files…");
        Loc.Register(CommandSessions, "搜索会话…", "Search sessions…");
        Loc.Register(CommandSymbols, "跳转到符号…", "Go to symbol in file…");
        Loc.Register(CommandCommands, "显示所有命令", "Show all commands");
        Loc.Register(CommandChanges, "显示当前改动", "Show the current changes");
        Loc.Register(CommandTerminal, "新建终端", "New terminal");
        Loc.Register(CommandToggleProjects, "最小化或展开项目面板", "Minimize or expand the projects panel");
        Loc.Register(CommandToggleFiles, "最小化或展开文件面板", "Minimize or expand the files panel");
        Loc.Register(CommandTogglePreview, "让预览列覆盖主区域", "Let the preview column cover the main area");
        Loc.Register(CommandTodoTab, "打开计划面板", "Open the plan panel");
        Loc.Register(CommandReviewTab, "打开评审面板", "Open the review panel");
        Loc.Register(CommandSpecsTab, "打开规格面板", "Open the spec panel");
        Loc.Register(CommandAddProject, "添加项目…", "Add a project…");
        Loc.Register(CommandRevealGeneral, "在文件管理器中打开通用工作区", "Reveal the general workspace");
        Loc.Register(CommandOpenSettings, "打开设置", "Open settings");
        Loc.Register(CommandReloadSettings, "重新加载设置文件", "Reload the settings file");
        Loc.Register(CommandGitFetch, "获取远端更新", "Fetch from the remote");
        Loc.Register(CommandGitPull, "拉取当前分支", "Pull the current branch");
        Loc.Register(CommandGitPush, "推送当前分支", "Push the current branch");

        Loc.Register(SettingsTitle, "命令面板", "Command palette");
        Loc.Register(SettingsLimit, "每个来源的结果上限", "Results per source");
        Loc.Register(
            SettingsLimitHint,
            "每个来源最多列出多少条（{0}–{1}）；超出时列表会说明只显示了前几条。",
            "How many rows one source may list ({0}–{1}); a longer list says it shows only the first rows.");
        Loc.Register(SettingsExclude, "内容搜索排除的目录", "Directories excluded from content search");
        Loc.Register(
            SettingsExcludeHint,
            "逗号分隔的目录名，在任意层级匹配；留空表示只遵循 .gitignore。",
            "Comma-separated directory names, matched at any depth; empty means .gitignore only.");
    }
}
