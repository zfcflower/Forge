using System.Runtime.CompilerServices;
using Forge.App.ViewModels;

namespace Forge.App.Localization;

/// <summary>
/// The chat timeline's own string table (wave W13): the readable name every axis node carries for a screen reader, and the
/// word for its state. Keys are namespaced <c>timeline.*</c> and registered from a module initializer, so no shared table is
/// edited by this feature.
/// <para>These texts are never drawn on screen - the icon is the visual - so they are read through
/// <see cref="Describe"/> only, which resolves both parts in the current language on every call; nothing is cached across a
/// language switch.</para>
/// </summary>
internal static class TimelineStrings
{
    public const string NameUser = "timeline.name.user";
    public const string NameAssistant = "timeline.name.assistant";
    public const string NameThinking = "timeline.name.thinking";
    public const string NameReadFile = "timeline.name.readFile";
    public const string NameSearch = "timeline.name.search";
    public const string NameEditFile = "timeline.name.editFile";
    public const string NameCommand = "timeline.name.command";
    public const string NameNetwork = "timeline.name.network";
    public const string NameMcp = "timeline.name.mcp";
    public const string NameSubAgent = "timeline.name.subAgent";
    public const string NamePlan = "timeline.name.plan";
    public const string NameNotice = "timeline.name.notice";
    public const string NameError = "timeline.name.error";
    public const string NameApproval = "timeline.name.approval";
    public const string NameTool = "timeline.name.tool";

    public const string StateRunning = "timeline.state.running";
    public const string StateFailed = "timeline.state.failed";
    public const string StateWarning = "timeline.state.warning";

    /// <summary>A node and its state: <c>{0}</c> is the name, <c>{1}</c> the state word.</summary>
    public const string Node = "timeline.node";

    [ModuleInitializer]
    internal static void Initialize() => Register();

    /// <summary>Idempotent: registering the same key with the same texts twice is a no-op.</summary>
    internal static void Register()
    {
        Loc.Register(NameUser, "用户消息", "User message");
        Loc.Register(NameAssistant, "助手回复", "Assistant reply");
        Loc.Register(NameThinking, "思考", "Thinking");
        Loc.Register(NameReadFile, "读取文件", "Read file");
        Loc.Register(NameSearch, "搜索", "Search");
        Loc.Register(NameEditFile, "编辑文件", "Edit file");
        Loc.Register(NameCommand, "运行命令", "Command");
        Loc.Register(NameNetwork, "网络请求", "Network request");
        Loc.Register(NameMcp, "MCP 工具", "MCP tool");
        Loc.Register(NameSubAgent, "子 Agent", "Sub-agent");
        Loc.Register(NamePlan, "计划", "Plan");
        Loc.Register(NameNotice, "提示", "Note");
        Loc.Register(NameError, "错误", "Error");
        Loc.Register(NameApproval, "审批", "Approval");
        Loc.Register(NameTool, "工具", "Tool");

        Loc.Register(StateRunning, "运行中", "running");
        Loc.Register(StateFailed, "失败", "failed");
        Loc.Register(StateWarning, "已拒绝", "denied");

        Loc.Register(Node, "{0}，{1}", "{0}, {1}");
    }

    /// <summary>The node's readable name in the current language, with its state when it is not idle.</summary>
    internal static string Describe(TimelineNodeKind kind, TimelineNodeState state)
    {
        var name = Loc.Instance[TimelineNodeMap.NameKey(kind)];
        var stateKey = TimelineNodeMap.StateKey(state);
        return stateKey.Length == 0 ? name : Loc.Instance.Format(Node, name, Loc.Instance[stateKey]);
    }
}