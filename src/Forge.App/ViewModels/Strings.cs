using System.Globalization;
using Forge.Abstractions;

namespace Forge.App.ViewModels;

/// <summary>
/// Every user-visible string of the shell in one place, so a later localization pass only has to swap this class out.
/// Formatted strings are methods because the product copy carries the counters ("第 N 轮", "M/N 完成") rather than
/// concatenations scattered over the views.
/// </summary>
internal static class Strings
{
    /// <summary>Shown instead of a number the provider did not report.</summary>
    public const string None = "—";

    // ---------------------------------------------------------------- shell
    public const string AppName = "Forge";
    public const string NoOpenSession = "没有打开的会话";
    public const string DocumentsTitle = "会话";
    public const string SettingsTitle = "设置";
    public const string Close = "关闭";
    public const string CloseOtherTabs = "关闭其他标签";
    public const string FloatToWindow = "浮动到新窗口";

    // ---------------------------------------------------------------- workbench chrome (tooltips of the title-bar icon buttons)
    public const string PinRegion = "停靠";
    public const string AutoHideRegion = "自动隐藏";
    public const string MaximizePanel = "放大";
    public const string RestorePanel = "还原";
    public const string OpenInNewWindow = "在新窗口打开";
    public const string DockBack = "停靠回主窗口";
    public const string CoverMain = "铺满主区";
    public const string UncoverMain = "退出铺满";
    public const string PreviewTitle = "预览";
    public const string Cancel = "取消";
    public const string Confirm = "确定";
    public const string ChangesTab = "改动";
    public const string OpenChangesTooltip = "打开改动文档";
    public const string SettingsTooltip = "设置";

    // ---------------------------------------------------------------- project tree
    public const string SectionProjects = "项目";
    public const string GeneralSection = "常规对话";
    public const string AddProject = "+ 添加项目";
    public const string AddProjectTooltip = "添加一个 git 仓库作为项目";
    public const string NewGeneralSessionTooltip = "新建常规对话";
    public const string NewSession = "新建会话";
    public const string NewSessionTooltip = "新建会话";
    public const string Rename = "重命名";
    public const string Archive = "归档";
    public const string RevealInExplorer = "在资源管理器中打开";
    public const string RemoveProject = "移除项目(仅取消注册)";
    public const string Open = "打开";
    public const string Refresh = "刷新";
    public const string EmptyProjects = "还没有项目。用“+”添加一个 git 仓库。";

    public static string ProjectCount(int count) => "共 " + Number(count) + " 个区域";

    public static string HistoryTitle(int count) => "历史 (" + Number(count) + ")";

    public static string ArchivedSubtitle() => "已归档 · 打开即恢复";

    public static string SessionTitle(string prefix) => prefix + " " + DateTimeOffset.Now.ToString("MM-dd HH:mm", CultureInfo.InvariantCulture);

    public static string GeneralSessionTitle() => SessionTitle("常规");

    public static string WorktreeSessionTitle() => SessionTitle("会话");

    public static string RenameSessionTitle() => "重命名会话";

    public static string RenameProjectTitle() => "重命名项目";

    public static string RunningLabel() => "运行中";

    public static string GeneralWorkspaceFailed(string message) => "通用工作区注册失败：" + message;

    public static string ProjectTreeFailed(string message) => "项目树读取失败：" + message;

    public static string NotAGitRepository() => "该文件夹不是 git 仓库。";

    public static string GeneralWorkspaceMissing() => "常规工作区尚未注册。";

    // ---------------------------------------------------------------- add project dialog
    public const string PickRepositoryTitle = "添加 git 仓库";
    public const string NotRepositoryTitle = "不是 git 仓库";
    public const string GitInit = "初始化仓库";
    public const string NewSessionTitle = "新建会话";
    public const string NewSessionQuestion = "要新建一个会话吗？";
    public const string Create = "新建";

    public static string NotRepositoryMessage(string folder) =>
        folder + "\n\n这个文件夹不是 git 仓库，要在这里执行 git init 吗？";

    // ---------------------------------------------------------------- transcript
    public const string TurnRunning = "运行中…";
    public const string NoText = "（模型没有返回文本）";
    public const string NoToolResult = "没有结果：本轮已结束";
    public const string ToolArguments = "参数";
    public const string ToolResult = "结果";
    public const string CollapseTurn = "折叠本轮";
    public const string StartConversation = "开始对话";
    public const string ChatHint = "输入消息后按 Ctrl+Enter 发送。工具调用与每一轮都会显示为卡片。";
    public const string MessagePlaceholder = "描述要完成的任务…";
    public const string SendTooltip = "发送 (Ctrl+Enter)";
    public const string StopTurnTooltip = "停止本轮";
    public const string Stopped = "已停止";
    public const string ReadFailed = "读取会话失败：";
    public const string TurnFailed = "本轮无法运行：";
    public const string ProviderMissing = "尚未配置模型提供方：";
    public const string SessionMessageName = "消息";
    public const string SendName = "发送";
    public const string StopName = "停止";

    public static string StopReasonText(StopReason reason) => reason switch
    {
        Forge.Abstractions.StopReason.EndTurn => "正常结束",
        Forge.Abstractions.StopReason.ToolUse => "请求工具",
        Forge.Abstractions.StopReason.MaxTokens => "达到长度上限",
        _ => "其他原因",
    };

    // ---------------------------------------------------------------- turn card
    public static string TurnHeader(
        int round,
        DateTimeOffset startedAt,
        TimeSpan? elapsed,
        bool running,
        int? inputTokens,
        int? outputTokens)
    {
        var builder = new System.Text.StringBuilder();
        builder.Append("第 ").Append(Number(round)).Append(" 轮 · ");
        builder.Append(startedAt.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture));
        builder.Append(" · ").Append(running ? TurnRunning : elapsed is { } value ? FormatElapsed(value) : None);
        builder.Append(" · ").Append(TokenSummary(inputTokens, outputTokens));
        return builder.ToString();
    }

    /// <summary>"输入 900 · 输出 80"; a side the provider did not report becomes "—".</summary>
    public static string TokenSummary(int? inputTokens, int? outputTokens) =>
        "输入 " + TokenCount(inputTokens) + " · 输出 " + TokenCount(outputTokens);

    public static string TokenCount(int? tokens) => tokens is { } value ? Number(value) : None;

    public static string FormatElapsed(TimeSpan elapsed) =>
        elapsed.TotalSeconds < 60
            ? elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " 秒"
            : ((int)elapsed.TotalMinutes).ToString(CultureInfo.InvariantCulture) + " 分 " +
              elapsed.Seconds.ToString("00", CultureInfo.InvariantCulture) + " 秒";

    public static string Denied(string tool) => "已拒绝：" + tool + "（尚未配置审批对话框）";

    public static string Compacted(int before, int after) =>
        "已压缩上下文：" + Number(before) + " → " + Number(after) + " tokens";

    // ---------------------------------------------------------------- sub agents
    public const string SubAgentQueued = "排队中";
    public const string SubAgentRunning = "运行中";
    public const string SubAgentCompleted = "完成";
    public const string SubAgentStopped = "已停止";
    public const string SubAgentFailed = "失败";

    /// <summary>Shown for a stored sub-agent whose run outcome the store cannot prove.</summary>
    public const string SubAgentEnded = "已结束";
    public const string SubAgentWaitingSlot = "等待运行名额";
    public const string SubAgentSteered = "已转向";
    public const string SubAgentStop = "停止";
    public const string SubAgentStopName = "停止子 Agent";
    public const string SubAgentStopRequested = "已请求停止";
    public const string SubAgentStoppedByUser = "已停止";
    public const string SubAgentsTitle = "子 Agent";
    public const string MainAgentTitle = "主 Agent";
    public const string FinishedAgentsTitle = "已结束";
    public const string FinishedAgentsHint = "按结束顺序排列，选择一个查看只读记录。";
    public const string ReadOnlyBadge = "已结束";
    public const string NoModel = "未配置模型";

    public static string FinishedAgentsMenu(int count) => FinishedAgentsTitle + " (" + Number(count) + ")";

    public const string SubAgentsCollapse = "收起";
    public const string SubAgentsExpand = "展开";
    public const string SubAgentsToggleName = "子 Agent 区域";
    public const string SubAgentsToggleTooltip = "收起或展开子 Agent 区域；手动收起后本会话不再自动弹出";

    public static string SubAgentsToggle(int running, bool collapsed) =>
        SubAgentsTitle +
        (running > 0 ? " " + Number(running) : string.Empty) +
        " · " +
        (collapsed ? SubAgentsExpand : SubAgentsCollapse);

    public static string SubAgentReplayFailed(string message) => "重放子 Agent 会话失败：" + message;

    public static string TurnCheckpointFailed(string message) => "轮次检查点失败：" + message;

    public const string FinishedAgentsNoTranscript = "这次子 Agent 没有存下可重放的消息。";

    public static string SubAgentSteeredNote(int droppedToolCalls) =>
        SubAgentSteered + " · 丢弃 " + Number(droppedToolCalls) + " 个调用";

    public static string SubAgentFollowUpNote(int pending) => "后续指令排队 (" + Number(pending) + ")";

    public static string SteeredNotice(string instruction) => "已转向：" + instruction;

    public static string FollowUpNotice(string instruction) => "后续指令：" + instruction;

    // ---------------------------------------------------------------- files panel
    public const string FilesAll = "全部";
    public const string FilesChanged = "已改动";
    public const string NoSession = "没有打开的会话";
    public const string FilesName = "文件";
    public const string RefreshFiles = "刷新文件";
    public const string OpenInNewTab = "在新标签页打开";
    public const string FileTabGlyph = "▤";

    public static string FileSummary(int changed, int files) =>
        Number(changed) + " 个改动 · " + Number(files) + " 个文件";

    public static string WatchUnavailable(string message) => "文件监视不可用：" + message;

    public static string StagedAndUnstaged() => "已暂存并有未暂存改动";

    public static string Staged() => "已暂存";

    public static string WorkingTree() => "工作区";

    public static string FileMissing(string path) => "文件不存在：" + path;

    public static string FileTooLarge(long bytes) => "文件过大，无法显示：" + Number(bytes) + " 字节";

    public static string LinesTruncated(int lines) => "只显示前 " + Number(lines) + " 行。";

    public static string LoadingFile() => "读取中…";

    public static string FileTabTitle(string fileName) => FileTabGlyph + " " + fileName;

    // ---------------------------------------------------------------- changes document
    public const string ChangesByTurn = "按轮次";
    public const string ChangesUncommitted = "未提交改动";
    public const string ChangesGlyph = "±";
    public const string ChangesTitle = ChangesGlyph + " 改动";
    public const string RefreshChanges = "刷新改动";
    public const string NoChanges = "没有改动。";
    public const string NoTextualChange = "（没有文本改动：二进制或仅权限变化）";

    public static string ChangesSummary(int files, bool byTurn) =>
        Number(files) + " 个文件 · " + (byTurn ? ChangesByTurn : ChangesUncommitted);

    public static string ChangeTurnLabel(int turn, DateTimeOffset startedAt) =>
        "第 " + Number(turn) + " 轮 · " + startedAt.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    public static string FileDiffHeader(string path, int added, int removed) =>
        path + "  +" + Number(added) + " −" + Number(removed);

    // ---------------------------------------------------------------- todo
    public const string TodoTitle = "TODO";
    public const string NewTaskPlaceholder = "添加任务…";
    public const string AddTask = "添加任务";
    public const string Done = "完成";
    public const string MoveUp = "上移";
    public const string MoveDown = "下移";
    public const string Delete = "删除";
    public const string ClearDone = "清除已完成";

    public static string TodoEmpty() => "还没有任务";

    public static string TodoSummary(int done, int total) => Number(done) + "/" + Number(total) + " 完成";

    // ---------------------------------------------------------------- review
    public const string ReviewTitle = "评审";
    public const string Resolved = "已解决";
    public const string Unresolved = "未解决";
    public const string Sent = "已发送";
    public const string Draft = "草稿";

    public static string ReviewSummary(int comments) => Number(comments) + " 条意见";

    public static string ReviewLine(int start, int end) =>
        start == end
            ? "L" + start.ToString(CultureInfo.InvariantCulture)
            : "L" + start.ToString(CultureInfo.InvariantCulture) + "–" + end.ToString(CultureInfo.InvariantCulture);

    public static string ReviewStatus(bool resolved, bool sent) =>
        (resolved ? Resolved : Unresolved) + " · " + (sent ? Sent : Draft);

    // ---------------------------------------------------------------- spec tree
    public const string SpecTreeTitle = "规格树";
    public const string NoProject = "没有打开的项目";

    public static string SpecCount(int count) => Number(count) + " 份规格";

    // ---------------------------------------------------------------- status bar
    public const string HostStarting = "扩展宿主：启动中";
    public const string HostStopped = "扩展宿主：已停止";
    public const string HostRunning = "扩展宿主：运行中";
    public const string HostFailed = "扩展宿主：失败";
    public const string PluginsUnknown = "插件：未知";
    public const string McpNone = "MCP：0 个配置";
    public const string ApprovalsUnknown = "审批：不可用";
    public const string ProviderUnknown = "提供方：未知";
    public const string ApprovalsUnavailable = "审批数据库不可用：";
    public const string InitializationFailed = "初始化失败：";
    public const string DatabaseProblem = "forge.db：";
    public const string ProjectsProblem = "项目：";
    public const string TurnRefreshFailed = "刷新失败：";

    public static string HostText(string state) => "扩展宿主：" + state;

    public static string HostStateName(string state) => state switch
    {
        "starting" => "启动中",
        "running" => "运行中",
        "failed" => "失败",
        "stopped" => "已停止",
        _ => state,
    };

    public static string Plugins(int plugins, int tools) =>
        "插件 " + Number(plugins) + " · 工具 " + Number(tools);

    public static string Mcp(int configured) => "MCP：" + Number(configured) + " 个配置";

    public static string McpState(int configured, int ready) =>
        "MCP：" + Number(configured) + " 个配置 · " + Number(ready) + " 个就绪";

    public static string Approvals(int records) => "审批 " + Number(records) + " 条";

    public static string ProviderText(string status) => "提供方：" + status;

    public static string ProcessId(int id) => "进程 " + id.ToString(CultureInfo.InvariantCulture);

    // ---------------------------------------------------------------- provider settings
    public static string ProviderReady(string summary) => "已就绪：" + summary;

    public static string ProviderNoKey(string summary) => "已配置但没有 API 密钥：" + summary;

    public static string ProviderSettingsMissing() => "未配置提供方（找不到 settings.json）";

    public static string ProviderSettingsUnreadable(string message) => "未配置提供方（settings.json 无法读取：" + message + "）";

    public static string ProviderSectionMissing() => "未配置提供方（settings.json 没有 provider 段）";

    public static string ProviderInvalid(string message) => "未配置提供方（" + message + "）";

    public static string KeyFromEnvironment() => "来自环境变量";

    public static string KeyFromFile() => "来自 settings.json";

    public static string KeyMissing() => "缺失";

    // ---------------------------------------------------------------- settings panel
    public const string ProviderSectionTitle = "模型提供方";
    public const string GeneralWorkspaceSectionTitle = "常规工作区";
    public const string PathsSectionTitle = "路径与服务";
    public const string ProviderHint = "提供方来自 settings.json，启动时与点击重新加载时读取；密钥可来自该文件指定的环境变量。";
    public const string ReloadSettings = "重新加载设置";

    public static string ProviderDetail(string summary, bool hasKey, bool fromEnvironment) =>
        summary + " · 密钥：" + (hasKey ? (fromEnvironment ? KeyFromEnvironment() : KeyFromFile()) : KeyMissing());

    public static string McpDetail(int configured, int diagnostics) =>
        Number(configured) + " 个配置 · " + Number(diagnostics) + " 条配置诊断";

    public static string EditorDetail(int languages, int grammars, string? problem) =>
        Number(languages) + " 种语言 · " + Number(grammars) + " 份语法" + (problem is null ? string.Empty : " · " + problem);

    // ---------------------------------------------------------------- shared
    public static string Number(int value) => value.ToString("N0", CultureInfo.InvariantCulture);

    public static string Number(long value) => value.ToString("N0", CultureInfo.InvariantCulture);
}
