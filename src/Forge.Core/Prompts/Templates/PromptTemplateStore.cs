using System.Security.Cryptography;
using System.Text;
using Forge.Abstractions;

namespace Forge.Core.Prompts.Templates;

/// <summary>
/// File-backed <see cref="IPromptTemplateStore"/>. Layout, one Markdown file per template (<c>&lt;id&gt;.md</c>):
/// <list type="bullet">
/// <item>global: <c>&lt;userDir&gt;/{commands,agents,prompts}/</c>, where <c>userDir</c> is <c>~/.forge</c>;</item>
/// <item>project: <c>&lt;workspace&gt;/.forge/{commands,agents,prompts}/</c>.</item>
/// </list>
/// Built-ins come from the <see cref="PromptTemplateRegistry"/> and are read-only. Files are re-read on every call
/// (they are small and few); <see cref="Resolve"/> is the snapshot a session builds its prompts from, so an edit takes
/// effect for the next agent host, never in the middle of one (the cached prefix stays byte-stable).
/// </summary>
public sealed class PromptTemplateStore : IPromptTemplateStore
{
    public const string CommandsDirectory = "commands";
    public const string AgentsDirectory = "agents";
    public const string SlotsDirectory = "prompts";
    public const string FileExtension = ".md";
    public const int MaxFileBytes = 256 * 1024;

    internal const string ModeKey = "mode";
    internal const string DescriptionKey = "description";
    internal const string ArgumentHintKey = "argument-hint";
    internal const string NameKey = "name";
    internal const string ModelKey = "model";
    internal const string ThinkingKey = "thinking";
    internal const string ToolsKey = "tools";

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly Lock writeGate = new();

    public PromptTemplateStore(PromptTemplateRegistry registry, string? userDirectory)
    {
        ArgumentNullException.ThrowIfNull(registry);
        Registry = registry;
        UserDirectory = string.IsNullOrWhiteSpace(userDirectory) ? null : userDirectory;
    }

    public PromptTemplateRegistry Registry { get; }

    /// <summary>The global root (<c>~/.forge</c>); null = no global scope (it is then read-only and empty).</summary>
    public string? UserDirectory { get; }

    /// <summary>The directory holding <paramref name="kind"/> files of <paramref name="scope"/>; null for built-ins or a missing root.</summary>
    public string? DirectoryFor(PromptTemplateKind kind, PromptTemplateScope scope, string? workspace)
    {
        var root = scope switch
        {
            PromptTemplateScope.Global => UserDirectory,
            PromptTemplateScope.Project when !string.IsNullOrWhiteSpace(workspace) => Path.Combine(workspace, LayerSources.ForgeDirectoryName),
            _ => null,
        };

        return root is null ? null : Path.Combine(root, SubDirectory(kind));
    }

    /// <summary>The file a reference lives in; null when the scope has no root or the id is invalid.</summary>
    public string? PathFor(PromptTemplateRef reference, string? workspace)
    {
        ArgumentNullException.ThrowIfNull(reference);
        if (!PromptTemplateIds.IsValid(reference.Id))
        {
            return null;
        }

        var directory = DirectoryFor(reference.Kind, reference.Scope, workspace);
        return directory is null ? null : Path.Combine(directory, reference.Id + FileExtension);
    }

    public static string SubDirectory(PromptTemplateKind kind) => kind switch
    {
        PromptTemplateKind.Command => CommandsDirectory,
        PromptTemplateKind.Agent => AgentsDirectory,
        _ => SlotsDirectory,
    };

    public IReadOnlyList<PromptTemplateEntry> List(string? workspace)
    {
        var entries = new List<PromptTemplateEntry>();
        foreach (var kind in Kinds)
        {
            foreach (var chain in Chains(kind, workspace))
            {
                var topReplace = -1;
                for (var i = chain.Count - 1; i >= 0; i--)
                {
                    if (chain[i].IsValid && chain[i].Mode == PromptTemplateMode.Replace)
                    {
                        topReplace = i;
                        break;
                    }
                }

                var hasBuiltIn = Registry.Find(kind, chain[0].Id) is not null;
                for (var i = 0; i < chain.Count; i++)
                {
                    var item = chain[i];
                    entries.Add(new PromptTemplateEntry(
                        new PromptTemplateRef(kind, item.Scope, item.Id),
                        item.DisplayName,
                        item.Description,
                        item.Mode,
                        item.Path,
                        IsShadowed: i < topReplace,
                        hasBuiltIn,
                        item.Problems));
                }
            }
        }

        return entries;
    }

    public PromptTemplateText? Read(PromptTemplateRef reference, string? workspace)
    {
        ArgumentNullException.ThrowIfNull(reference);
        if (reference.Scope == PromptTemplateScope.BuiltIn)
        {
            return Registry.Find(reference.Kind, reference.Id) is { } builtIn
                ? new PromptTemplateText(reference, BuiltInText(builtIn), null, null)
                : null;
        }

        var path = PathFor(reference, workspace);
        if (path is null || !File.Exists(path))
        {
            return null;
        }

        // The same guards as the listing: an oversized or undecodable file is never handed to the editor (it would stall
        // the text box, or open empty and let a save overwrite bytes the user never saw).
        if (new FileInfo(path).Length > MaxFileBytes)
        {
            throw new IOException($"'{path}' is larger than {MaxFileBytes / 1024} KiB.");
        }

        var bytes = File.ReadAllBytes(path);
        var text = Decode(bytes, out var error) ?? throw new IOException($"'{path}' is not valid UTF-8: {error}");
        return new PromptTemplateText(reference, text, Revision(bytes), path);
    }

    public PromptTemplateSaveResult Save(PromptTemplateRef reference, string? workspace, string text, string? expectedRevision)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(text);
        var path = reference.Scope == PromptTemplateScope.BuiltIn ? null : PathFor(reference, workspace);
        if (path is null)
        {
            return PromptTemplateIds.IsValid(reference.Id) || reference.Scope == PromptTemplateScope.BuiltIn
                ? new PromptTemplateSaveResult(PromptTemplateSaveStatus.ReadOnly, null, [])
                : new PromptTemplateSaveResult(PromptTemplateSaveStatus.Invalid, null, [new(PromptTemplateProblemCode.InvalidId, reference.Id)]);
        }

        var problems = Validate(reference.Kind, reference.Id, text);
        if (problems.Count > 0)
        {
            return new PromptTemplateSaveResult(PromptTemplateSaveStatus.Invalid, null, problems);
        }

        var bytes = StrictUtf8.GetBytes(text);
        lock (writeGate)
        {
            if (!MatchesRevision(path, expectedRevision))
            {
                return new PromptTemplateSaveResult(PromptTemplateSaveStatus.Conflict, null, []);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllBytes(temp, bytes);
                File.Move(temp, path, overwrite: true);
            }
            finally
            {
                if (File.Exists(temp))
                {
                    File.Delete(temp);
                }
            }
        }

        return new PromptTemplateSaveResult(PromptTemplateSaveStatus.Saved, Revision(bytes), []);
    }

    public PromptTemplateSaveStatus Delete(PromptTemplateRef reference, string? workspace, string expectedRevision)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(expectedRevision);
        var path = reference.Scope == PromptTemplateScope.BuiltIn ? null : PathFor(reference, workspace);
        if (path is null)
        {
            return PromptTemplateSaveStatus.ReadOnly;
        }

        lock (writeGate)
        {
            if (!File.Exists(path) || !MatchesRevision(path, expectedRevision))
            {
                return PromptTemplateSaveStatus.Conflict;
            }

            File.Delete(path);
        }

        return PromptTemplateSaveStatus.Saved;
    }

    public IReadOnlyList<PromptTemplateProblem> Validate(PromptTemplateKind kind, string id, string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return Parse(kind, PromptTemplateScope.Global, id ?? string.Empty, null, text).Problems;
    }

    /// <summary>
    /// The effective templates for <paramref name="workspace"/>: per kind and id, the chain built-in → global → project
    /// (valid files only) composed from its last replace-mode member. Never throws for file problems.
    /// </summary>
    public PromptTemplateSet Resolve(string? workspace)
    {
        var commands = new List<ResolvedCommand>();
        var agents = new List<ResolvedAgent>();
        var agentNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var slots = new Dictionary<string, ResolvedSlot>(StringComparer.Ordinal);
        foreach (var kind in Kinds)
        {
            foreach (var chain in Chains(kind, workspace))
            {
                var valid = chain.Where(static item => item.IsValid).ToList();
                if (valid.Count == 0)
                {
                    continue;
                }

                var start = valid.FindLastIndex(static item => item.Mode == PromptTemplateMode.Replace);
                var used = valid.GetRange(Math.Max(start, 0), valid.Count - Math.Max(start, 0));
                var body = ComposeBody(used);
                var top = used[^1];
                switch (kind)
                {
                    case PromptTemplateKind.Command:
                        commands.Add(new ResolvedCommand(
                            top.Id,
                            Last(used, DescriptionKey) ?? Registry.Find(kind, top.Id)?.Description ?? string.Empty,
                            Last(used, ArgumentHintKey) ?? Registry.Find(kind, top.Id)?.ArgumentHint,
                            body,
                            top.Scope));
                        break;
                    case PromptTemplateKind.Agent:
                        // Built-ins come first and their names are reserved, so a clash here is between two user
                        // agents; the first id (ordinal) wins and the later one is left out rather than shadowing it.
                        var agent = ResolveAgent(top.Id, used, body);
                        if (agentNames.Add(agent.Definition.Name))
                        {
                            agents.Add(agent);
                        }

                        break;
                    default:
                        if (Registry.Find(kind, top.Id) is { } slot)
                        {
                            slots[top.Id] = new ResolvedSlot(body, slot.Placeholders, top.Scope);
                        }

                        break;
                }
            }
        }

        return new PromptTemplateSet(commands, agents, slots);
    }

    /// <summary>The text an editor starts from when the user copies a built-in: its frontmatter plus body.</summary>
    public static string BuiltInText(BuiltInPromptTemplate template)
    {
        ArgumentNullException.ThrowIfNull(template);
        var scalars = new List<KeyValuePair<string, string>>();
        switch (template.Kind)
        {
            case PromptTemplateKind.Command:
                scalars.Add(new(DescriptionKey, template.Description));
                if (template.ArgumentHint is { } hint)
                {
                    scalars.Add(new(ArgumentHintKey, hint));
                }

                break;
            case PromptTemplateKind.Agent when template.Agent is { } agent:
                scalars.Add(new(NameKey, agent.Name));
                scalars.Add(new(DescriptionKey, agent.Description));
                scalars.Add(new(ThinkingKey, ThinkingName(agent.Model.Thinking)));
                if (agent.ToolNames is { Count: > 0 } tools)
                {
                    // Written inline (and unquoted by Render's rules, since it has brackets it would quote): see below.
                    scalars.Add(new(ToolsKey, string.Join(", ", tools)));
                }

                break;
        }

        return PromptFrontmatter.Render(scalars, TextNormalizer.Normalize(template.Body) + "\n", listKeys: [ToolsKey]);
    }

    internal static string Revision(ReadOnlySpan<byte> bytes)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(bytes, hash);
        return Convert.ToHexStringLower(hash[..12]);
    }

    private static readonly PromptTemplateKind[] Kinds = [PromptTemplateKind.Command, PromptTemplateKind.Agent, PromptTemplateKind.Slot];

    private static bool MatchesRevision(string path, string? expectedRevision)
    {
        if (!File.Exists(path))
        {
            return expectedRevision is null;
        }

        return expectedRevision is not null && string.Equals(Revision(File.ReadAllBytes(path)), expectedRevision, StringComparison.Ordinal);
    }

    private static string? Decode(byte[] bytes, out string? error)
    {
        error = null;
        try
        {
            var span = bytes.AsSpan();
            if (span.StartsWith(Encoding.UTF8.Preamble))
            {
                span = span[3..];
            }

            return StrictUtf8.GetString(span);
        }
        catch (DecoderFallbackException exception)
        {
            error = exception.Message;
            return null;
        }
    }

    /// <summary>Every id of <paramref name="kind"/>, each with its members in ascending scope; built-in ids first, in registry order.</summary>
    private List<List<ParsedTemplate>> Chains(PromptTemplateKind kind, string? workspace)
    {
        var byId = new Dictionary<string, List<ParsedTemplate>>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (var builtIn in Registry.All)
        {
            if (builtIn.Kind != kind)
            {
                continue;
            }

            byId[builtIn.Id] = [ParsedTemplate.FromBuiltIn(builtIn)];
            order.Add(builtIn.Id);
        }

        var extra = new List<string>();
        foreach (var scope in (ReadOnlySpan<PromptTemplateScope>)[PromptTemplateScope.Global, PromptTemplateScope.Project])
        {
            foreach (var file in ReadDirectory(kind, scope, workspace))
            {
                if (!byId.TryGetValue(file.Id, out var chain))
                {
                    byId[file.Id] = chain = [];
                    extra.Add(file.Id);
                }

                chain.Add(file);
            }
        }

        extra.Sort(StringComparer.Ordinal);
        order.AddRange(extra);
        return order.ConvertAll(id => byId[id]);
    }

    private IEnumerable<ParsedTemplate> ReadDirectory(PromptTemplateKind kind, PromptTemplateScope scope, string? workspace)
    {
        var directory = DirectoryFor(kind, scope, workspace);
        if (directory is null || !Directory.Exists(directory))
        {
            yield break;
        }

        string[] files;
        try
        {
            files = Directory.GetFiles(directory, "*" + FileExtension, SearchOption.TopDirectoryOnly);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        Array.Sort(files, StringComparer.Ordinal);
        foreach (var path in files)
        {
            if (!Path.GetExtension(path).Equals(FileExtension, StringComparison.Ordinal))
            {
                continue;
            }

            var id = Path.GetFileNameWithoutExtension(path);
            yield return ReadFile(kind, scope, id, path);
        }
    }

    private ParsedTemplate ReadFile(PromptTemplateKind kind, PromptTemplateScope scope, string id, string path)
    {
        byte[] bytes;
        try
        {
            var info = new FileInfo(path);
            if (info.Length > MaxFileBytes)
            {
                return ParsedTemplate.Broken(kind, scope, id, path, new(PromptTemplateProblemCode.TooLarge));
            }

            bytes = File.ReadAllBytes(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return ParsedTemplate.Broken(kind, scope, id, path, new(PromptTemplateProblemCode.Unreadable, exception.Message));
        }

        var text = Decode(bytes, out var error);
        return text is null
            ? ParsedTemplate.Broken(kind, scope, id, path, new(PromptTemplateProblemCode.Unreadable, error))
            : Parse(kind, scope, id, path, text);
    }

    private ParsedTemplate Parse(PromptTemplateKind kind, PromptTemplateScope scope, string id, string? path, string text)
    {
        var problems = new List<PromptTemplateProblem>();
        if (!PromptTemplateIds.IsValid(id))
        {
            problems.Add(new(PromptTemplateProblemCode.InvalidId, id));
        }

        if (Encoding.UTF8.GetByteCount(text) > MaxFileBytes)
        {
            problems.Add(new(PromptTemplateProblemCode.TooLarge));
        }

        var frontmatter = PromptFrontmatter.Parse(text);
        if (frontmatter.Unclosed)
        {
            problems.Add(new(PromptTemplateProblemCode.UnclosedFrontmatter));
        }

        var builtIn = Registry.Find(kind, id);
        if (kind == PromptTemplateKind.Slot && builtIn is null && PromptTemplateIds.IsValid(id))
        {
            problems.Add(new(PromptTemplateProblemCode.UnknownSlot, id));
        }

        foreach (var key in frontmatter.Keys)
        {
            if (!IsAllowedKey(kind, key))
            {
                problems.Add(new(PromptTemplateProblemCode.UnknownKey, key));
            }
        }

        var mode = PromptTemplateMode.Replace;
        if (frontmatter.Scalar(ModeKey) is { } modeText)
        {
            if (modeText.Equals("append", StringComparison.OrdinalIgnoreCase))
            {
                mode = PromptTemplateMode.Append;
            }
            else if (!modeText.Equals("replace", StringComparison.OrdinalIgnoreCase))
            {
                problems.Add(new(PromptTemplateProblemCode.InvalidValue, ModeKey));
            }
        }

        if (kind == PromptTemplateKind.Agent)
        {
            ValidateAgent(id, frontmatter, builtIn, problems);
        }

        var body = TextNormalizer.Normalize(frontmatter.Body);
        if (body.Length == 0)
        {
            problems.Add(new(PromptTemplateProblemCode.EmptyBody));
        }

        if (kind == PromptTemplateKind.Slot && builtIn is { Placeholders.Count: > 0 } slot)
        {
            foreach (var name in Placeholders(body))
            {
                if (!slot.Placeholders.Contains(name, StringComparer.Ordinal))
                {
                    problems.Add(new(PromptTemplateProblemCode.UnknownPlaceholder, name));
                }
            }
        }

        return new ParsedTemplate(
            kind,
            scope,
            id,
            path,
            mode,
            frontmatter,
            body,
            DisplayName(kind, id, frontmatter, builtIn),
            frontmatter.Scalar(DescriptionKey) ?? builtIn?.Description ?? string.Empty,
            problems);
    }

    /// <summary>
    /// Agents are spawned by name, so a name is an identity, not a label: a built-in's file keeps the built-in name (a
    /// rename would leave the built-in in place and add a copy), a user agent may not take a built-in's name or id (that
    /// would replace it in the host's catalog without its tool whitelist), and an empty tool list is refused rather than
    /// read as "every tool".
    /// </summary>
    private void ValidateAgent(string id, PromptFrontmatter frontmatter, BuiltInPromptTemplate? builtIn, List<PromptTemplateProblem> problems)
    {
        if (frontmatter.Scalar(ThinkingKey) is { } thinking && ParseThinking(thinking) is null)
        {
            problems.Add(new(PromptTemplateProblemCode.InvalidValue, ThinkingKey));
        }

        if (frontmatter.List(ToolsKey) is { Count: 0 })
        {
            problems.Add(new(PromptTemplateProblemCode.InvalidValue, ToolsKey));
        }

        var name = frontmatter.Scalar(NameKey);
        if (builtIn?.Agent is { } agent)
        {
            if (name is not null && !name.Equals(agent.Name, StringComparison.OrdinalIgnoreCase))
            {
                problems.Add(new(PromptTemplateProblemCode.InvalidValue, NameKey));
            }

            return;
        }

        var effective = name ?? id;
        foreach (var other in Registry.All)
        {
            if (other.Agent is { } reserved &&
                (effective.Equals(reserved.Name, StringComparison.OrdinalIgnoreCase) ||
                 effective.Equals(other.Id, StringComparison.OrdinalIgnoreCase)))
            {
                problems.Add(new(PromptTemplateProblemCode.InvalidValue, NameKey));
                return;
            }
        }
    }

    private static string DisplayName(PromptTemplateKind kind, string id, PromptFrontmatter frontmatter, BuiltInPromptTemplate? builtIn) =>
        kind == PromptTemplateKind.Agent
            ? builtIn?.DisplayName ?? frontmatter.Scalar(NameKey) ?? id
            : builtIn?.DisplayName ?? id;

    private static bool IsAllowedKey(PromptTemplateKind kind, string key) => key switch
    {
        ModeKey or DescriptionKey => true,
        ArgumentHintKey => kind == PromptTemplateKind.Command,
        NameKey or ModelKey or ThinkingKey or ToolsKey => kind == PromptTemplateKind.Agent,
        _ => false,
    };

    /// <summary><c>{name}</c> tokens whose name is a lower-case identifier; other braces (JSON, code) are not placeholders.</summary>
    internal static IEnumerable<string> Placeholders(string body)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < body.Length; i++)
        {
            if (body[i] != '{')
            {
                continue;
            }

            var end = i + 1;
            while (end < body.Length && (char.IsAsciiLetterLower(body[end]) || char.IsAsciiDigit(body[end]) || body[end] == '_'))
            {
                end++;
            }

            if (end > i + 1 && end < body.Length && body[end] == '}' && char.IsAsciiLetterLower(body[i + 1]))
            {
                var name = body[(i + 1)..end];
                if (seen.Add(name))
                {
                    yield return name;
                }
            }
        }
    }

    private static string ComposeBody(List<ParsedTemplate> used)
    {
        if (used.Count == 1)
        {
            return used[0].Body;
        }

        var builder = new StringBuilder();
        foreach (var item in used)
        {
            if (builder.Length > 0)
            {
                builder.Append("\n\n");
            }

            builder.Append(item.Body);
        }

        return builder.ToString();
    }

    private static string? Last(List<ParsedTemplate> used, string key)
    {
        for (var i = used.Count - 1; i >= 0; i--)
        {
            if (used[i].Frontmatter?.Scalar(key) is { } value)
            {
                return value;
            }
        }

        return null;
    }

    private static IReadOnlyList<string>? LastList(List<ParsedTemplate> used, string key)
    {
        for (var i = used.Count - 1; i >= 0; i--)
        {
            if (used[i].Frontmatter is not { } frontmatter)
            {
                continue;
            }

            if (frontmatter.List(key) is { } list)
            {
                return list;
            }

            if (frontmatter.Scalar(key) is { } single)
            {
                return [single];
            }
        }

        return null;
    }

    private ResolvedAgent ResolveAgent(string id, List<ParsedTemplate> used, string body)
    {
        // The built-in stays the baseline for metadata even when a replace-mode file starts the chain:
        // overriding Scout's prompt must not silently drop its read-only tool whitelist.
        var builtIn = Registry.Find(PromptTemplateKind.Agent, id)?.Agent;
        var name = builtIn?.Name ?? Last(used, NameKey) ?? id;
        var thinkingText = Last(used, ThinkingKey);
        var model = Last(used, ModelKey) ?? builtIn?.Model.Model ?? "default";
        var thinking = thinkingText is not null
            ? ParseThinking(thinkingText) ?? ThinkingEffort.Off
            : builtIn?.Model.Thinking ?? ThinkingEffort.Off;
        var tools = LastList(used, ToolsKey) ?? builtIn?.ToolNames;
        var definition = new AgentDefinition(
            name,
            Last(used, DescriptionKey) ?? builtIn?.Description ?? string.Empty,
            body,
            new ModelSettings(model, thinking),
            tools is { Count: > 0 } ? [.. tools] : null);
        return new ResolvedAgent(id, definition, ExplicitThinking: thinkingText is not null);
    }

    internal static ThinkingEffort? ParseThinking(string value) => value.Trim().ToLowerInvariant() switch
    {
        "off" => ThinkingEffort.Off,
        "minimal" => ThinkingEffort.Minimal,
        "low" => ThinkingEffort.Low,
        "medium" => ThinkingEffort.Medium,
        "high" => ThinkingEffort.High,
        "xhigh" => ThinkingEffort.XHigh,
        "max" => ThinkingEffort.Max,
        _ => null,
    };

    internal static string ThinkingName(ThinkingEffort effort) => effort switch
    {
        ThinkingEffort.Minimal => "minimal",
        ThinkingEffort.Low => "low",
        ThinkingEffort.Medium => "medium",
        ThinkingEffort.High => "high",
        ThinkingEffort.XHigh => "xhigh",
        ThinkingEffort.Max => "max",
        _ => "off",
    };

    private sealed record ParsedTemplate(
        PromptTemplateKind Kind,
        PromptTemplateScope Scope,
        string Id,
        string? Path,
        PromptTemplateMode Mode,
        PromptFrontmatter? Frontmatter,
        string Body,
        string DisplayName,
        string Description,
        IReadOnlyList<PromptTemplateProblem> Problems)
    {
        public bool IsValid => Problems.Count == 0;

        public static ParsedTemplate FromBuiltIn(BuiltInPromptTemplate template) => new(
            template.Kind,
            PromptTemplateScope.BuiltIn,
            template.Id,
            null,
            PromptTemplateMode.Replace,
            null,
            TextNormalizer.Normalize(template.Body),
            template.DisplayName,
            template.Description,
            []);

        public static ParsedTemplate Broken(PromptTemplateKind kind, PromptTemplateScope scope, string id, string path, PromptTemplateProblem problem) =>
            new(kind, scope, id, path, PromptTemplateMode.Replace, null, string.Empty, id, string.Empty, [problem]);
    }
}
