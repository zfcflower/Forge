using System.Runtime.CompilerServices;

namespace Forge.App.Localization;

/// <summary>
/// The settings foundation's own string table: the settings page headings, the section titles and the path rows, so the
/// shell has a reference table every other feature can copy. Keys are namespaced by feature (<c>settings.*</c>), and the
/// texts are the Chinese original plus its English counterpart.
/// <para>Adding a table: put it in <c>Strings.&lt;Feature&gt;.cs</c>, keep the class <c>internal static</c>, and register
/// every key from a <c>[ModuleInitializer]</c> method so nothing has to edit a shared file and
/// <see cref="Loc.EnsureRegistered"/> has nothing left to trigger at startup.</para>
/// </summary>
internal static class CoreStrings
{
    public const string SettingsTitle = "settings.title";
    public const string SettingsReload = "settings.reload";
    public const string SettingsClose = "settings.close";

    public const string SettingsSectionProvider = "settings.section.provider";
    public const string SettingsSectionPaths = "settings.section.paths";

    public const string SettingsProviderHint = "settings.provider.hint";

    public const string SettingsPathFile = "settings.path.file";
    public const string SettingsPathPreferences = "settings.path.preferences";
    public const string SettingsPathData = "settings.path.data";
    public const string SettingsPathWorkspace = "settings.path.workspace";
    public const string SettingsPathGeneralWorkspace = "settings.path.generalWorkspace";
    public const string SettingsPathHostData = "settings.path.hostData";
    public const string SettingsPathPlugins = "settings.path.plugins";
    public const string SettingsPathHost = "settings.path.host";
    public const string SettingsPathApprovals = "settings.path.approvals";
    public const string SettingsPathMcp = "settings.path.mcp";
    public const string SettingsPathEditor = "settings.path.editor";

    /// <summary>Registers this table before any code of the assembly runs (module initializer, AOT safe).</summary>
    [ModuleInitializer]
    internal static void Initialize() => Register();

    /// <summary>Idempotent: registering the same key with the same texts twice is a no-op.</summary>
    internal static void Register()
    {
        Loc.Register(SettingsTitle, "设置", "Settings");
        Loc.Register(SettingsReload, "重新加载设置", "Reload settings");
        Loc.Register(SettingsClose, "关闭", "Close");

        Loc.Register(SettingsSectionProvider, "模型提供方", "Model provider");
        Loc.Register(SettingsSectionPaths, "路径与服务", "Paths and services");

        Loc.Register(
            SettingsProviderHint,
            "提供方来自 settings.json，启动时与点击重新加载时读取；密钥可来自该文件指定的环境变量。",
            "The provider comes from settings.json and is read at startup and on reload; the key may come from the environment variable named there.");

        Loc.Register(SettingsPathFile, "settings.json", "settings.json");
        Loc.Register(SettingsPathPreferences, "偏好", "Preferences");
        Loc.Register(SettingsPathData, "数据目录", "Data directory");
        Loc.Register(SettingsPathWorkspace, "工作区", "Workspace");
        Loc.Register(SettingsPathGeneralWorkspace, "常规工作区", "General workspace");
        Loc.Register(SettingsPathHostData, "扩展宿主数据", "Extension host data");
        Loc.Register(SettingsPathPlugins, "插件", "Plugins");
        Loc.Register(SettingsPathHost, "扩展宿主", "Extension host");
        Loc.Register(SettingsPathApprovals, "审批数据库", "Approval database");
        Loc.Register(SettingsPathMcp, "MCP 服务器", "MCP servers");
        Loc.Register(SettingsPathEditor, "编辑器", "Editor");
    }
}
