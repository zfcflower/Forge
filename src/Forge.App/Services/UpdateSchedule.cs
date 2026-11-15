namespace Forge.App.Services;

/// <summary>
/// The automatic-check schedule (wave W10b). The preference token (<c>daily</c>/<c>weekly</c>/<c>never</c>) is read live, and
/// the last check comes from the <c>updates.lastCheck</c> UI-state entry: when it ran and whether it completed. This pure
/// function decides whether the periodic pump should run a check now. <c>never</c> and any unknown token turn the pump off, so
/// a value written by a newer build degrades to "don't check" rather than "check constantly".
/// <para>
/// A failed check waits <see cref="FailedRetryInterval"/> rather than the full frequency interval: a machine that was offline
/// when the app started must retry within the session, not a day later. A check that completed — whether it found an update or
/// not — waits out the configured interval.
/// </para>
/// </summary>
internal static class UpdateSchedule
{
    public const string Daily = "daily";
    public const string Weekly = "weekly";
    public const string Never = "never";

    /// <summary>The <c>status</c> token <see cref="UpdateService"/> records for a check that did not complete.</summary>
    public const string FailedStatus = "failed";

    /// <summary>How long after a failed check the pump tries again, regardless of the frequency.</summary>
    public static readonly TimeSpan FailedRetryInterval = TimeSpan.FromHours(6);

    /// <summary>
    /// <c>true</c> when the pump should check at <paramref name="now"/>. <paramref name="lastStatus"/> is the recorded outcome
    /// token of the previous check (<c>null</c> for an entry written before the token existed or by hand); only
    /// <see cref="FailedStatus"/> shortens the wait.
    /// </summary>
    public static bool ShouldCheck(string frequency, DateTimeOffset? lastCheckedAt, string? lastStatus, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(frequency);

        var interval = frequency switch
        {
            Daily => TimeSpan.FromDays(1),
            Weekly => TimeSpan.FromDays(7),
            _ => (TimeSpan?)null,
        };

        if (interval is not { } configured)
        {
            return false;
        }

        if (lastCheckedAt is not { } lastChecked)
        {
            return true;
        }

        var wait = string.Equals(lastStatus, FailedStatus, StringComparison.Ordinal) ? FailedRetryInterval : configured;
        return now - lastChecked >= wait;
    }
}
