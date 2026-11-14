using Forge.Abstractions;
using Forge.App.Editing;
using Forge.App.Localization;
using Forge.App.Services;
using Forge.App.Workbench;
using Forge.Editor.Lsp;
using Forge.Editor.Text;
using Forge.IO;

namespace Forge.App.ViewModels;

/// <summary>
/// The palette's place in the shell: it creates the view model, owns the four gestures that open it, and implements
/// <see cref="IPaletteHost"/> - the snapshots the palette filters (files, sessions, symbols), the one search it runs on the
/// workspace, and the three navigations a row can trigger. Everything that touches a window, a session or the language
/// server lives here, so <see cref="PaletteViewModel"/> stays a state machine a test can drive without a window.
/// </summary>
internal sealed partial class MainWindowViewModel : IPaletteHost
{
    private readonly PaletteFileCache paletteFiles = new();
    private PaletteViewModel paletteViewModel = null!;
    private IReadOnlyList<PaletteSessionSnapshot> paletteSessions = [];
    private PaletteSymbolSnapshot paletteSymbols = PaletteSymbolSnapshot.Empty;
    private object? palette;

    /// <summary>
    /// The overlay the window shows: null while the palette is closed, otherwise the view model <c>App.axaml</c> templates
    /// into its view. Publishing and clearing it is what puts the palette on screen and takes it off again.
    /// </summary>
    public object? Palette
    {
        get => palette;
        private set => Set(ref palette, value);
    }

    /// <summary><c>Ctrl+P</c>: the file source.</summary>
    public RelayCommand OpenFilePaletteCommand { get; private set; } = null!;

    /// <summary><c>Ctrl+Shift+P</c>: the command source.</summary>
    public RelayCommand OpenCommandPaletteCommand { get; private set; } = null!;

    /// <summary><c>Ctrl+Shift+F</c>: the content source.</summary>
    public RelayCommand OpenSearchPaletteCommand { get; private set; } = null!;

    /// <summary>Called once from the constructor, before the window exists (the overlay binds the view model).</summary>
    private void InitializePalette()
    {
        // The palette's view model lives as long as the window; the overlay comes and goes with its IsOpen flag.
        paletteViewModel = new PaletteViewModel(this, composition.Preferences);
        paletteViewModel.PropertyChanged += OnPaletteOpenChanged;
        OpenFilePaletteCommand = new RelayCommand(() => TogglePalette(PaletteSource.Files));
        OpenCommandPaletteCommand = new RelayCommand(() => TogglePalette(PaletteSource.Commands));
        OpenSearchPaletteCommand = new RelayCommand(() => TogglePalette(PaletteSource.Content));
        OpenPaletteCommand = new RelayCommand(() => TogglePalette(PaletteSource.Files));

        // Switching session switches the workspace the palette searches; a palette left open would list the files and
        // symbols of the session the user just left, so it closes instead.
        PropertyChanged += OnPaletteActiveSessionChanged;
    }

    /// <summary>The title-bar search button: the same gesture as <c>Ctrl+P</c>, on the same file source.</summary>
    public RelayCommand OpenPaletteCommand { get; private set; } = null!;

    private void OnPaletteActiveSessionChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ActiveSession) && paletteViewModel.IsOpen)
        {
            paletteViewModel.Close();
        }
    }

    /// <summary>Puts the palette on screen while it is open and clears the slot as soon as it closes.</summary>
    private void OnPaletteOpenChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(PaletteViewModel.IsOpen))
        {
            return;
        }

        Palette = paletteViewModel.IsOpen ? paletteViewModel : null;
    }

    /// <summary>
    /// The gestures toggle: pressing <c>Ctrl+P</c> while the file source is already open closes the palette, which is what
    /// the same gesture does in every other editor and keeps a second <c>Ctrl+P</c> from wiping a typed query.
    /// </summary>
    private void TogglePalette(PaletteSource target)
    {
        if (paletteViewModel.IsOpen && paletteViewModel.Source == target)
        {
            paletteViewModel.Close();
            return;
        }

        paletteViewModel.Open(target);
    }

    // ---------------------------------------------------------------- IPaletteHost

    string? IPaletteHost.Workspace => activeSession?.WorkDir;

    string IPaletteHost.WorkspaceLabel => activeSession?.Title ?? string.Empty;

    IReadOnlyList<PaletteCommand> IPaletteHost.Commands => PaletteCommandCatalog.All;

    IReadOnlyList<string> IPaletteHost.CachedFiles => paletteFiles.Cached(activeSession?.WorkDir);

    async Task<IReadOnlyList<string>> IPaletteHost.RefreshFilesAsync(CancellationToken ct) =>
        await paletteFiles.RefreshAsync(activeSession?.WorkDir, composition.Git, ct).ConfigureAwait(true);

    IReadOnlyList<PaletteSessionSnapshot> IPaletteHost.CachedSessions => paletteSessions;

    async Task<IReadOnlyList<PaletteSessionSnapshot>> IPaletteHost.RefreshSessionsAsync(CancellationToken ct)
    {
        // Main sessions only: a sub-agent session is not something the palette can open (the shell refuses it too), so the
        // snapshot never carries one and the palette's own filter only has to defend against a foreign host.
        // A null status asks for every row, deleted ones included; a deleted session is gone for the user, so it is skipped
        // here rather than offered as an "archived" row that opens something the tree no longer shows.
        var infos = await composition.Catalog.QueryAsync(new SessionQuery { ParentSessionId = string.Empty }, ct)
            .ConfigureAwait(true);
        var sessions = new List<PaletteSessionSnapshot>(infos.Count);
        foreach (var info in infos)
        {
            if (info.Status == SessionStatus.Deleted)
            {
                continue;
            }

            sessions.Add(new PaletteSessionSnapshot(
                info.Id,
                info.Title,
                info.ProjectId is { Length: > 0 } projectId && projectNames.TryGetValue(projectId, out var project) ? project : string.Empty,
                info.Branch ?? string.Empty,
                info.LastActiveAt,
                info.Status != SessionStatus.Active,
                info.ParentSessionId is not null));
        }

        paletteSessions = sessions;
        return sessions;
    }

    PaletteSymbolSnapshot IPaletteHost.CachedSymbols => paletteSymbols;

    async Task<PaletteSymbolSnapshot> IPaletteHost.RefreshSymbolsAsync(CancellationToken ct)
    {
        if (ActiveFileDocument is not { HasDocument: true } document || string.IsNullOrEmpty(document.Path))
        {
            paletteSymbols = PaletteSymbolSnapshot.Empty;
            return paletteSymbols;
        }

        var symbols = await document.RequestDocumentSymbolsAsync(ct).ConfigureAwait(true);
        ct.ThrowIfCancellationRequested();
        paletteSymbols = symbols is null
            ? new PaletteSymbolSnapshot([], WorkspaceRelative(document.Path), Loc.Instance[PaletteStrings.NoticeSymbolsUnavailable])
            : new PaletteSymbolSnapshot(PaletteSymbols.Flatten(symbols), WorkspaceRelative(document.Path), null);
        return paletteSymbols;
    }

    async Task<GrepResult> IPaletteHost.SearchContentAsync(string query, int limit, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        if (activeSession is not { WorkDir.Length: > 0 } session)
        {
            return GrepResult.Empty;
        }

        var request = new GrepRequest
        {
            Root = session.WorkDir,
            StartPath = session.WorkDir,
            Pattern = query,
            Regex = false,
            CaseSensitive = false,
            Limit = limit,
        };
        return await Task.Run(() => GrepEngine.SearchAsync(request, ct), ct).ConfigureAwait(true);
    }

    async Task IPaletteHost.ActivateAsync(PaletteItem item, CancellationToken ct)
    {
        switch (item)
        {
            case { Kind: PaletteItemKind.File or PaletteItemKind.ContentLine or PaletteItemKind.Symbol, Path: { Length: > 0 } path }:
                await OpenAtAsync(path, item.Line, item.Column, ct).ConfigureAwait(true);
                break;
            case { Kind: PaletteItemKind.Session, SessionId: { Length: > 0 } sessionId }:
                await OpenSessionByIdAsync(sessionId, ct).ConfigureAwait(true);
                break;
            case { Kind: PaletteItemKind.Command, CommandId: { Length: > 0 } commandId }:
                RunPaletteCommand(commandId);
                break;
        }
    }

    // ---------------------------------------------------------------- commands and navigation

    /// <summary>
    /// Runs one row of the command catalog. The ids are the catalog's own constants, so a row that reaches here always has
    /// an implementation; the default case is unreachable and does nothing.
    /// </summary>
    internal void RunPaletteCommand(string id)
    {
        switch (id)
        {
            case PaletteCommandIds.Files:
                TogglePalette(PaletteSource.Files);
                break;
            case PaletteCommandIds.Content:
                TogglePalette(PaletteSource.Content);
                break;
            case PaletteCommandIds.Sessions:
                TogglePalette(PaletteSource.Sessions);
                break;
            case PaletteCommandIds.Symbols:
                TogglePalette(PaletteSource.Symbols);
                break;
            case PaletteCommandIds.Commands:
                TogglePalette(PaletteSource.Commands);
                break;
            case PaletteCommandIds.Changes:
                ShowChangesCommand.Execute(null);
                break;
            case PaletteCommandIds.Terminal:
                NewTerminalCommand.Execute(null);
                break;
            case PaletteCommandIds.ToggleProjects:
                Workbench.ToggleMinimize(Workbench.ProjectPanel);
                break;
            case PaletteCommandIds.ToggleFiles:
                Workbench.ToggleMinimize(Workbench.FilesPanel);
                break;
            case PaletteCommandIds.TogglePreview:
                Workbench.TogglePreviewCoverMain();
                break;
            case PaletteCommandIds.TodoTab:
                ActivateWorkbenchTab(WorkbenchIds.TodoTab);
                break;
            case PaletteCommandIds.ReviewTab:
                ActivateWorkbenchTab(WorkbenchIds.ReviewTab);
                break;
            case PaletteCommandIds.SpecsTab:
                ActivateWorkbenchTab(WorkbenchIds.SpecsTab);
                break;
            case PaletteCommandIds.AddProject:
                AddProjectCommand.Execute(null);
                break;
            case PaletteCommandIds.RevealGeneral:
                RevealGeneralCommand.Execute(null);
                break;
            case PaletteCommandIds.OpenSettings:
                ToggleSettingsCommand.Execute(null);
                break;
            case PaletteCommandIds.ReloadSettings:
                ReloadSettingsCommand.Execute(null);
                break;
            case PaletteCommandIds.GitFetch:
                GitSync.FetchCommand.Execute(null);
                break;
            case PaletteCommandIds.GitPull:
                GitSync.PullCommand.Execute(null);
                break;
            case PaletteCommandIds.GitPush:
                GitSync.PushCommand.Execute(null);
                break;
        }
    }

    private void ActivateWorkbenchTab(string id)
    {
        if (Workbench.FindTab(id) is { } tab)
        {
            Workbench.ActivateTab(tab);
        }
    }

    /// <summary>
    /// Opens <paramref name="path"/> and puts the caret on the requested line and column. The reveal goes through the same
    /// pending-target mechanism as "go to definition": the view applies it when it attaches, which is what makes a newly
    /// opened tab land on the right line too.
    /// </summary>
    private async Task OpenAtAsync(string path, int line, int column, CancellationToken ct)
    {
        var resolved = ResolveFilePath(path);
        if (resolved is null)
        {
            return;
        }

        var tab = FindFileTab(resolved);
        if (tab is null)
        {
            await OpenFileAsync(resolved, newTab: false, ct).ConfigureAwait(true);
            tab = FindFileTab(resolved);
        }
        else
        {
            Workbench.ActivateTab(tab);
        }

        if (line <= 0 || tab?.Content is not FileDocumentViewModel document ||
            !document.HasDocument || document.HasError || document.IsEditorFaulted)
        {
            return;
        }

        document.RevealEditorPosition(new EditorLspDefinitionTarget(
            resolved,
            new LspPosition(Math.Max(0, line - 1), Math.Max(0, column - 1)),
            PositionEncoding.Utf16));
    }

    /// <summary>The editor whose symbols the palette offers: the preview group's active tab when it holds a document.</summary>
    private FileDocumentViewModel? ActiveFileDocument =>
        Workbench.PreviewTabs.ActiveTab?.Content as FileDocumentViewModel;

    /// <summary>A document's path as the palette lists it and as the session's editor resolves it: relative to the workspace.</summary>
    private string WorkspaceRelative(string path)
    {
        if (activeSession is not { WorkDir.Length: > 0 } session)
        {
            return path;
        }

        try
        {
            var relative = Path.GetRelativePath(session.WorkDir, path);
            return relative.StartsWith("..", StringComparison.Ordinal) ? path : relative.Replace('\\', '/');
        }
        catch (ArgumentException)
        {
            return path;
        }
    }
}
