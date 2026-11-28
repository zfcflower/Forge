using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Forge.Abstractions;
using Forge.App.Localization;
using Forge.App.ViewModels;
using Forge.App.Views;
using TextBlock = Avalonia.Controls.TextBlock;
using Xunit;

namespace Forge.App.Tests;

/// <summary>
/// The session's managed context as a timeline, headless: one node per turn with 「第 N 轮 — 用户输入」 at it and that turn's
/// card under it, read a page at a time — the newest page on open, older ones as the reader reaches the top, each landing in
/// front of what is already there. That window is the ledger's only surface.
/// </summary>
[Collection("forge-app-ui")]
public sealed class TurnLedgerWindowTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 26, 8, 0, 0, TimeSpan.FromHours(8));

    private static TurnLedgerEntry Entry(
        long turn,
        string task = "the task text",
        string conclusion = "the parsed conclusion",
        TurnLedgerStatus status = TurnLedgerStatus.Settled) => new(
        "s1",
        turn,
        status,
        task,
        "the answer as shown",
        conclusion,
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

    [Fact]
    public void A_row_is_the_round_the_question_and_the_card_the_ledger_recorded()
    {
        var row = new TurnLedgerRowViewModel(Entry(2, "fix the parser\nThe second line is not the header's business."));

        Assert.Equal(SessionStrings.UserTurnRound(2) + " — fix the parser", row.Header);
        // The whole question stays reachable even though the header shows one line.
        Assert.Contains("The second line", row.TaskText, StringComparison.Ordinal);

        // The card: the conclusion under its label, then the evidence as bullets — the files it read and changed, the specs
        // it touched and the message range the model can read back.
        Assert.Contains("**" + SessionStrings.SessionLedgerConclusion + "**", row.Markdown, StringComparison.Ordinal);
        Assert.Contains("the parsed conclusion", row.Markdown, StringComparison.Ordinal);
        Assert.Contains("**" + SessionStrings.SessionLedgerEvidence + "**", row.Markdown, StringComparison.Ordinal);
        Assert.Contains(
            "- " + SessionStrings.SessionLedgerRead + ": `src/Forge.Core/Agents/ManagedContext.cs`",
            row.Markdown,
            StringComparison.Ordinal);
        Assert.Contains(
            "- " + SessionStrings.SessionLedgerWrote + ": `src/Forge.Core/Agents/AgentLoop.cs`",
            row.Markdown,
            StringComparison.Ordinal);
        Assert.Contains("- " + SessionStrings.SessionLedgerSpecs + ": `forge-core`", row.Markdown, StringComparison.Ordinal);
        Assert.Contains("seq 4-12", row.Markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void A_turn_with_no_conclusion_yet_says_so_instead_of_showing_an_empty_card()
    {
        var row = new TurnLedgerRowViewModel(Entry(1, conclusion: "**Conclusion**:"));

        // The echoed label is stripped, so what is left is nothing to show - and the card says that rather than leaving a
        // blank paragraph under the label.
        Assert.Contains(SessionStrings.SessionLedgerNoConclusion, row.Markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("**Conclusion**", row.Markdown, StringComparison.Ordinal);
    }

    [Fact]
    public Task The_timeline_draws_one_node_and_one_card_per_turn() => SharedUi.RunAsync(async () =>
    {
        var rows = new[] { Entry(1), Entry(2), Entry(3) };
        var model = new TurnLedgerWindowViewModel((_, _, _) => Task.FromResult(new TurnLedgerPage(rows, false, 3)));
        var window = new TurnLedgerWindow(model) { Width = 760, Height = 640 };
        window.Show();
        await model.LoadOlderAsync(TestContext.Current.CancellationToken);
        SharedUi.Settle(window);

        // One node per turn, one card per turn: the timeline, not a table of rows.
        var nodes = window.GetVisualDescendants().OfType<TimelineGutter>().Count();
        Assert.Equal(3, nodes);
        Assert.Equal(3, window.GetVisualDescendants().OfType<MarkdownTextView>().Count());

        var footer = Assert.Single(
            window.GetVisualDescendants().OfType<TextBlock>(),
            block => AutomationProperties.GetAutomationId(block) == "session-ledger-footer");
        Assert.Equal(SessionStrings.SessionLedgerFooter(3).Resolve(), footer.Text);
        window.Close();
    });

    [Fact]
    public async Task An_older_page_lands_in_front_of_the_timeline_and_moves_the_cursor()
    {
        var pages = new Queue<TurnLedgerPage>([
            new TurnLedgerPage([Entry(4), Entry(5)], true, 5),
            new TurnLedgerPage([Entry(1), Entry(2), Entry(3)], false, 5),
        ]);
        var asked = new List<long>();
        var model = new TurnLedgerWindowViewModel((before, _, _) =>
        {
            asked.Add(before);
            return Task.FromResult(pages.Count > 0 ? pages.Dequeue() : new TurnLedgerPage([], false, 5));
        });

        await model.LoadOlderAsync(TestContext.Current.CancellationToken);
        Assert.Equal([4L, 5L], model.Rows.Select(row => row.TurnSequence));

        await model.LoadOlderAsync(TestContext.Current.CancellationToken);

        // The second read starts below the oldest turn already on screen, and the page joins in front of it: the list is one
        // timeline, oldest first.
        Assert.Equal([long.MaxValue, 4L], asked);
        Assert.Equal([1L, 2L, 3L, 4L, 5L], model.Rows.Select(row => row.TurnSequence));
        Assert.False(model.HasOlder);

        // Nothing older is left, so a third ask reads nothing at all.
        await model.LoadOlderAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, asked.Count);
    }

    [Fact]
    public Task An_empty_session_says_so_instead_of_showing_an_empty_timeline() => SharedUi.RunAsync(async () =>
    {
        var model = new TurnLedgerWindowViewModel(static (_, _, _) => Task.FromResult(new TurnLedgerPage([], false, 0)));
        var window = new TurnLedgerWindow(model) { Width = 760, Height = 640 };
        window.Show();
        await model.LoadOlderAsync(TestContext.Current.CancellationToken);
        SharedUi.Settle(window);

        Assert.True(model.IsEmpty);
        var empty = Assert.Single(
            window.GetVisualDescendants().OfType<TextBlock>(),
            block => AutomationProperties.GetAutomationId(block) == "session-ledger-empty");
        Assert.Equal(SessionStrings.SessionLedgerEmpty.Resolve(), empty.Text);
        // The footer counts turns and has nothing to say about a session that recorded none.
        Assert.False(model.ShowsFooter);
        var footer = Assert.Single(
            window.GetVisualDescendants().OfType<TextBlock>(),
            block => AutomationProperties.GetAutomationId(block) == "session-ledger-footer");
        Assert.False(footer.IsEffectivelyVisible);
        window.Close();
    });
}
