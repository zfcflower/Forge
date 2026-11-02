using System.Text;
using System.Text.Json;
using Forge.Abstractions;

namespace Forge.Core.Agents;

/// <summary>
/// Delegates one scoped task to a sub-agent. It is read-only on purpose: several spawn calls in one turn
/// then run concurrently, and the caller gives its parallelism permit back for as long as they run.
/// </summary>
public sealed class SpawnAgentTool : ITool
{
    public const string Name = "spawn_agent";

    private const string SchemaJson =
        """{"type":"object","properties":{"kind":{"type":"string","description":"Agent kind to run, from the kinds listed in this tool's description."},"task":{"type":"string","description":"The complete, self-contained task for the sub-agent."},"resume_id":{"type":"string","description":"Id of a finished sub-agent of the same kind whose context should continue instead of starting fresh."}},"required":["kind","task"],"additionalProperties":false}""";

    private readonly SubAgentScheduler scheduler;

    public SpawnAgentTool(SubAgentScheduler scheduler, IEnumerable<AgentDefinition> kinds)
    {
        ArgumentNullException.ThrowIfNull(scheduler);
        ArgumentNullException.ThrowIfNull(kinds);
        this.scheduler = scheduler;
        Definition = new ToolDefinition(Name, Describe(kinds), SchemaJson);
    }

    public ToolDefinition Definition { get; }

    public ToolSideEffect SideEffect => ToolSideEffect.ReadOnly;

    public async ValueTask<ToolResult> InvokeAsync(ToolCall call, ToolContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(call);
        ArgumentNullException.ThrowIfNull(context);
        if (AgentScope.Current is not { } scope)
        {
            return new ToolResult("spawn_agent is only available inside a running agent.", IsError: true);
        }

        string kind;
        string task;
        string? resumeId;
        try
        {
            using var document = JsonDocument.Parse(call.ArgumentsJson);
            var root = document.RootElement;
            if (!root.TryGetProperty("kind", out var kindElement) || kindElement.ValueKind != JsonValueKind.String)
            {
                return new ToolResult("spawn_agent requires a 'kind' string.", IsError: true);
            }

            if (!root.TryGetProperty("task", out var taskElement) || taskElement.ValueKind != JsonValueKind.String)
            {
                return new ToolResult("spawn_agent requires a 'task' string.", IsError: true);
            }

            kind = kindElement.GetString()!;
            task = taskElement.GetString()!;
            resumeId = JsonArguments.OptionalString(root, "resume_id");
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            return new ToolResult($"spawn_agent arguments are invalid: {exception.Message}", IsError: true);
        }

        if (string.IsNullOrWhiteSpace(kind) || string.IsNullOrWhiteSpace(task))
        {
            return new ToolResult("spawn_agent requires a non-empty 'kind' and 'task'.", IsError: true);
        }

        var outcome = await SpawnAsync(scope, kind, task, resumeId, context, ct).ConfigureAwait(false);
        var content = outcome.AgentId.Length == 0
            ? outcome.Text
            : string.Concat(outcome.Text, "\n\n[agent_id: ", outcome.AgentId, "]");
        return new ToolResult(content, outcome.IsError);
    }

    private async ValueTask<SubAgentOutcome> SpawnAsync(
        AgentScope scope,
        string kind,
        string task,
        string? resumeId,
        ToolContext context,
        CancellationToken ct)
    {
        await using var detached = scope.Permit.Detach(ct);
        return await scheduler
            .SpawnAsync(kind, task, resumeId, context, scope.Sink, scope.AgentId, ct)
            .ConfigureAwait(false);
    }

    private static string Describe(IEnumerable<AgentDefinition> kinds)
    {
        var ordered = kinds.OrderBy(static kind => kind.Name, StringComparer.Ordinal).ToArray();
        var builder = new StringBuilder(256);
        builder.Append("Delegate one self-contained task to a sub-agent and get its final report back. ")
            .Append("Parallel spawns run concurrently; nested depth is limited. Use resume_id to continue a finished sub-agent of the same kind.\n\nKinds:\n");
        foreach (var kind in ordered)
        {
            builder.Append("- ").Append(kind.Name).Append(": ").Append(kind.Description).Append('\n');
        }

        return builder.ToString().TrimEnd();
    }
}
