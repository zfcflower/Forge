using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Markup.Xaml.Templates;
using Avalonia.VisualTree;
using Forge.Abstractions;
using Forge.App.ViewModels;
using Forge.App.Views;
using Xunit;

namespace Forge.App.Tests;

/// <summary>
/// Reading the older page as the reader nears the top of the transcript, on the real list, panel and follower: the page
/// lands in front of the reader without being mistaken for a message they sent (which sent the list to the bottom), and
/// the prefetch asks for one page per landing rather than one per layout pass - a landing moves neither the offset nor
/// the extent on this panel, so re-arming it has to wait for the reader.
/// </summary>
[Collection("forge-app-ui")]
public sealed class TranscriptPrefetchTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 25, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public Task Three_prepended_pages_leave_following_off_and_the_readers_round_where_it_was() => SharedUi.RunAsync(() =>
    {
        var transcript = Conversation(turns: 30);
        var (window, list, scroller, follower) = Mount(transcript, loadOlder: null);
        try
        {
            var watched = transcript.Rounds[20];
            Place(list, scroller, watched, 0, window);
            var before = Y(list, scroller, watched);
            Assert.True(before is not null, "the watched round is not on screen");

            // Three pages, one insert at a time: the rule this guards against ("an added round whose first row is a user
            // message means the user sent one") only misfires from the second insert on, so one page would not prove it.
            for (var page = 1; page <= 3; page++)
            {
                transcript.PrependRounds(Conversation(turns: 5));
                Settle(window);

                Assert.False(follower.IsFollowing, $"following came back after prepended page {page}");
                Assert.True(
                    Y(list, scroller, watched) is { } now && Math.Abs(now - before.Value) <= 1,
                    $"page {page} moved the round the reader was on: {before} -> {Y(list, scroller, watched)}");
                Assert.True(
                    scroller.Offset.Y < scroller.Extent.Height - scroller.Viewport.Height - 2,
                    $"page {page} left the list at the bottom (offset {scroller.Offset.Y}, extent {scroller.Extent.Height})");
            }
        }
        finally
        {
            window.Close();
        }

        return Task.CompletedTask;
    });

    [Fact]
    public Task The_prefetch_asks_once_per_landing_and_again_only_after_a_screen_of_scrolling() => SharedUi.RunAsync(() =>
    {
        var transcript = Conversation(turns: 30);
        var calls = 0;
        var (window, list, scroller, follower) = Mount(transcript, loadOlder: () =>
        {
            calls++;
            // One short round: below one screen, so a landing cannot re-arm by growing the extent on its own.
            transcript.PrependRounds(Conversation(turns: 1, tools: false));
            return Task.FromResult(true);
        });
        try
        {
            var viewport = scroller.Viewport.Height;
            Scroll(follower, scroller, viewport * 3.5, window);
            Assert.Equal(1, calls);

            // The landing itself asks for nothing more, however many layout passes follow it.
            Settle(window);
            Settle(window);
            Assert.Equal(1, calls);

            // A screen of scrolling up arms it again - and then exactly one more page.
            Scroll(follower, scroller, scroller.Offset.Y - viewport, window);
            Assert.Equal(2, calls);
            Settle(window);
            Settle(window);
            Assert.Equal(2, calls);
        }
        finally
        {
            window.Close();
        }

        return Task.CompletedTask;
    });

    [Fact]
    public Task A_transcript_that_does_not_fill_the_window_keeps_reading_until_it_does() => SharedUi.RunAsync(() =>
    {
        var transcript = Conversation(turns: 1, tools: false);
        var calls = 0;
        var (window, list, scroller, follower) = Mount(transcript, loadOlder: () =>
        {
            calls++;
            transcript.PrependRounds(Conversation(turns: 1, tools: false));
            return Task.FromResult(true);
        });
        try
        {
            // Mounting leaves the reader at the top of a one-round transcript; one page landing there is what asks for the
            // next, because the window is still empty below it.
            transcript.PrependRounds(Conversation(turns: 1, tools: false));
            Settle(window);

            Assert.True(
                scroller.Extent.Height > scroller.Viewport.Height,
                $"the transcript never filled the window (extent {scroller.Extent.Height}, viewport {scroller.Viewport.Height})");
            Assert.True(calls >= 2, $"the transcript stopped filling after {calls} page(s)");
            var filled = calls;
            Settle(window);
            Settle(window);
            Assert.Equal(filled, calls);
        }
        finally
        {
            window.Close();
        }

        return Task.CompletedTask;
    });

    [Fact]
    public Task A_failed_read_stops_the_automatic_requests() => SharedUi.RunAsync(() =>
    {
        var transcript = Conversation(turns: 30);
        var calls = 0;
        var (window, list, scroller, follower) = Mount(transcript, loadOlder: () =>
        {
            calls++;
            // The view hands back false for a failed read: the red strip is the retry, not another request on every scroll.
            return Task.FromResult(false);
        });
        try
        {
            var viewport = scroller.Viewport.Height;
            Scroll(follower, scroller, viewport * 3, window);
            Assert.Equal(1, calls);

            Scroll(follower, scroller, scroller.Offset.Y - viewport, window);
            Scroll(follower, scroller, scroller.Offset.Y - viewport, window);
            Assert.Equal(1, calls);
        }
        finally
        {
            window.Close();
        }

        return Task.CompletedTask;
    });

    /// <summary>The transcript list, arranged and mounted the way the session view does it.</summary>
    private static (Window Window, TranscriptListBox List, ScrollViewer Scroller, TranscriptFollower Follower) Mount(
        TranscriptViewModel transcript, Func<Task<bool>>? loadOlder)
    {
        var list = new TranscriptListBox
        {
            Padding = new Thickness(12),
            SelectionMode = SelectionMode.Single,
            AutoScrollToSelectedItem = false,
            ItemsSource = transcript.Rounds,
            ItemsPanel = new ItemsPanelTemplate
            {
                Content = new Func<IServiceProvider?, TemplateResult<Control>?>(_ =>
                    new TemplateResult<Control>(new TranscriptStackPanel(), new NameScope())),
            },
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
        var window = new Window { Width = 800, Height = 600, Content = new Grid { Children = { list } } };
        window.Show();
        Settle(window);
        var follower = TranscriptFollower.Attach(list, loadOlder)!;
        Settle(window);
        var scroller = list.GetVisualDescendants().OfType<ScrollViewer>().First();
        follower.StopFollowing();
        follower.BindTranscript(transcript);
        return (window, list, scroller, follower);
    }

    /// <summary>A wheel-notch scroll of the reader's own: the anchor is told the reader moved, exactly as the follower's
    /// wheel handler tells it, or it puts the content back where it held it.</summary>
    private static void Scroll(TranscriptFollower follower, ScrollViewer scroller, double y, Window window)
    {
        follower.Anchor.UserScrolled();
        scroller.Offset = new Vector(0, Math.Max(0, y));
        Settle(window);
    }

    /// <summary>Scrolls until <paramref name="round"/>'s top sits at <paramref name="y"/> DIP on screen.</summary>
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

    /// <summary>A conversation of <paramref name="turns"/> rounds; each round is one user message, its reply and (unless
    /// <paramref name="tools"/> is off) a tool row - the sizes the transcript really has.</summary>
    private static TranscriptViewModel Conversation(int turns, bool tools = true)
    {
        var transcript = new TranscriptViewModel(collapseOlderTurns: false);
        for (var turn = 0; turn < turns; turn++)
        {
            transcript.AddUserMessage($"Turn {turn}: please look at the files and tell me what you find.", At);
            var card = transcript.BeginTurn(At, closePrevious: true);
            card.AppendText("The quick brown fox jumps over the lazy dog, twice over, so the reply wraps.\n\nThe end.");
            if (tools)
            {
                var call = new ToolCall($"t{turn}", "bash", "{\"command\":\"dir src\"}");
                card.AddTool(call);
                card.CompleteTool(
                    call,
                    new ToolResult(string.Join(
                        '\n',
                        Enumerable.Range(0, 20).Select(line => $"line {line}: some tool output long enough to look real"))));
            }

            card.Finish(TimeSpan.FromSeconds(1), null);
        }

        return transcript;
    }

    private static void Settle(Window window) => SharedUi.Settle(window);
}
