using System.Globalization;
using System.Text;
using Forge.Abstractions;

namespace Forge.Workspace;

/// <summary>
/// Renders what the PR check tools return to the model: compact plain text, verdict first, then the facts behind it. An
/// empty check list is only ever described as "not determined yet" here; "no CI" is a verdict the poller reaches after
/// several consecutive empty reads and nothing else.
/// </summary>
internal static class PrCheckText
{
    private const int MaxDescriptionCharacters = 200;

    /// <summary>One reading, no waiting: verdict if the reading settles it, otherwise why it is not determined yet.</summary>
    public static string Snapshot(int number, string? url, PrChecksObservation observation)
    {
        var text = new StringBuilder(512);
        AppendHeader(text, number, url);

        // A snapshot never concludes "no CI": a streak of 0 keeps an empty list undetermined.
        var outcome = PrChecksPoller.Evaluate(observation, 0, int.MaxValue);
        text.Append("Verdict: ")
            .Append(outcome is { } settled ? Verdict(settled, observation, 1, null) : Undetermined(observation))
            .Append('\n');
        AppendFacts(text, observation);
        return text.ToString();
    }

    /// <summary>The end of a wait: how long it took, the verdict, and the last facts seen.</summary>
    public static ToolResult WaitResult(int number, string? url, PrWaitResult result)
    {
        if (result.Outcome == PrWaitOutcome.Failed)
        {
            var failure = result.Failure
                ?? new GitRemoteResult(GitRemoteFailureKind.Other, "The platform could not be read.", string.Empty);
            return PrCheckFailure.ToToolResult(
                new GitRemoteResult(
                    failure.Failure,
                    $"Gave up waiting for pull request #{N(number)} after {N(result.Polls)} poll(s) over {Duration(result.Elapsed)}: " +
                    $"{N(PrChecksPoller.MaxConsecutiveFailures)} reads in a row failed. Last error: {failure.Message}",
                    failure.StandardError),
                result.FailureReason);
        }

        var text = new StringBuilder(512);
        AppendHeader(text, number, url);
        text.Append("Waited ").Append(Duration(result.Elapsed)).Append(" over ").Append(N(result.Polls)).Append(" poll(s).\n");
        text.Append("Verdict: ").Append(Verdict(result.Outcome, result.Observation, result.Polls, result)).Append('\n');
        if (result.Note is { Length: > 0 } note && result.Outcome != PrWaitOutcome.ReadyWithHooks)
        {
            text.Append("Note: ").Append(note).Append('\n');
        }

        if (result.Observation is { } observation)
        {
            AppendFacts(text, observation);
        }

        if (result.Failure is { } transient && result.Outcome == PrWaitOutcome.TimedOut)
        {
            text.Append("The most recent read also failed: ").Append(transient.Message).Append('\n');
        }

        return new ToolResult(text.ToString());
    }

    private static void AppendHeader(StringBuilder text, int number, string? url)
    {
        text.Append("Pull request #").Append(N(number));
        if (!string.IsNullOrEmpty(url))
        {
            text.Append(' ').Append(url);
        }

        text.Append('\n');
    }

    private static string Verdict(PrWaitOutcome outcome, PrChecksObservation? observation, int polls, PrWaitResult? wait)
    {
        var passed = observation?.PassedCount ?? 0;
        var skipped = observation?.SkippedCount ?? 0;
        var status = observation?.MergeState.MergeStateStatusText ?? string.Empty;

        switch (outcome)
        {
            case PrWaitOutcome.Ready:
                return $"READY. All {N(passed)} check(s) passed" + (skipped > 0 ? $" ({N(skipped)} of them skipped/neutral)" : string.Empty) +
                    " and the merge state is CLEAN. There is nothing left to wait for. Forge never merges a pull request: tell the user it is ready.";

            case PrWaitOutcome.ReadyWithHooks:
                return $"READY WITH A CAVEAT. All {N(passed)} check(s) passed and the merge state is HAS_HOOKS. " +
                    (wait?.Note ?? PrChecksPoller.HooksCaveat);

            case PrWaitOutcome.ChecksFailed:
            {
                var failing = observation?.FailingChecks ?? [];
                var pending = observation?.PendingChecks.Count ?? 0;
                var cancelled = 0;
                foreach (var check in failing)
                {
                    if (check.Bucket == PrCheckBucket.Cancel)
                    {
                        cancelled++;
                    }
                }

                var builder = new StringBuilder(256);
                builder.Append("CHECKS FAILED. ").Append(N(failing.Count)).Append(" check(s) failed or were cancelled");
                builder.Append(pending > 0 ? $"; {N(pending)} more are still running. " : ". ");
                builder.Append("Read why with pr_ci_log using the job_id (and run_id) listed below, fix the cause, commit and push, then check again.");
                if (cancelled > 0)
                {
                    builder.Append(" A cancelled check never counts as passed; it usually has no failed-step log.");
                }

                return builder.ToString();
            }

            case PrWaitOutcome.Behind:
                return "BEHIND. The checks have finished, but the base branch has commits this branch lacks. Nothing was synced. " +
                    "Sync the branch (pr_sync), push, then wait for the checks again.";

            case PrWaitOutcome.Dirty:
                return "CONFLICTS (DIRTY). The branch conflicts with its base. Nothing was changed. " +
                    "Resolve the conflicts through pr_sync, push, then wait for the checks again; CI often does not run while a pull request has conflicts.";

            case PrWaitOutcome.Blocked:
                return "BLOCKED. The checks have finished, but GitHub says a merge requirement is unmet" +
                    (observation is { } blocked ? $" (review decision: {ReviewDecision(blocked.MergeState)})" : string.Empty) +
                    ", typically a required review or a required check that never reported. Nothing was changed; report it to the user.";

            case PrWaitOutcome.Unstable:
                return "UNSTABLE. The pull request is mergeable, but a non-required status is failing or not passing. " +
                    "Look at the checks below; nothing was changed.";

            case PrWaitOutcome.Draft:
                return "DRAFT. The checks have finished, but the pull request is still a draft. " +
                    "Marking it ready for review is the user's decision, not yours.";

            case PrWaitOutcome.NoCi:
                return $"NO CI. No checks were reported by {N(polls)} poll(s) in a row, so this pull request has no CI " +
                    "(or none of its workflows run for pull requests), and nothing else is wrong with it" +
                    (string.IsNullOrEmpty(status) ? "." : $" (merge state {status}).") +
                    (wait?.Note is { Length: > 0 } hooks ? " " + hooks : string.Empty);

            case PrWaitOutcome.NotOpen:
                return observation?.MergeState.State == PrState.Merged
                    ? "NOT OPEN. The pull request has been merged; there is nothing left to wait for."
                    : "NOT OPEN. The pull request has been closed; there is nothing left to wait for.";

            case PrWaitOutcome.TimedOut:
                return $"TIMED OUT after {Duration(wait?.Elapsed ?? TimeSpan.Zero)}. " +
                    (observation is { } last ? Undetermined(last) : "No read of the platform succeeded.") +
                    " Nothing was changed. Ask the user whether to keep waiting; do not report this as a pass or as a failure.";

            default:
                return "The platform could not be read.";
        }
    }

    /// <summary>Why a reading is not a verdict yet: empty checks, running checks, or a merge state still being computed.</summary>
    private static string Undetermined(PrChecksObservation observation)
    {
        if (observation.Checks.NoneReported)
        {
            return "NOT DETERMINED YET. No checks are reported for this pull request at the moment. That does not mean there is " +
                "no CI: workflows may not have registered for the latest push yet. Do not conclude 'no CI' from one empty read; " +
                "check again shortly, or call with wait true, which only reports 'no CI' after several consecutive empty reads.";
        }

        var pending = observation.PendingChecks;
        if (pending.Count > 0)
        {
            var builder = new StringBuilder(160);
            builder.Append("NOT FINISHED. ").Append(N(pending.Count)).Append(" of ").Append(N(observation.Checks.Checks.Count))
                .Append(" check(s) have not settled (");
            AppendNames(builder, pending, 5);
            return builder.Append("). Call again later, or with wait true to wait for them.").ToString();
        }

        return "NOT DETERMINED YET. All checks have finished and passed, but GitHub has not computed the merge state yet (UNKNOWN). " +
            "Check again shortly, or call with wait true.";
    }

    private static void AppendFacts(StringBuilder text, PrChecksObservation observation)
    {
        var merge = observation.MergeState;
        text.Append("State: ").Append(PullRequestState(merge.State)).Append(". Draft: ").Append(merge.IsDraft ? "yes" : "no").Append(".\n");
        text.Append("Merge state: ").Append(MergeStateText(merge)).Append('\n');
        text.Append("Review decision: ").Append(ReviewDecision(merge)).Append('\n');

        var checks = observation.Checks;
        if (checks.NoneReported)
        {
            text.Append("Checks: none reported (not determined yet; this is not the same as no CI).\n");
            return;
        }

        var failing = observation.FailingChecks.Count;
        var pending = observation.PendingChecks.Count;
        text.Append("Checks: ").Append(N(checks.Checks.Count)).Append(" reported - ")
            .Append(N(checks.Count(PrCheckBucket.Pass))).Append(" passed, ")
            .Append(N(failing)).Append(" failed or cancelled, ")
            .Append(N(pending)).Append(" not settled, ")
            .Append(N(observation.SkippedCount)).Append(" skipped/neutral.\n");

        var ordered = checks.Checks
            .Select(static (check, index) => (check, index))
            .OrderBy(static pair => Rank(pair.check.Bucket))
            .ThenBy(static pair => pair.index);
        foreach (var (check, _) in ordered)
        {
            AppendCheck(text, check);
        }
    }

    private static void AppendCheck(StringBuilder text, PrCheck check)
    {
        text.Append("- [").Append(BucketLabel(check.Bucket)).Append("] ").Append(check.Name);
        if (check.Workflow.Length > 0 && !string.Equals(check.Workflow, check.Name, StringComparison.Ordinal))
        {
            text.Append(" (").Append(check.Workflow).Append(')');
        }

        if (check.State.Length > 0)
        {
            text.Append(" state=").Append(check.State);
        }

        if (check.Description.Length > 0)
        {
            text.Append(" - ").Append(OneLine(check.Description));
        }

        if (check.RunId is { } run)
        {
            text.Append(" run_id=").Append(run.ToString(CultureInfo.InvariantCulture));
        }

        if (check.JobId is { } job)
        {
            text.Append(" job_id=").Append(job.ToString(CultureInfo.InvariantCulture));
        }

        if (!string.IsNullOrEmpty(check.Link))
        {
            text.Append(' ').Append(check.Link);
        }

        text.Append('\n');
    }

    private static int Rank(PrCheckBucket bucket) => bucket switch
    {
        PrCheckBucket.Fail => 0,
        PrCheckBucket.Cancel => 1,
        PrCheckBucket.Pending => 2,
        PrCheckBucket.Unknown => 3,
        PrCheckBucket.Skipping => 4,
        _ => 5,
    };

    private static string BucketLabel(PrCheckBucket bucket) => bucket switch
    {
        PrCheckBucket.Pass => "pass",
        PrCheckBucket.Fail => "FAIL",
        PrCheckBucket.Pending => "pending",
        PrCheckBucket.Skipping => "skipped",
        PrCheckBucket.Cancel => "CANCELLED",
        _ => "unknown, not counted as passed",
    };

    private static string PullRequestState(PrState state) => state switch
    {
        PrState.Open => "open",
        PrState.Closed => "closed",
        PrState.Merged => "merged",
        _ => "unknown",
    };

    private static string MergeStateText(PrMergeState merge)
    {
        var raw = merge.MergeStateStatusText.Trim();
        return merge.MergeStateStatus switch
        {
            PrMergeStateStatus.Clean => "CLEAN - can be merged; every requirement is met.",
            PrMergeStateStatus.HasHooks => "HAS_HOOKS - mergeable with passing checks, but pre-receive hooks run at merge time and can still reject it.",
            PrMergeStateStatus.Behind => "BEHIND - the base branch has commits this branch lacks.",
            PrMergeStateStatus.Dirty => "DIRTY - the branch conflicts with the base branch.",
            PrMergeStateStatus.Blocked => "BLOCKED - a requirement is unmet, e.g. a required check or review.",
            PrMergeStateStatus.Unstable => "UNSTABLE - mergeable, but a non-required status is not passing.",
            PrMergeStateStatus.Draft => "DRAFT - the pull request is a draft.",
            _ when raw.Length == 0 => "not reported.",
            _ when string.Equals(raw, "UNKNOWN", StringComparison.OrdinalIgnoreCase) => "UNKNOWN - GitHub has not computed it yet.",
            _ => raw + " - a value this version does not recognise; treat it as not determined.",
        } + (merge.Mergeable == PrMergeable.Conflicting ? " Mergeable: CONFLICTING." : string.Empty);
    }

    private static string ReviewDecision(PrMergeState merge) => merge.ReviewDecision switch
    {
        PrReviewDecision.None => "none (no review required or given)",
        PrReviewDecision.Approved => "approved",
        PrReviewDecision.ChangesRequested => "changes requested",
        PrReviewDecision.ReviewRequired => "review required",
        _ => merge.ReviewDecisionText.Length > 0 ? merge.ReviewDecisionText + " (not recognised)" : "unknown",
    };

    private static void AppendNames(StringBuilder builder, IReadOnlyList<PrCheck> checks, int limit)
    {
        for (var index = 0; index < checks.Count && index < limit; index++)
        {
            if (index > 0)
            {
                builder.Append(", ");
            }

            builder.Append(checks[index].Name);
        }

        if (checks.Count > limit)
        {
            builder.Append(", and ").Append(N(checks.Count - limit)).Append(" more");
        }
    }

    private static string OneLine(string text)
    {
        var flat = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return flat.Length <= MaxDescriptionCharacters ? flat : flat[..MaxDescriptionCharacters] + "...";
    }

    private static string Duration(TimeSpan span)
    {
        var seconds = (long)Math.Round(span.TotalSeconds);
        if (seconds < 60)
        {
            return N(seconds) + "s";
        }

        var minutes = seconds / 60;
        var rest = seconds % 60;
        return minutes < 60
            ? $"{N(minutes)}m{N(rest)}s"
            : $"{N(minutes / 60)}h{N(minutes % 60)}m";
    }

    private static string N(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string N(long value) => value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>Turns a failed platform call into the tool error the model reads: what failed and the next step.</summary>
internal static class PrCheckFailure
{
    public static ToolResult ToToolResult(GitRemoteResult status, PrFailureReason reason) =>
        GitToolFailure.ToToolResult(status, Hint(status.Failure, reason));

    private static string Hint(GitRemoteFailureKind kind, PrFailureReason reason)
    {
        switch (reason)
        {
            case PrFailureReason.PlatformNotInstalled:
                return "Install the GitHub CLI (gh) and log in with gh auth login. Only the pull request tools need it; local git keeps working.";
            case PrFailureReason.PullRequestNotFound:
                return "There is no such pull request. Push the branch and open one with pr_create, or pass pr with the number.";
        }

        return kind == GitRemoteFailureKind.AuthenticationFailed
            ? "The GitHub CLI is not logged in or its token was rejected. Ask the user to run gh auth login, then retry."
            : GitToolFailure.DefaultHint(kind);
    }
}
