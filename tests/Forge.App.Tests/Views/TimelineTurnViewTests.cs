using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Forge.Abstractions;
using Forge.App.Localization;
using Forge.App.ViewModels;
using Forge.App.Workbench.Controls;
using Forge.App.Views;
using Xunit;

namespace Forge.App.Tests.Views;

/// <summary>
/// One agent turn rendered by <see cref="TimelineTurnView"/> (forge-app spec "聊天时间轴(W13)"): the realized rows are one
/// node-less header connector, one block row per reply block, and a footnote row carrying the error triangle exactly while
/// the turn failed. The turn is hosted the way the transcript hosts it — as the item of an <see cref="ItemsControl"/> —
/// because the rows' placement is resolved against the owning ItemsControl.
/// </summary>
[Collection("forge-app-ui")]
public sealed class TimelineTurnViewTests
{
    private static readonly DateTimeOffset At = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task A_turn_hides_its_footnote_row_until_it_fails()
    {
        await SharedUi.RunAsync(() =>
        {
            var turn = new TurnCardViewModel(1, At);
            turn.AppendText("reply");
            var done = turn.AddTool(new ToolCall("c1", "read", "{}"));
            done.Complete(new ToolResult("ok"));
            using var rig = TurnRig.Show(turn);

            var gutters = rig.Gutters().ToList();
            Assert.Equal(3, gutters.Count);
            Assert.Equal(TimelineRowRole.TurnHeader, gutters[0].Role);
            Assert.Equal(TimelineNodeKind.Turn, gutters[0].Kind);
            Assert.False(gutters[0].ShowNode, "the header is a node-less connector");
            Assert.Equal(2, gutters.Count(g => g.Role == TimelineRowRole.Block));
            Assert.Equal(0, gutters.Count(g => g.Role == TimelineRowRole.Footnote));

            // The reply text was sealed when the tool row arrived (AddTool), so both blocks are idle.
            Assert.DoesNotContain("running", Icon(gutters[1]).Classes);
            var doneIcon = Icon(gutters[2]);
            Assert.DoesNotContain("running", doneIcon.Classes);
            Assert.DoesNotContain("failed", doneIcon.Classes);
            Assert.DoesNotContain("pulsing", doneIcon.Classes);
            Assert.Same(
                rig.Window.FindResource(TimelineNodeMap.IconKey(TimelineNodeMap.KindForTool("read"), filled: false)),
                doneIcon.Data);

            // The turn failed: the footnote row appears and carries the error triangle.
            turn.Finish(TimeSpan.FromSeconds(1), SessionStrings.TurnFailed("boom"));
            SharedUi.Settle(rig.Window);

            var after = rig.Gutters().ToList();
            Assert.Equal(4, after.Count);
            var footnote = after[^1];
            Assert.Equal(TimelineRowRole.Footnote, footnote.Role);
            Assert.Equal(TimelineNodeKind.Error, footnote.Kind);
            Assert.True(footnote.ShowNode);
            Assert.True(footnote.ShowTopLine);
            Assert.False(footnote.ShowBottomLine, "the single-item turn's footnote is the last row of its list");
            var footnoteIcon = Icon(footnote);
            Assert.Contains("failed", footnoteIcon.Classes);
            Assert.DoesNotContain("running", footnoteIcon.Classes);
            Assert.DoesNotContain("pulsing", footnoteIcon.Classes);
            Assert.Same(
                rig.Window.FindResource(TimelineNodeMap.IconKey(TimelineNodeKind.Error, filled: true)),
                footnoteIcon.Data);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task A_turns_rows_land_on_the_same_axis_as_its_header()
    {
        await SharedUi.RunAsync(() =>
        {
            var turn = new TurnCardViewModel(2, At);
            turn.AppendText("reply");
            var done = turn.AddTool(new ToolCall("c1", "read", "{}"));
            done.Complete(new ToolResult("ok"));
            turn.Finish(TimeSpan.FromSeconds(1), SessionStrings.TurnFailed("boom"));
            using var rig = TurnRig.Show(turn);

            var gutters = rig.Gutters().ToList();
            Assert.Equal(4, gutters.Count);
            Assert.All(gutters, g => Assert.True(g.Bounds.Width > 0));
            Assert.All(gutters, g => Assert.Equal(gutters[0].Bounds.X, g.Bounds.X, 0.01));
            Assert.All(gutters, g => Assert.Equal(gutters[0].Bounds.Width, g.Bounds.Width, 0.01));
            return Task.CompletedTask;
        });
    }

    // ------------------------------------------------------------------ harness

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

    /// <summary>The turn hosted as the only item of an ItemsControl, the way the transcript hosts it.</summary>
    private sealed class TurnRig : IDisposable
    {
        private TurnRig(ItemsControl host, Window window) => (Host, Window) = (host, window);

        public ItemsControl Host { get; }

        public Window Window { get; }

        public static TurnRig Show(TurnCardViewModel turn)
        {
            var host = new ItemsControl { ItemsSource = new[] { turn } };
            var window = SharedUi.Show(host, 600, 500);
            SharedUi.Settle(window);
            return new TurnRig(host, window);
        }

        public IEnumerable<TimelineGutter> Gutters() =>
            Window.GetVisualDescendants().OfType<TimelineGutter>().Where(EffectivelyVisible);

        public void Dispose() => Window.Close();
    }
}
