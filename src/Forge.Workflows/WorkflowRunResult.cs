using System.Text.Json;

namespace Forge.Workflows;

/// <summary>Outcome of one step, in definition order in <see cref="WorkflowRunResult.Steps"/>.</summary>
public sealed record StepResult
{
    /// <summary>0-based index in the definition's <c>steps</c> array.</summary>
    public required int Index { get; init; }

    /// <summary>Step id (a synthesized <c>#n</c> when the definition omitted it).</summary>
    public required string Id { get; init; }

    /// <summary>Tool name the step called.</summary>
    public required string Tool { get; init; }

    /// <summary>Final status.</summary>
    public required StepStatus Status { get; init; }

    /// <summary>Raw <see cref="Forge.Plugin.Sdk.HostToolResult.Content"/>; <see langword="null"/> when the tool never ran.</summary>
    public string? Content { get; init; }

    /// <summary>True when the tool itself reported an error.</summary>
    public bool IsError { get; init; }

    /// <summary>Failure, cancellation or skip explanation; <see langword="null"/> for successful steps.</summary>
    public string? Error { get; init; }

    /// <summary>Wall-clock start (clock from <see cref="RunOptions.TimeProvider"/>).</summary>
    public DateTimeOffset StartedAt { get; init; }

    /// <summary>Wall-clock end.</summary>
    public DateTimeOffset CompletedAt { get; init; }

    /// <summary>Elapsed time measured with the <see cref="RunOptions.TimeProvider"/> timestamps.</summary>
    public TimeSpan Duration { get; init; }

    /// <summary>Ids this step waited for, including the implicit "previous step" edge.</summary>
    public IReadOnlyList<string> DependsOn { get; init; } = [];

    /// <summary>Parses <see cref="Content"/> as JSON. The caller owns the returned document.</summary>
    public JsonDocument? ParseContent()
    {
        if (string.IsNullOrWhiteSpace(Content))
        {
            return null;
        }

        try
        {
            return JsonDocument.Parse(Content);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>Result of <see cref="WorkflowEngine.RunAsync"/>; failures are reported here, never thrown.</summary>
public sealed record WorkflowRunResult
{
    /// <summary>Overall outcome.</summary>
    public required WorkflowRunStatus Status { get; init; }

    /// <summary>Definition name, when it had one.</summary>
    public string? Name { get; init; }

    /// <summary>One entry per definition step, always in definition order.</summary>
    public required IReadOnlyList<StepResult> Steps { get; init; }

    /// <summary>Validation findings. Non-empty with <see cref="WorkflowRunStatus.Invalid"/>; warnings may appear on any run.</summary>
    public IReadOnlyList<WorkflowDiagnostic> Diagnostics { get; init; } = [];

    /// <summary>Run-level explanation: the first failure, the timeout, or the cancellation summary.</summary>
    public string? Error { get; init; }

    /// <summary>Wall-clock start.</summary>
    public DateTimeOffset StartedAt { get; init; }

    /// <summary>Wall-clock end.</summary>
    public DateTimeOffset CompletedAt { get; init; }

    /// <summary>Whole-run elapsed time measured with the <see cref="RunOptions.TimeProvider"/> timestamps.</summary>
    public TimeSpan Duration { get; init; }

    /// <summary>Ids of steps that never produced a result (failed dependency, cancellation, timeout).</summary>
    public IReadOnlyList<string> NeverRanStepIds { get; init; } = [];

    /// <summary>True when every step succeeded.</summary>
    public bool Succeeded => Status == WorkflowRunStatus.Completed;

    /// <summary>Finds a step result by id, or <see langword="null"/>.</summary>
    public StepResult? FindStep(string stepId)
    {
        foreach (var step in Steps)
        {
            if (string.Equals(step.Id, stepId, StringComparison.Ordinal))
            {
                return step;
            }
        }

        return null;
    }
}
