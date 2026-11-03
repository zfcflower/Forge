namespace Forge.Workflows;

/// <summary>
/// Thrown when a workflow document cannot be read as a <see cref="WorkflowDefinition"/>: malformed JSON,
/// wrong JSON shapes, or unknown values for typed fields. Semantic problems (duplicate ids, missing tool
/// names, unknown dependencies, cycles, broken bindings) are reported by
/// <see cref="WorkflowValidator"/> instead of throwing.
/// </summary>
public sealed class WorkflowFormatException : Exception
{
    public WorkflowFormatException(
        string message,
        string? jsonPath = null,
        long? lineNumber = null,
        long? bytePositionInLine = null,
        Exception? inner = null)
        : base(message, inner)
    {
        JsonPath = jsonPath;
        LineNumber = lineNumber;
        BytePositionInLine = bytePositionInLine;
    }

    /// <summary>JSON pointer-ish path inside the document (for example <c>$.steps[1].tool</c>), when known.</summary>
    public string? JsonPath { get; }

    /// <summary>1-based line of the offending token, when known.</summary>
    public long? LineNumber { get; }

    /// <summary>0-based byte offset within <see cref="LineNumber"/>, when known.</summary>
    public long? BytePositionInLine { get; }
}
