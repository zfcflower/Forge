using Forge.App.Localization;
using Forge.App.Preferences;
using Forge.App.ViewModels;
using Forge.Workspace;

namespace Forge.App.Git;

/// <summary>
/// The <c>git</c> settings section (<see cref="SettingsSectionIds.Git"/>). Edits <see cref="AppPreferences"/> directly, so
/// every change applies to the next git operation and is persisted by the preferences store's debounce.
/// <para>Preferences contract (fields in <see cref="AppPreferences"/>, JSON in the <c>preferences</c> section):
/// <c>gitPullStrategy</c> = <c>"ff-only"</c> (default) | <c>"rebase"</c> | <c>"merge"</c>, unknown tokens fall back to the
/// default; <c>gitNetworkTimeoutSeconds</c> = integer clamped to [10, 600], default 120; <c>gitStepCommits</c> = boolean,
/// default <c>true</c> (a TODO item marked done commits that step; missing means the default);
/// <c>gitChecksPollSeconds</c> = integer clamped to [10, 300], default 30 (wait mode: time between two polls);
/// <c>gitChecksMaxWaitMinutes</c> = integer clamped to [1, 180], default 30 (wait mode: longest wait);
/// <c>gitNotifications</c> = boolean, default <c>true</c> (one system notification when a wait ends);
/// <c>gitGhPath</c> = string, default empty meaning <c>gh</c> from PATH, trimmed.</para>
/// </summary>
internal sealed class GitSettingsSectionViewModel : ObservableObject, IDisposable
{
    private readonly AppPreferences preferences;

    /// <summary>The thread the section was built on (the UI thread); a language notification from another one is not ours.</summary>
    private readonly int uiThread = Environment.CurrentManagedThreadId;

    /// <summary>Replaced (not mutated) on a language switch: the picker only re-reads its items for a new list.</summary>
    private GitPullStrategyOption[] options = CreateOptions();

    private bool disposed;

    public GitSettingsSectionViewModel(AppPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        this.preferences = preferences;

        // The picker's labels are <see cref="Loc"/> keys and have to be re-read after a language switch, like the
        // section heading itself. The section is disposed with the composition (which disposes every IDisposable
        // settings section), and Dispose drops this subscription so the process-wide Loc does not keep it alive.
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

    /// <summary>The three strategies in display order, with localized labels.</summary>
    public IReadOnlyList<GitPullStrategyOption> PullStrategies => options;

    public GitPullStrategyOption SelectedPullStrategy
    {
        get => ChoiceOf(GitPreferences.ParseStrategy(preferences.GitPullStrategy));
        set
        {
            if (value is null)
            {
                return;
            }

            preferences.GitPullStrategy = GitPreferences.StrategyToken(value.Value);

            // The picker also has to re-read the clamped/defaulted value the setter settled on.
            OnPropertyChanged();
        }
    }

    public int NetworkTimeoutSeconds
    {
        get => preferences.GitNetworkTimeoutSeconds;
        set
        {
            preferences.GitNetworkTimeoutSeconds = value;
            OnPropertyChanged();
        }
    }

    /// <summary>Whether marking a TODO item done commits that step's changes to the session branch.</summary>
    public bool StepCommits
    {
        get => preferences.GitStepCommits;
        set
        {
            preferences.GitStepCommits = value;
            OnPropertyChanged();
        }
    }

    /// <summary>Seconds between two polls of a pull request's checks in wait mode (10 to 300).</summary>
    public int ChecksPollSeconds
    {
        get => preferences.GitChecksPollSeconds;
        set
        {
            preferences.GitChecksPollSeconds = value;
            OnPropertyChanged();
        }
    }

    /// <summary>Longest wait mode keeps polling, in minutes (1 to 180).</summary>
    public int ChecksMaxWaitMinutes
    {
        get => preferences.GitChecksMaxWaitMinutes;
        set
        {
            preferences.GitChecksMaxWaitMinutes = value;
            OnPropertyChanged();
        }
    }

    /// <summary>Whether the end of a wait raises one system notification.</summary>
    public bool Notifications
    {
        get => preferences.GitNotifications;
        set
        {
            preferences.GitNotifications = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Full path of the GitHub CLI; empty means <c>gh</c> from PATH. The preference stores the trimmed value and the next gh
    /// call picks it up (see <see cref="ConfiguredGh"/>). Deliberately raises no <c>PropertyChanged</c>: the box is edited
    /// character by character, and pushing the trimmed value back would swallow the space typed inside "Program Files".
    /// </summary>
    public string GhPath
    {
        get => preferences.GitGhPath;
        set => preferences.GitGhPath = value;
    }

    /// <summary>
    /// Re-reads the picker's labels. The items are immutable records that read <see cref="Loc"/> on every
    /// <see cref="GitPullStrategyOption.Label"/> get, so the list itself is replaced; re-assigning the same instance would
    /// leave the realized item containers on the old text.
    /// </summary>
    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        // A notification from another thread belongs to a window that is gone (a test host): the picker bound to
        // these properties lives on its own thread.
        if (disposed || Environment.CurrentManagedThreadId != uiThread)
        {
            return;
        }

        options = CreateOptions();
        OnPropertyChanged(nameof(PullStrategies));
        OnPropertyChanged(nameof(SelectedPullStrategy));
    }

    private static GitPullStrategyOption[] CreateOptions() =>
    [
        new(GitPullStrategy.FastForwardOnly, GitStrings.SettingsPullStrategyFastForward),
        new(GitPullStrategy.Rebase, GitStrings.SettingsPullStrategyRebase),
        new(GitPullStrategy.Merge, GitStrings.SettingsPullStrategyMerge),
    ];

    private GitPullStrategyOption ChoiceOf(GitPullStrategy strategy) => strategy switch
    {
        GitPullStrategy.Rebase => options[1],
        GitPullStrategy.Merge => options[2],
        _ => options[0],
    };
}

/// <summary>One choice in the pull-strategy picker; <see cref="Label"/> is re-read on a language switch.</summary>
internal sealed record GitPullStrategyOption(GitPullStrategy Value, string LabelKey)
{
    public string Label => Localization.Loc.Instance[LabelKey];
}
