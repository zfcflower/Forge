using Forge.App.Preferences;
using Forge.Workspace;

namespace Forge.App.Git;

/// <summary>
/// Live settings for the agent's git tools (<see cref="GitTools"/>), built from the same preference tokens and with the
/// same mapping as <see cref="GitPreferences.PanelSettings"/>: an operation reads the current value at its start, so a
/// change in the <c>git</c> settings section applies to the next call without a restart.
/// </summary>
internal static class GitToolPreferences
{
    public static GitToolSettings ToolSettings(AppPreferences preferences) =>
        new()
        {
            PullStrategy = () => GitPreferences.ParseStrategy(preferences.GitPullStrategy),
            NetworkTimeout = () => TimeSpan.FromSeconds(preferences.GitNetworkTimeoutSeconds),
        };

    /// <summary>
    /// The wait-mode timing, read per wait from the live preferences (the settings section's poll interval and longest
    /// wait); the confirmation count is the poller's own default. Used by the <c>pr_checks</c> tool and the Git panel.
    /// </summary>
    public static PrChecksPollerOptions ChecksOptions(AppPreferences preferences) =>
        new()
        {
            Interval = TimeSpan.FromSeconds(preferences.GitChecksPollSeconds),
            MaxWait = TimeSpan.FromMinutes(preferences.GitChecksMaxWaitMinutes),
        };
}
