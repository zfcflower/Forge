using System.Collections.ObjectModel;
using Forge.App.Composition;
using Forge.App.Localization;
using Forge.App.Preferences;

namespace Forge.App.ViewModels;

/// <summary>
/// The settings surface. It is deliberately read-only: it shows where the settings file lives, what was read from it and
/// what the extension host reported, and it can reload. Nothing here edits a secret or invents a working model call.
/// <para>The page body is a list of sections (<see cref="SettingsSections"/>): this class contributes the two the shell
/// owns today (<c>provider</c> and <c>paths</c>), and the page renders every section through the app-level DataTemplate
/// for its view model, so a feature adds its settings without editing the page. Section headings and the page's own
/// labels are <see cref="Loc"/> keys and refresh on <see cref="Loc.LanguageChanged"/>.</para>
/// </summary>
internal sealed class SettingsViewModel : ObservableObject, IDisposable
{
    private readonly AppComposition composition;
    private readonly ProviderSettingsSectionViewModel provider;
    private readonly PathsSettingsSectionViewModel paths;
    private readonly int uiThread = Environment.CurrentManagedThreadId;
    private bool isOpen;

    public SettingsViewModel(AppComposition composition)
    {
        this.composition = composition;

        provider = new ProviderSettingsSectionViewModel();
        paths = new PathsSettingsSectionViewModel();

        // The registry belongs to the composition root (that is where features append their sections); this view model
        // contributes the two the shell owns today, unless a test or a feature already put them there.
        Sections = composition.Settings;
        if (!Sections.Items.Any(static section => string.Equals(section.Id, SettingsSectionIds.Provider, StringComparison.Ordinal)))
        {
            Sections.Add(new SettingsSection(SettingsSectionIds.Provider, CoreStrings.SettingsSectionProvider, SettingsSectionIds.ProviderOrder, provider));
            Sections.Add(new SettingsSection(SettingsSectionIds.Paths, CoreStrings.SettingsSectionPaths, SettingsSectionIds.PathsOrder, paths));
        }

        Loc.Instance.LanguageChanged += OnLanguageChanged;
        Rebuild();
    }

    /// <summary>The page body, ordered by each section's <see cref="SettingsSection.Order"/>.</summary>
    public SettingsSections Sections { get; }

    public bool IsOpen
    {
        get => isOpen;
        set => Set(ref isOpen, value);
    }

    public string Title => Loc.Instance[CoreStrings.SettingsTitle];

    public string ReloadText => Loc.Instance[CoreStrings.SettingsReload];

    public string CloseText => Loc.Instance[CoreStrings.SettingsClose];

    public void Rebuild()
    {
        paths.Paths.Clear();
        paths.Paths.Add(new SettingLine(Loc.Instance[CoreStrings.SettingsPathFile], composition.Paths.SettingsPath));
        paths.Paths.Add(new SettingLine(Loc.Instance[CoreStrings.SettingsPathData], composition.Paths.UserDataDirectory));
        paths.Paths.Add(new SettingLine(Loc.Instance[CoreStrings.SettingsPathWorkspace], composition.WorkspaceDirectory));
        paths.Paths.Add(new SettingLine(Loc.Instance[CoreStrings.SettingsPathGeneralWorkspace], composition.Paths.GeneralWorkspaceDirectory));
        paths.Paths.Add(new SettingLine(Loc.Instance[CoreStrings.SettingsPathHostData], composition.Paths.HostDataDirectory));
        paths.Paths.Add(new SettingLine(Loc.Instance[CoreStrings.SettingsPathPlugins], composition.Paths.PluginsDirectory));

        var providerSettings = composition.Provider;
        provider.Detail = providerSettings.Provider is { } snapshot
            ? ShellStrings.ProviderDetail(snapshot.Summary, snapshot.HasApiKey, snapshot.KeyFromEnvironment)
            : providerSettings.StatusText.Resolve();

        if (composition.PreferencesProblem is { Length: > 0 } preferencesProblem)
        {
            paths.Paths.Add(new SettingLine(Loc.Instance[CoreStrings.SettingsPathPreferences], preferencesProblem));
        }

        var hostProblem = composition.HostProblem;
        if (!hostProblem.IsEmpty)
        {
            paths.Paths.Add(new SettingLine(Loc.Instance[CoreStrings.SettingsPathHost], hostProblem.Resolve()));
        }

        if (composition.ApprovalsDatabasePath is { Length: > 0 } approvalsDatabase)
        {
            paths.Paths.Add(new SettingLine(Loc.Instance[CoreStrings.SettingsPathApprovals], approvalsDatabase));
        }

        if (composition.Mcp is { } mcp)
        {
            paths.Paths.Add(new SettingLine(Loc.Instance[CoreStrings.SettingsPathMcp], ShellStrings.McpDetail(mcp.ConfiguredServers, mcp.ConfigDiagnostics)));
        }

        if (composition.Editor is { } editor)
        {
            paths.Paths.Add(new SettingLine(Loc.Instance[CoreStrings.SettingsPathEditor], ShellStrings.EditorDetail(editor.LanguageCount, editor.GrammarCount, editor.Problem)));
        }
    }

    /// <summary>Drops the language subscription; the window view model disposes the settings page with itself.</summary>
    public void Dispose() => Loc.Instance.LanguageChanged -= OnLanguageChanged;

    /// <summary>
    /// Text that caches a language-dependent value has to re-read it: the headings through <see cref="SettingsSection.Title"/>,
    /// the provider section's hint, and the path rows' labels (rebuilt by <see cref="Rebuild"/>).
    /// </summary>
    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        // A notification from another thread belongs to a settings page whose window is gone (a test host): rebuilding
        // its rows there would touch bound controls off their thread.
        if (Environment.CurrentManagedThreadId != uiThread)
        {
            return;
        }

        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(ReloadText));
        OnPropertyChanged(nameof(CloseText));

        foreach (var section in Sections.Items)
        {
            section.RaiseTitle();
        }

        provider.RefreshText();
        Rebuild();
    }
}

/// <summary>The <c>provider</c> section: which model provider the app uses, or the sentence that says why there is none.</summary>
internal sealed class ProviderSettingsSectionViewModel : ObservableObject
{
    private string detail = string.Empty;

    public string Detail
    {
        get => detail;
        set => Set(ref detail, value);
    }

    public string Hint => Loc.Instance[CoreStrings.SettingsProviderHint];

    /// <summary>Re-reads the strings that come from <see cref="Loc"/> after a language switch.</summary>
    internal void RefreshText() => OnPropertyChanged(nameof(Hint));
}

/// <summary>The <c>paths</c> section: the data root, the workspace and the services the shell found.</summary>
internal sealed class PathsSettingsSectionViewModel : ObservableObject
{
    public ObservableCollection<SettingLine> Paths { get; } = [];
}

/// <summary>One label/value row of the settings panel.</summary>
internal sealed record SettingLine(string Label, string Value);
