using System.Text.Json;
using System.Text.Json.Serialization;

namespace Forge.Workflows;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
    WriteIndented = false)]
[JsonSerializable(typeof(WorkflowDefinition))]
internal sealed partial class WorkflowJsonContext : JsonSerializerContext
{
}
