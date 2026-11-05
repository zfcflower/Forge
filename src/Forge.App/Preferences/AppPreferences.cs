using Forge.App.ViewModels;

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
    public const string DefaultUiFontFamily = "Inter, Segoe UI, PingFang SC, Microsoft YaHei, sans-serif";
    public const string DefaultCodeFontFamily = "Consolas, Cascadia Mono, Menlo, monospace";
    public const double DefaultUiFontSize = 13;
    public const double DefaultCodeFontSize = 13;

    public string UiFontFamily { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }

    public double UiFontSize { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }

    public string CodeFontFamily { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }

    public double CodeFontSize { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }

    public UiLanguage Language { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }

    /// <summary>Shell executable for new terminals; empty means the platform default (pwsh/powershell on Windows, $SHELL or /bin/sh elsewhere).</summary>
    public string TerminalShell { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }

    /// <summary>Arguments for <see cref="TerminalShell"/>, space separated with double quotes for grouping.</summary>
    public string TerminalArguments { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }
}

/// <summary>
/// Loads and saves <see cref="AppPreferences"/> as the <c>preferences</c> section of <c>settings.json</c>, preserving every
/// other section byte-for-byte in meaning (the provider section is still read by <c>ProviderSettings</c>). Reads and writes
/// run on a background thread; a corrupt or missing section yields defaults and is reported through <see cref="LoadProblem"/>.
/// </summary>
internal sealed class PreferencesStore : IAsyncDisposable
{
    public PreferencesStore(string settingsPath) => throw new NotImplementedException();

    public AppPreferences Current => throw new NotImplementedException();

    /// <summary>Null when the last load succeeded or the file did not exist.</summary>
    public string? LoadProblem => throw new NotImplementedException();

    public Task LoadAsync(CancellationToken ct) => throw new NotImplementedException();

    /// <summary>Writes immediately (atomic replace via Forge.IO); normally called by the debounce, and once on dispose.</summary>
    public Task SaveAsync(CancellationToken ct) => throw new NotImplementedException();

    public ValueTask DisposeAsync() => throw new NotImplementedException();
}
