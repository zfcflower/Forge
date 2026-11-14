using Forge.App.Localization;
using Forge.App.Preferences;
using Forge.App.Tests.Localization;
using Forge.App.ViewModels;
using Xunit;

namespace Forge.App.Tests.ViewModels;

/// <summary>
/// Pins the pure half of the timeline node mapping (forge-app spec, "聊天时间轴(W13)"): the exact tool-name table, the
/// state precedence (a failure beats a run; a denied approval is a warning, not a failure), the error icon a failed
/// notice wears, and the icon/name/state keys every kind carries. Everything here is assertable without a visual tree.
/// </summary>
public sealed class TimelineNodeMapTests
{
    private static readonly TimelineNodeKind[] AllKinds = Enum.GetValues<TimelineNodeKind>();

    // ---------------------------------------------------------------- KindForTool: the exact table

    [Theory]
    // Reads: files, history, repository objects, specs.
    [InlineData("read", nameof(TimelineNodeKind.ReadFile))]
    [InlineData("read_artifact", nameof(TimelineNodeKind.ReadFile))]
    [InlineData("read_history", nameof(TimelineNodeKind.ReadFile))]
    [InlineData("git_status", nameof(TimelineNodeKind.ReadFile))]
    [InlineData("git_log", nameof(TimelineNodeKind.ReadFile))]
    [InlineData("git_diff", nameof(TimelineNodeKind.ReadFile))]
    [InlineData("spec_get", nameof(TimelineNodeKind.ReadFile))]
    [InlineData("spec_graph", nameof(TimelineNodeKind.ReadFile))]
    [InlineData("spec_validate", nameof(TimelineNodeKind.ReadFile))]
    // Searches.
    [InlineData("ls", nameof(TimelineNodeKind.Search))]
    [InlineData("find", nameof(TimelineNodeKind.Search))]
    [InlineData("grep", nameof(TimelineNodeKind.Search))]
    [InlineData("search_history", nameof(TimelineNodeKind.Search))]
    [InlineData("spec_grep", nameof(TimelineNodeKind.Search))]
    [InlineData("spec_search", nameof(TimelineNodeKind.Search))]
    // Writes and edits.
    [InlineData("edit", nameof(TimelineNodeKind.EditFile))]
    [InlineData("write", nameof(TimelineNodeKind.EditFile))]
    [InlineData("spec_create", nameof(TimelineNodeKind.EditFile))]
    [InlineData("spec_update", nameof(TimelineNodeKind.EditFile))]
    [InlineData("spec_delete", nameof(TimelineNodeKind.EditFile))]
    // Commands.
    [InlineData("bash", nameof(TimelineNodeKind.Command))]
    [InlineData("run_code", nameof(TimelineNodeKind.Command))]
    // Network: the web, a remote, the pull-request platform.
    [InlineData("web_fetch", nameof(TimelineNodeKind.Network))]
    [InlineData("web_search", nameof(TimelineNodeKind.Network))]
    [InlineData("git_fetch", nameof(TimelineNodeKind.Network))]
    [InlineData("git_sync", nameof(TimelineNodeKind.Network))]
    [InlineData("pr_view", nameof(TimelineNodeKind.Network))]
    [InlineData("pr_create", nameof(TimelineNodeKind.Network))]
    [InlineData("pr_sync", nameof(TimelineNodeKind.Network))]
    [InlineData("pr_edit", nameof(TimelineNodeKind.Network))]
    [InlineData("pr_workflow", nameof(TimelineNodeKind.Network))]
    [InlineData("pr_checks", nameof(TimelineNodeKind.Network))]
    [InlineData("pr_ci_log", nameof(TimelineNodeKind.Network))]
    [InlineData("pr_comments", nameof(TimelineNodeKind.Network))]
    [InlineData("pr_comment_reply", nameof(TimelineNodeKind.Network))]
    [InlineData("pr_comment_resolve", nameof(TimelineNodeKind.Network))]
    [InlineData("review_comments", nameof(TimelineNodeKind.Network))]
    [InlineData("review_comment_resolve", nameof(TimelineNodeKind.Network))]
    [InlineData("pr_screenshots", nameof(TimelineNodeKind.Network))]
    // An unknown name that only carries the systematic prefix is still network.
    [InlineData("pr_future_tool", nameof(TimelineNodeKind.Network))]
    [InlineData("review_future_tool", nameof(TimelineNodeKind.Network))]
    // MCP: the search helper and every server tool by its naming separator.
    [InlineData("mcp_tool_search", nameof(TimelineNodeKind.McpTool))]
    [InlineData("files__write", nameof(TimelineNodeKind.McpTool))]
    [InlineData("github__create_issue", nameof(TimelineNodeKind.McpTool))]
    // Sub-agents and the review that runs one.
    [InlineData("spawn_agent", nameof(TimelineNodeKind.SubAgent))]
    [InlineData("subagent_steer", nameof(TimelineNodeKind.SubAgent))]
    [InlineData("subagent_followup", nameof(TimelineNodeKind.SubAgent))]
    [InlineData("subagent_stop", nameof(TimelineNodeKind.SubAgent))]
    [InlineData("request_review", nameof(TimelineNodeKind.SubAgent))]
    // The plan, and a tool that asks the user something.
    [InlineData("todo", nameof(TimelineNodeKind.Plan))]
    [InlineData("ask_user", nameof(TimelineNodeKind.Notice))]
    // Everything else is a plain tool: git_branch, extension_* and unknown spec_* are NOT network
    // (only pr_/review_ are), and the empty name has no table row either.
    [InlineData("git_branch", nameof(TimelineNodeKind.Tool))]
    [InlineData("extension_create", nameof(TimelineNodeKind.Tool))]
    [InlineData("spec_whatever", nameof(TimelineNodeKind.Tool))]
    [InlineData("", nameof(TimelineNodeKind.Tool))]
    public void KindForTool_classifies_every_name_exactly(string name, string expectedKind) =>
        Assert.Equal(Enum.Parse<TimelineNodeKind>(expectedKind), TimelineNodeMap.KindForTool(name));

    // ---------------------------------------------------------------- StateOf

    [Fact]
    public void A_failure_wins_over_a_running_call()
    {
        foreach (var kind in AllKinds)
        {
            var expected = kind == TimelineNodeKind.Approval
                ? TimelineNodeState.Warning
                : TimelineNodeState.Failed;
            Assert.Equal(expected, TimelineNodeMap.StateOf(kind, running: true, failing: true));
        }
    }

    [Fact]
    public void A_denied_approval_is_a_warning_whether_or_not_it_is_still_running()
    {
        Assert.Equal(TimelineNodeState.Warning, TimelineNodeMap.StateOf(TimelineNodeKind.Approval, running: false, failing: true));
        Assert.Equal(TimelineNodeState.Warning, TimelineNodeMap.StateOf(TimelineNodeKind.Approval, running: true, failing: true));
    }

    [Fact]
    public void A_running_call_is_running_and_an_idle_one_is_idle()
    {
        foreach (var kind in AllKinds)
        {
            Assert.Equal(TimelineNodeState.Running, TimelineNodeMap.StateOf(kind, running: true, failing: false));
            Assert.Equal(TimelineNodeState.Idle, TimelineNodeMap.StateOf(kind, running: false, failing: false));
        }
    }

    // ---------------------------------------------------------------- EffectiveKind

    [Fact]
    public void A_failing_notice_wears_the_error_icon_and_an_idle_one_keeps_its_own()
    {
        Assert.Equal(TimelineNodeKind.Error, TimelineNodeMap.EffectiveKind(TimelineNodeKind.Notice, TimelineNodeState.Failed));
        Assert.Equal(TimelineNodeKind.Notice, TimelineNodeMap.EffectiveKind(TimelineNodeKind.Notice, TimelineNodeState.Idle));
    }

    [Fact]
    public void Every_other_kind_keeps_its_own_icon_in_every_state()
    {
        foreach (var kind in AllKinds)
        {
            if (kind == TimelineNodeKind.Notice)
            {
                continue;
            }

            Assert.Equal(kind, TimelineNodeMap.EffectiveKind(kind, TimelineNodeState.Idle));
            Assert.Equal(kind, TimelineNodeMap.EffectiveKind(kind, TimelineNodeState.Running));
            Assert.Equal(kind, TimelineNodeMap.EffectiveKind(kind, TimelineNodeState.Failed));
            Assert.Equal(kind, TimelineNodeMap.EffectiveKind(kind, TimelineNodeState.Warning));
        }
    }

    // ---------------------------------------------------------------- IconKey

    [Theory]
    [InlineData(nameof(TimelineNodeKind.User), "IconPerson", "IconPersonFilled")]
    [InlineData(nameof(TimelineNodeKind.Assistant), "IconChat", "IconChatFilled")]
    [InlineData(nameof(TimelineNodeKind.Turn), "IconChat", "IconChatFilled")]
    [InlineData(nameof(TimelineNodeKind.Thinking), "IconLightbulb", "IconLightbulbFilled")]
    [InlineData(nameof(TimelineNodeKind.ReadFile), "IconDocument", "IconDocumentFilled")]
    [InlineData(nameof(TimelineNodeKind.Search), "IconSearchRegular", "IconSearchFilled")]
    [InlineData(nameof(TimelineNodeKind.EditFile), "IconEditRegular", "IconEditFilled")]
    [InlineData(nameof(TimelineNodeKind.Command), "IconCode", "IconCodeFilled")]
    [InlineData(nameof(TimelineNodeKind.Network), "IconGlobeRegular", "IconGlobeFilled")]
    [InlineData(nameof(TimelineNodeKind.McpTool), "IconPlugRegular", "IconPlugFilled")]
    [InlineData(nameof(TimelineNodeKind.SubAgent), "IconBot", "IconBotFilled")]
    [InlineData(nameof(TimelineNodeKind.Plan), "IconTaskList", "IconTaskListFilled")]
    [InlineData(nameof(TimelineNodeKind.Notice), "IconInfo", "IconInfoFilled")]
    [InlineData(nameof(TimelineNodeKind.Error), "IconWarning", "IconWarningFilled")]
    [InlineData(nameof(TimelineNodeKind.Approval), "IconShieldCheckRegular", "IconShieldCheckFilled")]
    [InlineData(nameof(TimelineNodeKind.Tool), "IconWrenchRegular", "IconWrenchFilled")]
    public void Every_kind_wears_the_delivered_outline_and_filled_icon_key(
        string kindName, string outline, string filled)
    {
        var kind = Enum.Parse<TimelineNodeKind>(kindName);

        Assert.Equal(outline, TimelineNodeMap.IconKey(kind, filled: false));
        Assert.Equal(filled, TimelineNodeMap.IconKey(kind, filled: true));
    }

    [Fact]
    public void Outline_and_filled_keys_are_never_empty_and_never_the_same()
    {
        foreach (var kind in AllKinds)
        {
            var outline = TimelineNodeMap.IconKey(kind, filled: false);
            var filled = TimelineNodeMap.IconKey(kind, filled: true);

            Assert.NotEmpty(outline);
            Assert.NotEmpty(filled);
            Assert.NotEqual(outline, filled);
        }
    }

    // ---------------------------------------------------------------- NameKey / StateKey

    [Fact]
    public void Every_name_key_is_registered_in_both_languages_and_the_texts_differ()
    {
        foreach (var kind in AllKinds)
        {
            AssertRegistered(TimelineNodeMap.NameKey(kind));
        }
    }

    [Fact]
    public void Every_state_word_is_registered_in_both_languages_and_the_texts_differ()
    {
        AssertRegistered(TimelineNodeMap.StateKey(TimelineNodeState.Running));
        AssertRegistered(TimelineNodeMap.StateKey(TimelineNodeState.Failed));
        AssertRegistered(TimelineNodeMap.StateKey(TimelineNodeState.Warning));
    }

    [Fact]
    public void An_idle_node_has_no_state_word() =>
        Assert.Equal(string.Empty, TimelineNodeMap.StateKey(TimelineNodeState.Idle));

    /// <summary>A registered key resolves in both languages, is never the missing-key bracket form, and differs per language.</summary>
    private static void AssertRegistered(string key)
    {
        var chinese = Text(key, UiLanguage.Chinese);
        var english = Text(key, UiLanguage.English);

        Assert.NotEmpty(chinese);
        Assert.NotEmpty(english);
        Assert.NotEqual("[" + key + "]", chinese);
        Assert.NotEqual("[" + key + "]", english);
        Assert.NotEqual(chinese, english);
    }

    private static string Text(string key, UiLanguage language)
    {
        using var scope = new LanguageScope(language);
        return Loc.Instance[key];
    }
}
