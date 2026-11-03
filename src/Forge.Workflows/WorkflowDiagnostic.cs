namespace Forge.Workflows;

/// <summary>How serious a <see cref="WorkflowDiagnostic"/> is. Only errors prevent a run.</summary>
public enum WorkflowDiagnosticSeverity
{
    Warning,
    Error,
}

/// <summary>Machine-readable kind of a <see cref="WorkflowDiagnostic"/>.</summary>
public enum WorkflowDiagnosticCode
{
    /// <summary>A step has no <c>id</c>.</summary>
    MissingStepId,

    /// <summary>Two steps share an <c>id</c>.</summary>
    DuplicateStepId,

    /// <summary>A step has no <c>tool</c>.</summary>
    MissingTool,

    /// <summary>An <c>after</c> entry is empty or not a string.</summary>
    InvalidAfterEntry,

    /// <summary>An <c>after</c> entry names a step that does not exist.</summary>
    UnknownDependency,

    /// <summary>A step lists itself in <c>after</c>.</summary>
    SelfDependency,

    /// <summary>The dependency graph contains a cycle.</summary>
    DependencyCycle,

    /// <summary>A <c>$ref</c> path cannot be parsed.</summary>
    InvalidBindingPath,

    /// <summary>A binding object uses an unknown <c>$</c>-prefixed directive.</summary>
    InvalidBindingDirective,

    /// <summary>A <c>$ref</c> names a step that does not exist.</summary>
    UnknownBindingStep,

    /// <summary>A <c>$ref</c> names a step that is not a (transitive) dependency of the binding step.</summary>
    BindingOnNonDependency,

    /// <summary>The document declares a format version this package does not implement.</summary>
    UnsupportedVersion,
}

/// <summary>A validation finding. Validation never throws; errors are collected instead.</summary>
/// <param name="Severity">Warning or error.</param>
/// <param name="Code">Machine-readable kind.</param>
/// <param name="Message">Human-readable explanation, always naming the offending step.</param>
/// <param name="StepIndex">0-based index in <c>steps</c>, or -1 when the finding is not about one step.</param>
/// <param name="StepId">Step id when known, otherwise <see langword="null"/>.</param>
public sealed record WorkflowDiagnostic(
    WorkflowDiagnosticSeverity Severity,
    WorkflowDiagnosticCode Code,
    string Message,
    int StepIndex = -1,
    string? StepId = null)
{
    /// <inheritdoc />
    public override string ToString() => StepIndex >= 0
        ? $"{Severity} {Code} (steps[{StepIndex}]): {Message}"
        : $"{Severity} {Code}: {Message}";
}
