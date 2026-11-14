using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows.Input;
using ICommand = System.Windows.Input.ICommand;
using Avalonia.Threading;
using Forge.Abstractions;
using Forge.App.Composition;
using Forge.App.Localization;
using Forge.App.Logging;
using Forge.App.Preferences;
using Forge.App.Services;
using Forge.Mcp;
using ZLogger;

namespace Forge.App.ViewModels.Settings;

/// <summary>
/// The <c>mcp</c> settings section: the two <c>mcp.json</c> files (global and project), the servers the manager runs from
/// them, and one editor for entries plus an importer for Claude Desktop's config. Writes go through
/// <see cref="McpServices"/> so every save, remove, import or enable-toggle re-applies the file to the running manager;
/// reads go through <see cref="BackgroundRead"/> so the page never blocks on file IO.
/// <para>Lifecycle: the manager's <see cref="McpServerManager.StateChanged"/> only marks the page dirty and posts one
/// coalesced rebuild to the dispatcher; <see cref="Reload"/> re-reads the files and rebuilds the rows. Every async action
/// carries its own token; <see cref="Dispose"/> cancels them and drops the Loc and manager subscriptions.</para>
/// </summary>
internal sealed class McpSettingsSectionViewModel : NotifyingObject, IDisposable, IReloadableSettingsSection
{
    /// <summary>Most tool rows one server's expanded area shows; the rest are cut off with a notice.</summary>
    public const int MaxVisibleTools = 500;

    /// <summary>Most import candidates the import panel lists.</summary>
    public const int MaxImportCandidates = 500;

    /// <summary>
    /// The configs other applications keep their MCP servers in. All of them are scanned; when two list the same name the first
    /// source wins, so a name is never imported twice.
    /// </summary>
    private static readonly McpImportSource[] ImportSources = McpImportSource.Known;

    private readonly AppComposition composition;

    /// <summary>The thread the section was built on (the UI thread); a language notification from another one is not ours.</summary>
    private readonly int uiThread = Environment.CurrentManagedThreadId;

    private readonly Dispatcher dispatcher = Dispatcher.UIThread;

    private CancellationTokenSource? refreshCts;
    private CancellationTokenSource? actionCts;
    private Task? pendingRefresh;
    private bool refreshOk;
    private int refreshPending;
    private int stateChangePosted;
    private bool disposed;

    private string statusText = string.Empty;
    private bool statusIsError;
    private McpServerEditorViewModel? editor;
    private bool importOpen;
    private IReadOnlyList<McpImportCandidateViewModel> importCandidates = [];
    private string? importProblemText;
    private bool importToProject;
    private string importSummary = string.Empty;
    private IReadOnlyList<McpPresetOptionViewModel> presets;

    public McpSettingsSectionViewModel(AppComposition composition)
    {
        ArgumentNullException.ThrowIfNull(composition);
        this.composition = composition;
        presets = BuildPresets();

        AddCommand = new RelayCommand(Add);
        EditCommand = new ItemCommand<McpServerRowViewModel>(Edit);
        CancelEditCommand = new RelayCommand(CloseEditor);
        SaveCommand = new AsyncRelayCommand(SaveAsync, onError: ReportUnexpected);
        DeleteCommand = new ItemCommand<McpServerRowViewModel>(row => row?.Delete());
        ImportCommand = new AsyncRelayCommand(ImportAsync, onError: ReportUnexpected);
        ApplyImportCommand = new AsyncRelayCommand(ApplyImportAsync, onError: ReportUnexpected);
        CancelImportCommand = new RelayCommand(CloseImport);
        ApplyPresetCommand = new ItemCommand<McpPresetOptionViewModel>(ApplyPreset);
        ReloadCommand = new AsyncRelayCommand(ReloadAndReportAsync, onError: ReportUnexpected);

        if (composition.Mcp is { } services)
        {
            services.Manager.StateChanged += OnServerStateChanged;
            services.Manager.ToolsChanged += OnToolsChanged;
        }

        Loc.Instance.LanguageChanged += OnLanguageChanged;
    }

    // ------------------------------------------------------------------ bindable surface

    /// <summary>One row per configured server, in file order (a project entry shadows its global twin, like the manager).</summary>
    public ObservableCollection<McpServerRowViewModel> Servers { get; } = [];

    public bool HasServers => Servers.Count > 0;

    /// <summary>Resolved in the current language at bind time; refreshed on a language switch.</summary>
    public string Hint => McpSettingsStrings.PageHint;

    public string EmptyText => McpSettingsStrings.PageEmpty;

    /// <summary>The last action's result; empty means nothing to report.</summary>
    public string StatusText => statusText;

    public bool HasStatus => statusText.Length > 0;

    public bool StatusIsError => statusIsError;

    /// <summary>File-level diagnostics and per-entry validation problems, one line each, in the current language.</summary>
    public ObservableCollection<string> Diagnostics { get; } = [];

    public bool HasDiagnostics => Diagnostics.Count > 0;

    /// <summary>The open entry editor, or null.</summary>
    public McpServerEditorViewModel? Editor => editor;

    public bool IsEditing => editor is not null;

    // ------------------------------------------------------------------ import

    public bool IsImportOpen => importOpen;

    public IReadOnlyList<McpImportCandidateViewModel> ImportCandidates => importCandidates;

    /// <summary>The source problem or the selection count; empty when the source exists but holds no entries.</summary>
    public string ImportSummary => importSummary;

    /// <summary>True when the source file has entries and none of the candidates could be built (source missing or broken).</summary>
    public bool ImportEmpty => importCandidates.Count == 0 && importProblemText is null;

    public bool ImportToGlobal
    {
        get => !importToProject;
        set
        {
            if (value)
            {
                SetImportToProject(false);
            }
        }
    }

    public bool ImportToProject
    {
        get => importToProject;
        set => SetImportToProject(value);
    }

    public string ImportGlobalTargetText =>
        composition.Mcp is { } services ? McpSettingsStrings.TargetGlobal(services.Store.GlobalPath) : string.Empty;

    public string ImportProjectTargetText =>
        composition.Mcp is { } services && services.Store.ProjectPath is { } path
            ? McpSettingsStrings.TargetProject(path)
            : string.Empty;

    public bool HasImportProjectTarget => ImportProjectTargetText.Length > 0;

    // ------------------------------------------------------------------ presets

    /// <summary>The built-in preset catalogue; replaced (not mutated) on a language switch, like the git picker's options.</summary>
    public IReadOnlyList<McpPresetOptionViewModel> Presets => presets;

    // ------------------------------------------------------------------ preferences (write through to the live AppPreferences, clamped by its setters)

    public int McpRequestTimeoutSeconds
    {
        get => composition.Preferences.McpRequestTimeoutSeconds;
        set
        {
            composition.Preferences.McpRequestTimeoutSeconds = value;
            OnPropertyChanged();
        }
    }

    public int McpReconnectInitialDelaySeconds
    {
        get => composition.Preferences.McpReconnectInitialDelaySeconds;
        set
        {
            composition.Preferences.McpReconnectInitialDelaySeconds = value;
            OnPropertyChanged();
        }
    }

    public int McpToolSearchLimit
    {
        get => composition.Preferences.McpToolSearchLimit;
        set
        {
            composition.Preferences.McpToolSearchLimit = value;
            OnPropertyChanged();
        }
    }

    public int McpStderrTailLines
    {
        get => composition.Preferences.McpStderrTailLines;
        set
        {
            composition.Preferences.McpStderrTailLines = value;
            OnPropertyChanged();
        }
    }

    // ------------------------------------------------------------------ commands

    public ICommand AddCommand { get; }

    public ICommand EditCommand { get; }

    public ICommand CancelEditCommand { get; }

    public ICommand SaveCommand { get; }

    public ICommand DeleteCommand { get; }

    public ICommand ImportCommand { get; }

    public ICommand ApplyImportCommand { get; }

    public ICommand CancelImportCommand { get; }

    public ICommand ApplyPresetCommand { get; }

    public ICommand ReloadCommand { get; }

    // ------------------------------------------------------------------ reload / lifecycle

    /// <summary>Re-reads both files and rebuilds the rows; called when the section is first shown and after a language switch.</summary>
    public void Reload()
    {
        OnPropertyChanged(nameof(Hint));
        OnPropertyChanged(nameof(EmptyText));
        QueueRefresh();
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        CancelToken(ref refreshCts);
        CancelToken(ref actionCts);
        Loc.Instance.LanguageChanged -= OnLanguageChanged;
        if (composition.Mcp is { } services)
        {
            services.Manager.StateChanged -= OnServerStateChanged;
            services.Manager.ToolsChanged -= OnToolsChanged;
        }

        DetachImportCandidates();
        foreach (var row in Servers)
        {
            row.Dispose();
        }

        editor = null;
    }

    // ------------------------------------------------------------------ internal helpers (rows and editors call these)

    internal McpServices? Services => composition.Mcp;

    internal int StderrTailLines => composition.Preferences.McpStderrTailLines;

    /// <summary>Guards against reporting off the UI thread and against work that lands after the window closed.</summary>
    internal bool CanApply => !disposed && Environment.CurrentManagedThreadId == uiThread;

    /// <summary>The exceptions a user-triggered MCP action may reasonably fail with; anything else is a defect.</summary>
    internal static bool Expected(Exception exception) =>
        exception is McpException or IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException;

    internal void ShowStatus(string text, bool isError)
    {
        if (!CanApply)
        {
            return;
        }

        statusText = text ?? string.Empty;
        statusIsError = isError;
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(HasStatus));
        OnPropertyChanged(nameof(StatusIsError));
    }

    internal void ShowError(string message) => ShowStatus(McpSettingsStrings.StatusError(message), isError: true);

    internal void ClearStatus()
    {
        if (!CanApply)
        {
            return;
        }

        statusText = string.Empty;
        statusIsError = false;
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(HasStatus));
        OnPropertyChanged(nameof(StatusIsError));
    }

    internal void Edit(McpServerRowViewModel? row)
    {
        if (row is null)
        {
            return;
        }

        OpenEditor(row.Entry, isNew: false);
    }

    /// <summary>A token for one row action: starting a new one cancels the previous (one MCP action at a time).</summary>
    internal CancellationToken ActionToken() => NewToken(ref actionCts);

    private CancellationToken RefreshToken() => NewToken(ref refreshCts);

    private static CancellationToken NewToken(ref CancellationTokenSource? slot)
    {
        var previous = Interlocked.Exchange(ref slot, new CancellationTokenSource());
        if (previous is not null)
        {
            try
            {
                previous.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }

            previous.Dispose();
        }

        return slot!.Token;
    }

    private static void CancelToken(ref CancellationTokenSource? slot)
    {
        var current = Interlocked.Exchange(ref slot, null);
        if (current is null)
        {
            return;
        }

        try
        {
            current.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        current.Dispose();
    }

    // ------------------------------------------------------------------ refresh

    /// <summary>Coalesces refreshes: every entry point asks here and exactly one read runs at a time.</summary>
    private void QueueRefresh()
    {
        if (disposed)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref refreshPending, 1, 0) != 0)
        {
            return;
        }

        pendingRefresh = RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        try
        {
            refreshOk = await RefreshCoreAsync().ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            refreshOk = false;
        }
        catch (Exception exception)
        {
            refreshOk = false;
            ReportUnexpected(exception);
        }
        finally
        {
            Volatile.Write(ref refreshPending, 0);
            pendingRefresh = null;
        }
    }

    private async Task<bool> RefreshCoreAsync()
    {
        if (disposed || !CanApply)
        {
            return false;
        }

        var services = composition.Mcp;
        if (services is null)
        {
            var problem = composition.McpProblem.Resolve();
            ShowStatus(problem.Length > 0 ? problem : McpSettingsStrings.StatusUnavailable, isError: true);
            return false;
        }

        var ct = RefreshToken();
        McpConfigSnapshot snapshot;
        try
        {
            snapshot = await services.ReadConfigAsync(ct).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception exception) when (Expected(exception))
        {
            if (CanApply)
            {
                ShowError(exception.Message);
            }

            return false;
        }

        if (!CanApply)
        {
            return false;
        }

        Rebuild(snapshot, services.Manager.States);
        return true;
    }

    /// <summary>Rebuilds rows and diagnostics from the snapshot, keeping the rows that still exist (expansion survives).</summary>
    private void Rebuild(McpConfigSnapshot snapshot, IReadOnlyList<McpServerState> states)
    {
        var byName = new Dictionary<string, McpServerState>(states.Count, StringComparer.Ordinal);
        foreach (var state in states)
        {
            byName[state.Name] = state;
        }

        // File order, project entries shadowing their global twins — the same merge the manager applies.
        var entries = new Dictionary<string, McpServerEntry>(StringComparer.Ordinal);
        var sources = new Dictionary<string, (string Path, bool IsProject)>(StringComparer.Ordinal);
        foreach (var file in snapshot.Files)
        {
            foreach (var entry in file.Entries)
            {
                entries[entry.Name] = entry;
                sources[entry.Name] = (file.Path, file.IsProject);
            }
        }

        var diagnostics = new List<string>(snapshot.Diagnostics.Count);
        foreach (var diagnostic in snapshot.Diagnostics)
        {
            diagnostics.Add(McpSettingsStrings.DiagnosticFile(diagnostic));
        }

        var existing = new Dictionary<string, McpServerRowViewModel>(Servers.Count, StringComparer.Ordinal);
        foreach (var row in Servers)
        {
            existing[row.Name] = row;
        }

        var rebuilt = new List<McpServerRowViewModel>(entries.Count);
        foreach (var (name, entry) in entries)
        {
            if (!existing.Remove(name, out var row))
            {
                var (path, isProject) = sources[name];
                row = new McpServerRowViewModel(this, entry, path, isProject);
            }

            row.Apply(entry, byName.GetValueOrDefault(name));
            rebuilt.Add(row);
            foreach (var issue in entry.Validate())
            {
                diagnostics.Add(McpSettingsStrings.DiagnosticEntry(name, issue));
            }
        }

        foreach (var stale in existing.Values)
        {
            stale.Dispose();
        }

        Servers.Clear();
        foreach (var row in rebuilt)
        {
            Servers.Add(row);
        }

        OnPropertyChanged(nameof(HasServers));

        Diagnostics.Clear();
        foreach (var line in diagnostics)
        {
            Diagnostics.Add(line);
        }

        OnPropertyChanged(nameof(HasDiagnostics));
    }

    // ------------------------------------------------------------------ editor

    private void Add()
    {
        if (!CanApply || composition.Mcp is null)
        {
            return;
        }

        OpenEditor(new McpServerEntry { Name = UniqueName("server") }, isNew: true);
    }

    private void OpenEditor(McpServerEntry entry, bool isNew)
    {
        if (composition.Mcp is not { } services)
        {
            ShowError(McpSettingsStrings.StatusUnavailable);
            return;
        }

        editor = new McpServerEditorViewModel(entry, services.Store.GlobalPath, services.Store.ProjectPath, isNew);
        OnPropertyChanged(nameof(Editor));
        OnPropertyChanged(nameof(IsEditing));
        ClearStatus();
    }

    private void CloseEditor()
    {
        if (editor is null)
        {
            return;
        }

        editor = null;
        OnPropertyChanged(nameof(Editor));
        OnPropertyChanged(nameof(IsEditing));
    }

    private async Task SaveAsync(CancellationToken _)
    {
        if (disposed || !CanApply || editor is not { } current)
        {
            return;
        }

        var entry = current.BuildEntry();
        current.RefreshIssues();
        if (entry.Validate().Count > 0 || current.HasIssues)
        {
            ShowError(McpSettingsStrings.EditorInvalid);
            return;
        }

        if (composition.Mcp is not { } services)
        {
            ShowError(McpSettingsStrings.StatusUnavailable);
            return;
        }

        var path = current.TargetPath;
        var ct = ActionToken();
        try
        {
            await BackgroundRead.RunAsync(token => services.SaveAsync(path, entry, token), ct).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception exception) when (Expected(exception))
        {
            if (CanApply)
            {
                ShowError(exception.Message);
            }

            return;
        }

        if (!CanApply)
        {
            return;
        }

        CloseEditor();
        ShowStatus(McpSettingsStrings.StatusSaved, isError: false);
        QueueRefresh();
    }

    private string UniqueName(string baseName)
    {
        var taken = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in Servers)
        {
            taken.Add(row.Name);
        }

        if (composition.Mcp is { } services)
        {
            foreach (var name in services.Manager.ServerNames)
            {
                taken.Add(name);
            }
        }

        if (!taken.Contains(baseName))
        {
            return baseName;
        }

        for (var index = 2; ; index++)
        {
            var candidate = baseName + "-" + index.ToString(CultureInfo.InvariantCulture);
            if (!taken.Contains(candidate))
            {
                return candidate;
            }
        }
    }

    // ------------------------------------------------------------------ presets

    private IReadOnlyList<McpPresetOptionViewModel> BuildPresets()
    {
        var catalog = McpPresets.All;
        var options = new McpPresetOptionViewModel[catalog.Count];
        for (var index = 0; index < catalog.Count; index++)
        {
            var optionIndex = index;
            options[index] = new McpPresetOptionViewModel(
                catalog[index],
                new RelayCommand(() => ApplyPreset(options[optionIndex])));
        }

        return options;
    }

    private void ApplyPreset(McpPresetOptionViewModel? option)
    {
        if (option is null || !CanApply || composition.Mcp is null)
        {
            return;
        }

        // A preset that needs a directory starts from the workspace in front; the editor is the place to change it.
        var entry = McpPresets.ToEntry(
            option.Preset,
            UniqueName(option.Preset.Id),
            option.Preset.NeedsPath ? composition.WorkspaceDirectory : null);
        OpenEditor(entry, isNew: true);
    }

    // ------------------------------------------------------------------ import

    /// <summary>What one scan of every known source found: the entries, the sources that were not there, the first problem.</summary>
    private sealed record ImportScan(
        IReadOnlyList<McpServerEntry> Entries,
        IReadOnlyList<string> Missing,
        string? Problem);

    private async Task ImportAsync(CancellationToken _)
    {
        if (disposed || !CanApply)
        {
            return;
        }

        if (composition.Mcp is not { } services)
        {
            ShowError(McpSettingsStrings.StatusUnavailable);
            return;
        }

        var ct = ActionToken();
        ImportScan scan;
        try
        {
            scan = await BackgroundRead.RunAsync(_ => Task.FromResult(ScanImportSources()), ct).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception exception) when (Expected(exception))
        {
            if (CanApply)
            {
                ShowError(exception.Message);
            }

            return;
        }

        if (!CanApply)
        {
            return;
        }

        DetachImportCandidates();
        var existing = new HashSet<string>(services.Manager.ServerNames, StringComparer.Ordinal);
        var candidates = new List<McpImportCandidateViewModel>(Math.Min(scan.Entries.Count, MaxImportCandidates));
        foreach (var entry in scan.Entries)
        {
            if (candidates.Count >= MaxImportCandidates)
            {
                break;
            }

            candidates.Add(new McpImportCandidateViewModel(entry, existing.Contains(entry.Name), entry.SourcePath));
        }

        importCandidates = candidates;
        AttachImportCandidates();
        importProblemText = scan.Missing.Count == ImportSources.Length
            ? McpSettingsStrings.ImportSourceMissing(
                string.Join(Environment.NewLine, ImportSources.Select(static source => source.FilePath)))
            : scan.Problem is { Length: > 0 } problem
                ? McpSettingsStrings.ImportSourceError(problem)
                : null;
        importToProject = false;
        importOpen = true;
        OnPropertyChanged(nameof(IsImportOpen));
        OnPropertyChanged(nameof(ImportCandidates));
        OnPropertyChanged(nameof(ImportEmpty));
        OnPropertyChanged(nameof(ImportToGlobal));
        OnPropertyChanged(nameof(ImportToProject));
        OnPropertyChanged(nameof(HasImportProjectTarget));
        UpdateImportSummary();
    }

    /// <summary>Reads every known source; a missing file is not an error, and a name seen twice keeps the first source's entry.</summary>
    private static ImportScan ScanImportSources()
    {
        var entries = new List<McpServerEntry>(8);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var missing = new List<string>(ImportSources.Length);
        string? problem = null;
        foreach (var source in ImportSources)
        {
            string text;
            try
            {
                if (!File.Exists(source.FilePath))
                {
                    missing.Add(source.FilePath);
                    continue;
                }

                text = File.ReadAllText(source.FilePath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
            {
                problem ??= exception.Message;
                continue;
            }

            var read = McpConfigFile.Read(text, source.FilePath);
            problem ??= read.Diagnostics.Count > 0 ? read.Diagnostics[0].Message : null;
            foreach (var entry in read.Entries)
            {
                if (seen.Add(entry.Name))
                {
                    entries.Add(entry);
                }
            }
        }

        return new ImportScan(entries, missing, problem);
    }

    private async Task ApplyImportAsync(CancellationToken _)
    {
        if (disposed || !CanApply || composition.Mcp is not { } services)
        {
            return;
        }

        var selected = new List<McpServerEntry>(importCandidates.Count);
        foreach (var candidate in importCandidates)
        {
            if (candidate.IsSelected)
            {
                selected.Add(candidate.Entry);
            }
        }

        if (selected.Count == 0)
        {
            ShowError(McpSettingsStrings.ImportNoneSelected);
            return;
        }

        var path = importToProject && services.Store.ProjectPath is { } project
            ? project
            : services.Store.GlobalPath;
        var ct = ActionToken();
        try
        {
            await BackgroundRead.RunAsync(token => services.ImportAsync(path, selected, token), ct).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception exception) when (Expected(exception))
        {
            if (CanApply)
            {
                ShowError(exception.Message);
            }

            return;
        }

        if (!CanApply)
        {
            return;
        }

        CloseImport();
        ShowStatus(McpSettingsStrings.StatusImported(selected.Count), isError: false);
        QueueRefresh();
    }

    private void CloseImport()
    {
        if (!importOpen)
        {
            return;
        }

        importOpen = false;
        OnPropertyChanged(nameof(IsImportOpen));
    }

    private void SetImportToProject(bool value)
    {
        if (Set(ref importToProject, value))
        {
            OnPropertyChanged(nameof(ImportToGlobal));
        }
    }

    private void AttachImportCandidates()
    {
        foreach (var candidate in importCandidates)
        {
            candidate.PropertyChanged += OnImportCandidatePropertyChanged;
        }
    }

    private void DetachImportCandidates()
    {
        foreach (var candidate in importCandidates)
        {
            candidate.PropertyChanged -= OnImportCandidatePropertyChanged;
        }
    }

    private void OnImportCandidatePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.Equals(e.PropertyName, nameof(McpImportCandidateViewModel.IsSelected), StringComparison.Ordinal))
        {
            UpdateImportSummary();
        }
    }

    private void UpdateImportSummary()
    {
        if (!CanApply)
        {
            return;
        }

        if (importProblemText is { Length: > 0 } problem)
        {
            SetImportSummary(problem);
            return;
        }

        if (importCandidates.Count == 0)
        {
            SetImportSummary(string.Empty);
            return;
        }

        var selected = 0;
        foreach (var candidate in importCandidates)
        {
            if (candidate.IsSelected)
            {
                selected++;
            }
        }

        SetImportSummary(McpSettingsStrings.ImportSummary(selected, importCandidates.Count));
    }

    private void SetImportSummary(string value)
    {
        importSummary = value ?? string.Empty;
        OnPropertyChanged(nameof(ImportSummary));
        OnPropertyChanged(nameof(ImportEmpty));
    }

    // ------------------------------------------------------------------ manager events

    /// <summary>
    /// A state change only marks the page dirty and posts one coalesced rebuild: a burst of state changes (a start followed
    /// by a failure, a reconnect) becomes a single re-read, never one rebuild per event.
    /// </summary>
    private void OnServerStateChanged(McpServerState state)
    {
        if (disposed)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref stateChangePosted, 1, 0) == 0)
        {
            dispatcher.Post(() =>
            {
                Volatile.Write(ref stateChangePosted, 0);
                if (!disposed && CanApply)
                {
                    QueueRefresh();
                }
            });
        }
    }

    private void OnToolsChanged()
    {
        if (disposed)
        {
            return;
        }

        dispatcher.Post(() =>
        {
            if (disposed || !CanApply)
            {
                return;
            }

            foreach (var row in Servers)
            {
                if (row.IsExpanded)
                {
                    row.RequestToolsRefresh();
                }
            }
        });
    }

    // ------------------------------------------------------------------ language

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        // A notification from another thread belongs to a page whose window is gone (a test host).
        if (disposed || Environment.CurrentManagedThreadId != uiThread)
        {
            return;
        }

        OnPropertyChanged(nameof(Hint));
        OnPropertyChanged(nameof(EmptyText));
        presets = BuildPresets();
        OnPropertyChanged(nameof(Presets));
        OnPropertyChanged(nameof(ImportGlobalTargetText));
        OnPropertyChanged(nameof(ImportProjectTargetText));
        Editor?.RefreshTexts();
        Reload();
    }

    // ------------------------------------------------------------------ last resort

    private async Task ReloadAndReportAsync(CancellationToken _)
    {
        if (disposed || !CanApply)
        {
            return;
        }

        QueueRefresh();
        var pending = pendingRefresh;
        if (pending is not null)
        {
            try
            {
                await pending.ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
            }
        }

        if (CanApply && refreshOk)
        {
            ShowStatus(McpSettingsStrings.StatusReloaded, isError: false);
        }
    }

    /// <summary>The framework boundary for async actions: an unexpected exception is logged and shown, never thrown into the dispatcher.</summary>
    internal void ReportUnexpected(Exception exception)
    {
        AppLogs.Instruments.ZLogWarning($"mcp settings action failed unexpectedly: {exception.Message}");
        ShowError(exception.Message);
    }
}

/// <summary>One configured server: its entry (the file's words), the manager's state for it, and every action around it.</summary>
internal sealed class McpServerRowViewModel : NotifyingObject, IDisposable
{
    private readonly McpSettingsSectionViewModel owner;
    private readonly AsyncRelayCommand start;
    private readonly AsyncRelayCommand stop;
    private readonly AsyncRelayCommand toggleRunning;
    private readonly AsyncRelayCommand restart;
    private readonly AsyncRelayCommand test;
    private readonly AsyncRelayCommand refreshTools;
    private readonly AsyncRelayCommand delete;
    private bool actionRunning;
    private bool toolsLoaded;
    private bool expanded;
    private bool enabled;
    private bool disposed;

    public McpServerRowViewModel(
        McpSettingsSectionViewModel owner,
        McpServerEntry entry,
        string sourcePath,
        bool isProject)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(entry);
        this.owner = owner;
        Entry = entry;
        SourcePath = sourcePath;
        ScopeText = isProject ? McpSettingsStrings.ScopeProject : McpSettingsStrings.ScopeGlobal;
        Name = entry.Name;

        var notBusy = () => !actionRunning;
        start = new AsyncRelayCommand(StartAsync, canExecute: notBusy, onError: owner.ReportUnexpected);
        stop = new AsyncRelayCommand(StopAsync, canExecute: notBusy, onError: owner.ReportUnexpected);
        toggleRunning = new AsyncRelayCommand(ToggleRunningAsync, canExecute: notBusy, onError: owner.ReportUnexpected);
        restart = new AsyncRelayCommand(RestartAsync, canExecute: notBusy, onError: owner.ReportUnexpected);
        test = new AsyncRelayCommand(TestAsync, canExecute: notBusy, onError: owner.ReportUnexpected);
        refreshTools = new AsyncRelayCommand(RefreshToolsAsync, canExecute: notBusy, onError: owner.ReportUnexpected);
        delete = new AsyncRelayCommand(DeleteAsync, canExecute: notBusy, onError: owner.ReportUnexpected);
        EditCommand = new RelayCommand(() => owner.Edit(this));
        ToggleExpandCommand = new RelayCommand(ToggleExpand);
    }

    // ------------------------------------------------------------------ bindable surface

    /// <summary>The entry as the file spells it; the editor starts from it so unknown fields survive an edit.</summary>
    public McpServerEntry Entry { get; private set; }

    /// <summary>The file that defines this row's entry; the toggle and the delete write there.</summary>
    public string SourcePath { get; }

    public string Name { get; private set; }

    public string TransportText { get; private set; } = string.Empty;

    public string StatusText { get; private set; } = string.Empty;

    public bool IsReady { get; private set; }

    /// <summary>The switch: flips the manager and writes <c>disabled</c> back to the entry's file.</summary>
    public bool IsEnabled
    {
        get => enabled;
        set
        {
            if (disposed || enabled == value)
            {
                return;
            }

            enabled = value;
            OnPropertyChanged();
            _ = ApplyEnabledAsync(value);
        }
    }

    public bool IsExpanded
    {
        get => expanded;
        set
        {
            if (!Set(ref expanded, value))
            {
                return;
            }

            OnPropertyChanged(nameof(ExpandText));
            if (value && !toolsLoaded)
            {
                _ = LoadToolsAsync();
            }
        }
    }

    public string ExpandText => expanded ? McpSettingsStrings.RowCollapse : McpSettingsStrings.RowExpand;

    public string RunningToggleText => IsReady ? McpSettingsStrings.RowStop : McpSettingsStrings.RowStart;

    public bool HasError { get; private set; }

    public string ErrorText { get; private set; } = string.Empty;

    public string? StderrTailText { get; private set; }

    public bool HasStderr { get; private set; }

    /// <summary>Filled when the row is first expanded (and by the refresh action); capped at <see cref="McpSettingsSectionViewModel.MaxVisibleTools"/>.</summary>
    public ObservableCollection<McpToolRowViewModel> Tools { get; } = [];

    public bool HasTools => Tools.Count > 0;

    public bool ToolsTruncated { get; private set; }

    public string ToolsTruncatedText =>
        McpSettingsStrings.ToolsTruncated(McpSettingsSectionViewModel.MaxVisibleTools);

    /// <summary>True once tools were read and none came back; before the first read nothing is claimed.</summary>
    public bool ToolsEmpty => toolsLoaded && Tools.Count == 0;

    public string TestResultText { get; private set; } = string.Empty;

    public bool HasTestResult => TestResultText.Length > 0;

    public string ScopeText { get; }

    // ------------------------------------------------------------------ commands

    public ICommand StartCommand => start;

    public ICommand StopCommand => stop;

    public ICommand ToggleRunningCommand => toggleRunning;

    public ICommand RestartCommand => restart;

    public ICommand TestCommand => test;

    public ICommand RefreshToolsCommand => refreshTools;

    public ICommand EditCommand { get; }

    public ICommand DeleteCommand => delete;

    public ICommand ToggleExpandCommand { get; }

    public void Dispose()
    {
        disposed = true;
    }

    // ------------------------------------------------------------------ state

    /// <summary>Applies the fresh entry and manager state; every localized text is recomputed so a language switch re-renders.</summary>
    public void Apply(McpServerEntry entry, McpServerState? state)
    {
        Entry = entry;
        Name = entry.Name;
        TransportText = entry.IsHttp ? McpSettingsStrings.TransportHttp : McpSettingsStrings.TransportStdio;
        var current = state ?? new McpServerState(entry.Name, McpServerStatus.Stopped, !entry.Disabled);
        IsReady = current.IsReady;
        StatusText = McpSettingsStrings.StatusOf(current);
        enabled = current.Enabled;
        RunningToggleTextValue();
        HasError = current.LastError is { Length: > 0 };
        ErrorText = current.LastError ?? string.Empty;
        StderrTailText = McpServices.Tail(current.StderrTail, owner.StderrTailLines);
        HasStderr = StderrTailText is { Length: > 0 };
        OnPropertyChanged(nameof(Entry));
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(TransportText));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(IsReady));
        OnPropertyChanged(nameof(IsEnabled));
        OnPropertyChanged(nameof(HasError));
        OnPropertyChanged(nameof(ErrorText));
        OnPropertyChanged(nameof(StderrTailText));
        OnPropertyChanged(nameof(HasStderr));
        OnPropertyChanged(nameof(ScopeText));
    }

    private void RunningToggleTextValue() => OnPropertyChanged(nameof(RunningToggleText));

    // ------------------------------------------------------------------ actions

    internal void Delete() => _ = DeleteAsync(CancellationToken.None);

    internal void RequestToolsRefresh()
    {
        if (disposed || !toolsLoaded || actionRunning)
        {
            return;
        }

        _ = RefreshToolsAsync(CancellationToken.None);
    }

    private Task StartAsync(CancellationToken _) => RunManagerActionAsync(
        static async (manager, name, token) => await manager.StartAsync(name, token).ConfigureAwait(false));

    private Task StopAsync(CancellationToken _) => RunManagerActionAsync(
        static async (manager, name, token) => await manager.StopAsync(name, token).ConfigureAwait(false));

    private Task ToggleRunningAsync(CancellationToken _) => RunManagerActionAsync(
        static async (manager, name, token) =>
        {
            // The toggle reads the live state, not the row's cached copy, so a stale row cannot start a ready server again.
            if (manager.GetState(name).IsReady)
            {
                await manager.StopAsync(name, token).ConfigureAwait(false);
            }
            else
            {
                await manager.StartAsync(name, token).ConfigureAwait(false);
            }
        });

    private Task RestartAsync(CancellationToken _) => RunManagerActionAsync(
        static async (manager, name, token) => await manager.RestartAsync(name, token).ConfigureAwait(false));

    private async Task RunManagerActionAsync(Func<McpServerManager, string, CancellationToken, Task> action)
    {
        var services = owner.Services;
        if (services is null || disposed)
        {
            return;
        }

        SetBusy(true);
        var ct = owner.ActionToken();
        try
        {
            await BackgroundRead
                .RunAsync(token => action(services.Manager, Name, token), ct)
                .ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception) when (McpSettingsSectionViewModel.Expected(exception))
        {
            if (!disposed && owner.CanApply)
            {
                owner.ShowError(exception.Message);
            }
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task ApplyEnabledAsync(bool value)
    {
        var services = owner.Services;
        if (services is null || disposed)
        {
            return;
        }

        SetBusy(true);
        var ct = owner.ActionToken();
        try
        {
            await BackgroundRead.RunAsync(
                async token =>
                {
                    // The file first: its apply reconciles the manager; the explicit call then pins the final state
                    // without a second restart.
                    await services.SetDisabledAsync(SourcePath, Name, !value, token).ConfigureAwait(false);
                    await services.Manager.SetEnabledAsync(Name, value, token).ConfigureAwait(false);
                },
                ct).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception) when (McpSettingsSectionViewModel.Expected(exception))
        {
            if (!disposed && owner.CanApply)
            {
                owner.ShowError(exception.Message);
                var manager = owner.Services?.Manager;
                if (manager is not null)
                {
                    enabled = manager.GetState(Name).Enabled;
                    OnPropertyChanged(nameof(IsEnabled));
                }
            }
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task DeleteAsync(CancellationToken _)
    {
        var services = owner.Services;
        if (services is null || disposed)
        {
            return;
        }

        SetBusy(true);
        var ct = owner.ActionToken();
        try
        {
            await BackgroundRead
                .RunAsync(token => services.RemoveAsync(SourcePath, Name, token), ct)
                .ConfigureAwait(true);
            if (!disposed && owner.CanApply)
            {
                owner.ShowStatus(McpSettingsStrings.StatusDeleted(Name), isError: false);
                owner.Reload();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception) when (McpSettingsSectionViewModel.Expected(exception))
        {
            if (!disposed && owner.CanApply)
            {
                owner.ShowError(exception.Message);
            }
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task TestAsync(CancellationToken _)
    {
        var services = owner.Services;
        if (services is null || disposed)
        {
            return;
        }

        if (owner.CanApply)
        {
            TestResultText = McpSettingsStrings.RowTesting;
            OnPropertyChanged(nameof(HasTestResult));
        }

        SetBusy(true);
        var ct = owner.ActionToken();
        try
        {
            var probe = await BackgroundRead
                .RunAsync(token => services.ProbeAsync(Name, token), ct)
                .ConfigureAwait(true);
            if (disposed || !owner.CanApply)
            {
                return;
            }

            TestResultText = probe.IsReady
                ? McpSettingsStrings.TestOk(
                    TextFormat.Elapsed(probe.Elapsed),
                    probe.Tools.Count,
                    McpSettingsStrings.Capabilities(probe.Capabilities))
                : McpSettingsStrings.TestFailed(TextFormat.Elapsed(probe.Elapsed), probe.Error ?? "-");
            OnPropertyChanged(nameof(HasTestResult));
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception) when (McpSettingsSectionViewModel.Expected(exception))
        {
            if (!disposed && owner.CanApply)
            {
                TestResultText = McpSettingsStrings.TestFailed("-", exception.Message);
                OnPropertyChanged(nameof(HasTestResult));
                owner.ShowError(exception.Message);
            }
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task LoadToolsAsync()
    {
        if (toolsLoaded || disposed)
        {
            return;
        }

        var services = owner.Services;
        if (services is null)
        {
            return;
        }

        var ct = owner.ActionToken();
        IReadOnlyList<McpToolAdapter> tools;
        try
        {
            tools = await BackgroundRead
                .RunAsync(token => Task.FromResult(services.ToolsOf(Name)), ct)
                .ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception exception) when (McpSettingsSectionViewModel.Expected(exception))
        {
            return;
        }

        if (disposed || !owner.CanApply)
        {
            return;
        }

        ApplyTools(tools);
        toolsLoaded = true;
        OnPropertyChanged(nameof(ToolsEmpty));
    }

    private async Task RefreshToolsAsync(CancellationToken _)
    {
        var services = owner.Services;
        if (services is null || disposed)
        {
            return;
        }

        SetBusy(true);
        var ct = owner.ActionToken();
        try
        {
            IReadOnlyList<McpToolAdapter> tools = IsReady
                ? await BackgroundRead
                    .RunAsync(token => services.Manager.RefreshToolsAsync(Name, token), ct)
                    .ConfigureAwait(true)
                : await BackgroundRead
                    .RunAsync(token => Task.FromResult(services.ToolsOf(Name)), ct)
                    .ConfigureAwait(true);
            if (disposed || !owner.CanApply)
            {
                return;
            }

            ApplyTools(tools);
            toolsLoaded = true;
            OnPropertyChanged(nameof(ToolsEmpty));
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception) when (McpSettingsSectionViewModel.Expected(exception))
        {
            if (!disposed && owner.CanApply)
            {
                owner.ShowError(exception.Message);
            }
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void ApplyTools(IReadOnlyList<McpToolAdapter> tools)
    {
        Tools.Clear();
        ToolsTruncated = tools.Count > McpSettingsSectionViewModel.MaxVisibleTools;
        var kept = Math.Min(tools.Count, McpSettingsSectionViewModel.MaxVisibleTools);
        for (var index = 0; index < kept; index++)
        {
            var tool = tools[index];
            Tools.Add(new McpToolRowViewModel(
                tool.ExposedName,
                tool.Definition.Description ?? string.Empty,
                McpSettingsStrings.SideEffect(tool.SideEffect)));
        }

        OnPropertyChanged(nameof(ToolsTruncated));
        OnPropertyChanged(nameof(ToolsTruncatedText));
        OnPropertyChanged(nameof(HasTools));
    }

    private void ToggleExpand() => IsExpanded = !expanded;

    private void SetBusy(bool busy)
    {
        actionRunning = busy;
        start.NotifyCanExecuteChanged();
        stop.NotifyCanExecuteChanged();
        toggleRunning.NotifyCanExecuteChanged();
        restart.NotifyCanExecuteChanged();
        test.NotifyCanExecuteChanged();
        refreshTools.NotifyCanExecuteChanged();
        delete.NotifyCanExecuteChanged();
    }
}

/// <summary>One tool a server exposes: its exposed name, the server's description and its localized side effect.</summary>
internal sealed record McpToolRowViewModel(string Name, string Description, string SideEffectText)
{
    public bool HasDescription => Description.Length > 0;
}

/// <summary>One row of the editor's env/header tables; a removal button each, and every keystroke re-validates the entry.</summary>
internal sealed class McpKeyValueRowViewModel : NotifyingObject
{
    private readonly Action changed;
    private string key;
    private string value;

    public McpKeyValueRowViewModel(string key, string value, Action changed, Action<McpKeyValueRowViewModel?> remove)
    {
        this.key = key;
        this.value = value;
        this.changed = changed ?? throw new ArgumentNullException(nameof(changed));
        RemoveCommand = new ItemCommand<McpKeyValueRowViewModel>(remove);
    }

    public string Key
    {
        get => key;
        set
        {
            if (Set(ref key, value ?? string.Empty))
            {
                changed();
            }
        }
    }

    public string Value
    {
        get => value;
        set
        {
            if (Set(ref value, value ?? string.Empty))
            {
                changed();
            }
        }
    }

    public ICommand RemoveCommand { get; }
}

/// <summary>
/// The entry editor: every field of <see cref="McpServerEntry"/> as editable text, the env/header tables, the save target
/// and live validation. <see cref="BuildEntry"/> starts from the original entry when there is one, so an edit keeps the
/// fields Forge does not understand (<see cref="McpServerEntry.RawNodeJson"/>).
/// </summary>
internal sealed class McpServerEditorViewModel : NotifyingObject
{
    private readonly McpServerEntry? original;
    private readonly string globalPath;
    private readonly string? projectPath;
    private readonly bool isNew;
    private string nameText;
    private bool stdio;
    private bool http;
    private string commandText;
    private string argsText;
    private string workingDirectoryText;
    private string urlText;
    private string readOnlyToolsText;
    private string timeoutText;
    private bool timeoutInvalid;
    private bool saveToProject;

    public McpServerEditorViewModel(McpServerEntry entry, string globalPath, string? projectPath, bool isNew)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentException.ThrowIfNullOrWhiteSpace(globalPath);
        original = isNew ? null : entry;
        this.globalPath = globalPath;
        this.projectPath = projectPath;
        this.isNew = isNew;
        nameText = entry.Name;
        stdio = entry.IsStdio || !entry.IsHttp;
        http = entry.IsHttp;
        commandText = entry.Command ?? string.Empty;
        argsText = string.Join("\n", entry.Args);
        workingDirectoryText = entry.WorkingDirectory ?? string.Empty;
        urlText = entry.Url ?? string.Empty;
        foreach (var pair in entry.Env)
        {
            EnvRows.Add(new McpKeyValueRowViewModel(pair.Key, pair.Value, RefreshIssues, RemoveEnvRow));
        }

        foreach (var pair in entry.Headers)
        {
            HeaderRows.Add(new McpKeyValueRowViewModel(pair.Key, pair.Value, RefreshIssues, RemoveHeaderRow));
        }

        readOnlyToolsText = string.Join("\n", entry.ReadOnlyTools);
        timeoutText = entry.TimeoutSeconds?.ToString("0.###", CultureInfo.InvariantCulture) ?? string.Empty;
        saveToProject = projectPath is not null &&
                        string.Equals(entry.SourcePath, projectPath, StringComparison.Ordinal);
        AddEnvCommand = new RelayCommand(AddEnvRow);
        AddHeaderCommand = new RelayCommand(AddHeaderRow);
    }

    public string Title => isNew ? McpSettingsStrings.EditorTitleAdd : McpSettingsStrings.EditorTitleEdit(nameText);

    public string Name
    {
        get => nameText;
        set
        {
            if (Set(ref nameText, value ?? string.Empty))
            {
                RefreshIssues();
                OnPropertyChanged(nameof(Title));
            }
        }
    }

    public bool IsStdio
    {
        get => stdio;
        set
        {
            if (!Set(ref stdio, value))
            {
                return;
            }

            if (value && http)
            {
                http = false;
                OnPropertyChanged(nameof(IsHttp));
            }

            RefreshIssues();
        }
    }

    public bool IsHttp
    {
        get => http;
        set
        {
            if (!Set(ref http, value))
            {
                return;
            }

            if (value && stdio)
            {
                stdio = false;
                OnPropertyChanged(nameof(IsStdio));
            }

            RefreshIssues();
        }
    }

    public string Command
    {
        get => commandText;
        set
        {
            if (Set(ref commandText, value ?? string.Empty))
            {
                RefreshIssues();
            }
        }
    }

    public string ArgsText
    {
        get => argsText;
        set
        {
            if (Set(ref argsText, value ?? string.Empty))
            {
                RefreshIssues();
            }
        }
    }

    public string WorkingDirectory
    {
        get => workingDirectoryText;
        set
        {
            if (Set(ref workingDirectoryText, value ?? string.Empty))
            {
                RefreshIssues();
            }
        }
    }

    public string Url
    {
        get => urlText;
        set
        {
            if (Set(ref urlText, value ?? string.Empty))
            {
                RefreshIssues();
            }
        }
    }

    public ObservableCollection<McpKeyValueRowViewModel> EnvRows { get; } = [];

    public ObservableCollection<McpKeyValueRowViewModel> HeaderRows { get; } = [];

    public string ReadOnlyToolsText
    {
        get => readOnlyToolsText;
        set
        {
            if (Set(ref readOnlyToolsText, value ?? string.Empty))
            {
                RefreshIssues();
            }
        }
    }

    public string TimeoutText
    {
        get => timeoutText;
        set
        {
            if (Set(ref timeoutText, value ?? string.Empty))
            {
                RefreshIssues();
            }
        }
    }

    public string GlobalTargetText => McpSettingsStrings.TargetGlobal(globalPath);

    public string ProjectTargetText => projectPath is null ? string.Empty : McpSettingsStrings.TargetProject(projectPath);

    public bool HasProjectTarget => projectPath is not null;

    public bool SaveToGlobal
    {
        get => !saveToProject;
        set
        {
            if (value)
            {
                SetSaveToProject(false);
            }
        }
    }

    public bool SaveToProject
    {
        get => saveToProject;
        set => SetSaveToProject(value);
    }

    /// <summary>The file a save writes; the project file only when it exists and the user picked it.</summary>
    public string TargetPath => saveToProject && projectPath is not null ? projectPath : globalPath;

    /// <summary>One localized line per problem; live-recomputed on every edit.</summary>
    public ObservableCollection<string> Issues { get; } = [];

    public bool HasIssues => Issues.Count > 0;

    public ICommand AddEnvCommand { get; }

    public ICommand AddHeaderCommand { get; }

    /// <summary>The entry the editor currently spells; normalized the way the file writer wants it.</summary>
    public McpServerEntry BuildEntry()
    {
        var basedOn = original ?? new McpServerEntry { Name = nameText };
        var entry = basedOn with
        {
            Name = nameText,
            Command = stdio ? BlankToNull(commandText) : null,
            Url = http ? BlankToNull(urlText) : null,
            WorkingDirectory = stdio ? BlankToNull(workingDirectoryText) : null,
            Args = SplitLines(argsText),
            Env = PairsOf(EnvRows),
            Headers = PairsOf(HeaderRows),
            ReadOnlyTools = SplitLines(readOnlyToolsText),
            TimeoutSeconds = ParseTimeout(),
        };
        return entry.Normalized();
    }

    /// <summary>Re-derives the validation lines (and the title/target labels) after a language switch or an edit.</summary>
    public void RefreshIssues()
    {
        var entry = BuildEntry();
        var lines = new List<string>(4);
        foreach (var issue in entry.Validate())
        {
            lines.Add(McpSettingsStrings.IssueText(issue));
        }

        if (timeoutInvalid)
        {
            lines.Add(McpSettingsStrings.ValidationTimeoutNumber);
        }

        Issues.Clear();
        foreach (var line in lines)
        {
            Issues.Add(line);
        }

        OnPropertyChanged(nameof(HasIssues));
    }

    public void RefreshTexts()
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(GlobalTargetText));
        OnPropertyChanged(nameof(ProjectTargetText));
        RefreshIssues();
    }

    private void AddEnvRow()
    {
        EnvRows.Add(new McpKeyValueRowViewModel(string.Empty, string.Empty, RefreshIssues, RemoveEnvRow));
        RefreshIssues();
    }

    private void AddHeaderRow()
    {
        HeaderRows.Add(new McpKeyValueRowViewModel(string.Empty, string.Empty, RefreshIssues, RemoveHeaderRow));
        RefreshIssues();
    }

    private void RemoveEnvRow(McpKeyValueRowViewModel? row)
    {
        if (row is not null)
        {
            EnvRows.Remove(row);
        }

        RefreshIssues();
    }

    private void RemoveHeaderRow(McpKeyValueRowViewModel? row)
    {
        if (row is not null)
        {
            HeaderRows.Remove(row);
        }

        RefreshIssues();
    }

    private void SetSaveToProject(bool value)
    {
        if (Set(ref saveToProject, value))
        {
            OnPropertyChanged(nameof(SaveToGlobal));
            OnPropertyChanged(nameof(TargetPath));
        }
    }

    private double? ParseTimeout()
    {
        timeoutInvalid = false;
        var text = timeoutText.Trim();
        if (text.Length == 0)
        {
            return null;
        }

        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
        {
            timeoutInvalid = true;
            return null;
        }

        return seconds;
    }

    private static string? BlankToNull(string text) => string.IsNullOrWhiteSpace(text) ? null : text;

    private static IReadOnlyList<string> SplitLines(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var lines = new List<string>();
        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length > 0)
            {
                lines.Add(trimmed);
            }
        }

        return lines;
    }

    private static IReadOnlyList<KeyValuePair<string, string>> PairsOf(ObservableCollection<McpKeyValueRowViewModel> rows)
    {
        var pairs = new List<KeyValuePair<string, string>>(rows.Count);
        foreach (var row in rows)
        {
            var key = row.Key.Trim();
            if (key.Length > 0)
            {
                pairs.Add(new KeyValuePair<string, string>(key, row.Value));
            }
        }

        return pairs;
    }
}

/// <summary>One server read from the import source; the checkbox decides, the badge says whether the name is taken.</summary>
internal sealed class McpImportCandidateViewModel : NotifyingObject
{
    private bool selected;

    public McpImportCandidateViewModel(McpServerEntry entry, bool alreadyExists, string? sourcePath)
    {
        Entry = entry ?? throw new ArgumentNullException(nameof(entry));
        AlreadyExists = alreadyExists;
        SourceText = McpImportSource.LabelOf(sourcePath);
        selected = !alreadyExists;
    }

    public McpServerEntry Entry { get; }

    /// <summary>Which application's file this server came from ("Claude Code", "Cursor", …); empty when unknown.</summary>
    public string SourceText { get; }

    public bool HasSource => SourceText.Length > 0;

    public string Name => Entry.Name;

    public string TransportText => Entry.IsHttp ? McpSettingsStrings.TransportHttp : McpSettingsStrings.TransportStdio;

    public string Detail => Entry.Command ?? Entry.Url ?? string.Empty;

    public bool HasDetail => Detail.Length > 0;

    public bool AlreadyExists { get; }

    public bool IsSelected
    {
        get => selected;
        set => Set(ref selected, value);
    }
}

/// <summary>
/// One config another application keeps its MCP servers in. The names are the applications' own ("Claude Code", "Cursor"),
/// so they are not translated; only the sentences around them are.
/// </summary>
internal sealed record McpImportSource(string Label, string FilePath)
{
    /// <summary>The sources scanned by the import panel: the user's own files, in the order a duplicate name is decided.</summary>
    public static McpImportSource[] Known { get; } = Build();

    /// <summary>Which application a file belongs to; empty for a path that is not one of <see cref="Known"/>.</summary>
    public static string LabelOf(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        foreach (var source in Known)
        {
            if (string.Equals(source.FilePath, path, StringComparison.OrdinalIgnoreCase))
            {
                return source.Label;
            }
        }

        return string.Empty;
    }

    private static McpImportSource[] Build()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var sources = new List<McpImportSource>(3);
        if (!string.IsNullOrEmpty(home))
        {
            sources.Add(new McpImportSource("Claude Code", Path.Combine(home, ".claude.json")));
        }

        if (!string.IsNullOrEmpty(appData))
        {
            sources.Add(new McpImportSource(
                "Claude Desktop",
                Path.Combine(appData, "Claude", "claude_desktop_config.json")));
        }

        if (!string.IsNullOrEmpty(home))
        {
            sources.Add(new McpImportSource("Cursor", Path.Combine(home, ".cursor", "mcp.json")));
        }

        return [.. sources];
    }
}

/// <summary>One built-in preset: its localized title and note, and the apply command that opens the editor prefilled.</summary>
internal sealed class McpPresetOptionViewModel
{
    public McpPresetOptionViewModel(McpPreset preset, ICommand applyCommand)
    {
        Preset = preset ?? throw new ArgumentNullException(nameof(preset));
        ApplyCommand = applyCommand ?? throw new ArgumentNullException(nameof(applyCommand));
    }

    public McpPreset Preset { get; }

    public ICommand ApplyCommand { get; }

    public string Title => McpSettingsStrings.PresetTitle(Preset.Id);

    public string Description => McpSettingsStrings.PresetDescription(Preset.Id);

    public bool NeedsPath => Preset.NeedsPath;
}

/// <summary>An <see cref="ICommand"/> that passes its parameter to the delegate; no reflection, AOT-clean.</summary>
internal sealed class ItemCommand<T> : ICommand where T : class
{
    private readonly Action<T?> execute;

    public ItemCommand(Action<T?> execute)
    {
        this.execute = execute ?? throw new ArgumentNullException(nameof(execute));
    }

    public event EventHandler? CanExecuteChanged
    {
        add
        {
        }

        remove
        {
        }
    }

    public bool CanExecute(object? parameter) => true;

    public void Execute(object? parameter) => execute(parameter as T);
}
