using Forge.Abstractions;
using Forge.App.ViewModels;
using Xunit;

namespace Forge.App.Tests;

/// <summary>
/// An open tab against the row the store holds for it. The role is the row's parent link and nothing else, and it cannot be
/// half-old: a promoted session whose tab is still an agent view runs a Depth 1 instance (no extension tools, a kind's
/// whitelist) while its row says it is the branch's root, and a demoted root whose tab is still a root view runs the whole
/// root surface while its row says it is an agent session. Every case here is a shape a real path (promote, archive,
/// handover, restore, start-up restore) leaves behind.
/// </summary>
public sealed class SessionRoleRulesTests
{
    private static SessionInfo Row(string id, string? parentId = null, SessionStatus status = SessionStatus.Active) =>
        new(id, "/repo/worktree-a", id, DateTimeOffset.UnixEpoch)
        {
            ParentSessionId = parentId,
            ChildDriver = parentId is null ? null : "user",
            Status = status,
        };

    [Fact]
    public void A_root_tab_showing_the_root_row_keeps_its_view() =>
        Assert.Equal(
            SessionRoleAction.Keep,
            SessionRoleRules.ActionFor(Row("root"), viewIsAgentSession: false, viewIsReadOnly: false, viewRootId: null));

    [Fact]
    public void A_promoted_sessions_agent_tab_is_rebuilt_for_the_root_row() =>
        Assert.Equal(
            SessionRoleAction.Rebuild,
            SessionRoleRules.ActionFor(Row("promoted"), viewIsAgentSession: true, viewIsReadOnly: false, viewRootId: "old-root"));

    [Fact]
    public void A_demoted_roots_root_tab_is_rebuilt_for_the_agent_row() =>
        Assert.Equal(
            SessionRoleAction.Rebuild,
            SessionRoleRules.ActionFor(Row("demoted", "new-root"), viewIsAgentSession: false, viewIsReadOnly: false, viewRootId: null));

    [Fact]
    public void An_agent_tab_under_the_root_its_row_names_keeps_its_view() =>
        Assert.Equal(
            SessionRoleAction.Keep,
            SessionRoleRules.ActionFor(Row("child", "root"), viewIsAgentSession: true, viewIsReadOnly: false, viewRootId: "root"));

    [Fact]
    public void An_agent_tab_still_hanging_under_the_old_root_is_rebuilt_not_merely_reused() =>
        Assert.Equal(
            SessionRoleAction.Rebuild,
            SessionRoleRules.ActionFor(Row("child", "new-root"), viewIsAgentSession: true, viewIsReadOnly: false, viewRootId: "old-root"));

    [Fact]
    public void A_live_view_of_a_row_that_left_Active_is_turned_read_only() =>
        Assert.Equal(
            SessionRoleAction.ReadOnly,
            SessionRoleRules.ActionFor(Row("root", status: SessionStatus.Archived), viewIsAgentSession: false, viewIsReadOnly: false, viewRootId: null));

    [Fact]
    public void A_history_view_keeps_its_view_when_its_role_moved_behind_its_back() =>
        Assert.Equal(
            SessionRoleAction.Keep,
            SessionRoleRules.ActionFor(Row("old-root", "new-root", SessionStatus.Archived), viewIsAgentSession: false, viewIsReadOnly: true, viewRootId: null));

    [Fact]
    public void A_history_view_of_a_row_that_is_Active_now_is_rebuilt() =>
        Assert.Equal(
            SessionRoleAction.Rebuild,
            // The restore case the shell runs into: a child of an archived root, opened read-only, whose row is Active again
            // once the store moved it under the branch's active root. Re-homing alone would leave it read-only for good.
            SessionRoleRules.ActionFor(Row("restored", "root"), viewIsAgentSession: true, viewIsReadOnly: true, viewRootId: "root"));
}
