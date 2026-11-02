using System.Text.Json.Nodes;

namespace Forge.Editor.Languages;

/// <summary>
/// Language table built from the built-in defaults, <c>&lt;forge home&gt;/languages.json</c> and
/// <c>&lt;workspace&gt;/.forge/languages.json</c>. Later files override earlier ones per language and per field;
/// <c>"disabled": true</c> removes a language. Unreadable files are reported in <see cref="LoadErrors"/> instead of
/// throwing, so a broken config never blocks the editor.
/// </summary>
public sealed class LanguageRegistry
{
    public const string ConfigFileName = "languages.json";
    public const string WorkspaceConfigDirectory = ".forge";
    public const string ForgeHomeVariable = "FORGE_HOME";

    private readonly Dictionary<string, LanguageDefinition> _byId = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, LanguageDefinition> _byFileName = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(string Extension, LanguageDefinition Language)> _byExtension = [];
    private readonly List<string> _loadErrors = [];

    private LanguageRegistry(IEnumerable<LanguageDefinition> languages)
    {
        foreach (var language in languages)
        {
            _byId[language.Id] = language;
        }

        BuildIndexes();
    }

    public static LanguageDefinition DefaultCSharp { get; } = new(
        "csharp",
        [".cs", ".csx"],
        [],
        new LanguageServerConfig(
            "roslyn-language-server",
            ["--stdio"],
            new Dictionary<string, string>(StringComparer.Ordinal)),
        InitializationOptions: null,
        DisplayName: "C#");

    /// <summary>Built-in defaults: C# only. The server itself is user-installed.</summary>
    public static LanguageRegistry Default { get; } = new([DefaultCSharp]);

    public IReadOnlyList<LanguageDefinition> Languages => [.. _byId.Values];

    public IReadOnlyList<string> LoadErrors => _loadErrors;

    public static string ForgeHome => Environment.GetEnvironmentVariable(ForgeHomeVariable) is { Length: > 0 } custom
        ? custom
        : System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".forge");

    public static string UserConfigPath => System.IO.Path.Combine(ForgeHome, ConfigFileName);

    public static string WorkspaceConfigPath(string workspacePath) =>
        System.IO.Path.Combine(workspacePath, WorkspaceConfigDirectory, ConfigFileName);

    public LanguageDefinition? GetById(string? id) =>
        id is { Length: > 0 } && _byId.TryGetValue(id, out var language) ? language : null;

    /// <summary>Longest matching extension wins, then exact file names; ordinal and case-insensitive.</summary>
    public LanguageDefinition? DetectByPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var name = System.IO.Path.GetFileName(path);
        foreach (var (extension, language) in _byExtension)
        {
            if (name.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            {
                return language;
            }
        }

        return name.Length > 0 && _byFileName.TryGetValue(name, out var byName) ? byName : null;
    }

    public string? DetectLanguageId(string? path) => DetectByPath(path)?.Id;

    public static async ValueTask<LanguageRegistry> LoadAsync(
        string? workspacePath = null,
        string? userConfigPath = null,
        CancellationToken ct = default)
    {
        var languages = new Dictionary<string, LanguageDefinition>(StringComparer.OrdinalIgnoreCase)
        {
            [DefaultCSharp.Id] = DefaultCSharp,
        };
        var errors = new List<string>();

        await ApplyFileAsync(userConfigPath ?? UserConfigPath, languages, errors, ct).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(workspacePath))
        {
            await ApplyFileAsync(WorkspaceConfigPath(workspacePath), languages, errors, ct).ConfigureAwait(false);
        }

        var registry = new LanguageRegistry(languages.Values);
        registry._loadErrors.AddRange(errors);
        return registry;
    }

    /// <summary>Parses one config document and merges it over <paramref name="languages"/>.</summary>
    public static void ApplyJson(JsonNode? root, IDictionary<string, LanguageDefinition> languages, ICollection<string> errors)
    {
        ArgumentNullException.ThrowIfNull(languages);
        ArgumentNullException.ThrowIfNull(errors);
        if (root is not JsonObject rootObject)
        {
            errors.Add("The configuration root must be a JSON object.");
            return;
        }

        var container = rootObject["languages"] is JsonObject nested ? nested : rootObject;
        foreach (var (id, value) in container)
        {
            if (value is not JsonObject definition)
            {
                errors.Add($"Language '{id}' must be a JSON object.");
                continue;
            }

            if (definition["disabled"] is JsonValue disabled && disabled.TryGetValue<bool>(out var isDisabled) && isDisabled)
            {
                languages.Remove(id);
                continue;
            }

            languages.TryGetValue(id, out var existing);
            var extensions = ReadStrings(definition["extensions"]) ?? existing?.Extensions ?? [];
            var fileNames = ReadStrings(definition["filenames"]) ?? existing?.FileNames ?? [];
            var displayName = definition["displayName"] is JsonValue display && display.TryGetValue<string>(out var parsedName)
                ? parsedName
                : existing?.DisplayName;
            var initializationOptions = definition["initializationOptions"]?.DeepClone() ?? existing?.InitializationOptions;

            LanguageServerConfig? server;
            var hasServerProperty = definition.TryGetPropertyValue("server", out var serverNode);
            if (!hasServerProperty)
            {
                server = existing?.Server;
            }
            else if (serverNode is null)
            {
                server = null;
            }
            else if (serverNode is JsonObject serverObject && serverObject["command"] is JsonValue commandValue &&
                     commandValue.TryGetValue<string>(out var command) && !string.IsNullOrWhiteSpace(command))
            {
                var arguments = ReadStrings(serverObject["args"]) ?? [];
                var environment = new Dictionary<string, string>(StringComparer.Ordinal);
                var environmentValid = true;
                if (serverObject.TryGetPropertyValue("env", out var envNode) && envNode is not null)
                {
                    if (envNode is JsonObject envObject)
                    {
                        foreach (var (key, envValue) in envObject)
                        {
                            if (envValue is JsonValue environmentValue && environmentValue.TryGetValue<string>(out var text))
                            {
                                environment[key] = text;
                            }
                            else
                            {
                                errors.Add($"Language '{id}' has a non-string value for environment variable '{key}'; the server section is ignored.");
                                environmentValid = false;
                                break;
                            }
                        }
                    }
                    else
                    {
                        errors.Add($"Language '{id}' has an invalid env section; the server section is ignored.");
                        environmentValid = false;
                    }
                }

                server = environmentValid
                    ? new LanguageServerConfig(command, arguments, environment)
                    : existing?.Server;
            }
            else
            {
                errors.Add($"Language '{id}' has an invalid server section; the existing server is kept.");
                server = existing?.Server;
            }

            languages[id] = new LanguageDefinition(id, extensions, fileNames, server, initializationOptions, displayName);
        }
    }

    private static async ValueTask ApplyFileAsync(
        string path,
        IDictionary<string, LanguageDefinition> languages,
        ICollection<string> errors,
        CancellationToken ct)
    {
        if (!File.Exists(path))
        {
            return;
        }

        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 16 * 1024, useAsync: true);
            var node = await JsonNode.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            ApplyJson(node, languages, errors);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            errors.Add($"{path}: {ex.Message}");
        }
    }

    private static IReadOnlyList<string>? ReadStrings(JsonNode? node)
    {
        if (node is not JsonArray array)
        {
            return null;
        }

        var result = new List<string>(array.Count);
        foreach (var item in array)
        {
            if (item is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text))
            {
                result.Add(text.Trim());
            }
        }

        return result;
    }

    private void BuildIndexes()
    {
        foreach (var language in _byId.Values)
        {
            foreach (var fileName in language.FileNames)
            {
                _byFileName.TryAdd(fileName, language);
            }

            foreach (var extension in language.Extensions)
            {
                _byExtension.Add((extension.StartsWith('.') ? extension : "." + extension, language));
            }
        }

        _byExtension.Sort(static (a, b) => b.Extension.Length.CompareTo(a.Extension.Length));
    }
}
