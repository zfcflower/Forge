using System.Text.Json.Serialization;

namespace Forge.Approval;

/// <summary>
/// Source-generated JSON metadata for the approval state file. Reflection-based <c>JsonSerializer</c> overloads
/// are unavailable under Native AOT, so every persisted shape is declared here and serialized through its
/// <c>JsonTypeInfo</c>.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(PluginRecord))]
[JsonSerializable(typeof(PluginRecord[]))]
public sealed partial class ApprovalJsonContext : JsonSerializerContext;
