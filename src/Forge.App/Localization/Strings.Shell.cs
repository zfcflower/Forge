using System.Runtime.CompilerServices;

namespace Forge.App.Localization;

/// <summary>
/// The shell's table: title bar and window buttons, confirmation dialogs, workbench panel and tab titles, panel title-bar button tooltips, tab context menus, the status bar, provider status and startup diagnostics.
/// <para>Keys: <c>shell.*</c>, <c>workbench.*</c>, <c>status.*</c>, <c>provider.*</c>, <c>diag.*</c>. Each key has a Chinese and an English text; parameterized texts are exposed as typed accessor
/// methods (formatted through <see cref="Loc.Format"/>), parameterless ones as properties that read
/// <see cref="Loc.Instance"/>, so callers never cache a resolved string across a language switch.</para>
/// <para>Texts that are computed now and shown later (startup diagnostics, the provider status) are exposed as
/// <see cref="LocText"/>, which resolves the key when it is displayed; an exception message or a path passed to one of
/// them is <see cref="LocText.Literal">literal</see> and never translated.</para>
/// </summary>
internal static class ShellStrings
{
    // ---------------------------------------------------------------- shell (title bar, app chrome)

    public const string NoOpenSessionKey = "shell.noOpenSession";
    public const string OpenChangesTooltipKey = "shell.openChangesTooltip";
    public const string SearchPlaceholderKey = "shell.searchPlaceholder";
    public const string SearchNameKey = "shell.search";
    public const string SettingsKey = "shell.settings";
    public const string CloseSettingsKey = "shell.closeSettings";

    // ---------------------------------------------------------------- window captions

    public const string MinimizeKey = "window.minimize";
    public const string MaximizeKey = "window.maximize";
    public const string RestoreKey = "window.restore";
    public const string CloseKey = "window.close";
    public const string CloseWindowKey = "window.closeWindow";

    // ---------------------------------------------------------------- workbench titles

    public const string ProjectsTitleKey = "workbench.projects";
    public const string GeneralChatsTitleKey = "workbench.generalChats";
    public const string FilesTitleKey = "workbench.files";
    public const string DocumentsTitleKey = "workbench.documents";
    public const string SubAgentsTitleKey = "workbench.subAgents";
    public const string PreviewTitleKey = "workbench.preview";
    public const string TodoTitleKey = "workbench.todo";
    public const string ReviewTitleKey = "workbench.review";
    public const string SpecTreeTitleKey = "workbench.specTree";
    public const string ChangesTitleKey = "workbench.changes";

    // ---------------------------------------------------------------- workbench title-bar buttons

    public const string AutoHideKey = "workbench.autoHide";
    public const string RestorePanelKey = "workbench.restorePanel";
    public const string MinimizePanelKey = "workbench.minimizePanel";
    public const string MoreTabsKey = "workbench.moreTabs";
    public const string FloatKey = "workbench.float";
    public const string DockBackKey = "workbench.dockBack";
    public const string CoverKey = "workbench.cover";
    public const string UncoverKey = "workbench.uncover";

    // ---------------------------------------------------------------- tab context menu

    public const string OpenInNewWindowKey = "workbench.openInNewWindow";
    public const string CloseTabKey = "workbench.closeTab";
    public const string CloseOtherTabsKey = "workbench.closeOtherTabs";

    // ---------------------------------------------------------------- status bar

    public const string HostStartingKey = "status.host.starting";
    public const string HostRunningKey = "status.host.running";
    public const string HostStoppedKey = "status.host.stopped";
    public const string HostFailedKey = "status.host.failed";
    /// <summary>Just the noun: the window header shows it next to a coloured dot, without the state spelled out.</summary>
    public const string HostNameKey = "status.host.name";
    public const string PluginsUnknownKey = "status.plugins.unknown";
    public const string PluginsKey = "status.plugins";
    public const string McpNoneKey = "status.mcp.none";
    public const string McpKey = "status.mcp";
    public const string McpStateKey = "status.mcp.state";
    public const string ApprovalsUnknownKey = "status.approvals.unknown";
    public const string ApprovalsKey = "status.approvals";
    public const string ProviderUnknownKey = "status.provider.unknown";
    public const string ProviderTextKey = "status.provider";
    public const string ProcessIdKey = "status.processId";

    // ---------------------------------------------------------------- provider settings

    public const string ProviderReadyKey = "provider.ready";
    public const string ProviderNoKeyKey = "provider.noKey";
    public const string ProviderSettingsMissingKey = "provider.settingsMissing";
    public const string ProviderSettingsUnreadableKey = "provider.settingsUnreadable";
    public const string ProviderSectionMissingKey = "provider.sectionMissing";
    public const string ProviderInvalidKey = "provider.invalid";
    public const string ProviderDetailKey = "provider.detail";
    public const string KeyFromEnvironmentKey = "provider.key.environment";
    public const string KeyFromFileKey = "provider.key.file";
    public const string KeyMissingKey = "provider.key.missing";

    // ---------------------------------------------------------------- diagnostics (computed now, shown later)

    public const string DatabaseProblemKey = "diag.database";
    public const string ProjectsProblemKey = "diag.projects";
    public const string WorkspaceServiceProblemKey = "diag.workspaceService";
    public const string McpServiceProblemKey = "diag.mcpService";
    public const string EditorServiceProblemKey = "diag.editorService";
    public const string HostStartFailedKey = "diag.hostStartFailed";
    public const string JitHostMissingKey = "diag.jitHostMissing";
    public const string JitComponentMissingKey = "diag.jitComponentMissing";
    public const string GitInitFailedKey = "diag.gitInitFailed";
    public const string InitializationFailedKey = "diag.initializationFailed";
    public const string McpDetailKey = "diag.mcpDetail";
    public const string EditorDetailKey = "diag.editorDetail";
    public const string EditorDetailWithProblemKey = "diag.editorDetailWithProblem";
    public const string PreferencesReadKey = "diag.preferences.read";
    public const string PreferencesInvalidJsonKey = "diag.preferences.invalidJson";
    public const string PreferencesNotObjectKey = "diag.preferences.notObject";
    public const string PreferencesSectionNotObjectKey = "diag.preferences.sectionNotObject";
    public const string PreferencesSectionInvalidKey = "diag.preferences.sectionInvalid";
    public const string PreferencesLanguageUnknownKey = "diag.preferences.languageUnknown";

    // ---------------------------------------------------------------- dialogs

    public const string OkKey = "dialog.ok";
    public const string CancelKey = "dialog.cancel";
    public const string CreateKey = "dialog.create";
    public const string GitInitKey = "dialog.gitInit";
    public const string PickRepositoryTitleKey = "dialog.pickRepository";
    public const string NotRepositoryTitleKey = "dialog.notRepositoryTitle";
    public const string NotRepositoryMessageKey = "dialog.notRepositoryMessage";
    public const string NewSessionTitleKey = "dialog.newSessionTitle";
    public const string NewSessionQuestionKey = "dialog.newSessionQuestion";

    // ---------------------------------------------------------------- shell accessors

    public static string NoOpenSession => Loc.Instance[NoOpenSessionKey];

    public static string OpenChangesTooltip => Loc.Instance[OpenChangesTooltipKey];

    public static string SearchPlaceholder => Loc.Instance[SearchPlaceholderKey];

    public static string SearchName => Loc.Instance[SearchNameKey];

    public static string Settings => Loc.Instance[SettingsKey];

    public static string CloseSettings => Loc.Instance[CloseSettingsKey];

    public static string Minimize => Loc.Instance[MinimizeKey];

    public static string Maximize => Loc.Instance[MaximizeKey];

    public static string Restore => Loc.Instance[RestoreKey];

    public static string Close => Loc.Instance[CloseKey];

    public static string CloseWindow => Loc.Instance[CloseWindowKey];

    // ---------------------------------------------------------------- workbench accessors

    public static string ProjectsTitle => Loc.Instance[ProjectsTitleKey];

    /// <summary>The general-chats panel above the projects; the same words as the row it used to head inside one tree.</summary>
    public static string GeneralChatsTitle => Loc.Instance[GeneralChatsTitleKey];

    public static string FilesTitle => Loc.Instance[FilesTitleKey];

    public static string DocumentsTitle => Loc.Instance[DocumentsTitleKey];

    public static string SubAgentsTitle => Loc.Instance[SubAgentsTitleKey];

    public static string PreviewTitle => Loc.Instance[PreviewTitleKey];

    public static string TodoTitle => Loc.Instance[TodoTitleKey];

    public static string ReviewTitle => Loc.Instance[ReviewTitleKey];

    public static string SpecTreeTitle => Loc.Instance[SpecTreeTitleKey];

    public static string ChangesTitle => Loc.Instance[ChangesTitleKey];

    public static string AutoHide => Loc.Instance[AutoHideKey];

    public static string RestorePanel => Loc.Instance[RestorePanelKey];
    public static string MinimizePanel => Loc.Instance[MinimizePanelKey];

    public static string MoreTabs => Loc.Instance[MoreTabsKey];

    public static string Float => Loc.Instance[FloatKey];

    public static string DockBack => Loc.Instance[DockBackKey];

    public static string Cover => Loc.Instance[CoverKey];

    public static string Uncover => Loc.Instance[UncoverKey];

    public static string OpenInNewWindow => Loc.Instance[OpenInNewWindowKey];

    public static string CloseTab => Loc.Instance[CloseTabKey];

    public static string CloseOtherTabs => Loc.Instance[CloseOtherTabsKey];

    // ---------------------------------------------------------------- status bar accessors

    public static string PluginsUnknown => Loc.Instance[PluginsUnknownKey];

    public static string Plugins(int plugins, int tools) =>
        Loc.Instance.Format(PluginsKey, TextFormat.Number(plugins), TextFormat.Number(tools));

    public static string McpNone => Loc.Instance[McpNoneKey];

    public static string Mcp(int configured) => Loc.Instance.Format(McpKey, TextFormat.Number(configured));

    public static string McpState(int configured, int ready) =>
        Loc.Instance.Format(McpStateKey, TextFormat.Number(configured), TextFormat.Number(ready));

    public static string ApprovalsUnknown => Loc.Instance[ApprovalsUnknownKey];

    public static string Approvals(int records) => Loc.Instance.Format(ApprovalsKey, TextFormat.Number(records));

    public static string ProviderUnknown => Loc.Instance[ProviderUnknownKey];

    /// <summary>"提供方：…" around the provider status; the status may itself be a <see cref="LocText"/>.</summary>
    public static string ProviderText(object status) => Loc.Instance.Format(ProviderTextKey, status);

    public static string ProcessId(int id) => Loc.Instance.Format(ProcessIdKey, TextFormat.Id(id));

    // ---------------------------------------------------------------- provider accessors

    public static LocText ProviderReady(string summary) => new(ProviderReadyKey, summary);

    public static LocText ProviderNoKey(string summary) => new(ProviderNoKeyKey, summary);

    public static LocText ProviderSettingsMissing() => new(ProviderSettingsMissingKey);

    public static LocText ProviderSettingsUnreadable(string message) => new(ProviderSettingsUnreadableKey, message);

    public static LocText ProviderSectionMissing() => new(ProviderSectionMissingKey);

    public static LocText ProviderInvalid(string message) => new(ProviderInvalidKey, message);

    /// <summary>"<c>kind · model · key: …</c>": the provider section's detail row, rebuilt on every language switch.</summary>
    public static string ProviderDetail(string summary, bool hasKey, bool fromEnvironment) =>
        Loc.Instance.Format(
            ProviderDetailKey,
            summary,
            Loc.Instance[
                hasKey
                    ? fromEnvironment ? KeyFromEnvironmentKey : KeyFromFileKey
                    : KeyMissingKey]);

    // ---------------------------------------------------------------- diagnostics accessors

    public static LocText DatabaseProblem(string message) => new(DatabaseProblemKey, message);

    public static LocText ProjectsProblem(string message) => new(ProjectsProblemKey, message);

    public static LocText WorkspaceServiceProblem(string message) => new(WorkspaceServiceProblemKey, message);

    public static LocText McpServiceProblem(string message) => new(McpServiceProblemKey, message);

    public static LocText EditorServiceProblem(string message) => new(EditorServiceProblemKey, message);

    public static LocText HostStartFailed(string message) => new(HostStartFailedKey, message);

    /// <summary>The JIT host assembly itself was not found; <paramref name="assembly"/> and the variable are literals.</summary>
    public static LocText JitHostMissing(string assembly, string variable) =>
        new(JitHostMissingKey, assembly, variable);

    /// <summary>A JIT-side helper assembly (worker, executor) was not found.</summary>
    public static LocText JitComponentMissing(string assembly, string variable) =>
        new(JitComponentMissingKey, assembly, variable);

    public static LocText GitInitFailed(string message) => new(GitInitFailedKey, message);

    public static LocText InitializationFailed(string message) => new(InitializationFailedKey, message);

    public static string McpDetail(int configured, int diagnostics) =>
        Loc.Instance.Format(McpDetailKey, TextFormat.Number(configured), TextFormat.Number(diagnostics));

    public static string EditorDetail(int languages, int grammars, string? problem) =>
        problem is null
            ? Loc.Instance.Format(EditorDetailKey, TextFormat.Number(languages), TextFormat.Number(grammars))
            : Loc.Instance.Format(EditorDetailWithProblemKey, TextFormat.Number(languages), TextFormat.Number(grammars), problem);

    public static LocText PreferencesReadFailed(string message) => new(PreferencesReadKey, message);

    public static LocText PreferencesInvalidJson(string message) => new(PreferencesInvalidJsonKey, message);

    public static LocText PreferencesNotObject() => new(PreferencesNotObjectKey);

    public static LocText PreferencesSectionNotObject() => new(PreferencesSectionNotObjectKey);

    public static LocText PreferencesSectionInvalid(string message) => new(PreferencesSectionInvalidKey, message);

    public static LocText PreferencesLanguageUnknown(string? language) => new(PreferencesLanguageUnknownKey, language);

    // ---------------------------------------------------------------- dialog accessors

    public static string Ok => Loc.Instance[OkKey];

    public static string Cancel => Loc.Instance[CancelKey];

    public static string Create => Loc.Instance[CreateKey];

    public static string GitInit => Loc.Instance[GitInitKey];

    public static string PickRepositoryTitle => Loc.Instance[PickRepositoryTitleKey];

    public static string NotRepositoryTitle => Loc.Instance[NotRepositoryTitleKey];

    /// <summary>The folder is a path the user picked: an argument, not translated.</summary>
    public static string NotRepositoryMessage(string folder) => Loc.Instance.Format(NotRepositoryMessageKey, folder);

    public static string NewSessionTitle => Loc.Instance[NewSessionTitleKey];

    public static string NewSessionQuestion => Loc.Instance[NewSessionQuestionKey];

    /// <summary>Registers this table before any code of the assembly runs (module initializer, AOT safe).</summary>
    [ModuleInitializer]
    internal static void Initialize() => Register();

    /// <summary>Idempotent: registering the same key with the same texts twice is a no-op.</summary>
    internal static void Register()
    {
        Loc.Register(NoOpenSessionKey, "没有打开的会话", "No open session");
        Loc.Register(OpenChangesTooltipKey, "打开改动文档", "Open the changes document");
        Loc.Register(SearchPlaceholderKey, "搜索（即将可用）", "Search (coming soon)");
        Loc.Register(SearchNameKey, "搜索", "Search");
        Loc.Register(SettingsKey, "设置", "Settings");
        Loc.Register(CloseSettingsKey, "关闭设置", "Close settings");

        Loc.Register(MinimizeKey, "最小化", "Minimize");
        Loc.Register(MaximizeKey, "最大化", "Maximize");
        Loc.Register(RestoreKey, "还原", "Restore");
        Loc.Register(CloseKey, "关闭", "Close");
        Loc.Register(CloseWindowKey, "关闭窗口", "Close window");

        Loc.Register(ProjectsTitleKey, "项目", "Projects");
        Loc.Register(GeneralChatsTitleKey, "常规对话", "General chats");
        Loc.Register(FilesTitleKey, "文件", "Files");
        Loc.Register(DocumentsTitleKey, "会话", "Sessions");
        Loc.Register(SubAgentsTitleKey, "子 Agent", "Sub-agents");
        Loc.Register(PreviewTitleKey, "预览", "Preview");
        Loc.Register(TodoTitleKey, "TODO", "TODO");
        Loc.Register(ReviewTitleKey, "评审", "Review");
        Loc.Register(SpecTreeTitleKey, "规格树", "Spec tree");
        Loc.Register(ChangesTitleKey, "± 改动", "± Changes");

        Loc.Register(AutoHideKey, "自动隐藏", "Auto-hide");
        Loc.Register(RestorePanelKey, "还原", "Restore");
        Loc.Register(MinimizePanelKey, "缩小", "Minimize");
        Loc.Register(MoreTabsKey, "被遮挡的标签", "Hidden tabs");
        Loc.Register(FloatKey, "浮动到新窗口", "Float to a new window");
        Loc.Register(DockBackKey, "停靠回主窗口", "Dock back to the main window");
        Loc.Register(CoverKey, "铺满主区", "Cover main area");
        Loc.Register(UncoverKey, "退出铺满", "Exit cover");

        Loc.Register(OpenInNewWindowKey, "在新窗口打开", "Open in new window");
        Loc.Register(CloseTabKey, "关闭", "Close");
        Loc.Register(CloseOtherTabsKey, "关闭其他标签", "Close other tabs");

        Loc.Register(HostStartingKey, "扩展宿主：启动中", "Extension host: starting");
        Loc.Register(HostRunningKey, "扩展宿主：运行中", "Extension host: running");
        Loc.Register(HostStoppedKey, "扩展宿主：已停止", "Extension host: stopped");
        Loc.Register(HostFailedKey, "扩展宿主：失败", "Extension host: failed");
        Loc.Register(HostNameKey, "扩展宿主", "Extension host");
        Loc.Register(PluginsUnknownKey, "插件：未知", "Plugins: unknown");
        Loc.Register(PluginsKey, "插件 {0} · 工具 {1}", "Plugins {0} · Tools {1}");
        Loc.Register(McpNoneKey, "MCP：0 个配置", "MCP: 0 configured");
        Loc.Register(McpKey, "MCP：{0} 个配置", "MCP: {0} configured");
        Loc.Register(McpStateKey, "MCP：{0} 个配置 · {1} 个就绪", "MCP: {0} configured · {1} ready");
        Loc.Register(ApprovalsUnknownKey, "审批：不可用", "Approvals: unavailable");
        Loc.Register(ApprovalsKey, "审批 {0} 条", "Approvals {0}");
        Loc.Register(ProviderUnknownKey, "提供方：未知", "Provider: unknown");
        Loc.Register(ProviderTextKey, "提供方：{0}", "Provider: {0}");
        Loc.Register(ProcessIdKey, "进程 {0}", "Process {0}");

        Loc.Register(ProviderReadyKey, "已就绪：{0}", "Ready: {0}");
        Loc.Register(ProviderNoKeyKey, "已配置但没有 API 密钥：{0}", "Configured without an API key: {0}");
        Loc.Register(ProviderSettingsMissingKey, "未配置提供方（找不到 settings.json）", "No provider configured (settings.json not found)");
        Loc.Register(ProviderSettingsUnreadableKey, "未配置提供方（settings.json 无法读取：{0}）", "No provider configured (settings.json cannot be read: {0})");
        Loc.Register(ProviderSectionMissingKey, "未配置提供方（settings.json 没有 provider 段）", "No provider configured (the provider section is missing from settings.json)");
        Loc.Register(ProviderInvalidKey, "未配置提供方（{0}）", "No provider configured ({0})");
        Loc.Register(ProviderDetailKey, "{0} · 密钥：{1}", "{0} · API key: {1}");
        Loc.Register(KeyFromEnvironmentKey, "来自环境变量", "from the environment");
        Loc.Register(KeyFromFileKey, "来自 settings.json", "from settings.json");
        Loc.Register(KeyMissingKey, "缺失", "missing");

        Loc.Register(DatabaseProblemKey, "forge.db：{0}", "forge.db: {0}");
        Loc.Register(ProjectsProblemKey, "项目：{0}", "Projects: {0}");
        Loc.Register(WorkspaceServiceProblemKey, "工作区服务不可用：{0}", "Workspace service unavailable: {0}");
        Loc.Register(McpServiceProblemKey, "MCP 服务不可用：{0}", "MCP service unavailable: {0}");
        Loc.Register(EditorServiceProblemKey, "编辑器服务不可用：{0}", "Editor service unavailable: {0}");
        Loc.Register(HostStartFailedKey, "扩展宿主未能启动：{0}", "The extension host could not start: {0}");
        Loc.Register(
            JitHostMissingKey,
            "找不到 {0}.dll。请先构建 JIT 部分(dotnet build Forge.slnx)，或把 {1} 指向它所在的目录。",
            "{0}.dll was not found. Build the JIT part first (dotnet build Forge.slnx), or point {1} at the directory that holds it.");
        Loc.Register(
            JitComponentMissingKey,
            "找不到 {0}.dll；请先构建 JIT 部分，或设置 {1}。",
            "{0}.dll was not found; build the JIT part first, or set {1}.");
        Loc.Register(GitInitFailedKey, "在常规工作区执行 git init 失败：{0}", "git init failed in the general workspace: {0}");
        Loc.Register(InitializationFailedKey, "初始化失败：{0}", "Initialization failed: {0}");
        Loc.Register(McpDetailKey, "{0} 个配置 · {1} 条配置诊断", "{0} configured · {1} configuration diagnostics");
        Loc.Register(EditorDetailKey, "{0} 种语言 · {1} 份语法", "{0} languages · {1} grammars");
        Loc.Register(EditorDetailWithProblemKey, "{0} 种语言 · {1} 份语法 · {2}", "{0} languages · {1} grammars · {2}");
        Loc.Register(PreferencesReadKey, "读取 settings.json 失败：{0}", "Reading settings.json failed: {0}");
        Loc.Register(PreferencesInvalidJsonKey, "settings.json 不是有效的 JSON：{0}", "settings.json is not valid JSON: {0}");
        Loc.Register(PreferencesNotObjectKey, "settings.json 的顶层不是 JSON 对象。", "The top level of settings.json is not a JSON object.");
        Loc.Register(PreferencesSectionNotObjectKey, "settings.json 的 preferences 段不是 JSON 对象。", "The preferences section of settings.json is not a JSON object.");
        Loc.Register(PreferencesSectionInvalidKey, "settings.json 的 preferences 段无效：{0}", "The preferences section of settings.json is invalid: {0}");
        Loc.Register(PreferencesLanguageUnknownKey, "settings.json 的 preferences.language 无法识别：{0}", "The language in preferences.language of settings.json is not recognized: {0}");

        Loc.Register(OkKey, "确定", "OK");
        Loc.Register(CancelKey, "取消", "Cancel");
        Loc.Register(CreateKey, "新建", "Create");
        Loc.Register(GitInitKey, "初始化仓库", "Initialize repository");
        Loc.Register(PickRepositoryTitleKey, "添加 git 仓库", "Add a git repository");
        Loc.Register(NotRepositoryTitleKey, "不是 git 仓库", "Not a git repository");
        Loc.Register(NotRepositoryMessageKey, "{0}\n\n这个文件夹不是 git 仓库，要在这里执行 git init 吗？", "{0}\n\nThis folder is not a git repository. Run git init here?");
        Loc.Register(NewSessionTitleKey, "新建会话", "New session");
        Loc.Register(NewSessionQuestionKey, "要新建一个会话吗？", "Create a new session?");
    }
}
