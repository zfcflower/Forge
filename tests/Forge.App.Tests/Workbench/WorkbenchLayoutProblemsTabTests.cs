using System.Text.Json;
using Forge.App.Localization;
using Forge.App.Workbench;
using Xunit;

namespace Forge.App.Tests.Workbench;

/// <summary>
/// Pins the optional problems tab of <see cref="WorkbenchLayout"/>: with a problems view model the inspector tools group holds
/// four tabs and its heading names four; without one (a layout test, a host with no editor layer) it holds three and names
/// three. A layout saved by a host that had the tab loads into one that has not, and a layout saved before the tab existed
/// still loads into one that has it, with the tab appended after the saved order. Pure model tests: no window.
/// </summary>
public sealed class WorkbenchLayoutProblemsTabTests
{
    private const double WideWindowWidth = 2000;

    [Fact]
    public void Without_the_problems_parameter_the_group_has_three_tabs_and_names_three()
    {
        var layout = Build(withProblems: false);

        Assert.Equal(
            [WorkbenchIds.TodoTab, WorkbenchIds.ReviewTab, WorkbenchIds.SpecsTab],
            layout.InspectorTools.Tabs.Select(static tab => tab.Id).ToArray());
        Assert.Equal(
            ShellStrings.TodoTitle + " | " + ShellStrings.ReviewTitle + " | " + ShellStrings.SpecTreeTitle,
            layout.InspectorTools.Title);
        Assert.Equal(3, layout.InspectorTools.Title.Split(" | ").Length);
        Assert.DoesNotContain(ShellStrings.ProblemsTitle, layout.InspectorTools.Title, StringComparison.Ordinal);
        Assert.Null(layout.FindTab(WorkbenchIds.ProblemsTab));
    }

    [Fact]
    public void With_the_problems_parameter_the_group_has_four_tabs_with_problems_last_and_names_four()
    {
        var problems = new object();
        var layout = Build(withProblems: true, problems);

        Assert.Equal(
            [WorkbenchIds.TodoTab, WorkbenchIds.ReviewTab, WorkbenchIds.SpecsTab, WorkbenchIds.ProblemsTab],
            layout.InspectorTools.Tabs.Select(static tab => tab.Id).ToArray());
        var tab = layout.InspectorTools.Tabs[3];
        Assert.Equal(TabKind.Problems, tab.Kind);
        Assert.Same(problems, tab.Content);
        Assert.Equal(ShellStrings.ProblemsTitle, tab.Title);
        Assert.False(tab.IsClosable);
        Assert.Equal(4, layout.InspectorTools.Title.Split(" | ").Length);
        Assert.EndsWith(" | " + ShellStrings.ProblemsTitle, layout.InspectorTools.Title, StringComparison.Ordinal);
        // The first tab stays the active one, as before the problems tab existed.
        Assert.Equal(WorkbenchIds.TodoTab, layout.InspectorTools.ActiveTab?.Id);
    }

    [Fact]
    public void Capture_lists_the_problems_tab_only_when_the_host_has_it()
    {
        Assert.Equal(
            ["todo", "review", "specs", "problems"],
            Build(withProblems: true).Capture().ToolsOrder);
        Assert.Equal(
            ["todo", "review", "specs"],
            Build(withProblems: false).Capture().ToolsOrder);
    }

    [Fact]
    public void A_saved_order_with_problems_loads_into_a_host_without_the_tab_and_ignores_it()
    {
        var snapshot = RoundTrip(new LayoutSnapshot
        {
            Version = LayoutSnapshot.CurrentVersion,
            ToolsOrder = [WorkbenchIds.ProblemsTab, WorkbenchIds.SpecsTab, WorkbenchIds.TodoTab, WorkbenchIds.ReviewTab],
            ToolsActive = WorkbenchIds.ProblemsTab,
            Floating = [new FloatingSnapshot { Id = WorkbenchIds.ProblemsTab, X = 10, Y = 10, Width = 400, Height = 300 }],
        });
        var layout = Build(withProblems: false);

        layout.Apply(snapshot);
        layout.ApplyFloating(snapshot);

        // The tab is not in this host: the rest keep the saved relative order, nothing is invented, nothing floats.
        Assert.Equal(
            [WorkbenchIds.SpecsTab, WorkbenchIds.TodoTab, WorkbenchIds.ReviewTab],
            layout.InspectorTools.Tabs.Select(static tab => tab.Id).ToArray());
        Assert.NotNull(layout.InspectorTools.ActiveTab);
        Assert.NotEqual(WorkbenchIds.ProblemsTab, layout.InspectorTools.ActiveTab!.Id);
        Assert.Empty(layout.Floating);
        Assert.DoesNotContain(ShellStrings.ProblemsTitle, layout.InspectorTools.Title, StringComparison.Ordinal);
    }

    [Fact]
    public void An_old_saved_layout_without_problems_loads_and_the_tab_is_appended_after_the_saved_order()
    {
        var snapshot = RoundTrip(new LayoutSnapshot
        {
            Version = LayoutSnapshot.CurrentVersion,
            ToolsOrder = [WorkbenchIds.SpecsTab, WorkbenchIds.TodoTab, WorkbenchIds.ReviewTab],
            ToolsActive = WorkbenchIds.ReviewTab,
        });
        var layout = Build(withProblems: true);

        layout.Apply(snapshot);

        Assert.Equal(
            [WorkbenchIds.SpecsTab, WorkbenchIds.TodoTab, WorkbenchIds.ReviewTab, WorkbenchIds.ProblemsTab],
            layout.InspectorTools.Tabs.Select(static tab => tab.Id).ToArray());
        Assert.Equal(WorkbenchIds.ReviewTab, layout.InspectorTools.ActiveTab?.Id);
        Assert.Equal(4, layout.InspectorTools.Title.Split(" | ").Length);
    }

    [Fact]
    public void An_old_saved_layout_with_no_tool_order_at_all_keeps_the_default_order_including_problems()
    {
        var layout = Build(withProblems: true);

        layout.Apply(RoundTrip(new LayoutSnapshot { Version = LayoutSnapshot.CurrentVersion }));

        Assert.Equal(
            [WorkbenchIds.TodoTab, WorkbenchIds.ReviewTab, WorkbenchIds.SpecsTab, WorkbenchIds.ProblemsTab],
            layout.InspectorTools.Tabs.Select(static tab => tab.Id).ToArray());
    }

    [Fact]
    public void A_layout_saved_with_problems_active_restores_it_in_a_host_that_has_the_tab()
    {
        var source = Build(withProblems: true);
        source.ActivateTab(source.InspectorTools.Tabs[3]);
        var snapshot = RoundTrip(source.Capture());
        var restored = Build(withProblems: true);

        restored.Apply(snapshot);

        Assert.Equal(WorkbenchIds.ProblemsTab, restored.InspectorTools.ActiveTab?.Id);
        Assert.Equal(source.InspectorTools.Tabs.Select(static tab => tab.Id), restored.InspectorTools.Tabs.Select(static tab => tab.Id));
    }

    private static WorkbenchLayout Build(bool withProblems, object? problems = null)
    {
        var layout = withProblems
            ? new WorkbenchLayout("project", "files", "todo", "review", "specs", problems: problems ?? "problems")
            : new WorkbenchLayout("project", "files", "todo", "review", "specs");
        layout.SetWindowWidth(WideWindowWidth);
        return layout;
    }

    private static LayoutSnapshot RoundTrip(LayoutSnapshot snapshot)
    {
        var json = JsonSerializer.Serialize(snapshot, WorkbenchJsonContext.Default.LayoutSnapshot);
        return JsonSerializer.Deserialize(json, WorkbenchJsonContext.Default.LayoutSnapshot)
            ?? throw new InvalidOperationException("layout snapshot did not deserialize");
    }
}
