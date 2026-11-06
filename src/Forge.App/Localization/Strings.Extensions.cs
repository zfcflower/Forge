using System.Runtime.CompilerServices;

namespace Forge.App.Localization;

/// <summary>
/// The approval queue, remembered rules and extension settings string table. Keys carry the <c>Extensions.</c> prefix and
/// are registered from this table's own module initializer, so no shared file is edited and
/// <see cref="Loc.EnsureRegistered"/> has nothing left to trigger (forge-app spec, "偏好、文案与设置分区").
/// </summary>
internal static class ExtensionsStrings
{
    // ---------------------------------------------------------------- queue overlay

    public const string QueueTitle = "Extensions.Queue.Title";
    public const string QueueToggleExpand = "Extensions.Queue.Expand";
    public const string QueueToggleCollapse = "Extensions.Queue.Collapse";
    public const string QueueEmpty = "Extensions.Queue.Empty";

    // ---------------------------------------------------------------- tool call card

    public const string ToolTitle = "Extensions.Tool.Title";
    public const string ToolDeleteTitle = "Extensions.Tool.DeleteTitle";
    public const string ToolDeleteNote = "Extensions.Tool.DeleteNote";
    public const string ToolCommandTitle = "Extensions.Tool.CommandTitle";
    public const string ToolCommandNote = "Extensions.Tool.CommandNote";
    public const string ToolSession = "Extensions.Tool.Session";
    public const string ToolName = "Extensions.Tool.Name";
    public const string ToolSideEffect = "Extensions.Tool.SideEffect";
    public const string ToolWorkingDirectory = "Extensions.Tool.WorkingDirectory";
    public const string ToolArguments = "Extensions.Tool.Arguments";
    public const string ToolArgumentsExpand = "Extensions.Tool.ArgumentsExpand";
    public const string ToolArgumentsCollapse = "Extensions.Tool.ArgumentsCollapse";
    public const string ToolAllow = "Extensions.Tool.Allow";
    public const string ToolDeny = "Extensions.Tool.Deny";
    public const string ToolRememberSession = "Extensions.Tool.RememberSession";
    public const string ToolRememberProject = "Extensions.Tool.RememberProject";
    public const string ToolRememberGlobal = "Extensions.Tool.RememberGlobal";
    public const string ToolNoProject = "Extensions.Tool.NoProject";
    public const string ToolDecidedAllow = "Extensions.Tool.DecidedAllow";
    public const string ToolDecidedDeny = "Extensions.Tool.DecidedDeny";

    public const string SideEffectReadOnly = "Extensions.SideEffect.ReadOnly";
    public const string SideEffectWrite = "Extensions.SideEffect.Write";
    public const string SideEffectExecute = "Extensions.SideEffect.Execute";

    // ---------------------------------------------------------------- extension card

    public const string ExtensionTitleNew = "Extensions.Extension.TitleNew";
    public const string ExtensionTitleNewPreviousRun = "Extensions.Extension.TitleNewPreviousRun";
    public const string ExtensionTitleUpdate = "Extensions.Extension.TitleUpdate";
    public const string ExtensionTitleUpdatePreviousRun = "Extensions.Extension.TitleUpdatePreviousRun";
    public const string ExtensionId = "Extensions.Extension.Id";
    public const string ExtensionVersion = "Extensions.Extension.Version";
    public const string ExtensionLiveVersion = "Extensions.Extension.LiveVersion";
    public const string ExtensionKind = "Extensions.Extension.Kind";
    public const string ExtensionKindPlugin = "Extensions.Extension.KindPlugin";
    public const string ExtensionKindWorkflow = "Extensions.Extension.KindWorkflow";
    public const string ExtensionSession = "Extensions.Extension.Session";
    public const string ExtensionPermissions = "Extensions.Extension.Permissions";
    public const string ExtensionDependencies = "Extensions.Extension.Dependencies";
    public const string ExtensionSourceHash = "Extensions.Extension.SourceHash";
    public const string ExtensionDependencyHash = "Extensions.Extension.DependencyHash";
    public const string ExtensionSdkVersion = "Extensions.Extension.SdkVersion";
    public const string ExtensionFiles = "Extensions.Extension.Files";
    public const string ExtensionFileAdded = "Extensions.Extension.FileAdded";
    public const string ExtensionFileChanged = "Extensions.Extension.FileChanged";
    public const string ExtensionFileUnchanged = "Extensions.Extension.FileUnchanged";
    public const string ExtensionNoFileChanges = "Extensions.Extension.NoFileChanges";
    public const string ExtensionDiagnostics = "Extensions.Extension.Diagnostics";
    public const string ExtensionApprove = "Extensions.Extension.Approve";
    public const string ExtensionReject = "Extensions.Extension.Reject";
    public const string ExtensionApproveNote = "Extensions.Extension.ApproveNote";
    public const string ExtensionNone = "Extensions.Extension.None";
    public const string ExtensionTool = "Extensions.Extension.Tool";
    public const string ExtensionToolNoDescription = "Extensions.Extension.ToolNoDescription";
    public const string ExtensionDiffAdded = "Extensions.Extension.DiffAdded";
    public const string ExtensionDiffRemoved = "Extensions.Extension.DiffRemoved";
    public const string ExtensionBuildingDiff = "Extensions.Extension.BuildingDiff";

    public const string PermissionReadFile = "Extensions.Permission.ReadFile";
    public const string PermissionWriteFile = "Extensions.Permission.WriteFile";
    public const string PermissionExecuteCommand = "Extensions.Permission.ExecuteCommand";
    public const string PermissionNetwork = "Extensions.Permission.Network";
    public const string PermissionDatabase = "Extensions.Permission.Database";
    public const string PermissionUnknown = "Extensions.Permission.Unknown";

    // ---------------------------------------------------------------- settings section

    public const string SettingsSectionTitle = "Extensions.Settings.Section";
    public const string SettingsInstalled = "Extensions.Settings.Installed";
    public const string SettingsPending = "Extensions.Settings.Pending";
    public const string SettingsRules = "Extensions.Settings.Rules";
    public const string SettingsRefresh = "Extensions.Settings.Refresh";
    public const string SettingsLoading = "Extensions.Settings.Loading";
    public const string SettingsUnavailable = "Extensions.Settings.Unavailable";
    public const string SettingsNoHost = "Extensions.Settings.NoHost";
    public const string SettingsNoInstalled = "Extensions.Settings.NoInstalled";
    public const string SettingsNoPending = "Extensions.Settings.NoPending";
    public const string SettingsNoRules = "Extensions.Settings.NoRules";
    public const string SettingsEnable = "Extensions.Settings.Enable";
    public const string SettingsDisable = "Extensions.Settings.Disable";
    public const string SettingsDelete = "Extensions.Settings.Delete";
    public const string SettingsDeleteConfirm = "Extensions.Settings.DeleteConfirm";
    public const string SettingsCancel = "Extensions.Settings.Cancel";
    public const string SettingsOpenCard = "Extensions.Settings.OpenCard";
    public const string SettingsOpenCardFailed = "Extensions.Settings.OpenCardFailed";
    public const string SettingsRevoke = "Extensions.Settings.Revoke";
    public const string SettingsToolsLine = "Extensions.Settings.ToolsLine";
    public const string SettingsScopeSession = "Extensions.Settings.ScopeSession";
    public const string SettingsScopeProject = "Extensions.Settings.ScopeProject";
    public const string SettingsScopeGlobal = "Extensions.Settings.ScopeGlobal";
    public const string SettingsRuleLine = "Extensions.Settings.RuleLine";
    public const string SettingsLoadProblem = "Extensions.Settings.LoadProblem";
    public const string SettingsActionFailed = "Extensions.Settings.ActionFailed";

    public const string RulesUnreadable = "Extensions.Rules.Unreadable";
    public const string RulesReadFailed = "Extensions.Rules.ReadFailed";
    public const string RulesInvalid = "Extensions.Rules.Invalid";
    public const string RulesEmpty = "Extensions.Rules.Empty";
    public const string RulesVersion = "Extensions.Rules.Version";
    public const string RulesNoList = "Extensions.Rules.NoList";
    public const string RulesWriteFailed = "Extensions.Rules.WriteFailed";

    public const string StatusDraft = "Extensions.Status.Draft";
    public const string StatusApproved = "Extensions.Status.Approved";
    public const string StatusDisabled = "Extensions.Status.Disabled";
    public const string StatusUninstalled = "Extensions.Status.Uninstalled";
    public const string StatusPending = "Extensions.Status.Pending";
    public const string StatusUnknown = "Extensions.Status.Unknown";

    /// <summary>Registers this table before any code of the assembly runs (module initializer, AOT safe).</summary>
    [ModuleInitializer]
    internal static void Initialize() => Register();

    /// <summary>Idempotent: registering the same key with the same texts twice is a no-op.</summary>
    internal static void Register()
    {
        Loc.Register(QueueTitle, "待审批", "Approvals");
        Loc.Register(QueueToggleExpand, "展开", "Expand");
        Loc.Register(QueueToggleCollapse, "收起", "Collapse");
        Loc.Register(QueueEmpty, "没有待审批项", "Nothing is waiting for approval");

        Loc.Register(ToolTitle, "工具调用审批", "Tool call approval");
        Loc.Register(ToolDeleteTitle, "确认删除扩展", "Confirm extension deletion");
        Loc.Register(ToolDeleteNote, "删除扩展无法记住；每次删除都会单独确认。", "Deleting an extension cannot be remembered; every deletion asks again.");
        Loc.Register(ToolCommandTitle, "插件命令审批", "Plugin command approval");
        Loc.Register(ToolCommandNote, "插件命令每次都会单独确认，无法记住。", "A plugin command is confirmed on every call and cannot be remembered.");
        Loc.Register(ToolSession, "会话", "Session");
        Loc.Register(ToolName, "工具", "Tool");
        Loc.Register(ToolSideEffect, "副作用", "Side effect");
        Loc.Register(ToolWorkingDirectory, "工作目录", "Working directory");
        Loc.Register(ToolArguments, "参数", "Arguments");
        Loc.Register(ToolArgumentsExpand, "展开参数", "Show arguments");
        Loc.Register(ToolArgumentsCollapse, "收起参数", "Hide arguments");
        Loc.Register(ToolAllow, "允许", "Allow");
        Loc.Register(ToolDeny, "拒绝", "Deny");
        Loc.Register(ToolRememberSession, "本会话记住", "Remember for this session");
        Loc.Register(ToolRememberProject, "本项目记住", "Remember for this project");
        Loc.Register(ToolRememberGlobal, "永久记住", "Always allow");
        Loc.Register(ToolNoProject, "没有项目，无法记住本项目。", "There is no project, so this cannot be remembered for one.");
        Loc.Register(ToolDecidedAllow, "已允许", "Allowed");
        Loc.Register(ToolDecidedDeny, "已拒绝", "Denied");

        Loc.Register(SideEffectReadOnly, "只读", "Read-only");
        Loc.Register(SideEffectWrite, "写入", "Write");
        Loc.Register(SideEffectExecute, "执行", "Execute");

        Loc.Register(ExtensionTitleNew, "新扩展待批准", "New extension awaiting approval");
        Loc.Register(ExtensionTitleNewPreviousRun, "重启前提交的新扩展", "New extension submitted before the restart");
        Loc.Register(ExtensionTitleUpdate, "扩展更新待批准", "Extension update awaiting approval");
        Loc.Register(ExtensionTitleUpdatePreviousRun, "重启前提交的扩展更新", "Extension update submitted before the restart");
        Loc.Register(ExtensionId, "扩展", "Extension");
        Loc.Register(ExtensionVersion, "版本", "Version");
        Loc.Register(ExtensionLiveVersion, "线上版本", "Live version");
        Loc.Register(ExtensionKind, "类型", "Kind");
        Loc.Register(ExtensionKindPlugin, "C# 插件", "C# plugin");
        Loc.Register(ExtensionKindWorkflow, "声明式工作流", "Declarative workflow");
        Loc.Register(ExtensionSession, "来源会话", "From session");
        Loc.Register(ExtensionPermissions, "权限", "Permissions");
        Loc.Register(ExtensionDependencies, "依赖", "Dependencies");
        Loc.Register(ExtensionSourceHash, "源码哈希", "Source hash");
        Loc.Register(ExtensionDependencyHash, "依赖哈希", "Dependency hash");
        Loc.Register(ExtensionSdkVersion, "SDK 版本", "SDK version");
        Loc.Register(ExtensionFiles, "文件", "Files");
        Loc.Register(ExtensionFileAdded, "新增", "Added");
        Loc.Register(ExtensionFileChanged, "修改", "Changed");
        Loc.Register(ExtensionFileUnchanged, "未改动", "Unchanged");
        Loc.Register(ExtensionNoFileChanges, "与线上版本相同。", "Identical to the live version.");
        Loc.Register(ExtensionDiagnostics, "诊断", "Diagnostics");
        Loc.Register(ExtensionApprove, "批准", "Approve");
        Loc.Register(ExtensionReject, "拒绝", "Reject");
        Loc.Register(ExtensionApproveNote, "批准后新版本立即生效；拒绝等于什么都没发生。", "Approving makes the new version live; rejecting changes nothing.");
        Loc.Register(ExtensionNone, "无", "None");
        Loc.Register(ExtensionTool, "工具", "Tool");
        Loc.Register(ExtensionToolNoDescription, "（无描述）", "(no description)");
        Loc.Register(ExtensionDiffAdded, "新增行", "Added lines");
        Loc.Register(ExtensionDiffRemoved, "删除行", "Removed lines");
        Loc.Register(ExtensionBuildingDiff, "正在计算与线上版本的差异…", "Comparing with the live version…");

        Loc.Register(PermissionReadFile, "读取文件", "Read files");
        Loc.Register(PermissionWriteFile, "写入文件", "Write files");
        Loc.Register(PermissionExecuteCommand, "执行命令", "Run commands");
        Loc.Register(PermissionNetwork, "网络访问", "Network access");
        Loc.Register(PermissionDatabase, "数据库", "Database");
        Loc.Register(PermissionUnknown, "未知权限", "Unknown permission");

        Loc.Register(SettingsSectionTitle, "扩展", "Extensions");
        Loc.Register(SettingsInstalled, "已安装扩展", "Installed extensions");
        Loc.Register(SettingsPending, "待审批草稿", "Drafts awaiting approval");
        Loc.Register(SettingsRules, "记住的规则", "Remembered rules");
        Loc.Register(SettingsRefresh, "刷新", "Refresh");
        Loc.Register(SettingsLoading, "正在读取扩展…", "Reading extensions…");
        Loc.Register(SettingsUnavailable, "扩展宿主不可用：{0}", "The extension host is unavailable: {0}");
        Loc.Register(SettingsNoHost, "没有找到扩展宿主，扩展功能不可用。", "No extension host was found, so extensions are unavailable.");
        Loc.Register(SettingsNoInstalled, "还没有安装任何扩展。", "No extension is installed yet.");
        Loc.Register(SettingsNoPending, "没有待审批的草稿。", "No draft is awaiting approval.");
        Loc.Register(SettingsNoRules, "还没有记住任何规则。", "No rule has been remembered yet.");
        Loc.Register(SettingsEnable, "启用", "Enable");
        Loc.Register(SettingsDisable, "禁用", "Disable");
        Loc.Register(SettingsDelete, "删除", "Delete");
        Loc.Register(SettingsDeleteConfirm, "确认删除", "Confirm delete");
        Loc.Register(SettingsCancel, "取消", "Cancel");
        Loc.Register(SettingsOpenCard, "查看审批卡", "Show approval card");
        Loc.Register(SettingsOpenCardFailed, "这个草稿已经不在待审批列表里了。", "That draft is no longer waiting for approval.");
        Loc.Register(SettingsRevoke, "撤销", "Revoke");
        Loc.Register(SettingsToolsLine, "工具：{0}", "Tools: {0}");
        Loc.Register(SettingsScopeSession, "本会话", "This session");
        Loc.Register(SettingsScopeProject, "本项目", "This project");
        Loc.Register(SettingsScopeGlobal, "永久", "Always");
        Loc.Register(SettingsRuleLine, "{0} · {1}", "{0} · {1}");
        Loc.Register(SettingsLoadProblem, "审批规则文件有问题，已按没有记住处理：{0}", "The approval rules file has a problem and was treated as empty: {0}");
        Loc.Register(SettingsActionFailed, "操作失败：{0}", "The action failed: {0}");

        Loc.Register(RulesUnreadable, "approval-rules.json 无法读取：{0}", "approval-rules.json could not be read: {0}");
        Loc.Register(RulesReadFailed, "读取 approval-rules.json 失败：{0}", "Reading approval-rules.json failed: {0}");
        Loc.Register(RulesInvalid, "approval-rules.json 不是有效的审批规则文件：{0}", "approval-rules.json is not a valid approval rules file: {0}");
        Loc.Register(RulesEmpty, "approval-rules.json 为空。", "approval-rules.json is empty.");
        Loc.Register(RulesVersion, "approval-rules.json 的版本 {0} 无法识别。", "approval-rules.json has an unrecognized version: {0}.");
        Loc.Register(RulesNoList, "approval-rules.json 缺少规则列表。", "approval-rules.json has no rule list.");
        Loc.Register(RulesWriteFailed, "写入 approval-rules.json 失败：{0}", "Writing approval-rules.json failed: {0}");

        Loc.Register(StatusDraft, "草稿", "Draft");
        Loc.Register(StatusApproved, "已批准", "Approved");
        Loc.Register(StatusDisabled, "已禁用", "Disabled");
        Loc.Register(StatusUninstalled, "已卸载", "Uninstalled");
        Loc.Register(StatusPending, "待审批", "Pending approval");
        Loc.Register(StatusUnknown, "未知", "Unknown");
    }
}
