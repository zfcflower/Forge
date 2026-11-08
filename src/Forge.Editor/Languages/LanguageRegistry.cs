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
    private readonly HashSet<string> _configured = new(StringComparer.OrdinalIgnoreCase);

    private LanguageRegistry(IEnumerable<LanguageDefinition> languages, IEnumerable<string>? configured = null)
    {
        foreach (var language in languages)
        {
            _byId[language.Id] = language;
        }

        foreach (var id in configured ?? [])
        {
            _configured.Add(id);
        }

        BuildIndexes();
    }

    /// <summary>
    /// Languages the user configured in <c>languages.json</c> (user or workspace), including ones configured down to "no
    /// server": automatic discovery must never override an explicit choice.
    /// </summary>
    public IReadOnlyCollection<string> ConfiguredLanguageIds => _configured;

    /// <summary>
    /// Returns a registry where each discovered server serves its language, unless the user configured that language.
    /// A language the registry did not know at all is added with the extensions and file names discovery carries, so a
    /// server found for an unconfigured language still opens for the right files.
    /// </summary>
    public LanguageRegistry WithDiscoveredServers(IReadOnlyList<DiscoveredLanguageServer> servers)
    {
        ArgumentNullException.ThrowIfNull(servers);
        var languages = new Dictionary<string, LanguageDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var language in _byId.Values)
        {
            languages[language.Id] = language;
        }

        foreach (var server in servers)
        {
            if (_configured.Contains(server.LanguageId))
            {
                continue;
            }

            languages.TryGetValue(server.LanguageId, out var existing);
            languages[server.LanguageId] = existing is null
                ? new LanguageDefinition(
                    server.LanguageId,
                    server.Extensions,
                    server.FileNames,
                    server.Server,
                    server.InitializationOptions,
                    DisplayName(server.LanguageId),
                    server.Origin)
                : existing with
                {
                    Server = server.Server,
                    ServerOrigin = server.Origin,
                    InitializationOptions = server.InitializationOptions ?? existing.InitializationOptions,
                    Extensions = existing.Extensions.Count > 0 ? existing.Extensions : server.Extensions,
                    FileNames = existing.FileNames.Count > 0 ? existing.FileNames : server.FileNames,
                };
        }

        var merged = new LanguageRegistry(languages.Values, _configured);
        merged._loadErrors.AddRange(_loadErrors);
        return merged;
    }

    private static string DisplayName(string languageId) => languageId switch
    {
        "csharp" => "C#",
        "typescript" => "TypeScript",
        "javascript" => "JavaScript",
        "json" => "JSON",
        "jsonc" => "JSON with Comments",
        "css" => "CSS",
        "html" => "HTML",
        "python" => "Python",
        "go" => "Go",
        "rust" => "Rust",
        "cpp" => "C/C++",
        "shell" => "Shell",
        "yaml" => "YAML",
        "lua" => "Lua",
        _ => languageId,
    };

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

    /// <summary>
    /// Exact file names win over extensions — <c>tsconfig.json</c> is a JSONC file, not plain JSON — then a file inside a
    /// <c>.vscode</c> folder (an editor configuration, so comments are legal), then the longest matching extension;
    /// ordinal and case-insensitive.
    /// </summary>
    public LanguageDefinition? DetectByPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var name = System.IO.Path.GetFileName(path);
        if (name.Length > 0 && _byFileName.TryGetValue(name, out var byName))
        {
            return byName;
        }

        // .vscode/settings.json, tasks.json and the like are editor configuration: VS Code treats every JSON below a
        // .vscode folder as JSON with comments, and without this rule the JSON server reports the comments as errors.
        if (name.EndsWith(".json", StringComparison.OrdinalIgnoreCase) &&
            _byId.TryGetValue(JsoncLanguageId, out var jsonc) &&
            JsoncDirectories.Any(segment => HasDirectorySegment(path, segment)))
        {
            return jsonc;
        }

        foreach (var (extension, language) in _byExtension)
        {
            if (name.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            {
                return language;
            }
        }

        return null;
    }

    /// <summary>The language id JSON with comments uses; discovery registers it only when a JSON server exists.</summary>
    public const string JsoncLanguageId = "jsonc";

    private static readonly char[] Separators = ['/', '\\'];

    /// <summary>
    /// Directories whose JSON files are editor configuration and may carry comments (VS Code's rule): neither an extension
    /// nor an exact file name can express "any .json below .vscode".
    /// </summary>
    private static readonly string[] JsoncDirectories = [".vscode"];

    /// <summary>Whether one directory segment of <paramref name="path"/> is exactly <paramref name="segment"/>.</summary>
    private static bool HasDirectorySegment(string path, string segment)
    {
        var remaining = path.AsSpan();
        while (true)
        {
            var separator = remaining.IndexOfAny(Separators);
            var current = separator < 0 ? remaining : remaining[..separator];
            if (current.Equals(segment, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (separator < 0)
            {
                return false;
            }

            remaining = remaining[(separator + 1)..];
        }
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
        var configured = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        await ApplyFileAsync(userConfigPath ?? UserConfigPath, languages, errors, configured, ct).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(workspacePath))
        {
            await ApplyFileAsync(WorkspaceConfigPath(workspacePath), languages, errors, configured, ct).ConfigureAwait(false);
        }

        var registry = new LanguageRegistry(languages.Values, configured);
        registry._loadErrors.AddRange(errors);
        return registry;
    }

    /// <summary>Parses one config document and merges it over <paramref name="languages"/>.</summary>
    public static void ApplyJson(
        JsonNode? root,
        IDictionary<string, LanguageDefinition> languages,
        ICollection<string> errors,
        ICollection<string>? configured = null)
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
                configured?.Add(id);
                languages.Remove(id);
                continue;
            }

            configured?.Add(id);

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
        ICollection<string> configured,
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
            ApplyJson(node, languages, errors, configured);
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
