using System.Collections.ObjectModel;
using Forge.App.Localization;
using Forge.App.Preferences;
using Forge.App.Services;
using Forge.IO;

namespace Forge.App.ViewModels;

/// <summary>
/// The state of the global palette: which source is showing, the query, the rows of that source, the selected row and the
/// preview beside it. Every method here runs on the UI thread; all I/O lives behind <see cref="IPaletteHost"/> and comes
/// back through <c>ConfigureAwait(true)</c>, so the only work a keystroke does on the UI thread is one pass over the
/// current snapshot (a few thousand string comparisons for the file list, tens for the others).
/// <para>The list is rebuilt rather than filtered in place: a row is immutable, so a rebuild is the cheapest thing that
/// cannot leave a stale row selected, and the collection is bounded by the configured limit (50 by default).</para>
/// </summary>
internal sealed partial class PaletteViewModel : NotifyingObject, IDisposable
{
    private readonly IPaletteHost host;
    private readonly AppPreferences preferences;
    private readonly PalettePreview preview = new();
    private readonly List<PaletteSourceOption> sources = [];
    private IReadOnlyList<PaletteItem> rows = [];
    private ReadOnlyObservableCollection<PaletteItem> itemsView;

    private IReadOnlyList<string> files = [];
    private IReadOnlyList<PaletteSessionSnapshot> sessions = [];
    private PaletteSymbolSnapshot symbols = PaletteSymbolSnapshot.Empty;
    private GrepResult contentResult = GrepResult.Empty;
    private string contentQuery = string.Empty;
    private string contentProblem = string.Empty;

    private bool isOpen;
    private PaletteSource source = PaletteSource.Files;
    private string query = string.Empty;
    private string effectiveQuery = string.Empty;
    private PaletteSourceOption? selectedSource;
    private PaletteItem? selectedItem;
    private bool isPreviewLoading;
    private bool isSearching;
    private bool isLoadingSnapshot;
    private bool isTruncated;
    private string statusProblem = string.Empty;

    private CancellationTokenSource? contentCts;
    private CancellationTokenSource? previewCts;
    private CancellationTokenSource? refreshCts;
    private int refreshGeneration;
    private bool disposed;

    public PaletteViewModel(IPaletteHost host, AppPreferences preferences)
    {
        this.host = host ?? throw new ArgumentNullException(nameof(host));
        this.preferences = preferences ?? throw new ArgumentNullException(nameof(preferences));
        itemsView = new ReadOnlyObservableCollection<PaletteItem>(new ObservableCollection<PaletteItem>());

        CloseCommand = new RelayCommand(Close);
        sources.Add(new PaletteSourceOption(PaletteSource.Files, PaletteStrings.SourceFiles, PaletteCommandCatalog.FilesGesture));
        sources.Add(new PaletteSourceOption(PaletteSource.Content, PaletteStrings.SourceContent, PaletteCommandCatalog.ContentGesture));
        sources.Add(new PaletteSourceOption(PaletteSource.Sessions, PaletteStrings.SourceSessions, string.Empty));
        sources.Add(new PaletteSourceOption(PaletteSource.Commands, PaletteStrings.SourceCommands, PaletteCommandCatalog.CommandsGesture));
        sources.Add(new PaletteSourceOption(PaletteSource.Symbols, PaletteStrings.SourceSymbols, string.Empty));
        selectedSource = sources[0];

        Loc.Instance.LanguageChanged += OnLanguageChanged;
    }

    /// <summary>Closes the palette; bound to the close affordance so the view needs no code for it.</summary>
    public RelayCommand CloseCommand { get; }

    public bool IsOpen
    {
        get => isOpen;
        private set => Set(ref isOpen, value);
    }

    /// <summary>The source the list shows; also reachable by typing <c>&gt;</c>, <c>#</c>, <c>:</c> or <c>@</c> first.</summary>
    public PaletteSource Source => source;

    public IReadOnlyList<PaletteSourceOption> Sources => sources;

    /// <summary>The source strip's selection; setting it switches the source and drops a typed prefix.</summary>
    public PaletteSourceOption? SelectedSource
    {
        get => selectedSource;
        set
        {
            if (value is null || !Set(ref selectedSource, value))
            {
                return;
            }

            var (switched, text) = PaletteQuery.Parse(query);
            if (switched is not null)
            {
                // The typed prefix named a source; the strip now names one, so the prefix must not fight it.
                query = text;
                effectiveQuery = text;
                OnPropertyChanged(nameof(Query));
            }

            ShowSource(value.Source);
        }
    }

    /// <summary>The typed line, prefix included; the text box writes it and every change rebuilds the list.</summary>
    public string Query
    {
        get => query;
        set
        {
            value ??= string.Empty;
            if (!Set(ref query, value))
            {
                return;
            }

            OnQueryChanged();
        }
    }

    /// <summary>
    /// The rows of the current query. The collection is replaced rather than refilled, so the list resets once per
    /// keystroke instead of once per row, and a row that was selected under the old query can never survive into the new
    /// one.
    /// </summary>
    public ReadOnlyObservableCollection<PaletteItem> Items => itemsView;

    public PaletteItem? SelectedItem
    {
        get => selectedItem;
        set
        {
            if (!Set(ref selectedItem, value))
            {
                return;
            }

            OnPropertyChanged(nameof(HasSelectedItem));
            LoadPreview();
        }
    }

    public bool HasSelectedItem => selectedItem is not null;

    public bool HasItems => rows.Count > 0;

    public bool IsEmpty => rows.Count == 0;

    public string Placeholder => Loc.Instance[source switch
    {
        PaletteSource.Content => PaletteStrings.PlaceholderContent,
        PaletteSource.Sessions => PaletteStrings.PlaceholderSessions,
        PaletteSource.Commands => PaletteStrings.PlaceholderCommands,
        PaletteSource.Symbols => PaletteStrings.PlaceholderSymbols,
        _ => PaletteStrings.PlaceholderFiles,
    }];

    /// <summary>
    /// What an empty list says when it is not explaining a failure (a notice row does that). While the first snapshot of
    /// a source is still being read it says so, instead of claiming that a workspace it has not listed yet has no match.
    /// </summary>
    public string EmptyText => isLoadingSnapshot && rows.Count == 0
        ? Loc.Instance[PaletteStrings.StatusLoading]
        : Loc.Instance[source switch
        {
            PaletteSource.Content => effectiveQuery.Length == 0 ? PaletteStrings.EmptyContentQuery : PaletteStrings.EmptyContent,
            PaletteSource.Sessions => PaletteStrings.EmptySessions,
            PaletteSource.Commands => PaletteStrings.EmptyCommands,
            PaletteSource.Symbols => PaletteStrings.EmptySymbols,
            _ => PaletteStrings.EmptyFiles,
        }];

    /// <summary>The line under the list: a search in progress, a failure, the truncation note, or the workspace.</summary>
    public string StatusText
    {
        get
        {
            if (isSearching)
            {
                return Loc.Instance[PaletteStrings.StatusSearching];
            }

            if (statusProblem.Length > 0)
            {
                return statusProblem;
            }

            if (isTruncated)
            {
                return Loc.Instance.Format(
                    PaletteStrings.StatusTruncated,
                    TextFormat.Number(PaletteQuery.ClampLimit(preferences.PaletteResultLimit)));
            }

            return source == PaletteSource.Symbols && symbols.Document.Length > 0
                ? Loc.Instance.Format(PaletteStrings.StatusSymbolDocument, symbols.Document)
                : host.WorkspaceLabel;
        }
    }

    public bool IsSearching
    {
        get => isSearching;
        private set
        {
            if (Set(ref isSearching, value))
            {
                OnPropertyChanged(nameof(StatusText));
            }
        }
    }

    public string HeaderText => selectedSource?.Label ?? string.Empty;

    private string previewTitle = string.Empty;
    private string previewText = string.Empty;

    public string PreviewTitle
    {
        get => previewTitle;
        private set => Set(ref previewTitle, value);
    }

    public string PreviewText
    {
        get => previewText;
        private set
        {
            if (Set(ref previewText, value))
            {
                OnPropertyChanged(nameof(HasPreview));
            }
        }
    }

    public bool IsPreviewLoading
    {
        get => isPreviewLoading;
        private set => Set(ref isPreviewLoading, value);
    }

    public bool HasPreview => PreviewText.Length > 0;

    public bool IsDisposed => disposed;

    /// <summary>Opens the palette on <paramref name="target"/>, refreshing that source's snapshot in the background.</summary>
    public void Open(PaletteSource target)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        IsOpen = true;
        query = string.Empty;
        effectiveQuery = string.Empty;
        OnPropertyChanged(nameof(Query));
        contentResult = GrepResult.Empty;
        contentQuery = string.Empty;
        contentProblem = string.Empty;
        statusProblem = string.Empty;
        isTruncated = false;
        ShowSource(target);
    }

    /// <summary>Closes the palette and cancels everything it started; the preview's decoded file is dropped.</summary>
    public void Close()
    {
        if (disposed)
        {
            return;
        }

        Cancel(ref contentCts);
        Cancel(ref previewCts);
        Cancel(ref refreshCts);
        refreshGeneration++;
        IsOpen = false;
        IsSearching = false;
        IsPreviewLoading = false;
        isLoadingSnapshot = false;
        preview.Clear();
        SetSelection(null);
        Replace([]);
    }

    /// <summary>Moves the selection by <paramref name="delta"/> rows, stopping at both ends.</summary>
    public void MoveSelection(int delta)
    {
        if (delta == 0 || rows.Count == 0)
        {
            return;
        }

        var current = -1;
        for (var index = 0; index < rows.Count; index++)
        {
            if (ReferenceEquals(rows[index], selectedItem))
            {
                current = index;
                break;
            }
        }

        var next = Math.Clamp((current < 0 ? 0 : current) + delta, 0, rows.Count - 1);
        SelectedItem = rows[next];
    }

    /// <summary>
    /// Runs the selected row: the palette closes first, so a row that opens the palette on another source (a command does
    /// exactly that) is not closed again by the same call.
    /// </summary>
    public void Accept()
    {
        if (selectedItem is not { IsNavigable: true } item)
        {
            return;
        }

        Close();
        _ = ActivateAsync(item);
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        Loc.Instance.LanguageChanged -= OnLanguageChanged;
        Cancel(ref contentCts);
        Cancel(ref previewCts);
        Cancel(ref refreshCts);
        preview.Clear();
    }

    private async Task ActivateAsync(PaletteItem item)
    {
        try
        {
            await host.ActivateAsync(item, CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or ArgumentException)
        {
            // The host reports its own navigation failures on the status bar; nothing here can add to that.
        }
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        // The strip's labels resolve on read; only the bindings have to be told that they changed.
        OnPropertyChanged(nameof(Sources));
        OnPropertyChanged(nameof(HeaderText));
        OnPropertyChanged(nameof(Placeholder));
        OnPropertyChanged(nameof(EmptyText));
        OnPropertyChanged(nameof(StatusText));
        Rebuild();
    }

    private void OnQueryChanged()
    {
        var (switched, text) = PaletteQuery.Parse(query);
        effectiveQuery = text;
        OnPropertyChanged(nameof(EmptyText));
        if (switched is { } next && next != source)
        {
            ShowSource(next);
            return;
        }

        OnPropertyChanged(nameof(Placeholder));
        Rebuild();
        if (source == PaletteSource.Content)
        {
            ScheduleContentSearch();
        }
    }

    /// <summary>Switches to <paramref name="target"/>: new snapshot, empty list, a refresh of that source.</summary>
    private void ShowSource(PaletteSource target)
    {
        source = target;
        foreach (var option in sources)
        {
            if (option.Source == target)
            {
                selectedSource = option;
            }
        }

        contentResult = GrepResult.Empty;
        contentProblem = string.Empty;
        statusProblem = string.Empty;
        isTruncated = false;
        Cancel(ref previewCts);
        OnPropertyChanged(nameof(Source));
        OnPropertyChanged(nameof(SelectedSource));
        OnPropertyChanged(nameof(HeaderText));
        OnPropertyChanged(nameof(Placeholder));
        OnPropertyChanged(nameof(EmptyText));
        OnPropertyChanged(nameof(StatusText));
        Rebuild();
        if (target != PaletteSource.Content)
        {
            Cancel(ref contentCts);
            IsSearching = false;
        }

        Refresh(target);
    }

    private static void Cancel(ref CancellationTokenSource? source)
    {
        var cts = source;
        source = null;
        if (cts is null)
        {
            return;
        }

        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already finished and disposed by its own task; nothing to cancel.
        }
    }
}
