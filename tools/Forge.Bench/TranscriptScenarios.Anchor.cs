using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Forge.Abstractions;
using Forge.App.ViewModels;
using Forge.App.Views;

namespace Forge.Bench;

/// <summary>
/// Expand/collapse stability of the transcript on a real window: each case puts a row at a known place on screen, makes one
/// change the way the app makes it (a click on the row's button through its automation peer, or the view model's own
/// state change for what the app changes by itself), lets three frames pass and reports how far the content the user was
/// looking at moved on screen. The measure is the screen y of one row (or of the clicked button) found again by its view
/// model, never the scroll offset: an offset can stay equal while the content under it moved.
/// </summary>
internal static partial class TranscriptScenarios
{
    private static readonly string[] AnchorCases =
    [
        "turn-above",
        "rows-above",
        "follow-expand",
    ];


    /// <summary>"anchor": the app as it is (<see cref="TranscriptStackPanel"/> with the content anchor); "anchor-vsp": the
    /// content anchor on the stock panel; "anchor-off": the stock panel alone (the baseline before the fix).</summary>
    private static readonly string[] AnchorModes = ["anchor", "anchor-vsp", "anchor-off"];

    private static IEnumerable<BenchScenario> AnchorScenarios() =>
        AnchorModes.SelectMany(mode => AnchorCases.Select(name => AnchorCase(name, mode)));

    private static BenchScenario AnchorCase(string name, string mode) =>
        new(
            $"transcript.{mode}.{name}",
            "transcript",
            Heavy: false,
            PrepareAsync: async cancellationToken =>
            {
                var rig = await Rig.GetAsync(cancellationToken).ConfigureAwait(false);
                return new BenchCase
                {
                    Warmup = 0,
                    Iterations = 3,
                    Note = "screen drift of the watched content, DIP",
                    Run = async _ =>
                    {
                        TranscriptScrollAnchor.Enabled = mode != "anchor-off";
                        string report;
                        try
                        {
                            report = await rig.AnchorAsync(name, mode == "anchor").ConfigureAwait(false);
                        }
                        finally
                        {
                            TranscriptScrollAnchor.Enabled = true;
                        }

                        Console.Error.WriteLine($"{mode} {name}: {report}");
                        if (mode == "anchor")
                        {
                            AssertStable(name, report);
                        }

                        return new OpResult(Items: 1, Observation: report);
                    },
                };
            });

    /// <summary>Cases where the watched content leaves the list (its row collapses away or is replaced), so there is nothing left to hold still.</summary>
    private static readonly string[] ContentLeaves = ["turn-covering", "subagent-new-turn"];

    /// <summary>
    /// The acceptance for the app as shipped: in every case where the content stays in the list, it must not move more than
    /// one DIP on screen (the first frame included), or the case fails and the run with it.
    /// </summary>
    private static void AssertStable(string name, string report)
    {
        if (ContentLeaves.Contains(name))
        {
            return;
        }

        foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(report, @"(?:drift|first frame|worst between frames) (-?\d+(?:\.\d+)?|lost|NaN)"))
        {
            if (!double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var drift) || Math.Abs(drift) > 1d)
            {
                throw new InvalidOperationException($"transcript.anchor.{name}: the content moved ({match.Value}); the limit is 1 DIP. {report}");
            }
        }
    }

    private sealed partial class Rig
    {
        private static string F(double value) => value.ToString("F1", CultureInfo.InvariantCulture);

        private static TranscriptViewModel BuildRich(int turns, bool collapseOlder)
        {
            var transcript = new TranscriptViewModel(collapseOlder);
            var reply = new StringBuilder();
            for (var paragraph = 0; paragraph < 3; paragraph++)
            {
                reply.Append("Paragraph ").Append(paragraph).Append(": the agent's reply with `inline code` and a **bold** word, long enough to wrap over a couple of lines in the column. ");
                reply.Append("The quick brown fox jumps over the lazy dog and keeps going for a while.\n\n");
            }

            var output = string.Join('\n', Enumerable.Range(0, 60).Select(line => $"line {line}: some tool output that is long enough to look like a real result"));
            var thinking = string.Join('\n', Enumerable.Range(0, 12).Select(line => $"thought {line}: weighing the next step against what the files said"));
            var at = DateTimeOffset.UtcNow;
            for (var turn = 0; turn < turns; turn++)
            {
                transcript.AddUserMessage($"Turn {turn}: please look at the files and tell me what you find.", at);
                var card = transcript.BeginTurn(at, closePrevious: true);
                card.AppendThinking(thinking);
                card.AppendText(reply.ToString());
                for (var tool = 0; tool < 6; tool++)
                {
                    var call = new ToolCall($"t{turn}-{tool}", tool % 2 == 0 ? "read_file" : "bash", $"{{\"command\":\"dir src{tool}\"}}");
                    card.AddTool(call);
                    card.CompleteTool(call, new ToolResult(output));
                }

                card.Finish(TimeSpan.FromSeconds(3), null);
            }

            return transcript;
        }

        /// <summary>
        /// Turns that differ the way real ones do: replies of one to eight paragraphs, one to eight tool calls, results of five to
        /// sixty lines, thinking of two to twelve lines (fixed seed, so every run builds the same transcript).
        /// </summary>
        private static TranscriptViewModel BuildVaried(int turns)
        {
            var transcript = new TranscriptViewModel(false);
            var random = new Random(20261118);
            var at = DateTimeOffset.UtcNow;
            for (var turn = 0; turn < turns; turn++)
            {
                transcript.AddUserMessage($"Turn {turn}: {string.Join(' ', Enumerable.Repeat("please look at the files and tell me what you find.", random.Next(1, 4)))}", at);
                var card = transcript.BeginTurn(at, closePrevious: true);
                if (random.Next(0, 3) > 0)
                {
                    card.AppendThinking(string.Join('\n', Enumerable.Range(0, random.Next(2, 13)).Select(line => $"thought {line}: weighing the next step against what the files said")));
                }

                var reply = new StringBuilder();
                for (var paragraph = 0; paragraph < random.Next(1, 9); paragraph++)
                {
                    reply.Append("Paragraph ").Append(paragraph).Append(": the agent's reply with `inline code` and a **bold** word, long enough to wrap over a couple of lines in the column. ");
                    reply.Append("The quick brown fox jumps over the lazy dog and keeps going for a while.\n\n");
                }

                card.AppendText(reply.ToString());
                for (var tool = 0; tool < random.Next(1, 9); tool++)
                {
                    var call = new ToolCall($"t{turn}-{tool}", tool % 2 == 0 ? "read_file" : "bash", $"{{\"command\":\"dir src{tool}\"}}");
                    card.AddTool(call);
                    card.CompleteTool(call, new ToolResult(string.Join('\n', Enumerable.Range(0, random.Next(5, 61)).Select(line => $"line {line}: some tool output that is long enough to look like a real result"))));
                }

                card.Finish(TimeSpan.FromSeconds(3), null);
            }

            return transcript;
        }

        private sealed class Probe(ListBox list, ScrollViewer scroller, TranscriptFollower follower, TranscriptViewModel transcript)
        {
            public ListBox List { get; } = list;
            public ScrollViewer Scroller { get; } = scroller;
            public TranscriptFollower Follower { get; } = follower;
            public TranscriptViewModel Transcript { get; } = transcript;
            public int OffsetWrites { get; set; }
        }

        private async Task<Probe> MountAsync(TranscriptViewModel transcript, bool pinned)
        {
            var box = NewList(transcript, pinned ? "pinned" : "virtual");
            var host = new Grid();
            host.Children.Add(box);
            var follower = TranscriptFollower.Attach(box)!;
            window.Content = host;
            window.UpdateLayout();
            await FramesAsync(3).ConfigureAwait(true);
            var scroller = box.GetVisualDescendants().OfType<ScrollViewer>().First();
            var probe = new Probe(box, scroller, follower, transcript);
            scroller.PropertyChanged += (_, e) =>
            {
                if (e.Property == ScrollViewer.OffsetProperty)
                {
                    probe.OffsetWrites++;
                }
            };
            return probe;
        }

        private async Task FramesAsync(int count)
        {
            for (var i = 0; i < count; i++)
            {
                await NextFrameAsync().ConfigureAwait(true);
            }
        }

        private static double? RowY(Probe probe, object row)
        {
            // A row lives inside its round, so the list has no container for it: find the realized visual that is bound to it.
            if (probe.List.ContainerFromItem(row) is { } container && container.TranslatePoint(default, probe.Scroller) is { } point)
            {
                return point.Y;
            }

            var visual = probe.List.GetVisualDescendants().OfType<Control>().FirstOrDefault(c => ReferenceEquals(c.DataContext, row));
            return visual?.TranslatePoint(default, probe.Scroller)?.Y;
        }

        /// <summary>The index of the round that hosts <paramref name="row"/>, which is what the list scrolls by.</summary>
        private static int RoundIndexOf(TranscriptViewModel transcript, object row)
        {
            for (var i = 0; i < transcript.Rounds.Count; i++)
            {
                if (transcript.Rounds[i].Rows.Contains(row))
                {
                    return i;
                }
            }

            return 0;
        }

        /// <summary>Scrolls until <paramref name="row"/>'s top sits at <paramref name="y"/> on screen (clamped by the extent).</summary>
        private async Task PlaceAsync(Probe probe, object row, double y)
        {
            probe.List.ScrollIntoView(RoundIndexOf(probe.Transcript, row));
            await FramesAsync(2).ConfigureAwait(true);
            for (var attempt = 0; attempt < 4; attempt++)
            {
                if (RowY(probe, row) is not { } now || Math.Abs(now - y) < 0.5)
                {
                    break;
                }

                probe.Scroller.Offset = new Vector(0, probe.Scroller.Offset.Y + (now - y));
                await FramesAsync(2).ConfigureAwait(true);
            }
        }

        /// <summary>One measured change: the watched thing's screen y before and after, the offset, the extent and the writes.</summary>
        private async Task<string> MeasureAsync(Probe probe, string label, Func<double?> watch, Action change, int frames = 3)
        {
            var before = watch();
            var topBefore = FirstVisibleRow(probe);
            var topIndexBefore = probe.Transcript.FlatRows().IndexOf(topBefore);
            var offset = probe.Scroller.Offset.Y;
            var extent = probe.Scroller.Extent.Height;
            var anchor = probe.Follower.Anchor;
            var (corrections, restores, giveUps, busy) = (anchor.Corrections, anchor.Restores, anchor.GiveUps, anchor.Busy);
            probe.OffsetWrites = 0;
            var watchTime = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                change();
            }
            catch (InvalidOperationException exception)
            {
                return $"{label}: could not act ({exception.Message})";
            }

            await NextFrameAsync().ConfigureAwait(true);
            var firstFrame = watchTime.Elapsed.TotalMilliseconds;
            var afterFirst = watch();
            await FramesAsync(frames - 1).ConfigureAwait(true);
            var after = watch();
            var drift = before is { } b && after is { } a ? F(a - b) : "lost";
            var top = FirstVisibleRow(probe);
            var shown = $"top row #{topIndexBefore}->#{probe.Transcript.FlatRows().IndexOf(top)} (old top now #{probe.Transcript.FlatRows().IndexOf(topBefore)})";
            var firstDrift = before is { } b1 && afterFirst is { } a1 ? F(a1 - b1) : "lost";
            return $"{label}: drift {drift} (first frame {firstDrift}, {F(firstFrame)} ms), {shown} (y {F(before ?? double.NaN)}->{F(after ?? double.NaN)}), offset {F(offset)}->{F(probe.Scroller.Offset.Y)}, extent {F(extent)}->{F(probe.Scroller.Extent.Height)}, offset writes {probe.OffsetWrites} (anchor {anchor.Corrections - corrections}, restores {anchor.Restores - restores}, give-ups {anchor.GiveUps - giveUps}, {F((anchor.Busy - busy).TotalMilliseconds)} ms in it)";
        }

        private static ToolRowViewModel Tool(TranscriptViewModel transcript, int turn, int index) =>
            ToolsOf(Card(transcript, turn)).Skip(index).First();

        /// <summary>Every tool call of a card, in arrival order: the calls live inside their groups.</summary>
        private static IEnumerable<ToolRowViewModel> ToolsOf(TurnCardViewModel card) =>
            card.Blocks.OfType<TurnToolGroupViewModel>().SelectMany(group => group.Tools);

        /// <summary>Every tool call of every card, in order: a call lives inside its group, not directly in the round's rows.</summary>
        private static IEnumerable<ToolRowViewModel> AllTools(TranscriptViewModel transcript) =>
            Cards(transcript).SelectMany(ToolsOf);

        /// <summary>Every card of a live transcript, in order (the round rows host them for a sub-agent only).</summary>
        private static IEnumerable<TurnCardViewModel> Cards(TranscriptViewModel transcript) =>
            transcript.Rounds.Select(r => r.Card).OfType<TurnCardViewModel>();

        private static TurnCardViewModel Card(TranscriptViewModel transcript, int turn) =>
            Cards(transcript).Skip(turn).First();

        /// <summary>The card's own tool row by index — works on a collapsed-older transcript, where the global
        /// row-skip cannot assume every turn's blocks are in the flat list.</summary>
        private static ToolRowViewModel ToolOf(TurnCardViewModel card, int index) =>
            ToolsOf(card).Skip(index).First();

        /// <summary>The first row whose top is on screen, below <paramref name="from"/>.</summary>
        private static object FirstVisibleRow(Probe probe, double from = 0) =>
            probe.List.GetRealizedContainers()
                .Select(container => (Container: container, Y: container.TranslatePoint(default, probe.Scroller)?.Y ?? double.NaN))
                .Where(pair => pair.Y >= from && pair.Y < probe.Scroller.Viewport.Height)
                .OrderBy(pair => pair.Y)
                .Select(pair => probe.List.ItemFromContainer(pair.Container)!)
                .First();

        public Task<string> AnchorAsync(string name, bool pinned) =>
            Dispatcher.UIThread.InvokeAsync(async () =>
            {
                // The turn-header toggle only exists in sub-agent transcripts now (a main-session turn has no header
                // row): the three header cases ride a collapsed-older transcript like subagent-new-turn, so the coverage
                // of "toggling a header must not move the content anchor" survives the layout change.
                // A main-session turn has no header row any more, so the surviving header case rides a collapsed-older
                // (sub-agent style) transcript.
                var probe = await MountAsync(BuildRich(40, collapseOlder: name == "turn-above"), pinned).ConfigureAwait(true);
                try
                {
                    if (name != "follow-expand")
                    {
                        probe.Follower.StopFollowing();
                    }

                    return await RunAnchorCaseAsync(probe, name).ConfigureAwait(true);
                }
                finally
                {
                    window.Content = null;
                }
            });

        private async Task<string> RunAnchorCaseAsync(Probe probe, string name)
        {
            var transcript = probe.Transcript;
            var results = new List<string>();
            switch (name)
            {
                case "rows-above":
                {
                    // Tool rows two turns above the screen open and close (programmatic); the first row on screen is watched.
                    var tool = Tool(transcript, 20, 0);
                    await PlaceAsync(probe, tool, 40).ConfigureAwait(true);
                    var watched = FirstVisibleRow(probe);
                    results.Add(await MeasureAsync(probe, "open 6 above", () => RowY(probe, watched), () =>
                    {
                        for (var i = 0; i < 6; i++)
                        {
                            Tool(transcript, 18, i).IsExpanded = true;
                        }
                    }).ConfigureAwait(true));
                    break;
                }

                case "turn-above":
                {
                    Card(transcript, 19).IsExpanded = true;
                    var card = Card(transcript, 20);
                    card.IsExpanded = true;
                    await FramesAsync(2).ConfigureAwait(true);
                    var tool = ToolOf(card, 0);
                    await PlaceAsync(probe, tool, 40).ConfigureAwait(true);
                    var watched = FirstVisibleRow(probe);
                    results.Add(await MeasureAsync(probe, "collapse turn above", () => RowY(probe, watched), () => Card(transcript, 19).IsExpanded = false).ConfigureAwait(true));
                    results.Add(await MeasureAsync(probe, "expand turn above", () => RowY(probe, watched), () => Card(transcript, 19).IsExpanded = true).ConfigureAwait(true));
                    break;
                }

                case "follow-expand":
                {
                    // Following the reply at the bottom; the last tool row opens by itself. The list must end at the bottom.
                    await FramesAsync(3).ConfigureAwait(true);
                    var last = AllTools(transcript).Last();
                    last.IsExpanded = true;
                    await FramesAsync(4).ConfigureAwait(true);
                    var sv = probe.Scroller;
                    results.Add($"following {probe.Follower.IsFollowing}, distance from bottom {F(sv.Extent.Height - sv.Viewport.Height - sv.Offset.Y)}");
                    break;
                }

            }

            return string.Join(" | ", results);
        }
    }
}
