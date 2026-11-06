using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Forge.App.Localization;
using Forge.App.ViewModels;
using Forge.IO;

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
    /// <c>fonts:Inter#Inter</c> is the embedded Inter registered by <c>WithInterFont()</c>; a bare <c>Inter</c> only
    /// matches an installed system font and would silently fall through to the next family.
    /// </summary>
    public const string DefaultUiFontFamily = "fonts:Inter#Inter, Segoe UI, PingFang SC, Microsoft YaHei, sans-serif";
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
}

/// <summary>The <c>preferences</c> section, serialized through this source-generated context (no reflection, AOT safe).</summary>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(PreferencesSection))]
internal sealed partial class PreferencesJsonContext : JsonSerializerContext
{
}

/// <summary>
/// Loads and saves <see cref="AppPreferences"/> as the <c>preferences</c> section of <c>settings.json</c>, preserving every
/// other section byte-for-byte in meaning (the provider section is still read by <c>ProviderSettings</c>). Reads and writes
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
            Trace.WriteLine("[preferences] the final save failed: " + exception.Message);
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
            Trace.WriteLine("[preferences] save failed: " + exception.Message);
        }
    }

    /// <summary>The one write path: keep every other section, back up an unreadable file once, then replace atomically.</summary>
    private void SaveCore(CancellationToken ct)
    {
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
            Trace.WriteLine("[preferences] the existing settings.json could not be read: " + exception.Message);
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
