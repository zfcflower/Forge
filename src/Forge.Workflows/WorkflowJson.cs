using System.Text;
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

        // The reader consumes UTF-8 anyway, so transcode once here and reuse those bytes for the
        // "is `steps` present" scan instead of materializing the document twice.
        return Parse(Encoding.UTF8.GetBytes(json));
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

        if (definition is null)
        {
            throw new WorkflowFormatException("invalid workflow JSON: the document must be a JSON object, not null.");
        }

        if (!HasStepsProperty(utf8Json))
        {
            throw new WorkflowFormatException("invalid workflow JSON: 'steps' must be an array", "$.steps");
        }

        return Normalize(definition);
    }

    /// <summary>
    /// Reports whether the top-level object carries a <c>steps</c> property. Property initializers on the
    /// target type survive deserialization of an absent property, so <see cref="WorkflowDefinition.Steps"/> is
    /// an empty list for <c>{}</c> and cannot by itself tell "absent" from "explicitly empty". Only the
    /// document can, hence this scan. Runs after deserialization, so the JSON is known well formed.
    /// </summary>
    private static bool HasStepsProperty(ReadOnlySpan<byte> utf8Json)
    {
        // Same reader options as WorkflowJsonContext, otherwise a comment or a trailing comma the
        // deserializer accepts would throw here instead.
        var reader = new Utf8JsonReader(utf8Json, new JsonReaderOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        });
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
        {
            return false;
        }

        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            var isSteps = reader.ValueIsEscaped ? reader.GetString() == "steps" : reader.ValueTextEquals("steps"u8);
            reader.Read();
            if (isSteps)
            {
                return true;
            }

            reader.Skip();
        }

        return false;
    }

    /// <summary>Serializes a definition back to JSON in the canonical form <see cref="Parse(string)"/> accepts.</summary>
    public static string Serialize(WorkflowDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return JsonSerializer.Serialize(definition, WorkflowJsonContext.Default.WorkflowDefinition);
    }

    private static WorkflowDefinition Normalize(WorkflowDefinition definition)
    {
        // A property present as JSON null still lands here as null, which the presence scan cannot see.
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
