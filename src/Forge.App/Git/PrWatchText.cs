using System.Globalization;
using System.Text;
using Forge.App.Localization;
using Forge.Workspace;

namespace Forge.App.Git;

/// <summary>
/// The localized words for a wait on a pull request's checks: the live progress line, the verdict of every
/// <see cref="PrWaitOutcome"/> and the short "CI" marker of the status bar. Pure functions over the poller's records, so
/// the panel, the status bar and the system notification say the same thing. Reads <see cref="Loc"/> on every call (the
/// table is thread-safe), so a language switch shows in the next text.
/// </summary>
internal static class PrWatchText
{
    /// <summary>How many failing check names the verdict lists before it ends in an ellipsis.</summary>
    private const int MaxListedChecks = 5;

    /// <summary>"3 passed · 1 failed · 2 pending", or "no checks reported yet" before the first check appears.</summary>
    public static string Progress(PrChecksObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        if (observation.Checks.NoneReported)
        {
            return Loc.Instance[GitStrings.WatchNoChecksYet];
        }

        return Loc.Instance.Format(
            GitStrings.WatchProgress,
            observation.PassedCount,
            observation.FailingChecks.Count,
            observation.PendingChecks.Count);
    }

    /// <summary>"Merge state: CLEAN" with the platform's raw text; an empty text reads as UNKNOWN.</summary>
    public static string MergeState(PrChecksObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        var raw = observation.MergeState.MergeStateStatusText;
        return Loc.Instance.Format(GitStrings.WatchMergeState, string.IsNullOrWhiteSpace(raw) ? "UNKNOWN" : raw.Trim());
    }

    /// <summary>One sentence for how the wait ended; the same text is the body of the system notification.</summary>
    public static string Outcome(PrWaitResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var loc = Loc.Instance;
        return result.Outcome switch
        {
            PrWaitOutcome.Ready => loc[GitStrings.OutcomeReady],
            PrWaitOutcome.ReadyWithHooks => loc[GitStrings.OutcomeReadyWithHooks],
            PrWaitOutcome.ChecksFailed => loc.Format(
                GitStrings.OutcomeChecksFailed,
                result.FailedChecks.Count,
                FailedNames(result.FailedChecks)),
            PrWaitOutcome.Behind => loc[GitStrings.OutcomeBehind],
            PrWaitOutcome.Dirty => loc[GitStrings.OutcomeDirty],
            PrWaitOutcome.Blocked => loc[GitStrings.OutcomeBlocked],
            PrWaitOutcome.Unstable => loc[GitStrings.OutcomeUnstable],
            PrWaitOutcome.Draft => loc[GitStrings.OutcomeDraft],
            PrWaitOutcome.NoCi => loc[GitStrings.OutcomeNoCi],
            PrWaitOutcome.NotOpen => loc[GitStrings.OutcomeNotOpen],
            PrWaitOutcome.TimedOut => loc.Format(
                GitStrings.OutcomeTimedOut,
                Math.Max(1, (int)Math.Round(result.Elapsed.TotalMinutes, MidpointRounding.AwayFromZero))),
            PrWaitOutcome.Failed => result.FailureReason == PrFailureReason.PlatformNotInstalled
                ? loc[GitStrings.PullRequestGhNotReady]
                : loc.Format(GitStrings.OutcomeFailed, FailureMessage(result)),
            _ => loc.Format(GitStrings.OutcomeFailed, result.Outcome.ToString()),
        };
    }

    /// <summary>
    /// True when the outcome asks the user to do something (failed checks, conflicts, a missing review, a timeout), false
    /// when the pull request is fine or nothing is left to wait for. Decides the colour of the verdict line.
    /// </summary>
    public static bool IsProblem(PrWaitOutcome outcome) =>
        outcome is not (PrWaitOutcome.Ready or PrWaitOutcome.ReadyWithHooks or PrWaitOutcome.NoCi or PrWaitOutcome.NotOpen);

    /// <summary>The status bar marker while a wait runs: "CI …", or "CI ✗" as soon as a failing check is seen.</summary>
    public static string Running(PrChecksObservation? observation) =>
        Loc.Instance[observation is { FailingChecks.Count: > 0 } ? GitStrings.ChecksFailed : GitStrings.ChecksRunning];

    /// <summary>
    /// The status bar marker for a finished wait: "CI ✓" (ready), "CI ✗" (failed checks), "CI –" (no CI), "CI !" (anything
    /// that needs a look); empty when the pull request is merged or closed, where a marker would only be stale.
    /// </summary>
    public static string Finished(PrWaitOutcome outcome) => outcome switch
    {
        PrWaitOutcome.Ready or PrWaitOutcome.ReadyWithHooks => Loc.Instance[GitStrings.ChecksPassed],
        PrWaitOutcome.ChecksFailed => Loc.Instance[GitStrings.ChecksFailed],
        PrWaitOutcome.NoCi => Loc.Instance[GitStrings.ChecksNone],
        PrWaitOutcome.NotOpen => string.Empty,
        _ => Loc.Instance[GitStrings.ChecksAttention],
    };

    private static string FailureMessage(PrWaitResult result)
    {
        var message = result.Failure?.Message;
        return string.IsNullOrWhiteSpace(message) ? result.Outcome.ToString() : message.Trim();
    }

    private static string FailedNames(IReadOnlyList<PrCheck> checks)
    {
        var builder = new StringBuilder();
        var listed = Math.Min(checks.Count, MaxListedChecks);
        for (var index = 0; index < listed; index++)
        {
            if (index > 0)
            {
                builder.Append(", ");
            }

            builder.Append(checks[index].Name);
        }

        if (checks.Count > listed)
        {
            builder.Append(CultureInfo.InvariantCulture, $", … (+{checks.Count - listed})");
        }

        return builder.ToString();
    }
}
