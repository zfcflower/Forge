using System.Globalization;
using Avalonia.Threading;
using Forge.App.Localization;
using Forge.App.ViewModels;
using Forge.Editor.Lsp;
using Forge.Editor.Text;
using Xunit;

namespace Forge.App.Tests.ViewModels;

/// <summary>
/// Pins <see cref="ProblemsPanelViewModel"/> against a fake <see cref="IWorkspaceDiagnosticSource"/>: the rows come from the
/// source's snapshot in the order it returns them, the four severity filters hide rows and re-summarise, the row cap reports
/// truncation without losing the real totals, opening a row forwards its diagnostic, a change event leads to a fresh list,
/// and no source at all reads as "no problems". A refresh over an unchanged snapshot reuses both the list instance and the
/// row objects (that identity is what keeps a selection alive across a publish), while a diagnostic whose key changed gets a
/// new row, and the summary counts everything the filters let through rather than the rows the cap kept. "Has problems" is
/// the row list itself: an empty <see cref="ProblemsPanelViewModel.Items"/> is what the view shows as the empty state.
///
/// Headless note: the panel's refreshes after a filter toggle or a source change are fire-and-forget and resume on the
/// dispatcher, so every step pumps the dispatcher until the expected state is visible instead of awaiting them.
/// </summary>
[Collection("forge-app-ui")]
public sealed class ProblemsPanelTests
{
    [Fact]
    public async Task Refresh_builds_one_row_per_diagnostic_in_the_order_the_source_returns_them()
    {
        await SharedUi.RunAsync(async () =>
        {
            var source = new FakeDiagnosticSource(
            [
                Diagnostic(DiagnosticSeverity.Error, "first", line: 0),
                Diagnostic(DiagnosticSeverity.Warning, "second", line: 1),
                Diagnostic(DiagnosticSeverity.Information, "third", line: 2),
                Diagnostic(DiagnosticSeverity.Hint, "fourth", line: 3),
            ]);
            using var panel = new ProblemsPanelViewModel(source, _ => { });

            await panel.RefreshAsync(CancellationToken.None);

            Assert.Equal(["first", "second", "third", "fourth"], panel.Items.Select(static row => row.Message).ToArray());
            Assert.Equal(["1:1", "2:1", "3:1", "4:1"], panel.Items.Select(static row => row.Location).ToArray());
            Assert.All(panel.Items, static row => Assert.Equal("Program.cs", row.FileName));
            Assert.Equal([true, false, false, false], panel.Items.Select(static row => row.IsError).ToArray());
            Assert.False(panel.IsTruncated);
            Assert.Equal(string.Empty, panel.TruncatedNotice);
            Assert.Equal(
                Loc.Instance.Format(InspectorStrings.ProblemsSummaryPattern, "1", "1", "4"),
                panel.Summary);
        });
    }

    [Fact]
    public async Task Each_severity_filter_hides_its_rows_and_recounts_the_summary()
    {
        await SharedUi.RunAsync(async () =>
        {
            var source = new FakeDiagnosticSource(
            [
                Diagnostic(DiagnosticSeverity.Error, "first"),
                Diagnostic(DiagnosticSeverity.Warning, "second"),
                Diagnostic(DiagnosticSeverity.Information, "third"),
                Diagnostic(DiagnosticSeverity.Hint, "fourth"),
            ]);
            using var panel = new ProblemsPanelViewModel(source, _ => { });
            await panel.RefreshAsync(CancellationToken.None);
            Assert.Equal(4, panel.Items.Count);

            var cases = new (Action Hide, Action Show, string Hidden, string Summary)[]
            {
                (() => panel.ShowErrors = false, () => panel.ShowErrors = true, "first",
                    Loc.Instance.Format(InspectorStrings.ProblemsSummaryPattern, "0", "1", "3")),
                (() => panel.ShowWarnings = false, () => panel.ShowWarnings = true, "second",
                    Loc.Instance.Format(InspectorStrings.ProblemsSummaryPattern, "1", "0", "3")),
                (() => panel.ShowInformation = false, () => panel.ShowInformation = true, "third",
                    Loc.Instance.Format(InspectorStrings.ProblemsSummaryPattern, "1", "1", "3")),
                (() => panel.ShowHints = false, () => panel.ShowHints = true, "fourth",
                    Loc.Instance.Format(InspectorStrings.ProblemsSummaryPattern, "1", "1", "3")),
            };

            foreach (var (hide, show, hidden, summary) in cases)
            {
                hide();
                await Until(() => panel.Items.Count == 3);
                Assert.DoesNotContain(hidden, panel.Items.Select(static row => row.Message));
                Assert.Equal(summary, panel.Summary);

                show();
                await Until(() => panel.Items.Count == 4);
            }

            // Nothing ticked leaves no rows at all, so the panel is back to "nothing to show".
            panel.ShowErrors = false;
            panel.ShowWarnings = false;
            panel.ShowInformation = false;
            panel.ShowHints = false;
            await Until(() => panel.Items.Count == 0);
            Assert.Empty(panel.Items);
            Assert.False(panel.IsTruncated);
        });
    }

    [Fact]
    public async Task The_row_cap_truncates_the_list_but_keeps_the_real_totals_in_the_summary()
    {
        await SharedUi.RunAsync(async () =>
        {
            var total = ProblemsPanelViewModel.MaxRows + 5;
            var diagnostics = new List<WorkspaceDiagnostic>(total);
            for (var i = 0; i < total; i++)
            {
                diagnostics.Add(Diagnostic(
                    i % 2 == 0 ? DiagnosticSeverity.Error : DiagnosticSeverity.Warning,
                    "problem " + i.ToString(CultureInfo.InvariantCulture),
                    line: i));
            }

            var source = new FakeDiagnosticSource(diagnostics);
            using var panel = new ProblemsPanelViewModel(source, _ => { });

            await panel.RefreshAsync(CancellationToken.None);

            Assert.Equal(ProblemsPanelViewModel.MaxRows, panel.Items.Count);
            Assert.True(panel.IsTruncated);
            Assert.Equal(InspectorStrings.ProblemsTruncatedRows(ProblemsPanelViewModel.MaxRows).Resolve(), panel.TruncatedNotice);
            // The rows are the first 2000 of the snapshot, in order: truncation drops the tail, it does not re-sort.
            Assert.Equal("problem 0", panel.Items[0].Message);
            Assert.Equal("problem " + (ProblemsPanelViewModel.MaxRows - 1).ToString(CultureInfo.InvariantCulture), panel.Items[^1].Message);
            // 1003 errors and 1002 warnings went in; the list shows 2000 of them, the summary says how many exist (2005).
            Assert.Equal(
                Loc.Instance.Format(
                    InspectorStrings.ProblemsSummaryPattern,
                    TextFormat.Number(1003),
                    TextFormat.Number(1002),
                    TextFormat.Number(total)),
                panel.Summary);
        });
    }

    [Fact]
    public async Task A_refresh_over_the_same_snapshot_keeps_the_list_and_its_row_objects()
    {
        await SharedUi.RunAsync(async () =>
        {
            var source = new FakeDiagnosticSource(
            [
                Diagnostic(DiagnosticSeverity.Error, "first", line: 0),
                Diagnostic(DiagnosticSeverity.Warning, "second", line: 1),
            ]);
            using var panel = new ProblemsPanelViewModel(source, _ => { });
            await panel.RefreshAsync(CancellationToken.None);
            var before = panel.Items;

            await panel.RefreshAsync(CancellationToken.None);

            // The second pass found the same diagnostics, so the bound list is not replaced and the rows are the same
            // objects: that identity is what survives a publish as the ListBox selection.
            Assert.Same(before, panel.Items);
            Assert.Same(before[0], panel.Items[0]);
            Assert.Same(before[1], panel.Items[1]);
        });
    }

    [Fact]
    public async Task A_changed_diagnostic_gets_a_new_row_while_an_unchanged_one_keeps_its_object()
    {
        await SharedUi.RunAsync(async () =>
        {
            var unchanged = Diagnostic(DiagnosticSeverity.Error, "first", line: 0);
            var source = new FakeDiagnosticSource([unchanged, Diagnostic(DiagnosticSeverity.Warning, "before", line: 1)]);
            using var panel = new ProblemsPanelViewModel(source, _ => { });
            await panel.RefreshAsync(CancellationToken.None);
            var unchangedRow = panel.Items[0];
            var replacedRow = panel.Items[1];
            var before = panel.Items;

            // Same file, severity and place; only the second message changed, so only the second row's key changed.
            source.Replace([unchanged, Diagnostic(DiagnosticSeverity.Warning, "after", line: 1)]);
            await panel.RefreshAsync(CancellationToken.None);

            Assert.Equal(["first", "after"], panel.Items.Select(static row => row.Message).ToArray());
            // A changed sequence does replace the list, which is what makes the reuse assertion above mean something.
            Assert.NotSame(before, panel.Items);
            Assert.Same(unchangedRow, panel.Items[0]);
            Assert.NotSame(replacedRow, panel.Items[1]);
            Assert.Equal("after", panel.Items[1].Message);
        });
    }

    [Fact]
    public async Task Opening_a_row_forwards_that_rows_diagnostic()
    {
        await SharedUi.RunAsync(async () =>
        {
            var diagnostic = Diagnostic(DiagnosticSeverity.Warning, "second");
            var source = new FakeDiagnosticSource([Diagnostic(DiagnosticSeverity.Error, "first"), diagnostic]);
            WorkspaceDiagnostic? opened = null;
            using var panel = new ProblemsPanelViewModel(source, target => opened = target);
            await panel.RefreshAsync(CancellationToken.None);

            panel.Open(panel.Items[1]);

            Assert.Same(diagnostic, opened);
        });
    }

    [Fact]
    public async Task A_change_event_from_the_source_leads_to_a_refreshed_list()
    {
        await SharedUi.RunAsync(async () =>
        {
            var source = new FakeDiagnosticSource([Diagnostic(DiagnosticSeverity.Error, "first")]);
            using var panel = new ProblemsPanelViewModel(source, _ => { });
            await panel.RefreshAsync(CancellationToken.None);
            Assert.Single(panel.Items);

            source.Replace(
            [
                Diagnostic(DiagnosticSeverity.Error, "first"),
                Diagnostic(DiagnosticSeverity.Warning, "later"),
            ]);
            source.RaiseChanged();

            await Until(() => panel.Items.Count == 2);
            Assert.Equal(["first", "later"], panel.Items.Select(static row => row.Message).ToArray());
            Assert.Equal(
                Loc.Instance.Format(InspectorStrings.ProblemsSummaryPattern, "1", "1", "2"),
                panel.Summary);
        });
    }

    [Fact]
    public async Task A_null_source_reports_no_problems()
    {
        await SharedUi.RunAsync(async () =>
        {
            using var panel = new ProblemsPanelViewModel(null, _ => { });

            await panel.RefreshAsync(CancellationToken.None);

            Assert.Empty(panel.Items);
            Assert.False(panel.IsTruncated);
            Assert.Equal(string.Empty, panel.TruncatedNotice);
            Assert.Equal(Loc.Instance[InspectorStrings.ProblemsNone], panel.Summary);
        });
    }

    private static WorkspaceDiagnostic Diagnostic(DiagnosticSeverity severity, string message, int line = 0) =>
        new(
            Path.Combine(Path.GetTempPath(), "forge-problems", "Program.cs"),
            Path.Combine(Path.GetTempPath(), "forge-problems"),
            PositionEncoding.Utf16,
            severity,
            new LspRange(new LspPosition(line, 0), new LspPosition(line, 1)),
            message,
            "CS1",
            "csharp");

    /// <summary>Runs queued work (including the panel's dispatcher-posted refresh) until <paramref name="condition"/> holds.</summary>
    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            Dispatcher.UIThread.RunJobs();
            if (condition())
            {
                return;
            }

            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for the problems panel to refresh.");
            await Task.Delay(10);
        }
    }

    private sealed class FakeDiagnosticSource : IWorkspaceDiagnosticSource
    {
        private IReadOnlyList<WorkspaceDiagnostic> snapshot;

        public FakeDiagnosticSource(IReadOnlyList<WorkspaceDiagnostic> snapshot) => this.snapshot = snapshot;

        public event EventHandler? Changed;

        public IReadOnlyList<WorkspaceDiagnostic> Snapshot() => snapshot;

        public void Replace(IReadOnlyList<WorkspaceDiagnostic> next) => snapshot = next;

        public void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);
    }
}
