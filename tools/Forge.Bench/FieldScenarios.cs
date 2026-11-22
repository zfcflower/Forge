using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Forge.Bench;

/// <summary>
/// A settings page's form controls: building a page-shaped form (labels + text fields, dropdowns and number fields) and then
/// re-evaluating their styles. The two arms build the same tree and differ in one thing only - whether the sheet that carries
/// the three Forge field themes is in the subtree - so the pair answers "what does the custom frame cost" rather than "is
/// Avalonia slow". The numbers need an interactive desktop: the rig runs the product's own platform setup (Win32, overlay
/// popups) on its own UI thread, the way the app starts - and like the other UI families it runs on its own
/// (<c>-- form</c>), because a process can hold only one Avalonia setup and one UI thread.
/// </summary>
internal static class FieldScenarios
{
    private const int TextFields = 24;
    private const int Dropdowns = 8;
    private const int Numbers = 8;

    /// <summary>How much slower than Fluent's own field templates the custom frame is allowed to be.</summary>
    private const double BudgetShare = 1.3;

    public static IEnumerable<BenchScenario> Create(Fixtures fixtures)
    {
        yield return FormCreate();
        yield return FormRestyle();
    }

    /// <summary>Build the form, put it in the window and lay it out once.</summary>
    private static BenchScenario FormCreate() =>
        new(
            "form.create",
            "form",
            Heavy: false,
            PrepareAsync: async cancellationToken =>
            {
                var rig = await Rig.GetAsync(cancellationToken).ConfigureAwait(false);
                var measured = new List<double>();
                return new BenchCase
                {
                    Warmup = 5,
                    Iterations = 20,
                    ProcessWideAllocation = true,
                    BaselineName = "Fluent's own field templates",
                    Note = $"{TextFields} text + {Dropdowns} dropdowns + {Numbers} numbers, build + first layout",
                    Run = async _ =>
                    {
                        var result = await rig.BuildAsync(forge: true).ConfigureAwait(false);
                        measured.Add(result.Ms);
                        return new OpResult(Items: result.Fields, Observation: $"build+layout={result.Ms:F2} ms");
                    },
                    Baseline = async _ =>
                    {
                        var result = await rig.BuildAsync(forge: false).ConfigureAwait(false);
                        return new OpResult(Items: result.Fields);
                    },
                    Check = comparison => comparison.Ratio is { } ratio
                        ? new Verdict(
                            $"the Forge field themes stay within {BudgetShare:P0} of Fluent's on a form build (p50)",
                            $"{comparison.Implementation.P50Ms.ToString("F1", CultureInfo.InvariantCulture)} ms vs Fluent "
                            + $"{comparison.Baseline?.P50Ms.ToString("F1", CultureInfo.InvariantCulture) ?? "n/a"} ms "
                            + $"= {ratio.ToString("0.00", CultureInfo.InvariantCulture)}x",
                            ratio <= BudgetShare)
                        : null,
                };
            });

    /// <summary>
    /// Re-evaluating the styles of a built form: every field takes and gives back the pointer's state, then the layout is
    /// flushed. This is the cost the extra rules in the sheet can add to a hover, not to a build.
    /// </summary>
    private static BenchScenario FormRestyle() =>
        new(
            "form.restyle",
            "form",
            Heavy: false,
            PrepareAsync: async cancellationToken =>
            {
                var rig = await Rig.GetAsync(cancellationToken).ConfigureAwait(false);
                var measured = new List<double>();
                return new BenchCase
                {
                    Warmup = 5,
                    Iterations = 24,
                    ProcessWideAllocation = true,
                    BaselineName = "Fluent's own field templates",
                    Note = $"pointer state toggled on all {TextFields + Dropdowns + Numbers} fields, then one layout flush",
                    Run = async _ =>
                    {
                        var result = await rig.RestyleAsync(forge: true).ConfigureAwait(false);
                        measured.Add(result.Ms);
                        return new OpResult(Items: result.Fields, Observation: $"restyle={result.Ms:F2} ms");
                    },
                    Baseline = async _ =>
                    {
                        var result = await rig.RestyleAsync(forge: false).ConfigureAwait(false);
                        return new OpResult(Items: result.Fields);
                    },
                    Check = comparison => comparison.Ratio is { } ratio
                        ? new Verdict(
                            $"the Forge field themes stay within {BudgetShare:P0} of Fluent's on a style re-evaluation (p50)",
                            $"{comparison.Implementation.P50Ms.ToString("F3", CultureInfo.InvariantCulture)} ms vs Fluent "
                            + $"{comparison.Baseline?.P50Ms.ToString("F3", CultureInfo.InvariantCulture) ?? "n/a"} ms "
                            + $"= {ratio.ToString("0.00", CultureInfo.InvariantCulture)}x",
                            ratio <= BudgetShare)
                        : null,
                };
            });

    private readonly record struct Step(double Ms, int Fields);

    /// <summary>The window and the UI thread the two arms share; built once.</summary>
    private sealed class Rig
    {
        private static Rig? shared;
        private static Task<Rig>? starting;

        private Window window = null!;
        private Control? content;

        public static Task<Rig> GetAsync(CancellationToken cancellationToken) =>
            starting ??= StartAsync(cancellationToken);

        private static async Task<Rig> StartAsync(CancellationToken cancellationToken)
        {
            var ready = new TaskCompletionSource<Rig>(TaskCreationOptions.RunContinuationsAsynchronously);
            var thread = new Thread(() =>
            {
                try
                {
                    Forge.App.Program.BuildAvaloniaApp().SetupWithoutStarting();
                    var rig = new Rig();
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

        private void Build()
        {
            window = new Window
            {
                Width = 1040,
                Height = 760,
                ShowInTaskbar = false,
                Title = "forge-bench field",
            };
            window.Show();
            Dispatcher.UIThread.RunJobs();
        }

        /// <summary>Builds the form and lays it out once; returns the UI-thread time of the build and the layout flush.</summary>
        public async Task<Step> BuildAsync(bool forge) =>
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                var form = Form(forge);
                var watch = Stopwatch.StartNew();
                window.Content = form;
                window.UpdateLayout();
                var elapsed = watch.Elapsed.TotalMilliseconds;
                content = form;
                return new Step(elapsed, Fields(form));
            });

        /// <summary>Toggles the pointer state on every field of the form the last build left in the window, then flushes layout.</summary>
        public async Task<Step> RestyleAsync(bool forge) =>
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                var form = content ?? throw new InvalidOperationException("build the form before restyling it");
                var fields = Inputs(form).ToArray();
                var watch = Stopwatch.StartNew();
                foreach (var field in fields)
                {
                    ((IPseudoClasses)field.Classes).Set(":pointerover", true);
                }

                window.UpdateLayout();
                foreach (var field in fields)
                {
                    ((IPseudoClasses)field.Classes).Set(":pointerover", false);
                }

                window.UpdateLayout();
                return new Step(watch.Elapsed.TotalMilliseconds, fields.Length);
            });

        /// <summary>
        /// One settings page's shape: a scrolling column of 150 px label + field rows. With <paramref name="forge"/> the
        /// subtree carries the same sheet a settings page pulls in; without it the fields keep Fluent's own templates.
        /// </summary>
        private static UserControl Form(bool forge)
        {
            var rows = new StackPanel { Spacing = 6 };
            for (var index = 0; index < TextFields; index++)
            {
                rows.Children.Add(Row($"字段 {index}", new TextBox { Text = "值", PlaceholderText = "输入…" }));
            }

            for (var index = 0; index < Dropdowns; index++)
            {
                var combo = new ComboBox { ItemsSource = new[] { "选项一", "选项二", "选项三" }, SelectedIndex = 0 };
                rows.Children.Add(Row($"下拉 {index}", combo));
            }

            for (var index = 0; index < Numbers; index++)
            {
                rows.Children.Add(Row($"数值 {index}", new NumericUpDown { Value = index, Minimum = 0, Maximum = 100 }));
            }

            var page = new UserControl { Content = new ScrollViewer { Content = rows, Padding = new Thickness(12, 6) } };
            if (forge)
            {
                // The product gets this sheet from XAML at compile time; the bench attaches the same sheet at runtime so the
                // two arms differ in exactly one thing. Runtime StyleInclude is trim-unsafe by contract - acceptable here,
                // the bench is not published.
#pragma warning disable IL2026
                page.Styles.Add(new StyleInclude(new Uri("avares://Forge.App/"))
                {
                    Source = new Uri("avares://Forge.App/Resources/ChromeStyles.axaml"),
                });
#pragma warning restore IL2026
            }

            return page;
        }

        private static Grid Row(string label, Control field)
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("150,*"), Margin = new Thickness(0, 3) };
            var text = new TextBlock { Text = label, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
            Grid.SetColumn(field, 1);
            row.Children.Add(text);
            row.Children.Add(field);
            return row;
        }

        private static IEnumerable<Control> Inputs(Visual root) =>
            root.GetVisualDescendants().OfType<Control>().Where(control => control.GetType().Name is "TextBox" or "ComboBox" or "NumericUpDown");

        private static int Fields(Visual root) => Inputs(root).Count();
    }
}
