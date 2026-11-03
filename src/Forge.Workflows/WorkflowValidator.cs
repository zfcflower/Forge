namespace Forge.Workflows;

/// <summary>Static definition checks. Never throws for bad definitions; every finding is a diagnostic.</summary>
public static class WorkflowValidator
{
    /// <summary>
    /// Returns every finding for <paramref name="definition"/>, in a deterministic order: document-level first,
    /// then per step in document order (ids, tools, <c>after</c>, argument bindings), then dependency cycles.
    /// </summary>
    public static IReadOnlyList<WorkflowDiagnostic> Validate(WorkflowDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var diagnostics = new List<WorkflowDiagnostic>();
        _ = WorkflowPlanBuilder.Build(definition, diagnostics);
        return diagnostics;
    }
}
