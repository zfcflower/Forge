using System.Text.Json;
using Forge.Abstractions;
using Forge.App.Services;

namespace Forge.App.Agents;

/// <summary>
/// <c>skill</c>: the whole <c>SKILL.md</c> of one discovered skill, by name. The composer's <c>/</c> picker only inserts
/// <c>/name</c> into the draft, and the product's own skills are materialized outside the workspace where <c>read</c>
/// refuses to go (<c>WorkspacePath.Resolve</c>), so this tool is the one path by which a skill's text reaches the model.
/// <para>
/// The name is resolved against the catalog the session discovered, never against a path the model supplies: a skill file
/// outside those roots is unreachable, and no argument can walk out of them. Read-only, so it needs no approval.
/// </para>
/// </summary>
internal sealed class SkillTool : ITool
{
    public const string Name = "skill";

    /// <summary>Cap on one skill's text; past this the file is not a playbook any more and the result says it was cut.</summary>
    internal const int MaxTextChars = 64_000;

    private const string SchemaJson =
        """{"type":"object","properties":{"name":{"type":"string","description":"Skill name as the skill index or the / picker lists it."}},"required":["name"],"additionalProperties":false}""";

    private const string Description =
        "Read one skill's full instructions (its SKILL.md) by name. Skills are the playbooks this workspace, the user "
        + "directory and Forge itself provide; the skill index in your prompt lists the names and what each is for. "
        + "Read a skill before following it and follow the text you read, never a skill from memory. "
        + "An unknown name returns the names that are available.";

    private readonly Func<IReadOnlyList<SlashEntry>> skills;

    /// <param name="skills">The skills discovered for this session's workspace; called per invocation so an edit is picked up.</param>
    public SkillTool(Func<IReadOnlyList<SlashEntry>> skills)
    {
        ArgumentNullException.ThrowIfNull(skills);
        this.skills = skills;
        Definition = new ToolDefinition(Name, Description, SchemaJson);
    }

    public ToolDefinition Definition { get; }

    public ToolSideEffect SideEffect => ToolSideEffect.ReadOnly;

    public ValueTask<ToolResult> InvokeAsync(ToolCall call, ToolContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(call);
        ArgumentNullException.ThrowIfNull(context);

        string name;
        try
        {
            using var document = JsonDocument.Parse(call.ArgumentsJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return ValueTask.FromResult(new ToolResult($"{Name} arguments must be a JSON object.", IsError: true));
            }

            if (!root.TryGetProperty("name", out var element) || element.ValueKind != JsonValueKind.String)
            {
                return ValueTask.FromResult(new ToolResult($"{Name} requires a 'name' string.", IsError: true));
            }

            name = element.GetString()!.Trim();
        }
        catch (JsonException exception)
        {
            return ValueTask.FromResult(new ToolResult($"{Name} arguments are invalid: {exception.Message}", IsError: true));
        }

        if (name.Length == 0)
        {
            return ValueTask.FromResult(new ToolResult($"{Name} requires a 'name' string.", IsError: true));
        }

        ct.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Read(name));
    }

    private ToolResult Read(string name)
    {
        IReadOnlyList<SlashEntry> catalog;
        try
        {
            catalog = skills();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new ToolResult($"{Name} could not list the skills: {exception.Message}", IsError: true);
        }

        SlashEntry? found = null;
        foreach (var entry in catalog)
        {
            if (entry.IsSkill && string.Equals(entry.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                found = entry;
                break;
            }
        }

        if (found is null)
        {
            return new ToolResult(
                $"No skill named '{name}' is available. {Available(catalog)}",
                IsError: true);
        }

        if (found.Path.Length == 0)
        {
            return new ToolResult($"Skill '{found.Name}' has no file to read.", IsError: true);
        }

        string text;
        try
        {
            text = File.ReadAllText(found.Path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new ToolResult($"Skill '{found.Name}' could not be read: {exception.Message}", IsError: true);
        }

        if (text.Length <= MaxTextChars)
        {
            return new ToolResult(text);
        }

        // A cut that lands inside a surrogate pair would leave a lone surrogate in the result; step back one char then.
        var cut = MaxTextChars;
        if (char.IsHighSurrogate(text[cut - 1]))
        {
            cut--;
        }

        return new ToolResult(
            text[..cut]
            + $"\n\n[… {text.Length - cut} characters truncated; this skill is longer than one {Name} result.]",
            IsError: false);
    }

    private static string Available(IReadOnlyList<SlashEntry> catalog)
    {
        var names = new List<string>(catalog.Count);
        foreach (var entry in catalog)
        {
            if (entry.IsSkill)
            {
                names.Add(entry.Name);
            }
        }

        return names.Count == 0
            ? "No skills are discovered for this workspace."
            : "Available: " + string.Join(", ", names) + ".";
    }
}