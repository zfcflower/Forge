using System.Globalization;
using System.Text;
using Forge.App.Services;
using Forge.Workspace;

namespace Forge.App.Git;

/// <summary>
/// One failed check with the tail of its job log, as the checks panel read it: <see cref="Tail"/> is empty and
/// <see cref="Error"/> says why when the log could not be read, and <see cref="Truncated"/> means the platform layer had
/// already cut a very long log at its capture limit (the failure is at the end, so the tail is the part that matters).
/// </summary>
internal sealed record PrChecksJobLog(PrCheck Check, string Tail, bool Truncated, string? Error);

/// <summary>
/// The message the checks panel hands to the agent when the user asks it to fix the failing checks: the same English
/// digest the <c>pr_checks</c> tool returns (verdict first, then every check with the run and job ids), the tail of each
/// failed job's log, and one closing instruction. It is model input, not UI text, so it is English and lives here rather
/// than in a string table.
/// <para>Bounds: each log keeps at most the last <c>tailLines</c> lines (the user's setting) and
/// <see cref="MaxJobLogCharacters"/> – the same trim the CI fix chain uses (<c>CiChain.Tail</c>) – and the whole message
/// spends at most <see cref="MaxTotalLogCharacters"/> on logs: the remaining ones are named and left out, so a pull request
/// with many red jobs cannot blow up the turn's context.</para>
/// </summary>
internal static class PrChecksFixMessage
{
    /// <summary>Characters kept of one job log, whatever the line setting says (a minified stack trace is one line).</summary>
    internal const int MaxJobLogCharacters = 4000;

    /// <summary>Characters the whole log section may take; the rest of the message is the digest and the instruction.</summary>
    internal const int MaxTotalLogCharacters = 20000;

    /// <summary>Characters kept of one "the log could not be read" reason; gh's stderr can be long.</summary>
    internal const int MaxErrorCharacters = 500;

    /// <summary>
    /// Builds the message for <paramref name="number"/>. <paramref name="logs"/> is the list the panel loaded (one entry
    /// per failing check it could ask about); a failing check with no entry is named as missing rather than silently
    /// dropped.
    /// </summary>
    public static string Build(
        int number,
        string? url,
        PrChecksObservation observation,
        IReadOnlyList<PrChecksJobLog> logs,
        int tailLines)
    {
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentNullException.ThrowIfNull(logs);
        ArgumentOutOfRangeException.ThrowIfLessThan(tailLines, 1);

        var message = new StringBuilder(4096);
        message.Append(PrCheckText.Snapshot(number, url, observation));
        message.Append("\n== Failed job logs ==\n");

        var spent = 0;
        List<PrCheck>? omitted = null;
        foreach (var log in logs)
        {
            if (log.Error is { Length: > 0 } error)
            {
                // gh's reason can be its whole stderr; one line of bounded length is what the agent needs.
                var reason = error.Length > MaxErrorCharacters ? error[..MaxErrorCharacters] + "..." : error;
                message.Append("### ").Append(log.Check.Name).Append('\n');
                message.Append("The log could not be read: ").Append(reason.ReplaceLineEndings(" ")).Append("\n\n");
                continue;
            }

            var tail = TextTail.Keep(log.Tail, tailLines, MaxJobLogCharacters);

            if (spent + tail.Length > MaxTotalLogCharacters)
            {
                (omitted ??= []).Add(log.Check);
                continue;
            }

            spent += tail.Length;
            message.Append("### ").Append(Header(log.Check)).Append('\n');
            message.Append(tail.Length == 0 ? "(empty log)\n" : tail).Append('\n');
            if (log.Truncated)
            {
                message.Append("(the platform cut this log at its capture limit; the tail is shown)\n");
            }

            message.Append('\n');
        }

        if (omitted is { Count: > 0 })
        {
            message.Append(omitted.Count.ToString(CultureInfo.InvariantCulture))
                .Append(" more failed job log(s) were left out: the message is bounded. Read them with pr_ci_log if they matter:\n");
            foreach (var check in omitted)
            {
                message.Append("- ").Append(Header(check)).Append('\n');
            }

            message.Append('\n');
        }

        message.Append("== What to do ==\n");
        message.Append(
            "Fix the cause of every failing check above, then commit on the current branch and push it to its upstream so " +
            "the checks run again. Do not push to the base branch, do not merge the pull request, and do not re-run a check " +
            "without changing anything.\n");
        return message.ToString();
    }

    /// <summary>One check's heading line: name, description, and the ids <c>pr_ci_log</c> takes.</summary>
    private static string Header(PrCheck check)
    {
        var builder = new StringBuilder(check.Name);
        if (check.Workflow.Length > 0)
        {
            builder.Append(" (").Append(check.Workflow).Append(')');
        }

        if (check.Description.Length > 0)
        {
            builder.Append(" - ").Append(check.Description.ReplaceLineEndings(" "));
        }

        if (check.RunId is { } run)
        {
            builder.Append(" run_id=").Append(run.ToString(CultureInfo.InvariantCulture));
        }

        if (check.JobId is { } job)
        {
            builder.Append(" job_id=").Append(job.ToString(CultureInfo.InvariantCulture));
        }

        return builder.ToString();
    }
}
