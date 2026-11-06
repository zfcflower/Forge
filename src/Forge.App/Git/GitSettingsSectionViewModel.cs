using Forge.App.Localization;
using Forge.App.Preferences;
using Forge.App.ViewModels;
using Forge.Workspace;

namespace Forge.App.Git;

/// <summary>
/// The <c>git</c> settings section (<see cref="SettingsSectionIds.Git"/>). Edits <see cref="AppPreferences"/> directly, so
/// every change applies to the next git operation and is persisted by the preferences store's debounce. Later batches add
/// their rows here (step commits, gh path, wait interval and limit, system notifications).
/// <para>Preferences contract (fields in <see cref="AppPreferences"/>, JSON in the <c>preferences</c> section):
/// <c>gitPullStrategy</c> = <c>"ff-only"</c> (default) | <c>"rebase"</c> | <c>"merge"</c>, unknown tokens fall back to the
/// default; <c>gitNetworkTimeoutSeconds</c> = integer clamped to [10, 600], default 120.</para>
/// </summary>
internal sealed class GitSettingsSectionViewModel : ObservableObject
{
    private readonly AppPreferences preferences;

    /// <summary>Replaced (not mutated) on a language switch: the picker only re-reads its items for a new list.</summary>
    private GitPullStrategyOption[] options = CreateOptions();

    public GitSettingsSectionViewModel(AppPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        this.preferences = preferences;

        // One section lives as long as the window, so the subscription is never dropped: the picker's labels are
        // <see cref="Loc"/> keys and have to be re-read after a language switch, like the section heading itself.
        Loc.Instance.LanguageChanged += OnLanguageChanged;
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

    /// <summary>
    /// Re-reads the picker's labels. The items are immutable records that read <see cref="Loc"/> on every
    /// <see cref="GitPullStrategyOption.Label"/> get, so the list itself is replaced; re-assigning the same instance would
    /// leave the realized item containers on the old text.
    /// </summary>
    private void OnLanguageChanged(object? sender, EventArgs e)
    {
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
