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

    // Settings the thinkrail-gap waves read (registered in docs/research/2026-11-13-thinkrail-gap-plan.md, section 6.2).
    // They all live here so no wave edits this file again; each wave only reads them and adds its settings page.
    public const int MinAgentsMaxParallelSubAgents = 1;
    public const int MaxAgentsMaxParallelSubAgents = 16;
    public const int DefaultAgentsMaxParallelSubAgents = 4;
    public const bool DefaultAgentsSubAgentsEnabled = true;
    public const bool DefaultIdleArchiveEnabled = true;
    public const int MinIdleArchiveDays = 1;
    public const int MaxIdleArchiveDays = 365;
    public const int DefaultIdleArchiveDays = 7;
    public const bool DefaultNotificationPermissionReprompt = true;
    public const int MinPalettePerSourceLimit = 10;
    public const int MaxPalettePerSourceLimit = 500;
    public const int DefaultPalettePerSourceLimit = 50;
    public const int MinChatToolImageThumbnailPixels = 64;
    public const int MaxChatToolImageThumbnailPixels = 1024;
    public const int DefaultChatToolImageThumbnailPixels = 256;
    public const bool DefaultChatToolImagesToModel = true;
    public const int MinFilesCsvMaxRows = 100;
    public const int MaxFilesCsvMaxRows = 100000;
    public const int DefaultFilesCsvMaxRows = 1000;
    public const int MinFilesNotebookOutputKilobytes = 1;
    public const int MaxFilesNotebookOutputKilobytes = 1024;
    public const int DefaultFilesNotebookOutputKilobytes = 20;
    public const int MinFilesImagePreviewMaxMegabytes = 1;
    public const int MaxFilesImagePreviewMaxMegabytes = 256;
    public const int DefaultFilesImagePreviewMaxMegabytes = 20;
    public const int MinGitChecksNoChecksConfirmations = 1;
    public const int MaxGitChecksNoChecksConfirmations = 10;
    public const int DefaultGitChecksNoChecksConfirmations = 3;
    public const int MinGitChecksFailureLogTailLines = 20;
    public const int MaxGitChecksFailureLogTailLines = 2000;
    public const int DefaultGitChecksFailureLogTailLines = 200;
    public const int MinGitReviewDiffMaxChars = 1000;
    public const int MaxGitReviewDiffMaxChars = 500000;
    public const int DefaultGitReviewDiffMaxChars = 48000;
    public const int MinGitReviewMaxCommits = 5;
    public const int MaxGitReviewMaxCommits = 500;
    public const int DefaultGitReviewMaxCommits = 50;
    public const int MinGitReviewMaxFiles = 20;
    public const int MaxGitReviewMaxFiles = 2000;
    public const int DefaultGitReviewMaxFiles = 200;
    public const int MinGitReviewFieldMaxChars = 200;
    public const int MaxGitReviewFieldMaxChars = 20000;
    public const int DefaultGitReviewFieldMaxChars = 2000;
    public const int MinMcpRequestTimeoutSeconds = 5;
    public const int MaxMcpRequestTimeoutSeconds = 600;
    public const int DefaultMcpRequestTimeoutSeconds = 60;
    public const int MinMcpReconnectInitialDelaySeconds = 1;
    public const int MaxMcpReconnectInitialDelaySeconds = 60;
    public const int DefaultMcpReconnectInitialDelaySeconds = 1;
    public const int MinMcpToolSearchLimit = 0;
    public const int MaxMcpToolSearchLimit = 1000;
    public const int DefaultMcpToolSearchLimit = 0;
    public const int MinMcpStderrTailLines = 20;
    public const int MaxMcpStderrTailLines = 2000;
    public const int DefaultMcpStderrTailLines = 200;
    public const int MinInspectorLogTailLines = 20;
    public const int MaxInspectorLogTailLines = 1000;
    public const int DefaultInspectorLogTailLines = 200;
    public const int MinInspectorRefreshMilliseconds = 250;
    public const int MaxInspectorRefreshMilliseconds = 10000;
    public const int DefaultInspectorRefreshMilliseconds = 1000;
    public const int MinChatMermaidTimeoutMilliseconds = 500;
    public const int MaxChatMermaidTimeoutMilliseconds = 30000;
    public const int DefaultChatMermaidTimeoutMilliseconds = 2000;
    public const int MinProjectsCloneTimeoutSeconds = 0;
    public const int MaxProjectsCloneTimeoutSeconds = 3600;
    public const int DefaultProjectsCloneTimeoutSeconds = 0;
    public const string DefaultIdleArchiveScope = "automation";
    private static readonly string[] IdleArchiveScopeTokens = ["automation", "all"];
    public const string DefaultUpdatesCheckFrequency = "daily";
    private static readonly string[] UpdatesCheckFrequencyTokens = ["daily", "weekly", "never"];
    public const string DefaultUpdatesChannel = "stable";
    private static readonly string[] UpdatesChannelTokens = ["stable", "beta"];
    public const string DefaultPaletteExcludeDirectories = "";
    public const string DefaultChatMermaidDiagramTypes = "flowchart,sequence,state,class,er";
    public const string DefaultProjectsCloneDirectory = "";

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
    private int agentsMaxParallelSubAgents = DefaultAgentsMaxParallelSubAgents;
    private bool agentsSubAgentsEnabled = DefaultAgentsSubAgentsEnabled;
    private bool idleArchiveEnabled = DefaultIdleArchiveEnabled;
    private int idleArchiveDays = DefaultIdleArchiveDays;
    private bool notificationPermissionReprompt = DefaultNotificationPermissionReprompt;
    private int palettePerSourceLimit = DefaultPalettePerSourceLimit;
    private int chatToolImageThumbnailPixels = DefaultChatToolImageThumbnailPixels;
    private bool chatToolImagesToModel = DefaultChatToolImagesToModel;
    private int filesCsvMaxRows = DefaultFilesCsvMaxRows;
    private int filesNotebookOutputKilobytes = DefaultFilesNotebookOutputKilobytes;
    private int filesImagePreviewMaxMegabytes = DefaultFilesImagePreviewMaxMegabytes;
    private int gitChecksNoChecksConfirmations = DefaultGitChecksNoChecksConfirmations;
    private int gitChecksFailureLogTailLines = DefaultGitChecksFailureLogTailLines;
    private int gitReviewDiffMaxChars = DefaultGitReviewDiffMaxChars;
    private int gitReviewMaxCommits = DefaultGitReviewMaxCommits;
    private int gitReviewMaxFiles = DefaultGitReviewMaxFiles;
    private int gitReviewFieldMaxChars = DefaultGitReviewFieldMaxChars;
    private int mcpRequestTimeoutSeconds = DefaultMcpRequestTimeoutSeconds;
    private int mcpReconnectInitialDelaySeconds = DefaultMcpReconnectInitialDelaySeconds;
    private int mcpToolSearchLimit = DefaultMcpToolSearchLimit;
    private int mcpStderrTailLines = DefaultMcpStderrTailLines;
    private int inspectorLogTailLines = DefaultInspectorLogTailLines;
    private int inspectorRefreshMilliseconds = DefaultInspectorRefreshMilliseconds;
    private int chatMermaidTimeoutMilliseconds = DefaultChatMermaidTimeoutMilliseconds;
    private int projectsCloneTimeoutSeconds = DefaultProjectsCloneTimeoutSeconds;
    private string idleArchiveScope = DefaultIdleArchiveScope;
    private string updatesCheckFrequency = DefaultUpdatesCheckFrequency;
    private string updatesChannel = DefaultUpdatesChannel;
    private string paletteExcludeDirectories = DefaultPaletteExcludeDirectories;
    private string chatMermaidDiagramTypes = DefaultChatMermaidDiagramTypes;
    private string projectsCloneDirectory = DefaultProjectsCloneDirectory;
    private IReadOnlyList<string> modelsHidden = [];

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
    public bool IsToolEnabled(string name) => !toolsDisabled.Contains(name, StringComparer.Ordinal);

    /// <summary>Switches one tool on or off; raises PropertyChanged for <see cref="ToolsDisabled"/> only when it moved.</summary>
    public void SetToolEnabled(string name, bool enabled)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ToolsDisabled = enabled ? [.. toolsDisabled.Where(n => !string.Equals(n, name, StringComparison.Ordinal))] : [.. toolsDisabled, name];
    }

    /// <summary>How many sub-agents one turn may run at the same time (wave W1; replaces the fixed 4 of <c>AgentOptions</c>). Clamped to [<see cref="MinAgentsMaxParallelSubAgents"/>, <see cref="MaxAgentsMaxParallelSubAgents"/>].</summary>
    public int AgentsMaxParallelSubAgents
    {
        get => agentsMaxParallelSubAgents;
        set => Set(ref agentsMaxParallelSubAgents, Math.Clamp(value, MinAgentsMaxParallelSubAgents, MaxAgentsMaxParallelSubAgents));
    }

    /// <summary>Whether the <c>spawn_agent</c> tool is offered at all (wave W1).</summary>
    public bool AgentsSubAgentsEnabled { get => agentsSubAgentsEnabled; set => Set(ref agentsSubAgentsEnabled, value); }

    /// <summary>Whether idle workspaces are archived automatically (wave W1).</summary>
    public bool IdleArchiveEnabled { get => idleArchiveEnabled; set => Set(ref idleArchiveEnabled, value); }

    /// <summary>Days without activity before an idle workspace is archived (wave W1; replaces the fixed 7 of <c>Housekeeping</c>). Clamped to [<see cref="MinIdleArchiveDays"/>, <see cref="MaxIdleArchiveDays"/>].</summary>
    public int IdleArchiveDays
    {
        get => idleArchiveDays;
        set => Set(ref idleArchiveDays, Math.Clamp(value, MinIdleArchiveDays, MaxIdleArchiveDays));
    }

    /// <summary>Whether the app keeps offering to fix a blocked notification permission (wave W1).</summary>
    public bool NotificationPermissionReprompt { get => notificationPermissionReprompt; set => Set(ref notificationPermissionReprompt, value); }

    /// <summary>Most results one palette source returns (wave W3). Clamped to [<see cref="MinPalettePerSourceLimit"/>, <see cref="MaxPalettePerSourceLimit"/>].</summary>
    public int PalettePerSourceLimit
    {
        get => palettePerSourceLimit;
        set => Set(ref palettePerSourceLimit, Math.Clamp(value, MinPalettePerSourceLimit, MaxPalettePerSourceLimit));
    }

    /// <summary>Longest edge of a tool-result image thumbnail, in pixels (wave W4). Clamped to [<see cref="MinChatToolImageThumbnailPixels"/>, <see cref="MaxChatToolImageThumbnailPixels"/>].</summary>
    public int ChatToolImageThumbnailPixels
    {
        get => chatToolImageThumbnailPixels;
        set => Set(ref chatToolImageThumbnailPixels, Math.Clamp(value, MinChatToolImageThumbnailPixels, MaxChatToolImageThumbnailPixels));
    }

    /// <summary>Whether images a tool returned are sent back to the model (wave W4).</summary>
    public bool ChatToolImagesToModel { get => chatToolImagesToModel; set => Set(ref chatToolImagesToModel, value); }

    /// <summary>Most rows the CSV/TSV preview shows (wave W4). Clamped to [<see cref="MinFilesCsvMaxRows"/>, <see cref="MaxFilesCsvMaxRows"/>].</summary>
    public int FilesCsvMaxRows
    {
        get => filesCsvMaxRows;
        set => Set(ref filesCsvMaxRows, Math.Clamp(value, MinFilesCsvMaxRows, MaxFilesCsvMaxRows));
    }

    /// <summary>Most kilobytes of one notebook cell output the preview shows (wave W4). Clamped to [<see cref="MinFilesNotebookOutputKilobytes"/>, <see cref="MaxFilesNotebookOutputKilobytes"/>].</summary>
    public int FilesNotebookOutputKilobytes
    {
        get => filesNotebookOutputKilobytes;
        set => Set(ref filesNotebookOutputKilobytes, Math.Clamp(value, MinFilesNotebookOutputKilobytes, MaxFilesNotebookOutputKilobytes));
    }

    /// <summary>Largest image the file preview loads, in megabytes (wave W4). Clamped to [<see cref="MinFilesImagePreviewMaxMegabytes"/>, <see cref="MaxFilesImagePreviewMaxMegabytes"/>].</summary>
    public int FilesImagePreviewMaxMegabytes
    {
        get => filesImagePreviewMaxMegabytes;
        set => Set(ref filesImagePreviewMaxMegabytes, Math.Clamp(value, MinFilesImagePreviewMaxMegabytes, MaxFilesImagePreviewMaxMegabytes));
    }

    /// <summary>Polls in a row that must find no checks before wait mode concludes there are none (wave W5). Clamped to [<see cref="MinGitChecksNoChecksConfirmations"/>, <see cref="MaxGitChecksNoChecksConfirmations"/>].</summary>
    public int GitChecksNoChecksConfirmations
    {
        get => gitChecksNoChecksConfirmations;
        set => Set(ref gitChecksNoChecksConfirmations, Math.Clamp(value, MinGitChecksNoChecksConfirmations, MaxGitChecksNoChecksConfirmations));
    }

    /// <summary>Lines of a failed check log handed to the agent (wave W5). Clamped to [<see cref="MinGitChecksFailureLogTailLines"/>, <see cref="MaxGitChecksFailureLogTailLines"/>].</summary>
    public int GitChecksFailureLogTailLines
    {
        get => gitChecksFailureLogTailLines;
        set => Set(ref gitChecksFailureLogTailLines, Math.Clamp(value, MinGitChecksFailureLogTailLines, MaxGitChecksFailureLogTailLines));
    }

    /// <summary>Characters of diff a review package carries before it cuts and says so (request_review; read for each review). Clamped to [<see cref="MinGitReviewDiffMaxChars"/>, <see cref="MaxGitReviewDiffMaxChars"/>].</summary>
    public int GitReviewDiffMaxChars
    {
        get => gitReviewDiffMaxChars;
        set => Set(ref gitReviewDiffMaxChars, Math.Clamp(value, MinGitReviewDiffMaxChars, MaxGitReviewDiffMaxChars));
    }

    /// <summary>Commit lines a review package lists (request_review; read for each review). Clamped to [<see cref="MinGitReviewMaxCommits"/>, <see cref="MaxGitReviewMaxCommits"/>].</summary>
    public int GitReviewMaxCommits
    {
        get => gitReviewMaxCommits;
        set => Set(ref gitReviewMaxCommits, Math.Clamp(value, MinGitReviewMaxCommits, MaxGitReviewMaxCommits));
    }

    /// <summary>File lines a review package lists (request_review; read for each review). Clamped to [<see cref="MinGitReviewMaxFiles"/>, <see cref="MaxGitReviewMaxFiles"/>].</summary>
    public int GitReviewMaxFiles
    {
        get => gitReviewMaxFiles;
        set => Set(ref gitReviewMaxFiles, Math.Clamp(value, MinGitReviewMaxFiles, MaxGitReviewMaxFiles));
    }

    /// <summary>Characters kept of each intent, step text, focus, commit subject and the narrowed-to path list in a review package (request_review; read for each review). Clamped to [<see cref="MinGitReviewFieldMaxChars"/>, <see cref="MaxGitReviewFieldMaxChars"/>].</summary>
    public int GitReviewFieldMaxChars
    {
        get => gitReviewFieldMaxChars;
        set => Set(ref gitReviewFieldMaxChars, Math.Clamp(value, MinGitReviewFieldMaxChars, MaxGitReviewFieldMaxChars));
    }

    /// <summary>Default MCP request timeout; a server's own <c>timeout</c> still overrides it (wave W6). Clamped to [<see cref="MinMcpRequestTimeoutSeconds"/>, <see cref="MaxMcpRequestTimeoutSeconds"/>].</summary>
    public int McpRequestTimeoutSeconds
    {
        get => mcpRequestTimeoutSeconds;
        set => Set(ref mcpRequestTimeoutSeconds, Math.Clamp(value, MinMcpRequestTimeoutSeconds, MaxMcpRequestTimeoutSeconds));
    }

    /// <summary>First delay before an MCP server is reconnected (wave W6). Clamped to [<see cref="MinMcpReconnectInitialDelaySeconds"/>, <see cref="MaxMcpReconnectInitialDelaySeconds"/>].</summary>
    public int McpReconnectInitialDelaySeconds
    {
        get => mcpReconnectInitialDelaySeconds;
        set => Set(ref mcpReconnectInitialDelaySeconds, Math.Clamp(value, MinMcpReconnectInitialDelaySeconds, MaxMcpReconnectInitialDelaySeconds));
    }

    /// <summary>Most MCP tools offered to the model per turn; 0 means no limit (wave W6). Clamped to [<see cref="MinMcpToolSearchLimit"/>, <see cref="MaxMcpToolSearchLimit"/>].</summary>
    public int McpToolSearchLimit
    {
        get => mcpToolSearchLimit;
        set => Set(ref mcpToolSearchLimit, Math.Clamp(value, MinMcpToolSearchLimit, MaxMcpToolSearchLimit));
    }

    /// <summary>Lines of an MCP server's stderr the settings page shows (wave W6). Clamped to [<see cref="MinMcpStderrTailLines"/>, <see cref="MaxMcpStderrTailLines"/>].</summary>
    public int McpStderrTailLines
    {
        get => mcpStderrTailLines;
        set => Set(ref mcpStderrTailLines, Math.Clamp(value, MinMcpStderrTailLines, MaxMcpStderrTailLines));
    }

    /// <summary>Lines of a running command's output tail the resource inspector shows (wave W8). Clamped to [<see cref="MinInspectorLogTailLines"/>, <see cref="MaxInspectorLogTailLines"/>].</summary>
    public int InspectorLogTailLines
    {
        get => inspectorLogTailLines;
        set => Set(ref inspectorLogTailLines, Math.Clamp(value, MinInspectorLogTailLines, MaxInspectorLogTailLines));
    }

    /// <summary>How often the resource inspector refreshes its elapsed times (wave W8). Clamped to [<see cref="MinInspectorRefreshMilliseconds"/>, <see cref="MaxInspectorRefreshMilliseconds"/>].</summary>
    public int InspectorRefreshMilliseconds
    {
        get => inspectorRefreshMilliseconds;
        set => Set(ref inspectorRefreshMilliseconds, Math.Clamp(value, MinInspectorRefreshMilliseconds, MaxInspectorRefreshMilliseconds));
    }

    /// <summary>Longest one Mermaid diagram may take to render (wave W11). Clamped to [<see cref="MinChatMermaidTimeoutMilliseconds"/>, <see cref="MaxChatMermaidTimeoutMilliseconds"/>].</summary>
    public int ChatMermaidTimeoutMilliseconds
    {
        get => chatMermaidTimeoutMilliseconds;
        set => Set(ref chatMermaidTimeoutMilliseconds, Math.Clamp(value, MinChatMermaidTimeoutMilliseconds, MaxChatMermaidTimeoutMilliseconds));
    }

    /// <summary>Longest a clone may run; 0 means no timeout (wave W9). Clamped to [<see cref="MinProjectsCloneTimeoutSeconds"/>, <see cref="MaxProjectsCloneTimeoutSeconds"/>].</summary>
    public int ProjectsCloneTimeoutSeconds
    {
        get => projectsCloneTimeoutSeconds;
        set => Set(ref projectsCloneTimeoutSeconds, Math.Clamp(value, MinProjectsCloneTimeoutSeconds, MaxProjectsCloneTimeoutSeconds));
    }

    /// <summary>Which workspaces idle archiving covers: <c>automation</c> (sessions an automation created) or <c>all</c> root sessions (wave W1). An unknown token means the default.</summary>
    public string IdleArchiveScope { get => idleArchiveScope; set => Set(ref idleArchiveScope, Token(value, IdleArchiveScopeTokens, DefaultIdleArchiveScope)); }

    /// <summary>How often the app checks for an update (wave W10). An unknown token means the default.</summary>
    public string UpdatesCheckFrequency { get => updatesCheckFrequency; set => Set(ref updatesCheckFrequency, Token(value, UpdatesCheckFrequencyTokens, DefaultUpdatesCheckFrequency)); }

    /// <summary>Which release channel updates come from (wave W10). An unknown token means the default.</summary>
    public string UpdatesChannel { get => updatesChannel; set => Set(ref updatesChannel, Token(value, UpdatesChannelTokens, DefaultUpdatesChannel)); }

    /// <summary>Directories (comma or newline separated) the palette content search skips besides what <c>.gitignore</c> excludes (wave W3).</summary>
    public string PaletteExcludeDirectories { get => paletteExcludeDirectories; set => Set(ref paletteExcludeDirectories, Text(value, DefaultPaletteExcludeDirectories)); }

    /// <summary>Mermaid diagram types that are rendered, comma separated; others stay source text (wave W11).</summary>
    public string ChatMermaidDiagramTypes { get => chatMermaidDiagramTypes; set => Set(ref chatMermaidDiagramTypes, Text(value, DefaultChatMermaidDiagramTypes)); }

    /// <summary>Folder new clones go into; empty means beside the project (wave W9).</summary>
    public string ProjectsCloneDirectory { get => projectsCloneDirectory; set => Set(ref projectsCloneDirectory, Text(value, DefaultProjectsCloneDirectory)); }

    /// <summary>
    /// Model ids every model picker hides (sorted, deduplicated; wave W1). Read through <see cref="IsModelHidden"/> so the
    /// session composer, the agent-defaults combo and the settings section share one definition of "hidden".
    /// </summary>
    public IReadOnlyList<string> ModelsHidden
    {
        get => modelsHidden;
        set
        {
            var normalized = NormalizeNames(value);
            if (!modelsHidden.SequenceEqual(normalized, StringComparer.Ordinal))
            {
                modelsHidden = normalized;
                OnPropertyChanged();
            }
        }
    }

    /// <summary>Whether the named model is hidden from the pickers (present in <see cref="ModelsHidden"/>).</summary>
    public bool IsModelHidden(string name) => modelsHidden.Contains(name, StringComparer.Ordinal);

    /// <summary>Hides or shows one model; raises PropertyChanged for <see cref="ModelsHidden"/> only when it moved.</summary>
    public void SetModelHidden(string name, bool hidden)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ModelsHidden = hidden ? [.. modelsHidden, name] : [.. modelsHidden.Where(n => !string.Equals(n, name, StringComparison.Ordinal))];
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
        agentsMaxParallelSubAgents = section.AgentsMaxParallelSubAgents is { } agentsMaxParallelSubAgentsLoaded
            ? Math.Clamp(agentsMaxParallelSubAgentsLoaded, MinAgentsMaxParallelSubAgents, MaxAgentsMaxParallelSubAgents)
            : DefaultAgentsMaxParallelSubAgents;
        agentsSubAgentsEnabled = section.AgentsSubAgentsEnabled ?? DefaultAgentsSubAgentsEnabled;
        idleArchiveEnabled = section.IdleArchiveEnabled ?? DefaultIdleArchiveEnabled;
        idleArchiveDays = section.IdleArchiveDays is { } idleArchiveDaysLoaded
            ? Math.Clamp(idleArchiveDaysLoaded, MinIdleArchiveDays, MaxIdleArchiveDays)
            : DefaultIdleArchiveDays;
        notificationPermissionReprompt = section.NotificationPermissionReprompt ?? DefaultNotificationPermissionReprompt;
        palettePerSourceLimit = section.PalettePerSourceLimit is { } palettePerSourceLimitLoaded
            ? Math.Clamp(palettePerSourceLimitLoaded, MinPalettePerSourceLimit, MaxPalettePerSourceLimit)
            : DefaultPalettePerSourceLimit;
        chatToolImageThumbnailPixels = section.ChatToolImageThumbnailPixels is { } chatToolImageThumbnailPixelsLoaded
            ? Math.Clamp(chatToolImageThumbnailPixelsLoaded, MinChatToolImageThumbnailPixels, MaxChatToolImageThumbnailPixels)
            : DefaultChatToolImageThumbnailPixels;
        chatToolImagesToModel = section.ChatToolImagesToModel ?? DefaultChatToolImagesToModel;
        filesCsvMaxRows = section.FilesCsvMaxRows is { } filesCsvMaxRowsLoaded
            ? Math.Clamp(filesCsvMaxRowsLoaded, MinFilesCsvMaxRows, MaxFilesCsvMaxRows)
            : DefaultFilesCsvMaxRows;
        filesNotebookOutputKilobytes = section.FilesNotebookOutputKilobytes is { } filesNotebookOutputKilobytesLoaded
            ? Math.Clamp(filesNotebookOutputKilobytesLoaded, MinFilesNotebookOutputKilobytes, MaxFilesNotebookOutputKilobytes)
            : DefaultFilesNotebookOutputKilobytes;
        filesImagePreviewMaxMegabytes = section.FilesImagePreviewMaxMegabytes is { } filesImagePreviewMaxMegabytesLoaded
            ? Math.Clamp(filesImagePreviewMaxMegabytesLoaded, MinFilesImagePreviewMaxMegabytes, MaxFilesImagePreviewMaxMegabytes)
            : DefaultFilesImagePreviewMaxMegabytes;
        gitChecksNoChecksConfirmations = section.GitChecksNoChecksConfirmations is { } gitChecksNoChecksConfirmationsLoaded
            ? Math.Clamp(gitChecksNoChecksConfirmationsLoaded, MinGitChecksNoChecksConfirmations, MaxGitChecksNoChecksConfirmations)
            : DefaultGitChecksNoChecksConfirmations;
        gitChecksFailureLogTailLines = section.GitChecksFailureLogTailLines is { } gitChecksFailureLogTailLinesLoaded
            ? Math.Clamp(gitChecksFailureLogTailLinesLoaded, MinGitChecksFailureLogTailLines, MaxGitChecksFailureLogTailLines)
            : DefaultGitChecksFailureLogTailLines;
        gitReviewDiffMaxChars = section.GitReviewDiffMaxChars is { } gitReviewDiffMaxCharsLoaded
            ? Math.Clamp(gitReviewDiffMaxCharsLoaded, MinGitReviewDiffMaxChars, MaxGitReviewDiffMaxChars)
            : DefaultGitReviewDiffMaxChars;
        gitReviewMaxCommits = section.GitReviewMaxCommits is { } gitReviewMaxCommitsLoaded
            ? Math.Clamp(gitReviewMaxCommitsLoaded, MinGitReviewMaxCommits, MaxGitReviewMaxCommits)
            : DefaultGitReviewMaxCommits;
        gitReviewMaxFiles = section.GitReviewMaxFiles is { } gitReviewMaxFilesLoaded
            ? Math.Clamp(gitReviewMaxFilesLoaded, MinGitReviewMaxFiles, MaxGitReviewMaxFiles)
            : DefaultGitReviewMaxFiles;
        gitReviewFieldMaxChars = section.GitReviewFieldMaxChars is { } gitReviewFieldMaxCharsLoaded
            ? Math.Clamp(gitReviewFieldMaxCharsLoaded, MinGitReviewFieldMaxChars, MaxGitReviewFieldMaxChars)
            : DefaultGitReviewFieldMaxChars;
        mcpRequestTimeoutSeconds = section.McpRequestTimeoutSeconds is { } mcpRequestTimeoutSecondsLoaded
            ? Math.Clamp(mcpRequestTimeoutSecondsLoaded, MinMcpRequestTimeoutSeconds, MaxMcpRequestTimeoutSeconds)
            : DefaultMcpRequestTimeoutSeconds;
        mcpReconnectInitialDelaySeconds = section.McpReconnectInitialDelaySeconds is { } mcpReconnectInitialDelaySecondsLoaded
            ? Math.Clamp(mcpReconnectInitialDelaySecondsLoaded, MinMcpReconnectInitialDelaySeconds, MaxMcpReconnectInitialDelaySeconds)
            : DefaultMcpReconnectInitialDelaySeconds;
        mcpToolSearchLimit = section.McpToolSearchLimit is { } mcpToolSearchLimitLoaded
            ? Math.Clamp(mcpToolSearchLimitLoaded, MinMcpToolSearchLimit, MaxMcpToolSearchLimit)
            : DefaultMcpToolSearchLimit;
        mcpStderrTailLines = section.McpStderrTailLines is { } mcpStderrTailLinesLoaded
            ? Math.Clamp(mcpStderrTailLinesLoaded, MinMcpStderrTailLines, MaxMcpStderrTailLines)
            : DefaultMcpStderrTailLines;
        inspectorLogTailLines = section.InspectorLogTailLines is { } inspectorLogTailLinesLoaded
            ? Math.Clamp(inspectorLogTailLinesLoaded, MinInspectorLogTailLines, MaxInspectorLogTailLines)
            : DefaultInspectorLogTailLines;
        inspectorRefreshMilliseconds = section.InspectorRefreshMilliseconds is { } inspectorRefreshMillisecondsLoaded
            ? Math.Clamp(inspectorRefreshMillisecondsLoaded, MinInspectorRefreshMilliseconds, MaxInspectorRefreshMilliseconds)
            : DefaultInspectorRefreshMilliseconds;
        chatMermaidTimeoutMilliseconds = section.ChatMermaidTimeoutMilliseconds is { } chatMermaidTimeoutMillisecondsLoaded
            ? Math.Clamp(chatMermaidTimeoutMillisecondsLoaded, MinChatMermaidTimeoutMilliseconds, MaxChatMermaidTimeoutMilliseconds)
            : DefaultChatMermaidTimeoutMilliseconds;
        projectsCloneTimeoutSeconds = section.ProjectsCloneTimeoutSeconds is { } projectsCloneTimeoutSecondsLoaded
            ? Math.Clamp(projectsCloneTimeoutSecondsLoaded, MinProjectsCloneTimeoutSeconds, MaxProjectsCloneTimeoutSeconds)
            : DefaultProjectsCloneTimeoutSeconds;
        idleArchiveScope = Token(section.IdleArchiveScope, IdleArchiveScopeTokens, DefaultIdleArchiveScope);
        updatesCheckFrequency = Token(section.UpdatesCheckFrequency, UpdatesCheckFrequencyTokens, DefaultUpdatesCheckFrequency);
        updatesChannel = Token(section.UpdatesChannel, UpdatesChannelTokens, DefaultUpdatesChannel);
        paletteExcludeDirectories = Text(section.PaletteExcludeDirectories, DefaultPaletteExcludeDirectories);
        chatMermaidDiagramTypes = Text(section.ChatMermaidDiagramTypes, DefaultChatMermaidDiagramTypes);
        projectsCloneDirectory = Text(section.ProjectsCloneDirectory, DefaultProjectsCloneDirectory);
        modelsHidden = NormalizeNames(section.ModelsHidden);
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
            AgentsMaxParallelSubAgents = agentsMaxParallelSubAgents,
            AgentsSubAgentsEnabled = agentsSubAgentsEnabled,
            IdleArchiveEnabled = idleArchiveEnabled,
            IdleArchiveDays = idleArchiveDays,
            NotificationPermissionReprompt = notificationPermissionReprompt,
            PalettePerSourceLimit = palettePerSourceLimit,
            ChatToolImageThumbnailPixels = chatToolImageThumbnailPixels,
            ChatToolImagesToModel = chatToolImagesToModel,
            FilesCsvMaxRows = filesCsvMaxRows,
            FilesNotebookOutputKilobytes = filesNotebookOutputKilobytes,
            FilesImagePreviewMaxMegabytes = filesImagePreviewMaxMegabytes,
            GitChecksNoChecksConfirmations = gitChecksNoChecksConfirmations,
            GitChecksFailureLogTailLines = gitChecksFailureLogTailLines,
            GitReviewDiffMaxChars = gitReviewDiffMaxChars,
            GitReviewMaxCommits = gitReviewMaxCommits,
            GitReviewMaxFiles = gitReviewMaxFiles,
            GitReviewFieldMaxChars = gitReviewFieldMaxChars,
            McpRequestTimeoutSeconds = mcpRequestTimeoutSeconds,
            McpReconnectInitialDelaySeconds = mcpReconnectInitialDelaySeconds,
            McpToolSearchLimit = mcpToolSearchLimit,
            McpStderrTailLines = mcpStderrTailLines,
            InspectorLogTailLines = inspectorLogTailLines,
            InspectorRefreshMilliseconds = inspectorRefreshMilliseconds,
            ChatMermaidTimeoutMilliseconds = chatMermaidTimeoutMilliseconds,
            ProjectsCloneTimeoutSeconds = projectsCloneTimeoutSeconds,
            IdleArchiveScope = idleArchiveScope,
            UpdatesCheckFrequency = updatesCheckFrequency,
            UpdatesChannel = updatesChannel,
            PaletteExcludeDirectories = paletteExcludeDirectories,
            ChatMermaidDiagramTypes = chatMermaidDiagramTypes,
            ProjectsCloneDirectory = projectsCloneDirectory,
            ModelsHidden = modelsHidden.Count == 0 ? null : [.. modelsHidden],
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

    private static string Token(string? value, string[] allowed, string fallback)
    {
        var trimmed = value?.Trim();
        foreach (var token in allowed)
        {
            if (string.Equals(token, trimmed, StringComparison.OrdinalIgnoreCase))
            {
                return token;
            }
        }

        return fallback;
    }

    private static string Text(string? value, string fallback) => value?.Trim() ?? fallback;

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

    [JsonPropertyName("agentsMaxParallelSubAgents")]
    public int? AgentsMaxParallelSubAgents { get; set; }

    [JsonPropertyName("agentsSubAgentsEnabled")]
    public bool? AgentsSubAgentsEnabled { get; set; }

    [JsonPropertyName("idleArchiveEnabled")]
    public bool? IdleArchiveEnabled { get; set; }

    [JsonPropertyName("idleArchiveDays")]
    public int? IdleArchiveDays { get; set; }

    [JsonPropertyName("notificationPermissionReprompt")]
    public bool? NotificationPermissionReprompt { get; set; }

    [JsonPropertyName("palettePerSourceLimit")]
    public int? PalettePerSourceLimit { get; set; }

    [JsonPropertyName("chatToolImageThumbnailPixels")]
    public int? ChatToolImageThumbnailPixels { get; set; }

    [JsonPropertyName("chatToolImagesToModel")]
    public bool? ChatToolImagesToModel { get; set; }

    [JsonPropertyName("filesCsvMaxRows")]
    public int? FilesCsvMaxRows { get; set; }

    [JsonPropertyName("filesNotebookOutputKilobytes")]
    public int? FilesNotebookOutputKilobytes { get; set; }

    [JsonPropertyName("filesImagePreviewMaxMegabytes")]
    public int? FilesImagePreviewMaxMegabytes { get; set; }

    [JsonPropertyName("gitChecksNoChecksConfirmations")]
    public int? GitChecksNoChecksConfirmations { get; set; }

    [JsonPropertyName("gitChecksFailureLogTailLines")]
    public int? GitChecksFailureLogTailLines { get; set; }

    [JsonPropertyName("gitReviewDiffMaxChars")]
    public int? GitReviewDiffMaxChars { get; set; }

    [JsonPropertyName("gitReviewMaxCommits")]
    public int? GitReviewMaxCommits { get; set; }

    [JsonPropertyName("gitReviewMaxFiles")]
    public int? GitReviewMaxFiles { get; set; }

    [JsonPropertyName("gitReviewFieldMaxChars")]
    public int? GitReviewFieldMaxChars { get; set; }

    [JsonPropertyName("mcpRequestTimeoutSeconds")]
    public int? McpRequestTimeoutSeconds { get; set; }

    [JsonPropertyName("mcpReconnectInitialDelaySeconds")]
    public int? McpReconnectInitialDelaySeconds { get; set; }

    [JsonPropertyName("mcpToolSearchLimit")]
    public int? McpToolSearchLimit { get; set; }

    [JsonPropertyName("mcpStderrTailLines")]
    public int? McpStderrTailLines { get; set; }

    [JsonPropertyName("inspectorLogTailLines")]
    public int? InspectorLogTailLines { get; set; }

    [JsonPropertyName("inspectorRefreshMilliseconds")]
    public int? InspectorRefreshMilliseconds { get; set; }

    [JsonPropertyName("chatMermaidTimeoutMilliseconds")]
    public int? ChatMermaidTimeoutMilliseconds { get; set; }

    [JsonPropertyName("projectsCloneTimeoutSeconds")]
    public int? ProjectsCloneTimeoutSeconds { get; set; }

    [JsonPropertyName("idleArchiveScope")]
    public string? IdleArchiveScope { get; set; }

    [JsonPropertyName("updatesCheckFrequency")]
    public string? UpdatesCheckFrequency { get; set; }

    [JsonPropertyName("updatesChannel")]
    public string? UpdatesChannel { get; set; }

    [JsonPropertyName("paletteExcludeDirectories")]
    public string? PaletteExcludeDirectories { get; set; }

    [JsonPropertyName("chatMermaidDiagramTypes")]
    public string? ChatMermaidDiagramTypes { get; set; }

    [JsonPropertyName("projectsCloneDirectory")]
    public string? ProjectsCloneDirectory { get; set; }

    [JsonPropertyName("modelsHidden")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string[]? ModelsHidden { get; set; }
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
