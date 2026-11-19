using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Forge.App.ViewModels;

namespace Forge.Bench;

/// <summary>
/// UI-thread cost of one frame's layout for what the user does to the transcript: wheel scrolling, dragging the scroll bar,
/// opening and closing rows. Each step changes the list and then runs one layout pass synchronously (measure, arrange and the
/// LayoutUpdated handlers: the follower and the content anchor), timed with a stopwatch, so the numbers are the work a frame
/// has to fit in and carry no vsync wait. At 90 FPS a frame is 11.1 ms; the report gives the share of steps over that, with the
/// row types the slowest steps had to build. Rendering on the render thread is not in these numbers.
/// </summary>
internal static partial class TranscriptScenarios
{
    private static readonly string[] FrameCases = ["wheel", "thumb", "thumb-stock", "toggle", "toggle-text", "extent", "extent-varied", "extent-varied-stock","thumb-gap", "thumb-gap-stock", "thumb-gap-varied", "thumb-gap-long", "thumb-gap-long-stock", "rowcost", "rowcost-composer", "popup-styles", "scoped-sheets", "gutter", "thumb-unpooled", "thumb-follow", "thumb-follow-stock", "thumb-follow-long", "thumb-follow-long-stock", "pool-pixels", "reuse", "reuse-unpooled", "rebind", "attach", "attach-plain"];

    private static IEnumerable<BenchScenario> FrameScenarios() => FrameCases.Select(FrameCase);

    private static BenchScenario FrameCase(string name) =>
        new(
            $"transcript.frame.{name}",
            "transcript",
            Heavy: false,
            PrepareAsync: async cancellationToken =>
            {
                var rig = await Rig.GetAsync(cancellationToken).ConfigureAwait(false);
                return new BenchCase
                {
                    Warmup = 1,
                    Iterations = 3,
                    Note = "UI-thread layout ms per step; 11.1 ms is one frame at 90 FPS",
                    Run = async _ =>
                    {
                        var report = await rig.FrameAsync(name).ConfigureAwait(false);
                        Console.Error.WriteLine($"frame {name}: {report}");
                        return new OpResult(Items: 1, Observation: report);
                    },
                };
            });

    private sealed partial class Rig
    {
        private const double FrameBudget = 11.1;

        private double StepPauseMs, StepAllocKb, StepMs;
        private int StepRows;

        private static string F3(double value) => value.ToString("F3", CultureInfo.InvariantCulture);

        /// <summary>The list's own padding and the gaps between rows are up to ~9 DIP of the viewport no row covers; more than this is a blank band.</summary>
        private const double BlankBand = 20;

        public Task<string> FrameAsync(string name) =>
            Dispatcher.UIThread.InvokeAsync(async () =>
            {
                if (name.StartsWith("attach", StringComparison.Ordinal))
                {
                    return await AttachAsync(name == "attach-plain").ConfigureAwait(true);
                }

                if (name == "pool-pixels")
                {
                    return await PoolPixelsAsync().ConfigureAwait(true);
                }

                if (name == "rebind")
                {
                    return await RebindAsync().ConfigureAwait(true);
                }

                if (name == "gutter")
                {
                    return await GutterPixelsAsync().ConfigureAwait(true);
                }

                if (name == "scoped-sheets")
                {
                    return ScopedSheets();
                }

                if (name == "popup-styles")
                {
                    return await PopupStylesAsync().ConfigureAwait(true);
                }

                if (name is "rowcost" or "rowcost-composer")
                {
                    return await RowCostAsync(name == "rowcost-composer").ConfigureAwait(true);
                }

                Unpooled = name.EndsWith("-unpooled");
                var probe = await MountAsync(name.StartsWith("extent-varied", StringComparison.Ordinal) ? BuildVaried(40) : name == "thumb-gap-varied" ? BuildVaried(600) : BuildRich(name.Contains("long") ? 200 : 40, false), pinned: !name.EndsWith("-stock")).ConfigureAwait(true);
                try
                {
                    probe.Follower.StopFollowing();
                    var steps = new List<(double Ms, int Built, string Types, int Gcs)>();
                    var built = new List<string>();
                    probe.List.ContainerPrepared += (_, e) => built.Add(probe.Transcript.FlatRows()[e.Index].GetType().Name.Replace("ViewModel", ""));
                    var transcript = probe.Transcript;
                    switch (name)
                    {
                        case "wheel":
                        case "thumb":
                        case "thumb-unpooled":
                        case "thumb-stock":
                        {
                            var start = Tool(transcript, 20, 0);
                            await PlaceAsync(probe, start, 0).ConfigureAwait(true);
                            var delta = name == "wheel" ? 48d : 600d;
                            var count = name == "wheel" ? 300 : 40;
                            for (var i = 0; i < count; i++)
                            {
                                Step(probe, steps, built, () => probe.Scroller.Offset = new Vector(0, probe.Scroller.Offset.Y + delta));
                            }

                            break;
                        }

                        case "reuse":
                        case "reuse-unpooled":
                        {
                            var start = Tool(transcript, 20, 0);
                            await PlaceAsync(probe, start, 0).ConfigureAwait(true);
                            var containers = new HashSet<object>(ReferenceEqualityComparer.Instance);
                            var children = new Dictionary<string, HashSet<object>>();
                            var presented = new Dictionary<string, int>();
                            var childOfContainer = new Dictionary<object, object?>(ReferenceEqualityComparer.Instance);
                            var kept = 0;
                            var swapped = 0;
                            for (var i = 0; i < 40; i++)
                            {
                                Step(probe, steps, built, () => probe.Scroller.Offset = new Vector(0, probe.Scroller.Offset.Y + 600));
                                foreach (var item in probe.List.GetVisualDescendants().OfType<ListBoxItem>())
                                {
                                    containers.Add(item);
                                    var child = item.Presenter?.Child;
                                    if (child is null)
                                    {
                                        continue;
                                    }

                                    var kind = item.DataContext?.GetType().Name.Replace("ViewModel", "") ?? "?";
                                    if (!children.TryGetValue(kind, out var set))
                                    {
                                        children[kind] = set = new HashSet<object>(ReferenceEqualityComparer.Instance);
                                    }

                                    if (set.Add(child))
                                    {
                                        presented[kind] = presented.GetValueOrDefault(kind) + 1;
                                    }

                                    if (childOfContainer.TryGetValue(item, out var before))
                                    {
                                        if (ReferenceEquals(before, child))
                                        {
                                            kept++;
                                        }
                                        else
                                        {
                                            swapped++;
                                        }
                                    }

                                    childOfContainer[item] = child;
                                }
                            }

                            var typeNames = string.Join(", ", children.Select(pair => $"{pair.Key}: {pair.Value.Count} distinct contents"));
                            return $"{built.Count} rows built over 40 jumps, {containers.Count} distinct ListBoxItem containers; content controls built per type: {typeNames}; per container across steps: content kept {kept}, replaced {swapped}; built by type: {string.Join(", ", built.GroupBy(b => b).Select(g => $"{g.Key}x{g.Count()}"))}";
                        }

                        case "extent":
                        case "extent-varied":
                        case "extent-varied-stock":
                            return await ExtentAsync(probe, guard: name == "extent-varied").ConfigureAwait(true);

                        case "thumb-follow":
                        case "thumb-follow-long":
                        case "thumb-follow-stock":
                        case "thumb-follow-long-stock":
                            return await ThumbFollowAsync(probe, guard: !name.EndsWith("-stock")).ConfigureAwait(true);

                        case "thumb-gap":
                        case "thumb-gap-stock":
                        case "thumb-gap-varied":
                        case "thumb-gap-long":
                        case "thumb-gap-long-stock":
                            return await ThumbGapAsync(probe, !name.EndsWith("-stock")).ConfigureAwait(true);

                        case "toggle":
                        case "toggle-text":
                        {
                            var tool = Tool(transcript, 20, 2);
                            await PlaceAsync(probe, tool, 300).ConfigureAwait(true);
                            var thinking = transcript.FlatRows().OfType<TurnThinkingViewModel>().Skip(20).First();
                            for (var i = 0; i < 40; i++)
                            {
                                if (name == "toggle")
                                {
                                    Step(probe, steps, built, () => tool.IsExpanded = !tool.IsExpanded);
                                }
                                else
                                {
                                    Step(probe, steps, built, () => thinking.IsExpanded = !thinking.IsExpanded);
                                }
                            }

                            break;
                        }
                    }

                    Console.Error.WriteLine($"frame {name}: over the steps that built rows: {StepRows} rows, {F(StepMs)} ms in them, of which GC pause {F(StepPauseMs)} ms; {F(StepAllocKb / Math.Max(1, StepRows))} KB allocated per built row, {F(StepMs / Math.Max(1, StepRows))} ms per built row");
                    return Summarize(steps, probe);
                }
                finally
                {
                    window.Content = null;
                    Unpooled = false;
                }
            });

        /// <summary>
        /// The transcript list keeps the controls of rows that scrolled out and gives them to the next row of the type. This checks
        /// that nothing of the old row is left: the same transcript, with two tool rows opened, is mounted in the stock list and
        /// in the pooled list, both are scrolled through (the pool fills and is drawn from), and then the same rows are placed at
        /// the same place and rendered to pixels. Every pixel must be the same (but the scrollbar's strip, whose thumb follows the estimated extent). Also: each realized row's controls are bound to
        /// that row, the pool stays within its bounds, and it is emptied when the list's source is replaced.
        /// </summary>
        private async Task<string> PoolPixelsAsync()
        {
            var transcript = BuildRich(40, false);
            var tools = AllTools(transcript).ToList();
            var samples = new List<object>
            {
                transcript.FlatRows().OfType<UserMessageViewModel>().Skip(3).First(),
                tools[3],
                tools[4],
                transcript.FlatRows().OfType<TurnThinkingViewModel>().Skip(7).First(),
                tools[9],
                tools[10],
                // The main-session transcript no longer carries turn-card rows (the turn header only exists in a
                // sub-agent transcript now, where the anchor scenarios mount it); the row type this sample has to cover
                // is the divider — the new template whose pooled reuse must render pixel-identically.
                transcript.FlatRows().OfType<TurnDividerViewModel>().Skip(12).First(),
                tools[30],
                tools[31],
            };
            tools[3].IsExpanded = true;
            tools[9].IsExpanded = true;
            var failures = new List<string>();
            var shots = new Dictionary<bool, List<Func<int, int, Avalonia.Media.Color>>>();
            var size = new PixelSize(0, 0);
            var pooledCount = 0;
            try
            {
                foreach (var unpooled in new[] { true, false })
                {
                    Unpooled = unpooled;
                    var probe = await MountAsync(transcript, pinned: true).ConfigureAwait(true);
                    probe.Follower.StopFollowing();
                    await PlaceAsync(probe, tools[3], 0).ConfigureAwait(true);
                    for (var i = 0; i < 40; i++)
                    {
                        probe.Scroller.Offset = new Vector(0, probe.Scroller.Offset.Y + 600);
                        window.UpdateLayout();
                        if (!unpooled)
                        {
                            foreach (var item in probe.List.GetVisualDescendants().OfType<ListBoxItem>())
                            {
                                if (item.Presenter?.Child is { } child && !ReferenceEquals(child.DataContext, item.DataContext))
                                {
                                    failures.Add($"jump {i}: a {item.DataContext?.GetType().Name} row shows controls bound to {child.DataContext?.GetType().Name}");
                                }
                            }
                        }
                    }

                    var list = new List<Func<int, int, Avalonia.Media.Color>>();
                    // The jumps can reach the end of an accurate extent, which resumes following; the rows are placed by hand below.
                    probe.Follower.StopFollowing();
                    size = new PixelSize((int)probe.List.Bounds.Width, (int)probe.List.Bounds.Height);
                    foreach (var row in samples)
                    {
                        await PlaceAsync(probe, row, 40).ConfigureAwait(true);
                        list.Add(Capture(probe.List, size.Width, size.Height));
                    }

                    shots[unpooled] = list;
                    if (!unpooled)
                    {
                        pooledCount = ((Forge.App.Views.TranscriptListBox)probe.List).PooledCount;
                        if (pooledCount > 4 * 24)
                        {
                            failures.Add($"the pool holds {pooledCount} controls, above its bound of 96");
                        }

                        probe.List.ItemsSource = new List<object>();
                        window.UpdateLayout();
                        var left = ((Forge.App.Views.TranscriptListBox)probe.List).PooledCount;
                        if (left != 0)
                        {
                            failures.Add($"{left} controls stay pooled after the source was replaced");
                        }
                    }
                }

                for (var i = 0; i < samples.Count; i++)
                {
                    var different = 0;
                    var first = (-1, -1);
                    for (var y = 0; y < size.Height; y++)
                    {
                        for (var x = 0; x < size.Width - 24; x++)
                        {
                            if (shots[true][i](x, y) != shots[false][i](x, y))
                            {
                                different++;
                                if (first.Item1 < 0)
                                {
                                    first = (x, y);
                                }
                            }
                        }
                    }

                    if (different > 0)
                    {
                        failures.Add($"{samples[i].GetType().Name.Replace("ViewModel", "")} #{i}: {different} pixels differ from the stock list, first at {first}");
                    }
                }

                if (failures.Count > 0)
                {
                    throw new InvalidOperationException("pooled rows: " + string.Join("; ", failures.Take(6)));
                }

                return $"{samples.Count} rows render pixel for pixel like the stock list after the pool was used ({pooledCount} controls pooled)";
            }
            finally
            {
                Unpooled = false;
                window.Content = null;
            }
        }

        /// <summary>
        /// The ceiling of reusing a row's controls: one built row, attached to the window, is given the next row of its type as data
        /// context over and over (bindings re-evaluate, nothing is built), against a fresh build of the same row type. Per row type:
        /// ms and KB for the rebind, and for the fresh build (the template, attached to the window, laid out).
        /// </summary>
        private async Task<string> RebindAsync()
        {
            var transcript = BuildRich(40, false);
            var parts = new List<string>();
            Control? BuildRow(object data)
            {
                foreach (var candidate in Application.Current!.DataTemplates)
                {
                    if (candidate.Match(data))
                    {
                        return candidate.Build(data);
                    }
                }

                return null;
            }

            foreach (var group in transcript.FlatRows().GroupBy(r => r.GetType()))
            {
                var rows = group.ToList();
                if (rows.Count < 8)
                {
                    continue;
                }

                var host = new StackPanel { Width = 700 };
                window.Content = host;
                window.UpdateLayout();
                double Run(bool fresh, out double kb)
                {
                    var total = 0d;
                    var kbTotal = 0d;
                    var n = 0;
                    var current = fresh ? null : BuildRow(rows[0]);
                    if (current is not null)
                    {
                        host.Children.Add(current);
                        current.DataContext = rows[0];
                        window.UpdateLayout();
                    }

                    for (var round = 0; round < 3; round++)
                    {
                        for (var i = 1; i < rows.Count; i++)
                        {
                            var allocBefore = GC.GetAllocatedBytesForCurrentThread();
                            var started = Stopwatch.GetTimestamp();
                            if (fresh)
                            {
                                host.Children.Clear();
                                var control = BuildRow(rows[i])!;
                                control.DataContext = rows[i];
                                host.Children.Add(control);
                            }
                            else
                            {
                                current!.DataContext = rows[i];
                            }

                            window.UpdateLayout();
                            var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                            if (round > 0)
                            {
                                total += elapsed;
                                kbTotal += (GC.GetAllocatedBytesForCurrentThread() - allocBefore) / 1024d;
                                n++;
                            }
                        }
                    }

                    host.Children.Clear();
                    kb = kbTotal / n;
                    return total / n;
                }

                var freshMs = Run(true, out var freshKb);
                var rebindMs = Run(false, out var rebindKb);
                parts.Add($"{group.Key.Name.Replace("ViewModel", "")}: build {F(freshMs)} ms / {F(freshKb)} KB, rebind {F(rebindMs)} ms / {F(rebindKb)} KB");
            }

            window.Content = null;
            return string.Join(" | ", parts);
        }

        /// <summary>
        /// What building one row costs, by row type (with <c>composerSheetAbove</c>, as if the composer's style sheet still sat above the list): a list of only that type's rows jumps to places it has not built, and the layout
        /// of each jump is timed (UI thread, synchronous) and divided by the rows it built. The first jump of every type is left out (JIT).
        /// </summary>
        private async Task<string> RowCostAsync(bool composerSheetAbove = false)
        {
            var transcript = BuildRich(40, false);
            var parts = new List<string>();
            foreach (var group in transcript.FlatRows().GroupBy(r => r.GetType()))
            {
                var rows = group.ToList();
                var box = NewList(transcript, "pinned");
                box.ItemsSource = rows;
                if (composerSheetAbove)
                {
                    // How SessionView held the composer's rules before they moved into the composer's own subtree: on the view that
                    // contains the transcript, so every row control of a matching type paid a match for each of them.
                    #pragma warning disable IL2026 // The bench is not trimmed; this loads the sheet the way the old XAML held it.
                    box.Styles.Add(new Avalonia.Markup.Xaml.Styling.StyleInclude(new Uri("avares://Forge.App/")) { Source = new Uri("avares://Forge.App/Resources/ComposerStyles.axaml") });
#pragma warning restore IL2026
                }

                window.Content = box;
                window.UpdateLayout();
                await NextFrameAsync().ConfigureAwait(true);
                var scroller = box.GetVisualDescendants().OfType<ScrollViewer>().First();
                var built = 0;
                box.ContainerPrepared += (_, _) => built++;
                var ms = new List<double>();
                var counts = new List<int>();
                var kb = 0d;
                var kbRows = 0;
                var step = Math.Max(scroller.Viewport.Height * 1.5, 1200);
                for (var jump = 0; jump < 24; jump++)
                {
                    var limit = scroller.Extent.Height - scroller.Viewport.Height;
                    var target = (jump * step) % Math.Max(limit, 1);
                    built = 0;
                    var allocBefore = GC.GetAllocatedBytesForCurrentThread();
                    var started = Stopwatch.GetTimestamp();
                    scroller.Offset = new Vector(0, target);
                    window.UpdateLayout();
                    var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                    if (jump > 0 && built > 0)
                    {
                        ms.Add(elapsed);
                        counts.Add(built);
                        kb += (GC.GetAllocatedBytesForCurrentThread() - allocBefore) / 1024d;
                        kbRows += built;
                    }
                }

                window.Content = null;
                parts.Add(ms.Count == 0
                    ? $"{group.Key.Name.Replace("ViewModel", "")}: nothing built"
                    : $"{group.Key.Name.Replace("ViewModel", "")} (n={rows.Count}): {F(ms.Sum() / counts.Sum())} ms per row, {F(counts.Average())} rows per jump, {F(ms.Average())} ms per jump, {F(kb / Math.Max(1, kbRows))} KB per row");
            }

            return string.Join(" | ", parts);
        }

        /// <summary>
        /// Every view that includes one of the scoped style sheets builds and loads its sheets (a wrong path would only fail
        /// when the panel first opens), and a view that is told to use a sheet really holds it.
        /// </summary>
        /// <summary>
        /// The self-drawn timeline gutter, checked on pixels: the axis is the 1 px line at x = 7 (the node's centre, the node
        /// being a 14 px slot on the left of the 26 px column), the node's icon ink stays inside its 14 x 14 slot 6 px from the
        /// top, the node-less header draws one line through the slot, the still waiting ring reaches beyond the slot, and the
        /// icon takes the theme's colours. Fails with the first pixels that are wrong.
        /// </summary>
        private async Task<string> GutterPixelsAsync()
        {
            const int Width = 26;
            const int Height = 60;
            var reduced = Forge.App.ViewModels.TreeMotion.ReducedMotion;
            try
            {
                Forge.App.ViewModels.TreeMotion.ReducedMotion = true;
                // The middle row of three: a node above and one below it, so the row decides top and bottom lines itself.
                var middle = await MiddleGutterAsync(role: Forge.App.Views.TimelineRowRole.Item, Height).ConfigureAwait(true);
                var gutter = middle.Gutter;
                var host = middle.Host;
                gutter.TryFindResource("BorderBrush", gutter.ActualThemeVariant, out var border);
                gutter.TryFindResource("TextMutedBrush", gutter.ActualThemeVariant, out var muted);
                var line = ((Avalonia.Media.ISolidColorBrush)border!).Color;
                var ink = ((Avalonia.Media.ISolidColorBrush)muted!).Color;

                var node = Capture(gutter, Width, Height);

                // What the templated gutter drew, as references: its axis was a 1 px Border with a 6.5 left margin (layout rounding
                // decides the column), its node a FluentIcon in a 14 x 14 box, 6 px from the top.
                gutter.TryFindResource("IconPerson", gutter.ActualThemeVariant, out var person);
                var reference = new Grid { Width = Width, Height = Height };
                reference.Children.Add(new Border { Width = 1, Margin = new Thickness(6.5, 0, 0, 0), HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left, Background = (Avalonia.Media.IBrush)border!, Height = Height });
                window.Content = reference;
                window.UpdateLayout();
                var axis = Capture(reference, Width, Height);
                                reference.Children.Clear();
                reference.Children.Add(new Forge.App.Workbench.Controls.FluentIcon { Data = (Avalonia.Media.Geometry)person!, Foreground = (Avalonia.Media.IBrush)muted!, Width = 14, Height = 14, Margin = new Thickness(0, 6, 0, 0), HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top });
                window.UpdateLayout();
                var templated = Capture(reference, Width, Height);

                var failures = new List<string>();
                void Expect(string what, bool ok)
                {
                    if (!ok)
                    {
                        failures.Add(what);
                    }
                }

                // The axis the template drew is the reference (at a fractional device scale the column straddles two pixels).
                bool Same(Avalonia.Media.Color a, Avalonia.Media.Color b) => Math.Abs(a.A - b.A) <= 8 && (a.A < 20 || (Math.Abs(a.R - b.R) <= 8 && Math.Abs(a.G - b.G) <= 8 && Math.Abs(a.B - b.B) <= 8));
                bool AxisOnly(Func<int, int, Avalonia.Media.Color> drawn, int top, int bottom) => Enumerable.Range(top, bottom - top).All(y => Enumerable.Range(0, Width).All(x => Same(drawn(x, y), axis(x, y))));
                Expect("top line, y=0..5, and nothing else beside it", AxisOnly(node, 0, 6));
                Expect("bottom line, y=20..59, and nothing else beside it", AxisOnly(node, 20, Height));
                var inkPixels = Enumerable.Range(6, 14).Sum(y => Enumerable.Range(0, 14).Count(x => node(x, y).A > 40));
                Expect("nothing but the axis beside the node slot", Enumerable.Range(6, 14).All(y => Enumerable.Range(14, Width - 14).All(x => node(x, y).A == 0)));

                Expect("icon has ink", inkPixels > 20);
                var worst = 0;
                for (var y = 6; y < 20; y++)
                {
                    for (var x = 0; x < 14; x++)
                    {
                        var drawn = node(x, y);
                        var was = templated(x, y);
                        worst = Math.Max(worst, Math.Max(Math.Abs(drawn.A - was.A), Math.Max(Math.Abs(drawn.R - was.R), Math.Abs(drawn.G - was.G))));
                    }
                }

                Expect($"icon pixels equal the templated icon's (worst channel difference {worst})", worst <= 8);
                Expect("icon ink uses the muted text colour", Enumerable.Range(6, 14).SelectMany(y => Enumerable.Range(0, 14).Select(x => node(x, y))).Any(c => c.A > 150 && Math.Abs(c.R - ink.R) <= 3 && Math.Abs(c.G - ink.G) <= 3));

                // The header: no node, one line through the slot.
                var headerRow = await MiddleGutterAsync(role: Forge.App.Views.TimelineRowRole.TurnHeader, Height).ConfigureAwait(true);
                var header = Capture(headerRow.Gutter, Width, Height);
                Expect("node-less header: the axis from top to bottom and nothing else", AxisOnly(header, 0, Height));

                // A node waiting for the user, motion reduced: a still ring (18.9 across) around the node.
                gutter = new Forge.App.Views.TimelineGutter { Kind = TimelineNodeKind.Approval, IsRunning = true };
                host = new Border { Width = Width, Height = Height, Child = gutter };
                window.Content = host;
                window.UpdateLayout();
                var ring = Capture(gutter, Width, Height);
                Expect("still ring reaches past the node slot", Enumerable.Range(0, Height).Any(y => Enumerable.Range(14, 12).Any(x => ring(x, y).A > 40)));

                // The node's colours follow the state.
                gutter = new Forge.App.Views.TimelineGutter { Kind = TimelineNodeKind.Command, IsFailing = true };
                host = new Border { Width = Width, Height = Height, Child = gutter };
                window.Content = host;
                window.UpdateLayout();
                var failed = Capture(gutter, Width, Height);
                gutter.TryFindResource("StatusErrorBrush", gutter.ActualThemeVariant, out var error);
                var red = ((Avalonia.Media.ISolidColorBrush)error!).Color;
                Expect("failed node is drawn in the error colour", Enumerable.Range(6, 14).SelectMany(y => Enumerable.Range(0, 14).Select(x => failed(x, y))).Any(c => c.A > 150 && Math.Abs(c.R - red.R) <= 3 && Math.Abs(c.G - red.G) <= 3));

                if (failures.Count > 0)
                {
                    throw new InvalidOperationException("gutter pixels: " + string.Join("; ", failures.Take(6)));
                }

                return $"gutter drawn as designed ({inkPixels} ink pixels in the node slot, icon matches the template within {worst}/255, line colour {line}, ink {ink})";
            }
            finally
            {
                Forge.App.ViewModels.TreeMotion.ReducedMotion = reduced;
                window.Content = null;
            }
        }

        /// <summary>Three rows in a list, each a gutter of the given role and height; returns the middle one, which has a node above and below.</summary>
        private async Task<(Control Host, Forge.App.Views.TimelineGutter Gutter)> MiddleGutterAsync(Forge.App.Views.TimelineRowRole role, int height)
        {
            var list = new ListBox { ItemsSource = new[] { 0, 1, 2 } };
            list.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<int>((_, _) => new Forge.App.Views.TimelineGutter { Kind = TimelineNodeKind.User, Role = role, Height = height }, true);
            window.Content = list;
            window.UpdateLayout();
            await NextFrameAsync().ConfigureAwait(true);
            window.UpdateLayout();
            var gutters = list.GetVisualDescendants().OfType<Forge.App.Views.TimelineGutter>().ToList();
            return (list, gutters[1]);
        }

        /// <summary>Renders a visual at 1:1 into a bitmap and returns a reader of its pixels (straight alpha).</summary>
        private static Func<int, int, Avalonia.Media.Color> Capture(Visual visual, int width, int height)
        {
            var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(new PixelSize(width, height), new Vector(96, 96));
            bitmap.Render(visual);
            var stride = width * 4;
            var buffer = System.Runtime.InteropServices.Marshal.AllocHGlobal(stride * height);
            try
            {
                bitmap.CopyPixels(new PixelRect(0, 0, width, height), buffer, stride * height, stride);
                var bytes = new byte[stride * height];
                System.Runtime.InteropServices.Marshal.Copy(buffer, bytes, 0, bytes.Length);
                return (x, y) =>
                {
                    // BGRA, premultiplied.
                    var at = y * stride + x * 4;
                    var a = bytes[at + 3];
                    return a == 0
                        ? default
                        : Avalonia.Media.Color.FromArgb(a, (byte)Math.Min(255, bytes[at + 2] * 255 / a), (byte)Math.Min(255, bytes[at + 1] * 255 / a), (byte)Math.Min(255, bytes[at] * 255 / a));
                };
            }
            finally
            {
                System.Runtime.InteropServices.Marshal.FreeHGlobal(buffer);
            }
        }

        private static string ScopedSheets()
        {
            var views = new (string Name, Func<Control> Make, string[] Sheets)[]
            {
                ("FilesToolView", () => new Forge.App.Views.FilesToolView(), ["ChromeStyles", "GitStyles", "TreeStyles", "ChangeMenuStyles"]),
                ("ProjectView", () => new Forge.App.Views.ProjectView(), ["ChromeStyles", "TreeStyles"]),
                ("SpecTreeToolView", () => new Forge.App.Views.SpecTreeToolView(), ["TreeStyles"]),
                ("BranchPickerView", () => new Forge.App.Views.BranchPickerView(), ["ChromeStyles", "TreeStyles", "ChangeMenuStyles"]),
                ("NotificationOverlay", () => new Forge.App.Views.NotificationOverlay(), ["ToastStyles"]),
                ("ApprovalQueueOverlay", () => new Forge.App.Approval.ApprovalQueueOverlay(), ["ChromeStyles", "DiffStyles"]),
                ("ChecksPanelView", () => new Forge.App.Views.ChecksPanelView(), ["ChromeStyles"]),
                ("PrCommentsView", () => new Forge.App.Views.PrCommentsView(), ["ChromeStyles"]),
                ("ResourceInspectorView", () => new Forge.App.Views.ResourceInspectorView(), ["ChromeStyles"]),
                ("FinishedSubAgentsHeaderView", () => new Forge.App.Views.FinishedSubAgentsHeaderView(), ["ChromeStyles"]),
                ("OnboardingView", () => new Forge.App.Views.OnboardingView(), ["ChromeStyles"]),
                ("McpSettingsView", () => new Forge.App.Views.McpSettingsView(), ["ChromeStyles"]),
                ("ChangesView", () => new Forge.App.Views.ChangesView(), ["DiffStyles"]),
                ("DiffFileHeader", () => new Forge.App.Changes.DiffFileHeader(), ["DiffStyles"]),
                ("DiffBlockBar", () => new Forge.App.Changes.DiffBlockBar(), ["DiffStyles"]),
            };
            var report = new List<string>();

            // The classes that live in ChromeStyles must not be defined at the application level any more (every row control
            // of the transcript would pay a match for each): the sheet holds them, the views above include it.
            string[] chromeClasses = [".h2", ".section-title", ".glyph", ".primary", ".breadcrumb", ".tab", ".count-badge", ".card", ".divider", ".panel", ".toolbar", ".status-dot"];
            var leaked = new List<string>();
            var checkedStyles = 0;
            void Walk(Avalonia.Styling.Styles styles)
            {
                foreach (var style in styles)
                {
                    switch (style)
                    {
                        case Avalonia.Styling.Styles nested:
                            Walk(nested);
                            break;
                        case Avalonia.Styling.Style { Selector: { } } when ++checkedStyles < 0:
                            break;
                        case Avalonia.Styling.Style { Selector: { } selector } when !selector.ToString()!.Contains(',') && chromeClasses.Any(c => selector.ToString()!.Contains(c, StringComparison.Ordinal)):
                            leaked.Add(selector.ToString()!);
                            break;
                    }
                }
            }

            Walk(Application.Current!.Styles);
            if (leaked.Count > 0)
            {
                throw new InvalidOperationException("Application-level styles for classes that belong to ChromeStyles: " + string.Join("; ", leaked.Take(8)));
            }

            foreach (var (typeName, make, sheets) in views)
            {
                var view = make();
                // Compiled XAML replaces a StyleInclude with the styles it loaded, so what can be checked is that each sheet
                // arrived as a non-empty collection of styles.
                var loaded = view.Styles.OfType<Avalonia.Styling.Styles>().Count(styles => styles.Count > 0);
                if (loaded < sheets.Length)
                {
                    throw new InvalidOperationException($"{typeName} holds {loaded} loaded style sheets, expected {sheets.Length} ({string.Join("+", sheets)}).");
                }

                report.Add($"{typeName}: {string.Join("+", sheets)}");
            }

            return $"{checkedStyles} application styles hold none of the chrome classes; " + string.Join(", ", report);
        }

        /// <summary>Whether a style held by a control reaches the content of a flyout opened from inside it (the scoped style sheets rely on it).</summary>
        private async Task<string> PopupStylesAsync()
        {
            var mark = new Border { Classes = { "popup-probe" }, Width = 40, Height = 20 };
            var button = new Button { Content = "open", Flyout = new Flyout { Content = mark } };
            var host = new StackPanel { Children = { button } };
            host.Styles.Add(new Style(x => x.OfType<Border>().Class("popup-probe")) { Setters = { new Setter(Border.BackgroundProperty, Avalonia.Media.Brushes.Red) } });
            window.Content = host;
            window.UpdateLayout();
            button.Flyout.ShowAt(button);
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(200).ConfigureAwait(true);
            window.UpdateLayout();
            var painted = Equals(mark.Background, Avalonia.Media.Brushes.Red);
            button.Flyout.Hide();
            window.Content = null;
            if (!painted)
            {
                throw new InvalidOperationException("The content of a flyout no longer gets the styles of the view that holds its button: the scoped style sheets (GitStyles, ChangeMenuStyles...) would not reach their flyouts.");
            }

            return "flyout content gets the style of an ancestor of the button";
        }

        /// <summary>Allocation (KB per control) of constructing, attaching, measuring and arranging 200 bare controls, round by round; with the app's style sheets, or with the theme alone for the floor.</summary>
        private async Task<string> AttachAsync(bool fluentOnly)
        {
            var app = Avalonia.Application.Current!;
            var taken = new List<(int Index, Avalonia.Styling.IStyle Style)>();
            var sources = app.Styles.Skip(1).ToList();
            for (var i = 0; i < sources.Count; i++)
            {
                if (fluentOnly)
                {
                    taken.Add((i, sources[i]));
                    app.Styles.Remove(sources[i]);
                }
            }

            var host = new StackPanel();
            window.Content = host;
            window.UpdateLayout();
            var lines = new List<string>();
            foreach (var (label, make) in new (string, Func<Control>)[] { ("Border", () => new Border { Height = 10 }), ("TextBlock", () => new Avalonia.Controls.TextBlock { Text = "hello" }), ("Button", () => new Button { Content = "x" }) })
            {
                var rounds = new List<string>();
                for (var round = 0; round < 8; round++)
                {
                    host.Children.Clear();
                    window.UpdateLayout();
                    var made = new List<Control>();
                    var a0 = GC.GetAllocatedBytesForCurrentThread();
                    for (var i = 0; i < 200; i++)
                    {
                        made.Add(make());
                    }

                    var a1 = GC.GetAllocatedBytesForCurrentThread();
                    var t1 = Stopwatch.GetTimestamp();
                    foreach (var control in made)
                    {
                        host.Children.Add(control);
                    }

                    var a2 = GC.GetAllocatedBytesForCurrentThread();
                    var attachMs = Stopwatch.GetElapsedTime(t1).TotalMilliseconds;
                    host.Measure(new Size(800, double.PositiveInfinity));
                    var a3 = GC.GetAllocatedBytesForCurrentThread();
                    host.Arrange(new Rect(0, 0, 800, host.DesiredSize.Height));
                    var a4 = GC.GetAllocatedBytesForCurrentThread();
                    rounds.Add($"[new {(a1 - a0) / 200 / 1024d:F1} attach {(a2 - a1) / 200 / 1024d:F1} ({attachMs / 200 * 1000:F0} us) measure {(a3 - a2) / 200 / 1024d:F1} arrange {(a4 - a3) / 200 / 1024d:F1}]");
                }

                lines.Add($"{label} KB per control: {string.Join(" ", rounds)}");
            }

            window.Content = null;
            foreach (var (_, style) in taken)
            {
                app.Styles.Add(style);
            }

            return (fluentOnly ? "Fluent only: " : "all styles: ") + string.Join(" | ", lines);
        }

        /// <summary>The part of the viewport (DIP) that no realized row covers: a blank band on screen.</summary>
        private static double Uncovered(Probe probe)
        {
            var height = probe.Scroller.Viewport.Height;
            var spans = new List<(double Top, double Bottom)>();
            foreach (var container in probe.List.GetRealizedContainers())
            {
                if (container.TranslatePoint(default, probe.Scroller) is { } at)
                {
                    spans.Add((Math.Max(0, at.Y), Math.Min(height, at.Y + container.Bounds.Height)));
                }
            }

            var gap = 0d;
            var reached = 0d;
            foreach (var (top, bottom) in spans.Where(s => s.Bottom > s.Top).OrderBy(s => s.Top))
            {
                gap += Math.Max(0, top - reached);
                reached = Math.Max(reached, bottom);
            }

            return gap + Math.Max(0, height - reached);
        }

        private static string Describe(Probe probe, double target, double gap)
        {
            var spans = probe.List.GetRealizedContainers()
                .Select(c => (Y: c.TranslatePoint(default, probe.Scroller)?.Y ?? double.NaN, H: c.Bounds.Height, I: probe.List.IndexFromContainer(c)))
                .OrderBy(t => t.I)
                .ToList();
            return $"target {F(target)}, offset {F(probe.Scroller.Offset.Y)}, extent {F(probe.Scroller.Extent.Height)}, uncovered {F(gap)}, realized {spans.Count} rows #{spans.FirstOrDefault().I}..#{spans.LastOrDefault().I} y {F(spans.FirstOrDefault().Y)}..{F(spans.LastOrDefault().Y + spans.LastOrDefault().H)} of viewport {F(probe.Scroller.Viewport.Height)}, rows {probe.Transcript.FlatRows().Count}, reconciled {Forge.App.Views.TranscriptStackPanel.Reconciled}";
        }

        /// <summary>
        /// Drags the scroll bar by jumps of every size (under, around and over a screen, up and down) and looks at the screen
        /// after each: no band of the viewport may be left without a row. Fails the run when one is.
        /// </summary>
        private async Task<string> ThumbGapAsync(Probe probe, bool mustHold)
        {
            var scroller = probe.Scroller;
            await PlaceAsync(probe, Tool(probe.Transcript, 20, 0), 0).ConfigureAwait(true);
            var worst = 0d;
            var bad = 0;
            var total = 0;
            var seed = 12345u;
            for (var i = 0; i < 400; i++)
            {
                seed = unchecked(seed * 1664525u + 1013904223u);
                var pick = (seed >> 16) % 10;
                var max = scroller.Extent.Height - scroller.Viewport.Height;
                var target = pick switch
                {
                    0 => 0d,
                    1 => max,
                    2 => max * ((seed >> 8) % 1000 / 1000d),
                    _ => scroller.Offset.Y + (((seed >> 8) % 5000) - 2500),
                };
                scroller.Offset = new Vector(0, Math.Clamp(target, 0, max));
                window.UpdateLayout();
                var gap = Uncovered(probe);
                worst = Math.Max(worst, gap);
                total++;
                if (gap > BlankBand)
                {
                    bad++;
                    Console.Error.WriteLine($"frame blank band at jump {i} (pick {pick}): {Describe(probe, target, gap)}");
                }
            }

            // Dragging the thumb from the middle to either end in steps, the way a hand does: the rows near the end must sit where
            // the end is (the first row at the top, the last at the bottom), not where an old estimate put them.
            foreach (var toTop in new[] { true, false })
            {
                var max = scroller.Extent.Height - scroller.Viewport.Height;
                scroller.Offset = new Vector(0, max / 2);
                window.UpdateLayout();
                for (var step = 0; step < 400 && (toTop ? scroller.Offset.Y > 0 : scroller.Offset.Y < scroller.Extent.Height - scroller.Viewport.Height - 1); step++)
                {
                    var limit = scroller.Extent.Height - scroller.Viewport.Height;
                    scroller.Offset = new Vector(0, Math.Clamp(scroller.Offset.Y + (toTop ? -400 : 400), 0, limit));
                    window.UpdateLayout();
                    var gap = Uncovered(probe);
                    worst = Math.Max(worst, gap);
                    total++;
                    if (gap > BlankBand)
                    {
                        bad++;
                        Console.Error.WriteLine($"frame blank band in the drag to the {(toTop ? "top" : "bottom")}, step {step}: {Describe(probe, scroller.Offset.Y, gap)}");
                    }
                }

                var first = probe.List.GetRealizedContainers().Select(c => c.TranslatePoint(default, scroller)?.Y ?? double.NaN).Min();
                Console.Error.WriteLine($"frame {(toTop ? "top" : "bottom")} of the drag: offset {F(scroller.Offset.Y)} of {F(scroller.Extent.Height - scroller.Viewport.Height)}, first realized row at y {F(first)}, uncovered {F(Uncovered(probe))}");
            }

            var report = $"{total} jumps, {bad} left a blank band in the viewport, worst {F(worst)} DIP";
            if (bad > 0 && mustHold)
            {
                throw new InvalidOperationException(report);
            }

            return report;
        }

        /// <summary>
        /// Drags the real scroll bar thumb with pointer events, down the whole track and back, 8 px of mouse per step, and
        /// reports how far the thumb strays from the mouse (the thumb's centre against the pointer, in px, relative to where it
        /// was grabbed) and how much its length changes (the length follows viewport / extent, and the extent is an estimate).
        /// </summary>
        private async Task<string> ThumbFollowAsync(Probe probe, bool guard)
        {
            probe.Follower.StopFollowing();
            probe.Scroller.Offset = default;
            window.UpdateLayout();
            await NextFrameAsync().ConfigureAwait(true);
            var bar = probe.Scroller.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.ScrollBar>().First(b => b.Orientation == Avalonia.Layout.Orientation.Vertical);
            var thumb = bar.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.Thumb>().First();
            var track = bar.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.Track>().First();

            double CentreY() => thumb.TranslatePoint(new Point(0, thumb.Bounds.Height / 2), window)!.Value.Y;
            var pointer = new Avalonia.Input.Pointer(Avalonia.Input.Pointer.GetNextFreeId(), Avalonia.Input.PointerType.Mouse, true);
            var x = thumb.TranslatePoint(new Point(thumb.Bounds.Width / 2, 0), window)!.Value.X;
            var mouseY = CentreY();
            var grabOffset = 0d;
            var down = new Avalonia.Input.PointerPointProperties(Avalonia.Input.RawInputModifiers.LeftMouseButton, Avalonia.Input.PointerUpdateKind.LeftButtonPressed);
            thumb.RaiseEvent(new Avalonia.Input.PointerPressedEventArgs(thumb, pointer, window, new Point(x, mouseY), 0, down, Avalonia.Input.KeyModifiers.None));

            var held = new Avalonia.Input.PointerPointProperties(Avalonia.Input.RawInputModifiers.LeftMouseButton, Avalonia.Input.PointerUpdateKind.Other);
            var heights = new List<double>();
            var extents = new List<double>();
            var errors = new List<double>();
            var lengthJumps = new List<double>();
            var corrections = new List<double>();
            var extentMoves = new List<double>();
            var offsetBack = 0;
            var previousOffset = probe.Scroller.Offset.Y;
            var reachedBottom = false;
            var trackTop = track.TranslatePoint(default, window)!.Value.Y;
            var trackBottom = trackTop + track.Bounds.Height;
            var direction = 1;
            for (var step = 0; step < 2000; step++)
            {
                mouseY += direction * 8;
                if (mouseY > trackBottom - 2)
                {
                    direction = -1;
                    mouseY = trackBottom - 2;
                }
                else if (mouseY < trackTop + 2 && direction < 0)
                {
                    break;
                }

                thumb.RaiseEvent(new Avalonia.Input.PointerEventArgs(Avalonia.Input.InputElement.PointerMovedEvent, thumb, pointer, window, new Point(x, mouseY), (ulong)step, held, Avalonia.Input.KeyModifiers.None));
                var offsetAsDragged = probe.Scroller.Offset.Y;
                var extentAsDragged = probe.Scroller.Extent.Height;
                window.UpdateLayout();
                corrections.Add(probe.Scroller.Offset.Y - offsetAsDragged);
                extentMoves.Add(probe.Scroller.Extent.Height - extentAsDragged);
                var height = thumb.Bounds.Height;
                if (heights.Count > 0)
                {
                    lengthJumps.Add(Math.Abs(height - heights[^1]));
                }

                heights.Add(height);
                extents.Add(probe.Scroller.Extent.Height);
                errors.Add(CentreY() - mouseY);
                // The mouse goes down, the content must go down: an offset that moves against the drag is the thumb running away.
                if ((probe.Scroller.Offset.Y - previousOffset) * direction < -0.5)
                {
                    offsetBack++;
                }

                previousOffset = probe.Scroller.Offset.Y;
                reachedBottom |= probe.Scroller.Offset.Y >= probe.Scroller.Extent.Height - probe.Scroller.Viewport.Height - 1;
            }

            thumb.RaiseEvent(new Avalonia.Input.PointerReleasedEventArgs(thumb, pointer, window, new Point(x, mouseY), 0, new Avalonia.Input.PointerPointProperties(Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PointerUpdateKind.LeftButtonReleased), Avalonia.Input.KeyModifiers.None, Avalonia.Input.MouseButton.Left));
            _ = grabOffset;
            var far = errors.Count(e => Math.Abs(e) > 3);
            if (guard)
            {
                var broken = new List<string>();
                if (lengthJumps.Count(j => j > 1) > 0)
                {
                    broken.Add($"the thumb changed its length by up to {F(lengthJumps.Max())} px in a step");
                }

                if (offsetBack > 0)
                {
                    broken.Add($"the content moved against the drag in {offsetBack} steps");
                }

                if (far * 10 > errors.Count)
                {
                    broken.Add($"the thumb was more than 3 px from the mouse in {far} of {errors.Count} steps");
                }

                if (!reachedBottom)
                {
                    broken.Add("dragging to the end of the track never reached the end of the content");
                }

                if (broken.Count > 0)
                {
                    throw new InvalidOperationException("thumb drag: " + string.Join("; ", broken));
                }
            }

            return $"{errors.Count} mouse steps of 8 px: thumb {F(heights.Min())}-{F(heights.Max())} px long (track {F(track.Bounds.Height)}), largest change of length in one step {F(lengthJumps.DefaultIfEmpty(0).Max())} px, steps where it changed by more than 1 px {lengthJumps.Count(j => j > 1)}; thumb centre against the mouse: worst {F(errors.Max(Math.Abs))} px, steps further than 3 px {far}; extent {F(extents.Min())}-{F(extents.Max())} DIP; steps where the content moved against the drag {offsetBack}; the layout after a drag step changed the offset by {F(corrections.Sum(Math.Abs))} DIP in all (worst {F(corrections.Max(Math.Abs))}, steps over 1 DIP: {corrections.Count(c => Math.Abs(c) > 1)}) and the extent by {F(extentMoves.Sum(Math.Abs))} DIP in all (worst {F(extentMoves.Max(Math.Abs))}); the mouse moved {F(8d * errors.Count)} px = {F(8d * errors.Count / Math.Max(1, track.Bounds.Height - thumb.Bounds.Height) * (probe.Scroller.Extent.Height - probe.Scroller.Viewport.Height))} DIP of offset if nothing shifted";
        }

        /// <summary>
        /// How the extent (and so the scroll bar's thumb) moves while the user reads from the top to the bottom: the panel
        /// estimates the rows it has not built by the average of the ones it has, so the extent changes as real heights come in.
        /// </summary>
        private async Task<string> ExtentAsync(Probe probe, bool guard)
        {
            var scroller = probe.Scroller;
            scroller.Offset = default;
            window.UpdateLayout();
            await NextFrameAsync().ConfigureAwait(true);
            var trace = new List<double> { scroller.Extent.Height };
            var viewport = scroller.Viewport.Height;
            while (scroller.Offset.Y + viewport < scroller.Extent.Height - 1 && trace.Count < 2000)
            {
                scroller.Offset = new Vector(0, scroller.Offset.Y + 200);
                window.UpdateLayout();
                trace.Add(scroller.Extent.Height);
            }

            var final = trace[^1];
            var jump = 0d;
            for (var i = 1; i < trace.Count; i++)
            {
                jump = Math.Max(jump, Math.Abs(trace[i] - trace[i - 1]));
            }

            if (guard)
            {
                // Rows of different kinds have different heights, and the panel must count the rows it has not built by what it
                // knows of them (their own height, or their type's): the extent may not swing with the kinds on screen. The first
                // steps are left out: the first mount in a process is still settling (the stock panel is as far off there).
                const int Settle = 3;
                var worst = trace.Skip(Settle).Max(t => Math.Abs(t - final)) / final;
                var step = 0d;
                for (var i = Settle + 1; i < trace.Count; i++)
                {
                    step = Math.Max(step, Math.Abs(trace[i] - trace[i - 1]) / final);
                }

                if (worst > 0.08 || step > 0.05)
                {
                    throw new InvalidOperationException($"extent: the extent was up to {100 * worst:F1}% off the final one and changed by up to {100 * step:F1}% in a step (limits 8% and 5%)");
                }
            }

            // Thumb length is viewport / extent of the track: report it as a share of the track at the start, at the worst, at the end.
            return $"extent steps {trace.Count}: first {F(trace[0])}, min {F(trace.Min())}, max {F(trace.Max())}, final {F(final)} DIP; start is {F(100 * (trace[0] - final) / final)}% off the final, worst {F(100 * trace.Max(t => Math.Abs(t - final)) / final)}%; largest single-step change {F(jump)} DIP ({F(100 * jump / final)}% of the extent); thumb {F(100 * viewport / trace[0])}% at start, {F(100 * viewport / final)}% at the end";
        }

        private void Step(Probe probe, List<(double Ms, int Built, string Types, int Gcs)> steps, List<string> built, Action change)
        {
            built.Clear();
            var gcBefore = GC.CollectionCount(0);
            var pauseBefore = GC.GetTotalPauseDuration();
            var allocBefore = GC.GetAllocatedBytesForCurrentThread();
            var started = Stopwatch.GetTimestamp();
            change();
            window.UpdateLayout();
            var ms = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            if (built.Count > 0)
            {
                StepPauseMs += (GC.GetTotalPauseDuration() - pauseBefore).TotalMilliseconds;
                StepAllocKb += (GC.GetAllocatedBytesForCurrentThread() - allocBefore) / 1024d;
                StepRows += built.Count;
                StepMs += ms;
            }

            steps.Add((ms, built.Count, string.Join(",", built.GroupBy(t => t).Select(g => $"{g.Key}x{g.Count()}")), GC.CollectionCount(0) - gcBefore));
        }

        private static string Summarize(List<(double Ms, int Built, string Types, int Gcs)> steps, Probe probe)
        {
            var sorted = steps.Select(s => s.Ms).Order().ToArray();
            double P(double q) => sorted[Math.Min(sorted.Length - 1, (int)Math.Ceiling(q * sorted.Length) - 1)];
            var over = steps.Count(s => s.Ms > FrameBudget);
            var overGc = steps.Count(s => s.Ms > FrameBudget && s.Gcs > 0);
            var worst = string.Join("; ", steps.OrderByDescending(s => s.Ms).Take(3).Select(s => $"{F(s.Ms)} ms [{(s.Types.Length == 0 ? "no new rows" : s.Types)}]"));
            var withNew = steps.Where(s => s.Built > 0).ToList();
            var perType = string.Join(", ", steps.Where(s => s.Built > 0 && !s.Types.Contains(',') && s.Types.EndsWith("x1")).GroupBy(s => s.Types).Select(g => $"{g.Key} {F(g.Average(s => s.Ms))} ms (n={g.Count()})"));
            return $"{steps.Count} steps: p50 {F(P(0.5))} p95 {F(P(0.95))} p99 {F(P(0.99))} max {F(sorted[^1])} ms, over {FrameBudget} ms {over} ({F(100d * over / steps.Count)}%, {overGc} with a GC in the step), steps that built rows {withNew.Count}, avg {F(withNew.Count == 0 ? 0 : withNew.Average(s => s.Ms))} ms; slowest: {worst}; single-row steps: {perType}; anchor {F(probe.Follower.Anchor.Busy.TotalMilliseconds)} ms total";
        }
    }
}
