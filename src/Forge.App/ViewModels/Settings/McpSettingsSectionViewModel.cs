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

    /// <summary>The names each file listed at the last refresh, so opening an editor needs no file read of its own.</summary>
    private HashSet<string> globalNames = new(StringComparer.Ordinal);
    private HashSet<string> projectNames = new(StringComparer.Ordinal);

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
        ReloadCommand = new AsyncRelayCommand(ReloadAndApplyAsync, onError: ReportUnexpected);

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

    /// <summary>
    /// The reload action: re-read both files into the running manager (the page's own refresh only redraws), then redraw. A
    /// hand edit therefore reaches the servers on the click, and a file that cannot be read leaves the servers alone.
    /// </summary>
    private async Task ReloadAndApplyAsync(CancellationToken ct)
    {
        if (disposed || !CanApply || composition.Mcp is not { } services)
        {
            ShowError(McpSettingsStrings.StatusUnavailable);
            return;
        }

        try
        {
            await BackgroundRead.RunAsync(token => services.ReloadAsync(token), ct).ConfigureAwait(true);
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

        ShowStatus(McpSettingsStrings.StatusReloaded, isError: false);
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

        Rebuild(snapshot, services.Manager.States, KeptServers(services, snapshot));
        return true;
    }

    /// <summary>
    /// The running servers the file read could not see (a file whose root is unreadable contributes no entries, and the manager
    /// keeps its servers). They have no row, so the page names them and the file to fix instead of hiding them.
    /// </summary>
    private static List<(string Name, string Source)> KeptServers(McpServices services, McpConfigSnapshot snapshot)
    {
        var fromFiles = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in snapshot.Files)
        {
            foreach (var entry in file.Entries)
            {
                fromFiles.Add(entry.Name);
            }
        }

        var kept = new List<(string Name, string Source)>(2);
        foreach (var name in services.Manager.ServerNames)
        {
            if (fromFiles.Contains(name))
            {
                continue;
            }

            var config = services.Manager.GetServer(name);
            kept.Add((name, config.Source ?? string.Empty));
        }

        return kept;
    }

    /// <summary>Rebuilds rows and diagnostics from the snapshot, keeping the rows that still exist (expansion survives).</summary>
    private void Rebuild(
        McpConfigSnapshot snapshot,
        IReadOnlyList<McpServerState> states,
        IReadOnlyList<(string Name, string Source)> keptServers)
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

        foreach (var (name, source) in keptServers)
        {
            diagnostics.Add(McpSettingsStrings.DiagnosticKeptServer(name, source));
        }

        var globals = new HashSet<string>(StringComparer.Ordinal);
        var projects = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in snapshot.Files)
        {
            var into = file.IsProject ? projects : globals;
            foreach (var entry in file.Entries)
            {
                into.Add(entry.Name);
            }
        }

        globalNames = globals;
        projectNames = projects;

        var existing = new Dictionary<string, McpServerRowViewModel>(Servers.Count, StringComparer.Ordinal);
        foreach (var row in Servers)
        {
            existing[row.Name] = row;
        }

        var rebuilt = new List<McpServerRowViewModel>(entries.Count);
        foreach (var (name, entry) in entries)
        {
            var (path, isProject) = sources[name];
            if (!existing.Remove(name, out var row))
            {
                row = new McpServerRowViewModel(this, entry, path, isProject);
            }

            row.Apply(entry, byName.GetValueOrDefault(name), path, isProject);
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

        editor = new McpServerEditorViewModel(
            entry,
            services.Store.GlobalPath,
            services.Store.ProjectPath,
            isNew,
            NamesOfTheOtherFile(entry));
        OnPropertyChanged(nameof(Editor));
        OnPropertyChanged(nameof(IsEditing));
        ClearStatus();
    }

    /// <summary>
    /// The names listed by the file the entry does not live in — what a save can collide with. Taken from the last refresh
    /// rather than the disk: opening an editor must not read a file on the UI thread.
    /// </summary>
    private IReadOnlyCollection<string> NamesOfTheOtherFile(McpServerEntry entry) =>
        string.Equals(entry.SourcePath, composition.Mcp?.Store.ProjectPath, StringComparison.Ordinal)
            ? globalNames
            : projectNames;

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
            var replacedName = current.OriginalName;
            var replacedPath = current.OriginalSourcePath;
            await BackgroundRead.RunAsync(
                token => services.SaveAsync(path, entry, token, replacedName, replacedPath),
                ct).ConfigureAwait(true);
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

    /// <summary>The framework boundary for async actions: an unexpected exception is logged and shown, never thrown into the dispatcher.</summary>
    internal void ReportUnexpected(Exception exception)
    {
        AppLogs.Instruments.ZLogWarning($"mcp settings action failed unexpectedly: {exception.Message}");
        ShowError(exception.Message);
    }
}
