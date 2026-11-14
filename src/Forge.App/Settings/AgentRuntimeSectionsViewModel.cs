using System.Collections.ObjectModel;
using Forge.App.Localization;
using Forge.App.Preferences;
using Forge.App.ViewModels;

namespace Forge.App.Settings;

/// <summary>
/// The <c>agents</c> settings section (<see cref="SettingsSectionIds.Agents"/>): the cap on how many agents of one session's
/// spawn tree may run at once, and whether a session may spawn sub-agents at all. It edits <see cref="AppPreferences"/>
/// directly, and <c>SessionRuntime</c> reads both values before every turn, so a change reaches a running session without a
/// restart; the range shown by the field is the range the preference setter clamps to, so the two cannot disagree.
/// </summary>
internal sealed class AgentsSettingsSectionViewModel : ObservableObject
{
    private readonly AppPreferences preferences;

    public AgentsSettingsSectionViewModel(AppPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        this.preferences = preferences;
    }

    /// <summary>Smallest cap the field offers; zero permits would never admit the root agent.</summary>
    public int MinParallel => AppPreferences.MinAgentsMaxParallelSubAgents;

    /// <summary>Largest cap the field offers.</summary>
    public int MaxParallelLimit => AppPreferences.MaxAgentsMaxParallelSubAgents;

    /// <summary>Agents of one session tree that may run at once (1 to 16). Re-read after writing, so the field shows the clamped value.</summary>
    public int MaxParallel
    {
        get => preferences.AgentsMaxParallelSubAgents;
        set
        {
            preferences.AgentsMaxParallelSubAgents = value;
            OnPropertyChanged();
        }
    }

    /// <summary>Whether a session may spawn sub-agents; off removes <c>spawn_agent</c> from the model's tools from the next turn.</summary>
    public bool SubAgentsEnabled
    {
        get => preferences.AgentsSubAgentsEnabled;
        set
        {
            preferences.AgentsSubAgentsEnabled = value;
            OnPropertyChanged();
        }
    }
}

/// <summary>One choice of the archive scope picker: the stored token and its label in the current language.</summary>
internal sealed record IdleArchiveScopeOption(string Token, string Label);

/// <summary>
/// The <c>idle-archive</c> settings section (<see cref="SettingsSectionIds.IdleArchive"/>): whether housekeeping archives
/// idle sessions at all, how many idle days it waits, and whether it may touch every root session or only the ones an
/// automation created. It edits <see cref="AppPreferences"/> directly; the runner reads the values through a factory once
/// per run, so a change applies from the next nightly run. The other exemptions of the step (open sessions, sub-sessions,
/// branches a weekly automation reuses) are not configurable and are listed in the hint.
/// </summary>
internal sealed class IdleArchiveSettingsSectionViewModel : ObservableObject, IDisposable
{
    private readonly AppPreferences preferences;

    /// <summary>The thread the section was built on (the UI thread); a language notification from another one is not ours.</summary>
    private readonly int uiThread = Environment.CurrentManagedThreadId;

    /// <summary>Replaced (not mutated) on a language switch: the picker only re-reads its items for a new list.</summary>
    private IdleArchiveScopeOption[] options = CreateOptions();

    private bool disposed;

    public IdleArchiveSettingsSectionViewModel(AppPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        this.preferences = preferences;
        Loc.Instance.LanguageChanged += OnLanguageChanged;
    }

    /// <summary>Drops the <see cref="Loc.LanguageChanged"/> subscription; idempotent.</summary>
    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        Loc.Instance.LanguageChanged -= OnLanguageChanged;
    }

    public int MinDays => AppPreferences.MinIdleArchiveDays;

    public int MaxDays => AppPreferences.MaxIdleArchiveDays;

    /// <summary>The two scopes in display order, with localized labels.</summary>
    public IReadOnlyList<IdleArchiveScopeOption> Scopes => options;

    public bool Enabled
    {
        get => preferences.IdleArchiveEnabled;
        set
        {
            preferences.IdleArchiveEnabled = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanConfigure));
        }
    }

    /// <summary>The window and scope only matter while archiving is on, so the body greys them out instead of hiding them.</summary>
    public bool CanConfigure => preferences.IdleArchiveEnabled;

    /// <summary>Days without activity before a session is archived (1 to 365).</summary>
    public int Days
    {
        get => preferences.IdleArchiveDays;
        set
        {
            preferences.IdleArchiveDays = value;
            OnPropertyChanged();
        }
    }

    public IdleArchiveScopeOption SelectedScope
    {
        get => OptionOf(preferences.IdleArchiveScope);
        set
        {
            if (value is null)
            {
                return;
            }

            preferences.IdleArchiveScope = value.Token;

            // The picker also has to re-read the value the setter accepted, which is the default for an unknown token.
            OnPropertyChanged();
        }
    }

    private IdleArchiveScopeOption OptionOf(string token) =>
        options.FirstOrDefault(option => string.Equals(option.Token, token, StringComparison.Ordinal)) ?? options[0];

    private static IdleArchiveScopeOption[] CreateOptions() =>
    [
        new(Forge.App.Automations.IdleArchiveSettings.AutomationScopeToken, AgentSettingsStrings.IdleArchiveScopeAutomation),
        new(Forge.App.Automations.IdleArchiveSettings.AllScopeToken, AgentSettingsStrings.IdleArchiveScopeAll),
    ];

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        if (disposed || Environment.CurrentManagedThreadId != uiThread)
        {
            return;
        }

        options = CreateOptions();
        OnPropertyChanged(nameof(Scopes));
        OnPropertyChanged(nameof(SelectedScope));
    }
}
