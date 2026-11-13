using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Forge.App.Localization;
using Forge.App.ViewModels;
using Forge.IO;
using Forge.App.Logging;
using ZLogger;

namespace Forge.App.Preferences;

/// <summary>UI language choice; <see cref="System"/> follows the OS UI culture (Chinese when it is zh-*, otherwise English).</summary>
internal enum UiLanguage
{
    System,
    Chinese,
    English,
}

/// <summary>
/// User preferences that apply live (no restart). Owned by <see cref="PreferencesStore"/>, edited only on the UI thread;
/// every setter raises PropertyChanged and the store persists after a short debounce. Values are validated by the
/// setters: an empty family falls back to the default, sizes are clamped to [<see cref="MinFontSize"/>, <see cref="MaxFontSize"/>].
/// </summary>
internal sealed class AppPreferences : ObservableObject
{
    public const double MinFontSize = 9;
    public const double MaxFontSize = 28;
    /// <summary>
    /// The interface starts on the system <c>Segoe UI</c> chain rather than the embedded Inter: Inter reads visibly
    /// larger at the same pixel size because of its bigger x-height. Inter still ships with the app
    /// (<c>WithInterFont()</c> registers its collection as <c>fonts:Inter</c>), so <c>fonts:Inter#Inter</c> stays a valid
    /// choice — a bare <c>Inter</c> only matches an OS-installed font and would silently fall through to the next family.
    /// </summary>
    public const string DefaultUiFontFamily = "Segoe UI, PingFang SC, Microsoft YaHei, sans-serif";
    public const string DefaultCodeFontFamily = "Consolas, Cascadia Mono, Menlo, monospace";
    public const double DefaultUiFontSize = 13;
    public const double DefaultCodeFontSize = 13;

    /// <summary>Lower bound of the terminal scrollback; below it the ring is too short to scroll a build log.</summary>
    public const int MinScrollbackLines = 1000;

    /// <summary>Upper bound of the terminal scrollback; the emulator caps there too.</summary>
    public const int MaxScrollbackLines = 100000;

    public const int DefaultScrollbackLines = 10000;
    /// <summary>The pull-strategy tokens of the <c>preferences</c> section; an unknown token falls back to <see cref="GitPullStrategyFastForwardOnly"/>.</summary>
    public const string GitPullStrategyFastForwardOnly = "ff-only";
    public const string GitPullStrategyRebase = "rebase";
    public const string GitPullStrategyMerge = "merge";
    public const string DefaultGitPullStrategy = GitPullStrategyFastForwardOnly;
    public const int MinGitNetworkTimeoutSeconds = 10;
    public const int MaxGitNetworkTimeoutSeconds = 600;
    public const int DefaultGitNetworkTimeoutSeconds = 120;
    public const bool DefaultGitStepCommits = true;
    public const int MinGitChecksPollSeconds = 10;
    public const int MaxGitChecksPollSeconds = 300;
    public const int DefaultGitChecksPollSeconds = 30;
    public const int MinGitChecksMaxWaitMinutes = 1;
    public const int MaxGitChecksMaxWaitMinutes = 180;
    public const int DefaultGitChecksMaxWaitMinutes = 30;

    /// <summary>Whether any notification is shown at all (toast in the window, desktop notification in the background).</summary>
    public const bool DefaultNotifications = true;

    /// <summary>Longest a <c>bash</c> call may run unless the model asks for less; clamped to [<see cref=MinToolBashTimeoutSeconds/>, <see cref=MaxToolBashTimeoutSeconds/>].</summary>
    public const int MinToolBashTimeoutSeconds = 1;
    public const int MaxToolBashTimeoutSeconds = 600;
    public const int DefaultToolBashTimeoutSeconds = 120;

    /// <summary>Editor indentation width; clamped to [<see cref="MinEditorIndentationSize"/>, <see cref="MaxEditorIndentationSize"/>].</summary>
    public const int MinEditorIndentationSize = 1;
    public const int MaxEditorIndentationSize = 8;
    public const int DefaultEditorIndentationSize = 4;

    /// <summary>The largest column a ruler may mark; beyond it the ruler would never be reached by a sane line.</summary>
    public const int MaxEditorColumnRuler = 400;

    /// <summary>The three TextMate theme tokens: follow the UI, always light, always dark.</summary>
    public const string EditorThemeAuto = "auto";
    public const string EditorThemeLight = "light";
    public const string EditorThemeDark = "dark";
    public const string DefaultEditorTheme = EditorThemeAuto;

    /// <summary>
    /// Longest edge of a thumbnail in a chat tool row, in device-independent pixels; clamped to
    /// [<see cref="MinToolImageThumbnailEdge"/>, <see cref="MaxToolImageThumbnailEdge"/>]. The stored image is never re-encoded for
    /// this — it only bounds what is decoded for the row.
    /// </summary>
    public const int MinToolImageThumbnailEdge = 64;
    public const int MaxToolImageThumbnailEdge = 512;
    public const int DefaultToolImageThumbnailEdge = 256;

    /// <summary>
    /// Whether a tool result's images are sent to the model (default on). Off keeps them in the transcript and sends the text
    /// placeholder the result already carries, which is what a text-only model or a cost-conscious user wants.
    /// </summary>
    public const bool DefaultSendToolImagesToModel = true;

    /// <summary>Rows a CSV/TSV preview parses before it stops; clamped to [<see cref="MinFilePreviewRows"/>, <see cref="MaxFilePreviewRows"/>].</summary>
    public const int MinFilePreviewRows = 50;
    public const int MaxFilePreviewRows = 10_000;
    public const int DefaultFilePreviewRows = 1_000;

    /// <summary>Characters kept from one <c>.ipynb</c> output; clamped to [<see cref="MinNotebookOutputChars"/>, <see cref="MaxNotebookOutputChars"/>].</summary>
    public const int MinNotebookOutputChars = 1_000;
    public const int MaxNotebookOutputChars = 200_000;
    public const int DefaultNotebookOutputChars = 20_480;

    /// <summary>
    /// Largest image (in bytes, on disk) the image preview opens; clamped to
    /// [<see cref="MinPreviewImageBytes"/>, <see cref="MaxPreviewImageBytes"/>]. Beyond it the document shows the placeholder card and
    /// offers the system's own viewer, which streams instead of holding the whole file in memory.
    /// </summary>
    public const int MinPreviewImageBytes = 1 * 1024 * 1024;
    public const int MaxPreviewImageBytes = 512 * 1024 * 1024;
    public const int DefaultPreviewImageBytes = 20 * 1024 * 1024;

    private string uiFontFamily = DefaultUiFontFamily;
    private double uiFontSize = DefaultUiFontSize;
    private string codeFontFamily = DefaultCodeFontFamily;
    private double codeFontSize = DefaultCodeFontSize;
    private UiLanguage language = UiLanguage.System;
    private string terminalShell = string.Empty;
    private string terminalArguments = string.Empty;
    private int terminalScrollbackLines = DefaultScrollbackLines;
    private string gitPullStrategy = DefaultGitPullStrategy;
    private int gitNetworkTimeoutSeconds = DefaultGitNetworkTimeoutSeconds;
    private bool gitStepCommits = DefaultGitStepCommits;
    private int gitChecksPollSeconds = DefaultGitChecksPollSeconds;
    private int gitChecksMaxWaitMinutes = DefaultGitChecksMaxWaitMinutes;
    private bool notifications = DefaultNotifications;
    private string gitGhPath = string.Empty;
    private string toolsShellPath = string.Empty;
    private string toolsRipgrepPath = string.Empty;
    private int toolsBashTimeoutSeconds = DefaultToolBashTimeoutSeconds;
    private IReadOnlyList<string> toolsDisabled = [];
    private bool editorShowLineNumbers = true;
    private bool editorWordWrap;
    private bool editorShowSpaces;
    private bool editorShowTabs;
    private bool editorShowEndOfLine;
    private bool editorShowControlCharacters;
    private bool editorScrollBelowDocument = true;
    private bool editorHighlightCurrentLine = true;
    private bool editorHyperlinks = true;
    private bool editorAutoFolding = true;
    private int editorIndentationSize = DefaultEditorIndentationSize;
    private bool editorTabsToSpaces = true;
    private string editorColumnRulers = string.Empty;
    private string editorTheme = DefaultEditorTheme;
    private int toolImageThumbnailEdge = DefaultToolImageThumbnailEdge;
    private bool sendToolImagesToModel = DefaultSendToolImagesToModel;
    private int filePreviewRows = DefaultFilePreviewRows;
    private int notebookOutputChars = DefaultNotebookOutputChars;
    private int previewImageBytes = DefaultPreviewImageBytes;

    public string UiFontFamily { get => uiFontFamily; set => Set(ref uiFontFamily, Family(value, DefaultUiFontFamily)); }

    public double UiFontSize { get => uiFontSize; set => Set(ref uiFontSize, Size(value, DefaultUiFontSize)); }

    public string CodeFontFamily { get => codeFontFamily; set => Set(ref codeFontFamily, Family(value, DefaultCodeFontFamily)); }

    public double CodeFontSize { get => codeFontSize; set => Set(ref codeFontSize, Size(value, DefaultCodeFontSize)); }

    public UiLanguage Language { get => language; set => Set(ref language, value); }

    /// <summary>Shell executable for new terminals; empty means the platform default (pwsh/powershell on Windows, $SHELL or /bin/sh elsewhere).</summary>
    public string TerminalShell { get => terminalShell; set => Set(ref terminalShell, (value ?? string.Empty).Trim()); }

    /// <summary>Arguments for <see cref="TerminalShell"/>, space separated with double quotes for grouping.</summary>
    public string TerminalArguments { get => terminalArguments; set => Set(ref terminalArguments, value ?? string.Empty); }

    /// <summary>Scrollback lines a terminal keeps; clamped to [<see cref="MinScrollbackLines"/>, <see cref="MaxScrollbackLines"/>].</summary>
    public int TerminalScrollbackLines
    {
        get => terminalScrollbackLines;
        set => Set(ref terminalScrollbackLines, Math.Clamp(value, MinScrollbackLines, MaxScrollbackLines));
    }

    /// <summary>
    /// How <see cref="Forge.Workspace.GitPullStrategy"/> integrates the upstream: <see cref="GitPullStrategyFastForwardOnly"/>
    /// (default), <see cref="GitPullStrategyRebase"/> or <see cref="GitPullStrategyMerge"/>. The token is stored as is, so a
    /// file written by a newer build still opens; an unknown token means the default.
    /// </summary>
    public string GitPullStrategy { get => gitPullStrategy; set => Set(ref gitPullStrategy, PullStrategy(value)); }

    /// <summary>Bounded wait for fetch, pull and push, in seconds; clamped to [<see cref="MinGitNetworkTimeoutSeconds"/>, <see cref="MaxGitNetworkTimeoutSeconds"/>].</summary>
    public int GitNetworkTimeoutSeconds
    {
        get => gitNetworkTimeoutSeconds;
        set => Set(ref gitNetworkTimeoutSeconds, Math.Clamp(value, MinGitNetworkTimeoutSeconds, MaxGitNetworkTimeoutSeconds));
    }

    /// <summary>
    /// Whether finishing a TODO item commits that step's changes to the session branch (default on). Read at the start of
    /// every step commit, so a change applies to the next item that is marked done.
    /// </summary>
    public bool GitStepCommits { get => gitStepCommits; set => Set(ref gitStepCommits, value); }

    /// <summary>Seconds between two polls of a pull request's checks in wait mode; clamped to [<see cref="MinGitChecksPollSeconds"/>, <see cref="MaxGitChecksPollSeconds"/>].</summary>
    public int GitChecksPollSeconds
    {
        get => gitChecksPollSeconds;
        set => Set(ref gitChecksPollSeconds, Math.Clamp(value, MinGitChecksPollSeconds, MaxGitChecksPollSeconds));
    }

    /// <summary>Longest wait mode keeps polling, in minutes; clamped to [<see cref="MinGitChecksMaxWaitMinutes"/>, <see cref="MaxGitChecksMaxWaitMinutes"/>].</summary>
    public int GitChecksMaxWaitMinutes
    {
        get => gitChecksMaxWaitMinutes;
        set => Set(ref gitChecksMaxWaitMinutes, Math.Clamp(value, MinGitChecksMaxWaitMinutes, MaxGitChecksMaxWaitMinutes));
    }

    /// <summary>
    /// Whether the user wants to be told about things at all: off means neither a toast in the window nor a desktop
    /// notification. Read at the start of every notification, so a change applies without a restart.
    /// </summary>
    public bool Notifications { get => notifications; set => Set(ref notifications, value); }

    /// <summary>
    /// The GitHub CLI executable; empty (default) means <c>gh</c> from PATH. Trimmed. Read at the start of every <c>gh</c>
    /// call, so a change applies to the next call without a restart.
    /// </summary>
    public string GitGhPath { get => gitGhPath; set => Set(ref gitGhPath, (value ?? string.Empty).Trim()); }

    /// <summary>
    /// The shell a script-mode <c>bash</c> call runs in; empty (default) means the platform default (<c>pwsh</c> then
    /// <c>cmd</c> on Windows, <c>/bin/bash</c> then <c>/bin/sh</c> elsewhere). Trimmed; read when the file tools are built.
    /// </summary>
    public string ToolsShellPath { get => toolsShellPath; set => Set(ref toolsShellPath, (value ?? string.Empty).Trim()); }

    /// <summary>
    /// An optional ripgrep executable used to speed up <c>grep</c>; empty (default) means the managed search engine. Trimmed;
    /// read when the file tools are built.
    /// </summary>
    public string ToolsRipgrepPath { get => toolsRipgrepPath; set => Set(ref toolsRipgrepPath, (value ?? string.Empty).Trim()); }

    /// <summary>Default timeout of a <c>bash</c> call in seconds; clamped to [<see cref=MinToolBashTimeoutSeconds/>, <see cref=MaxToolBashTimeoutSeconds/>].</summary>
    public int ToolsBashTimeoutSeconds
    {
        get => toolsBashTimeoutSeconds;
        set => Set(ref toolsBashTimeoutSeconds, Math.Clamp(value, MinToolBashTimeoutSeconds, MaxToolBashTimeoutSeconds));
    }

    /// <summary>Whether the editor shows a line-number margin (default on).</summary>
    public bool EditorShowLineNumbers { get => editorShowLineNumbers; set => Set(ref editorShowLineNumbers, value); }

    /// <summary>Word wrap a newly opened file starts with (default off, matching the wrap icon's default state).</summary>
    public bool EditorWordWrap { get => editorWordWrap; set => Set(ref editorWordWrap, value); }

    /// <summary>Whether spaces are drawn as interpuncts (default off).</summary>
    public bool EditorShowSpaces { get => editorShowSpaces; set => Set(ref editorShowSpaces, value); }

    /// <summary>Whether tabs are drawn as arrows (default off).</summary>
    public bool EditorShowTabs { get => editorShowTabs; set => Set(ref editorShowTabs, value); }

    /// <summary>Whether line ends are drawn as pilcrows (default off).</summary>
    public bool EditorShowEndOfLine { get => editorShowEndOfLine; set => Set(ref editorShowEndOfLine, value); }

    /// <summary>Whether other control characters are drawn as boxes (default off).</summary>
    public bool EditorShowControlCharacters { get => editorShowControlCharacters; set => Set(ref editorShowControlCharacters, value); }

    /// <summary>Whether the editor may scroll past the last line (default on).</summary>
    public bool EditorScrollBelowDocument { get => editorScrollBelowDocument; set => Set(ref editorScrollBelowDocument, value); }

    /// <summary>Whether the caret's line is highlighted (default on).</summary>
    public bool EditorHighlightCurrentLine { get => editorHighlightCurrentLine; set => Set(ref editorHighlightCurrentLine, value); }

    /// <summary>Whether http/https/mailto text is clickable with Ctrl/Cmd (default on).</summary>
    public bool EditorHyperlinks { get => editorHyperlinks; set => Set(ref editorHyperlinks, value); }

    /// <summary>Whether foldable ranges are computed while editing (default on).</summary>
    public bool EditorAutoFolding { get => editorAutoFolding; set => Set(ref editorAutoFolding, value); }

    /// <summary>Indentation width in columns; clamped to [<see cref="MinEditorIndentationSize"/>, <see cref="MaxEditorIndentationSize"/>].</summary>
    public int EditorIndentationSize
    {
        get => editorIndentationSize;
        set => Set(ref editorIndentationSize, Math.Clamp(value, MinEditorIndentationSize, MaxEditorIndentationSize));
    }

    /// <summary>Whether Tab inserts spaces instead of a tab character (default on).</summary>
    public bool EditorTabsToSpaces { get => editorTabsToSpaces; set => Set(ref editorTabsToSpaces, value); }

    /// <summary>
    /// Comma-separated column numbers that draw a ruler; empty (default) draws none. Stored as typed (only trimmed): a
    /// settings field is edited one character at a time, and rewriting the list under the caret fights the user. The
    /// editor parses it with <see cref="ParseRulers"/>.
    /// </summary>
    public string EditorColumnRulers { get => editorColumnRulers; set => Set(ref editorColumnRulers, (value ?? string.Empty).Trim()); }

    /// <summary>TextMate theme token: <c>auto</c> (default), <c>light</c> or <c>dark</c>; unknown tokens mean the default.</summary>
    public string EditorTheme { get => editorTheme; set => Set(ref editorTheme, ThemeToken(value)); }

    /// <summary>
    /// Names of the built-in tools the user switched off (sorted, deduplicated). Read before every turn of every session, so a
    /// switch applies from the next turn; extension and MCP tools are governed by their own approval and enable state.
    /// </summary>
    public IReadOnlyList<string> ToolsDisabled
    {
        get => toolsDisabled;
        set
        {
            var normalized = NormalizeNames(value);
            if (!toolsDisabled.SequenceEqual(normalized, StringComparer.Ordinal))
            {
                toolsDisabled = normalized;
                OnPropertyChanged();
            }
        }
    }

    /// <summary>Whether the named tool is switched on (absent from <see cref="ToolsDisabled"/>).</summary>
    /// <summary>Longest edge of a tool row's thumbnail, in device-independent pixels; clamped to [<see cref="MinToolImageThumbnailEdge"/>, <see cref="MaxToolImageThumbnailEdge"/>].</summary>
    public int ToolImageThumbnailEdge
    {
        get => toolImageThumbnailEdge;
        set => Set(ref toolImageThumbnailEdge, Math.Clamp(value, MinToolImageThumbnailEdge, MaxToolImageThumbnailEdge));
    }

    /// <summary>Whether a tool result's images reach the model (default on); read when each request is built.</summary>
    public bool SendToolImagesToModel { get => sendToolImagesToModel; set => Set(ref sendToolImagesToModel, value); }

    /// <summary>Rows a CSV/TSV preview parses; clamped to [<see cref="MinFilePreviewRows"/>, <see cref="MaxFilePreviewRows"/>].</summary>
    public int FilePreviewRows
    {
        get => filePreviewRows;
        set => Set(ref filePreviewRows, Math.Clamp(value, MinFilePreviewRows, MaxFilePreviewRows));
    }

    /// <summary>Characters kept from one <c>.ipynb</c> output; clamped to [<see cref="MinNotebookOutputChars"/>, <see cref="MaxNotebookOutputChars"/>].</summary>
    public int NotebookOutputChars
    {
        get => notebookOutputChars;
        set => Set(ref notebookOutputChars, Math.Clamp(value, MinNotebookOutputChars, MaxNotebookOutputChars));
    }

    /// <summary>Largest image the image preview opens, in bytes; clamped to [<see cref="MinPreviewImageBytes"/>, <see cref="MaxPreviewImageBytes"/>].</summary>
    public int PreviewImageBytes
    {
        get => previewImageBytes;
        set => Set(ref previewImageBytes, Math.Clamp(value, MinPreviewImageBytes, MaxPreviewImageBytes));
    }

    public bool IsToolEnabled(string name) => !toolsDisabled.Contains(name, StringComparer.Ordinal);

    /// <summary>Switches one tool on or off; raises PropertyChanged for <see cref="ToolsDisabled"/> only when it moved.</summary>
    public void SetToolEnabled(string name, bool enabled)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ToolsDisabled = enabled ? [.. toolsDisabled.Where(n => !string.Equals(n, name, StringComparison.Ordinal))] : [.. toolsDisabled, name];
    }

    /// <summary>
    /// Applies the section read from <c>settings.json</c>. Never raises PropertyChanged: the load runs on a pool thread
    /// before the first window is bound, and the composition root pushes <see cref="Language"/> into <c>Loc</c> itself.
    /// Values go through the same validation as the setters.
    /// </summary>
    internal void ApplyLoaded(PreferencesSection section, UiLanguage loadedLanguage)
    {
        uiFontFamily = Family(section.UiFontFamily, DefaultUiFontFamily);
        uiFontSize = Size(section.UiFontSize, DefaultUiFontSize);
        codeFontFamily = Family(section.CodeFontFamily, DefaultCodeFontFamily);
        codeFontSize = Size(section.CodeFontSize, DefaultCodeFontSize);
        language = loadedLanguage;
        terminalShell = (section.TerminalShell ?? string.Empty).Trim();
        terminalArguments = section.TerminalArguments ?? string.Empty;
        terminalScrollbackLines = section.TerminalScrollbackLines is { } scrollback
            ? Math.Clamp(scrollback, MinScrollbackLines, MaxScrollbackLines)
            : DefaultScrollbackLines;
        gitPullStrategy = PullStrategy(section.GitPullStrategy);
        gitNetworkTimeoutSeconds = section.GitNetworkTimeoutSeconds is { } seconds
            ? Math.Clamp(seconds, MinGitNetworkTimeoutSeconds, MaxGitNetworkTimeoutSeconds)
            : DefaultGitNetworkTimeoutSeconds;
        gitStepCommits = section.GitStepCommits ?? DefaultGitStepCommits;
        gitChecksPollSeconds = section.GitChecksPollSeconds is { } pollSeconds
            ? Math.Clamp(pollSeconds, MinGitChecksPollSeconds, MaxGitChecksPollSeconds)
            : DefaultGitChecksPollSeconds;
        gitChecksMaxWaitMinutes = section.GitChecksMaxWaitMinutes is { } waitMinutes
            ? Math.Clamp(waitMinutes, MinGitChecksMaxWaitMinutes, MaxGitChecksMaxWaitMinutes)
            : DefaultGitChecksMaxWaitMinutes;
        notifications = section.Notifications ?? section.LegacyGitNotifications ?? DefaultNotifications;
        gitGhPath = (section.GitGhPath ?? string.Empty).Trim();
        toolsShellPath = (section.ToolsShellPath ?? string.Empty).Trim();
        toolsRipgrepPath = (section.ToolsRipgrepPath ?? string.Empty).Trim();
        toolsBashTimeoutSeconds = section.ToolsBashTimeoutSeconds is { } bashTimeout
            ? Math.Clamp(bashTimeout, MinToolBashTimeoutSeconds, MaxToolBashTimeoutSeconds)
            : DefaultToolBashTimeoutSeconds;
        toolsDisabled = NormalizeNames(section.ToolsDisabled);
        editorShowLineNumbers = section.EditorShowLineNumbers ?? true;
        editorWordWrap = section.EditorWordWrap ?? false;
        editorShowSpaces = section.EditorShowSpaces ?? false;
        editorShowTabs = section.EditorShowTabs ?? false;
        editorShowEndOfLine = section.EditorShowEndOfLine ?? false;
        editorShowControlCharacters = section.EditorShowControlCharacters ?? false;
        editorScrollBelowDocument = section.EditorScrollBelowDocument ?? true;
        editorHighlightCurrentLine = section.EditorHighlightCurrentLine ?? true;
        editorHyperlinks = section.EditorHyperlinks ?? true;
        editorAutoFolding = section.EditorAutoFolding ?? true;
        editorIndentationSize = section.EditorIndentationSize is { } indentationSize
            ? Math.Clamp(indentationSize, MinEditorIndentationSize, MaxEditorIndentationSize)
            : DefaultEditorIndentationSize;
        editorTabsToSpaces = section.EditorTabsToSpaces ?? true;
        editorColumnRulers = (section.EditorColumnRulers ?? string.Empty).Trim();
        editorTheme = ThemeToken(section.EditorTheme);
        toolImageThumbnailEdge = section.ToolImageThumbnailEdge is { } thumbnailEdge
            ? Math.Clamp(thumbnailEdge, MinToolImageThumbnailEdge, MaxToolImageThumbnailEdge)
            : DefaultToolImageThumbnailEdge;
        sendToolImagesToModel = section.SendToolImagesToModel ?? DefaultSendToolImagesToModel;
        filePreviewRows = section.FilePreviewRows is { } previewRows
            ? Math.Clamp(previewRows, MinFilePreviewRows, MaxFilePreviewRows)
            : DefaultFilePreviewRows;
        notebookOutputChars = section.NotebookOutputChars is { } outputChars
            ? Math.Clamp(outputChars, MinNotebookOutputChars, MaxNotebookOutputChars)
            : DefaultNotebookOutputChars;
        previewImageBytes = section.PreviewImageBytes is { } imageBytes
            ? Math.Clamp(imageBytes, MinPreviewImageBytes, MaxPreviewImageBytes)
            : DefaultPreviewImageBytes;
    }

    /// <summary>The section written back to <c>settings.json</c>; the stored text is the live, validated value.</summary>
    internal PreferencesSection ToSection() =>
        new()
        {
            UiFontFamily = uiFontFamily,
            UiFontSize = uiFontSize,
            CodeFontFamily = codeFontFamily,
            CodeFontSize = codeFontSize,
            Language = LanguageToken(language),
            TerminalShell = terminalShell,
            TerminalArguments = terminalArguments,
            TerminalScrollbackLines = terminalScrollbackLines,
            GitPullStrategy = gitPullStrategy,
            GitNetworkTimeoutSeconds = gitNetworkTimeoutSeconds,
            GitStepCommits = gitStepCommits,
            GitChecksPollSeconds = gitChecksPollSeconds,
            GitChecksMaxWaitMinutes = gitChecksMaxWaitMinutes,
            Notifications = notifications,
            GitGhPath = gitGhPath,
            ToolsShellPath = toolsShellPath,
            ToolsRipgrepPath = toolsRipgrepPath,
            ToolsBashTimeoutSeconds = toolsBashTimeoutSeconds,
            ToolsDisabled = toolsDisabled.Count == 0 ? null : [.. toolsDisabled],
            EditorShowLineNumbers = editorShowLineNumbers,
            EditorWordWrap = editorWordWrap,
            EditorShowSpaces = editorShowSpaces,
            EditorShowTabs = editorShowTabs,
            EditorShowEndOfLine = editorShowEndOfLine,
            EditorShowControlCharacters = editorShowControlCharacters,
            EditorScrollBelowDocument = editorScrollBelowDocument,
            EditorHighlightCurrentLine = editorHighlightCurrentLine,
            EditorHyperlinks = editorHyperlinks,
            EditorAutoFolding = editorAutoFolding,
            EditorIndentationSize = editorIndentationSize,
            EditorTabsToSpaces = editorTabsToSpaces,
            EditorColumnRulers = editorColumnRulers,
            EditorTheme = editorTheme,
            ToolImageThumbnailEdge = toolImageThumbnailEdge,
            SendToolImagesToModel = sendToolImagesToModel,
            FilePreviewRows = filePreviewRows,
            NotebookOutputChars = notebookOutputChars,
            PreviewImageBytes = previewImageBytes,
        };

    /// <summary>Reads a <c>language</c> token; an empty value means <see cref="UiLanguage.System"/>.</summary>
    internal static bool TryParseLanguage(string? text, out UiLanguage parsed)
    {
        parsed = UiLanguage.System;
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        switch (text.Trim().ToLowerInvariant())
        {
            case "system":
                return true;
            case "chinese" or "zh" or "zh-cn" or "zh-hans":
                parsed = UiLanguage.Chinese;
                return true;
            case "english" or "en" or "en-us":
                parsed = UiLanguage.English;
                return true;
            default:
                return false;
        }
    }

    private static string LanguageToken(UiLanguage language) => language switch
    {
        UiLanguage.Chinese => "chinese",
        UiLanguage.English => "english",
        _ => "system",
    };

    /// <summary>The valid ruler columns of <paramref name="value"/>, ascending and deduplicated; the editor feeds them to the view.</summary>
    internal static IReadOnlyList<int> ParseRulers(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        var columns = new List<int>(4);
        foreach (var part in value.Split([',', ';', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (!int.TryParse(part, out var column) || column < 1 || column > MaxEditorColumnRuler || columns.Contains(column))
            {
                continue;
            }

            columns.Add(column);
        }

        columns.Sort();
        return columns;
    }

    /// <summary>Reads a theme token; a missing or unknown value means “follow the UI”.</summary>
    internal static string ThemeToken(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            EditorThemeLight => EditorThemeLight,
            EditorThemeDark => EditorThemeDark,
            _ => EditorThemeAuto,
        };

    private static string[] NormalizeNames(IEnumerable<string>? names) =>
        names is null
            ? []
            : [.. names.Select(static n => n?.Trim() ?? string.Empty)
                .Where(static n => n.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)];

    private static string Family(string? value, string fallback)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? fallback : trimmed;
    }

    private static double Size(double? value, double fallback) =>
        value is { } size && !double.IsNaN(size) ? Math.Clamp(size, MinFontSize, MaxFontSize) : fallback;

    /// <summary>Reads a <c>gitPullStrategy</c> token; a missing or unknown value is the default, never an error.</summary>
    private static string PullStrategy(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            GitPullStrategyRebase => GitPullStrategyRebase,
            GitPullStrategyMerge => GitPullStrategyMerge,
            _ => DefaultGitPullStrategy,
        };
}

/// <summary>
/// The <c>preferences</c> section of <c>settings.json</c>: every property optional, so a hand-written or older file still
/// loads and a value that is absent keeps its default. The language is stored as a token, not as a number.
/// </summary>
internal sealed class PreferencesSection
{
    [JsonPropertyName("uiFontFamily")]
    public string? UiFontFamily { get; set; }

    [JsonPropertyName("uiFontSize")]
    public double? UiFontSize { get; set; }

    [JsonPropertyName("codeFontFamily")]
    public string? CodeFontFamily { get; set; }

    [JsonPropertyName("codeFontSize")]
    public double? CodeFontSize { get; set; }

    [JsonPropertyName("language")]
    public string? Language { get; set; }

    [JsonPropertyName("terminalShell")]
    public string? TerminalShell { get; set; }

    [JsonPropertyName("terminalArguments")]
    public string? TerminalArguments { get; set; }

    [JsonPropertyName("terminalScrollbackLines")]
    public int? TerminalScrollbackLines { get; set; }
    [JsonPropertyName("gitPullStrategy")]
    public string? GitPullStrategy { get; set; }

    [JsonPropertyName("gitNetworkTimeoutSeconds")]
    public int? GitNetworkTimeoutSeconds { get; set; }

    [JsonPropertyName("gitStepCommits")]
    public bool? GitStepCommits { get; set; }

    [JsonPropertyName("gitChecksPollSeconds")]
    public int? GitChecksPollSeconds { get; set; }

    [JsonPropertyName("gitChecksMaxWaitMinutes")]
    public int? GitChecksMaxWaitMinutes { get; set; }

    [JsonPropertyName("notifications")]
    public bool? Notifications { get; set; }

    /// <summary>
    /// The key this one switch used when it only covered the end of a checks wait. Still read so an older settings file does
    /// not lose the choice; never written back.
    /// </summary>
    [JsonPropertyName("gitNotifications")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? LegacyGitNotifications { get; set; }

    [JsonPropertyName("gitGhPath")]
    public string? GitGhPath { get; set; }

    [JsonPropertyName("toolsShellPath")]
    public string? ToolsShellPath { get; set; }

    [JsonPropertyName("toolsRipgrepPath")]
    public string? ToolsRipgrepPath { get; set; }

    [JsonPropertyName("toolsBashTimeoutSeconds")]
    public int? ToolsBashTimeoutSeconds { get; set; }

    /// <summary>Names of the built-in tools switched off; absent when every tool is on.</summary>
    [JsonPropertyName("toolsDisabled")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string[]? ToolsDisabled { get; set; }

    [JsonPropertyName("editorShowLineNumbers")]
    public bool? EditorShowLineNumbers { get; set; }

    [JsonPropertyName("editorWordWrap")]
    public bool? EditorWordWrap { get; set; }

    [JsonPropertyName("editorShowSpaces")]
    public bool? EditorShowSpaces { get; set; }

    [JsonPropertyName("editorShowTabs")]
    public bool? EditorShowTabs { get; set; }

    [JsonPropertyName("editorShowEndOfLine")]
    public bool? EditorShowEndOfLine { get; set; }

    [JsonPropertyName("editorShowControlCharacters")]
    public bool? EditorShowControlCharacters { get; set; }

    [JsonPropertyName("editorScrollBelowDocument")]
    public bool? EditorScrollBelowDocument { get; set; }

    [JsonPropertyName("editorHighlightCurrentLine")]
    public bool? EditorHighlightCurrentLine { get; set; }

    [JsonPropertyName("editorHyperlinks")]
    public bool? EditorHyperlinks { get; set; }

    [JsonPropertyName("editorAutoFolding")]
    public bool? EditorAutoFolding { get; set; }

    [JsonPropertyName("editorIndentationSize")]
    public int? EditorIndentationSize { get; set; }

    [JsonPropertyName("editorTabsToSpaces")]
    public bool? EditorTabsToSpaces { get; set; }

    [JsonPropertyName("editorColumnRulers")]
    public string? EditorColumnRulers { get; set; }

    [JsonPropertyName("editorTheme")]
    public string? EditorTheme { get; set; }

    [JsonPropertyName("toolImageThumbnailEdge")]
    public int? ToolImageThumbnailEdge { get; set; }

    [JsonPropertyName("sendToolImagesToModel")]
    public bool? SendToolImagesToModel { get; set; }

    [JsonPropertyName("filePreviewRows")]
    public int? FilePreviewRows { get; set; }

    [JsonPropertyName("notebookOutputChars")]
    public int? NotebookOutputChars { get; set; }

    [JsonPropertyName("previewImageBytes")]
    public int? PreviewImageBytes { get; set; }
}

/// <summary>The <c>preferences</c> section, serialized through this source-generated context (no reflection, AOT safe).</summary>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(PreferencesSection))]
internal sealed partial class PreferencesJsonContext : JsonSerializerContext
{
}

/// <summary>
/// Loads and saves <see cref="AppPreferences"/> as the <c>preferences</c> section of <c>settings.json</c>, preserving every
/// other section byte-for-byte in meaning (the provider section is read by <c>ProviderStore</c>). Reads and writes
/// run on a background thread; a corrupt or missing section yields defaults and is reported through <see cref="LoadProblem"/>.
/// </summary>
internal sealed class PreferencesStore : IAsyncDisposable
{
    /// <summary>How long a burst of edits waits before it becomes one file write.</summary>
    private const int DebounceMilliseconds = 500;

    private const string SectionName = "preferences";
    private const string BackupSuffix = ".bak";

    /// <summary>Whole-document output: other sections keep their values, only their whitespace is normalized.</summary>
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    private readonly string settingsPath;
    private readonly Lock gate = new();
    private readonly AppPreferences current = new();
    private CancellationTokenSource? debounce;
    private byte[]? originalBytes;
    private LocText loadProblem;
    private bool protectOriginal;
    private bool dirty;
    private bool disposed;
    private int version;
    private int saveCount;

    public PreferencesStore(string settingsPath)
    {
        this.settingsPath = Path.GetFullPath(settingsPath);
        BackupPath = this.settingsPath + BackupSuffix;
        current.PropertyChanged += OnCurrentChanged;
    }

    public AppPreferences Current => current;

    /// <summary>Null when the last load succeeded or the file did not exist. Resolved on read, so the row in the
    /// settings page follows a language switch.</summary>
    public string? LoadProblem
    {
        get
        {
            lock (gate)
            {
                return loadProblem.IsEmpty ? null : loadProblem.Resolve();
            }
        }
    }

    /// <summary>Where an unreadable <c>settings.json</c> is preserved before the first write replaces it.</summary>
    internal string BackupPath { get; }

    /// <summary>Completed file writes; the debounce test pins that a burst of edits still lands as one write.</summary>
    internal int SaveCount
    {
        get
        {
            lock (gate)
            {
                return saveCount;
            }
        }
    }

    public async Task LoadAsync(CancellationToken ct)
    {
        var outcome = await Task.Run(() => Read(ct), ct).ConfigureAwait(false);

        lock (gate)
        {
            loadProblem = outcome.Problem;
            protectOriginal = !outcome.Problem.IsEmpty;
            originalBytes = outcome.Protect ? outcome.Raw : null;
            dirty = false;
        }

        current.ApplyLoaded(outcome.Section ?? new PreferencesSection(), outcome.Language ?? UiLanguage.System);
    }

    /// <summary>Writes immediately (atomic replace via Forge.IO); normally called by the debounce, and once on dispose.</summary>
    public async Task SaveAsync(CancellationToken ct)
    {
        CancellationTokenSource? pending;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            pending = debounce;
            debounce = null;
        }

        Cancel(pending);
        await Task.Run(() => SaveCore(ct), ct).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        CancellationTokenSource? pending;
        bool flush;
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            pending = debounce;
            debounce = null;
            flush = dirty;
        }

        Cancel(pending);

        if (!flush)
        {
            return;
        }

        try
        {
            await Task.Run(() => SaveCore(CancellationToken.None), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsFileFailure(exception))
        {
            AppLogs.Preferences.ZLogError(exception, $"the final save failed");
        }
    }

    /// <summary>
    /// Any preference change is a user edit: the load path writes the fields without notifying, so a notification can only
    /// come from a setter. Each edit restarts the 500 ms window; the write itself runs on the thread pool.
    /// </summary>
    private void OnCurrentChanged(object? sender, PropertyChangedEventArgs e)
    {
        CancellationTokenSource cts;
        CancellationTokenSource? previous;
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            version++;
            dirty = true;
            previous = debounce;
            cts = new CancellationTokenSource();
            debounce = cts;
        }

        Cancel(previous);
        _ = DebounceAsync(cts.Token);
    }

    private async Task DebounceAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(DebounceMilliseconds, ct).ConfigureAwait(false);
            SaveCore(ct);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception) when (IsFileFailure(exception))
        {
            AppLogs.Preferences.ZLogError(exception, $"save failed");
        }
    }

    /// <summary>The one write path: keep every other section, back up an unreadable file once, then replace atomically.</summary>
    private void SaveCore(CancellationToken ct)
    {
        // The provider store rewrites the same file: both merges have to see each other's writes.
        using var lease = Composition.SettingsFileMutex.Acquire(settingsPath);

        var section = current.ToSection();
        int snapshot;
        bool shouldBackUp;
        byte[]? retained;
        lock (gate)
        {
            snapshot = version;
            shouldBackUp = protectOriginal;
            retained = originalBytes;
        }

        var existing = ReadExistingBytes(ct);
        var document = existing is { Length: > 0 } ? TryParseDocument(existing) : null;

        // A file that turned invalid after the load is still the user's content: back it up before it is replaced.
        var unreadable = existing is { Length: > 0 } && document is null;
        if (shouldBackUp || unreadable)
        {
            var source = existing is { Length: > 0 } ? existing : retained;
            if (source is { Length: > 0 })
            {
                AtomicFile.WriteAllBytes(BackupPath, source, null, ct);
            }
        }

        document ??= new JsonObject();
        document[SectionName] = JsonSerializer.SerializeToNode(section, PreferencesJsonContext.Default.PreferencesSection);

        AtomicFile.WriteAllBytes(settingsPath, Encoding.UTF8.GetBytes(document.ToJsonString(WriteOptions) + "\n"), null, ct);

        lock (gate)
        {
            if (snapshot == version)
            {
                dirty = false;
            }

            protectOriginal = false;
            originalBytes = null;
            saveCount++;
        }
    }

    private LoadOutcome Read(CancellationToken ct)
    {
        byte[]? bytes;
        try
        {
            bytes = AtomicFile.ReadAllBytesShared(settingsPath, null, ct);
        }
        catch (Exception exception) when (IsFileFailure(exception))
        {
            return new LoadOutcome(null, null, ShellStrings.PreferencesReadFailed(exception.Message), null, false);
        }

        if (bytes is null || bytes.Length == 0)
        {
            return new LoadOutcome(null, null, default, null, false);
        }

        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(bytes);
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or InvalidOperationException)
        {
            return new LoadOutcome(null, null, ShellStrings.PreferencesInvalidJson(exception.Message), bytes, true);
        }

        if (parsed is not JsonObject document)
        {
            return new LoadOutcome(null, null, ShellStrings.PreferencesNotObject(), bytes, true);
        }

        if (document[SectionName] is not { } node)
        {
            return new LoadOutcome(null, null, default, null, false);
        }

        if (node is not JsonObject)
        {
            return new LoadOutcome(null, null, ShellStrings.PreferencesSectionNotObject(), bytes, true);
        }

        PreferencesSection? section;
        try
        {
            section = node.Deserialize(PreferencesJsonContext.Default.PreferencesSection);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or NotSupportedException)
        {
            return new LoadOutcome(null, null, ShellStrings.PreferencesSectionInvalid(exception.Message), bytes, true);
        }

        if (section is null)
        {
            return new LoadOutcome(null, null, default, null, false);
        }

        if (!AppPreferences.TryParseLanguage(section.Language, out var language))
        {
            return new LoadOutcome(section, UiLanguage.System, ShellStrings.PreferencesLanguageUnknown(section.Language), bytes, true);
        }

        return new LoadOutcome(section, language, default, null, false);
    }

    /// <summary>Re-reads the whole file so the write is a merge, not an overwrite; null when it is missing.</summary>
    private byte[]? ReadExistingBytes(CancellationToken ct)
    {
        try
        {
            return AtomicFile.ReadAllBytesShared(settingsPath, null, ct);
        }
        catch (Exception exception) when (IsFileFailure(exception))
        {
            AppLogs.Preferences.ZLogWarning($"the existing settings.json could not be read: {exception.Message}");
            return null;
        }
    }

    private static JsonObject? TryParseDocument(byte[] bytes)
    {
        try
        {
            return JsonNode.Parse(bytes) as JsonObject;
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    private static void Cancel(CancellationTokenSource? cancellation)
    {
        if (cancellation is null)
        {
            return;
        }

        cancellation.Cancel();
        cancellation.Dispose();
    }

    private static bool IsFileFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException or NotSupportedException;

    /// <summary>What one read of <c>settings.json</c> produced: values, the resolved language, and whether the file must survive.</summary>
    private readonly record struct LoadOutcome(
        PreferencesSection? Section,
        UiLanguage? Language,
        LocText Problem,
        byte[]? Raw,
        bool Protect);
}
