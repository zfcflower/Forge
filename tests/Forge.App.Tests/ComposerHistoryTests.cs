using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Forge.Abstractions;
using Forge.App.Agents;
using Forge.App.Localization;
using Forge.App.Providers;
using Forge.App.Services;
using Forge.App.ViewModels;
using Forge.App.Views;
using Forge.Core.Storage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Forge.App.Tests;

/// <summary>
/// The composer's Chat History: every message of the session in one list, the user's own messages takeable back into the
/// composer, the agent's readable but never a draft, and each row's age relative to now. The list is rebuilt from the
/// transcript, so what the flyout shows is what the transcript holds.
/// </summary>
[Collection("forge-app-ui")]
public sealed class ComposerHistoryTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "forge-composer-history-" + Guid.NewGuid().ToString("N"));
    private readonly HttpClient http = new();

    public void Dispose()
    {
        http.Dispose();
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
            // A pooled handle can outlive the test; the temp directory is the OS's problem then.
        }
    }

    [Fact]
    public void A_block_holds_the_user_message_and_the_answer_it_got()
    {
        var at = new DateTimeOffset(2026, 1, 2, 9, 30, 0, TimeSpan.Zero);
        var transcript = new TranscriptViewModel(collapseOlderTurns: false);
        transcript.AddUserMessage("first question", at);
        var card = transcript.BeginTurn(at);
        card.AppendText("the answer");
        card.AppendText(" and more of it");
        transcript.AddUserMessage("second question", at);
        transcript.BeginTurn(at);

        var history = new ComposerHistoryViewModel(() => transcript);
        history.Refresh();

        // One block per round the user opened: the message on top, every reply block of the turn below it joined into one
        // answer; the round that got no answer still has its message.
        Assert.Equal(["first question", "second question"], history.Entries.Select(static entry => entry.Text));
        Assert.Equal("the answer and more of it", history.Entries[0].Reply);
        Assert.True(history.Entries[0].HasReply);
        Assert.Empty(history.Entries[1].Reply);
        Assert.False(history.Entries[1].HasReply);
        Assert.Equal([0, 1], history.Entries.Select(static entry => entry.RoundIndex));
        Assert.False(history.IsEmpty);
        Assert.False(history.HasNoMatch);
    }

    [Fact]
    public void A_row_flattens_the_message_and_the_answer_to_one_line_but_fills_the_message_as_written()
    {
        var at = new DateTimeOffset(2026, 1, 2, 9, 30, 0, TimeSpan.Zero);
        var transcript = new TranscriptViewModel(collapseOlderTurns: false);
        transcript.AddUserMessage("  first\n\tquestion   again ", at);
        var card = transcript.BeginTurn(at);
        card.AppendText("the\nanswer and   its\n second part");

        var history = new ComposerHistoryViewModel(() => transcript);
        history.Refresh();

        var block = Assert.Single(history.Entries);
        Assert.Equal("  first\n\tquestion   again ", block.Text);
        Assert.Equal("first question again", block.DisplayText);
        Assert.Equal("the answer and its second part", block.Reply);
    }

    [Fact]
    public void A_filter_narrows_the_list_and_distinguishes_no_match_from_no_message()
    {
        var transcript = new TranscriptViewModel(collapseOlderTurns: false);
        transcript.AddUserMessage("fix the parser", DateTimeOffset.UnixEpoch);
        transcript.AddUserMessage("write the docs", DateTimeOffset.UnixEpoch);

        var history = new ComposerHistoryViewModel(() => transcript);
        history.Refresh();
        Assert.Equal(2, history.Entries.Count);

        history.Search = "PARSE";
        var match = Assert.Single(history.Entries);
        Assert.Equal("fix the parser", match.Text);
        Assert.False(history.HasNoMatch);

        // The answer is searchable too: a word only the reply carries finds the block that holds it.
        var transcript2 = new TranscriptViewModel(collapseOlderTurns: false);
        transcript2.AddUserMessage("what broke", DateTimeOffset.UnixEpoch);
        transcript2.BeginTurn(DateTimeOffset.UnixEpoch).AppendText("the lexer did");
        var answers = new ComposerHistoryViewModel(() => transcript2);
        answers.Refresh();
        answers.Search = "LEXER";
        Assert.Equal("what broke", Assert.Single(answers.Entries).Text);

        history.Search = "nothing like it";
        Assert.Empty(history.Entries);
        Assert.True(history.HasNoMatch);
        Assert.False(history.IsEmpty);

        // A session that never held a message is the other empty state, and no filter turns it into "no match".
        var empty = new ComposerHistoryViewModel(() => null);
        empty.Refresh();
        Assert.True(empty.IsEmpty);
        Assert.False(empty.HasNoMatch);
    }

    [Fact]
    public void A_row_ages_relative_to_now_and_reads_as_a_clock_once_it_is_older_than_an_hour()
    {
        // Local-time stamps, because the row shows local time: a UTC one would be shifted by this machine's offset.
        var now = new DateTimeOffset(new DateTime(2026, 1, 2, 18, 0, 0, DateTimeKind.Local));

        Assert.Equal(Loc.Instance[ComposerHistoryStrings.JustNowKey], ComposerHistoryStrings.Relative(now.AddSeconds(-20), now));
        Assert.Equal(
            Loc.Instance.Format(ComposerHistoryStrings.MinutesAgoKey, 5),
            ComposerHistoryStrings.Relative(now.AddMinutes(-5), now));
        Assert.Equal("17:00", ComposerHistoryStrings.Relative(now.AddHours(-1), now));
        Assert.Equal(
            Loc.Instance.Format(ComposerHistoryStrings.YesterdayKey, "09:30"),
            ComposerHistoryStrings.Relative(new DateTimeOffset(new DateTime(2026, 1, 1, 9, 30, 0, DateTimeKind.Local)), now));
        Assert.Equal(
            "12-20 09:30",
            ComposerHistoryStrings.Relative(new DateTimeOffset(new DateTime(2025, 12, 20, 9, 30, 0, DateTimeKind.Local)), now));
    }

    [Fact]
    public async Task A_block_fills_the_composer_with_its_message_and_can_be_saved_as_an_instruction()
    {
        var saved = new List<string>();
        var session = await SessionAsync(saved);
        session.Transcript.AddUserMessage("the earlier question", DateTimeOffset.Now);
        session.Transcript.BeginTurn(DateTimeOffset.Now).AppendText("the answer");
        session.Draft = "half-written";

        // The two actions, as the flyout's block calls them: the message is appended to the draft (never overwriting it), and
        // the same message becomes a new instruction.
        session.ComposerHistory.Refresh();
        var block = Assert.Single(session.ComposerHistory.Entries);
        Assert.Equal("the earlier question", block.Text);
        Assert.Equal("the answer", block.Reply);

        session.FillFromHistory(block);
        Assert.Equal("half-written\n\nthe earlier question", session.Draft);

        session.SaveHistoryAsInstruction(block);
        Assert.Equal(["the earlier question"], saved);

        session.Dispose();
    }

    [Fact]
    public Task A_flyout_block_shows_the_message_above_its_answer_and_both_are_the_fill_target() => SharedUi.RunAsync(async () =>
    {
        var saved = new List<string>();
        var session = await SessionAsync(saved);
        session.Transcript.AddUserMessage("the earlier question", DateTimeOffset.Now);
        session.Transcript.BeginTurn(DateTimeOffset.Now).AppendText("the answer");

        var view = new SessionView { DataContext = session };
        var window = new Window { Width = 900, Height = 640, Content = view };
        window.Show();
        SharedUi.Settle(window);

        var trigger = view.GetVisualDescendants().OfType<Button>()
            .First(button => AutomationProperties.GetAutomationId(button) == "composer-history");

        // Rebuilt on opening: one row for the round, holding its message and the answer under it.
        var rows = OpenRows(trigger, window);
        Assert.Single(session.ComposerHistory.Entries);
        var row = Assert.Single(rows);
        var texts = row.GetVisualDescendants().OfType<Avalonia.Controls.TextBlock>()
            .Where(text => text.Classes.Contains("composer-history-text"))
            .ToArray();
        Assert.Equal(["the earlier question", "the answer"], texts.Select(static text => text.Text));
        Assert.Contains("user", texts[0].Classes);
        Assert.Contains("agent", texts[1].Classes);

        // The block - message and answer together - is one click that writes the message back, and it paints no hover tint
        // under the words: the row's own surface stays transparent.
        var block = Assert.Single(
            row.GetVisualDescendants().OfType<Button>(),
            button => button.Classes.Contains("composer-history-block"));
        Assert.Equal(Colors.Transparent, Assert.IsAssignableFrom<ISolidColorBrush>(block.Background).Color);

        // The answer is quoted: a 2 px rule on its left edge, indented, no fill of its own.
        var quote = Assert.Single(
            row.GetVisualDescendants().OfType<Border>(),
            border => border.Classes.Contains("composer-history-quote"));
        Assert.Equal(new Thickness(2, 0, 0, 0), quote.BorderThickness);
        Assert.Equal(Colors.Transparent, quote.Background is ISolidColorBrush ink ? ink.Color : Colors.Transparent);

        // The pointer fills the row - the whole row, actions included - and the text block adds nothing on top of it, which
        // is what made the earlier version read as two stacked backgrounds.
        var rowBackground = Assert.IsAssignableFrom<ISolidColorBrush>(row.Background);
        Assert.Equal(Colors.Transparent, rowBackground.Color);
        var center = block.TranslatePoint(new Point(block.Bounds.Width / 2, 8), window)!.Value;
        window.MouseMove(center);
        SharedUi.Settle(window);
        Assert.Equal(
            Assert.IsAssignableFrom<ISolidColorBrush>(Resource(row, "SurfaceHoverBrush")).Color,
            Assert.IsAssignableFrom<ISolidColorBrush>(row.Background).Color);
        var surface = block.GetVisualDescendants().OfType<Avalonia.Controls.Presenters.ContentPresenter>()
            .First(presenter => presenter.Name == "PART_ContentPresenter");
        Assert.True(
            surface.Background is not ISolidColorBrush hover || hover.Color.A == 0,
            "the block painted a second background inside the row's hover fill");

        block.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal("the earlier question", session.Draft);
        Assert.False(trigger.Flyout!.IsOpen);

        // Reopened, the block's "save as instruction" hands the same message to the shell.
        rows = OpenRows(trigger, window);
        rows[0].GetVisualDescendants().OfType<Button>()
            .Single(button => Avalonia.Automation.AutomationProperties.GetAutomationId(button) == "composer-history-save")
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(["the earlier question"], saved);

        session.Dispose();
        window.Close();
    });

    [Fact]
    public Task Locating_a_message_scrolls_its_round_to_the_top_and_stops_following() => SharedUi.RunAsync(async () =>
    {
        var session = await SessionAsync([]);
        for (var round = 0; round < 20; round++)
        {
            session.Transcript.AddUserMessage($"question {round}", DateTimeOffset.Now);
            session.Transcript.BeginTurn(DateTimeOffset.Now).AppendText($"answer {round}");
        }

        var view = new SessionView { DataContext = session };
        var window = new Window { Width = 900, Height = 640, Content = view };
        window.Show();
        SharedUi.Settle(window);

        var transcript = view.GetVisualDescendants().OfType<ListBox>()
            .First(list => list.Name == "Transcript");
        var scroller = transcript.GetVisualDescendants().OfType<ScrollViewer>().First();
        Assert.True(scroller.Offset.Y > 0, "the transcript did not start at the end of the session");

        var trigger = view.GetVisualDescendants().OfType<Button>()
            .First(button => AutomationProperties.GetAutomationId(button) == "composer-history");
        var rows = OpenRows(trigger, window);
        rows[0].GetVisualDescendants().OfType<Button>()
            .Single(button => AutomationProperties.GetAutomationId(button) == "composer-history-locate")
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        SharedUi.Settle(window);

        // The first round of the session goes to the top of the viewport, following stops (the "follow" button appears) and
        // the flyout closes behind the jump.
        var located = transcript.GetVisualDescendants().OfType<Avalonia.Controls.TextBlock>()
            .First(block => block.Text == "question 0");
        var y = located.TranslatePoint(default, scroller)!.Value.Y;
        Assert.InRange(y, 0, scroller.Bounds.Height);
        Assert.Contains(
            window.GetVisualDescendants().OfType<Button>(),
            button => AutomationProperties.GetName(button) == TranscriptScrollStrings.Follow && button.IsVisible);
        Assert.False(trigger.Flyout!.IsOpen);

        session.Dispose();
        window.Close();
    });

    /// <summary>A theme resource as a control in the tree resolves it (the app's dictionaries are per theme variant).</summary>
    private static object Resource(Control context, string key) =>
        context.TryFindResource(key, context.ActualThemeVariant, out var value) && value is not null
            ? value
            : throw new InvalidOperationException("no resource " + key);

    /// <summary>Opens the history flyout and returns its rows, in the order the list shows them.</summary>
    private static Border[] OpenRows(Button trigger, Window window)
    {
        trigger.Flyout!.ShowAt(trigger);
        SharedUi.Settle(window);
        var list = window.GetVisualDescendants().OfType<ItemsControl>()
            .First(control => AutomationProperties.GetAutomationId(control) == "composer-history-list");
        return list.GetVisualDescendants().OfType<Border>()
            .Where(border => border.Classes.Contains("composer-history-row"))
            .ToArray();
    }

    private async Task<SessionWorkspaceViewModel> SessionAsync(List<string> saved)
    {
        Directory.CreateDirectory(directory);
        await using var store = new SqliteSessionStore(directory);
        var root = await store.CreateAsync(new SessionCreate(directory, "history"), CancellationToken.None);
        var runtime = new SessionRuntime(
            new AgentRuntimeServices(
                // Nothing sends here: the history flyout is the whole subject.
                ProviderFactory: static _ => throw new InvalidOperationException("no turn is run here"),
                HostClient: null,
                Store: store,
                Catalog: store,
                Todos: new SqliteTodoStore(directory),
                Model: new ModelSettings("test-model")),
            root);
        return new SessionWorkspaceViewModel(
            root,
            runtime,
            checkpoints: null,
            store: new SqliteSessionStore(directory),
            new ProviderServices(Path.Combine(directory, "settings.json"), directory, http),
            saveAsInstruction: saved.Add);
    }
}
