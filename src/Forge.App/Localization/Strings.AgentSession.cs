using System.Runtime.CompilerServices;

namespace Forge.App.Localization;

/// <summary>
/// The agent-session feature's own sentences (forge-app "Agent 会话的界面与接线" / forge-core "Agent 会话"): the tree menu entry,
/// the reason it is greyed out, and the failures the shell reports when a tab cannot be opened.
/// <para>Keys: <c>agentSession.*</c>. The tab and row label ("root › child") and the unread mark are glyphs plus stored
/// titles, so they need no key; the default child name is the agent kind plus an ordinal, which is not translated either.</para>
/// </summary>
internal static class AgentSessionStrings
{
    // ---------------------------------------------------------------- the tree menu entry
    public const string MenuNewKey = "agentSession.menu.new";

    // ---------------------------------------------------------------- why the entry cannot run
    public const string NoRootKey = "agentSession.reason.noRoot";
    public const string NoKindKey = "agentSession.reason.noKind";

    // ---------------------------------------------------------------- failures the shell reports
    public const string OpenFailedKey = "agentSession.openFailed";
    public const string RootNotActiveKey = "agentSession.rootNotActive";

    public static string MenuNew => Loc.Instance[MenuNewKey];

    public static string NoRoot => Loc.Instance[NoRootKey];

    public static string NoKind => Loc.Instance[NoKindKey];

    public static string RootNotActive(string root) => Loc.Instance.Format(RootNotActiveKey, root);

    public static string OpenFailed(string reason) => Loc.Instance.Format(OpenFailedKey, reason);

    [ModuleInitializer]
    internal static void Register()
    {
        Loc.Register(MenuNewKey, "新增 Agent 会话", "New agent session");
        Loc.Register(NoRootKey, "这个工作区没有活动的根会话", "No active root session in this workspace");
        Loc.Register(NoKindKey, "没有可用作会话的 Agent 种类:它们的工具集能改 git 或开 PR", "No agent kind can drive a session here: their tool sets change git or open pull requests");
        Loc.Register(OpenFailedKey, "打不开这个 Agent 会话:{0}", "Could not open this agent session: {0}");
        Loc.Register(RootNotActiveKey, "根会话「{0}」已不是活动状态,它的 Agent 会话不能再起回合。", "The root session \"{0}\" is no longer active, so its agent sessions cannot start a turn.");
    }
}
