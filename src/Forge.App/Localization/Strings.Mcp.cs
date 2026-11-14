using System.Runtime.CompilerServices;
using Forge.Abstractions;
using Forge.Mcp;

namespace Forge.App.Localization;

/// <summary>
/// Texts of the <c>mcp</c> settings section. The section title (<c>settings.mcp.title</c>) and the empty/enabled/disabled
/// keys the read-only section used live in <see cref="InstrumentsStrings"/> and stay registered; everything the new page
/// shows is namespaced <c>settings.mcp.page.*</c>, <c>settings.mcp.row.*</c>, <c>settings.mcp.editor.*</c>,
/// <c>settings.mcp.import.*</c>, <c>settings.mcp.preset.*</c> and <c>settings.mcp.pref.*</c>.
/// </summary>
internal static class McpSettingsStrings
{
    // ------------------------------------------------------------------ page
    public const string PageHintKey = "settings.mcp.page.hint";
    public const string PageAddKey = "settings.mcp.page.add";
    public const string PageImportKey = "settings.mcp.page.import";
    public const string PagePresetsKey = "settings.mcp.page.presets";
    public const string PageReloadKey = "settings.mcp.page.reload";
    public const string PageEmptyKey = "settings.mcp.page.empty";

    // ------------------------------------------------------------------ status
    public const string StatusErrorKey = "settings.mcp.status.error";
    public const string StatusSavedKey = "settings.mcp.status.saved";
    public const string StatusDeletedKey = "settings.mcp.status.deleted";
    public const string StatusReloadedKey = "settings.mcp.status.reloaded";
    public const string StatusImportedKey = "settings.mcp.status.imported";
    public const string StatusUnavailableKey = "settings.mcp.status.unavailable";

    // ------------------------------------------------------------------ diagnostics
    public const string DiagTitleKey = "settings.mcp.diag.title";
    public const string DiagFileKey = "settings.mcp.diag.file";
    public const string DiagKeptServerKey = "settings.mcp.diag.keptServer";
    public const string DiagEntryKey = "settings.mcp.diag.entry";

    // ------------------------------------------------------------------ shared labels
    public const string TransportStdioKey = "settings.mcp.transport.stdio";
    public const string TransportHttpKey = "settings.mcp.transport.http";
    public const string ScopeGlobalKey = "settings.mcp.scope.global";
    public const string ScopeProjectKey = "settings.mcp.scope.project";

    // ------------------------------------------------------------------ row
    public const string RowStoppedKey = "settings.mcp.row.stopped";
    public const string RowStartingKey = "settings.mcp.row.starting";
    public const string RowReadyKey = "settings.mcp.row.ready";
    public const string RowReadyAtKey = "settings.mcp.row.readyAt";
    public const string RowFailedKey = "settings.mcp.row.failed";
    public const string RowEnableKey = "settings.mcp.row.enable";
    public const string RowStartKey = "settings.mcp.row.start";
    public const string RowStopKey = "settings.mcp.row.stop";
    public const string RowRestartKey = "settings.mcp.row.restart";
    public const string RowTestKey = "settings.mcp.row.test";
    public const string RowTestingKey = "settings.mcp.row.testing";
    public const string RowRefreshToolsKey = "settings.mcp.row.refreshTools";
    public const string RowEditKey = "settings.mcp.row.edit";
    public const string RowDeleteKey = "settings.mcp.row.delete";
    public const string RowExpandKey = "settings.mcp.row.expand";
    public const string RowCollapseKey = "settings.mcp.row.collapse";
    public const string RowErrorKey = "settings.mcp.row.error";
    public const string RowStderrKey = "settings.mcp.row.stderr";
    public const string RowToolsKey = "settings.mcp.row.tools";
    public const string RowToolsTruncatedKey = "settings.mcp.row.toolsTruncated";
    public const string RowToolsEmptyKey = "settings.mcp.row.toolsEmpty";
    public const string RowSideEffectReadOnlyKey = "settings.mcp.row.sideEffect.readOnly";
    public const string RowSideEffectWriteKey = "settings.mcp.row.sideEffect.write";
    public const string RowSideEffectExecuteKey = "settings.mcp.row.sideEffect.execute";
    public const string RowTestOkKey = "settings.mcp.row.test.ok";
    public const string RowTestFailedKey = "settings.mcp.row.test.failed";
    public const string RowCapsNoneKey = "settings.mcp.row.caps.none";
    public const string RowCapToolsKey = "settings.mcp.row.cap.tools";
    public const string RowCapPromptsKey = "settings.mcp.row.cap.prompts";
    public const string RowCapResourcesKey = "settings.mcp.row.cap.resources";
    public const string RowCapLoggingKey = "settings.mcp.row.cap.logging";
    public const string RowCapCompletionsKey = "settings.mcp.row.cap.completions";

    // ------------------------------------------------------------------ editor
    public const string EditorTitleAddKey = "settings.mcp.editor.title.add";
    public const string EditorTitleEditKey = "settings.mcp.editor.title.edit";
    public const string EditorNameKey = "settings.mcp.editor.name";
    public const string EditorNamePlaceholderKey = "settings.mcp.editor.namePlaceholder";
    public const string EditorTransportKey = "settings.mcp.editor.transport";
    public const string EditorTransportStdioKey = "settings.mcp.editor.transport.stdio";
    public const string EditorTransportHttpKey = "settings.mcp.editor.transport.http";
    public const string EditorCommandKey = "settings.mcp.editor.command";
    public const string EditorCommandPlaceholderKey = "settings.mcp.editor.commandPlaceholder";
    public const string EditorArgsKey = "settings.mcp.editor.args";
    public const string EditorArgsPlaceholderKey = "settings.mcp.editor.argsPlaceholder";
    public const string EditorCwdKey = "settings.mcp.editor.cwd";
    public const string EditorCwdPlaceholderKey = "settings.mcp.editor.cwdPlaceholder";
    public const string EditorUrlKey = "settings.mcp.editor.url";
    public const string EditorUrlPlaceholderKey = "settings.mcp.editor.urlPlaceholder";
    public const string EditorEnvKey = "settings.mcp.editor.env";
    public const string EditorEnvKeyLabelKey = "settings.mcp.editor.envKey";
    public const string EditorEnvValueLabelKey = "settings.mcp.editor.envValue";
    public const string EditorHeadersKey = "settings.mcp.editor.headers";
    public const string EditorHeadersKeyLabelKey = "settings.mcp.editor.headersKey";
    public const string EditorHeadersValueLabelKey = "settings.mcp.editor.headersValue";
    public const string EditorAddRowKey = "settings.mcp.editor.addRow";
    public const string EditorRemoveRowKey = "settings.mcp.editor.removeRow";
    public const string EditorReadOnlyToolsKey = "settings.mcp.editor.readOnlyTools";
    public const string EditorTimeoutKey = "settings.mcp.editor.timeout";
    public const string EditorSaveTargetKey = "settings.mcp.editor.saveTarget";
    public const string EditorTargetGlobalKey = "settings.mcp.editor.target.global";
    public const string EditorTargetProjectKey = "settings.mcp.editor.target.project";
    public const string EditorTargetLineKey = "settings.mcp.editor.target.line";
    public const string EditorSaveKey = "settings.mcp.editor.save";
    public const string EditorCancelKey = "settings.mcp.editor.cancel";
    public const string EditorIssueKey = "settings.mcp.editor.issue";
    public const string EditorInvalidKey = "settings.mcp.editor.invalid";

    // ------------------------------------------------------------------ validation
    public const string ValidationFieldNameKey = "settings.mcp.validation.field.name";
    public const string ValidationFieldTransportKey = "settings.mcp.validation.field.transport";
    public const string ValidationFieldCommandKey = "settings.mcp.validation.field.command";
    public const string ValidationFieldUrlKey = "settings.mcp.validation.field.url";
    public const string ValidationFieldTimeoutKey = "settings.mcp.validation.field.timeout";
    public const string ValidationFieldEnvKey = "settings.mcp.validation.field.env";
    public const string ValidationFieldHeadersKey = "settings.mcp.validation.field.headers";
    public const string ValidationRequiredKey = "settings.mcp.validation.required";
    public const string ValidationInvalidCharacterKey = "settings.mcp.validation.invalidCharacter";
    public const string ValidationBothSetKey = "settings.mcp.validation.bothSet";
    public const string ValidationNeitherSetKey = "settings.mcp.validation.neitherSet";
    public const string ValidationNotHttpUrlKey = "settings.mcp.validation.notHttpUrl";
    public const string ValidationOutOfRangeKey = "settings.mcp.validation.outOfRange";
    public const string ValidationDuplicateKeyKey = "settings.mcp.validation.duplicateKey";
    public const string ValidationTimeoutNumberKey = "settings.mcp.validation.timeoutNumber";

    // ------------------------------------------------------------------ import
    public const string ImportTitleKey = "settings.mcp.import.title";
    public const string ImportHintKey = "settings.mcp.import.hint";
    public const string ImportSourceMissingKey = "settings.mcp.import.sourceMissing";
    public const string ImportSourceErrorKey = "settings.mcp.import.sourceError";
    public const string ImportExistsKey = "settings.mcp.import.exists";
    public const string ImportSummaryKey = "settings.mcp.import.summary";
    public const string ImportNoneKey = "settings.mcp.import.none";
    public const string ImportNoneSelectedKey = "settings.mcp.import.noneSelected";
    public const string ImportApplyKey = "settings.mcp.import.apply";
    public const string ImportCancelKey = "settings.mcp.import.cancel";

    // ------------------------------------------------------------------ presets
    public const string PresetNeedsPathKey = "settings.mcp.preset.needsPath";
    public const string PresetFilesystemKey = "settings.mcp.preset.filesystem";
    public const string PresetFilesystemDescKey = "settings.mcp.preset.filesystem.desc";
    public const string PresetContext7Key = "settings.mcp.preset.context7";
    public const string PresetContext7DescKey = "settings.mcp.preset.context7.desc";
    public const string PresetMemoryKey = "settings.mcp.preset.memory";
    public const string PresetMemoryDescKey = "settings.mcp.preset.memory.desc";
    public const string PresetPlaywrightKey = "settings.mcp.preset.playwright";
    public const string PresetPlaywrightDescKey = "settings.mcp.preset.playwright.desc";
    public const string PresetGitKey = "settings.mcp.preset.git";
    public const string PresetGitDescKey = "settings.mcp.preset.git.desc";
    public const string PresetFetchKey = "settings.mcp.preset.fetch";
    public const string PresetFetchDescKey = "settings.mcp.preset.fetch.desc";
    public const string PresetTimeKey = "settings.mcp.preset.time";
    public const string PresetTimeDescKey = "settings.mcp.preset.time.desc";

    // ------------------------------------------------------------------ preferences
    public const string PrefHeadingKey = "settings.mcp.pref.heading";
    public const string PrefRequestTimeoutKey = "settings.mcp.pref.requestTimeout";
    public const string PrefRequestTimeoutHintKey = "settings.mcp.pref.requestTimeoutHint";
    public const string PrefReconnectDelayKey = "settings.mcp.pref.reconnectDelay";
    public const string PrefReconnectDelayHintKey = "settings.mcp.pref.reconnectDelayHint";
    public const string PrefToolLimitKey = "settings.mcp.pref.toolLimit";
    public const string PrefToolLimitHintKey = "settings.mcp.pref.toolLimitHint";
    public const string PrefStderrLinesKey = "settings.mcp.pref.stderrLines";
    public const string PrefStderrLinesHintKey = "settings.mcp.pref.stderrLinesHint";

    // ------------------------------------------------------------------ page
    public static string PageHint => Loc.Instance[PageHintKey];

    public static string PageEmpty => Loc.Instance[PageEmptyKey];

    // ------------------------------------------------------------------ status
    public static string StatusError(string message) => Loc.Instance.Format(StatusErrorKey, message);

    public static string StatusSaved => Loc.Instance[StatusSavedKey];

    public static string StatusDeleted(string name) => Loc.Instance.Format(StatusDeletedKey, name);

    public static string StatusReloaded => Loc.Instance[StatusReloadedKey];

    public static string StatusImported(int count) =>
        Loc.Instance.Format(StatusImportedKey, TextFormat.Number(count));

    public static string StatusUnavailable => Loc.Instance[StatusUnavailableKey];

    // ------------------------------------------------------------------ diagnostics
    public static string DiagnosticFile(McpConfigDiagnostic diagnostic) =>
        Loc.Instance.Format(DiagFileKey, diagnostic.Source, diagnostic.Message);

    public static string DiagnosticEntry(string serverName, McpEntryIssue issue) =>
        Loc.Instance.Format(DiagEntryKey, serverName, IssueText(issue));

    /// <summary>A server the manager still runs although its file could not be read: named, with the file to repair.</summary>
    public static string DiagnosticKeptServer(string name, string source) =>
        Loc.Instance.Format(DiagKeptServerKey, name, source);

    // ------------------------------------------------------------------ shared labels
    public static string TransportStdio => Loc.Instance[TransportStdioKey];

    public static string TransportHttp => Loc.Instance[TransportHttpKey];

    public static string ScopeGlobal => Loc.Instance[ScopeGlobalKey];

    public static string ScopeProject => Loc.Instance[ScopeProjectKey];

    // ------------------------------------------------------------------ row
    /// <summary>The localized run-state line of one server, ready time included.</summary>
    public static string StatusOf(McpServerState state) => state.Status switch
    {
        McpServerStatus.Ready => state.ReadyAt is { } at
            ? Loc.Instance.Format(RowReadyAtKey, TextFormat.Clock(at))
            : Loc.Instance[RowReadyKey],
        McpServerStatus.Starting => Loc.Instance[RowStartingKey],
        McpServerStatus.Failed => Loc.Instance[RowFailedKey],
        _ => Loc.Instance[RowStoppedKey],
    };

    public static string RowStart => Loc.Instance[RowStartKey];

    public static string RowStop => Loc.Instance[RowStopKey];

    public static string RowTesting => Loc.Instance[RowTestingKey];

    public static string RowExpand => Loc.Instance[RowExpandKey];

    public static string RowCollapse => Loc.Instance[RowCollapseKey];

    public static string ToolsTruncated(int shown) =>
        Loc.Instance.Format(RowToolsTruncatedKey, TextFormat.Number(shown));

    public static string SideEffect(ToolSideEffect effect) => effect switch
    {
        ToolSideEffect.Write => Loc.Instance[RowSideEffectWriteKey],
        ToolSideEffect.Execute => Loc.Instance[RowSideEffectExecuteKey],
        _ => Loc.Instance[RowSideEffectReadOnlyKey],
    };

    public static string TestOk(string elapsed, int toolCount, string capabilities) =>
        Loc.Instance.Format(RowTestOkKey, elapsed, TextFormat.Number(toolCount), capabilities);

    public static string TestFailed(string elapsed, string error) =>
        Loc.Instance.Format(RowTestFailedKey, elapsed, error);

    /// <summary>The capabilities a test reported, as one line; <see cref="RowCapsNoneKey"/> when the server lists none.</summary>
    public static string Capabilities(McpServerCapabilities capabilities)
    {
        var parts = new List<string>(5);
        if (capabilities.Tools)
        {
            parts.Add(Loc.Instance[RowCapToolsKey]);
        }

        if (capabilities.Prompts)
        {
            parts.Add(Loc.Instance[RowCapPromptsKey]);
        }

        if (capabilities.Resources)
        {
            parts.Add(Loc.Instance[RowCapResourcesKey]);
        }

        if (capabilities.Logging)
        {
            parts.Add(Loc.Instance[RowCapLoggingKey]);
        }

        if (capabilities.Completions)
        {
            parts.Add(Loc.Instance[RowCapCompletionsKey]);
        }

        return parts.Count == 0 ? Loc.Instance[RowCapsNoneKey] : string.Join(" · ", parts);
    }

    // ------------------------------------------------------------------ editor
    public static string EditorTitleAdd => Loc.Instance[EditorTitleAddKey];

    public static string EditorTitleEdit(string name) => Loc.Instance.Format(EditorTitleEditKey, name);

    public static string TargetGlobal(string path) =>
        Loc.Instance.Format(EditorTargetLineKey, Loc.Instance[EditorTargetGlobalKey], path);

    public static string TargetProject(string path) =>
        Loc.Instance.Format(EditorTargetLineKey, Loc.Instance[EditorTargetProjectKey], path);

    public static string EditorInvalid => Loc.Instance[EditorInvalidKey];

    public static string ValidationTimeoutNumber => Loc.Instance[ValidationTimeoutNumberKey];

    // ------------------------------------------------------------------ validation
    /// <summary>One validation problem as a sentence, the field label resolved to the current language.</summary>
    public static string IssueText(McpEntryIssue issue)
    {
        var field = Loc.Instance[FieldKey(issue.Field)];
        return issue.Problem switch
        {
            McpEntryProblem.Required => Loc.Instance.Format(ValidationRequiredKey, field),
            McpEntryProblem.InvalidCharacter => Loc.Instance.Format(ValidationInvalidCharacterKey, field),
            McpEntryProblem.BothSet => Loc.Instance[ValidationBothSetKey],
            McpEntryProblem.NeitherSet => Loc.Instance[ValidationNeitherSetKey],
            McpEntryProblem.NotHttpUrl => Loc.Instance.Format(ValidationNotHttpUrlKey, field, issue.Detail ?? string.Empty),
            McpEntryProblem.OutOfRange => Loc.Instance.Format(ValidationOutOfRangeKey, field, issue.Detail ?? string.Empty),
            McpEntryProblem.DuplicateKey => Loc.Instance.Format(ValidationDuplicateKeyKey, field, issue.Detail ?? string.Empty),
            _ => field,
        };
    }

    private static string FieldKey(McpEntryField field) => field switch
    {
        McpEntryField.Name => ValidationFieldNameKey,
        McpEntryField.Transport => ValidationFieldTransportKey,
        McpEntryField.Command => ValidationFieldCommandKey,
        McpEntryField.Url => ValidationFieldUrlKey,
        McpEntryField.Timeout => ValidationFieldTimeoutKey,
        McpEntryField.Env => ValidationFieldEnvKey,
        McpEntryField.Headers => ValidationFieldHeadersKey,
        _ => ValidationFieldTransportKey,
    };

    // ------------------------------------------------------------------ import
    public static string ImportSourceMissing(string path) => Loc.Instance.Format(ImportSourceMissingKey, path);

    public static string ImportSourceError(string message) => Loc.Instance.Format(ImportSourceErrorKey, message);

    public static string ImportSummary(int selected, int total) =>
        Loc.Instance.Format(ImportSummaryKey, TextFormat.Number(selected), TextFormat.Number(total));

    public static string ImportNoneSelected => Loc.Instance[ImportNoneSelectedKey];

    // ------------------------------------------------------------------ presets
    public static string PresetTitle(string id) => id switch
    {
        "filesystem" => Loc.Instance[PresetFilesystemKey],
        "context7" => Loc.Instance[PresetContext7Key],
        "memory" => Loc.Instance[PresetMemoryKey],
        "playwright" => Loc.Instance[PresetPlaywrightKey],
        "git" => Loc.Instance[PresetGitKey],
        "fetch" => Loc.Instance[PresetFetchKey],
        "time" => Loc.Instance[PresetTimeKey],
        _ => id,
    };

    public static string PresetDescription(string id) => id switch
    {
        "filesystem" => Loc.Instance[PresetFilesystemDescKey],
        "context7" => Loc.Instance[PresetContext7DescKey],
        "memory" => Loc.Instance[PresetMemoryDescKey],
        "playwright" => Loc.Instance[PresetPlaywrightDescKey],
        "git" => Loc.Instance[PresetGitDescKey],
        "fetch" => Loc.Instance[PresetFetchDescKey],
        "time" => Loc.Instance[PresetTimeDescKey],
        _ => string.Empty,
    };

    [ModuleInitializer]
    internal static void Initialize() => Register();

    /// <summary>Idempotent: registering the same key with the same texts twice is a no-op.</summary>
    internal static void Register()
    {
        Loc.Register(
            PageHintKey,
            "在这里管理 mcp.json 里的 MCP 服务器：添加、编辑、启停与测试，或从预设和 Claude Desktop 导入。保存后立即应用。",
            "Manage the MCP servers in mcp.json: add, edit, enable, stop and test them, or import from presets and Claude Desktop. Saving applies at once.");
        Loc.Register(PageAddKey, "添加服务器", "Add server");
        Loc.Register(PageImportKey, "导入", "Import");
        Loc.Register(PagePresetsKey, "预设", "Presets");
        Loc.Register(PageReloadKey, "重新读取", "Re-read files");
        Loc.Register(PageEmptyKey, "还没有配置 MCP 服务器。点击「添加服务器」或从预设开始。", "No MCP server configured yet. Add one or start from a preset.");

        Loc.Register(StatusErrorKey, "操作失败：{0}", "Action failed: {0}");
        Loc.Register(StatusSavedKey, "已保存并应用", "Saved and applied");
        Loc.Register(StatusDeletedKey, "已删除 {0}", "Removed {0}");
        Loc.Register(StatusReloadedKey, "已重新读取配置", "Configuration re-read");
        Loc.Register(StatusImportedKey, "已导入 {0} 台服务器", "Imported {0} servers");
        Loc.Register(StatusUnavailableKey, "MCP 服务不可用，无法管理服务器", "MCP services are unavailable, so servers cannot be managed");

        Loc.Register(DiagTitleKey, "诊断", "Diagnostics");
        Loc.Register(DiagFileKey, "{0}：{1}", "{0}: {1}");
        Loc.Register(DiagEntryKey, "{0} · {1}", "{0} · {1}");
        Loc.Register(
            DiagKeptServerKey,
            "服务器 {0} 仍在运行，但它的文件 {1} 现在读不懂；修好该文件后它会重新出现在列表里。",
            "Server {0} is still running, but its file {1} cannot be read right now; repair that file and it appears in the list again.");

        Loc.Register(TransportStdioKey, "stdio", "stdio");
        Loc.Register(TransportHttpKey, "http", "http");
        Loc.Register(ScopeGlobalKey, "全局", "Global");
        Loc.Register(ScopeProjectKey, "项目", "Project");

        Loc.Register(RowStoppedKey, "已停止", "Stopped");
        Loc.Register(RowStartingKey, "启动中", "Starting");
        Loc.Register(RowReadyKey, "就绪", "Ready");
        Loc.Register(RowReadyAtKey, "就绪 · {0}", "ready · {0}");
        Loc.Register(RowFailedKey, "失败", "Failed");
        Loc.Register(RowEnableKey, "启用", "Enable");
        Loc.Register(RowStartKey, "启动", "Start");
        Loc.Register(RowStopKey, "停止", "Stop");
        Loc.Register(RowRestartKey, "重启", "Restart");
        Loc.Register(RowTestKey, "测试", "Test");
        Loc.Register(RowTestingKey, "测试中…", "Testing...");
        Loc.Register(RowRefreshToolsKey, "刷新工具", "Refresh tools");
        Loc.Register(RowEditKey, "编辑", "Edit");
        Loc.Register(RowDeleteKey, "删除", "Delete");
        Loc.Register(RowExpandKey, "展开", "Expand");
        Loc.Register(RowCollapseKey, "收起", "Collapse");
        Loc.Register(RowErrorKey, "错误", "Error");
        Loc.Register(RowStderrKey, "stderr 尾部", "stderr tail");
        Loc.Register(RowToolsKey, "工具", "Tools");
        Loc.Register(RowToolsTruncatedKey, "列表已截断，只显示前 {0} 个工具。", "The list is truncated to the first {0} tools.");
        Loc.Register(RowToolsEmptyKey, "服务器没有在运行，或没有暴露任何工具。", "The server is not running, or it exposes no tools.");
        Loc.Register(RowSideEffectReadOnlyKey, "只读", "Read-only");
        Loc.Register(RowSideEffectWriteKey, "写入", "Write");
        Loc.Register(RowSideEffectExecuteKey, "执行", "Execute");
        Loc.Register(RowTestOkKey, "测试通过：耗时 {0}，发现 {1} 个工具，能力 {2}", "Test passed: {0}, {1} tools, capabilities {2}");
        Loc.Register(RowTestFailedKey, "测试失败（耗时 {0}）：{1}", "Test failed ({0}): {1}");
        Loc.Register(RowCapsNoneKey, "无", "none");
        Loc.Register(RowCapToolsKey, "tools", "tools");
        Loc.Register(RowCapPromptsKey, "prompts", "prompts");
        Loc.Register(RowCapResourcesKey, "resources", "resources");
        Loc.Register(RowCapLoggingKey, "logging", "logging");
        Loc.Register(RowCapCompletionsKey, "completions", "completions");

        Loc.Register(EditorTitleAddKey, "添加 MCP 服务器", "Add MCP server");
        Loc.Register(EditorTitleEditKey, "编辑 {0}", "Edit {0}");
        Loc.Register(EditorNameKey, "名称", "Name");
        Loc.Register(EditorNamePlaceholderKey, "例如 filesystem", "e.g. filesystem");
        Loc.Register(EditorTransportKey, "传输方式", "Transport");
        Loc.Register(EditorTransportStdioKey, "stdio（本地命令）", "stdio (local command)");
        Loc.Register(EditorTransportHttpKey, "http（Streamable HTTP）", "http (Streamable HTTP)");
        Loc.Register(EditorCommandKey, "命令", "Command");
        Loc.Register(EditorCommandPlaceholderKey, "npx", "npx");
        Loc.Register(EditorArgsKey, "参数（每行一个）", "Arguments (one per line)");
        Loc.Register(EditorArgsPlaceholderKey, "-y", "-y");
        Loc.Register(EditorCwdKey, "工作目录", "Working directory");
        Loc.Register(EditorCwdPlaceholderKey, "默认：Forge 的工作目录", "Default: Forge's working directory");
        Loc.Register(EditorUrlKey, "URL", "URL");
        Loc.Register(EditorUrlPlaceholderKey, "http://127.0.0.1:3000/mcp", "http://127.0.0.1:3000/mcp");
        Loc.Register(EditorEnvKey, "环境变量", "Environment variables");
        Loc.Register(EditorEnvKeyLabelKey, "变量名", "Variable");
        Loc.Register(EditorEnvValueLabelKey, "值", "Value");
        Loc.Register(EditorHeadersKey, "请求头", "Headers");
        Loc.Register(EditorHeadersKeyLabelKey, "名称", "Name");
        Loc.Register(EditorHeadersValueLabelKey, "值", "Value");
        Loc.Register(EditorAddRowKey, "添加一行", "Add a row");
        Loc.Register(EditorRemoveRowKey, "删除此行", "Remove this row");
        Loc.Register(EditorReadOnlyToolsKey, "readOnlyTools（只读工具名，每行一个）", "readOnlyTools (read-only tool names, one per line)");
        Loc.Register(EditorTimeoutKey, "timeoutSeconds（留空表示不限）", "timeoutSeconds (blank = no limit)");
        Loc.Register(EditorSaveTargetKey, "保存到", "Save to");
        Loc.Register(EditorTargetGlobalKey, "全局文件", "Global file");
        Loc.Register(EditorTargetProjectKey, "项目文件", "Project file");
        Loc.Register(EditorTargetLineKey, "{0}：{1}", "{0}: {1}");
        Loc.Register(EditorSaveKey, "保存", "Save");
        Loc.Register(EditorCancelKey, "取消", "Cancel");
        Loc.Register(EditorIssueKey, "校验问题", "Validation issues");
        Loc.Register(EditorInvalidKey, "请先修正校验问题再保存", "Fix the validation issues before saving");

        Loc.Register(ValidationFieldNameKey, "名称", "Name");
        Loc.Register(ValidationFieldTransportKey, "传输方式", "Transport");
        Loc.Register(ValidationFieldCommandKey, "命令", "Command");
        Loc.Register(ValidationFieldUrlKey, "URL", "URL");
        Loc.Register(ValidationFieldTimeoutKey, "超时", "Timeout");
        Loc.Register(ValidationFieldEnvKey, "环境变量", "Environment");
        Loc.Register(ValidationFieldHeadersKey, "请求头", "Headers");
        Loc.Register(ValidationRequiredKey, "{0}不能为空", "{0} is required");
        Loc.Register(ValidationInvalidCharacterKey, "{0}含控制字符或引号", "{0} contains control characters or quotes");
        Loc.Register(ValidationBothSetKey, "command 与 url 不能同时填写", "command and url cannot both be set");
        Loc.Register(ValidationNeitherSetKey, "command 与 url 必须填写一个", "either command or url must be set");
        Loc.Register(ValidationNotHttpUrlKey, "{0} 不是有效的 http(s) 地址：{1}", "{0} is not a valid http(s) address: {1}");
        Loc.Register(ValidationOutOfRangeKey, "{0} 超出范围（0–{1} 秒）", "{0} is out of range (0–{1} seconds)");
        Loc.Register(ValidationDuplicateKeyKey, "{0}含重复的键：{1}", "{0} has a duplicate key: {1}");
        Loc.Register(ValidationTimeoutNumberKey, "timeoutSeconds 必须是数字或留空", "timeoutSeconds must be a number or blank");

        Loc.Register(ImportTitleKey, "导入 MCP 服务器", "Import MCP servers");
        Loc.Register(
            ImportHintKey,
            "从 Claude Code（.claude.json）、Claude Desktop（claude_desktop_config.json）与 Cursor（.cursor/mcp.json）读取服务器；勾选后写入目标文件，与现有同名条目会覆盖，同名只取先找到的那份。",
            "Reads servers from Claude Code (.claude.json), Claude Desktop (claude_desktop_config.json) and Cursor (.cursor/mcp.json); checked servers are written to the target file, replace same-name entries, and a name seen twice is taken from the first source.");
        Loc.Register(
            ImportSourceMissingKey,
            "三个导入源都没找到：{0}。",
            "None of the three import sources was found: {0}.");
        Loc.Register(ImportSourceErrorKey, "读取导入源失败：{0}", "Failed to read the import source: {0}");
        Loc.Register(ImportExistsKey, "已存在", "exists");
        Loc.Register(ImportSummaryKey, "已勾选 {0} / 共 {1} 台", "{0} selected of {1}");
        Loc.Register(ImportNoneKey, "源文件里没有可导入的服务器", "The source file has no servers to import");
        Loc.Register(ImportNoneSelectedKey, "没有勾选要导入的服务器", "Select at least one server to import");
        Loc.Register(ImportApplyKey, "导入所选", "Import selected");
        Loc.Register(ImportCancelKey, "取消", "Cancel");

        Loc.Register(PresetNeedsPathKey, "需要目录", "needs a directory");
        Loc.Register(PresetFilesystemKey, "文件系统", "Filesystem");
        Loc.Register(
            PresetFilesystemDescKey,
            "npx @modelcontextprotocol/server-filesystem：读写所选目录下的文件。",
            "npx @modelcontextprotocol/server-filesystem: reads and writes files under a directory you choose.");
        Loc.Register(PresetContext7Key, "Context7", "Context7");
        Loc.Register(
            PresetContext7DescKey,
            "npx @upstash/context7-mcp：为编程问题提供最新的库文档。",
            "npx @upstash/context7-mcp: up-to-date library docs for coding questions.");
        Loc.Register(PresetMemoryKey, "记忆（知识图谱）", "Memory (knowledge graph)");
        Loc.Register(
            PresetMemoryDescKey,
            "npx @modelcontextprotocol/server-memory：基于知识图谱的持久记忆。",
            "npx @modelcontextprotocol/server-memory: persistent memory over a knowledge graph.");
        Loc.Register(PresetPlaywrightKey, "Playwright", "Playwright");
        Loc.Register(
            PresetPlaywrightDescKey,
            "npx @playwright/mcp：浏览器自动化，截图、点击、表单。",
            "npx @playwright/mcp: browser automation - screenshots, clicks, forms.");
        Loc.Register(PresetGitKey, "Git", "Git");
        Loc.Register(
            PresetGitDescKey,
            "uvx mcp-server-git：对所选仓库的 Git 操作。",
            "uvx mcp-server-git: git operations on a repository you choose.");
        Loc.Register(PresetFetchKey, "Fetch", "Fetch");
        Loc.Register(PresetFetchDescKey, "uvx mcp-server-fetch：把网页抓成 markdown。", "uvx mcp-server-fetch: fetches web pages as markdown.");
        Loc.Register(PresetTimeKey, "时间", "Time");
        Loc.Register(
            PresetTimeDescKey,
            "uvx mcp-server-time：当前时间与时区换算。",
            "uvx mcp-server-time: current time and timezone conversion.");

        Loc.Register(PrefHeadingKey, "默认参数", "Defaults");
        Loc.Register(PrefRequestTimeoutKey, "请求超时（秒）", "Request timeout (s)");
        Loc.Register(
            PrefRequestTimeoutHintKey,
            "5–600，默认 60；服务器自己的 timeoutSeconds 会覆盖它。",
            "5-600, default 60; a server's own timeoutSeconds overrides it.");
        Loc.Register(PrefReconnectDelayKey, "重连初始间隔（秒）", "Initial reconnect delay (s)");
        Loc.Register(
            PrefReconnectDelayHintKey,
            "1–60，默认 1：服务器失败后的第一次重连等待时间。",
            "1-60, default 1: the first reconnect wait after a server fails.");
        Loc.Register(PrefToolLimitKey, "每回合工具数上限", "Tool limit per turn");
        Loc.Register(PrefToolLimitHintKey, "0–1000，默认 0 表示不限。", "0-1000, default 0 = unlimited.");
        Loc.Register(PrefStderrLinesKey, "stderr 显示行数", "stderr lines shown");
        Loc.Register(PrefStderrLinesHintKey, "20–2000，默认 200。", "20-2000, default 200.");
    }
}
