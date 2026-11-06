using System.Text.Json;
using Forge.Abstractions;

namespace Forge.Workspace;

/// <summary>Reports the checks and merge state of a pull request, once or after waiting for them to settle.</summary>
internal sealed class PrChecksTool(IPrPlatform platform, PrChecksPoller poller, Func<PrChecksPollerOptions> options) : PrCheckToolBase(
    PrCheckTools.ChecksName,
    "Report the CI checks and merge readiness of a pull request (default: the pull request of the current branch), " +
    "read just now. By default one snapshot: every check with its bucket and link (failed ones also with the run_id and " +
    "job_id for pr_ci_log), plus draft flag, review decision and merge state. An empty check list is reported as " +
    "'not determined yet', never as 'no CI'. With wait true it polls until the checks settle (interval and longest wait " +
    "are user settings; cancelled with the turn) and returns the verdict: ready, ready with a hooks caveat, checks failed " +
    "(returned as soon as one fails), behind, conflicting, blocked, unstable, draft, no CI (only after several consecutive " +
    "empty reads), merged or closed, or timed out. Waiting never syncs, fixes or merges anything; it only reports. Read " +
    "pr_workflow with phase \"checks\" for what to do with each result.",
    """
    {"type":"object","properties":{"pr":{"type":"integer","minimum":1,"description":"Pull request number; default the pull request of the current branch."},"wait":{"type":"boolean","description":"Poll until the checks settle instead of reading once; default false."}},"additionalProperties":false}
    """,
    ToolSideEffect.ReadOnly)
{
    protected override async ValueTask<ToolResult> InvokeCoreAsync(
        JsonElement arguments,
        ToolContext context,
        CancellationToken ct)
    {
        GitToolJson.EnsureObject(arguments, "pr", "wait");

        var requested = PrCheckJson.PositiveInt32(arguments, "pr");
        var wait = GitToolJson.Boolean(arguments, "wait", false);
        var directory = context.WorkingDirectory;

        int number;
        string? url = null;
        if (requested is { } given)
        {
            number = given;
        }
        else
        {
            var view = await platform.ViewPullRequestAsync(directory, null, ct).ConfigureAwait(false);
            if (!view.Success || view.Value is null)
            {
                return PrCheckFailure.ToToolResult(view.Status, view.Reason);
            }

            number = view.Value.Number;
            url = view.Value.Url;
        }

        if (wait)
        {
            var result = await poller.WaitAsync(directory, number, options(), null, ct).ConfigureAwait(false);
            return PrCheckText.WaitResult(number, url, result);
        }

        var read = await poller.ObserveAsync(directory, number, ct).ConfigureAwait(false);
        return read.Success && read.Value is { } observation
            ? new ToolResult(PrCheckText.Snapshot(number, url, observation))
            : PrCheckFailure.ToToolResult(read.Status, read.Reason);
    }
}
