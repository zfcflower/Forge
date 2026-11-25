using System.Text;
using System.Text.Json;
using Forge.Abstractions;

namespace Forge.Core.Agents;

/// <summary>
/// Delegates one scoped task to a sub-agent. It is read-only on purpose: several spawn calls in one turn
/// then run concurrently, and the parent keeps its own parallelism permit while the child runs.
/// <para>
/// The optional <c>model</c>/<c>provider</c>/<c>thinking</c> arguments apply to this one spawn only. They are
/// resolved by the host's <see cref="AgentHostOptions.SpawnModels"/> callback (the app owns the model catalogue),
/// and they are absent from the tool's definition when that callback is missing, so a host without an app behind
/// it keeps the old bytes. Nothing here writes settings: a saved default is changed in the settings page, not by a
/// delegation.
/// </para>
/// </summary>
public sealed class SpawnAgentTool : ITool
{
    public const string Name = "spawn_agent";

    private const string BaseSchemaJson =
        """{"type":"object","properties":{"kind":{"type":"string","description":"Agent kind to run, from the kinds listed in this tool's description."},"task":{"type":"string","description":"The complete, self-contained task for the sub-agent."},"resume_id":{"type":"string","description":"Id of a finished sub-agent of the same kind whose context should continue instead of starting fresh."}},"required":["kind","task"],"additionalProperties":false}""";

    private const string OverrideSchemaJson =
        """{"type":"object","properties":{"kind":{"type":"string","description":"Agent kind to run, from the kinds listed in this tool's description."},"task":{"type":"string","description":"The complete, self-contained task for the sub-agent."},"resume_id":{"type":"string","description":"Id of a finished sub-agent of the same kind whose context should continue instead of starting fresh."},"model":{"type":"string","description":"Model id for this spawn only, exactly as the models tool lists it. Omit to run on the agent's saved default; cannot be combined with resume_id."},"provider":{"type":"string","description":"Connected provider id or display name, to disambiguate a model more than one provider lists."},"thinking":{"type":"string","description":"Reasoning step for this spawn only (off, minimal, low, medium, high, xhigh, max); clamped down to one the model accepts."}},"required":["kind","task"],"additionalProperties":false}""";

    private const string OverrideHelp =
        "\n\nModel override: pass 'model' with the exact id from the models tool to run this one spawn on a different model, "
        + "'provider' to name the connected provider when several list that model, and 'thinking' to pick a reasoning step "
        + "(it is clamped down to one the model accepts). The override applies to this spawn only and never changes the "
        + "agent's saved default, which the user changes in Settings. It cannot be combined with resume_id: a resumed "
        + "sub-agent keeps the model it started with."
        + "\n\nThe sub-agent runs in the background: this call returns as soon as it is started (there is no report yet), you "
        + "keep working in the same turn, and its report arrives later as a session-reply message you read like any other user "
        + "message. Until then you can redirect it with subagent_steer, queue more with subagent_followup or end it with "
        + "subagent_stop.";

    private readonly SubAgentScheduler scheduler;
    private readonly AgentDefinition[] kinds;
    private readonly Func<SpawnModelRequest, SpawnModelChoice>? spawnModels;

    /// <summary>
    /// The session's own event sink, which outlives the turn: a background child's started/finished events have to reach the tab
    /// that shows it after the starting turn's channel is gone (see <see cref="DetachedEventSink"/>). Null for a host with no shell.
    /// </summary>
    private readonly Action<AgentEvent>? sessionEvents;

    public SpawnAgentTool(
        SubAgentScheduler scheduler,
        IEnumerable<AgentDefinition> kinds,
        Func<SpawnModelRequest, SpawnModelChoice>? spawnModels = null,
        Action<AgentEvent>? sessionEvents = null)
    {
        ArgumentNullException.ThrowIfNull(scheduler);
        ArgumentNullException.ThrowIfNull(kinds);
        this.scheduler = scheduler;
        this.spawnModels = spawnModels;
        this.sessionEvents = sessionEvents;
        this.kinds = [.. kinds.OrderBy(static kind => kind.Name, StringComparer.Ordinal)];
        Definition = new ToolDefinition(
            Name,
            Describe(this.kinds) + (spawnModels is null ? string.Empty : OverrideHelp),
            spawnModels is null ? BaseSchemaJson : OverrideSchemaJson);
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
        string? model;
        string? provider;
        string? thinking;
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
            model = StringArgument(root, "model");
            provider = StringArgument(root, "provider");
            thinking = StringArgument(root, "thinking");
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            return new ToolResult($"spawn_agent arguments are invalid: {exception.Message}", IsError: true);
        }

        if (string.IsNullOrWhiteSpace(kind) || string.IsNullOrWhiteSpace(task))
        {
            return new ToolResult("spawn_agent requires a non-empty 'kind' and 'task'.", IsError: true);
        }

        model = model?.Trim();
        provider = provider?.Trim();
        thinking = thinking?.Trim();
        if (model is { Length: 0 } || provider is { Length: 0 } || thinking is { Length: 0 })
        {
            return new ToolResult("spawn_agent's 'model', 'provider' and 'thinking' must not be empty when given.", IsError: true);
        }

        ModelSettings? resolved = null;
        string? note = null;
        if (model is not null || provider is not null || thinking is not null)
        {
            if (spawnModels is null)
            {
                return new ToolResult("spawn_agent on this host cannot resolve a model override; drop 'model', 'provider' and 'thinking'.", IsError: true);
            }

            if (resumeId is not null)
            {
                return new ToolResult(
                    "spawn_agent cannot combine 'model', 'provider' or 'thinking' with 'resume_id': a resumed sub-agent keeps the model it started with. Start a new spawn instead.",
                    IsError: true);
            }

            // The kind's current settings are the baseline (a model-only override keeps its thinking, a thinking-only
            // override keeps its model). An unknown kind is left to the scheduler, whose message lists what exists.
            if (kinds.FirstOrDefault(agent => string.Equals(agent.Name, kind, StringComparison.OrdinalIgnoreCase)) is { } definition)
            {
                var choice = spawnModels(new SpawnModelRequest(
                    model,
                    provider,
                    thinking,
                    definition.Model,
                    context.Tools is { } tools && tools.TryGet("models", out _)));
                if (choice.Error is { } error)
                {
                    return new ToolResult(error, IsError: true);
                }

                if (choice.Settings is null)
                {
                    return new ToolResult("spawn_agent could not resolve the model override.", IsError: true);
                }

                resolved = choice.Settings;
                note = choice.Note;
            }
        }

        var outcome = await SpawnAsync(scope, kind, task, resumeId, context, resolved, ct).ConfigureAwait(false);
        var content = outcome.AgentId.Length == 0
            ? outcome.Text
            : string.Concat(outcome.Text, "\n\n[agent_id: ", outcome.AgentId, outcome.Resumed ? ", resumed" : string.Empty, "]");
        if (resolved is not null && outcome.AgentId.Length > 0)
        {
            content = string.Concat(
                content,
                "\n[model: ",
                resolved.Model,
                " on ",
                resolved.ProviderId ?? "the default provider",
                " @ ",
                ThinkingName(resolved.Thinking),
                "; this spawn only",
                note is null ? string.Empty : "; " + note,
                "]");
        }

        return new ToolResult(content, outcome.IsError);
    }

    /// <summary>
    /// Starts the child and answers at once, so the parent's turn goes on with it still running. The parent's parallelism permit is
    /// not handed back (the parent keeps working and therefore keeps its slot), and the run is not tied to this turn's token: its
    /// report reaches the parent as a reply message when it ends.
    /// </summary>
    private async ValueTask<SubAgentOutcome> SpawnAsync(
        AgentScope scope,
        string kind,
        string task,
        string? resumeId,
        ToolContext context,
        ModelSettings? model,
        CancellationToken ct)
    {
        var started = await scheduler
            .StartBackgroundAsync(
                kind,
                task,
                resumeId,
                context,
                sessionEvents is null ? scope.Sink : new DetachedEventSink(scope.Sink, sessionEvents),
                scope.AgentId,
                ct,
                model: model)
            .ConfigureAwait(false);
        if (started.Refusal is { } refusal)
        {
            return refusal;
        }

        return new SubAgentOutcome(
            started.AgentId,
            new StringBuilder(256)
                .Append("Started ").Append(kind).Append(" '").Append(started.AgentId).Append("' (session ")
                .Append(started.ChildSessionId).Append("). It is still running, so there is no report yet: it arrives later as a ")
                .Append("session-reply message in this conversation, and you can keep working meanwhile. While it runs you can ")
                .Append("redirect it with subagent_steer, queue more with subagent_followup, or end it with subagent_stop, ")
                .Append("using id '").Append(started.AgentId).Append("'.")
                .ToString(),
            IsError: false);
    }

    private static string Describe(IReadOnlyList<AgentDefinition> kinds)
    {
        var builder = new StringBuilder(256);
        builder.Append("Delegate one self-contained task to a sub-agent; it runs in the background and reports back. ")
            .Append("Parallel spawns run concurrently; nested depth is limited. An idle sub-agent of the same kind that ")
            .Append("recently worked on the same files or topic is continued automatically, so its earlier context ")
            .Append("carries over; a task about something else starts a fresh agent. Use resume_id to continue one ")
            .Append("specific finished sub-agent of the same kind. A spawn never waits for its report; that arrives as a ")
            .Append("message.\n\nKinds:\n");
        foreach (var kind in kinds)
        {
            builder.Append("- ").Append(kind.Name).Append(": ").Append(kind.Description).Append('\n');
        }

        return builder.ToString().TrimEnd();
    }

    private static string? StringArgument(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            throw new InvalidOperationException($"'{name}' must be a string.");
        }

        return value.GetString();
    }

    private static string ThinkingName(ThinkingEffort effort) => effort switch
    {
        ThinkingEffort.Minimal => "minimal",
        ThinkingEffort.Low => "low",
        ThinkingEffort.Medium => "medium",
        ThinkingEffort.High => "high",
        ThinkingEffort.XHigh => "xhigh",
        ThinkingEffort.Max => "max",
        _ => "off",
    };
}
