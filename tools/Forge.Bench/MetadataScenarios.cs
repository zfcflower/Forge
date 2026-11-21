using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Forge.Abstractions;
using Forge.App.Preferences;
using Forge.App.Providers;
using Forge.App.Settings;
using Forge.Core.Providers;

namespace Forge.Bench;

/// <summary>
/// The model metadata window's table: opening it over a provider with 300 models, and scrolling it. The rows are the app's
/// densest table - nine columns, a number field (stepper, text box, two repeat buttons) in five of them and up to six icons
/// in the other two - so this family is the ruler for "does a settings table with editable cells render acceptably".
/// <para>Windows are real here (the product's own platform setup on its own UI thread), because the table only realizes
/// its rows once it is attached and laid out; the <c>open</c> row therefore carries one window creation in both arms and
/// reports the empty window as its baseline, so the ratio is the rows, not the OS.</para>
/// </summary>
internal static class MetadataScenarios
{
    private const int Models = 300;

    /// <summary>Rows scrolled per timed call: a drag of one viewport, which is what a thumb drag costs per report step.</summary>
    private const int ScrollSteps = 24;

    /// <summary>
    /// What opening the window over 300 models may cost. The rows rest as text and build their field only for the cell being
    /// edited, and the settings sheet is scoped to the title and the footer instead of the window; before either, the same
    /// window read 275-390 ms. After the change the same row measures 182-221 ms across runs (in-call 110-156 ms, empty
    /// window 32-41 ms), so the budget leaves room for that spread and still fails if the per-cell fields come back.
    /// </summary>
    private const double OpenBudgetMs = 260;

    /// <summary>What one screen of scrolling may cost per jump; before the same change it read 261-304 ms.</summary>
    private const double JumpBudgetMs = 110;

    public static IEnumerable<BenchScenario> Create(Fixtures fixtures)
    {
        yield return Open();
        yield return Scroll();
    }

    private static BenchScenario Open() =>
        new(
            "table.metadata.open",
            "table",
            Heavy: false,
            PrepareAsync: async cancellationToken =>
            {
                var rig = await TableRig.GetAsync(cancellationToken).ConfigureAwait(false);
                return new BenchCase
                {
                    Warmup = 2,
                    Iterations = 10,
                    ProcessWideAllocation = true,
                    BaselineName = "同一窗口、0 个模型",
                    Note = $"{Models} models, 9 columns, open + first layout",
                    Run = async _ =>
                    {
                        var result = await rig.OpenAsync(Models).ConfigureAwait(false);
                        return new OpResult(Items: Models, Observation: $"open={result.Ms:F1} ms (ctor {result.CtorMs:F1}) rows={result.Rows} realized={result.RealizedRows} numberFields={result.NumberFields}");
                    },
                    Baseline = async _ =>
                    {
                        var result = await rig.OpenAsync(0).ConfigureAwait(false);
                        return new OpResult(Items: 0, Observation: $"open={result.Ms:F1} ms");
                    },
                    Check = comparison => new Verdict(
                        $"opening 300 models stays within {OpenBudgetMs:0} ms (p50)",
                        $"{comparison.Implementation.P50Ms.ToString("F1", CultureInfo.InvariantCulture)} ms, "
                        + $"{comparison.Implementation.AllocatedBytesPerOp / (1024.0 * 1024.0):F1} MB, "
                        + $"{comparison.Implementation.P50Ms / OpenBudgetMs:P0} of the budget",
                        comparison.Implementation.P50Ms <= OpenBudgetMs),
                };
            });

    private static BenchScenario Scroll() =>
        new(
            "table.metadata.scroll",
            "table",
            Heavy: false,
            PrepareAsync: async cancellationToken =>
            {
                var rig = await TableRig.GetAsync(cancellationToken).ConfigureAwait(false);
                return new BenchCase
                {
                    Warmup = 3,
                    Iterations = 20,
                    ProcessWideAllocation = true,
                    Note = $"{Models} models, {ScrollSteps} viewport jumps per call",
                    Run = async _ =>
                    {
                        var result = await rig.ScrollAsync(ScrollSteps).ConfigureAwait(false);
                        return new OpResult(Items: ScrollSteps, Observation: $"jump={result.Ms / ScrollSteps:F2} ms rows={result.Rows}");
                    },
                    Check = comparison => new Verdict(
                        $"a viewport jump stays within {JumpBudgetMs:0} ms (p50 per jump)",
                        $"{comparison.Implementation.P50Ms / ScrollSteps:F1} ms per jump, "
                        + $"{comparison.Implementation.AllocatedBytesPerOp / (1024.0 * 1024.0):F1} MB per call",
                        comparison.Implementation.P50Ms / ScrollSteps <= JumpBudgetMs),
                };
            });

    /// <summary>The UI thread and the provider the two scenarios share, built once.</summary>
    private sealed class TableRig
    {
        private static TableRig? shared;
        private static Task<TableRig>? starting;

        private readonly Dictionary<int, ConnectedProvider> providers = [];
        private ProviderServices services = null!;

        public static Task<TableRig> GetAsync(CancellationToken cancellationToken) =>
            starting ??= StartAsync(cancellationToken);

        private static async Task<TableRig> StartAsync(CancellationToken cancellationToken)
        {
            var ready = new TaskCompletionSource<TableRig>(TaskCreationOptions.RunContinuationsAsynchronously);
            var thread = new Thread(() =>
            {
                try
                {
                    Forge.App.Program.BuildAvaloniaApp().SetupWithoutStarting();
                    var rig = new TableRig();
                    rig.services = new ProviderServices(
                        Path.Combine(Path.GetTempPath(), "forge-bench-table", "settings.json"),
                        Path.Combine(Path.GetTempPath(), "forge-bench-table"),
                        new HttpClient());
                    Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "forge-bench-table"));
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

        /// <summary>
        /// Opens the window over <paramref name="models"/> models, lays it out once, then closes it again: no window outlives
        /// the call it was opened for. A window left open keeps its rows, its render surface and its layout alive, so the next
        /// measurement would run in a busier process and read slow for no reason.
        /// </summary>
        public async Task<Step> OpenAsync(int models) =>
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                var (viewModel, rows) = Model(models);
                var watch = Stopwatch.StartNew();
                var window = new ModelMetadataWindow(viewModel);
                var ctorMs = watch.Elapsed.TotalMilliseconds;
                window.Show();
                window.UpdateLayout();
                var elapsed = watch.Elapsed.TotalMilliseconds;
                var realizedRows = window.GetVisualDescendants().OfType<TableViewRow>().Count();
                var cells = window.GetVisualDescendants().OfType<NumericUpDown>().Count();
                window.Close();
                return new Step(elapsed, rows, realizedRows, cells, ctorMs);
            });

        /// <summary>Opens a fresh window, jumps the table down one viewport per step, then closes it.</summary>
        public async Task<Step> ScrollAsync(int steps) =>
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                var (viewModel, rows) = Model(Models);
                var window = new ModelMetadataWindow(viewModel);
                window.Show();
                window.UpdateLayout();
                var table = window.GetVisualDescendants().OfType<TableView>().Single();
                var scroller = table.GetVisualDescendants().OfType<ScrollViewer>().First();
                var step = Math.Max(scroller.Viewport.Height, 56);
                var watch = Stopwatch.StartNew();
                for (var index = 0; index < steps; index++)
                {
                    var offset = scroller.Offset.Y + step;
                    scroller.Offset = new Vector(0, offset > scroller.Extent.Height - scroller.Viewport.Height
                        ? 0
                        : offset);
                    window.UpdateLayout();
                }

                var elapsed = watch.Elapsed.TotalMilliseconds;
                window.Close();
                return new Step(elapsed, rows);
            });

        /// <summary>
        /// The window's view model over a provider of <paramref name="count"/> models. The provider is built once per count:
        /// the app already holds it when the window opens, so building it is not part of the measured work.
        /// </summary>
        private (ModelMetadataViewModel Model, int Rows) Model(int count)
        {
            if (!providers.TryGetValue(count, out var provider))
            {
                provider = Provider(count);
                providers[count] = provider;
            }

            var model = new ModelMetadataViewModel(services, provider, new AppPreferences());
            return (model, count);
        }

        /// <summary>Models that all carry a window, four prices, all five input kinds and four reasoning tiers.</summary>
        private ConnectedProvider Provider(int count)
        {
            var names = new string[count];
            var overrides = new Dictionary<string, ModelMetadata>(StringComparer.Ordinal);
            for (var index = 0; index < count; index++)
            {
                var name = string.Create(CultureInfo.InvariantCulture, $"vendor-model-{index:0000}");
                names[index] = name;
                overrides[name] = new ModelMetadata(200000 + index * 1000, new ModelPricing(3, 15, 0.3, 3.75))
                {
                    Capabilities = new ModelCapabilities(
                        ModelInputs.Text | ModelInputs.Image | ModelInputs.Pdf | ModelInputs.Audio | ModelInputs.Video,
                        [ThinkingEffort.Off, ThinkingEffort.Low, ThinkingEffort.Medium, ThinkingEffort.High]),
                };
            }

            var definition = new ProviderDefinition
            {
                Id = "bench",
                PresetId = "custom",
                DisplayName = "Bench",
                Protocol = ProviderProtocol.Anthropic,
                BaseUrl = "https://example.invalid",
                CredentialKind = ProviderCredentialKind.ApiKey,
                Models = names,
                DefaultModel = names.FirstOrDefault() ?? string.Empty,
                Overrides = overrides,
            };

            return new ConnectedProvider(definition, services.Secrets, services.Cache, services.Credentials);
        }

        internal readonly record struct Step(double Ms, int Rows, int RealizedRows = 0, int NumberFields = 0, double CtorMs = 0);
    }
}
