using System.Text;
using System.Text.Json;
using Forge.Abstractions;

namespace Forge.Core.Agents;

/// <summary>
/// The session's checklist and plan summary. Reading them is a read-only call; adding, updating, removing and writing
/// the plan summary change state, so the approval policy sees the side effect of the call instead of the tool's worst
/// case. When a step committer is configured, marking an item done also commits that step; the commit is a local,
/// undoable write to the session's own worktree and so rides along with the <c>todo</c> call instead of asking again.
/// </summary>
public sealed class TodoTool : ITool, IToolSideEffectResolver
{
    public const string Name = "todo";

    private const int ShortCommitLength = 7;
    private const int ListFieldLimit = 120;
    private const int ListErrorLimit = 200;

    private const string SchemaJson =
        """{"type":"object","properties":{"operation":{"type":"string","enum":["list","add","update","remove","plan_summary_get","plan_summary_set"],"description":"list reads the checklist; add, update and remove change it; plan_summary_get reads the session's plan summary and plan_summary_set replaces it."},"id":{"type":"string","description":"Item id from a previous list; required by update and remove."},"title":{"type":"string","description":"Item title for add, or the new title for update."},"status":{"type":"string","enum":["pending","in_progress","done"],"description":"New status for update. done commits every uncommitted change in the worktree as this step."},"note":{"type":"string","description":"Short note for add or update."},"commit_subject":{"type":"string","description":"Commit subject line of the step, written in the repo's commit style (read git_log first); the item title is used when absent. Update only; an empty string clears it."},"summary":{"type":"string","description":"What the step changed; becomes the commit body and the step's entry in the PR plan. Update only; an empty string clears it."},"verification":{"type":"string","description":"How the step was verified (tests run, checks made); kept on the item and in the PR plan, not part of the commit message. Update only; an empty string clears it."},"text":{"type":"string","description":"Plan summary for plan_summary_set, with Summary, Changes and Testing sections; it becomes the PR description."},"after_id":{"type":"string","description":"Insert a new item right after this item; default appends."}},"required":["operation"],"additionalProperties":false}""";

    private const string Description =
        "Read and edit the checklist of this session, which the user sees and edits too. Operations: list, add, update (title, status, note, commit_subject, summary, verification), remove, plan_summary_get, plan_summary_set. "
        + "Marking an item done commits every uncommitted change in the worktree as that step, so mark it done only when the step is finished and verified. "
        + "Before you write a commit_subject, call git_log to read the last ~20 commits and follow the repo's style (Conventional Commits if it uses them); without a commit_subject the item title is the commit subject. "
        + "The summary becomes the commit body and the step's entry in the PR plan; verification records how the step was verified and is not part of the commit message. "
        + "A failed step commit does not undo done: fix the cause and call update with status done again. "
        + "plan_summary_set stores the session's plan summary (Summary, Changes and Testing sections) that becomes the PR description; plan_summary_get reads it.";

    private readonly ITodoStore store;
    private readonly IStepCommitter? committer;

    public TodoTool(ITodoStore store, IStepCommitter? committer = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        this.store = store;
        this.committer = committer;
        Definition = new ToolDefinition(Name, Description, SchemaJson);
    }

    public ToolDefinition Definition { get; }

    public ToolSideEffect SideEffect => ToolSideEffect.Write;

    public ToolSideEffect Resolve(ToolCall call) => IsReadOnly(call) ? ToolSideEffect.ReadOnly : ToolSideEffect.Write;

    public async ValueTask<ToolResult> InvokeAsync(ToolCall call, ToolContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(call);
        ArgumentNullException.ThrowIfNull(context);
        if (string.IsNullOrWhiteSpace(context.SessionId))
        {
            return new ToolResult($"{Name} needs a session.", IsError: true);
        }

        string operation;
        string? id;
        string? title;
        string? status;
        string? note;
        string? afterId;
        string? commitSubject;
        string? summary;
        string? verification;
        string? text;
        try
        {
            using var document = JsonDocument.Parse(call.ArgumentsJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return new ToolResult($"{Name} arguments must be a JSON object.", IsError: true);
            }

            if (!root.TryGetProperty("operation", out var operationElement) || operationElement.ValueKind != JsonValueKind.String)
            {
                return new ToolResult($"{Name} requires an 'operation' string.", IsError: true);
            }

            operation = operationElement.GetString()!.Trim().ToLowerInvariant();
            id = JsonArguments.OptionalString(root, "id");
            title = JsonArguments.OptionalString(root, "title");
            status = JsonArguments.OptionalString(root, "status");
            note = JsonArguments.OptionalString(root, "note");
            afterId = JsonArguments.OptionalString(root, "after_id");
            commitSubject = JsonArguments.OptionalString(root, "commit_subject");
            summary = JsonArguments.OptionalString(root, "summary");
            verification = JsonArguments.OptionalString(root, "verification");
            text = JsonArguments.OptionalString(root, "text");
        }
        catch (JsonException exception)
        {
            return new ToolResult($"{Name} arguments are invalid: {exception.Message}", IsError: true);
        }

        try
        {
            return operation switch
            {
                "list" => await ListAsync(context.SessionId, ct).ConfigureAwait(false),
                "add" => await AddAsync(context.SessionId, title, note, afterId, ct).ConfigureAwait(false),
                "update" => await UpdateAsync(context.SessionId, id, title, status, note, commitSubject, summary, verification, ct).ConfigureAwait(false),
                "remove" => await RemoveAsync(context.SessionId, id, ct).ConfigureAwait(false),
                "plan_summary_get" => await PlanSummaryGetAsync(context.SessionId, ct).ConfigureAwait(false),
                "plan_summary_set" => await PlanSummarySetAsync(context.SessionId, text, ct).ConfigureAwait(false),
                _ => new ToolResult($"{Name} operation must be one of list, add, update, remove, plan_summary_get, plan_summary_set.", IsError: true),
            };
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new ToolResult($"{Name} failed: {exception.Message}", IsError: true);
        }
    }

    private bool IsReadOnly(ToolCall call)
    {
        try
        {
            using var document = JsonDocument.Parse(call.ArgumentsJson);

            // Valid JSON that is not an object (an array, a scalar, null) cannot be a read; InvokeAsync rejects it, and the
            // approval policy sees the conservative side effect instead of the resolver throwing out of the agent loop.
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("operation", out var element)
                || element.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            var operation = element.GetString();
            return string.Equals(operation, "list", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(operation, "plan_summary_get", StringComparison.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            return true;
        }
    }

    private async ValueTask<ToolResult> ListAsync(string sessionId, CancellationToken ct)
    {
        var items = await store.ListAsync(sessionId, ct).ConfigureAwait(false);
        if (items.Count == 0)
        {
            return new ToolResult("The checklist is empty.");
        }

        var builder = new StringBuilder(items.Count * 48);
        builder.Append(items.Count).Append(items.Count == 1 ? " item" : " items").Append(":\n");
        for (var index = 0; index < items.Count; index++)
        {
            AppendItem(builder, index + 1, items[index], details: true);
            builder.Append('\n');
        }

        return new ToolResult(builder.ToString().TrimEnd('\n'));
    }

    private async ValueTask<ToolResult> AddAsync(string sessionId, string? title, string? note, string? afterId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return new ToolResult($"{Name} add requires a non-empty 'title'.", IsError: true);
        }

        var item = await store.AddAsync(sessionId, title, note, afterId, ct).ConfigureAwait(false);
        return new ToolResult($"Added #{item.Id}: {item.Title}");
    }

    private async ValueTask<ToolResult> UpdateAsync(
        string sessionId,
        string? id,
        string? title,
        string? status,
        string? note,
        string? commitSubject,
        string? summary,
        string? verification,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return new ToolResult($"{Name} update requires the 'id' of an item.", IsError: true);
        }

        if (title is null && status is null && note is null && commitSubject is null && summary is null && verification is null)
        {
            return new ToolResult(
                $"{Name} update requires 'title', 'status', 'note', 'commit_subject', 'summary' or 'verification'.",
                IsError: true);
        }

        TodoStatus? parsed = null;
        if (status is not null)
        {
            if (!TryParseStatus(status, out var value))
            {
                return new ToolResult($"{Name} status must be pending, in_progress or done.", IsError: true);
            }

            parsed = value;
        }

        // The same read tells whether the item belongs to this session and whether this call finishes it.
        var before = await FindAsync(sessionId, id, ct).ConfigureAwait(false);
        if (before is null)
        {
            return NotInSession(id);
        }

        var step = commitSubject is null && summary is null && verification is null
            ? null
            : new TodoStepFields(commitSubject, summary, verification);
        var item = await store.UpdateAsync(id, title, parsed, note, step, ct).ConfigureAwait(false);
        if (item is null)
        {
            return new ToolResult($"No checklist item '#{id}' exists.", IsError: true);
        }

        var builder = new StringBuilder(96);
        builder.Append("Updated #").Append(item.Id).Append(": ");
        AppendItem(builder, 0, item, details: false);

        // A step commits when it becomes done; an already-done step with a failed commit is retried by saying done again.
        if (committer is not null
            && parsed == TodoStatus.Done
            && item.Status == TodoStatus.Done
            && (before.Status != TodoStatus.Done || before.CommitError is not null))
        {
            await AppendStepCommitAsync(builder, committer, item, ct).ConfigureAwait(false);
        }

        return new ToolResult(builder.ToString());
    }

    private async ValueTask AppendStepCommitAsync(StringBuilder builder, IStepCommitter stepCommitter, TodoItem item, CancellationToken ct)
    {
        StepCommitResult result;
        try
        {
            result = await stepCommitter.CommitStepAsync(item, ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            result = new StepCommitResult(StepCommitOutcome.Failed, null, exception.Message);
            await TryRecordFailureAsync(item.Id, exception.Message, ct).ConfigureAwait(false);
        }

        builder.Append("\nStep commit: ");
        switch (result.Outcome)
        {
            case StepCommitOutcome.Committed:
                builder.Append("committed");
                if (!string.IsNullOrEmpty(result.CommitId))
                {
                    builder.Append(' ').Append(result.CommitId.AsSpan(0, Math.Min(ShortCommitLength, result.CommitId.Length)));
                }
                else if (!string.IsNullOrWhiteSpace(result.Message))
                {
                    builder.Append(" (").Append(result.Message).Append(')');
                }

                break;
            case StepCommitOutcome.NoChanges:
                builder.Append("no changes");
                break;
            case StepCommitOutcome.Skipped:
                builder.Append("skipped");
                if (!string.IsNullOrWhiteSpace(result.Message))
                {
                    builder.Append(" (").Append(result.Message).Append(')');
                }

                break;
            default:
                builder.Append("FAILED: ")
                    .Append(string.IsNullOrWhiteSpace(result.Message) ? "the commit failed without a message" : result.Message)
                    .Append(" — the item stays done; fix the cause and call update with status done again or let the user retry");
                break;
        }
    }

    /// <summary>
    /// A committer that throws instead of reporting leaves no trace on the item, so the retry path would not open; record
    /// the failure here. Best effort: the tool result already carries the failure.
    /// </summary>
    private async ValueTask TryRecordFailureAsync(string id, string message, CancellationToken ct)
    {
        try
        {
            await store.SetCommitResultAsync(id, null, string.IsNullOrWhiteSpace(message) ? "the commit failed" : message, ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
        }
    }

    private async ValueTask<ToolResult> RemoveAsync(string sessionId, string? id, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return new ToolResult($"{Name} remove requires the 'id' of an item.", IsError: true);
        }

        if (await FindAsync(sessionId, id, ct).ConfigureAwait(false) is null)
        {
            return NotInSession(id);
        }

        return await store.RemoveAsync(id, ct).ConfigureAwait(false)
            ? new ToolResult($"Removed #{id}.")
            : new ToolResult($"No checklist item '#{id}' exists.", IsError: true);
    }

    private async ValueTask<ToolResult> PlanSummaryGetAsync(string sessionId, CancellationToken ct)
    {
        var summary = await store.GetPlanSummaryAsync(sessionId, ct).ConfigureAwait(false);
        return summary is null
            ? new ToolResult("No plan summary has been written for this session yet.")
            : new ToolResult(summary.Text);
    }

    private async ValueTask<ToolResult> PlanSummarySetAsync(string sessionId, string? text, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new ToolResult($"{Name} plan_summary_set requires a non-empty 'text'.", IsError: true);
        }

        var summary = await store.SetPlanSummaryAsync(sessionId, text, ct).ConfigureAwait(false);
        return new ToolResult($"Plan summary saved ({summary.Text.Length} characters).");
    }

    private async ValueTask<TodoItem?> FindAsync(string sessionId, string id, CancellationToken ct)
    {
        var items = await store.ListAsync(sessionId, ct).ConfigureAwait(false);
        for (var index = 0; index < items.Count; index++)
        {
            if (string.Equals(items[index].Id, id, StringComparison.Ordinal))
            {
                return items[index];
            }
        }

        return null;
    }

    private static ToolResult NotInSession(string id) =>
        new($"No todo item '{id}' in this session.", IsError: true);

    private static void AppendItem(StringBuilder builder, int position, TodoItem item, bool details)
    {
        if (position > 0)
        {
            builder.Append(position).Append(". ");
        }

        builder.Append(item.Status switch
        {
            TodoStatus.Done => "[x] ",
            TodoStatus.InProgress => "[~] ",
            _ => "[ ] ",
        });
        builder.Append('#').Append(item.Id).Append(' ').Append(item.Title);
        if (!string.IsNullOrWhiteSpace(item.Note))
        {
            builder.Append(" — ").Append(item.Note);
        }

        if (!details)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(item.CommitId))
        {
            builder.Append(" [commit ").Append(item.CommitId.AsSpan(0, Math.Min(ShortCommitLength, item.CommitId.Length))).Append(']');
        }
        else if (!string.IsNullOrWhiteSpace(item.CommitError))
        {
            builder.Append(" [commit failed: ");
            AppendCompact(builder, item.CommitError, ListErrorLimit);
            builder.Append(']');
        }

        AppendField(builder, "commit_subject", item.CommitSubject);
        AppendField(builder, "summary", item.Summary);
        AppendField(builder, "verification", item.Verification);
    }

    private static void AppendField(StringBuilder builder, string label, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        builder.Append("\n   ").Append(label).Append(": ");
        AppendCompact(builder, value, ListFieldLimit);
    }

    /// <summary>Appends <paramref name="text"/> on one line (whitespace runs collapse to a space), cut at <paramref name="limit"/> characters.</summary>
    private static void AppendCompact(StringBuilder builder, string text, int limit)
    {
        var written = 0;
        var gap = false;
        for (var index = 0; index < text.Length; index++)
        {
            var current = text[index];
            if (char.IsWhiteSpace(current))
            {
                gap = written > 0;
                continue;
            }

            if (written + (gap ? 2 : 1) > limit)
            {
                if (char.IsHighSurrogate(builder[builder.Length - 1]))
                {
                    builder.Length--;
                }

                builder.Append('…');
                return;
            }

            if (gap)
            {
                builder.Append(' ');
                written++;
                gap = false;
            }

            builder.Append(current);
            written++;
        }
    }

    private static bool TryParseStatus(string value, out TodoStatus status)
    {
        switch (value.Trim().ToLowerInvariant())
        {
            case "pending":
            case "todo":
            case "open":
                status = TodoStatus.Pending;
                return true;
            case "in_progress":
            case "in-progress":
            case "inprogress":
            case "doing":
            case "running":
                status = TodoStatus.InProgress;
                return true;
            case "done":
            case "completed":
            case "closed":
                status = TodoStatus.Done;
                return true;
            default:
                status = TodoStatus.Pending;
                return false;
        }
    }
}
