using System.Text.Json;
using Forge.Abstractions;

namespace Forge.Core.Agents;

/// <summary>The sub-agent tools a parent agent gets; all of them disappear once the depth limit is reached.</summary>
public static class SubAgentTools
{
    public static IReadOnlyList<string> Names { get; } =
        [SpawnAgentTool.Name, SubAgentSteerTool.Name, SubAgentFollowUpTool.Name, SubAgentStopTool.Name];
}

/// <summary>Redirects a running sub-agent at its next tool-batch boundary.</summary>
public sealed class SubAgentSteerTool : ITool
{
    public const string Name = "subagent_steer";

    private const string SchemaJson =
        """{"type":"object","properties":{"id":{"type":"string","description":"Id of the sub-agent, as reported by spawn_agent."},"instruction":{"type":"string","description":"The new instruction. It replaces the plan the sub-agent was following."}},"required":["id","instruction"],"additionalProperties":false}""";

    private readonly ISubAgentControl control;

    public SubAgentSteerTool(ISubAgentControl control)
    {
        ArgumentNullException.ThrowIfNull(control);
        this.control = control;
        Definition = new ToolDefinition(
            Name,
            "Redirect a running sub-agent. When the tool calls it is running right now finish, the remaining calls of that batch are skipped and the instruction takes over. A sub-agent that is still queued gets the instruction appended to its task.",
            SchemaJson);
    }

    public ToolDefinition Definition { get; }

    public ToolSideEffect SideEffect => ToolSideEffect.ReadOnly;

    public ValueTask<ToolResult> InvokeAsync(ToolCall call, ToolContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(call);
        return SubAgentToolArguments.SendAsync(call, Name, "instruction", "the instruction is delivered at its next tool-batch boundary", control.Steer);
    }
}

/// <summary>Queues an instruction for a sub-agent to run after it finishes its current task.</summary>
public sealed class SubAgentFollowUpTool : ITool
{
    public const string Name = "subagent_followup";

    private const string SchemaJson =
        """{"type":"object","properties":{"id":{"type":"string","description":"Id of the sub-agent, as reported by spawn_agent."},"instruction":{"type":"string","description":"The follow-up instruction, delivered as a new user message."}},"required":["id","instruction"],"additionalProperties":false}""";

    private readonly ISubAgentControl control;

    public SubAgentFollowUpTool(ISubAgentControl control)
    {
        ArgumentNullException.ThrowIfNull(control);
        this.control = control;
        Definition = new ToolDefinition(
            Name,
            "Queue an instruction for a sub-agent to run after it finishes its current task instead of completing. The sub-agent keeps its context.",
            SchemaJson);
    }

    public ToolDefinition Definition { get; }

    public ToolSideEffect SideEffect => ToolSideEffect.ReadOnly;

    public ValueTask<ToolResult> InvokeAsync(ToolCall call, ToolContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(call);
        return SubAgentToolArguments.SendAsync(call, Name, "instruction", "the instruction is delivered when its current task ends", control.FollowUp);
    }
}

/// <summary>Stops one sub-agent; its partial output comes back as the spawn result.</summary>
public sealed class SubAgentStopTool : ITool
{
    public const string Name = "subagent_stop";

    private const string SchemaJson =
        """{"type":"object","properties":{"id":{"type":"string","description":"Id of the sub-agent, as reported by spawn_agent."},"reason":{"type":"string","description":"Why it is stopped; reported with the partial output."}},"required":["id"],"additionalProperties":false}""";

    private readonly ISubAgentControl control;

    public SubAgentStopTool(ISubAgentControl control)
    {
        ArgumentNullException.ThrowIfNull(control);
        this.control = control;
        Definition = new ToolDefinition(
            Name,
            "Stop a running or queued sub-agent now. It does not affect the caller or its other sub-agents; the partial output of the stopped sub-agent comes back in the spawn_agent result.",
            SchemaJson);
    }

    public ToolDefinition Definition { get; }

    public ToolSideEffect SideEffect => ToolSideEffect.ReadOnly;

    public ValueTask<ToolResult> InvokeAsync(ToolCall call, ToolContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(call);
        if (AgentScope.Current is null)
        {
            return new ValueTask<ToolResult>(new ToolResult($"{Name} is only available inside a running agent.", IsError: true));
        }

        string id;
        string? reason;
        try
        {
            using var document = JsonDocument.Parse(call.ArgumentsJson);
            var root = document.RootElement;
            if (!root.TryGetProperty("id", out var idElement) || idElement.ValueKind != JsonValueKind.String)
            {
                return new ValueTask<ToolResult>(new ToolResult($"{Name} requires an 'id' string.", IsError: true));
            }

            id = idElement.GetString()!;
            reason = JsonArguments.OptionalString(root, "reason");
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            return new ValueTask<ToolResult>(new ToolResult($"{Name} arguments are invalid: {exception.Message}", IsError: true));
        }

        if (string.IsNullOrWhiteSpace(id))
        {
            return new ValueTask<ToolResult>(new ToolResult($"{Name} requires a non-empty 'id'.", IsError: true));
        }

        return new ValueTask<ToolResult>(SubAgentToolArguments.Report(control.Stop(id, reason), id, Name, "it stops and its partial output is returned to you"));
    }
}

/// <summary>Argument parsing and result wording shared by the sub-agent control tools.</summary>
internal static class SubAgentToolArguments
{
    internal static ValueTask<ToolResult> SendAsync(
        ToolCall call,
        string toolName,
        string instructionName,
        string accepted,
        Func<string, string, SubAgentControlResult> send)
    {
        if (AgentScope.Current is null)
        {
            return new ValueTask<ToolResult>(new ToolResult($"{toolName} is only available inside a running agent.", IsError: true));
        }

        string id;
        string instruction;
        try
        {
            using var document = JsonDocument.Parse(call.ArgumentsJson);
            var root = document.RootElement;
            if (!root.TryGetProperty("id", out var idElement) || idElement.ValueKind != JsonValueKind.String)
            {
                return new ValueTask<ToolResult>(new ToolResult($"{toolName} requires an 'id' string.", IsError: true));
            }

            id = idElement.GetString()!;
            instruction = JsonArguments.OptionalString(root, instructionName) ?? string.Empty;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            return new ValueTask<ToolResult>(new ToolResult($"{toolName} arguments are invalid: {exception.Message}", IsError: true));
        }

        if (string.IsNullOrWhiteSpace(id))
        {
            return new ValueTask<ToolResult>(new ToolResult($"{toolName} requires a non-empty 'id'.", IsError: true));
        }

        if (string.IsNullOrWhiteSpace(instruction))
        {
            return new ValueTask<ToolResult>(new ToolResult($"{toolName} requires a non-empty '{instructionName}'.", IsError: true));
        }

        return new ValueTask<ToolResult>(Report(send(id, instruction), id, toolName, accepted));
    }

    internal static ToolResult Report(SubAgentControlResult result, string id, string toolName, string accepted) => result switch
    {
        SubAgentControlResult.Accepted => new ToolResult($"accepted: {accepted}."),
        SubAgentControlResult.NotFound => new ToolResult($"not found: no sub-agent '{id}' exists in this session.", IsError: true),
        SubAgentControlResult.AlreadyFinished => new ToolResult($"already finished: sub-agent '{id}' has ended.", IsError: true),
        _ => new ToolResult($"{toolName} failed for sub-agent '{id}'.", IsError: true),
    };
}
