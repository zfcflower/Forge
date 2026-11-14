using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Forge.Abstractions;
using Forge.App.Localization;
using Forge.App.Preferences;
using Forge.App.Tests.Localization;
using Forge.App.ViewModels;
using Forge.App.Workbench.Controls;
using Forge.App.Views;
using Xunit;

namespace Forge.App.Tests.Views;

/// <summary>
/// The chat timeline's gutter behaviour in a real virtualized transcript (forge-app spec "聊天时间轴(W13)"): the realized
/// rows land on one axis in transcript order, the first node has no line above and the last none below, a running node is
/// filled and pulsing (unless the motion gate says otherwise), every gutter is invisible to focus and to hit testing and
/// carries its localized node name, and append / collapse / scroll recompute the line flags without rebuilding anything.
/// <para>The transcript is driven through the production <see cref="TranscriptViewModel"/> API and rendered by the
/// application's own data templates in a plain <see cref="ListBox"/>, exactly like <see cref="SessionView"/> hosts it.</para>
/// </summary>
[Collection("forge-app-ui")]
public sealed class TimelineGutterTests
{
    private static readonly DateTimeOffset At = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    // ------------------------------------------------------------------ placement and states

    [Fact]
    public async Task A_realized_transcript_lays_its_gutters_out_in_order_with_the_right_states()
    {
        var previousMotion = TreeMotion.ReducedMotion;
        TreeMotion.ReducedMotion = false;
        try
        {
            await SharedUi.RunAsync(() =>
            {
                var transcript = FullTranscript();
                using var rig = TranscriptRig.Show(transcript, 1200, 800);

                var gutters = rig.VisibleGutters().ToList();
                Assert.Equal(9, gutters.Count);

                // (Kind, Role, ShowNode, ShowTopLine, ShowBottomLine) in transcript order.
                var expected = new[]
                {
                    (TimelineNodeKind.User, TimelineRowRole.Item, true, false, true),
                    (TimelineNodeKind.Turn, TimelineRowRole.TurnHeader, false, true, true),
                    (TimelineNodeKind.Assistant, TimelineRowRole.Block, true, true, true),
                    (TimelineNodeKind.Tool, TimelineRowRole.Block, true, true, true),
                    (TimelineNodeKind.Tool, TimelineRowRole.Block, true, true, true),
                    (TimelineNodeKind.Notice, TimelineRowRole.Block, true, true, true),
                    (TimelineNodeKind.Tool, TimelineRowRole.Block, true, true, true),
                    (TimelineNodeKind.Error, TimelineRowRole.Footnote, true, true, true),
                    (TimelineNodeKind.Notice, TimelineRowRole.Item, true, true, false),
                };
                for (var i = 0; i < expected.Length; i++)
                {
                    var gutter = gutters[i];
                    Assert.Equal(expected[i].Item1, gutter.Kind);
                    Assert.Equal(expected[i].Item2, gutter.Role);
                    Assert.Equal(expected[i].Item3, gutter.ShowNode);
                    Assert.Equal(expected[i].Item4, gutter.ShowTopLine);
                    Assert.Equal(expected[i].Item5, gutter.ShowBottomLine);
                }

                // The first gutter has no line above it; the last visible node has none below it.
                Assert.False(gutters[0].ShowTopLine);
                Assert.False(gutters[^1].ShowBottomLine);

                // The three tool rows carry their names so the kind mapping can be checked on the icons.
                var done = gutters[3];
                var failed = gutters[4];
                var running = gutters[6];
                Assert.Equal("read", done.ToolName);
                Assert.Equal("edit", failed.ToolName);
                Assert.Equal("bash", running.ToolName);

                // The running call: running + pulsing classes and the filled variant of its mapped icon.
                var runningIcon = Icon(running);
                Assert.Contains("running", runningIcon.Classes);
                Assert.Contains("pulsing", runningIcon.Classes);
                Assert.DoesNotContain("failed", runningIcon.Classes);
                Assert.Same(
                    rig.Window.FindResource(TimelineNodeMap.IconKey(TimelineNodeMap.KindForTool("bash"), filled: true)),
                    runningIcon.Data);

                // The failed call: filled + failed, no pulse.
                var failedIcon = Icon(failed);
                Assert.Contains("failed", failedIcon.Classes);
                Assert.DoesNotContain("running", failedIcon.Classes);
                Assert.DoesNotContain("pulsing", failedIcon.Classes);
                Assert.Same(
                    rig.Window.FindResource(TimelineNodeMap.IconKey(TimelineNodeMap.KindForTool("edit"), filled: true)),
                    failedIcon.Data);

                // The finished call: neither state class, outline icon.
                var doneIcon = Icon(done);
                Assert.DoesNotContain("running", doneIcon.Classes);
                Assert.DoesNotContain("failed", doneIcon.Classes);
                Assert.DoesNotContain("pulsing", doneIcon.Classes);
                Assert.Same(
                    rig.Window.FindResource(TimelineNodeMap.IconKey(TimelineNodeMap.KindForTool("read"), filled: false)),
                    doneIcon.Data);

                // The footnote closed the turn with the error triangle.
                var footnote = gutters[7];
                var footnoteIcon = Icon(footnote);
                Assert.Contains("failed", footnoteIcon.Classes);
                Assert.Same(
                    rig.Window.FindResource(TimelineNodeMap.IconKey(TimelineNodeKind.Error, filled: true)),
                    footnoteIcon.Data);
                return Task.CompletedTask;
            });
        }
        finally
        {
            TreeMotion.ReducedMotion = previousMotion;
        }
    }

    // ------------------------------------------------------------------ accessibility

    [Fact]
    public async Task Gutters_are_unfocusable_and_carry_the_localized_node_name_that_follows_the_language()
    {
        using var language = new LanguageScope(UiLanguage.Chinese);
        await SharedUi.RunAsync(() =>
        {
            var transcript = FullTranscript();
            using var rig = TranscriptRig.Show(transcript, 1200, 800);
            var gutters = rig.VisibleGutters().ToList();
            Assert.Equal(9, gutters.Count);

            foreach (var gutter in gutters)
            {
                Assert.False(gutter.Focusable);
                Assert.False(gutter.IsTabStop);
                Assert.False(gutter.IsHitTestVisible);
            }

            // Names: the localized node name, with the state word appended while running or failing.
            Assert.Equal(TimelineStrings.Describe(TimelineNodeKind.User, TimelineNodeState.Idle), gutters[0].NodeName);
            Assert.Equal(string.Empty, gutters[1].NodeName); // the turn header has no node.
            Assert.Equal(TimelineStrings.Describe(TimelineNodeKind.Assistant, TimelineNodeState.Idle), gutters[2].NodeName);
            Assert.Equal(TimelineStrings.Describe(TimelineNodeKind.Command, TimelineNodeState.Running), gutters[6].NodeName);
            Assert.Equal(TimelineStrings.Describe(TimelineNodeKind.EditFile, TimelineNodeState.Failed), gutters[4].NodeName);
            Assert.Equal(TimelineStrings.Describe(TimelineNodeKind.Error, TimelineNodeState.Failed), gutters[7].NodeName);
            Assert.Equal(TimelineStrings.Describe(TimelineNodeKind.Notice, TimelineNodeState.Idle), gutters[8].NodeName);

            // The names are live text: switching the language renames every realized gutter in place.
            Loc.Instance.Language = UiLanguage.English;
            Assert.Equal("User message", gutters[0].NodeName);
            Assert.Equal(TimelineStrings.Describe(TimelineNodeKind.Command, TimelineNodeState.Running), gutters[6].NodeName);
            Assert.Equal(string.Empty, gutters[1].NodeName);
            return Task.CompletedTask;
        });
    }

    // ------------------------------------------------------------------ reduced motion

    [Fact]
    public async Task The_motion_gate_stops_and_restores_the_running_nodes_pulse_in_place()
    {
        var previousMotion = TreeMotion.ReducedMotion;
        TreeMotion.ReducedMotion = false;
        try
        {
            await SharedUi.RunAsync(() =>
            {
                var transcript = FullTranscript();
                using var rig = TranscriptRig.Show(transcript, 1200, 800);
                var running = rig.VisibleGutters().Single(gutter => gutter.IsRunning);
                var icon = Icon(running);
                Assert.Contains("running", icon.Classes);
                Assert.Contains("pulsing", icon.Classes);

                TreeMotion.ReducedMotion = true;
                Assert.True(icon.Classes.Contains("running"), "the state class stays while the pulse is dropped");
                Assert.DoesNotContain("pulsing", icon.Classes);

                TreeMotion.ReducedMotion = false;
                Assert.Contains("running", icon.Classes);
                Assert.Contains("pulsing", icon.Classes);
                return Task.CompletedTask;
            });
        }
        finally
        {
            TreeMotion.ReducedMotion = previousMotion;
        }
    }

    // ------------------------------------------------------------------ append

    [Fact]
    public async Task Appending_a_notice_recomputes_the_previous_last_rows_bottom_line()
    {
        await SharedUi.RunAsync(() =>
        {
            var transcript = new TranscriptViewModel(collapseOlderTurns: false);
            transcript.AddUserMessage("hello", At);
            var turn = transcript.BeginTurn(At);
            turn.AppendText("reply");
            turn.Finish(TimeSpan.FromSeconds(1), null);
            transcript.AddNotice(SessionStrings.Compacted(120, 60));
            using var rig = TranscriptRig.Show(transcript, 1200, 800);

            var gutters = rig.VisibleGutters().ToList();
            Assert.Equal(4, gutters.Count);
            Assert.False(gutters[^1].ShowBottomLine, "the last row must not draw a line below before anything follows");
            Assert.True(gutters[^2].ShowBottomLine);

            transcript.AddNotice(SessionStrings.Compacted(60, 30));
            SharedUi.Settle(rig.Window);

            var after = rig.VisibleGutters().ToList();
            Assert.Equal(5, after.Count);
            Assert.True(after[^2].ShowBottomLine, "the previously last row gained a follower, so its axis continues");
            Assert.False(after[^1].ShowBottomLine, "the new last row still ends the axis");
            return Task.CompletedTask;
        });
    }

    // ------------------------------------------------------------------ collapse / expand

    [Fact]
    public async Task Collapsing_a_middle_turn_hides_its_blocks_and_keeps_the_header_continuous()
    {
        await SharedUi.RunAsync(() =>
        {
            var transcript = new TranscriptViewModel(collapseOlderTurns: false);
            transcript.AddUserMessage("hello", At);
            var turn = transcript.BeginTurn(At);
            turn.AppendText("reply");
            turn.AddTool(new ToolCall("c1", "read", "{}"));
            turn.Finish(TimeSpan.FromSeconds(1), null);
            transcript.AddNotice(SessionStrings.Compacted(120, 60));
            using var rig = TranscriptRig.Show(transcript, 1200, 800);

            var header = rig.VisibleGutters().Single(gutter => gutter.Role == TimelineRowRole.TurnHeader);
            Assert.False(header.ShowNode);
            Assert.True(header.ShowTopLine);
            Assert.True(header.ShowBottomLine);
            Assert.Equal(2, rig.VisibleGutters().Count(gutter => gutter.Role == TimelineRowRole.Block));

            turn.IsExpanded = false;
            SharedUi.Settle(rig.Window);

            header = rig.VisibleGutters().Single(gutter => gutter.Role == TimelineRowRole.TurnHeader);
            Assert.True(header.ShowTopLine && header.ShowBottomLine, "the axis passes straight through a collapsed header");
            Assert.False(header.ShowNode);
            Assert.All(rig.VisibleGutters().Where(g => g.Role == TimelineRowRole.Block), g => Assert.False(EffectivelyVisible(g)));

            turn.IsExpanded = true;
            SharedUi.Settle(rig.Window);

            header = rig.VisibleGutters().Single(gutter => gutter.Role == TimelineRowRole.TurnHeader);
            Assert.False(header.ShowNode, "the header is a node-less connector again");
            Assert.True(header.ShowTopLine);
            Assert.True(header.ShowBottomLine);
            var blocks = rig.VisibleGutters().Where(g => g.Role == TimelineRowRole.Block).ToList();
            Assert.Equal(2, blocks.Count);
            Assert.All(blocks, g => Assert.True(EffectivelyVisible(g)));
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task An_expanded_first_turns_header_starts_the_axis_at_its_first_block()
    {
        await SharedUi.RunAsync(() =>
        {
            var transcript = new TranscriptViewModel(collapseOlderTurns: false);
            var turn = transcript.BeginTurn(At);
            turn.AppendText("reply");
            turn.AddTool(new ToolCall("c1", "read", "{}"));
            turn.Finish(TimeSpan.FromSeconds(1), null);
            transcript.AddNotice(SessionStrings.Compacted(120, 60));
            using var rig = TranscriptRig.Show(transcript, 1200, 800);

            var header = rig.VisibleGutters().Single(gutter => gutter.Role == TimelineRowRole.TurnHeader);
            Assert.False(header.ShowNode);
            Assert.False(header.ShowTopLine);
            Assert.False(header.ShowBottomLine);
            Assert.Equal(2, rig.VisibleGutters().Count(gutter => gutter.Role == TimelineRowRole.Block && EffectivelyVisible(gutter)));

            // Collapsing the first turn changes nothing about its connector: it never had a line to draw.
            turn.IsExpanded = false;
            SharedUi.Settle(rig.Window);
            header = rig.VisibleGutters().Single(gutter => gutter.Role == TimelineRowRole.TurnHeader);
            Assert.False(header.ShowNode);
            Assert.False(header.ShowTopLine);
            Assert.False(header.ShowBottomLine);
            return Task.CompletedTask;
        });
    }

    // ------------------------------------------------------------------ alignment

    [Fact]
    public async Task Every_realized_gutter_sits_on_the_same_axis()
    {
        await SharedUi.RunAsync(() =>
        {
            using var rig = TranscriptRig.Show(FullTranscript(), 1200, 800);
            var gutters = rig.VisibleGutters().ToList();
            Assert.True(gutters.Count > 3);

            Assert.All(gutters, g => Assert.True(g.Bounds.Width > 0, "a gutter must be laid out"));
            Assert.All(gutters, g => Assert.Equal(gutters[0].Bounds.X, g.Bounds.X, 0.01));
            Assert.All(gutters, g => Assert.Equal(gutters[0].Bounds.Width, g.Bounds.Width, 0.01));
            return Task.CompletedTask;
        });
    }

    // ------------------------------------------------------------------ virtualization

    [Fact]
    public async Task A_long_transcript_realizes_far_fewer_gutters_than_items()
    {
        await SharedUi.RunAsync(() =>
        {
            var transcript = new TranscriptViewModel(collapseOlderTurns: false);
            for (var i = 0; i < 300; i++)
            {
                transcript.AddNotice(LocText.Literal("note " + i.ToString(CultureInfo.InvariantCulture)));
            }

            using var rig = TranscriptRig.Show(transcript, 400, 400);
            var realized = rig.VisibleGutters().ToList();
            Assert.True(
                realized.Count < transcript.Items.Count / 2,
                $"a 400x400 window must virtualize: realized {realized.Count} of {transcript.Items.Count}");
            return Task.CompletedTask;
        });
    }

    /// <summary>
    /// EXPOSES A PRODUCT DEFECT (fails until <c>TimelineGutter</c> recomputes after a scroll-driven recycle). After
    /// <see cref="ListBox.ScrollIntoView"/> and settling, the recycled rows keep the "not in a realized container"
    /// fallback (node, no lines) although the containers are realized and resolvable: poking any gutter property makes a
    /// recompute produce the correct flags at once. Expected for the noticed rows 285..299: <c>ShowTopLine</c> true on all
    /// and <c>ShowBottomLine</c> true on all but the last; actual: false on all (verified via a forced recompute).
    /// </summary>
    [Fact]
    public async Task Scrolling_to_the_end_keeps_the_axis_flags_of_the_realized_rows()
    {
        await SharedUi.RunAsync(() =>
        {
            var transcript = new TranscriptViewModel(collapseOlderTurns: false);
            for (var i = 0; i < 300; i++)
            {
                transcript.AddNotice(LocText.Literal("note " + i.ToString(CultureInfo.InvariantCulture)));
            }

            using var rig = TranscriptRig.Show(transcript, 400, 400);
            rig.List.ScrollIntoView(transcript.Items[^1]);
            SharedUi.Settle(rig.Window);

            var scrolled = rig.VisibleGutters().ToList();
            Assert.NotEmpty(scrolled);
            // Bounds are container-relative: translate to the window to find the visually lowest realized row.
            var lowest = scrolled.Aggregate((a, b) => InWindow(rig.Window, a) >= InWindow(rig.Window, b) ? a : b);
            Assert.False(
                lowest.ShowBottomLine,
                $"the last visible node must end the axis after scrolling (got ShowTopLine={lowest.ShowTopLine}, "
                + $"ShowBottomLine={lowest.ShowBottomLine}: TimelineGutter keeps the not-in-a-container fallback "
                + "after a scroll-driven recycle)");
            Assert.True(
                lowest.ShowTopLine,
                $"the last visible node has a node above it (got ShowTopLine={lowest.ShowTopLine}: the gutter never "
                + "recomputed after the recycle)");
            Assert.Equal(TimelineRowRole.Item, lowest.Role);
            Assert.All(
                scrolled.Where(g => !ReferenceEquals(g, lowest)),
                g => Assert.True(g.ShowTopLine && g.ShowBottomLine, "a middle row connects the axis above and below"));
            return Task.CompletedTask;
        });
    }

    // ------------------------------------------------------------------ harness

    /// <summary>
    /// The task-shaped transcript: a user message, then a turn (reply text, a finished call, a failed call, a note, a
    /// still-running call, an error footnote), then a top-level notice. The running call is added after <c>Finish</c>
    /// because a finishing turn force-completes its pending calls; the row keeps its live state all the same.
    /// </summary>
    private static TranscriptViewModel FullTranscript()
    {
        var transcript = new TranscriptViewModel(collapseOlderTurns: false);
        transcript.AddUserMessage("hello", At);
        var turn = transcript.BeginTurn(At);
        turn.AppendText("reply");
        var done = turn.AddTool(new ToolCall("c1", "read", "{}"));
        done.Complete(new ToolResult("ok"));
        var failed = turn.AddTool(new ToolCall("c2", "edit", "{}"));
        failed.Complete(new ToolResult("boom", IsError: true));
        turn.AddNotice(SessionStrings.SteeredNotice("go on"));
        turn.Finish(TimeSpan.FromSeconds(2), SessionStrings.TurnFailed("boom"));
        var running = turn.AddTool(new ToolCall("c3", "bash", "{}"));
        Assert.True(running.IsRunning);
        transcript.AddNotice(SessionStrings.Compacted(120, 60));
        return transcript;
    }

    private static FluentIcon Icon(TimelineGutter gutter) =>
        gutter.GetVisualDescendants().OfType<FluentIcon>().Single();

    private static bool EffectivelyVisible(Visual visual)
    {
        for (var node = visual; node is not null; node = node.GetVisualParent())
        {
            if (node is Control control && !control.IsVisible)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>A transcript hosted in a plain ListBox the way <see cref="SessionView"/> hosts it, in a shown window.</summary>
    private sealed class TranscriptRig : IDisposable
    {
        private TranscriptRig(ListBox list, Window window) => (List, Window) = (list, window);

        public ListBox List { get; }

        public Window Window { get; }

        public static TranscriptRig Show(TranscriptViewModel transcript, double width, double height)
        {
            var list = new ListBox { ItemsSource = transcript.Items };
            var window = SharedUi.Show(list, width, height);
            SharedUi.Settle(window);
            return new TranscriptRig(list, window);
        }

        /// <summary>The realized, effectively visible gutters in visual (transcript) order.</summary>
        public IEnumerable<TimelineGutter> VisibleGutters() =>
            Window.GetVisualDescendants().OfType<TimelineGutter>().Where(g => EffectivelyVisible(g));

        public void Dispose() => Window.Close();
    }

    private static double InWindow(Window window, TimelineGutter gutter) =>
        gutter.TranslatePoint(new Point(0, gutter.Bounds.Bottom), window)
        is { } point ? point.Y : double.MinValue;
}
