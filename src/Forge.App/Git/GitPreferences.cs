using Forge.App.Preferences;
using Forge.Workspace;

namespace Forge.App.Git;

/// <summary>
/// Bridges the persisted preference tokens (see <see cref="AppPreferences.GitPullStrategy"/> and
/// <see cref="AppPreferences.GitNetworkTimeoutSeconds"/>) and the typed values the git feature passes to
/// <see cref="GitCli"/>. Validation belongs to the preference setters, so an unknown token can only mean "the default".
/// </summary>
internal static class GitPreferences
{
    public static GitPullStrategy ParseStrategy(string? token) => token switch
    {
        AppPreferences.GitPullStrategyRebase => GitPullStrategy.Rebase,
        AppPreferences.GitPullStrategyMerge => GitPullStrategy.Merge,
        _ => GitPullStrategy.FastForwardOnly,
    };

    public static string StrategyToken(GitPullStrategy strategy) => strategy switch
    {
        GitPullStrategy.Rebase => AppPreferences.GitPullStrategyRebase,
        GitPullStrategy.Merge => AppPreferences.GitPullStrategyMerge,
        _ => AppPreferences.GitPullStrategyFastForwardOnly,
    };

    /// <summary>
    /// Live settings for <see cref="GitPanelViewModel"/>: both values are read at the start of every operation on the UI
    /// thread, so a change in the settings section applies to the next operation without a restart.
    /// </summary>
    public static GitPanelSettings PanelSettings(AppPreferences preferences) =>
        new(
            () => ParseStrategy(preferences.GitPullStrategy),
            () => TimeSpan.FromSeconds(preferences.GitNetworkTimeoutSeconds));
}
