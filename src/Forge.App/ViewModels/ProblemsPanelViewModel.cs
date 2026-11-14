using Avalonia.Threading;
using Forge.App.Localization;
using Forge.Editor.Lsp;
using Forge.Editor.Text;
using Forge.App.Logging;
using ZLogger;

namespace Forge.App.ViewModels;

/// <summary>
/// The Problems tab: every diagnostic the language sessions currently hold, from every workspace, as one severity-filtered
/// list. The editor already renders squiggles and end-of-line dots for the open file; this is the list you scan when you do
/// not know which file is wrong.
/// <para>Nothing is polled. <see cref="WorkspaceDiagnostics"/> raises one change per publish, which is coalesced into one
/// UI-thread refresh, and the flattening plus sorting runs off the UI thread — the same shape the review panel uses for its
/// queries.</para>
/// </summary>
internal sealed class ProblemsPanelViewModel : ObservableObject, IDisposable
{
    /// <summary>Upper bound of rows built per refresh; a workspace with more is reported as truncated instead of stalling.</summary>
    internal const int MaxRows = 2_000;

    private readonly IWorkspaceDiagnosticSource? source;
    private readonly Action<WorkspaceDiagnostic> open;
    private readonly FrameCoalescer<bool>? coalescer;
    /// <summary>
    /// The row objects of the last pass, keyed by what a row displays. A publish that repeats the same diagnostics gets the
    /// same objects back, so the list keeps its selected row and the view does not re-template rows that did not change.
    /// </summary>
    private Dictionary<ProblemRowKey, ProblemRowViewModel> rowsByKey = [];
    private IReadOnlyList<ProblemRowViewModel> items = [];
    private string summary = Loc.Instance[InspectorStrings.ProblemsNone];
    private bool isTruncated;
    private bool showErrors = true;
    private bool showWarnings = true;
    private bool showInformation = true;
    private bool showHints = true;
    private int refreshPending;
    private int refreshRequested;
    private bool disposed;

    public ProblemsPanelViewModel(IWorkspaceDiagnosticSource? source, Action<WorkspaceDiagnostic> open)
    {
        ArgumentNullException.ThrowIfNull(open);
        this.source = source;
        this.open = open;
        RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => !disposed, ReportFailure);
        if (source is not null)
        {
            source.Changed += OnSourceChanged;
            coalescer = new FrameCoalescer<bool>(_ => StartRefresh());
        }

        Loc.Instance.LanguageChanged += OnLanguageChanged;
    }

    /// <summary>The rows of the current refresh, already filtered by severity and started at the worst one.</summary>
    public IReadOnlyList<ProblemRowViewModel> Items
    {
        get => items;
        private set => Set(ref items, value);
    }

    /// <summary>"3 个错误 · 12 条问题" / "3 errors · 12 problems", re-read in the language of the moment it is displayed.</summary>
    public string Summary
    {
        get => summary;
        private set => Set(ref summary, value);
    }

    /// <summary>True when <see cref="MaxRows"/> cut the list; shown so a truncated list never reads as the whole truth.</summary>
    public bool IsTruncated
    {
        get => isTruncated;
        private set
        {
            if (Set(ref isTruncated, value))
            {
                OnPropertyChanged(nameof(TruncatedNotice));
            }
        }
    }

    /// <summary>"只显示前 2,000 条" / "Only the first 2,000 are shown", resolved when read so a language switch re-translates it.</summary>
    public string TruncatedNotice => isTruncated ? InspectorStrings.ProblemsTruncatedRows(MaxRows).Resolve() : string.Empty;

    public bool ShowErrors
    {
        get => showErrors;
        set
        {
            if (Set(ref showErrors, value))
            {
                StartRefresh();
            }
        }
    }

    public bool ShowWarnings
    {
        get => showWarnings;
        set
        {
            if (Set(ref showWarnings, value))
            {
                StartRefresh();
            }
        }
    }

    public bool ShowInformation
    {
        get => showInformation;
        set
        {
            if (Set(ref showInformation, value))
            {
                StartRefresh();
            }
        }
    }

    public bool ShowHints
    {
        get => showHints;
        set
        {
            if (Set(ref showHints, value))
            {
                StartRefresh();
            }
        }
    }

    public AsyncRelayCommand RefreshCommand { get; }

    /// <summary>Starts a refresh without awaiting it; the list updates when the pass publishes.</summary>
    public void StartRefresh() => _ = RefreshAsync(CancellationToken.None);

    /// <summary>Opens the file a row points at, with the caret on the diagnostic.</summary>
    public void Open(ProblemRowViewModel row)
    {
        ArgumentNullException.ThrowIfNull(row);
        open(row.Diagnostic);
    }

    /// <summary>
    /// Rebuilds the rows from the sessions' stores. Concurrent refreshes collapse: a call that arrives while a pass is
    /// running only marks the result for one more pass, which the running call then performs — a storm of publishes cannot
    /// stack up unbounded work on the UI thread.
    /// </summary>
    public async Task RefreshAsync(CancellationToken ct)
    {
        if (source is null || disposed)
        {
            rowsByKey = [];
            Items = [];
            IsTruncated = false;
            Summary = Loc.Instance[InspectorStrings.ProblemsNone];
            return;
        }

        if (Interlocked.Exchange(ref refreshPending, 1) != 0)
        {
            Volatile.Write(ref refreshRequested, 1);
            return;
        }

        try
        {
            do
            {
                Volatile.Write(ref refreshRequested, 0);
                var filters = new SeverityFilter(showErrors, showWarnings, showInformation, showHints);
                var previous = rowsByKey;
                var snapshot = source.Snapshot();
                var rows = await Task.Run(
                    () => BuildRows(snapshot, filters, previous),
                    ct).ConfigureAwait(true);

                if (disposed || ct.IsCancellationRequested)
                {
                    return;
                }

                rowsByKey = rows.ByKey;
                if (!items.SequenceEqual(rows.Rows))
                {
                    Items = rows.Rows;
                }

                IsTruncated = rows.Truncated;
                // The total counts everything the filters let through, not what the row cap kept: a summary that says "1003
                // errors · 1002 warnings · 2000 total" reads as a bug in the counts.
                Summary = rows.Matched == 0
                    ? Loc.Instance[InspectorStrings.ProblemsNone]
                    : InspectorStrings.ProblemsSummary(rows.Errors, rows.Warnings, rows.Matched).Resolve();
            }
            while (Volatile.Read(ref refreshRequested) == 1 && !ct.IsCancellationRequested);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            AppLogs.File.ZLogError(exception, $"listing problems failed");
            Summary = Loc.Instance[InspectorStrings.ProblemsFailed];
        }
        finally
        {
            Volatile.Write(ref refreshPending, 0);
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        Loc.Instance.LanguageChanged -= OnLanguageChanged;
        if (source is not null)
        {
            source.Changed -= OnSourceChanged;
        }

        coalescer?.Dispose();
    }

    /// <summary>
    /// The list-building pass, off the UI thread: filter by severity, count the two severities the summary names, stop at the
    /// row cap, and resolve the display strings in the language of the moment (a refresh after a language switch therefore
    /// comes back translated). Rows the previous pass already built are reused by key, which is what keeps a selection alive
    /// across publishes; only the single pass in flight touches these dictionaries.
    /// </summary>
    private static (ProblemRowViewModel[] Rows, Dictionary<ProblemRowKey, ProblemRowViewModel> ByKey, int Errors, int Warnings, bool Truncated, int Matched) BuildRows(
        IReadOnlyList<WorkspaceDiagnostic> diagnostics,
        SeverityFilter filter,
        Dictionary<ProblemRowKey, ProblemRowViewModel> previous)
    {
        var byKey = new Dictionary<ProblemRowKey, ProblemRowViewModel>(previous.Count);
        var rows = new List<ProblemRowViewModel>(Math.Min(diagnostics.Count, MaxRows));
        var errors = 0;
        var warnings = 0;
        var matched = 0;
        var truncated = false;
        foreach (var diagnostic in diagnostics)
        {
            if (!filter.Accepts(diagnostic.Severity))
            {
                continue;
            }

            if (diagnostic.Severity == DiagnosticSeverity.Error)
            {
                errors++;
            }
            else if (diagnostic.Severity == DiagnosticSeverity.Warning)
            {
                warnings++;
            }

            matched++;
            if (rows.Count >= MaxRows)
            {
                truncated = true;
                continue;
            }

            var key = new ProblemRowKey(diagnostic);
            var row = previous.TryGetValue(key, out var existing) ? existing : new ProblemRowViewModel(diagnostic);
            byKey[key] = row;
            rows.Add(row);
        }

        return ([.. rows], byKey, errors, warnings, truncated, matched);
    }

    private void OnSourceChanged(object? sender, EventArgs e) => coalescer?.Enqueue(true);

    /// <summary>
    /// Rows cache their display text because the row key has no language in it: a switch has to drop them, or the visible rows
    /// would keep the old wording while the summary line translated itself.
    /// </summary>
    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        rowsByKey = [];
        coalescer?.Enqueue(true);
    }

    private void ReportFailure(Exception exception)
    {
        AppLogs.File.ZLogError(exception, $"refreshing problems failed");
        Summary = Loc.Instance[InspectorStrings.ProblemsFailed];
    }

    /// <summary>
    /// Everything a row displays *and* everything opening it uses: two diagnostics with the same key produce the same row, so
    /// the workspace and the position encoding belong in it — reusing a row whose owner workspace differs would open the right
    /// file at the wrong place and key a review comment to another conversation.
    /// </summary>
    private readonly record struct ProblemRowKey(
        string Path,
        string Workspace,
        PositionEncoding Encoding,
        int Line,
        int Column,
        DiagnosticSeverity Severity,
        string Message,
        string? Code,
        string? Source)
    {
        public ProblemRowKey(WorkspaceDiagnostic diagnostic)
            : this(
                diagnostic.Path,
                diagnostic.Workspace,
                diagnostic.Encoding,
                diagnostic.Line,
                diagnostic.Column,
                diagnostic.Severity,
                diagnostic.Message,
                diagnostic.Code,
                diagnostic.Source)
        {
        }
    }

    private readonly record struct SeverityFilter(bool Errors, bool Warnings, bool Information, bool Hints)
    {
        public bool Accepts(DiagnosticSeverity severity) => severity switch
        {
            DiagnosticSeverity.Error => Errors,
            DiagnosticSeverity.Warning => Warnings,
            DiagnosticSeverity.Information => Information,
            DiagnosticSeverity.Hint => Hints,
            _ => true,
        };
    }
}

/// <summary>One row of the problems list: where it is, how bad it is, and what it says — all resolved once per refresh.</summary>
internal sealed class ProblemRowViewModel(WorkspaceDiagnostic diagnostic)
{
    public WorkspaceDiagnostic Diagnostic { get; } = diagnostic;

    /// <summary>The file name only; the directory is a tooltip, the list is not wide enough for both.</summary>
    public string FileName => System.IO.Path.GetFileName(Diagnostic.Path);

    public string Location => InspectorStrings.EditorLspReferenceLocation(Diagnostic.Line, Diagnostic.Column);

    /// <summary>"L12:5 — src/Forge.App/Foo.cs", the row's tooltip and accessible name.</summary>
    public string Description =>
        Loc.Instance.Format(InspectorStrings.ProblemsRowDescriptionPattern, Location, Diagnostic.Path);

    public string Message => Diagnostic.Message;

    /// <summary>The rule that produced it (<c>CS0168</c>, <c>no-unused-vars</c>), or empty when the server names none.</summary>
    public string Code => Diagnostic.Code is { Length: > 0 } code
        ? InspectorStrings.ProblemsRowCode(Diagnostic.Source, code)
        : string.Empty;

    public string SeverityText => Loc.Instance[Diagnostic.Severity switch
    {
        DiagnosticSeverity.Error => InspectorStrings.EditorLspSeverityError,
        DiagnosticSeverity.Warning => InspectorStrings.EditorLspSeverityWarning,
        DiagnosticSeverity.Information => InspectorStrings.EditorLspSeverityInformation,
        _ => InspectorStrings.EditorLspSeverityHint,
    }];

    public bool IsError => Diagnostic.Severity == DiagnosticSeverity.Error;

    public bool IsWarning => Diagnostic.Severity == DiagnosticSeverity.Warning;

    public bool IsInformation => Diagnostic.Severity == DiagnosticSeverity.Information;

    public bool IsHint => Diagnostic.Severity == DiagnosticSeverity.Hint;
}
