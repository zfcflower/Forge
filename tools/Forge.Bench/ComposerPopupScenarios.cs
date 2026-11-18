using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Threading;
using Forge.App.Composition;
using Forge.App.Localization;
using Forge.App.Providers;
using Forge.App.Services;
using Forge.App.ViewModels;
using Forge.App.Views;

namespace Forge.Bench;

/// <summary>
/// The composer's pickers on a real Avalonia window: how long a chip's flyout takes from the click until the second frame after
/// it opened has been presented, against an idle two-frame wait on the same window. The catalogue is a copy of the user's own
/// <c>settings.json</c> and model caches (no secrets), so the model list has the size they actually see; with no such files the
/// scenarios are skipped, not faked. The numbers come from the platform the bench runs on (Win32 with overlay popups, the way
/// the product starts), so they need an interactive desktop.
/// <para>The row's check is the requirement the user set: opening a picker costs less than two frames over an idle wait.</para>
/// </summary>
internal static class ComposerPopupScenarios
{
    private const double BudgetMs = 33.4;

    /// <summary>One frame at 60 Hz, the budget a single notch of a drag gets.</summary>
    private const double FrameBudgetMs = 16.7;

    public static IEnumerable<BenchScenario> Create(Fixtures fixtures)
    {
        yield return Open(fixtures, "model", "ModelChip");
        yield return Open(fixtures, "orchestration", "OrchestrationChip");
        yield return Open(fixtures, "agent", "AgentChip");
        yield return Filter(fixtures);
        yield return ToolbarCreate(fixtures);
        yield return EffortStep(fixtures);
        yield return EffortSweep(fixtures);
        yield return EffortModel(fixtures);
    }

    private static BenchScenario Open(Fixtures fixtures, string picker, string chipName) =>
        new(
            $"composer.{picker}.open",
            "composer",
            Heavy: false,
            PrepareAsync: async cancellationToken =>
            {
                var rig = await Rig.GetAsync(fixtures, cancellationToken).ConfigureAwait(false);
                var syncMs = new List<double>();
                return new BenchCase
                {
                    Warmup = 3,
                    Iterations = 30,
                    ProcessWideAllocation = true,
                    BaselineName = "idle window, two frames",
                    Note = $"{rig.ModelCount} models; the click to the second presented frame",
                    Run = async _ =>
                    {
                        var shown = await rig.OpenAndCloseAsync(chipName).ConfigureAwait(false);
                        syncMs.Add(shown);
                        return new OpResult(Items: 1, Observation: $"show()={Median(syncMs):F1} ms on the UI thread");
                    },
                    Baseline = async _ =>
                    {
                        await rig.FramesAsync(2).ConfigureAwait(false);
                        return new OpResult(Items: 1);
                    },
                    Check = comparison => comparison.Baseline is { } baseline
                        ? new Verdict(
                            $"{picker} picker opens within {BudgetMs:F0} ms of an idle two-frame wait (p95)",
                            $"{comparison.Implementation.P95Ms.ToString("F1", CultureInfo.InvariantCulture)} ms vs idle "
                            + $"{baseline.P50Ms.ToString("F1", CultureInfo.InvariantCulture)} ms",
                            comparison.Implementation.P95Ms <= baseline.P50Ms + BudgetMs)
                        : null,
                };
            });

    /// <summary>One keystroke in the model search: filter the catalogue and let the list catch up.</summary>
    private static BenchScenario Filter(Fixtures fixtures) =>
        new(
            "composer.model.filter",
            "composer",
            Heavy: false,
            PrepareAsync: async cancellationToken =>
            {
                var rig = await Rig.GetAsync(fixtures, cancellationToken).ConfigureAwait(false);
                var flip = false;
                return new BenchCase
                {
                    Warmup = 3,
                    Iterations = 30,
                    ProcessWideAllocation = true,
                    Note = $"{rig.ModelCount} models; alternates 'a' and empty",
                    Run = async _ =>
                    {
                        flip = !flip;
                        var rows = await rig.FilterAsync(flip ? "a" : string.Empty).ConfigureAwait(false);
                        return new OpResult(Items: rows, Observation: $"rows={rows}");
                    },
                };
            });

    /// <summary>Building a session's composer: the view model (every model row) and the toolbar view, laid out once.</summary>
    private static BenchScenario ToolbarCreate(Fixtures fixtures) =>
        new(
            "composer.toolbar.create",
            "composer",
            Heavy: false,
            PrepareAsync: async cancellationToken =>
            {
                var rig = await Rig.GetAsync(fixtures, cancellationToken).ConfigureAwait(false);
                return new BenchCase
                {
                    Warmup = 2,
                    Iterations = 15,
                    ProcessWideAllocation = true,
                    Note = $"{rig.ModelCount} models; view model + toolbar + first frame",
                    Run = async _ =>
                    {
                        await rig.CreateToolbarAsync().ConfigureAwait(false);
                        return new OpResult(Items: 1);
                    },
                };
            });

    /// <summary>
    /// One notch on the reasoning dial while the picker sits open: from the value change to the next presented frame. This is
    /// what a drag feels like — one notch per pointer move — and it is the row that catches per-notch work added to the view
    /// model (bars, tick classes, automation properties) that only shows up once the popup is on screen.
    /// </summary>
    private static BenchScenario EffortStep(Fixtures fixtures) =>
        new(
            "composer.effort.step",
            "composer",
            Heavy: false,
            PrepareAsync: async cancellationToken =>
            {
                var rig = await Rig.GetAsync(fixtures, cancellationToken).ConfigureAwait(false);
                return new BenchCase
                {
                    Warmup = 5,
                    Iterations = 60,
                    ProcessWideAllocation = true,
                    BaselineName = "idle window, one frame",
                    Note = $"{rig.EffortSteps} steps; one notch, to the next presented frame",
                    Run = async _ =>
                    {
                        var ms = await rig.StepEffortAsync().ConfigureAwait(false);
                        return new OpResult(Items: 1, Observation: $"notch={ms:F1} ms");
                    },
                    Baseline = async _ =>
                    {
                        await rig.FramesAsync(1).ConfigureAwait(false);
                        return new OpResult(Items: 1);
                    },
                    Check = comparison => comparison.Baseline is { } baseline
                        ? new Verdict(
                            "one effort notch costs less than one frame over an idle wait (p95)",
                            $"{comparison.Implementation.P95Ms.ToString("F1", CultureInfo.InvariantCulture)} ms vs idle "
                            + $"{baseline.P50Ms.ToString("F1", CultureInfo.InvariantCulture)} ms",
                            comparison.Implementation.P95Ms <= baseline.P50Ms + FrameBudgetMs)
                        : null,
                };
            });

    /// <summary>
    /// A fast drag: a pointer move that crosses every notch before the next frame. One frame's worth of dial work, so a
    /// per-notch cost that is invisible at 60 Hz (a notch per frame) shows up here multiplied by the step count.
    /// </summary>
    private static BenchScenario EffortSweep(Fixtures fixtures) =>
        new(
            "composer.effort.sweep",
            "composer",
            Heavy: false,
            PrepareAsync: async cancellationToken =>
            {
                var rig = await Rig.GetAsync(fixtures, cancellationToken).ConfigureAwait(false);
                return new BenchCase
                {
                    Warmup = 5,
                    Iterations = 60,
                    ProcessWideAllocation = true,
                    BaselineName = "idle window, one frame",
                    Note = $"{rig.EffortSteps} steps; every notch in one frame",
                    Run = async _ =>
                    {
                        var ms = await rig.SweepEffortAsync().ConfigureAwait(false);
                        return new OpResult(Items: rig.EffortSteps, Observation: $"sweep={ms:F1} ms");
                    },
                    Baseline = async _ =>
                    {
                        await rig.FramesAsync(1).ConfigureAwait(false);
                        return new OpResult(Items: 1);
                    },
                };
            });

    /// <summary>
    /// The same notch with no frame presented and the picker closed: the view model and binding cost alone. Run it beside
    /// <c>composer.effort.step</c> — when the two agree, the wait is the screen, not the code.
    /// </summary>
    private static BenchScenario EffortModel(Fixtures fixtures) =>
        new(
            "composer.effort.model",
            "composer",
            Heavy: false,
            PrepareAsync: async cancellationToken =>
            {
                var rig = await Rig.GetAsync(fixtures, cancellationToken).ConfigureAwait(false);
                await rig.ClosePickerAsync().ConfigureAwait(false);
                return new BenchCase
                {
                    Warmup = 5,
                    Iterations = 60,
                    ProcessWideAllocation = true,
                    Note = $"{rig.EffortSteps} steps; one notch, picker closed, no frame",
                    Run = async _ =>
                    {
                        var ms = await rig.StepEffortOnlyAsync().ConfigureAwait(false);
                        return new OpResult(Items: 1, Observation: $"notch={ms:F2} ms");
                    },
                };
            });

    private static double Median(List<double> values)
    {
        var sorted = values.OrderBy(value => value).ToArray();
        return sorted[sorted.Length / 2];
    }

    /// <summary>The window, view model and toolbar the scenarios share; built once on the Avalonia thread.</summary>
    private sealed class Rig
    {
        private static Rig? shared;
        private static Task<Rig>? starting;

        private readonly Func<ComposerViewModel> newComposer;
        private Window window = null!;
        private ComposerViewModel composer = null!;
        private ComposerToolbar toolbar = null!;

        private Rig(Func<ComposerViewModel> newComposer) => this.newComposer = newComposer;

        public int ModelCount { get; private set; }

        public int EffortSteps => composer.EffortSteps.Count;
        public static Task<Rig> GetAsync(Fixtures fixtures, CancellationToken cancellationToken) =>
            starting ??= StartAsync(fixtures, cancellationToken);

        private static async Task<Rig> StartAsync(Fixtures fixtures, CancellationToken cancellationToken)
        {
            var source = AppPaths.Create().UserDataDirectory;
            var data = fixtures.Workspace.DirectoryAt("composer/data");
            var copied = 0;
            foreach (var name in (string[])["settings.json", "provider-models.json", "model-catalog.json"])
            {
                var from = Path.Combine(source, name);
                if (File.Exists(from))
                {
                    File.Copy(from, Path.Combine(data, name), overwrite: true);
                    copied++;
                }
            }

            if (copied == 0)
            {
                throw new InvalidOperationException("no settings.json in the user data directory: nothing to size the model list from");
            }

            var ready = new TaskCompletionSource<Rig>(TaskCreationOptions.RunContinuationsAsynchronously);
            var thread = new Thread(() =>
            {
                try
                {
                    Forge.App.Program.BuildAvaloniaApp().SetupWithoutStarting();
                    Loc.EnsureRegistered();
                    var services = new ProviderServices(Path.Combine(data, "settings.json"), data, new HttpClient());
                    var workspace = fixtures.Workspace.DirectoryAt("composer/workspace");
                    ComposerViewModel NewComposer() => new(
                        services,
                        _ => { },
                        new ComposerPreferences(),
                        workspace,
                        fileSource: _ => Task.FromResult<IReadOnlyList<string>>([]),
                        modelRefresh: _ => Task.CompletedTask);

                    var rig = new Rig(NewComposer);
                    rig.Build();
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
            var rig = await ready.Task.WaitAsync(TimeSpan.FromSeconds(60), cancellationToken).ConfigureAwait(false);
            shared = rig;
            return rig;
        }

        /// <summary>Runs on the UI thread: the host window with a toolbar at its bottom edge, the way the composer sits in a session.</summary>
        private void Build()
        {
            composer = newComposer();
            ModelCount = composer.Models.Count;
            toolbar = new ComposerToolbar { DataContext = composer, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Bottom, Margin = new Thickness(12) };
            window = new Window { Width = 900, Height = 640, Content = toolbar, ShowInTaskbar = false, Title = "forge-bench composer" };
            window.Show();
            Dispatcher.UIThread.RunJobs();
        }

        public Task FramesAsync(int count) =>
            Dispatcher.UIThread.InvokeAsync(async () =>
            {
                for (var index = 0; index < count; index++)
                {
                    await NextFrameAsync().ConfigureAwait(true);
                }
            });

        private Task NextFrameAsync()
        {
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            window.RequestAnimationFrame(_ => done.TrySetResult());
            return done.Task;
        }

        /// <summary>Opens a chip's picker, waits for the second frame after it opened, closes it again; returns the UI-thread time of the open call.</summary>
        public Task<double> OpenAndCloseAsync(string chipName) =>
            Dispatcher.UIThread.InvokeAsync(async () =>
            {
                var picker = toolbar.Picker(chipName);
                var watch = Stopwatch.StartNew();
                picker.Open();
                var shown = watch.Elapsed.TotalMilliseconds;
                await NextFrameAsync().ConfigureAwait(true);
                await NextFrameAsync().ConfigureAwait(true);
                picker.Close(restoreFocus: false);
                return shown;
            });

        /// <summary>One notch on the reasoning dial with the picker open; returns the time to the next presented frame.</summary>
        public Task<double> StepEffortAsync() =>
            Dispatcher.UIThread.InvokeAsync(async () =>
            {
                var picker = toolbar.Picker("ModelChip");
                if (!picker.IsOpen)
                {
                    picker.Open();
                    await NextFrameAsync().ConfigureAwait(true);
                    await NextFrameAsync().ConfigureAwait(true);
                }

                var watch = Stopwatch.StartNew();
                StepEffort();
                await NextFrameAsync().ConfigureAwait(true);
                return watch.Elapsed.TotalMilliseconds;
            });

        /// <summary>One notch with no frame presented and the picker already closed: the view model's own cost.</summary>
        public async Task<double> StepEffortOnlyAsync()
        {
            var elapsed = 0.0;
            var operation = Dispatcher.UIThread.InvokeAsync(() =>
            {
                var watch = Stopwatch.StartNew();
                StepEffort();
                elapsed = watch.Elapsed.TotalMilliseconds;
            });
            await operation;
            return elapsed;
        }

        /// <summary>Every notch in one frame: what a pointer move that crosses the whole dial costs before it is drawn.</summary>
        public Task<double> SweepEffortAsync() =>
            Dispatcher.UIThread.InvokeAsync(async () =>
            {
                var picker = toolbar.Picker("ModelChip");
                if (!picker.IsOpen)
                {
                    picker.Open();
                    await NextFrameAsync().ConfigureAwait(true);
                    await NextFrameAsync().ConfigureAwait(true);
                }

                var maximum = composer.EffortMaximum;
                var watch = Stopwatch.StartNew();
                // Half steps: the dial reports continuous positions while dragging, so this walks the same mapping a pointer does.
                for (var position = 0.0; position <= maximum; position += 0.5)
                {
                    composer.EffortValue = position;
                }

                await NextFrameAsync().ConfigureAwait(true);
                return watch.Elapsed.TotalMilliseconds;
            });

        /// <summary>Closes the model picker and lets the frame settle, outside any timed window.</summary>
        public Task ClosePickerAsync() =>
            Dispatcher.UIThread.InvokeAsync(async () =>
            {
                toolbar.Picker("ModelChip").Close(restoreFocus: false);
                await NextFrameAsync().ConfigureAwait(true);
            });

        private void StepEffort()
        {
            var maximum = composer.EffortMaximum;
            composer.EffortValue = composer.EffortValue >= maximum ? 0 : composer.EffortValue + 1;
        }

        public Task<int> FilterAsync(string search) =>
            Dispatcher.UIThread.InvokeAsync(async () =>
            {
                composer.ModelSearch = search;
                await NextFrameAsync().ConfigureAwait(true);
                return composer.Models.Count;
            });

        public Task CreateToolbarAsync() =>
            Dispatcher.UIThread.InvokeAsync(async () =>
            {
                var fresh = newComposer();
                var view = new ComposerToolbar { DataContext = fresh };
                window.Content = view;
                await NextFrameAsync().ConfigureAwait(true);
                window.Content = toolbar;
                fresh.Dispose();
            });
    }
}
