using System.Text;
using System.Text.Json;
using Forge.Abstractions;

namespace Forge.Core.Agents;

/// <summary>
/// The session's checklist. Reading it is a read-only call; adding, updating and removing items write, so the
/// approval policy sees the side effect of the call instead of the tool's worst case.
/// </summary>
public sealed class TodoTool : ITool, IToolSideEffectResolver
{
    public const string Name = "todo";

    private const string SchemaJson =
        """{"type":"object","properties":{"operation":{"type":"string","enum":["list","add","update","remove"],"description":"list reads the checklist; add, update and remove change it."},"id":{"type":"string","description":"Item id from a previous list; required by update and remove."},"title":{"type":"string","description":"Item title for add, or the new title for update."},"status":{"type":"string","enum":["pending","in_progress","done"],"description":"New status for update."},"note":{"type":"string","description":"Short note for add or update."},"after_id":{"type":"string","description":"Insert a new item right after this item; default appends."}},"required":["operation"],"additionalProperties":false}""";

    private readonly ITodoStore store;

    public TodoTool(ITodoStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        this.store = store;
        Definition = new ToolDefinition(
            Name,
            "Read and edit the checklist of this session, which the user sees and edits too. Operations: list, add, update (title, status, note), remove.",
            SchemaJson);
    }

    public ToolDefinition Definition { get; }

    public ToolSideEffect SideEffect => ToolSideEffect.Write;

    public ToolSideEffect Resolve(ToolCall call) => IsList(call) ? ToolSideEffect.ReadOnly : ToolSideEffect.Write;

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
        try
        {
            using var document = JsonDocument.Parse(call.ArgumentsJson);
            var root = document.RootElement;
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
                "update" => await UpdateAsync(context.SessionId, id, title, status, note, ct).ConfigureAwait(false),
                "remove" => await RemoveAsync(context.SessionId, id, ct).ConfigureAwait(false),
                _ => new ToolResult($"{Name} operation must be one of list, add, update, remove.", IsError: true),
            };
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new ToolResult($"{Name} failed: {exception.Message}", IsError: true);
        }
    }

    private bool IsList(ToolCall call)
    {
        try
        {
            using var document = JsonDocument.Parse(call.ArgumentsJson);
            return document.RootElement.TryGetProperty("operation", out var element)
                   && element.ValueKind == JsonValueKind.String
                   && string.Equals(element.GetString(), "list", StringComparison.OrdinalIgnoreCase);
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
            AppendItem(builder, index + 1, items[index]);
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

    private async ValueTask<ToolResult> UpdateAsync(string sessionId, string? id, string? title, string? status, string? note, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return new ToolResult($"{Name} update requires the 'id' of an item.", IsError: true);
        }

        if (title is null && status is null && note is null)
        {
            return new ToolResult($"{Name} update requires 'title', 'status' or 'note'.", IsError: true);
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

        if (!await ContainsAsync(sessionId, id, ct).ConfigureAwait(false))
        {
            return NotInSession(id);
        }

        var item = await store.UpdateAsync(id, title, parsed, note, ct).ConfigureAwait(false);
        if (item is null)
        {
            return new ToolResult($"No checklist item '#{id}' exists.", IsError: true);
        }

        var builder = new StringBuilder(96);
        builder.Append("Updated #").Append(item.Id).Append(": ");
        AppendItem(builder, 0, item);
        return new ToolResult(builder.ToString());
    }

    private async ValueTask<ToolResult> RemoveAsync(string sessionId, string? id, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return new ToolResult($"{Name} remove requires the 'id' of an item.", IsError: true);
        }

        if (!await ContainsAsync(sessionId, id, ct).ConfigureAwait(false))
        {
            return NotInSession(id);
        }

        return await store.RemoveAsync(id, ct).ConfigureAwait(false)
            ? new ToolResult($"Removed #{id}.")
            : new ToolResult($"No checklist item '#{id}' exists.", IsError: true);
    }

    private async ValueTask<bool> ContainsAsync(string sessionId, string id, CancellationToken ct)
    {
        var items = await store.ListAsync(sessionId, ct).ConfigureAwait(false);
        for (var index = 0; index < items.Count; index++)
        {
            if (string.Equals(items[index].Id, id, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static ToolResult NotInSession(string id) =>
        new($"No todo item '{id}' in this session.", IsError: true);

    private static void AppendItem(StringBuilder builder, int position, TodoItem item)
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
