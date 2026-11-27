using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Forge.Abstractions;
using Forge.App.ViewModels;
using Forge.App.Views;
using TextBlock = Avalonia.Controls.TextBlock;
using Xunit;

namespace Forge.App.Tests;

/// <summary>
/// The two ledger observation windows, headless: the reply card's turn entry button exists only for the rounds that
/// have a row behind it (a card without one shows no button, not a disabled one), its flyout shows the row's own
/// fields, and the session dialog lists the rows in turn order.
/// </summary>
[Collection("forge-app-ui")]
public sealed class TurnLedgerWindowTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 26, 8, 0, 0, TimeSpan.FromHours(8));

    private static TurnLedgerEntry Entry(long turn) => new(
        "s1",
        turn,
        TurnLedgerStatus.Settled,
        "the task text",
        "the answer as shown",
        "the parsed conclusion",
        ["src/Forge.Core/Agents/ManagedContext.cs"],
        ["src/Forge.Core/Agents/AgentLoop.cs"],
        ["forge-core"],
        4,
        12,
        12,
        "anthropic",
        "claude-sonnet",
        "Root",
        0,
        false,
        At,
        At.AddMinutes(1));

    private static TurnTextViewModel ReplyOf(TurnCardViewModel card)
    {
        card.AppendText("the reply");
        return Assert.IsType<TurnTextViewModel>(Assert.Single(card.Blocks.OfType<TurnTextViewModel>()));
    }

    private static (Window Window, TurnTextView View) ShowReply(TurnTextViewModel text)
    {
        var view = new TurnTextView { DataContext = text };
        var window = new Window { Content = view, Width = 640, Height = 480 };
        window.Show();
        SharedUi.Settle(window);
        return (window, view);
    }

    private static IReadOnlyList<Button> ButtonsOf(TurnTextView view) =>
        view.GetVisualDescendants().OfType<Button>().ToList();

    [Fact]
    public Task A_round_without_a_row_shows_no_turn_entry_button() => SharedUi.RunAsync(() =>
    {
        var transcript = new TranscriptViewModel(collapseOlderTurns: false);
        transcript.AddUserMessage("task", At);
        var card = transcript.BeginTurn(At);
        var text = ReplyOf(card);

        var (window, view) = ShowReply(text);

        Assert.Contains(ButtonsOf(view), button => AutomationProperties.GetAutomationId(button) == "message-copy");
        // No row, no button: the turn entry is invisible, not a disabled control the user can point at.
        Assert.DoesNotContain(
            ButtonsOf(view),
            button => AutomationProperties.GetAutomationId(button) == "turn-ledger" && button.IsVisible);
        window.Close();
        return Task.CompletedTask;
    });

    [Fact]
    public Task A_round_with_a_row_shows_the_button_and_its_flyout_reads_the_row() => SharedUi.RunAsync(() =>
    {
        var transcript = new TranscriptViewModel(collapseOlderTurns: false);
        transcript.AddUserMessage("task", At);
        var card = transcript.BeginTurn(At);
        var text = ReplyOf(card);
        card.SetLedger(Entry(1));

        var (window, view) = ShowReply(text);

        var button = Assert.Single(ButtonsOf(view), button => AutomationProperties.GetAutomationId(button) == "turn-ledger");
        Assert.True(button.IsVisible);
        var flyout = Assert.IsType<Flyout>(button.Flyout);
        flyout.ShowAt(button);
        SharedUi.Settle(window);

        // The flyout's content lives in the window's overlay layer, not under the view that opened it.
        var lines = window.GetVisualDescendants()
            .OfType<StackPanel>()
            .Single(panel => AutomationProperties.GetAutomationId(panel) == "turn-ledger-panel")
            .GetVisualDescendants()
            .OfType<TextBlock>()
            .Select(block => block.Text ?? string.Empty)
            .ToList();
        Assert.Equal("Turn 1", lines[0]);
        Assert.Equal("Task", lines[1]);
        Assert.Equal("the task text", lines[2]);
        Assert.Contains("Conclusion", lines);
        Assert.Contains("the parsed conclusion", lines);
        Assert.Contains("Evidence", lines);
        // A reply is free-form: the window shows no state section and no verdict, only the answer and the evidence.
        Assert.DoesNotContain("Status", lines);
        Assert.DoesNotContain(lines, line => line.Contains("unresolved", StringComparison.Ordinal));
        // The evidence section is one text block, so its lines are read inside it.
        Assert.Contains(lines, line => line.Contains("read: src/Forge.Core/Agents/ManagedContext.cs", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("wrote: src/Forge.Core/Agents/AgentLoop.cs", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("spec: forge-core", StringComparison.Ordinal));
        // The evidence pin is `seq:` — the same word the model reads and the same one `read_history` takes.
        Assert.Contains(lines, line => line.Contains("turn: 1", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("seq: 4-12", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, line => line.Contains("events:", StringComparison.Ordinal));
        window.Close();
        return Task.CompletedTask;
    });

    [Fact]
    public Task The_session_dialog_lists_the_rows_in_turn_order() => SharedUi.RunAsync(() =>
    {
        var rows = new[] { Entry(1), Entry(2), Entry(3) };
        var window = new TurnLedgerWindow(new TurnLedgerWindowViewModel(rows)) { Width = 640, Height = 480 };
        window.Show();
        SharedUi.Settle(window);

        // One header per row, in the store's order, and nothing else: a row records a turn, it does not grade a reply.
        var headers = window.GetVisualDescendants()
            .OfType<TextBlock>()
            .Select(block => block.Text ?? string.Empty)
            .Where(text => text.StartsWith("Turn ", StringComparison.Ordinal))
            .ToList();
        Assert.Equal(["Turn 1", "Turn 2", "Turn 3"], headers);

        var footer = Assert.Single(
            window.GetVisualDescendants().OfType<TextBlock>(),
            block => AutomationProperties.GetAutomationId(block) == "session-ledger-footer");
        Assert.Contains("3", footer.Text ?? string.Empty, StringComparison.Ordinal);
        window.Close();
        return Task.CompletedTask;
    });
}
