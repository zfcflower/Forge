using System.Text;
using System.Text.Json;
using Forge.Abstractions;
using Forge.Core.Agents;

namespace Forge.Core.Tools;

/// <summary>
/// <c>request_review</c>: reads one change set through <see cref="IReviewChangeSetSource"/>, renders it as a review package
/// and hands it to an independent, read-only <c>Reviewer</c> sub-agent, then brings the findings and a normalised verdict
/// back into the session. It is read-only in nature: a read of git plus a read-only child agent, like <c>spawn_agent</c>.
/// The verdict blocks nothing by itself; the caller still has to act on it.
/// </summary>
public sealed class RequestReviewTool : ITool
{
    public const string Name = "request_review";

    private const string ReviewerKind = "Reviewer";
    private const string ResultSeparator = "\n\n---\n\n";

    private const string SchemaJson =
        """{"type":"object","properties":{"step_id":{"type":"string","description":"Checklist step id from the todo tool; the review then covers exactly that step's own commit."},"intent":{"type":"string","description":"What the change is supposed to do; required when step_id is absent."},"base":{"type":"string","description":"Commit or ref to review against; default the repository's default branch. Cannot be combined with step_id."},"paths":{"type":"array","items":{"type":"string"},"description":"Limit the change set to these paths."},"focus":{"type":"string","description":"What the reviewer should concentrate on."}},"additionalProperties":false}""";

    private const string Description =
        "Start an independent, read-only Reviewer sub-agent over one change set and get its deduplicated findings and a one-line verdict back. "
        + "With step_id the change set is exactly that checklist step's own commit; without it the change set is base (default: the repository's default branch) plus the uncommitted tree, and intent is then required. "
        + "paths narrows the change set and focus says what to concentrate on. "
        + "A request changes verdict must be acted on: fix the findings, commit, and call request_review again at the new HEAD. "
        + "This tool blocks nothing by itself, and an approving review does not replace the session's own git_diff read before pr_create. "
        + "The reviewer is read-only and never edits files.";

    private readonly SubAgentScheduler scheduler;
    private readonly IReviewChangeSetSource changeSets;
    private readonly ITodoStore? todos;
    private readonly Func<ReviewPackageLimits>? limits;

    public RequestReviewTool(SubAgentScheduler scheduler, IReviewChangeSetSource changeSets, ITodoStore? todos = null, Func<ReviewPackageLimits>? limits = null)
    {
        ArgumentNullException.ThrowIfNull(scheduler);
        ArgumentNullException.ThrowIfNull(changeSets);
        this.scheduler = scheduler;
        this.changeSets = changeSets;
        this.todos = todos;
        this.limits = limits;
        Definition = new ToolDefinition(Name, Description, SchemaJson);
    }

    public ToolDefinition Definition { get; }

    public ToolSideEffect SideEffect => ToolSideEffect.ReadOnly;

    public async ValueTask<ToolResult> InvokeAsync(ToolCall call, ToolContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(call);
        ArgumentNullException.ThrowIfNull(context);
        if (AgentScope.Current is not { } scope)
        {
            return new ToolResult("request_review is only available inside a running agent.", IsError: true);
        }

        // The built-in Reviewer does not list this tool, so the guard only catches an agent kind a user overrode that way:
        // a review that can ask for a review would nest until the depth limit, spending tokens to review itself.
        if (string.Equals(context.AgentName, ReviewerKind, StringComparison.OrdinalIgnoreCase))
        {
            return new ToolResult(
                "A review cannot ask for another review: the Reviewer is read-only and reports findings to the agent that asked. Report what you found instead.",
                IsError: true);
        }

        ReviewRequestArguments arguments;
        try
        {
            using var document = JsonDocument.Parse(call.ArgumentsJson);
            if (TryReadArguments(document.RootElement, out var error) is not { } parsed)
            {
                return new ToolResult(error!, IsError: true);
            }

            arguments = parsed;
        }
        catch (JsonException exception)
        {
            return new ToolResult($"{Name} arguments are invalid: {exception.Message}", IsError: true);
        }

        if (string.IsNullOrWhiteSpace(context.WorkingDirectory))
        {
            return new ToolResult("The session has no working directory, so there is nothing to review.", IsError: true);
        }

        if (arguments.StepId is null && arguments.Intent is null)
        {
            return new ToolResult(
                $"{Name} requires 'step_id' (review a checklist step's own commit) or 'intent' (say what the change is supposed to do).",
                IsError: true);
        }

        if (arguments.StepId is not null && arguments.Base is not null)
        {
            return new ToolResult(
                "step_id and base cannot be combined: a step review compares the step's own commit with its parent.",
                IsError: true);
        }

        var step = arguments.StepId is null ? null : await FindStepAsync(context.SessionId, arguments.StepId, ct).ConfigureAwait(false);
        if (arguments.StepId is not null && step is null)
        {
            return todos is null
                ? new ToolResult(
                    "This session has no checklist, so step_id cannot be resolved; pass 'intent' instead and review the branch against base.",
                    IsError: true)
                : new ToolResult(
                    $"No checklist step '{arguments.StepId}' in this session; step_id must be the id of one of this session's steps as the todo tool lists them.",
                    IsError: true);
        }

        var brief = step is null
            ? new ReviewBrief(arguments.Intent!, Focus: arguments.Focus)
            : new ReviewBrief(
                WithCommitNote(arguments.Intent ?? "Finish the checklist step: " + step.Title, step),
                step.Title,
                step.Summary,
                step.Verification,
                arguments.Focus);

        var request = new ReviewChangeSetRequest
        {
            WorkingDirectory = context.WorkingDirectory,
            Base = step is null ? arguments.Base : null,
            Commit = step?.CommitId,
            Paths = arguments.Paths,
        };

        var result = await changeSets.BuildAsync(request, ct).ConfigureAwait(false);
        if (result.ChangeSet is not { } changeSet)
        {
            return new ToolResult(
                "The change set could not be read: " + (result.Error ?? "the change set source returned no change set."),
                IsError: true);
        }

        if (changeSet.Files.Count == 0 && changeSet.Commits.Count == 0 && changeSet.DiffText.Length == 0)
        {
            return new ToolResult($"Nothing to review: {DescribeEmpty(changeSet, request.Commit is not null)}. No reviewer was started.");
        }

        var package = ReviewPackageText.Render(brief, changeSet, limits?.Invoke());
        SubAgentOutcome outcome;
        await using (scope.Permit.Detach(ct))
        {
            outcome = await scheduler
                .SpawnAsync(ReviewerKind, package.Text, resumeId: null, context, scope.Sink, scope.AgentId, ct)
                .ConfigureAwait(false);
        }

        if (outcome.IsError)
        {
            // The scheduler's refusals (depth limit, unknown kind, a stopped child) name the mechanism, not this call.
            return new ToolResult("The review could not be started: " + outcome.Text, IsError: true);
        }

        if (outcome.Stopped)
        {
            // A stopped reviewer's partial text can already hold a verdict line it wrote before it read everything (or one
            // quoted from its instructions); reading it would turn an unfinished review into an approval.
            return new ToolResult(
                new StringBuilder(160 + Math.Min(outcome.Text.Length, RequestReviewVerdict.MaxReviewChars))
                    .Append("The review was stopped before it finished, so it has no verdict and is not an approval: ask for another review before relying on it.")
                    .Append(ResultSeparator)
                    .Append(RequestReviewVerdict.Clamp(outcome.Text))
                    .Append(outcome.AgentId.Length > 0 ? "\n\n[agent_id: " + outcome.AgentId + "]" : string.Empty)
                    .ToString(),
                IsError: true);
        }

        if (string.IsNullOrWhiteSpace(outcome.Text))
        {
            return new ToolResult("The reviewer returned no text; nothing was reviewed.", IsError: true);
        }

        var verdict = RequestReviewVerdict.Parse(outcome.Text);
        var builder = new StringBuilder(384 + Math.Min(outcome.Text.Length, RequestReviewVerdict.MaxReviewChars));
        builder.Append(RequestReviewVerdict.VerdictLine(verdict)).Append('\n');
        if (string.IsNullOrWhiteSpace(changeSet.HeadCommit))
        {
            builder.Append("reviewed: unknown HEAD");
        }
        else
        {
            builder.Append("reviewed: ").Append(ReviewPackageText.ShortSha(changeSet.HeadCommit))
                .Append(" · ").Append(changeSet.Files.Count == 1 ? "1 file" : changeSet.Files.Count + " files");
            if (!string.IsNullOrWhiteSpace(changeSet.Base))
            {
                builder.Append(" · base ").Append(ReviewPackageText.ShortSha(changeSet.Base));
            }
        }

        if (package.DiffCut)
        {
            builder.Append("\nnote: the diff was cut and the reviewer was told how to read the rest with git_diff.");
        }

        builder.Append("\n\n").Append(RequestReviewVerdict.NextStep(verdict))
            .Append(ResultSeparator)
            .Append(RequestReviewVerdict.Clamp(outcome.Text));
        if (outcome.AgentId.Length > 0)
        {
            builder.Append("\n\n[agent_id: ").Append(outcome.AgentId).Append(']');
        }

        return new ToolResult(builder.ToString());
    }

    /// <summary>The one checklist item with this id, or null when the store has none or there is no store at all.</summary>
    private async ValueTask<TodoItem?> FindStepAsync(string sessionId, string id, CancellationToken ct)
    {
        if (todos is null)
        {
            return null;
        }

        var items = await todos.ListAsync(sessionId, ct).ConfigureAwait(false);
        for (var index = 0; index < items.Count; index++)
        {
            if (string.Equals(items[index].Id, id, StringComparison.Ordinal))
            {
                return items[index];
            }
        }

        return null;
    }

    /// <summary>
    /// A step whose commit never landed (step commits switched off, or nothing to commit) is reviewed as the branch plus the
    /// working tree. The package would otherwise read as if the reviewed commit were the step's own, so the intent says which
    /// change set the reviewer actually got.
    /// </summary>
    private static string WithCommitNote(string intent, TodoItem step) =>
        step.CommitId is null
            ? intent + " (this step has no commit of its own, so the change set is the branch and the uncommitted tree)"
            : intent;

    /// <summary>
    /// Why there was nothing to review. <paramref name="commitReview"/> says the caller asked for one commit, so the empty
    /// answer is about that commit against its parent rather than about a branch; saying the wrong one would read as if the
    /// review had happened and found nothing wrong.
    /// </summary>
    private static string DescribeEmpty(ReviewChangeSet changeSet, bool commitReview)
    {
        if (commitReview)
        {
            return $"commit {ReviewPackageText.ShortSha(changeSet.HeadCommit)} changes nothing against its parent";
        }

        return string.IsNullOrWhiteSpace(changeSet.Base)
            ? "the branch and the uncommitted tree contain no change"
            : $"the branch and the uncommitted tree against {ReviewPackageText.ShortSha(changeSet.Base)} contain no change";
    }

    /// <summary>
    /// Reads the arguments, rejecting anything this tool does not know. Returns null and sets <paramref name="error"/> on
    /// the first problem, so the caller reads one message instead of a list.
    /// </summary>
    private static ReviewRequestArguments? TryReadArguments(JsonElement root, out string? error)
    {
        error = null;
        if (root.ValueKind != JsonValueKind.Object)
        {
            error = $"{Name} arguments must be a JSON object.";
            return null;
        }

        foreach (var property in root.EnumerateObject())
        {
            if (!IsKnown(property.Name))
            {
                error = $"{Name} got an unknown property '{property.Name}'; it takes base, focus, intent, paths and step_id.";
                return null;
            }

            // A value of the wrong JSON type is an error, never a silent default: {"step_id": 5} must not turn into a
            // branch review that looks like the step review the caller asked for.
            if (!string.Equals(property.Name, "paths", StringComparison.Ordinal) && property.Value.ValueKind != JsonValueKind.String)
            {
                error = $"{Name} '{property.Name}' must be a string.";
                return null;
            }
        }

        IReadOnlyList<string> paths = [];
        if (root.TryGetProperty("paths", out var pathsElement))
        {
            if (pathsElement.ValueKind != JsonValueKind.Array)
            {
                error = $"{Name} 'paths' must be an array of strings.";
                return null;
            }

            var list = new List<string>(pathsElement.GetArrayLength());
            foreach (var element in pathsElement.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.String)
                {
                    error = $"{Name} 'paths' must be an array of strings.";
                    return null;
                }

                var path = element.GetString();
                if (!string.IsNullOrWhiteSpace(path))
                {
                    list.Add(path);
                }
            }

            paths = list;
        }

        return new ReviewRequestArguments(
            Present(JsonArguments.OptionalString(root, "step_id")),
            Present(JsonArguments.OptionalString(root, "intent")),
            Present(JsonArguments.OptionalString(root, "base")),
            Present(JsonArguments.OptionalString(root, "focus")),
            paths);
    }

    private static bool IsKnown(string name) =>
        string.Equals(name, "step_id", StringComparison.Ordinal)
        || string.Equals(name, "intent", StringComparison.Ordinal)
        || string.Equals(name, "base", StringComparison.Ordinal)
        || string.Equals(name, "paths", StringComparison.Ordinal)
        || string.Equals(name, "focus", StringComparison.Ordinal);

    /// <summary>An argument the caller left out or left empty, so an all-whitespace string counts as absent.</summary>
    private static string? Present(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed record ReviewRequestArguments(string? StepId, string? Intent, string? Base, string? Focus, IReadOnlyList<string> Paths);
}
