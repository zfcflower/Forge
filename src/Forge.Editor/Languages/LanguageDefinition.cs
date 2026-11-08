using System.Text.Json.Nodes;

namespace Forge.Editor.Languages;

/// <summary>How to start the language server of one language.</summary>
public sealed record LanguageServerConfig(
    string Command,
    IReadOnlyList<string> Arguments,
    IReadOnlyDictionary<string, string> Environment)
{
    public static LanguageServerConfig Create(string command, params string[] arguments) =>
        new(command, arguments, new Dictionary<string, string>());
}

/// <summary>
/// One language: the file patterns that select it and the server that serves it. A definition without a server is a
/// language the editor highlights lexically only.
/// </summary>
public sealed record LanguageDefinition(
    string Id,
    IReadOnlyList<string> Extensions,
    IReadOnlyList<string> FileNames,
    LanguageServerConfig? Server,
    JsonNode? InitializationOptions = null,
    string? DisplayName = null,
    string? ServerOrigin = null)
{
}
