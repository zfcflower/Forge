using Forge.Abstractions;
using Forge.App.Localization;
using Forge.App.Preferences;
using Forge.App.Tests.Localization;
using Forge.App.ViewModels;
using Xunit;

namespace Forge.App.Tests.Workbench;

/// <summary>
/// Pins the session breadcrumb (forge-app spec "L3 breadcrumbs"): the main session reads project/workspace/branch from the
/// session and the registry-backed instruments, the project/workspace segments focus the matching tree row through the
/// shell's callback, and a sub-agent breadcrumb carries its parent title plus a "back to parent" action.
/// </summary>
public sealed class SessionBreadcrumbTests
{
    private static readonly DateTimeOffset At = new(2025, 6, 1, 12, 0, 0, TimeSpan.Zero);

    private static SessionWorkspaceViewModel OpenMain(string branch, Action<string> focusTree)
    {
        var info = new SessionInfo("s1", "C:\\repo\\.worktrees\\slug", "Fix the build", At)
        {
            ProjectId = "p1",
            WorktreePath = "C:\\repo\\.worktrees\\slug",
            Branch = branch,
        };
        return new SessionWorkspaceViewModel(
            info,
            runtime: null,
            checkpoints: null,
            store: null,
            TestProviderServices.UnconfiguredProbe(),
            focusTree: focusTree);
    }

    [Fact]
    public void Main_breadcrumb_reads_project_workspace_and_branch_and_navigates_the_tree()
    {
        using var language = new LanguageScope(UiLanguage.English);
        var keys = new List<string>();
        using var workspace = OpenMain("feature/x", keys.Add);
        workspace.Instruments.SetProject("forge", "C:\\repo");

        Assert.True(workspace.HasBreadcrumbProject);
        Assert.Equal("forge", workspace.BreadcrumbProject);
        Assert.Contains("slug", workspace.BreadcrumbWorkspace, StringComparison.Ordinal);
        Assert.Contains("feature/x", workspace.BreadcrumbWorkspace, StringComparison.Ordinal);

        workspace.FocusProjectCommand.Execute(null);
        workspace.FocusWorkspaceCommand.Execute(null);

        Assert.Equal(["project:p1", "worktree:p1|C:\\repo\\.worktrees\\slug"], keys);
    }

    [Fact]
    public void Main_breadcrumb_hides_the_project_segment_for_the_general_workspace()
    {
        using var language = new LanguageScope(UiLanguage.English);
        var info = new SessionInfo("g1", "C:\\general\\scratch", "General task", At)
        {
            ProjectId = "general-project",
        };
        using var workspace = new SessionWorkspaceViewModel(
            info,
            runtime: null,
            checkpoints: null,
            store: null,
            TestProviderServices.UnconfiguredProbe());
        workspace.Instruments.SetProject("General", "C:\\general", isGeneral: true);

        Assert.False(workspace.HasBreadcrumbProject);
        Assert.Equal("scratch", workspace.BreadcrumbWorkspace);
    }

    [Fact]
    public void Sub_agent_breadcrumb_carries_the_parent_title_and_switches_back()
    {
        var back = 0;
        var agent = new SubAgentViewModel(
            "parent-session",
            "agent-1",
            "child-session",
            "Explore",
            "review the diff",
            "model",
            stopRequested: null,
            parentTitle: "Parent title",
            backToParent: () => back++);

        Assert.Equal("Parent title", agent.ParentSessionTitle);
        Assert.Equal("Explore", agent.BreadcrumbName);
        Assert.True(agent.HasBackToParent);

        agent.BackToParentCommand.Execute(null);
        Assert.Equal(1, back);
    }

    [Fact]
    public void Breadcrumb_workspace_reraises_when_the_language_changes()
    {
        using var language = new LanguageScope(UiLanguage.English);
        using var workspace = OpenMain("feature/x", _ => { });
        var before = workspace.BreadcrumbWorkspace;
        var raised = false;
        workspace.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(SessionWorkspaceViewModel.BreadcrumbWorkspace))
            {
                raised = true;
            }
        };

        Loc.Instance.Language = UiLanguage.Chinese;

        Assert.True(raised);
        Assert.NotEqual(before, workspace.BreadcrumbWorkspace);
    }
}
