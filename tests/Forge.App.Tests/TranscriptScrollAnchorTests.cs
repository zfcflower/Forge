using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Markup.Xaml.Templates;
using Avalonia.VisualTree;
using Forge.Abstractions;
using TextBlock = Avalonia.Controls.TextBlock;
using Forge.App.ViewModels;
using Forge.App.Views;
using Xunit;

namespace Forge.App.Tests;

/// <summary>
/// The two things a reader's place depends on when rows change under them, tested on the real list, panel, follower and
/// anchor (the plan's "content anchor" and "a click inside a row must not scroll the list" decisions):
/// a row that opens by itself below the top edge moves what comes after it, never what the reader is on; and pressing inside
/// a row - its text, or a label that is neither text nor button - must not bring the row into view.
/// </summary>
[Collection("forge-app-ui")]
public sealed class TranscriptScrollAnchorTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public Task The_round_at_the_top_stays_put_when_a_thinking_block_below_it_opens_by_itself() => SharedUi.RunAsync(() =>
    {
        var transcript = Paged(turns: 8);
        var (window, list, scroller, follower) = Mount(transcript);
        try
        {
            // The round crossing the top edge is the anchor; the thinking block that opens on its own is two rounds further
            // down, so what it grows must move the rows after it and nothing above.
            var anchored = transcript.Rounds[3];
            var thinking = transcript.Rounds[5].Rows.OfType<TurnThinkingViewModel>().Single();
            Place(list, scroller, anchored, -20, window);
            var before = Y(list, scroller, anchored);
            var extentBefore = scroller.Extent.Height;
            Assert.True(before is > -60 and < 0, $"the anchored round should cross the top edge, its top is at {before}");

            thinking.IsExpanded = true;
            Settle(window);

            Assert.True(
                scroller.Extent.Height > extentBefore + 20,
                $"the thinking body did not grow the extent ({extentBefore} -> {scroller.Extent.Height})");
            var after = Y(list, scroller, anchored);
            Assert.True(
                after is not null && before is not null && Math.Abs(after.Value - before.Value) <= 1,
                $"the round at the top moved from {before} to {after} while content below it grew");
        }
        finally
        {
            window.Close();
        }

        return Task.CompletedTask;
    });

    [Fact]
    public Task Pressing_the_text_of_an_open_call_does_not_scroll_the_list() => SharedUi.RunAsync(() =>
    {
        var transcript = Paged(turns: 8);
        var (window, list, scroller, follower) = Mount(transcript);
        try
        {
            var (round, tool) = OpenCall(transcript, window, list, scroller, index: 4);

            // The result is the tall body, and it runs past the bottom of the window; the reader presses the part that shows.
            // That is the case where asking for the whole block to be brought into view used to drag the list to it.
            var viewport = scroller.Viewport.Height;
            var text = Visible<SelectableTextBlock>(list, tool, t => true, tallest: true);
            var top = text.TranslatePoint(default, scroller)!.Value.Y;
            scroller.Offset = new Vector(0, scroller.Offset.Y + (top - viewport / 3));
            Settle(window);
            top = text.TranslatePoint(default, scroller)?.Y ?? double.NaN;
            Assert.True(
                top is > 20 && top + text.Bounds.Height > viewport + 20,
                $"the press has to land on visible text that runs past the bottom (top {top}, height {text.Bounds.Height}, viewport {viewport})");
            var offsetBefore = scroller.Offset.Y;

            Press(text, window);

            Assert.True(
                Math.Abs(scroller.Offset.Y - offsetBefore) <= 1,
                $"pressing the text of an open call moved the list from {offsetBefore} to {scroller.Offset.Y}");
        }
        finally
        {
            window.Close();
        }

        return Task.CompletedTask;
    });

    [Fact]
    public Task Pressing_a_label_of_an_open_call_does_not_scroll_the_list() => SharedUi.RunAsync(() =>
    {
        var transcript = Paged(turns: 8);
        var (window, list, scroller, follower) = Mount(transcript);
        try
        {
            var (round, tool) = OpenCall(transcript, window, list, scroller, index: 4);
            var label = Visible<TextBlock>(list, tool, t => t.Classes.Contains("label"));
            var offsetBefore = scroller.Offset.Y;

            Press(label, window);

            Assert.True(
                Math.Abs(scroller.Offset.Y - offsetBefore) <= 1,
                $"pressing a label of an open call moved the list from {offsetBefore} to {scroller.Offset.Y}");
        }
        finally
        {
            window.Close();
        }

        return Task.CompletedTask;
    });

    /// <summary>A main-session transcript of short turns, each with reasoning, a reply and one call with a long result.</summary>
    private static TranscriptViewModel Paged(int turns)
    {
        var transcript = new TranscriptViewModel(collapseOlderTurns: false);
        var output = string.Join('\n', Enumerable.Range(0, 40).Select(line => $"line {line}: some tool output long enough to look real"));
        for (var turn = 0; turn < turns; turn++)
        {
            transcript.AddUserMessage($"Turn {turn}: please look at the files and tell me what you find.", At);
            var card = transcript.BeginTurn(At, closePrevious: true);
            card.AppendThinking(string.Join('\n', Enumerable.Range(0, 6).Select(line => $"thought {line}: weighing the next step")));
            card.AppendText("The quick brown fox jumps over the lazy dog, twice over, so the reply wraps onto a few lines.\n\nThe end.");
            var call = new ToolCall($"t{turn}", "bash", "{\"command\":\"dir src\"}");
            card.AddTool(call);
            card.CompleteTool(call, new ToolResult(output));
            card.Finish(TimeSpan.FromSeconds(1), null);
        }

        return transcript;
    }

    /// <summary>The call of a round, opened and scrolled onto the screen (its body starts just above the top edge).</summary>
    private static (TranscriptRoundViewModel Round, ToolRowViewModel Tool) OpenCall(
        TranscriptViewModel transcript, Window window, TranscriptListBox list, ScrollViewer scroller, int index)
    {
        var round = transcript.Rounds[index];
        var tool = round.Rows.OfType<TurnToolGroupViewModel>().Single().Tools[0];
        tool.IsExpanded = true;
        Place(list, scroller, round, -120, window);
        return (round, tool);
    }

    private static (Window Window, TranscriptListBox List, ScrollViewer Scroller, TranscriptFollower Follower) Mount(TranscriptViewModel transcript)
    {
        var list = new TranscriptListBox
        {
            Padding = new Thickness(12),
            SelectionMode = SelectionMode.Single,
            AutoScrollToSelectedItem = false,
            ItemsSource = transcript.Rounds,
            ItemsPanel = new ItemsPanelTemplate
            {
                Content = new Func<IServiceProvider?, TemplateResult<Control>?>(_ => new TemplateResult<Control>(new TranscriptStackPanel(), new NameScope())),
            },
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
        var window = new Window { Width = 800, Height = 600, Content = new Grid { Children = { list } } };
        window.Show();
        SharedUi.Settle(window);
        var follower = TranscriptFollower.Attach(list)!;
        SharedUi.Settle(window);
        var scroller = list.GetVisualDescendants().OfType<ScrollViewer>().First();
        follower.StopFollowing();
        return (window, list, scroller, follower);
    }

    /// <summary>Scrolls until the round's top sits at <paramref name="y"/> DIP on screen.</summary>
    private static void Place(TranscriptListBox list, ScrollViewer scroller, object round, double y, Window window)
    {
        list.ScrollIntoView(round);
        Settle(window);
        for (var attempt = 0; attempt < 4; attempt++)
        {
            if (Y(list, scroller, round) is not { } now || Math.Abs(now - y) < 0.5)
            {
                return;
            }

            scroller.Offset = new Vector(0, scroller.Offset.Y + (now - y));
            Settle(window);
        }
    }

    private static double? Y(TranscriptListBox list, ScrollViewer scroller, object round) =>
        list.ContainerFromItem(round)?.TranslatePoint(default, scroller)?.Y;

    /// <summary>A realized control of a row (a row is inside a round, so the list has no container for it).</summary>
    private static T Visible<T>(TranscriptListBox list, object row, Func<T, bool>? where = null, bool tallest = false)
        where T : Control
    {
        var matches = list.GetVisualDescendants().OfType<T>()
            .Where(c => c.IsEffectivelyVisible && ReferenceEquals(c.DataContext, row) && (where?.Invoke(c) ?? true));
        return (tallest ? matches.OrderByDescending(c => c.Bounds.Height) : matches).FirstOrDefault()
               ?? throw new InvalidOperationException($"nothing visible of {row.GetType().Name} is realized");
    }

    /// <summary>
    /// A real press and release on the control, with the focus a press gives it: the chain the list must not act on is the
    /// focus change asking to bring the control (or the whole row) into view.
    /// </summary>
    private static void Press(Control target, Window window)
    {
        var local = new Point(Math.Min(20, target.Bounds.Width / 2), Math.Min(8, target.Bounds.Height / 2));
        var position = target.TranslatePoint(local, window) ?? throw new InvalidOperationException("the target is not attached");
        var pointer = new Pointer(Pointer.GetNextFreeId(), PointerType.Mouse, isPrimary: true);
        var focus = target.GetSelfAndVisualAncestors().OfType<InputElement>().FirstOrDefault(e => e.Focusable && e.IsEffectivelyEnabled && e.IsEffectivelyVisible) ?? target;
        focus.Focus(NavigationMethod.Pointer);
        var pressed = new PointerPoint(pointer, position, new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed));
        target.RaiseEvent(new PointerPressedEventArgs(target, pointer, window, position, 0, pressed.Properties, KeyModifiers.None));
        var released = new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased);
        target.RaiseEvent(new PointerReleasedEventArgs(target, pointer, window, position, 0, released, KeyModifiers.None, MouseButton.Left));
        SharedUi.Settle(window);
    }

    private static void Settle(Window window) => SharedUi.Settle(window, passes: 2);
}
