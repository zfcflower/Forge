using System.Runtime.CompilerServices;

namespace Forge.App.Localization;

/// <summary>
/// The CodeMode (<c>run_code</c>) script surface (wave W14): the "script tools" block of the <c>tools</c> settings
/// section, which says which tools a script may call, how many of those calls one script keeps in flight and how much of
/// their text the model sees. Keys: <c>codeMode.*</c>.
/// <para>Only wording lives here. The numbers are the fields' own values, clamped by
/// <c>Forge.App.Preferences.AppPreferences</c>, so a language switch re-renders a label without re-reading anything.</para>
/// </summary>
internal static class CodeModeStrings
{
    // ---------------------------------------------------------------- the `tools` section's script block
    public const string SettingsHeadingKey = "codeMode.settings.heading";
    public const string SettingsHintKey = "codeMode.settings.hint";
    public const string SettingsConcurrencyKey = "codeMode.settings.concurrency";
    public const string SettingsConcurrencyHintKey = "codeMode.settings.concurrencyHint";
    public const string SettingsResultCharsKey = "codeMode.settings.resultChars";
    public const string SettingsResultCharsHintKey = "codeMode.settings.resultCharsHint";
    public const string SettingsInnerResultCharsKey = "codeMode.settings.innerResultChars";
    public const string SettingsInnerResultCharsHintKey = "codeMode.settings.innerResultCharsHint";
    public const string SettingsToolsKey = "codeMode.settings.tools";
    public const string SettingsToolsHintKey = "codeMode.settings.toolsHint";

    /// <summary>Registers this table before any code of the assembly runs (module initializer, AOT safe).</summary>
    [ModuleInitializer]
    internal static void Initialize() => Register();

    /// <summary>"脚本工具(run_code)": the heading of the block.</summary>
    public static string SettingsHeading => Loc.Instance[SettingsHeadingKey];

    /// <summary>What a script can call and what it never can: read-only, approval-free tools only.</summary>
    public static string SettingsHint => Loc.Instance[SettingsHintKey];

    /// <summary>"并发调用上限": the label of the concurrency field.</summary>
    public static string SettingsConcurrency => Loc.Instance[SettingsConcurrencyKey];

    /// <summary>The concurrency field's caption: the 1–32 range and what raising it costs.</summary>
    public static string SettingsConcurrencyHint => Loc.Instance[SettingsConcurrencyHintKey];

    /// <summary>"结果字符上限": the label of the <c>run_code</c> result field.</summary>
    public static string SettingsResultChars => Loc.Instance[SettingsResultCharsKey];

    /// <summary>The result field's caption: the 1000–400000 range and that the rest is cut off.</summary>
    public static string SettingsResultCharsHint => Loc.Instance[SettingsResultCharsHintKey];

    /// <summary>"单次调用结果上限": the label of the per-call result field.</summary>
    public static string SettingsInnerResultChars => Loc.Instance[SettingsInnerResultCharsKey];

    /// <summary>The per-call result field's caption: the 500–400000 range and that the rest is cut off.</summary>
    public static string SettingsInnerResultCharsHint => Loc.Instance[SettingsInnerResultCharsHintKey];

    /// <summary>"可见工具名单": the label of the tool-name field.</summary>
    public static string SettingsTools => Loc.Instance[SettingsToolsKey];

    /// <summary>
    /// The tool-name field's caption: comma/semicolon/newline separated, a trailing <c>*</c> as a wildcard, blank means
    /// every candidate, and the candidates are the built-in read-only file tools, the web tools, enabled read-only MCP
    /// tools and plugin tools.
    /// </summary>
    public static string SettingsToolsHint => Loc.Instance[SettingsToolsHintKey];

    internal static void Register()
    {
        Loc.Register(SettingsHeadingKey, "脚本工具(run_code)", "Script tools (run_code)");
        Loc.Register(
            SettingsHintKey,
            "脚本里的 Tools.Call 只能调到只读、免审批的工具:内置文件工具 read/ls/find/grep、web_fetch/web_search、已启用的 MCP 只读工具与插件工具。写入与执行类(bash、write、edit)永不暴露给脚本。",
            "A script's Tools.Call reaches read-only tools that need no approval: the built-in read/ls/find/grep, web_fetch/web_search, enabled read-only MCP tools and plugin tools. Writing and execution tools (bash, write, edit) are never exposed to a script.");
        Loc.Register(SettingsConcurrencyKey, "并发调用上限", "Concurrent calls");
        Loc.Register(
            SettingsConcurrencyHintKey,
            "一段脚本同时最多有多少个 Tools.Call 在飞(1–32)。调高扇出更快,也更快用完提供商的配额。",
            "How many Tools.Call requests one script keeps in flight at once (1-32). Raising it fans out faster and uses up the provider's quota faster.");
        Loc.Register(SettingsResultCharsKey, "结果字符上限", "Result characters");
        Loc.Register(
            SettingsResultCharsHintKey,
            "run_code 交回模型的最终结果最多多少字符(1000–400000),超出部分被截断。",
            "How many characters of the final result run_code hands back to the model (1000-400000); anything beyond is cut off.");
        Loc.Register(SettingsInnerResultCharsKey, "单次调用结果上限", "Per-call result characters");
        Loc.Register(
            SettingsInnerResultCharsHintKey,
            "脚本内一次 Tools.Call 的结果最多多少字符(500–400000),超出部分被截断。",
            "How many characters one Tools.Call result inside the script may hold (500-400000); anything beyond is cut off.");
        Loc.Register(SettingsToolsKey, "可见工具名单", "Visible tools");
        Loc.Register(
            SettingsToolsHintKey,
            "工具名用逗号、分号或换行分隔;末尾的 * 是通配(如 web_*)。空白表示全部候选:内置只读文件工具 read/ls/find/grep、web_fetch/web_search、已启用的 MCP 只读工具与插件工具;写入与执行类(bash、write、edit)不在候选里。",
            "Tool names separated by commas, semicolons or newlines; a trailing * is a wildcard (for example web_*). Blank means every candidate: the built-in read-only file tools read/ls/find/grep, web_fetch/web_search, enabled read-only MCP tools and plugin tools. Writing and execution tools (bash, write, edit) are not candidates.");
    }
}