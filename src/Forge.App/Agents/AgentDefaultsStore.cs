using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Forge.Abstractions;
using Forge.App.Composition;
using Forge.App.Providers;
using Forge.Core.Providers;
using Forge.Core.Prompts;
using Forge.IO;

namespace Forge.App.Agents;

/// <summary>The agents whose defaults <c>settings.json</c> can pin: the root agent and the four built-in kinds.</summary>
internal static class AgentIds
{
    public const string Main = "main";
    public const string Scout = "scout";
    public const string Worker = "worker";
    public const string Plan = "plan";
    public const string Reviewer = "reviewer";

    /// <summary>Every known id, in the order the settings page lists them.</summary>
    public static IReadOnlyList<string> All { get; } = [Main, Scout, Worker, Plan, Reviewer];

    public static bool IsKnown(string? id) =>
        id is not null && All.Contains(id, StringComparer.Ordinal);

    /// <summary>The settings id of a built-in agent kind, or <c>null</c> for a kind this build does not know.</summary>
    public static string? FromBuiltInName(string? name)
    {
        if (string.Equals(name, BuiltInAgents.Scout.Name, StringComparison.OrdinalIgnoreCase))
        {
            return Scout;
        }

        if (string.Equals(name, BuiltInAgents.Worker.Name, StringComparison.OrdinalIgnoreCase))
        {
            return Worker;
        }

        if (string.Equals(name, BuiltInAgents.Plan.Name, StringComparison.OrdinalIgnoreCase))
        {
            return Plan;
        }

        return string.Equals(name, BuiltInAgents.Reviewer.Name, StringComparison.OrdinalIgnoreCase) ? Reviewer : null;
    }
}

/// <summary>What one agent runs on: which connected provider (or the default one), which model and how hard it thinks.</summary>
internal sealed record AgentDefault(string? ProviderId, string Model, ThinkingEffort Thinking);

/// <summary>
/// The <c>agents</c> section of <c>settings.json</c>: an agent id mapped to its provider, model and thinking step. It is a
/// sibling of <c>providers</c> and <c>preferences</c> and follows the same file rules as <see cref="ProviderStore"/>:
/// <list type="bullet">
/// <item>the whole file is read as <see cref="JsonNode"/> and only the <c>agents</c> node is replaced, so every section
/// (including ones this build does not model, and agent ids it does not know) survives a save;</item>
/// <item>writes take <see cref="SettingsFileMutex"/> and go through <see cref="AtomicFile"/>;</item>
/// <item>a file that is not valid JSON, or whose root is not an object, is refused rather than overwritten;</item>
/// <item>loading never throws at startup: a missing or damaged <c>agents</c> node is an empty map.</item>
/// </list>
/// <para>Reading is in-memory; <see cref="Reload"/> re-reads after a hand edit. The resolution rule for an agent without
/// an entry is the Worker's entry, otherwise the first model of the first connected provider at Off.</para>
/// </summary>
internal sealed class AgentDefaultsStore
{
    private const string SectionName = "agents";

    private readonly string settingsPath;
    private readonly Lock gate = new();
    private readonly Dictionary<string, AgentDefault> defaults = new(StringComparer.Ordinal);

    public AgentDefaultsStore(string settingsPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settingsPath);
        this.settingsPath = settingsPath;
        Reload();
    }

    public string SettingsPath => settingsPath;

    /// <summary>Every known agent that has an entry; unknown ids in the file are kept there but not loaded.</summary>
    public IReadOnlyDictionary<string, AgentDefault> All
    {
        get
        {
            lock (gate)
            {
                return new Dictionary<string, AgentDefault>(defaults, StringComparer.Ordinal);
            }
        }
    }

    /// <summary>The stored entry of one agent, or <c>null</c> when it inherits; unknown ids always read as <c>null</c>.</summary>
    public AgentDefault? Get(string agentId)
    {
        if (!AgentIds.IsKnown(agentId))
        {
            return null;
        }

        lock (gate)
        {
            return defaults.GetValueOrDefault(agentId);
        }
    }

    /// <summary>Re-reads the file. Never throws for file problems: a damaged file leaves the map empty.</summary>
    public void Reload()
    {
        var loaded = new Dictionary<string, AgentDefault>(StringComparer.Ordinal);
        if (File.Exists(settingsPath))
        {
            try
            {
                if (JsonNode.Parse(File.ReadAllText(settingsPath)) is JsonObject root &&
                    root[SectionName] is JsonObject agents)
                {
                    foreach (var (id, node) in agents)
                    {
                        if (AgentIds.IsKnown(id) && Parse(node) is { } value)
                        {
                            loaded[id] = value;
                        }
                    }
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
            {
                // A broken file is an empty map; the settings page reports a failed write only when the user saves.
            }
        }

        lock (gate)
        {
            defaults.Clear();
            foreach (var (id, value) in loaded)
            {
                defaults[id] = value;
            }
        }
    }

    /// <summary>
    /// Replaces one agent's entry (or removes it when <paramref name="value"/> is <c>null</c>) and writes the file. Reads
    /// the current file under the settings mutex first, so a provider save that happened since <see cref="Reload"/> is
    /// never dropped. Throws <see cref="InvalidOperationException"/> when the file is not valid JSON or its root is not an
    /// object, exactly like <see cref="ProviderStore.Save"/>.
    /// </summary>
    public void Save(string agentId, AgentDefault? value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        if (!AgentIds.IsKnown(agentId))
        {
            throw new ArgumentException("Unknown agent id '" + agentId + "'.", nameof(agentId));
        }

        var normalized = value is null || string.IsNullOrWhiteSpace(value.Model)
            ? null
            : new AgentDefault(
                string.IsNullOrWhiteSpace(value.ProviderId) ? null : value.ProviderId.Trim(),
                value.Model.Trim(),
                value.Thinking);

        using var lease = SettingsFileMutex.Acquire(settingsPath);
        var root = ProviderStore.ReadRootForEdit(settingsPath);
        if (root[SectionName] is not JsonObject agents)
        {
            agents = [];
            root[SectionName] = agents;
        }

        if (normalized is null)
        {
            agents.Remove(agentId);
            if (agents.Count == 0)
            {
                root.Remove(SectionName);
            }
        }
        else
        {
            // Keep any field this build does not model (a hand note, a future setting) by editing the existing entry object
            // instead of replacing it.
            var entry = agents[agentId] as JsonObject ?? new JsonObject();
            if (normalized.ProviderId is { } providerId)
            {
                entry["providerId"] = providerId;
            }
            else
            {
                entry.Remove("providerId");
            }

            entry["model"] = normalized.Model;
            entry["thinking"] = ThinkingEfforts.Name(normalized.Thinking);
            agents[agentId] = entry;
        }

        var json = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        AtomicFile.WriteAllBytes(settingsPath, Encoding.UTF8.GetBytes(json));
        lock (gate)
        {
            if (normalized is null)
            {
                defaults.Remove(agentId);
            }
            else
            {
                defaults[agentId] = normalized;
            }
        }
    }

    /// <summary>
    /// The model settings an agent runs with: its own entry, else the Worker's, else the first model of the first connected
    /// provider at <see cref="ThinkingEffort.Off"/>. The fallback provider is the first one <see cref="ProviderCatalog.Providers"/>
    /// lists, not the default one, so it agrees with the first row the settings page shows. <c>null</c> when no provider is
    /// connected, which keeps the caller's existing "no model" behaviour. The thinking step is clamped to what the model
    /// accepts, never above it.
    /// </summary>
    /// <param name="levels">The accepted steps per model; defaults to the Core table (a test can pass a restricted one).</param>
    public ModelSettings? Resolve(
        string agentId,
        ProviderCatalog catalog,
        Func<string?, IReadOnlyList<ThinkingEffort>>? levels = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        if (!AgentIds.IsKnown(agentId))
        {
            return null;
        }

        var entry = Get(agentId);
        if (entry is null && !string.Equals(agentId, AgentIds.Worker, StringComparison.Ordinal))
        {
            entry = Get(AgentIds.Worker);
        }

        // No entry anywhere: the model comes from the first connected provider, which only differs from the default one when
        // the user picked another provider as default.
        var target = entry is null
            ? catalog.Providers.FirstOrDefault()
            : catalog.Find(entry.ProviderId) ?? catalog.Default;
        if (target is null)
        {
            return null;
        }

        var model = entry?.Model ?? (target.Models.Count > 0 ? target.Models[0] : target.DefaultModel);
        if (string.IsNullOrWhiteSpace(model))
        {
            return null;
        }

        var accepted = (levels ?? ThinkingLevels.For)(model);
        var thinking = ThinkingEfforts.Clamp(entry?.Thinking ?? ThinkingEffort.Off, accepted);
        return new ModelSettings(model, thinking, target.Definition.MaxOutputTokens, target.Id);
    }

    private static AgentDefault? Parse(JsonNode? node)
    {
        if (node is not JsonObject entry)
        {
            return null;
        }

        var model = Text(entry, "model");
        if (string.IsNullOrWhiteSpace(model))
        {
            return null;
        }

        var providerId = Text(entry, "providerId");
        var thinking = ThinkingEfforts.TryParse(Text(entry, "thinking"), out var parsed) ? parsed : ThinkingEffort.Off;
        return new AgentDefault(
            string.IsNullOrWhiteSpace(providerId) ? null : providerId.Trim(),
            model.Trim(),
            thinking);
    }

    private static string? Text(JsonObject entry, string name) =>
        entry[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}
