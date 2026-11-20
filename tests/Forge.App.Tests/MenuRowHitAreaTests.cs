using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Forge.App.ViewModels;
using Forge.App.Views;
using Xunit;

namespace Forge.App.Tests;

/// <summary>
/// An option row of a flyout menu is one full-width rectangle: the blank end of a short label belongs to the row, so the
/// pointer lights it and a click there picks the option. A button left-aligns to its content unless it says otherwise, which
/// left that blank end outside the row; the bottom group's finished-agent menu and the change-scope / base-branch pickers are
/// the two menu families in the app whose rows are buttons, and nothing inside either flyout notices when it regresses.
/// </summary>
[Collection("forge-app-ui")]
public sealed class MenuRowHitAreaTests
{
    [Fact]
    public Task A_finished_agent_row_spans_the_width_of_its_menu() => SharedUi.RunAsync(() =>
    {
        var menu = new FinishedSubAgentsViewModel();
        menu.Bind([FinishedAgent()], static _ => { });
        var view = new FinishedSubAgentsHeaderView { DataContext = menu };
        var button = view.FindControl<Button>("MenuButton");
        Assert.NotNull(button);
        var flyout = Assert.IsType<Flyout>(button.Flyout);

        // The flyout's own popup needs a top level that hosts popups; hosting the menu content directly lays the rows out the
        // same way and keeps the assertion on the one relationship that matters. The content is bound to the menu the button's
        // own DataContext is, so the item template renders as it does in the app.
        var window = new Window
        {
            DataContext = menu,
            Width = 400,
            Height = 240,
            Content = Assert.IsAssignableFrom<Control>(flyout.Content),
        };
        window.Show();
        SharedUi.Settle(window);

        var list = Assert.Single(window.GetVisualDescendants().OfType<ItemsControl>());
        var row = Assert.Single(list.GetVisualDescendants().OfType<Button>(), static b => b.Classes.Contains("finished-row"));

        // The menu is capped at 320 and the window is wider, so a row that spans the menu is the whole hit area.
        Assert.Equal(320, list.Bounds.Width);
        Assert.Equal(list.Bounds.Width, row.Bounds.Width);

        window.Close();
        return Task.CompletedTask;
    });

    [Fact]
    public Task A_change_scope_row_spans_the_width_of_its_menu() => SharedUi.RunAsync(() =>
    {
        // The pickers' rows are the shared `change-menu-row` style, which the two flyouts that use it include by URL.
        var styles = Assert.IsAssignableFrom<Styles>(
            AvaloniaXamlLoader.Load(new Uri("avares://Forge.App/Resources/ChangeMenuStyles.axaml")));
        var row = new Button { Classes = { "change-menu-row" }, Content = "全部改动" };
        var window = new Window
        {
            Width = 360,
            Height = 120,
            Content = new StackPanel { Children = { row } },
        };
        window.Styles.Add(styles);
        window.Show();
        SharedUi.Settle(window);

        // The stack offers the whole client width; a row that spans it is the whole hit area.
        Assert.Equal(window.ClientSize.Width, row.Bounds.Width);

        window.Close();
        return Task.CompletedTask;
    });

    private static SubAgentViewModel FinishedAgent() => new(
        sessionId: "session-1",
        agentId: "agent-1",
        childSessionId: "child-session-1",
        kind: "Worker",
        task: "Say hi.",
        model: "test-model",
        stopRequested: null,
        parentAgentId: "root");
}
