using System.Runtime.CompilerServices;
using Forge.App.Agents;

namespace Forge.App.Localization;

/// <summary>
/// The resource window's table: the overlay the composer's "资源" button opens for the current session. Four groups — the
/// automation agents that are running, the MCP servers, what the tool definitions cost and what the prompt snapshot costs —
/// plus the command card's tail captions and the <c>inspector</c> settings section. Keys: <c>resources.*</c>.
/// <para>Only names and wording live here; the numbers (counts, characters, tokens) are formatted by the callers and arrive
/// as arguments, so a language switch re-renders a row without re-reading anything.</para>
/// </summary>
internal static class ResourceInspectorStrings
{
    // ---------------------------------------------------------------- the window's chrome
    public const string TitleKey = "resources.title";
    public const string CloseKey = "resources.close";
    public const string CloseTooltipKey = "resources.closeTooltip";
    public const string NoSessionKey = "resources.noSession";

    // ---------------------------------------------------------------- the composer button
    public const string ButtonTooltipKey = "resources.button.tooltip";
    public const string ButtonRunningTooltipKey = "resources.button.runningTooltip";
    public const string ButtonFailedTooltipKey = "resources.button.failedTooltip";

    // ---------------------------------------------------------------- automation agents
    public const string AgentsTitleKey = "resources.agents.title";
    public const string AgentsEmptyKey = "resources.agents.empty";
    public const string AgentsOpenTooltipKey = "resources.agents.openTooltip";

    // ---------------------------------------------------------------- MCP servers
    public const string McpTitleKey = "resources.mcp.title";
    public const string McpNoteKey = "resources.mcp.note";
    public const string McpEmptyKey = "resources.mcp.empty";
    public const string McpServerKey = "resources.mcp.server";
    public const string McpStatusKey = "resources.mcp.status";
    public const string McpRestartKey = "resources.mcp.restart";
    public const string McpStopKey = "resources.mcp.stop";
    public const string McpStartKey = "resources.mcp.start";
    public const string McpSignInKey = "resources.mcp.signIn";
    public const string McpDisabledKey = "resources.mcp.disabled";

    // ---------------------------------------------------------------- the two usage tables
    public const string ColumnToolsKey = "resources.column.tools";
    public const string ColumnCharsKey = "resources.column.chars";
    public const string ColumnTokensKey = "resources.column.tokens";
    public const string TotalKey = "resources.total";
    public const string ToolsTitleKey = "resources.tools.title";
    public const string ToolsNoteKey = "resources.tools.note";
    public const string ToolsCategoryKey = "resources.tools.category";
    public const string ToolsEmptyKey = "resources.tools.empty";

    // ---------------------------------------------------------------- prompts
    public const string PromptsTitleKey = "resources.prompts.title";
    public const string PromptsSourceKey = "resources.prompts.source";
    public const string PromptsEmptyKey = "resources.prompts.empty";
    public const string PromptsStaleKey = "resources.prompts.stale";
    public const string PromptsReloadKey = "resources.prompts.reload";
    public const string PromptsReloadTooltipKey = "resources.prompts.reloadTooltip";
    public const string PromptsTurnRunningKey = "resources.prompts.turnRunning";
    public const string PromptsAgentRunningKey = "resources.prompts.agentRunning";
    public const string PromptSystemKey = "resources.prompts.system";
    public const string PromptUserAgentsKey = "resources.prompts.userAgents";
    public const string PromptWorkspaceAgentsKey = "resources.prompts.workspaceAgents";
    public const string PromptSkillsKey = "resources.prompts.skills";
    public const string PromptRenderingKey = "resources.prompts.rendering";
    public const string EstimateNoteKey = "resources.estimateNote";

    // ---------------------------------------------------------------- the command card in the transcript
    public const string TailKey = "resources.tail";
    public const string TailEmptyKey = "resources.tail.empty";
    public const string RootAgentKey = "resources.agent.root";
    public const string ChildAgentKey = "resources.agent.child";

    // ---------------------------------------------------------------- the `inspector` settings section
    public const string SettingsTitleKey = "resources.settings.title";
    public const string SettingsHintKey = "resources.settings.hint";
    public const string SettingsTailLinesKey = "resources.settings.tailLines";
    public const string SettingsTailLinesHintKey = "resources.settings.tailLinesHint";

    /// <summary>Registers this table before any code of the assembly runs (module initializer, AOT safe).</summary>
    [ModuleInitializer]
    internal static void Initialize() => Register();

    public static string Title => Loc.Instance[TitleKey];

    public static string Close => Loc.Instance[CloseKey];

    public static string CloseTooltip => Loc.Instance[CloseTooltipKey];

    public static string NoSession => Loc.Instance[NoSessionKey];

    public static string ButtonTooltip => Loc.Instance[ButtonTooltipKey];

    /// <summary>"3 个自动化 Agent 运行中": the button's tooltip while any runs.</summary>
    public static string ButtonRunningTooltip(int running) =>
        Loc.Instance.Format(ButtonRunningTooltipKey, TextFormat.Number(running));

    public static string ButtonFailedTooltip => Loc.Instance[ButtonFailedTooltipKey];

    public static string AgentsTitle => Loc.Instance[AgentsTitleKey];

    public static string AgentsEmpty => Loc.Instance[AgentsEmptyKey];

    public static string AgentsOpenTooltip => Loc.Instance[AgentsOpenTooltipKey];

    public static string McpTitle => Loc.Instance[McpTitleKey];

    public static string McpNote => Loc.Instance[McpNoteKey];

    public static string McpEmpty => Loc.Instance[McpEmptyKey];

    public static string McpServer => Loc.Instance[McpServerKey];

    public static string McpStatus => Loc.Instance[McpStatusKey];

    public static string McpRestart => Loc.Instance[McpRestartKey];

    public static string McpStop => Loc.Instance[McpStopKey];

    public static string McpStart => Loc.Instance[McpStartKey];

    public static string McpSignIn => Loc.Instance[McpSignInKey];

    public static string McpDisabled => Loc.Instance[McpDisabledKey];

    public static string ColumnTools => Loc.Instance[ColumnToolsKey];

    public static string ColumnChars => Loc.Instance[ColumnCharsKey];

    public static string ColumnTokens => Loc.Instance[ColumnTokensKey];

    public static string Total => Loc.Instance[TotalKey];

    public static string ToolsTitle => Loc.Instance[ToolsTitleKey];

    public static string ToolsNote => Loc.Instance[ToolsNoteKey];

    public static string ToolsCategory => Loc.Instance[ToolsCategoryKey];

    public static string ToolsEmpty => Loc.Instance[ToolsEmptyKey];

    public static string PromptsTitle => Loc.Instance[PromptsTitleKey];

    public static string PromptsSource => Loc.Instance[PromptsSourceKey];

    public static string PromptsEmpty => Loc.Instance[PromptsEmptyKey];

    public static string PromptsStale => Loc.Instance[PromptsStaleKey];

    public static string PromptsReload => Loc.Instance[PromptsReloadKey];

    public static string PromptsReloadTooltip => Loc.Instance[PromptsReloadTooltipKey];

    public static string PromptsTurnRunning => Loc.Instance[PromptsTurnRunningKey];

    public static string PromptsAgentRunning => Loc.Instance[PromptsAgentRunningKey];

    public static string EstimateNote => Loc.Instance[EstimateNoteKey];

    /// <summary>The row name of one prompt source in the prompt table.</summary>
    public static string PromptSource(PromptUsageKind kind) => kind switch
    {
        PromptUsageKind.System => Loc.Instance[PromptSystemKey],
        PromptUsageKind.UserAgents => Loc.Instance[PromptUserAgentsKey],
        PromptUsageKind.WorkspaceAgents => Loc.Instance[PromptWorkspaceAgentsKey],
        PromptUsageKind.Skills => Loc.Instance[PromptSkillsKey],
        _ => Loc.Instance[PromptRenderingKey],
    };

    /// <summary>"主 Agent": the agent a tool call belongs to when it is the session's own agent.</summary>
    public static string RootAgent => Loc.Instance[RootAgentKey];

    /// <summary>"子 Agent": the agent a tool call belongs to when a child runs it (the child's kind is shown beside it).</summary>
    public static string ChildAgent => Loc.Instance[ChildAgentKey];

    public static string SettingsTitle => Loc.Instance[SettingsTitleKey];

    public static string SettingsHint => Loc.Instance[SettingsHintKey];

    public static string SettingsTailLines => Loc.Instance[SettingsTailLinesKey];

    public static string SettingsTailLinesHint => Loc.Instance[SettingsTailLinesHintKey];

    internal static void Register()
    {
        Loc.Register(TitleKey, "资源", "Resources");
        Loc.Register(CloseKey, "关闭", "Close");
        Loc.Register(CloseTooltipKey, "关闭资源窗口", "Close the resource window");
        Loc.Register(NoSessionKey, "没有打开的会话", "No session is open");

        Loc.Register(
            ButtonTooltipKey,
            "资源:自动化 Agent、MCP 服务器、工具与提示词占用的上下文",
            "Resources: automation agents, MCP servers and the context the tools and prompts take");
        Loc.Register(ButtonRunningTooltipKey, "{0} 个自动化 Agent 运行中", "{0} automation agent(s) running");
        Loc.Register(ButtonFailedTooltipKey, "有 MCP 服务器连接失败", "An MCP server failed to connect");

        Loc.Register(AgentsTitleKey, "自动化 Agent", "Automation agents");
        Loc.Register(AgentsEmptyKey, "没有运行中的自动化 Agent", "No automation agent is running");
        Loc.Register(AgentsOpenTooltipKey, "打开这个 Agent 的标签页", "Open this agent's tab");

        Loc.Register(McpTitleKey, "MCP 服务器", "MCP servers");
        Loc.Register(McpNoteKey, "停止与重连影响所有会话", "Stopping or reconnecting affects every session");
        Loc.Register(McpEmptyKey, "没有配置 MCP 服务器", "No MCP server is configured");
        Loc.Register(McpServerKey, "服务器", "Server");
        Loc.Register(McpStatusKey, "状态", "Status");
        Loc.Register(McpRestartKey, "重连", "Reconnect");
        Loc.Register(McpStopKey, "停止", "Stop");
        Loc.Register(McpStartKey, "启动", "Start");
        Loc.Register(McpSignInKey, "登录", "Sign in");
        Loc.Register(McpDisabledKey, "已停用", "Disabled");

        Loc.Register(ColumnToolsKey, "工具", "Tools");
        Loc.Register(ColumnCharsKey, "字符", "Chars");
        Loc.Register(ColumnTokensKey, "≈ token", "≈ tokens");
        Loc.Register(TotalKey, "合计", "Total");
        Loc.Register(ToolsTitleKey, "工具占用", "Tool usage");
        Loc.Register(ToolsNoteKey, "发给模型的工具定义,MCP 工具见上表", "The tool definitions sent to the model; MCP tools are in the table above");
        Loc.Register(ToolsCategoryKey, "类别", "Category");
        Loc.Register(ToolsEmptyKey, "第一回合开始后显示", "Shown once the first turn has started");

        Loc.Register(PromptsTitleKey, "使用的提示词", "Prompts in use");
        Loc.Register(PromptsSourceKey, "来源", "Source");
        Loc.Register(PromptsEmptyKey, "第一回合开始后显示", "Shown once the first turn has started");
        Loc.Register(PromptsStaleKey, "快照已过期", "Snapshot is stale");
        Loc.Register(PromptsReloadKey, "重载", "Reload");
        Loc.Register(
            PromptsReloadTooltipKey,
            "重新读取提示词、技能与 AGENTS.md;下一条消息起使用新快照(提示词缓存会失效一次)",
            "Re-reads the prompts, skills and AGENTS.md; the next message uses the new snapshot (the prompt cache is lost once)");
        Loc.Register(PromptsTurnRunningKey, "回合运行中,结束后可重载", "A turn is running; reload after it ends");
        Loc.Register(PromptsAgentRunningKey, "自动化 Agent 运行中,结束后可重载", "An automation agent is running; reload after it ends");
        Loc.Register(PromptSystemKey, "系统提示词", "System prompt");
        Loc.Register(PromptUserAgentsKey, "用户层 AGENTS.md", "User AGENTS.md");
        Loc.Register(PromptWorkspaceAgentsKey, "工作区 AGENTS.md", "Workspace AGENTS.md");
        Loc.Register(PromptSkillsKey, "技能索引与路由", "Skill index and routing");
        Loc.Register(PromptRenderingKey, "渲染说明", "Rendering notes");
        Loc.Register(
            EstimateNoteKey,
            "字符数是发给模型的文本长度;token 为估算(约四个字符一个),不是模型的真实计数",
            "Characters are the length of the text sent to the model; tokens are an estimate (about four characters each), not the model's own count");

        Loc.Register(TailKey, "输出尾部", "Output tail");
        Loc.Register(TailEmptyKey, "还没有输出", "No output yet");
        Loc.Register(RootAgentKey, "主 Agent", "Main agent");
        Loc.Register(ChildAgentKey, "子 Agent", "Sub-agent");

        Loc.Register(SettingsTitleKey, "资源与命令", "Resources and commands");
        Loc.Register(
            SettingsHintKey,
            "“资源”窗口列出当前会话的自动化 Agent、MCP 服务器以及工具和提示词占用的上下文;运行中的命令在转录里展开时显示输出尾部。",
            "The Resources window lists the session's automation agents, MCP servers and the context its tools and prompts take; a running command shows its output tail when expanded in the transcript.");
        Loc.Register(SettingsTailLinesKey, "输出尾部行数", "Output tail lines");
        Loc.Register(
            SettingsTailLinesHintKey,
            "运行中的命令显示最后多少行(20–1000)。命令输出的缓冲上限是 16 KiB,行数再大也只到这么多。",
            "How many lines of a running command are shown (20-1000). The command's output buffer is capped at 16 KiB, so a larger line count still ends there.");
    }
}
