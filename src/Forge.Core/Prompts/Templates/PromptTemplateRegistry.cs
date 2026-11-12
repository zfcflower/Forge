using System.Diagnostics.CodeAnalysis;
using Forge.Abstractions;
using Forge.Core.Agents;

namespace Forge.Core.Prompts.Templates;

/// <summary>Ids of the built-in prompt slots Core owns. Other assemblies register their own slots (see <see cref="PromptTemplateRegistry.With"/>).</summary>
public static class PromptSlotIds
{
    /// <summary>The first layer of every agent prompt (<see cref="BuiltInPrompts.System"/>).</summary>
    public const string System = "system";

    /// <summary>The context compactor's summarizer (<see cref="SummaryCompactor.SummarizerPrompt"/>).</summary>
    public const string CompactionSummarizer = "compaction.summarizer";

    /// <summary>The handover note writer (<see cref="HandoverSummary.Prompt"/>).</summary>
    public const string HandoverSummary = "handover.summary";
}

/// <summary>
/// One compiled-in template. <see cref="Placeholders"/> lists the <c>{name}</c> tokens a slot is filled with (empty =
/// the text is used verbatim). <see cref="Agent"/> carries the full definition of a built-in agent kind.
/// </summary>
public sealed record BuiltInPromptTemplate(
    PromptTemplateKind Kind,
    string Id,
    string DisplayName,
    string Description,
    string Body,
    IReadOnlyList<string> Placeholders,
    string? ArgumentHint = null,
    AgentDefinition? Agent = null);

/// <summary>
/// The compiled-in templates: Core's agents, commands and slots, plus whatever the composition root layers on (the
/// pull request workflow texts live in <c>Forge.Workspace</c>, the automation prompts in the app). Immutable; ids are
/// unique per kind and ordered as registered.
/// </summary>
public sealed class PromptTemplateRegistry
{
    private readonly BuiltInPromptTemplate[] all;
    private readonly Dictionary<(PromptTemplateKind, string), BuiltInPromptTemplate> byKey;

    private PromptTemplateRegistry(IEnumerable<BuiltInPromptTemplate> templates)
    {
        var list = new List<BuiltInPromptTemplate>();
        byKey = [];
        foreach (var template in templates)
        {
            if (!PromptTemplateIds.IsValid(template.Id))
            {
                throw new ArgumentException($"Invalid built-in template id '{template.Id}'.", nameof(templates));
            }

            if (!byKey.TryAdd((template.Kind, template.Id), template))
            {
                throw new ArgumentException($"Duplicate built-in {template.Kind} '{template.Id}'.", nameof(templates));
            }

            list.Add(template);
        }

        all = [.. list];
    }

    /// <summary>Core's built-ins: the four agent kinds, the starter commands and the Core slots.</summary>
    public static PromptTemplateRegistry Core { get; } = new(CoreTemplates());

    public IReadOnlyList<BuiltInPromptTemplate> All => all;

    public bool TryGet(PromptTemplateKind kind, string id, [NotNullWhen(true)] out BuiltInPromptTemplate? template) =>
        byKey.TryGetValue((kind, id), out template);

    public BuiltInPromptTemplate? Find(PromptTemplateKind kind, string id) => byKey.GetValueOrDefault((kind, id));

    /// <summary>A new registry with <paramref name="templates"/> added after this one's; a duplicate id throws.</summary>
    public PromptTemplateRegistry With(IEnumerable<BuiltInPromptTemplate> templates) => new([.. all, .. templates]);

    /// <summary>Slot helper for other assemblies: a verbatim (or placeholder-filled) built-in prompt.</summary>
    public static BuiltInPromptTemplate Slot(string id, string displayName, string description, string body, params string[] placeholders) =>
        new(PromptTemplateKind.Slot, id, displayName, description, body, placeholders);

    private static IEnumerable<BuiltInPromptTemplate> CoreTemplates()
    {
        foreach (var agent in BuiltInAgents.All)
        {
            yield return new BuiltInPromptTemplate(
                PromptTemplateKind.Agent,
                PromptTemplateIds.FromAgentName(agent.Name),
                agent.Name,
                agent.Description,
                agent.Prompt,
                [],
                Agent: agent);
        }

        foreach (var command in BuiltInCommands.All)
        {
            yield return command;
        }

        yield return Slot(
            PromptSlotIds.System,
            "System",
            "The first layer of every agent prompt, before the global and project AGENTS.md.",
            BuiltInPrompts.System);
        yield return Slot(
            PromptSlotIds.CompactionSummarizer,
            "Compaction summarizer",
            "System prompt of the summarizer that turns older conversation into a structured checkpoint when the context window fills up.",
            SummaryCompactor.SummarizerPrompt);
        yield return Slot(
            PromptSlotIds.HandoverSummary,
            "Handover summary",
            "Writes the note a new session starts from when work is handed over.",
            HandoverSummary.Prompt);
    }
}

/// <summary>The id rule shared by every template: it is also the file name, so it must be portable.</summary>
public static class PromptTemplateIds
{
    public const int MaxLength = 64;

    private static readonly string[] ReservedDeviceNames =
        ["con", "prn", "aux", "nul", "com1", "com2", "com3", "com4", "com5", "com6", "com7", "com8", "com9",
         "lpt1", "lpt2", "lpt3", "lpt4", "lpt5", "lpt6", "lpt7", "lpt8", "lpt9"];

    /// <summary>1-64 chars of <c>a-z 0-9 . _ -</c>, starting with a letter or digit, not ending with a dot, not a Windows device name.</summary>
    public static bool IsValid([NotNullWhen(true)] string? id)
    {
        if (string.IsNullOrEmpty(id) || id.Length > MaxLength || !IsLowerOrDigit(id[0]) || id[^1] == '.')
        {
            return false;
        }

        foreach (var c in id)
        {
            if (!IsLowerOrDigit(c) && c is not ('.' or '_' or '-'))
            {
                return false;
            }
        }

        var stem = id.Split('.')[0];
        return !ReservedDeviceNames.Contains(stem, StringComparer.Ordinal);
    }

    private static bool IsLowerOrDigit(char c) => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c);

    /// <summary>The template id of an agent name: lower-case, spaces to dashes.</summary>
    public static string FromAgentName(string name) =>
        name.Trim().ToLowerInvariant().Replace(' ', '-');
}
