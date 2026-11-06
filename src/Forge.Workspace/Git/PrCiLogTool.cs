using System.Globalization;
using System.Text;
using System.Text.Json;
using Forge.Abstractions;

namespace Forge.Workspace;

/// <summary>Reads the log of the failed steps of one CI job so the agent can see why a check failed.</summary>
internal sealed class PrCiLogTool(IPrPlatform platform) : PrCheckToolBase(
    PrCheckTools.CiLogName,
    "Read the log of the failed steps of one CI job (gh run view --job <job_id> --log-failed). job_id, and run_id when " +
    "it is listed, come from a failed check reported by pr_checks. The log is returned whole; a very long log keeps its " +
    "end, where the failure is, and says so. Read pr_workflow with phase \"checks\" for how to act on it.",
    """
    {"type":"object","properties":{"job_id":{"type":"integer","minimum":1,"description":"Job id from a failed check of pr_checks."},"run_id":{"type":"integer","minimum":1,"description":"Workflow run id from the same check; optional."}},"required":["job_id"],"additionalProperties":false}
    """,
    ToolSideEffect.ReadOnly)
{
    protected override async ValueTask<ToolResult> InvokeCoreAsync(
        JsonElement arguments,
        ToolContext context,
        CancellationToken ct)
    {
        GitToolJson.EnsureObject(arguments, "job_id", "run_id");

        var jobId = PrCheckJson.RequiredPositiveInt64(arguments, "job_id");
        var runId = PrCheckJson.PositiveInt64(arguments, "run_id");

        var result = await platform.GetFailedJobLogAsync(context.WorkingDirectory, runId, jobId, ct).ConfigureAwait(false);
        if (!result.Success || result.Value is not { } log)
        {
            return PrCheckFailure.ToToolResult(result.Status, result.Reason);
        }

        var text = new StringBuilder(log.Text.Length + 128);
        text.Append("Failed-step log of job ").Append(jobId.ToString(CultureInfo.InvariantCulture));
        if (runId is { } run)
        {
            text.Append(" (run ").Append(run.ToString(CultureInfo.InvariantCulture)).Append(')');
        }

        text.Append(":\n");
        if (log.Text.Trim().Length == 0)
        {
            text.Append(
                "(no failed-step output). The job may not have failed, may still be running, may have been cancelled, " +
                "or its logs may have expired; check its bucket with pr_checks.\n");
            return new ToolResult(text.ToString());
        }

        if (log.Truncated)
        {
            text.Append("(the log exceeds the capture limit: only its end is shown)\n");
        }

        text.Append(log.Text);
        return new ToolResult(text.ToString());
    }
}
