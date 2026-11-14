using System.Runtime.CompilerServices;

namespace Forge.App.Localization;

/// <summary>
/// The notification string table (the toast, its de-duplication badge, the settings section and the permission guide); keys
/// are namespaced <c>notifications.*</c>.
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

    // The permission guide (wave W1): one sentence per state of the permission, the two actions that change it, the switch
    // that decides whether the first-run wizard keeps asking, and the text of the test notification itself.
    public const string PermissionSectionTitle = "notifications.permission.section";
    public const string PermissionStateNotApplicable = "notifications.permission.state.notApplicable";
    public const string PermissionStateUnknown = "notifications.permission.state.unknown";
    public const string PermissionStateGranted = "notifications.permission.state.granted";
    public const string PermissionStateDenied = "notifications.permission.state.denied";
    public const string PermissionTestAction = "notifications.permission.test";
    public const string PermissionTestRunning = "notifications.permission.testRunning";
    public const string PermissionOpenSettings = "notifications.permission.openSettings";
    public const string PermissionReprompt = "notifications.permission.reprompt";
    public const string PermissionRepromptHint = "notifications.permission.repromptHint";
    public const string PermissionTestTitleKey = "notifications.permission.testTitle";
    public const string PermissionTestBodyKey = "notifications.permission.testBody";

    // Titles of the notifications the shell raises from its own work. The body is usually the text the panel already shows
    // ("the turn could not run: ..."), so only the headline needs a line of its own; a sentence that also exists in a
    // feature table is registered again here on purpose (each UI area owns its wording).
    public const string ApprovalWaitingTitleKey = "notifications.title.approval";
    public const string TurnFailedTitleKey = "notifications.title.turnFailed";
    public const string ProviderMissingTitleKey = "notifications.title.providerMissing";
    public const string SubAgentFinishedTitleKey = "notifications.title.subAgentFinished";
    public const string StepCommitFailedTitleKey = "notifications.title.stepCommitFailed";
    public const string GitOperationFailedTitleKey = "notifications.title.gitOperationFailed";
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

    /// <summary>Headline of the "git operation failed" notification.</summary>
    public static LocText GitOperationFailedTitle => new(GitOperationFailedTitleKey);

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

    /// <summary>Title of the test notification (one real toast the user can look for).</summary>
    public static LocText PermissionTestTitle => new(PermissionTestTitleKey);

    /// <summary>Body of the test notification.</summary>
    public static LocText PermissionTestBody => new(PermissionTestBodyKey);

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

        Loc.Register(PermissionSectionTitle, "通知权限", "Notification permission");
        Loc.Register(
            PermissionStateNotApplicable,
            "这个构建没有可用的系统通知工具，不会弹桌面通知。",
            "This build has no desktop notification tool, so no desktop notification can appear.");
        Loc.Register(
            PermissionStateUnknown,
            "还不知道系统是否允许通知。发一条测试通知就能确认。",
            "It is not known yet whether the system allows notifications. Send a test notification to find out.");
        Loc.Register(
            PermissionStateGranted,
            "系统已接受测试通知，桌面通知应该能正常送达。",
            "The system accepted the test notification, so desktop notifications should arrive.");
        Loc.Register(
            PermissionStateDenied,
            "系统没有接受测试通知，可能已在系统设置里关掉了 Forge 的通知。",
            "The system did not accept the test notification; Forge's notifications may be switched off in the system settings.");
        Loc.Register(PermissionTestAction, "发送测试通知", "Send a test notification");
        Loc.Register(PermissionTestRunning, "正在发送…", "Sending…");
        Loc.Register(PermissionOpenSettings, "打开系统通知设置", "Open the system notification settings");
        Loc.Register(PermissionReprompt, "再次提示", "Ask again");
        Loc.Register(
            PermissionRepromptHint,
            "关掉后，首启向导与后台通知不再反复提示修复通知权限。",
            "When off, the first-run wizard and background notifications stop offering to fix the notification permission.");
        Loc.Register(PermissionTestTitleKey, "通知测试", "Notification test");
        Loc.Register(
            PermissionTestBodyKey,
            "看到这条消息，说明系统通知是通的。",
            "If you can see this, desktop notifications work.");

        Loc.Register(LevelInfo, "提示", "Info");
        Loc.Register(LevelSuccess, "成功", "Success");
        Loc.Register(LevelWarning, "警告", "Warning");
        Loc.Register(LevelError, "错误", "Error");

        Loc.Register(ApprovalWaitingTitleKey, "等待你的审批", "Waiting for your approval");
        Loc.Register(TurnFailedTitleKey, "回合失败", "Turn failed");
        Loc.Register(ProviderMissingTitleKey, "提供商不可用", "Provider unavailable");
        Loc.Register(SubAgentFinishedTitleKey, "子 Agent 结束", "Sub-agent finished");
        Loc.Register(StepCommitFailedTitleKey, "步骤提交失败", "Step commit failed");
        Loc.Register(GitOperationFailedTitleKey, "Git 操作失败", "Git operation failed");
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
