using System.Diagnostics;
using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Templates;
using Avalonia.Markup.Xaml.Templates;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Forge.Abstractions;
using Forge.App.Localization;
using Forge.App.ViewModels;
using Forge.App.Views;

namespace Forge.Bench;

/// <summary>
/// The transcript list on a real Avalonia window, with a synthetic conversation of N turns (each: a user message, an agent
/// card with a markdown reply and six tool rows). It compares the two panels the list can use: the default virtualizing
/// panel and a plain <c>StackPanel</c>. <c>open</c> is the time from handing the list its items until the second frame after
/// has been presented; <c>scroll</c> is one jump of the scroll position to each of eight offsets across the extent, each
/// followed by a presented frame. Both run on the same window size, so the numbers are comparable with each other only.
/// Run with <c>--filter transcript.</c> (the Avalonia platform is set up once per process, so not together with the composer scenarios).
/// </summary>
internal static partial class TranscriptScenarios
{
    private static readonly int[] TurnCounts = [20, 100, 400];

    public static IEnumerable<BenchScenario> Create(Fixtures fixtures)
    {
        foreach (var turns in TurnCounts)
        {
            foreach (var stack in new[] { "stack", "virtual" })
            {
                yield return Open(turns, stack);
                yield return Scroll(turns, stack);
            }

            // The app's panel (keeps its first visible row in place) on its own, and the app as it is: that panel with the
            // follower and content anchor attached (paused), which must not fight the jumps.
            yield return Scroll(turns, "anchored");
            yield return Open(turns, "pinned");
            yield return Scroll(turns, "pinned");
        }

        yield return PanelFields();

        foreach (var scenario in AnchorScenarios().Concat(FrameScenarios()))
        {
            yield return scenario;
        }
    }

    /// <summary>Fails when an Avalonia upgrade has taken away the private fields <see cref="TranscriptStackPanel"/> steers by.</summary>
    private static BenchScenario PanelFields() =>
        new(
            "transcript.guard.panel-fields",
            "transcript",
            Heavy: false,
            PrepareAsync: async cancellationToken =>
            {
                var rig = await Rig.GetAsync(cancellationToken).ConfigureAwait(false);
                return new BenchCase
                {
                    Warmup = 0,
                    Iterations = 1,
                    Note = "TranscriptStackPanel can steer and price: _scrollToIndex, _scrollToElement and _lastEstimatedElementSizeU exist in the pinned Avalonia",
                    Run = async _ =>
                    {
                        if (!await rig.CanSteerAsync().ConfigureAwait(false))
                        {
                            throw new InvalidOperationException("VirtualizingStackPanel lost _scrollToIndex, _scrollToElement or _lastEstimatedElementSizeU: TranscriptStackPanel has fallen back to the stock panel (or stock extent); re-derive it for this Avalonia.");
                        }

                        return new OpResult(Items: 1, Observation: "fields present");
                    },
                };
            });

    private static string Name(string what, int turns, string stack) => $"transcript.{stack}.{turns}.{what}";

    private static BenchScenario Open(int turns, string stack) =>
        new(
            Name("open", turns, stack),
            "transcript",
            Heavy: false,
            PrepareAsync: async cancellationToken =>
            {
                var rig = await Rig.GetAsync(cancellationToken).ConfigureAwait(false);
                return new BenchCase
                {
                    Warmup = 1,
                    Iterations = turns >= 400 ? 5 : 10,
                    ProcessWideAllocation = true,
                    WatchMemory = true,
                    Note = $"{turns} turns, {stack}; items handed over to the second presented frame",
                    Run = async _ =>
                    {
                        var (milliseconds, visuals, modelMb, viewMb) = await rig.OpenAsync(turns, stack).ConfigureAwait(false);
                        return new OpResult(Items: turns, Observation: $"first-layout {milliseconds.ToString("F0", CultureInfo.InvariantCulture)} ms, {visuals} visuals, heap: view models {modelMb.ToString("F1", CultureInfo.InvariantCulture)} MB + views {viewMb.ToString("F1", CultureInfo.InvariantCulture)} MB");
                    },
                };
            });

    private static BenchScenario Scroll(int turns, string stack) =>
        new(
            Name("scroll", turns, stack),
            "transcript",
            Heavy: false,
            PrepareAsync: async cancellationToken =>
            {
                var rig = await Rig.GetAsync(cancellationToken).ConfigureAwait(false);
                await rig.PrepareScrollAsync(turns, stack).ConfigureAwait(false);
                return new BenchCase
                {
                    Warmup = 1,
                    Iterations = 8,
                    ProcessWideAllocation = true,
                    Note = $"{turns} turns, {stack}; eight jumps across the extent, a presented frame after each",
                    Run = async _ =>
                    {
                        var drift = await rig.ScrollAsync().ConfigureAwait(false);
                        return new OpResult(Items: 8, Observation: $"thumb/content drift {drift.ToString("F0", CultureInfo.InvariantCulture)} px (max over jumps)");
                    },
                };
            });

    private sealed partial class Rig
    {
        private static Task<Rig>? starting;
        private Window window = null!;
        private ListBox? list;

        public static Task<Rig> GetAsync(CancellationToken cancellationToken) => starting ??= StartAsync(cancellationToken);

        private static async Task<Rig> StartAsync(CancellationToken cancellationToken)
        {
            var ready = new TaskCompletionSource<Rig>(TaskCreationOptions.RunContinuationsAsynchronously);
            var thread = new Thread(() =>
            {
                try
                {
                    Forge.App.Program.BuildAvaloniaApp().SetupWithoutStarting();
                    Loc.EnsureRegistered();
                    var rig = new Rig();
                    rig.window = new Window { Width = 900, Height = 900, ShowInTaskbar = false, Title = "forge-bench transcript" };
                    rig.window.Show();
                    Dispatcher.UIThread.RunJobs();
                    ready.SetResult(rig);
                    Dispatcher.UIThread.MainLoop(CancellationToken.None);
                }
                catch (Exception exception)
                {
                    ready.TrySetException(exception);
                }
            })
            {
                IsBackground = true,
                Name = "avalonia-ui",
            };
            if (OperatingSystem.IsWindows())
            {
                thread.SetApartmentState(ApartmentState.STA);
            }

            thread.Start();
            return await ready.Task.WaitAsync(TimeSpan.FromSeconds(60), cancellationToken).ConfigureAwait(false);
        }

        public Task<bool> CanSteerAsync() => Dispatcher.UIThread.InvokeAsync(TranscriptStackPanel.CanSteer).GetTask();

        private Task NextFrameAsync()
        {
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            window.RequestAnimationFrame(_ => done.TrySetResult());
            return done.Task;
        }

        private static TranscriptViewModel Build(int turns)
        {
            var transcript = new TranscriptViewModel(collapseOlderTurns: false);
            var reply = new StringBuilder();
            for (var paragraph = 0; paragraph < 4; paragraph++)
            {
                reply.Append("Paragraph ").Append(paragraph).Append(": this is the agent's reply with `inline code`, a **bold** word and some more text so that it wraps over a couple of lines in the transcript column. ");
                reply.Append("The quick brown fox jumps over the lazy dog and keeps going for a while.\n\n");
            }

            reply.Append("- first item\n- second item\n- third item\n");
            var output = string.Join('\n', Enumerable.Range(0, 40).Select(line => $"line {line}: some tool output that is long enough to look like a real result"));
            var at = DateTimeOffset.UtcNow;
            for (var turn = 0; turn < turns; turn++)
            {
                transcript.AddUserMessage($"Turn {turn}: please look at the files and tell me what you find, with a short summary.", at);
                var card = transcript.BeginTurn(at, closePrevious: true);
                card.AppendText(reply.ToString());
                for (var tool = 0; tool < 6; tool++)
                {
                    var call = new ToolCall($"t{turn}-{tool}", tool % 2 == 0 ? "read_file" : "bash", $"{{\"path\":\"src/File{tool}.cs\",\"command\":\"dir src\"}}");
                    card.AddTool(call);
                    card.CompleteTool(call, new ToolResult(output));
                }

                card.Finish(TimeSpan.FromSeconds(3), null);
            }

            return transcript;
        }

        /// <summary>Bench switch: the stock ListBox, whose rows build their template every time (the cases ending in "-unpooled").</summary>
        private static bool Unpooled;

        private ListBox NewList(TranscriptViewModel transcript, string stack)
        {
            ListBox box = Unpooled ? new ListBox() : new Forge.App.Views.TranscriptListBox();
            box.Padding = new Thickness(8);
            box.SelectionMode = SelectionMode.Single;
            box.AutoScrollToSelectedItem = false;
            box.ItemsSource = transcript.Rounds;
            ScrollViewer.SetHorizontalScrollBarVisibility(box, Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled);
            if (stack == "stack")
            {
                box.ItemsPanel = new ItemsPanelTemplate { Content = new Func<IServiceProvider?, TemplateResult<Control>?>(_ => new TemplateResult<Control>(new StackPanel(), new Avalonia.Controls.NameScope())) };
            }
            else if (stack == "pinned")
            {
                box.ItemsPanel = new ItemsPanelTemplate { Content = new Func<IServiceProvider?, TemplateResult<Control>?>(_ => new TemplateResult<Control>(new TranscriptStackPanel(), new Avalonia.Controls.NameScope())) };
            }

            return box;
        }

        private static int CountVisuals(Visual root)
        {
            var count = 1;
            foreach (var child in root.GetVisualChildren())
            {
                count += CountVisuals(child);
            }

            return count;
        }

        public Task<(double Milliseconds, int Visuals, double ModelMb, double ViewMb)> OpenAsync(int turns, string stack) =>
            Dispatcher.UIThread.InvokeAsync(async () =>
            {
                var baseline = GC.GetTotalMemory(true);
                var transcript = Build(turns);
                var withModel = GC.GetTotalMemory(true);
                var box = NewList(transcript, stack);
                var watch = Stopwatch.StartNew();
                window.Content = box;
                window.UpdateLayout();
                await NextFrameAsync().ConfigureAwait(true);
                await NextFrameAsync().ConfigureAwait(true);
                var elapsed = watch.Elapsed.TotalMilliseconds;
                var visuals = CountVisuals(box);
                var withView = GC.GetTotalMemory(true);
                GC.KeepAlive(transcript);
                window.Content = null;
                return (elapsed, visuals, (withModel - baseline) / 1048576d, (withView - withModel) / 1048576d);
            });

        public Task PrepareScrollAsync(int turns, string stack) =>
            Dispatcher.UIThread.InvokeAsync(async () =>
            {
                list = NewList(Build(turns), stack == "anchored" ? "pinned" : stack);
                if (stack == "anchored")
                {
                    var host = new Grid();
                    host.Children.Add(list);
                    window.Content = host;
                    Forge.App.Views.TranscriptFollower.Attach(list)!.StopFollowing();
                }
                else
                {
                    window.Content = list;
                }

                window.UpdateLayout();
                await NextFrameAsync().ConfigureAwait(true);
                await NextFrameAsync().ConfigureAwait(true);
            });

        /// <summary>Jumps to eight fractions of the extent; the drift is how far the offset ended from where it was put after the frame.</summary>
        public Task<double> ScrollAsync() =>
            Dispatcher.UIThread.InvokeAsync(async () =>
            {
                var scroller = list!.GetVisualDescendants().OfType<ScrollViewer>().First();
                var worst = 0d;
                for (var step = 0; step < 8; step++)
                {
                    var target = (scroller.Extent.Height - scroller.Viewport.Height) * (step % 2 == 0 ? step / 7d : 1 - step / 7d);
                    scroller.Offset = new Vector(0, target);
                    await NextFrameAsync().ConfigureAwait(true);
                    worst = Math.Max(worst, Math.Abs(scroller.Offset.Y - target));
                }

                return worst;
            });
    }
}
