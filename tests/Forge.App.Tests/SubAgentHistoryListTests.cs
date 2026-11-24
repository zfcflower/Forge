using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Forge.Abstractions;
using Forge.App.ViewModels;
using Forge.App.Views;
using Xunit;

namespace Forge.App.Tests;

/// <summary>
/// The finished menu of the bottom group answers three things at a glance: which rows were live tabs of this app run
/// (the live tint on their dot, not the colour of their recorded fate), which of them ended most recently (rows read
/// newest first), and who each row was - its name is the same word the live tab was titled by, so a row the user just
/// closed can be matched back to the tab it came from. A store-replayed row is none of the first kind: its dot keeps the
/// fate colour and it sinks below every run that ended after its own last activity.
/// </summary>
[Collection("forge-app-ui")]
public sealed class SubAgentHistoryListTests
{
    [Fact]
    public Task The_menu_reads_newest_first_and_a_lived_row_keeps_its_tab_name() => SharedUi.RunAsync(() =>
    {
        // A live tab that finished at 11:30 and was retired into the menu.
        var lived = Live("Worker-2", "Find the login regression.", Ended(11, 30));
        // A second one, finished later: the newest thing the menu has.
        var latest = Live("Worker-3", "Draft the fix.", Ended(12, 0));
        // A row replayed from the store: its own last activity is the oldest stamp in the list.
        var replayed = FromStored("Recon", "Check CI cache.", Ended(10, 0));

        var menu = new FinishedSubAgentsViewModel();
        // Bind receives the roster's own run-end order; the menu must not simply keep it.
        menu.Bind([replayed, lived, latest], static _ => { });

        Assert.Equal([latest, lived, replayed], menu.Items.ToArray());
        Assert.True(latest.WasLiveTab);
        Assert.False(replayed.WasLiveTab);
        // The name rule the live tab uses: the id Core issued it, ahead of the task it ran.
        Assert.Equal("Worker-2 · Find the login regression.", lived.HistoryName);
        // A replayed row has no live id to show; its kind names it.
        Assert.Equal("Recon · Check CI cache.", replayed.HistoryName);

        // The row renders as it does in the app: the flyout's own popup needs a top level that hosts popups, so the
        // menu content is hosted directly, the same way the row's hit-area test lays it out.
        var view = new FinishedSubAgentsHeaderView { DataContext = menu };
        var button = view.FindControl<Button>("MenuButton");
        Assert.NotNull(button);
        var flyout = Assert.IsType<Flyout>(button.Flyout);
        var window = new Window
        {
            DataContext = menu,
            Width = 400,
            Height = 240,
            Content = Assert.IsAssignableFrom<Control>(flyout.Content),
        };
        // The flyout's rows take their dot colours from ChromeStyles, which the view includes for itself; hosting the
        // content directly bypasses the view, so the window carries the same sheet the app's window does.
        window.Styles.Add(Assert.IsAssignableFrom<Styles>(
            AvaloniaXamlLoader.Load(new Uri("avares://Forge.App/Resources/ChromeStyles.axaml"))));
        window.Show();
        SharedUi.Settle(window);

        var rows = window.GetVisualDescendants()
            .OfType<Button>()
            .Where(b => b.Classes.Contains("finished-row"))
            .ToArray();
        Assert.Equal(3, rows.Length);

        // The newest row is the lived one that ended last, and its dot wears the shell's live tint - even though the
        // same dot also carries the completed-fate class, the live tint is the one the styles let win.
        Assert.Equal("Worker-3 · Draft the fix.", RowText(rows[0]).Text);
        Assert.Same(Brush(window, "TiffanyInkBrush"), RowDot(rows[0]).Fill);
        Assert.Same(Brush(window, "TiffanyInkBrush"), RowDot(rows[1]).Fill);

        // A replayed row keeps the colour of its recorded fate: completed, green - history, not a live tab that closed.
        Assert.Equal("Recon · Check CI cache.", RowText(rows[2]).Text);
        Assert.Same(Brush(window, "StatusSuccessBrush"), RowDot(rows[2]).Fill);

        // The live tint buries the fate colour, so a lived row's hover carries the fate's words under the task; a
        // replayed row's dot already says how it ended, so its hover is the task alone.
        Assert.Equal(latest.Task + "\n" + latest.StatusText, TipText(rows[0]));
        Assert.Equal(lived.Task + "\n" + lived.StatusText, TipText(rows[1]));
        Assert.Equal(replayed.Task, TipText(rows[2]));

        window.Close();
        return Task.CompletedTask;
    });

    [Fact]
    public Task A_row_whose_task_is_empty_is_its_name_alone()
        => SharedUi.RunAsync(() =>
        {
            var lived = Live("Worker-2", string.Empty, Ended(11, 30));
            Assert.Equal("Worker-2", lived.HistoryName);
            return Task.CompletedTask;
        });

    private static DateTimeOffset Ended(int hour, int minute) =>
        new(2026, 11, 25, hour, minute, 0, TimeSpan.Zero);

    private static SubAgentViewModel Live(string agentId, string task, DateTimeOffset finishedAt)
    {
        var agent = new SubAgentViewModel(
            sessionId: "session-1",
            agentId: agentId,
            childSessionId: agentId + "-session",
            kind: "Worker",
            task: task,
            model: "test-model",
            stopRequested: null,
            parentAgentId: "root");
        agent.Finish(SubAgentStatus.Completed, TimeSpan.FromMinutes(1), null, finishedAt);
        return agent;
    }

    private static SubAgentViewModel FromStored(string kind, string task, DateTimeOffset lastActive)
    {
        var row = new SessionInfo("child-2", "D:\\ws", task, lastActive)
        {
            ParentSessionId = "session-1",
            AgentKind = kind,
            Task = task,
            LastActiveAt = lastActive,
        };
        return SubAgentViewModel.FromStored("session-1", row, "root", SubAgentStatus.Completed);
    }

    private static Avalonia.Controls.TextBlock RowText(Button row) =>
        Assert.IsType<Avalonia.Controls.TextBlock>(Assert.Single(row.GetVisualDescendants().OfType<Avalonia.Controls.TextBlock>()));

    private static Avalonia.Controls.Shapes.Ellipse RowDot(Button row) =>
        Assert.Single(row.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Ellipse>());

    private static string TipText(Button row) =>
        Assert.IsType<string>(row.GetValue(ToolTip.TipProperty));

    /// <summary>The named brush of the window's current theme; a missing key fails here instead of comparing two nulls.</summary>
    private static Brush Brush(Window window, string key)
    {
        Assert.True(window.TryFindResource(key, window.ActualThemeVariant, out var value), "missing resource: " + key);
        return Assert.IsAssignableFrom<Brush>(value);
    }
}
