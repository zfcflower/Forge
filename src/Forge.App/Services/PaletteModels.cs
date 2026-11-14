using Forge.Editor.Lsp;
using Forge.IO;

namespace Forge.App.Services;

/// <summary>The five sources the palette can search. The order is the order of the source strip.</summary>
internal enum PaletteSource
{
    Files,
    Content,
    Sessions,
    Commands,
    Symbols,
}

/// <summary>What a row stands for, which decides its icon, its detail line and what Enter does.</summary>
internal enum PaletteItemKind
{
    File,
    ContentLine,
    Session,
    Command,
    Symbol,

    /// <summary>A row that only explains why the list is empty (no workspace, no language server, a failed search).</summary>
    Notice,
}

/// <summary>
/// One row of the palette. Immutable: a keystroke builds a new list out of a snapshot, so a row never has to be mutated
/// and the list can be replaced wholesale without the selected row turning into a different one.
/// </summary>
internal sealed record PaletteItem
{
    public required PaletteSource Source { get; init; }

    public required PaletteItemKind Kind { get; init; }

    /// <summary>The primary line: a workspace-relative path, a command title, a session title or a symbol name.</summary>
    public required string Title { get; init; }

    /// <summary>The secondary line: the directory of a file, the project of a session, a symbol's container.</summary>
    public string Detail { get; init; } = string.Empty;

    /// <summary>A short right-aligned tag: the source word for a mixed list, the kind of a symbol.</summary>
    public string Badge { get; init; } = string.Empty;

    /// <summary>The file to open, workspace-relative (the palette's workspace) or absolute.</summary>
    public string? Path { get; init; }

    /// <summary>1-based line to reveal after opening; 0 opens the file at its start.</summary>
    public int Line { get; init; }

    /// <summary>1-based column to reveal; 0 means the start of the line.</summary>
    public int Column { get; init; }

    /// <summary>The session to open.</summary>
    public string? SessionId { get; init; }

    /// <summary>The command to run; a <c>PaletteCommandIds</c> value.</summary>
    public string? CommandId { get; init; }

    /// <summary>The keyboard gesture of a command row, shown right-aligned.</summary>
    public string Gesture { get; init; } = string.Empty;

    public bool HasDetail => Detail.Length > 0;

    public bool HasBadge => Badge.Length > 0;

    public bool HasGesture => Gesture.Length > 0;

    /// <summary>True for a row that only explains an empty list; the view draws it muted and Enter does nothing.</summary>
    public bool IsNotice => Kind == PaletteItemKind.Notice;

    /// <summary>False for a notice row: Enter has nothing to do, so the list does not offer it.</summary>
    public bool IsNavigable => Kind != PaletteItemKind.Notice;

    /// <summary>A notice row: one already-localized sentence that explains the empty list, drawn without an icon.</summary>
    public static PaletteItem Notice(PaletteSource source, string text) =>
        new() { Source = source, Kind = PaletteItemKind.Notice, Title = text };
}

/// <summary>One entry of the source strip: the source, the localization key of its word and its opening gesture.</summary>
internal sealed record PaletteSourceOption(PaletteSource Source, string LabelKey, string Gesture)
{
    public string Label => Localization.Loc.Instance[LabelKey];

    public bool HasGesture => Gesture.Length > 0;

    public string AccessibleName => Gesture.Length == 0 ? Label : Label + " (" + Gesture + ")";
}

/// <summary>A session as the palette matches it: the fields a query is scored against and the preview shows.</summary>
internal sealed record PaletteSessionSnapshot(
    string Id,
    string Title,
    string Project,
    string Branch,
    DateTimeOffset LastActiveAt,
    bool IsArchived,
    bool IsSubAgent)
{
    /// <summary>
    /// The text a query is matched against after the title: project and branch. Built once with the snapshot, so a
    /// keystroke over the session list concatenates nothing.
    /// </summary>
    public string MatchText { get; } = Join(Project, Branch, " ");

    /// <summary>The row's secondary line: <c>project · branch</c>, or whichever of the two exists.</summary>
    public string DetailText { get; } = Join(Project, Branch, " · ");

    private static string Join(string first, string second, string separator) =>
        string.IsNullOrEmpty(first) ? second ?? string.Empty
        : string.IsNullOrEmpty(second) ? first
        : first + separator + second;
}

/// <summary>
/// One document symbol as the palette offers it: the flat form of the language server's tree, with the 0-based line the
/// editor has to reveal (1-based once it becomes a <see cref="PaletteItem"/>).
/// </summary>
internal readonly record struct PaletteSymbol(string Name, string Container, SymbolKind Kind, int Line, int Column);

/// <summary>The symbols of the active editor document, plus the words that say which document they belong to.</summary>
internal sealed record PaletteSymbolSnapshot(IReadOnlyList<PaletteSymbol> Symbols, string Document, string? Problem)
{
    public static PaletteSymbolSnapshot Empty { get; } = new([], string.Empty, null);
}

/// <summary>
/// What the palette asks the shell for: the snapshots it filters, the background refreshes it starts, and the two
/// navigations a row can trigger. Implemented by <c>MainWindowViewModel</c> (its <c>Palette</c> partial files) and
/// replaced by a test double in unit tests, so the view model itself holds no dependency on the window.
/// </summary>
internal interface IPaletteHost
{
    /// <summary>The active session's working directory; null (or empty) when no session is open.</summary>
    string? Workspace { get; }

    /// <summary>The workspace as it reads in a list: the session or project name, never a path.</summary>
    string WorkspaceLabel { get; }

    /// <summary>The commands of the current language, cheapest to rebuild on every open.</summary>
    IReadOnlyList<PaletteCommand> Commands { get; }

    /// <summary>The file list of the last refresh; empty until the first one finished.</summary>
    IReadOnlyList<string> CachedFiles { get; }

    /// <summary>Re-reads the workspace's file list (git listing when possible, otherwise a walk) and caches it.</summary>
    Task<IReadOnlyList<string>> RefreshFilesAsync(CancellationToken ct);

    /// <summary>The sessions of the last refresh.</summary>
    IReadOnlyList<PaletteSessionSnapshot> CachedSessions { get; }

    /// <summary>Re-reads every session the palette may open, newest activity first.</summary>
    Task<IReadOnlyList<PaletteSessionSnapshot>> RefreshSessionsAsync(CancellationToken ct);

    /// <summary>The symbols of the active editor document from the last refresh.</summary>
    PaletteSymbolSnapshot CachedSymbols { get; }

    /// <summary>Asks the active document's language server for its symbols; a no-op snapshot when none is open.</summary>
    Task<PaletteSymbolSnapshot> RefreshSymbolsAsync(CancellationToken ct);

    /// <summary>
    /// Runs the workspace content search for one query, stopping after <paramref name="limit"/> matching lines (the
    /// palette's own budget: the per-source limit, or a multiple of it while directories are excluded).
    /// </summary>
    Task<GrepResult> SearchContentAsync(string query, int limit, CancellationToken ct);

    /// <summary>Opens <paramref name="item"/>: a file at its line, a session, or the command it names.</summary>
    Task ActivateAsync(PaletteItem item, CancellationToken ct);
}
