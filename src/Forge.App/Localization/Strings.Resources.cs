using System.Runtime.CompilerServices;

namespace Forge.App.Localization;

/// <summary>
/// The resource inspector's table (wave W8,缺口 #13): the overlay that lists what the open sessions are running right now —
/// their live tool calls and sub-agents, grouped by session, with elapsed time, the tail of a streamed output and a stop
/// button per row — plus the row of
/// the composer's resources menu that opens it and the <c>inspector</c> settings section. Keys: <c>resources.*</c>.
/// <para>Only names and wording live here; the numbers (elapsed seconds, counts) are formatted by the callers through
/// <see cref="TextFormat"/> and arrive as arguments, so a language switch re-renders a row without re-reading anything.</para>
/// </summary>
internal static class ResourceInspectorStrings
{
    // ---------------------------------------------------------------- the composer menu row and the overlay's chrome
    public const string MenuKey = "resources.menu";
    public const string MenuCountPatternKey = "resources.menu.count";
    public const string MenuTooltipKey = "resources.menu.tooltip";
    public const string TitleKey = "resources.title";
    public const string CloseKey = "resources.close";
    public const string CloseTooltipKey = "resources.closeTooltip";
    public const string CountPatternKey = "resources.count";
    public const string EmptyKey = "resources.empty";
    public const string EmptyHintKey = "resources.emptyHint";
    public const string NoSessionKey = "resources.noSession";
    public const string ActiveSessionKey = "resources.session.active";

    // ---------------------------------------------------------------- one running tool call
    public const string TailKey = "resources.tail";
    public const string TailEmptyKey = "resources.tail.empty";
    public const string StopToolKey = "resources.stop";
    public const string StopToolNameKey = "resources.stop.name";
    public const string StopToolTooltipKey = "resources.stop.tooltip";
    public const string StoppingKey = "resources.stopping";
    public const string RootAgentKey = "resources.agent.root";
    public const string ChildAgentKey = "resources.agent.child";

    // ---------------------------------------------------------------- the `inspector` settings section
    public const string SettingsTitleKey = "resources.settings.title";
    public const string SettingsHintKey = "resources.settings.hint";
    public const string SettingsTailLinesKey = "resources.settings.tailLines";
    public const string SettingsTailLinesHintKey = "resources.settings.tailLinesHint";
    public const string SettingsRefreshKey = "resources.settings.refresh";
    public const string SettingsRefreshHintKey = "resources.settings.refreshHint";

    /// <summary>Registers this table before any code of the assembly runs (module initializer, AOT safe).</summary>
    [ModuleInitializer]
    internal static void Initialize() => Register();

    /// <summary>"运行中的资源…": the menu row, for a session with nothing running and no host wired to open the overlay.</summary>
    public static string Menu => Loc.Instance[MenuKey];

    /// <summary>"运行中的资源(3)…": the same row with this session's live count.</summary>
    public static string MenuCount(int running) =>
        Loc.Instance.Format(MenuCountPatternKey, TextFormat.Number(running));

    public static string MenuTooltip => Loc.Instance[MenuTooltipKey];

    public static string Title => Loc.Instance[TitleKey];

    public static string Close => Loc.Instance[CloseKey];

    public static string CloseTooltip => Loc.Instance[CloseTooltipKey];

    /// <summary>"3 项进行中" beside the title; only rendered when at least one row is listed.</summary>
    public static string Count(int running) => Loc.Instance.Format(CountPatternKey, TextFormat.Number(running));

    public static string Empty => Loc.Instance[EmptyKey];

    public static string EmptyHint => Loc.Instance[EmptyHintKey];

    public static string NoSession => Loc.Instance[NoSessionKey];

    /// <summary>"当前会话": the tag on the group of the session the window shows.</summary>
    public static string ActiveSession => Loc.Instance[ActiveSessionKey];

    public static string StopTool => Loc.Instance[StopToolKey];

    public static string StopToolName => Loc.Instance[StopToolNameKey];

    public static string StopToolTooltip => Loc.Instance[StopToolTooltipKey];

    /// <summary>"正在停止…": a stopped call that is still winding down (killing its process tree, cancelling its child).</summary>
    public static string Stopping => Loc.Instance[StoppingKey];

    /// <summary>"主 Agent": the agent a tool call belongs to when it is the session's own agent.</summary>
    public static string RootAgent => Loc.Instance[RootAgentKey];

    /// <summary>"子 Agent": the agent a tool call belongs to when a child runs it (the child's kind is shown beside it).</summary>
    public static string ChildAgent => Loc.Instance[ChildAgentKey];

    public static string SettingsTitle => Loc.Instance[SettingsTitleKey];

    public static string SettingsHint => Loc.Instance[SettingsHintKey];

    public static string SettingsTailLines => Loc.Instance[SettingsTailLinesKey];

    public static string SettingsTailLinesHint => Loc.Instance[SettingsTailLinesHintKey];

    public static string SettingsRefresh => Loc.Instance[SettingsRefreshKey];

    public static string SettingsRefreshHint => Loc.Instance[SettingsRefreshHintKey];

    internal static void Register()
    {
        Loc.Register(MenuKey, "运行中的资源…", "Running resources…");
        Loc.Register(MenuCountPatternKey, "运行中的资源({0})…", "Running resources ({0})…");
        Loc.Register(
            MenuTooltipKey,
            "所有打开会话正在运行的工具调用与子 Agent;括号里的数字是本会话的",
            "The tool calls and sub-agents every open session is running; the number in brackets is this session's");
        Loc.Register(TitleKey, "运行中的资源", "Running resources");
        Loc.Register(CloseKey, "关闭", "Close");
        Loc.Register(CloseTooltipKey, "关闭资源检查器", "Close the resource inspector");
        Loc.Register(CountPatternKey, "{0} 项进行中", "{0} active");
        Loc.Register(EmptyKey, "打开的会话都没有正在运行的工具或子 Agent", "Nothing is running in the open sessions");
        Loc.Register(
            EmptyHintKey,
            "所有打开会话的工具调用与子 Agent 会按会话分组列在这里,带实时耗时、输出尾部与停止按钮。",
            "The tool calls and sub-agents of every open session appear here, grouped by session, with their live elapsed time, output tail and a stop button.");
        Loc.Register(NoSessionKey, "没有打开的会话", "No session is open");
        Loc.Register(ActiveSessionKey, "当前会话", "Current session");

        Loc.Register(TailKey, "输出尾部", "Output tail");
        Loc.Register(TailEmptyKey, "还没有输出", "No output yet");
        Loc.Register(StopToolKey, "停止", "Stop");
        Loc.Register(StopToolNameKey, "停止这个工具调用", "Stop this tool call");
        Loc.Register(
            StopToolTooltipKey,
            "只取消这一个工具调用(命令连同它启动的子进程一起结束,子 Agent 被取消);回合继续,模型会收到“已被用户取消”的结果",
            "Cancels only this tool call (a command is ended with the processes it started, a sub-agent is cancelled); the turn goes on and the model is told the user cancelled the call");
        Loc.Register(StoppingKey, "正在停止…", "Stopping…");
        Loc.Register(RootAgentKey, "主 Agent", "Main agent");
        Loc.Register(ChildAgentKey, "子 Agent", "Sub-agent");

        Loc.Register(SettingsTitleKey, "资源检查器", "Resource inspector");
        Loc.Register(
            SettingsHintKey,
            "“运行中的资源”按会话列出所有打开会话正在运行的工具调用与子 Agent,并给出耗时、输出尾部与停止按钮。",
            "\"Running resources\" lists the running tool calls and sub-agents of every open session, grouped by session, with their elapsed time, output tail and a stop button.");
        Loc.Register(SettingsTailLinesKey, "输出尾部行数", "Output tail lines");
        Loc.Register(
            SettingsTailLinesHintKey,
            "运行中的命令显示最后多少行(20–1000)。命令输出的缓冲上限是 16 KiB,行数再大也只到这么多。",
            "How many lines of a running command are shown (20-1000). The command's output buffer is capped at 16 KiB, so a larger line count still ends there.");
        Loc.Register(SettingsRefreshKey, "刷新间隔(毫秒)", "Refresh interval (ms)");
        Loc.Register(
            SettingsRefreshHintKey,
            "输出尾部多久重读一次(250–10000)。耗时读数按会话时钟每秒更新,不随这一项变化。",
            "How often the output tail is re-read (250-10000). Elapsed time follows the session clock (once a second) and does not change with this.");
    }
}