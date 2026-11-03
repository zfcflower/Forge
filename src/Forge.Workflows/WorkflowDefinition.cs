using System.Text.Json;
using System.Text.Json.Serialization;

namespace Forge.Workflows;

/// <summary>
/// A declarative workflow: a flat list of tool steps. Ordering comes only from each step's
/// <see cref="WorkflowStep.After"/> list, so steps without a relationship between them run in parallel.
/// </summary>
public sealed record WorkflowDefinition
{
    /// <summary>The definition format version understood by this package.</summary>
    public const int SupportedVersion = 1;

    /// <summary>Optional display name; not used for scheduling.</summary>
    public string? Name { get; init; }

    /// <summary>Definition format version. Only <see cref="SupportedVersion"/> is executable.</summary>
    public int Version { get; init; } = SupportedVersion;

    /// <summary>Steps in document order. Order is the stable tie-breaker for scheduling.</summary>
    public IReadOnlyList<WorkflowStep> Steps { get; init; } = [];
}

/// <summary>One tool call in a workflow.</summary>
public sealed record WorkflowStep
{
    /// <summary>Stable step identifier, unique within the definition and used by bindings and <see cref="After"/>.</summary>
    public string? Id { get; init; }

    /// <summary>Tool name passed to <see cref="Forge.Plugin.Sdk.IHostFacade.CallToolAsync"/>.</summary>
    public string? Tool { get; init; }

    /// <summary>
    /// Tool arguments. Literal JSON with embedded <c>{"$ref": "…"}</c> binding objects that are
    /// resolved from earlier steps' outputs before the call. Absent means an empty object.
    /// </summary>
    public JsonElement Args { get; init; }

    /// <summary>
    /// Step ids this step waits for. Omitted (<see langword="null"/>) means "the immediately preceding
    /// step" for every step but the first; an explicit empty array means "no dependencies".
    /// </summary>
    public IReadOnlyList<string>? After { get; init; }

    /// <summary>What to do when this step fails. Only <see cref="StepErrorMode.Fail"/> exists today.</summary>
    public StepErrorMode OnError { get; init; } = StepErrorMode.Fail;
}

/// <summary>Failure policy of a step.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<StepErrorMode>))]
public enum StepErrorMode
{
    /// <summary>Record the failure, never run dependent steps, and finish the run with status failed.</summary>
    Fail,
}

/// <summary>Status of a single step at the end of a run.</summary>
public enum StepStatus
{
    /// <summary>Never scheduled: the run was cancelled or timed out before this step became ready.</summary>
    NotRun,

    /// <summary>The tool call completed without an error.</summary>
    Succeeded,

    /// <summary>The tool call reported an error, threw, or its argument bindings could not be resolved.</summary>
    Failed,

    /// <summary>Not scheduled because a step it depends on (directly or transitively) failed.</summary>
    Skipped,

    /// <summary>Started and then cancelled, either by the caller's token or by the workflow timeout.</summary>
    Cancelled,
}

/// <summary>Status of a whole run.</summary>
public enum WorkflowRunStatus
{
    /// <summary>Every step succeeded.</summary>
    Completed,

    /// <summary>At least one step failed; steps depending on it were skipped.</summary>
    Failed,

    /// <summary>The caller's cancellation token stopped scheduling.</summary>
    Cancelled,

    /// <summary>The <see cref="RunOptions.Timeout"/> budget was exhausted.</summary>
    TimedOut,

    /// <summary>The definition has validation errors, so no step ran.</summary>
    Invalid,
}
