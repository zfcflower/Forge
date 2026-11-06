using System.Runtime.CompilerServices;

namespace Forge.App.Localization;

/// <summary>
/// The terminal feature's string table (forge-app spec, "偏好、文案与设置分区"): the tab title, the status line under the
/// screen, the close banner, the context menu, the status-bar button and the <c>terminal</c> settings section. Keys are
/// namespaced with <c>terminal.</c> and every key is registered from <see cref="Initialize"/> so no shared file has to
/// list this table.
/// </summary>
internal static class TerminalStrings
{
    public const string TabTitle = "terminal.tab.title";

    public const string StatusStarting = "terminal.status.starting";
    public const string StatusExited = "terminal.status.exited";
    public const string StatusFailed = "terminal.status.failed";

    public const string FailureHostUnavailable = "terminal.failure.host";
    public const string FailureNoSession = "terminal.failure.noSession";

    public const string NewTerminal = "terminal.new";
    public const string StatusButton = "terminal.status.button";

    public const string MenuCopy = "terminal.menu.copy";
    public const string MenuPaste = "terminal.menu.paste";
    public const string MenuClear = "terminal.menu.clear";

    public const string ClosePrompt = "terminal.close.prompt";
    public const string CloseConfirm = "terminal.close.confirm";
    public const string CloseCancel = "terminal.close.cancel";

    public const string AutomationSurface = "terminal.automation.surface";

    public const string SettingsSection = "terminal.settings.section";
    public const string SettingsShell = "terminal.settings.shell";
    public const string SettingsArguments = "terminal.settings.arguments";
    public const string SettingsScrollback = "terminal.settings.scrollback";
    public const string SettingsHint = "terminal.settings.hint";

    /// <summary>Registers this table before any code of the assembly runs (module initializer, AOT safe).</summary>
    [ModuleInitializer]
    internal static void Initialize() => Register();

    /// <summary>Idempotent: registering the same key with the same texts twice is a no-op.</summary>
    internal static void Register()
    {
        Loc.Register(TabTitle, "终端 {0}", "Terminal {0}");

        Loc.Register(StatusStarting, "正在启动终端…", "Starting the terminal…");
        Loc.Register(StatusExited, "进程已退出（退出码 {0}）", "The process exited (exit code {0}).");
        Loc.Register(StatusFailed, "终端已停止：{0}", "The terminal stopped: {0}");

        Loc.Register(
            FailureHostUnavailable,
            "扩展宿主未运行，无法启动终端。",
            "The extension host is not running, so a terminal cannot start.");
        Loc.Register(
            FailureNoSession,
            "没有打开的会话，无法新建终端。",
            "No session is open, so a terminal cannot be created.");

        Loc.Register(NewTerminal, "新建终端", "New terminal");
        Loc.Register(StatusButton, "终端", "Terminal");

        Loc.Register(MenuCopy, "复制", "Copy");
        Loc.Register(MenuPaste, "粘贴", "Paste");
        Loc.Register(MenuClear, "清屏", "Clear");

        Loc.Register(
            ClosePrompt,
            "仍有程序在运行，关闭会结束它。",
            "A program is still running; closing ends it.");
        Loc.Register(CloseConfirm, "结束并关闭", "End and close");
        Loc.Register(CloseCancel, "取消", "Cancel");

        Loc.Register(AutomationSurface, "终端", "Terminal");

        Loc.Register(SettingsSection, "终端", "Terminal");
        Loc.Register(SettingsShell, "Shell 程序", "Shell");
        Loc.Register(SettingsArguments, "参数", "Arguments");
        Loc.Register(SettingsScrollback, "回滚行数", "Scrollback lines");
        Loc.Register(
            SettingsHint,
            "Shell 留空时用平台默认值；参数按空格切分，双引号可分组（例如 \"-l -i\"）。回滚行数取 1000–100000，默认 10000。",
            "An empty shell means the platform default; arguments are split on spaces and double quotes group (for example \"-l -i\"). Scrollback takes 1000–100000 lines (10000 by default).");
    }
}
