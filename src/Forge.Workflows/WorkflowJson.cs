using System.Text.Json;

namespace Forge.Workflows;

/// <summary>
/// Reads and writes <see cref="WorkflowDefinition"/> documents through a source-generated
/// <see cref="JsonSerializerContext"/>, so no reflection is used and the reader is AOT safe.
/// </summary>
public static class WorkflowJson
{
    /// <summary>Parses a workflow document.</summary>
    /// <exception cref="WorkflowFormatException">The document is not a well-formed workflow.</exception>
    public static WorkflowDefinition Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        WorkflowDefinition? definition;
        try
        {
            definition = JsonSerializer.Deserialize(json, WorkflowJsonContext.Default.WorkflowDefinition);
        }
        catch (JsonException ex)
        {
            throw Convert(ex);
        }

        return Normalize(definition);
    }

    /// <summary>Parses a UTF-8 workflow document.</summary>
    /// <exception cref="WorkflowFormatException">The document is not a well-formed workflow.</exception>
    public static WorkflowDefinition Parse(ReadOnlySpan<byte> utf8Json)
    {
        WorkflowDefinition? definition;
        try
        {
            definition = JsonSerializer.Deserialize(utf8Json, WorkflowJsonContext.Default.WorkflowDefinition);
        }
        catch (JsonException ex)
        {
            throw Convert(ex);
        }

        return Normalize(definition);
    }

    /// <summary>Serializes a definition back to JSON in the canonical form <see cref="Parse(string)"/> accepts.</summary>
    public static string Serialize(WorkflowDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return JsonSerializer.Serialize(definition, WorkflowJsonContext.Default.WorkflowDefinition);
    }

    private static WorkflowDefinition Normalize(WorkflowDefinition? definition)
    {
        if (definition is null)
        {
            throw new WorkflowFormatException("invalid workflow JSON: the document must be a JSON object, not null.");
        }

        // The source-generated deserializer assigns default(T) to every property absent from the document, so
        // defaults declared as property initializers do not survive a parse and have to be restored here.
        var steps = definition.Steps;
        if (steps is null)
        {
            throw new WorkflowFormatException("invalid workflow JSON: 'steps' must be an array", "$.steps");
        }

        for (var i = 0; i < steps.Count; i++)
        {
            var step = steps[i];
            if (step is null)
            {
                throw new WorkflowFormatException($"invalid workflow JSON: 'steps[{i}]' must be an object.", $"$.steps[{i}]");
            }

            if (step.After is not { } after)
            {
                continue;
            }

            for (var j = 0; j < after.Count; j++)
            {
                if (after[j] is null)
                {
                    throw new WorkflowFormatException(
                        $"invalid workflow JSON: 'steps[{i}].after[{j}]' must be a step id string.",
                        $"$.steps[{i}].after[{j}]");
                }
            }
        }

        var version = definition.Version == 0 ? WorkflowDefinition.SupportedVersion : definition.Version;
        return ReferenceEquals(steps, definition.Steps) && version == definition.Version
            ? definition
            : definition with { Steps = steps, Version = version };
    }

    private static WorkflowFormatException Convert(JsonException ex)
    {
        var path = ex.Path;
        var message = path is null
            ? $"invalid workflow JSON: {ex.Message}"
            : $"invalid workflow JSON at {path}: {ex.Message}";
        var line = ex.LineNumber is { } zeroBasedLine ? zeroBasedLine + 1 : (long?)null;
        return new WorkflowFormatException(message, path, line, ex.BytePositionInLine, ex);
    }
}
