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

    public static IEnumerable<BenchScenario> Create(Fixtures fixtures)
    {
        yield return Open(fixtures, "model", "ModelChip");
        yield return Open(fixtures, "orchestration", "OrchestrationChip");
        yield return Open(fixtures, "agent", "AgentChip");
        yield return Filter(fixtures);
        yield return ToolbarCreate(fixtures);
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
