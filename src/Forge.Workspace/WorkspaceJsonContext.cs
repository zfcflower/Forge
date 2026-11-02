using System.Text.Json.Serialization;

namespace Forge.Workspace;

/// <summary>
/// Source-generated JSON metadata for the shapes the workspace persists itself. Reflection-based
/// <c>JsonSerializer</c> overloads are unavailable under Native AOT, so every persisted type is declared here
/// and serialized through its <c>JsonTypeInfo</c>. Tool payloads do not belong here: they go through
/// <see cref="JsonPayload"/>.
/// </summary>
[JsonSerializable(typeof(DiffAnchor))]
internal sealed partial class WorkspaceJsonContext : JsonSerializerContext;
