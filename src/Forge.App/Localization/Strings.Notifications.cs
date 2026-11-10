using System.Runtime.CompilerServices;

namespace Forge.App.Localization;

/// <summary>
/// The notification string table (the toast, its de-duplication badge and the settings section); keys are namespaced
/// <c>notifications.*</c>.
/// </summary>
internal static class NotificationsStrings
{
    /// <summary>Title of the settings section that holds the one global switch.</summary>
    public const string SectionTitle = "notifications.section";

    public const string EnabledLabel = "notifications.enabled";
    public const string EnabledHint = "notifications.enabledHint";

    public const string LevelInfo = "notifications.level.info";
    public const string LevelSuccess = "notifications.level.success";
    public const string LevelWarning = "notifications.level.warning";
    public const string LevelError = "notifications.level.error";

    // Titles of the notifications the shell raises from its own work. The body is usually the text the panel already shows
    // ("the turn could not run: ..."), so only the headline needs a line of its own; a sentence that also exists in a
    // feature table is registered again here on purpose (each UI area owns its wording).
    public const string ApprovalWaitingTitleKey = "notifications.title.approval";
    public const string TurnFailedTitleKey = "notifications.title.turnFailed";
    public const string ProviderMissingTitleKey = "notifications.title.providerMissing";
    public const string SubAgentFinishedTitleKey = "notifications.title.subAgentFinished";
    public const string StepCommitFailedTitleKey = "notifications.title.stepCommitFailed";
    public const string ScreenshotTitleKey = "notifications.title.screenshot";
    public const string GitOperationFailedTitleKey = "notifications.title.gitOperationFailed";
    public const string GitHubNotReadyTitleKey = "notifications.title.githubNotReady";
    public const string HostUnavailableTitleKey = "notifications.title.hostUnavailable";
    public const string McpFailedTitleKey = "notifications.title.mcpFailed";
    public const string PreferencesProblemTitleKey = "notifications.title.preferencesProblem";
    public const string StartupProblemTitleKey = "notifications.title.startupProblem";
    public const string TerminalExitedTitleKey = "notifications.title.terminalExited";
    public const string SaveFailedTitleKey = "notifications.title.saveFailed";
    public const string EditorFailedTitleKey = "notifications.title.editorFailed";

    // Bodies that are one sentence of their own.
    public const string ApprovalWaitingBody = "notifications.body.approval";
    public const string ApprovalWaitingRestoredBody = "notifications.body.approvalRestored";
    public const string SubAgentFinishedBody = "notifications.body.subAgentFinished";
    public const string TerminalExitedBody = "notifications.body.terminalExited";

    /// <summary>Headline of the "approval waiting" notification.</summary>
    public static LocText ApprovalWaitingTitle => new(ApprovalWaitingTitleKey);

    /// <summary>Headline of the "turn failed" notification.</summary>
    public static LocText TurnFailedTitle => new(TurnFailedTitleKey);

    /// <summary>Headline of the "provider missing" notification.</summary>
    public static LocText ProviderMissingTitle => new(ProviderMissingTitleKey);

    /// <summary>Headline of the "sub agent finished" notification.</summary>
    public static LocText SubAgentFinishedTitle => new(SubAgentFinishedTitleKey);

    /// <summary>Headline of the "step commit failed" notification.</summary>
    public static LocText StepCommitFailedTitle => new(StepCommitFailedTitleKey);

    /// <summary>Headline of the "screenshot" notification.</summary>
    public static LocText ScreenshotTitle => new(ScreenshotTitleKey);

    /// <summary>Headline of the "git operation failed" notification.</summary>
    public static LocText GitOperationFailedTitle => new(GitOperationFailedTitleKey);

    /// <summary>Headline of the "git hub not ready" notification.</summary>
    public static LocText GitHubNotReadyTitle => new(GitHubNotReadyTitleKey);

    /// <summary>Headline of the "host unavailable" notification.</summary>
    public static LocText HostUnavailableTitle => new(HostUnavailableTitleKey);

    /// <summary>Headline of the "mcp failed" notification.</summary>
    public static LocText McpFailedTitle => new(McpFailedTitleKey);

    /// <summary>Headline of the "preferences problem" notification.</summary>
    public static LocText PreferencesProblemTitle => new(PreferencesProblemTitleKey);

    /// <summary>Headline of the "startup problem" notification.</summary>
    public static LocText StartupProblemTitle => new(StartupProblemTitleKey);

    /// <summary>Headline of the "terminal exited" notification.</summary>
    public static LocText TerminalExitedTitle => new(TerminalExitedTitleKey);

    /// <summary>Headline of the "save failed" notification.</summary>
    public static LocText SaveFailedTitle => new(SaveFailedTitleKey);

    /// <summary>Headline of the "editor failed" notification.</summary>
    public static LocText EditorFailedTitle => new(EditorFailedTitleKey);

    /// <summary>One approval card started waiting: which session asked, and for which tool.</summary>
    public static LocText ApprovalWaiting(string session, string tool) => new(ApprovalWaitingBody, session, tool);

    /// <summary>An extension card restored after a restart, which has no session to name.</summary>
    public static LocText ApprovalWaitingRestored(string tool) => new(ApprovalWaitingRestoredBody, tool);

    /// <summary>A sub-agent ended: its name and the state it ended in.</summary>
    public static LocText SubAgentFinished(string name, string status) => new(SubAgentFinishedBody, name, status);

    /// <summary>A terminal process ended on its own, with its exit code.</summary>
    public static LocText TerminalExited(string command, int exitCode) =>
        new(TerminalExitedBody, command, TextFormat.Id(exitCode));

    [ModuleInitializer]
    internal static void Initialize() => Register();

    /// <summary>Idempotent: registering the same key with the same texts twice is a no-op.</summary>
    internal static void Register()
    {
        Loc.Register(SectionTitle, "通知", "Notifications");

        Loc.Register(EnabledLabel, "启用通知", "Show notifications");
        Loc.Register(
            EnabledHint,
            "关掉后既不显示窗口内提示，也不发系统通知。窗口最小化或不在前台时，通知改由操作系统弹出。",
            "When off, nothing is shown in the window and no desktop notification is raised. Windows minimized or in the background get a desktop notification instead.");

        Loc.Register(LevelInfo, "提示", "Info");
        Loc.Register(LevelSuccess, "成功", "Success");
        Loc.Register(LevelWarning, "警告", "Warning");
        Loc.Register(LevelError, "错误", "Error");

        Loc.Register(ApprovalWaitingTitleKey, "等待你的审批", "Waiting for your approval");
        Loc.Register(TurnFailedTitleKey, "回合失败", "Turn failed");
        Loc.Register(ProviderMissingTitleKey, "提供方不可用", "Provider unavailable");
        Loc.Register(SubAgentFinishedTitleKey, "子 Agent 结束", "Sub-agent finished");
        Loc.Register(StepCommitFailedTitleKey, "步骤提交失败", "Step commit failed");
        Loc.Register(ScreenshotTitleKey, "截图", "Screenshots");
        Loc.Register(GitOperationFailedTitleKey, "Git 操作失败", "Git operation failed");
        Loc.Register(GitHubNotReadyTitleKey, "GitHub CLI 不可用", "GitHub CLI unavailable");
        Loc.Register(HostUnavailableTitleKey, "扩展宿主不可用", "Extension host unavailable");
        Loc.Register(McpFailedTitleKey, "MCP 服务器异常", "MCP server failed");
        Loc.Register(PreferencesProblemTitleKey, "设置未能读取", "Settings could not be read");
        Loc.Register(StartupProblemTitleKey, "启动未完成", "Startup did not finish");
        Loc.Register(TerminalExitedTitleKey, "终端进程退出", "Terminal process exited");
        Loc.Register(SaveFailedTitleKey, "保存失败", "Save failed");
        Loc.Register(EditorFailedTitleKey, "编辑器操作失败", "Editor failed");

        Loc.Register(ApprovalWaitingBody, "{0} 请求执行 {1}", "{0} wants to run {1}");
        Loc.Register(ApprovalWaitingRestoredBody, "「{0}」等待审批", "{0} is waiting for approval");
        Loc.Register(SubAgentFinishedBody, "{0} · {1}", "{0} · {1}");
        Loc.Register(TerminalExitedBody, "{0} · 退出码 {1}", "{0} · exit code {1}");
    }
}
