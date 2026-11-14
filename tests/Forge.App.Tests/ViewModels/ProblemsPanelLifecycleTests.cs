using System.Reflection;
using Avalonia.Threading;
using Forge.App.Localization;
using Forge.App.Preferences;
using Forge.App.ViewModels;
using Forge.Editor.Lsp;
using Forge.Editor.Text;
using Xunit;

namespace Forge.App.Tests.ViewModels;

/// <summary>
/// The lifecycle of <see cref="ProblemsPanelViewModel"/> beyond what <see cref="ProblemsPanelTests"/> covers: disposal
/// unsubscribes it from both event sources and a refresh after it is a no-op for the source, a language switch drops the cached
/// rows, a storm of change events collapses into one extra pass, a snapshot that throws reads as the failed summary (and the
/// next good pass recovers), and the snapshot is read on a worker thread rather than on the UI thread.
/// </summary>
[Collection("forge-app-ui")]
public sealed class ProblemsPanelLifecycleTests
{
    [Fact]
    public async Task Dispose_unsubscribes_from_the_source_and_the_language_and_a_later_refresh_does_not_read_the_source()
    {
        await SharedUi.RunAsync(async () =>
        {
            var source = new CountingSource([Diagnostic("first")]);
            var panel = new ProblemsPanelViewModel(source, _ => { });
            await panel.RefreshAsync(CancellationToken.None);
            Assert.Equal(1, source.Subscribers);
            Assert.Contains(panel, LanguageSubscribers());
            var reads = source.SnapshotCalls;

            panel.Dispose();

            Assert.Equal(0, source.Subscribers);
            Assert.DoesNotContain(panel, LanguageSubscribers());

            // A late publish and a late refresh (the command, a filter toggle in flight) must neither read the source nor throw.
            source.RaiseChanged();
            await Pump(150);
            await panel.RefreshAsync(CancellationToken.None);
            panel.StartRefresh();
            await Pump(50);
            Assert.Equal(reads, source.SnapshotCalls);
            Assert.False(panel.RefreshCommand.CanExecute(null));

            // Disposing twice is harmless.
            panel.Dispose();
        });
    }

    [Fact]
    public async Task A_language_switch_drops_the_cached_rows_and_translates_the_summary()
    {
        await SharedUi.RunAsync(async () =>
        {
            var previous = Loc.Instance.Language;
            Loc.Instance.Language = UiLanguage.English;
            try
            {
                var source = new CountingSource([Diagnostic("first")]);
                using var panel = new ProblemsPanelViewModel(source, _ => { });
                await panel.RefreshAsync(CancellationToken.None);
                var row = panel.Items[0];
                var summary = panel.Summary;
                var reads = source.SnapshotCalls;

                // Without the switch an identical snapshot reuses the row object; that is what makes the next assertion mean it.
                await panel.RefreshAsync(CancellationToken.None);
                Assert.Same(row, panel.Items[0]);

                Loc.Instance.Language = UiLanguage.Chinese;
                await Until(() => !ReferenceEquals(panel.Items[0], row));

                Assert.True(source.SnapshotCalls > reads + 1, "the switch triggered a fresh pass over the source");
                Assert.NotEqual(summary, panel.Summary);
                Assert.Equal(Loc.Instance.Format(InspectorStrings.ProblemsSummaryPattern, "1", "0", "1"), panel.Summary);
                Assert.Equal(Loc.Instance[InspectorStrings.EditorLspSeverityError], panel.Items[0].SeverityText);
            }
            finally
            {
                Loc.Instance.Language = previous;
            }
        });
    }

    [Fact]
    public async Task A_storm_of_change_events_collapses_into_at_most_one_extra_pass()
    {
        await SharedUi.RunAsync(async () =>
        {
            using var firstPassGate = new ManualResetEventSlim(false);
            var source = new CountingSource([Diagnostic("first")]) { BlockFirstCallOn = firstPassGate };
            using var panel = new ProblemsPanelViewModel(source, _ => { });

            for (var i = 0; i < 100; i++)
            {
                source.RaiseChanged();
            }

            // The first pass is now running on a worker and parked inside Snapshot().
            await Until(() => source.SnapshotCalls == 1);

            // More publishes land while it is still in flight; they only mark the result for one more pass.
            source.Replace([Diagnostic("first"), Diagnostic("second", line: 1)]);
            for (var i = 0; i < 100; i++)
            {
                source.RaiseChanged();
            }

            await Pump(100);
            Assert.Equal(1, source.SnapshotCalls);

            firstPassGate.Set();
            await Until(() => panel.Items.Count == 2);
            await Pump(250);

            Assert.Equal(2, source.SnapshotCalls);
            Assert.Equal(["first", "second"], panel.Items.Select(static row => row.Message).ToArray());
        });
    }

    [Fact]
    public async Task A_snapshot_that_throws_gives_the_failed_summary_and_the_next_good_pass_recovers()
    {
        await SharedUi.RunAsync(async () =>
        {
            var source = new CountingSource([Diagnostic("first")]) { Failure = new InvalidOperationException("store is gone") };
            using var panel = new ProblemsPanelViewModel(source, _ => { });

            // The failure is reported on the summary, not thrown into the caller (a fire-and-forget refresh would lose it).
            await panel.RefreshAsync(CancellationToken.None);

            Assert.Equal(Loc.Instance[InspectorStrings.ProblemsFailed], panel.Summary);
            Assert.Empty(panel.Items);

            source.Failure = null;
            await panel.RefreshAsync(CancellationToken.None);

            Assert.Equal(["first"], panel.Items.Select(static row => row.Message).ToArray());
            Assert.Equal(Loc.Instance.Format(InspectorStrings.ProblemsSummaryPattern, "1", "0", "1"), panel.Summary);
        });
    }

    [Fact]
    public async Task A_failed_pass_keeps_the_previous_rows_in_the_list()
    {
        await SharedUi.RunAsync(async () =>
        {
            var source = new CountingSource([Diagnostic("first")]);
            using var panel = new ProblemsPanelViewModel(source, _ => { });
            await panel.RefreshAsync(CancellationToken.None);
            var rows = panel.Items;

            source.Failure = new InvalidOperationException("store is gone");
            await panel.RefreshAsync(CancellationToken.None);

            Assert.Equal(Loc.Instance[InspectorStrings.ProblemsFailed], panel.Summary);
            Assert.Same(rows, panel.Items);
        });
    }

    [Fact]
    public async Task The_snapshot_is_read_off_the_ui_thread()
    {
        await SharedUi.RunAsync(async () =>
        {
            var source = new CountingSource([Diagnostic("first")]);
            using var panel = new ProblemsPanelViewModel(source, _ => { });
            var callerThread = Environment.CurrentManagedThreadId;
            Assert.True(Dispatcher.UIThread.CheckAccess());
            var callerContext = SynchronizationContext.Current;

            await panel.RefreshAsync(CancellationToken.None);

            // The caller is the UI thread, so a snapshot read on another thread (without the UI context) was read on a worker.
            Assert.Equal(1, source.SnapshotCalls);
            Assert.NotEqual(callerThread, source.LastThreadId);
            Assert.False(source.LastOnUiThread);
            Assert.NotSame(callerContext, source.LastContext);
            // The result still lands on the caller's thread.
            Assert.Equal(callerThread, Environment.CurrentManagedThreadId);
            Assert.Single(panel.Items);
        });
    }

    private static WorkspaceDiagnostic Diagnostic(string message, int line = 0) =>
        new(
            Path.Combine(Path.GetTempPath(), "forge-problems", "Program.cs"),
            Path.Combine(Path.GetTempPath(), "forge-problems"),
            PositionEncoding.Utf16,
            DiagnosticSeverity.Error,
            new LspRange(new LspPosition(line, 0), new LspPosition(line, 1)),
            message,
            "CS1",
            "csharp");

    /// <summary>The objects currently subscribed to <see cref="Loc.LanguageChanged"/> (the event is field-like).</summary>
    private static List<object?> LanguageSubscribers()
    {
        var field = typeof(Loc).GetField(nameof(Loc.LanguageChanged), BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Loc.LanguageChanged is no longer a field-like event; update this helper.");
        return ((MulticastDelegate?)field.GetValue(Loc.Instance))?.GetInvocationList().Select(static d => d.Target).ToList() ?? [];
    }

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

            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for the problems panel.");
            await Task.Delay(10);
        }
    }

    private static async Task Pump(int milliseconds)
    {
        var until = DateTime.UtcNow.AddMilliseconds(milliseconds);
        while (DateTime.UtcNow < until)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }
    }

    private sealed class CountingSource : IWorkspaceDiagnosticSource
    {
        private readonly object gate = new();
        private IReadOnlyList<WorkspaceDiagnostic> snapshot;
        private EventHandler? changed;
        private int subscribers;
        private int calls;

        public CountingSource(IReadOnlyList<WorkspaceDiagnostic> snapshot) => this.snapshot = snapshot;

        public event EventHandler? Changed
        {
            add { lock (gate) { changed += value; subscribers++; } }
            remove { lock (gate) { changed -= value; subscribers--; } }
        }

        public int Subscribers { get { lock (gate) { return subscribers; } } }

        public int SnapshotCalls => Volatile.Read(ref calls);

        public Exception? Failure { get; set; }

        public ManualResetEventSlim? BlockFirstCallOn { get; init; }

        public int LastThreadId { get; private set; }

        public bool LastOnUiThread { get; private set; }

        public SynchronizationContext? LastContext { get; private set; }

        public IReadOnlyList<WorkspaceDiagnostic> Snapshot()
        {
            LastThreadId = Environment.CurrentManagedThreadId;
            LastOnUiThread = Dispatcher.UIThread.CheckAccess();
            LastContext = SynchronizationContext.Current;
            var call = Interlocked.Increment(ref calls);
            if (call == 1)
            {
                BlockFirstCallOn?.Wait(TimeSpan.FromSeconds(20));
            }

            if (Failure is { } failure)
            {
                throw failure;
            }

            lock (gate) { return snapshot; }
        }

        public void Replace(IReadOnlyList<WorkspaceDiagnostic> next)
        {
            lock (gate) { snapshot = next; }
        }

        public void RaiseChanged()
        {
            EventHandler? handler;
            lock (gate) { handler = changed; }
            handler?.Invoke(this, EventArgs.Empty);
        }
    }
}
