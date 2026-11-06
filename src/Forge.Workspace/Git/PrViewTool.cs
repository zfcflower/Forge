using System.Globalization;
using System.Text;
using System.Text.Json;
using Forge.Abstractions;

namespace Forge.Workspace;

/// <summary>Reads one pull request fresh from the platform: identity, state, branches, merge state and review decision.</summary>
internal sealed class PrViewTool(GitCli git, IPrPlatform platform) : PrLifecycleToolBase(
    git,
    platform,
    PrTools.ViewName,
    "Show a pull request as the platform reports it right now: number, state, URL, title, head and base branch, draft " +
    "flag, merge state and review decision, and the description. pr is a number, URL or branch name; without it the " +
    "pull request of the current branch is shown. When there is none, the result says so. Read pr_workflow with phase " +
    "'checks' to interpret the merge state (CLEAN, BEHIND, DIRTY, BLOCKED and the rest).",
    """
    {"type":"object","properties":{"pr":{"type":["integer","string"],"description":"Pull request number, URL or branch; default the current branch's pull request."}},"additionalProperties":false}
    """,
    ToolSideEffect.ReadOnly)
{
    private const int BodyLimit = 8000;

    protected override async ValueTask<ToolResult> InvokeCoreAsync(
        JsonElement arguments,
        ToolContext context,
        CancellationToken ct)
    {
        GitToolJson.EnsureObject(arguments, "pr");
        var selector = ReadSelector(arguments);
        var directory = context.WorkingDirectory;

        if (await RequireReadyAsync(directory, ct).ConfigureAwait(false) is { } notReady)
        {
            return notReady;
        }

        var view = await Platform.ViewPullRequestAsync(directory, selector, ct).ConfigureAwait(false);
        if (view.PullRequestNotFound)
        {
            return new ToolResult(
                selector is null
                    ? "There is no pull request for the current branch. Create one with pr_create (read pr_workflow phase 'creating' first)."
                    : $"No pull request matches '{selector}'.");
        }

        return view.Success && view.Value is { } info
            ? new ToolResult(Format(info))
            : PrLifecycleFailure.FromPlatform(view);
    }

    internal static string Format(PullRequestInfo info)
    {
        var text = new StringBuilder(512);
        text.Append("PR #").Append(info.Number.ToString(CultureInfo.InvariantCulture))
            .Append(" (").Append(StateName(info.State)).Append(info.IsDraft ? ", draft" : string.Empty).Append(") ")
            .Append(info.Url).Append('\n');
        text.Append("Title: ").Append(info.Title).Append('\n');
        text.Append(info.HeadRefName).Append(" -> ").Append(info.BaseRefName)
            .Append(" (head ").Append(PrLifecycleFailure.Short(info.HeadRefOid)).Append(")\n");
        text.Append("Merge state: ").Append(MergeStateName(info.MergeStateStatus))
            .Append(" - ").Append(MergeStateMeaning(info.MergeStateStatus)).Append('\n');
        text.Append("Review decision: ").Append(ReviewName(info.ReviewDecision));

        var body = info.Body.Trim();
        if (body.Length == 0)
        {
            return text.Append("\nBody: (empty)").ToString();
        }

        text.Append("\nBody:\n");
        if (body.Length > BodyLimit)
        {
            text.Append(body, 0, BodyLimit)
                .Append("\n... [body cut after ").Append(BodyLimit.ToString(CultureInfo.InvariantCulture))
                .Append(" of ").Append(body.Length.ToString(CultureInfo.InvariantCulture)).Append(" characters]");
        }
        else
        {
            text.Append(body);
        }

        return text.ToString();
    }

    internal static string StateName(PrState state) => state switch
    {
        PrState.Open => "open",
        PrState.Closed => "closed",
        PrState.Merged => "merged",
        _ => "unknown state",
    };

    internal static string MergeStateName(PrMergeStateStatus status) => status switch
    {
        PrMergeStateStatus.Clean => "CLEAN",
        PrMergeStateStatus.HasHooks => "HAS_HOOKS",
        PrMergeStateStatus.Behind => "BEHIND",
        PrMergeStateStatus.Dirty => "DIRTY",
        PrMergeStateStatus.Blocked => "BLOCKED",
        PrMergeStateStatus.Unstable => "UNSTABLE",
        PrMergeStateStatus.Draft => "DRAFT",
        _ => "UNKNOWN",
    };

    private static string MergeStateMeaning(PrMergeStateStatus status) => status switch
    {
        PrMergeStateStatus.Clean => "mergeable, nothing blocks it",
        PrMergeStateStatus.HasHooks => "mergeable with passing checks; pre-receive hooks still run at merge time",
        PrMergeStateStatus.Behind => "the base moved on; the branch needs updating (pr_sync) once you decided to",
        PrMergeStateStatus.Dirty => "conflicts with the base",
        PrMergeStateStatus.Blocked => "a required check, review or rule is not satisfied",
        PrMergeStateStatus.Unstable => "mergeable, but non-required checks are failing",
        PrMergeStateStatus.Draft => "still a draft",
        _ => "not computed yet (or not recognised); read again, do not treat it as good",
    };

    private static string ReviewName(PrReviewDecision decision) => decision switch
    {
        PrReviewDecision.Approved => "APPROVED",
        PrReviewDecision.ChangesRequested => "CHANGES_REQUESTED",
        PrReviewDecision.ReviewRequired => "REVIEW_REQUIRED",
        PrReviewDecision.None => "none (no review required or given)",
        _ => "UNKNOWN",
    };

    private static string? ReadSelector(JsonElement arguments)
    {
        if (!arguments.TryGetProperty("pr", out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        switch (value.ValueKind)
        {
            case JsonValueKind.Number:
                if (!value.TryGetInt32(out var number) || number <= 0)
                {
                    throw new ArgumentException("'pr' must be a positive pull request number.");
                }

                return number.ToString(CultureInfo.InvariantCulture);

            case JsonValueKind.String:
                var text = value.GetString()!.Trim();
                if (text.Length == 0)
                {
                    return null;
                }

                if (text[0] == '-')
                {
                    throw new ArgumentException("'pr' must be a number, URL or branch name, not an option.");
                }

                return text;

            default:
                throw new ArgumentException("'pr' must be a number or a string.");
        }
    }
}
