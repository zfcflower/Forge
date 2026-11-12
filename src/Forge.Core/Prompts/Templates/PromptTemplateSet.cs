using System.Text;
using Forge.Abstractions;

namespace Forge.Core.Prompts.Templates;

/// <summary>An effective slash command: the composed body of its chain and the scope of its topmost file.</summary>
public sealed record ResolvedCommand(string Name, string Description, string? ArgumentHint, string Body, PromptTemplateScope Scope);

/// <summary>
/// An effective agent kind: its template id (also its <c>settings.json</c> key), the definition, and whether the files set
/// <c>thinking</c> themselves (then a settings default replaces only the model, not the effort).
/// </summary>
public sealed record ResolvedAgent(string Id, AgentDefinition Definition, bool ExplicitThinking);

internal sealed record ResolvedSlot(string Text, IReadOnlyList<string> Placeholders, PromptTemplateScope Scope);

/// <summary>
/// An immutable snapshot of the effective templates (<see cref="PromptTemplateStore.Resolve"/>). A session takes one
/// when its agent host is built and keeps it, so prompt text never changes under a cached prefix.
/// </summary>
public sealed class PromptTemplateSet
{
    private readonly Dictionary<string, ResolvedCommand> commandsByName;
    private readonly Dictionary<string, ResolvedAgent> agentsByName;
    private readonly IReadOnlyDictionary<string, ResolvedSlot> slots;

    internal PromptTemplateSet(
        IReadOnlyList<ResolvedCommand> commands,
        IReadOnlyList<ResolvedAgent> agents,
        IReadOnlyDictionary<string, ResolvedSlot> slots)
    {
        Commands = commands;
        ResolvedAgents = agents;
        Agents = agents.Select(static agent => agent.Definition).ToArray();
        agentsByName = new Dictionary<string, ResolvedAgent>(agents.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var agent in agents)
        {
            agentsByName[agent.Definition.Name] = agent;
        }

        this.slots = slots;
        commandsByName = new Dictionary<string, ResolvedCommand>(commands.Count, StringComparer.Ordinal);
        foreach (var command in commands)
        {
            commandsByName[command.Name] = command;
        }
    }

    /// <summary>The compiled-in templates only; what a test or a runtime without a store uses.</summary>
    public static PromptTemplateSet BuiltIn(PromptTemplateRegistry? registry = null) =>
        new PromptTemplateStore(registry ?? PromptTemplateRegistry.Core, userDirectory: null).Resolve(workspace: null);

    /// <summary>Slash commands: built-in ids in registry order, then user ids ordinally.</summary>
    public IReadOnlyList<ResolvedCommand> Commands { get; }

    /// <summary>Agent kinds: the built-ins (possibly overridden) in registry order, then user agents ordinally by id.</summary>
    public IReadOnlyList<AgentDefinition> Agents { get; }

    /// <summary><see cref="Agents"/> with each one's template id and whether its thinking was set explicitly.</summary>
    public IReadOnlyList<ResolvedAgent> ResolvedAgents { get; }

    /// <summary>The agent spawned under <paramref name="name"/> (case-insensitive, as the host's catalog matches), or null.</summary>
    public ResolvedAgent? FindAgent(string name) => agentsByName.GetValueOrDefault(name);

    public ResolvedCommand? FindCommand(string name) => commandsByName.GetValueOrDefault(name);

    /// <summary>The effective text of a slot; <see cref="KeyNotFoundException"/> when no such slot is registered.</summary>
    public string Slot(string id) =>
        slots.TryGetValue(id, out var slot) ? slot.Text : throw new KeyNotFoundException($"No prompt slot '{id}' is registered.");

    /// <summary>The effective text of a slot, or null when the registry has no such slot.</summary>
    public string? FindSlot(string id) => slots.GetValueOrDefault(id)?.Text;

    /// <summary>True when the slot's text comes from a user file rather than the compiled-in default.</summary>
    public bool IsSlotCustomized(string id) => slots.TryGetValue(id, out var slot) && slot.Scope != PromptTemplateScope.BuiltIn;

    /// <summary>
    /// The slot with its <c>{name}</c> placeholders replaced in one left-to-right pass: a value that contains
    /// <c>{other}</c> is not expanded again, and braces that are not a provided name stay as typed.
    /// </summary>
    public string FormatSlot(string id, IReadOnlyDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var text = Slot(id);
        var builder = new StringBuilder(text.Length + 256);
        var i = 0;
        while (i < text.Length)
        {
            if (text[i] == '{')
            {
                var close = text.IndexOf('}', i + 1);
                if (close > i + 1 && values.TryGetValue(text[(i + 1)..close], out var value))
                {
                    builder.Append(value);
                    i = close + 1;
                    continue;
                }
            }

            builder.Append(text[i]);
            i++;
        }

        return builder.ToString();
    }

    /// <summary>
    /// When <paramref name="message"/> is <c>/name args</c> and <c>name</c> is a known command, the expanded text;
    /// otherwise false and the message is sent as typed.
    /// </summary>
    public bool TryExpandCommand(string? message, out string expanded)
    {
        expanded = string.Empty;
        if (!CommandExpander.TryParse(message, out var name, out var arguments) || FindCommand(name) is not { } command)
        {
            return false;
        }

        expanded = CommandExpander.Expand(command.Body, arguments);
        return true;
    }
}
