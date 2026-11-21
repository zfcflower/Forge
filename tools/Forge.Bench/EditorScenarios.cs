using System.Diagnostics;
using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Forge.App.Localization;
using Forge.App.ViewModels;
using Forge.App.Views;

namespace Forge.Bench;

/// <summary>
/// The <c>editor</c> command: what one click on a file in the file tree costs the UI thread. The click reads the file, builds
/// a <see cref="FileDocumentView"/> (XAML plus the editor bridge: TextMate grammar and theme installation, folding, the
/// review overlay) and lays it out once for the first frame - all of it on the UI thread except the read itself.
/// <para>Each iteration is a cold open: a fresh document view model and a fresh view, exactly like a first click after
/// start-up. The phases are printed per iteration, so a cost that is paid again for every document instead of once per
/// process is visible as a number that does not settle.</para>
/// </summary>
internal static class EditorScenarios
{
    /// <summary>A realistic source file: a type with forty members, about two thousand lines and 64 KiB of text.</summary>
    private const int CSharpMembers = 240;

    /// <summary>The same byte count as plain text, where no grammar is installed: the control case.</summary>
    private const int PlainBytes = 64 * 1024;

    public static IEnumerable<BenchScenario> Create(Fixtures fixtures)
    {
        yield return Open(fixtures, "cs-4KiB", ".cs", CSharpSource(15));
        yield return Open(fixtures, "cs-64KiB", ".cs", CSharpSource());
        yield return Open(fixtures, "txt-64KiB", ".txt", Fixtures.MakeText(PlainBytes));
        yield return Open(fixtures, "cs-64KiB-second", ".cs", CSharpSource(), second: true);
    }

    /// <summary>
    /// The same open a second time in a process that already opened this language: if the grammar or the theme is installed
    /// per document, this row costs what the first one did.
    /// </summary>
    private static BenchScenario Open(Fixtures fixtures, string label, string extension, string content, bool second = false)
    {
        return new BenchScenario(
            $"editor.preview-open.{label}",
            "editor",
            Heavy: false,
            PrepareAsync: async cancellationToken =>
            {
                var path = fixtures.Workspace.WriteTextFile($"editor/preview-{label}{extension}", content);
                var rig = await EditorRig.GetAsync(cancellationToken).ConfigureAwait(false);
                if (second)
                {
                    // Warm the language once outside the timed iterations.
                    await rig.OpenAsync(path, cancellationToken).ConfigureAwait(false);
                }

                return new BenchCase
                {
                    Warmup = 1,
                    Iterations = 5,
                    ProcessWideAllocation = true,
                    Note = $"{content.Length / 1024} KiB, {extension.TrimStart('.')}, UI thread",
                    Run = async cancellationToken =>
                    {
                        var report = await rig.OpenAsync(path, cancellationToken).ConfigureAwait(false);
                        Console.Error.WriteLine($"editor.preview-open.{label}: {report}");
                        return new OpResult(Items: 1, Signature: (long)report.ClickMs, Observation: report.Line);
                    },
                };
            });
    }

    /// <summary>A C# file with the shapes the grammar has to tokenize: types, attributes, generics, strings, comments.</summary>
    private static string CSharpSource(int members = CSharpMembers)
    {
        var builder = new StringBuilder(64 * 1024);
        builder.AppendLine("using System;");
        builder.AppendLine("using System.Collections.Generic;");
        builder.AppendLine();
        builder.AppendLine("namespace Forge.Bench.Fixture;");
        builder.AppendLine();
        builder.AppendLine("/// <summary>A generated type: the grammar has to see the same tokens real code has.</summary>");
        builder.AppendLine("internal sealed class FixtureType");
        builder.AppendLine("{");
        for (var member = 0; member < members; member++)
        {
            builder.AppendLine(CultureInfo.InvariantCulture, $"    /// <summary>Member {member}: a doc comment.</summary>");
            builder.AppendLine(CultureInfo.InvariantCulture, $"    [Obsolete(\"member {member}\")]");
            builder.AppendLine(CultureInfo.InvariantCulture, $"    public int Member{member}(string input, IReadOnlyList<int> values)");
            builder.AppendLine("    {");
            builder.AppendLine(CultureInfo.InvariantCulture, $"        var name = $\"member {member}: {{input}}\";");
            builder.AppendLine("        var total = 0;");
            builder.AppendLine("        foreach (var value in values)");
            builder.AppendLine("        {");
            builder.AppendLine("            total += value * 2;");
            builder.AppendLine("        }");
            builder.AppendLine();
            builder.AppendLine("        // A trailing comment with a path: src/Forge.Bench/Fixture.cs.");
            builder.AppendLine(CultureInfo.InvariantCulture, $"        return total + name.Length + {member};");
            builder.AppendLine("    }");
            builder.AppendLine();
        }

        builder.AppendLine("}");
        return builder.ToString();
    }

    /// <summary>
    /// One open, phase by phase: the read, the view's XAML, the bare editor template and its first layout, then the document
    /// binding that builds the editor bridge (highlighting, folding, adornments) and the layout that follows it. The totals
    /// are the UI thread's share of the click.
    /// </summary>
    private sealed record OpenReport(double LoadMs, double ViewMs, double TemplateMs, double BridgeMs, double DeferredMs)
    {
        public double ClickMs => LoadMs + ViewMs + TemplateMs + BridgeMs;

        public string Line => string.Create(
            CultureInfo.InvariantCulture,
            $"load {LoadMs:F1} · xaml {ViewMs:F1} · template {TemplateMs:F1} · bridge {BridgeMs:F1} · [click {ClickMs:F1}] · deferred {DeferredMs:F1} ms");
    }

    /// <summary>The Avalonia UI thread the views are built on; one per process, shared by every case of this family.</summary>
    private sealed class EditorRig
    {
        private static Task<EditorRig>? starting;
        private Window window = null!;

        public static Task<EditorRig> GetAsync(CancellationToken cancellationToken) => starting ??= StartAsync(cancellationToken);

        private static async Task<EditorRig> StartAsync(CancellationToken cancellationToken)
        {
            // Another family may already own the process-wide setup (the transcript rig does). Then its thread is the UI
            // thread and this family only posts to it.
            if (Application.Current is not null)
            {
                var existing = new EditorRig();
                await existing.CreateWindowAsync().ConfigureAwait(false);
                return existing;
            }

            var ready = new TaskCompletionSource<EditorRig>(TaskCreationOptions.RunContinuationsAsynchronously);
            var thread = new Thread(() =>
            {
                try
                {
                    Forge.App.Program.BuildAvaloniaApp().SetupWithoutStarting();
                    Loc.EnsureRegistered();
                    var rig = new EditorRig();
                    rig.window = new Window { Width = 1200, Height = 800, ShowInTaskbar = false, Title = "forge-bench editor" };
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

        private Task CreateWindowAsync() => Dispatcher.UIThread.InvokeAsync(() =>
        {
            Loc.EnsureRegistered();
            window = new Window { Width = 1200, Height = 800, ShowInTaskbar = false, Title = "forge-bench editor" };
            window.Show();
            Dispatcher.UIThread.RunJobs();
        }).GetTask();

        public Task<OpenReport> OpenAsync(string path, CancellationToken cancellationToken) =>
            Dispatcher.UIThread.InvokeAsync(async () =>
            {
                var watch = Stopwatch.StartNew();
                var model = FileDocumentViewModel.Create();
                await model.LoadAsync(path, cancellationToken).ConfigureAwait(true);
                var loadMs = watch.Elapsed.TotalMilliseconds;

                watch.Restart();
                var view = new FileDocumentView();
                var viewMs = watch.Elapsed.TotalMilliseconds;

                // The editor's own template and first layout, without a document: what the shell pays for the empty view.
                watch.Restart();
                window.Content = view;
                window.UpdateLayout();
                var templateMs = watch.Elapsed.TotalMilliseconds;

                // Binding the document builds the bridge: folding, adornments, the review overlay, highlighting.
                watch.Restart();
                view.DataContext = model;
                window.UpdateLayout();
                var bridgeMs = watch.Elapsed.TotalMilliseconds;

                // Everything the click does not wait for: queued work at the frame's own priority and later.
                watch.Restart();
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                var deferredMs = watch.Elapsed.TotalMilliseconds;

                window.Content = null;
                Dispatcher.UIThread.RunJobs();
                await model.ReleaseDocumentAsync().ConfigureAwait(true);
                return new OpenReport(loadMs, viewMs, templateMs, bridgeMs, deferredMs);
            });
    }
}
