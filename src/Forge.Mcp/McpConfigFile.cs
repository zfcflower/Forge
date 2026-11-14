using System.Text.Json;
using System.Text.Json.Nodes;

namespace Forge.Mcp;

/// <summary>
/// One <c>mcpServers</c> entry exactly as the file spells it. Every string is verbatim, so a <c>${VAR}</c> placeholder stays a
/// placeholder: writing an entry back can never turn a secret into a literal, which is why the editor works on this shape and
/// not on <see cref="McpServerConfig"/> (where the environment is already expanded).
/// <para><see cref="RawNodeJson"/> keeps the entry as it was read. An edit starts from it, so unknown fields of that entry
/// survive; an import writes it as it is, so placeholders and fields Forge does not know about come across unchanged.
/// Both nullable: an entry the user is creating has neither a raw node nor a source file yet.</para>
/// </summary>
public sealed record McpServerEntry
{
    public required string Name { get; init; }

    public string? Command { get; init; }

    public IReadOnlyList<string> Args { get; init; } = [];

    /// <summary>Pairs in file order; only the key is ever shown outside the editor.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> Env { get; init; } = [];

    public string? WorkingDirectory { get; init; }

    public string? Url { get; init; }

    public IReadOnlyList<KeyValuePair<string, string>> Headers { get; init; } = [];

    public bool Disabled { get; init; }

    public IReadOnlyList<string> ReadOnlyTools { get; init; } = [];

    public double? TimeoutSeconds { get; init; }

    /// <summary>The file that defined the entry; null for an entry being created.</summary>
    public string? SourcePath { get; init; }

    /// <summary>The entry as read, unknown fields included; null for an entry being created.</summary>
    public string? RawNodeJson { get; init; }

    public bool IsStdio => !string.IsNullOrWhiteSpace(Command);

    public bool IsHttp => !string.IsNullOrWhiteSpace(Url);

    /// <summary>Reads one entry verbatim; a field of an unexpected JSON kind is dropped instead of failing the whole entry.</summary>
    public static McpServerEntry FromNode(string name, JsonObject node, string? sourcePath = null)
    {
        ArgumentNullException.ThrowIfNull(node);
        return new McpServerEntry
        {
            Name = name,
            Command = Text(node, "command"),
            Args = StringList(node, "args"),
            Env = Pairs(node, "env"),
            WorkingDirectory = Text(node, "cwd"),
            Url = Text(node, "url"),
            Headers = Pairs(node, "headers"),
            Disabled = node["disabled"] is JsonValue disabled &&
                       disabled.TryGetValue<bool>(out var isDisabled) && isDisabled,
            ReadOnlyTools = StringList(node, "readOnlyTools"),
            TimeoutSeconds = Number(node, "timeoutSeconds"),
            SourcePath = sourcePath,
            RawNodeJson = node.ToJsonString(),
        };
    }

    /// <summary>Trims every text, drops blank args and the env/header rows whose key is blank.</summary>
    public McpServerEntry Normalized() => this with
    {
        Name = Name.Trim(),
        Command = Clean(Command),
        Args = CleanList(Args),
        Env = CleanPairs(Env),
        WorkingDirectory = Clean(WorkingDirectory),
        Url = Clean(Url),
        Headers = CleanPairs(Headers),
        ReadOnlyTools = CleanList(ReadOnlyTools),
    };

    /// <summary>
    /// What is wrong with the entry, in the language of <see cref="McpEntryIssue"/> codes rather than sentences: the settings
    /// page owns the words. Empty means the entry can be written.
    /// </summary>
    public IReadOnlyList<McpEntryIssue> Validate()
    {
        var normalized = Normalized();
        var issues = new List<McpEntryIssue>(4);
        if (normalized.Name.Length == 0)
        {
            issues.Add(new McpEntryIssue(McpEntryField.Name, McpEntryProblem.Required));
        }
        else if (normalized.Name.Any(char.IsControl) || normalized.Name.Contains('"', StringComparison.Ordinal))
        {
            issues.Add(new McpEntryIssue(McpEntryField.Name, McpEntryProblem.InvalidCharacter));
        }

        if (normalized.IsStdio && normalized.IsHttp)
        {
            issues.Add(new McpEntryIssue(McpEntryField.Transport, McpEntryProblem.BothSet));
        }
        else if (!normalized.IsStdio && !normalized.IsHttp)
        {
            issues.Add(new McpEntryIssue(McpEntryField.Transport, McpEntryProblem.NeitherSet));
        }
        else if (normalized.IsHttp &&
                 (!Uri.TryCreate(normalized.Url, UriKind.Absolute, out var uri) ||
                  (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)))
        {
            issues.Add(new McpEntryIssue(McpEntryField.Url, McpEntryProblem.NotHttpUrl, normalized.Url));
        }

        if (normalized.TimeoutSeconds is { } seconds &&
            (!double.IsFinite(seconds) || seconds <= 0 || seconds > McpConfigLoader.MaxTimeoutSeconds))
        {
            issues.Add(new McpEntryIssue(
                McpEntryField.Timeout,
                McpEntryProblem.OutOfRange,
                McpConfigLoader.MaxTimeoutSeconds.ToString("0", System.Globalization.CultureInfo.InvariantCulture)));
        }

        AddDuplicateIssue(issues, normalized.Env, McpEntryField.Env);
        AddDuplicateIssue(issues, normalized.Headers, McpEntryField.Headers);
        return issues;
    }

    /// <summary>The entry as JSON; a field at its default is omitted, so a saved entry stays as short as it was written.</summary>
    public JsonObject ToNode() => ToNode(new JsonObject());

    /// <summary>
    /// Writes the managed fields onto <paramref name="template"/> and leaves every other field alone: that is how an edit keeps
    /// the fields of an entry Forge does not understand.
    /// </summary>
    public JsonObject ToNode(JsonObject template)
    {
        ArgumentNullException.ThrowIfNull(template);
        var normalized = Normalized();
        SetOrRemove(template, "command", normalized.Command);
        SetArray(template, "args", normalized.Args);
        SetPairs(template, "env", normalized.Env);
        SetOrRemove(template, "cwd", normalized.WorkingDirectory);
        SetOrRemove(template, "url", normalized.Url);
        SetPairs(template, "headers", normalized.Headers);
        if (normalized.Disabled)
        {
            template["disabled"] = true;
        }
        else
        {
            template.Remove("disabled");
        }

        SetArray(template, "readOnlyTools", normalized.ReadOnlyTools);
        if (normalized.TimeoutSeconds is { } seconds)
        {
            template["timeoutSeconds"] = seconds;
        }
        else
        {
            template.Remove("timeoutSeconds");
        }

        return template;
    }

    private static void AddDuplicateIssue(
        List<McpEntryIssue> issues,
        IReadOnlyList<KeyValuePair<string, string>> pairs,
        McpEntryField field)
    {
        if (pairs.Count < 2)
        {
            return;
        }

        var seen = new HashSet<string>(pairs.Count, StringComparer.Ordinal);
        foreach (var pair in pairs)
        {
            if (!seen.Add(pair.Key))
            {
                issues.Add(new McpEntryIssue(field, McpEntryProblem.DuplicateKey, pair.Key));
                return;
            }
        }
    }

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static IReadOnlyList<string> CleanList(IReadOnlyList<string> values)
    {
        var kept = new List<string>(values.Count);
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                kept.Add(value.Trim());
            }
        }

        return kept.Count == 0 ? [] : kept;
    }

    private static IReadOnlyList<KeyValuePair<string, string>> CleanPairs(IReadOnlyList<KeyValuePair<string, string>> pairs)
    {
        var kept = new List<KeyValuePair<string, string>>(pairs.Count);
        foreach (var pair in pairs)
        {
            var key = pair.Key.Trim();
            if (key.Length == 0)
            {
                continue;
            }

            kept.Add(new KeyValuePair<string, string>(key, pair.Value));
        }

        return kept.Count == 0 ? [] : kept;
    }

    private static string? Text(JsonObject node, string property) =>
        node[property] is JsonValue value && value.TryGetValue<string>(out var text) && text.Length > 0 ? text : null;

    private static double? Number(JsonObject node, string property) =>
        node[property] is JsonValue value && value.TryGetValue<double>(out var number) ? number : null;

    private static IReadOnlyList<string> StringList(JsonObject node, string property)
    {
        if (node[property] is not JsonArray array)
        {
            return [];
        }

        var list = new List<string>(array.Count);
        foreach (var item in array)
        {
            if (item is JsonValue value && value.TryGetValue<string>(out var text) && text.Length > 0)
            {
                list.Add(text);
            }
        }

        return list;
    }

    private static IReadOnlyList<KeyValuePair<string, string>> Pairs(JsonObject node, string property)
    {
        if (node[property] is not JsonObject pairs || pairs.Count == 0)
        {
            return [];
        }

        var list = new List<KeyValuePair<string, string>>(pairs.Count);
        foreach (var pair in pairs)
        {
            list.Add(new KeyValuePair<string, string>(pair.Key, Scalar(pair.Value)));
        }

        return list;
    }

    /// <summary>A pair value is free-form in the loader (a string, a number, null); the editor keeps it as text.</summary>
    private static string Scalar(JsonNode? value) => value switch
    {
        null => string.Empty,
        JsonValue scalar when scalar.TryGetValue<string>(out var text) => text,
        _ => value.ToJsonString(),
    };

    private static void SetOrRemove(JsonObject node, string property, string? value)
    {
        if (value is null)
        {
            node.Remove(property);
        }
        else
        {
            node[property] = value;
        }
    }

    private static void SetArray(JsonObject node, string property, IReadOnlyList<string> values)
    {
        if (values.Count == 0)
        {
            node.Remove(property);
            return;
        }

        var array = new JsonArray();
        foreach (var value in values)
        {
            array.Add((JsonNode?)JsonValue.Create(value));
        }

        node[property] = array;
    }

    private static void SetPairs(JsonObject node, string property, IReadOnlyList<KeyValuePair<string, string>> pairs)
    {
        if (pairs.Count == 0)
        {
            node.Remove(property);
            return;
        }

        var map = new JsonObject();
        foreach (var pair in pairs)
        {
            map[pair.Key] = pair.Value;
        }

        node[property] = map;
    }
}

/// <summary>The fields an entry's issue can be about; the settings page turns one into the label of its row.</summary>
public enum McpEntryField
{
    Name,
    Transport,
    Command,
    Url,
    Timeout,
    Env,
    Headers,
}

/// <summary>What is wrong with one field of an entry; the settings page owns the sentence for each code.</summary>
public enum McpEntryProblem
{
    Required,
    InvalidCharacter,
    BothSet,
    NeitherSet,
    NotHttpUrl,
    OutOfRange,
    DuplicateKey,
}

/// <summary>One problem of one entry; <paramref name="Detail"/> is the offending url, key or bound, never personal data.</summary>
public sealed record McpEntryIssue(McpEntryField Field, McpEntryProblem Problem, string? Detail = null);

/// <summary>One file's view of <c>mcpServers</c>: its entries in file order and the problems that belong to the file itself.</summary>
public sealed record McpConfigEntries(
    IReadOnlyList<McpServerEntry> Entries,
    IReadOnlyList<McpConfigDiagnostic> Diagnostics);

/// <summary>
/// Reads and writes the text of one <c>mcp.json</c>. Pure on purpose: no file, no lock, no environment, so the whole merge is
/// testable and the settings page can stage an edit before touching the disk.
/// <para>Only the entry it is given is replaced: other entries, other root properties and the unknown fields of the entry
/// being edited survive. JSON comments do not survive a write — <see cref="JsonNode"/> has no way to keep them — and the file
/// is re-serialized with two-space indentation.</para>
/// </summary>
public static class McpConfigFile
{
    public const string ServersProperty = "mcpServers";

    private static readonly JsonDocumentOptions ReadOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    /// <summary>Reads the entries verbatim; a broken entry is a diagnostic, never a failure, so the page can repair it.</summary>
    public static McpConfigEntries Read(string? json, string? sourcePath = null)
    {
        var label = sourcePath ?? "(no file)";
        if (json is null)
        {
            // No file: nothing to report. An existing but empty file is different, and falls through to the parse below, which
            // calls it invalid JSON — exactly what the runtime read says about it.
            return new McpConfigEntries([], []);
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, ReadOptions);
        }
        catch (JsonException exception)
        {
            return new McpConfigEntries([], [new McpConfigDiagnostic(label, null, "Invalid JSON: " + exception.Message)]);
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return new McpConfigEntries([], [new McpConfigDiagnostic(label, null, "The document root is not an object.")]);
            }

            if (!document.RootElement.TryGetProperty(ServersProperty, out var servers))
            {
                // The runtime read calls this a problem, so the file view must too: otherwise a hand edit that dropped the key
                // hides every server from the page while the manager keeps running them.
                return new McpConfigEntries([], [new McpConfigDiagnostic(label, null, "No \"mcpServers\" object.")]);
            }

            if (servers.ValueKind != JsonValueKind.Object)
            {
                return new McpConfigEntries([], [new McpConfigDiagnostic(label, null, "\"mcpServers\" is not an object.")]);
            }

            var entries = new List<McpServerEntry>(servers.GetPropertyCount());
            var diagnostics = new List<McpConfigDiagnostic>();
            foreach (var property in servers.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.Object)
                {
                    diagnostics.Add(new McpConfigDiagnostic(label, property.Name, "The server entry is not an object."));
                    continue;
                }

                var node = JsonNode.Parse(property.Value.GetRawText()) as JsonObject;
                if (node is null)
                {
                    diagnostics.Add(new McpConfigDiagnostic(label, property.Name, "The server entry could not be read."));
                    continue;
                }

                entries.Add(McpServerEntry.FromNode(property.Name, node, sourcePath));
            }

            return new McpConfigEntries(entries, diagnostics);
        }
    }

    /// <summary>
    /// Adds or replaces one entry. <paramref name="replacedName"/> is the name the entry had before a rename: when it differs,
    /// that key is removed in the same write, so renaming a server never leaves the old one behind as a live second entry.
    /// Throws <see cref="ArgumentException"/> when the entry does not validate.
    /// </summary>
    public static string Upsert(string? json, McpServerEntry entry, string? replacedName = null)
    {
        ArgumentNullException.ThrowIfNull(entry);
        RequireValid(entry);
        var remove = replacedName is { Length: > 0 } previous &&
                     !string.Equals(previous, entry.Name.Trim(), StringComparison.Ordinal)
            ? new[] { previous }
            : null;
        return Apply(json, [entry], remove);
    }

    /// <summary>
    /// Writes entries read from someone else's file as they are: the raw node is copied, so placeholders, unknown fields and even
    /// the kind of a value survive. Only the name is required — a field this app would refuse is the source file's business, and
    /// the page reports it after the next read instead of refusing the import.
    /// </summary>
    public static string Import(string? json, IReadOnlyList<McpServerEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        foreach (var entry in entries)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(entry.Name);
        }

        return Apply(json, entries, null, verbatim: true);
    }

    /// <summary>Removes one entry; a name that is not there is not an error (the file is simply rewritten as it is).</summary>
    public static string Remove(string? json, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return Apply(json, [], [name]);
    }

    private static void RequireValid(McpServerEntry entry)
    {
        var issues = entry.Validate();
        if (issues.Count > 0)
        {
            var first = issues[0];
            throw new ArgumentException(
                $"The entry '{entry.Name}' is not valid: {first.Field}/{first.Problem}" +
                (first.Detail is { Length: > 0 } detail ? " (" + detail + ")" : string.Empty),
                nameof(entry));
        }
    }

    private static string Apply(
        string? json,
        IReadOnlyList<McpServerEntry> entries,
        IReadOnlyList<string>? remove,
        bool verbatim = false)
    {
        var root = Root(json);
        if (root[ServersProperty] is { } existing && existing is not JsonObject)
        {
            throw new InvalidDataException($"\"{ServersProperty}\" is not an object.");
        }

        var servers = root[ServersProperty] as JsonObject ?? new JsonObject();
        if (remove is not null)
        {
            foreach (var name in remove)
            {
                servers.Remove(name);
            }
        }

        foreach (var entry in entries)
        {
            if (verbatim && entry.RawNodeJson is { Length: > 0 } raw &&
                JsonNode.Parse(raw, documentOptions: ReadOptions) is JsonObject asRead)
            {
                servers[entry.Name.Trim()] = asRead;
                continue;
            }

            var template = TemplateFor(servers, entry);
            servers[entry.Name.Trim()] = entry.ToNode(template);
        }

        root[ServersProperty] = servers;
        return root.ToJsonString(WriteOptions) + "\n";
    }

    /// <summary>An edit starts from the entry as it was read, so the fields Forge does not manage survive it.</summary>
    private static JsonObject TemplateFor(JsonObject servers, McpServerEntry entry)
    {
        if (entry.RawNodeJson is { Length: > 0 } raw &&
            JsonNode.Parse(raw, documentOptions: ReadOptions) is JsonObject fromFile)
        {
            return (JsonObject)fromFile.DeepClone();
        }

        return servers[entry.Name.Trim()] is JsonObject existing ? (JsonObject)existing.DeepClone() : new JsonObject();
    }

    private static JsonObject Root(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new JsonObject();
        }

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(json, documentOptions: ReadOptions);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The file is not valid JSON: " + exception.Message, exception);
        }

        return node as JsonObject ?? throw new InvalidDataException("The document root is not an object.");
    }
}
