using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Forge.App.Composition;
using Forge.App.Providers;
using Forge.Core.Agents;
using Forge.IO;

namespace Forge.App.Agents;

/// <summary>
/// The automatic-compaction settings: off/on, the trigger line and the target, both in percent of each model's usable
/// input budget (its effective window minus the output reserve). Valid when 50 ≤ trigger ≤ 95 and 1 ≤ target &lt; trigger.
/// </summary>
internal sealed record CompactionSettings(bool Enabled = true, int TriggerPercent = CompactionSettings.DefaultTriggerPercent, int TargetPercent = CompactionSettings.DefaultTargetPercent)
{
    public const int DefaultTriggerPercent = 85;
    public const int DefaultTargetPercent = 50;
    public const int MinTriggerPercent = 50;
    public const int MaxTriggerPercent = 95;
    public const int MinTargetPercent = 1;

    public static CompactionSettings Default { get; } = new();

    public static bool IsValidTrigger(int percent) => percent is >= MinTriggerPercent and <= MaxTriggerPercent;

    public static bool IsValidTarget(int target, int trigger) => target >= MinTargetPercent && target < trigger;

    public bool IsValid => IsValidTrigger(TriggerPercent) && IsValidTarget(TargetPercent, TriggerPercent);

    /// <summary>The Core policy for these settings; the resolvers come from the provider catalog.</summary>
    public CompactionPolicy ToPolicy(Func<Forge.Abstractions.ModelSettings, int?>? contextWindow, Func<Forge.Abstractions.ModelSettings, int?>? maxOutput)
    {
        var valid = IsValid ? this : Default with { Enabled = Enabled };
        return new CompactionPolicy
        {
            Enabled = valid.Enabled,
            TriggerRatio = valid.TriggerPercent / 100d,
            TargetRatio = valid.TargetPercent / 100d,
            ContextWindow = contextWindow,
            MaxOutputTokens = maxOutput,
        };
    }
}

/// <summary>
/// The <c>compaction</c> section of <c>settings.json</c> (<c>{"enabled":true,"triggerPercent":85,"targetPercent":50}</c>),
/// following the same file rules as <see cref="AgentDefaultsStore"/>: only this node is replaced on save, writes take
/// <see cref="SettingsFileMutex"/> and go through <see cref="AtomicFile"/>, a file that is not a JSON object is refused
/// rather than overwritten, and loading never throws: a missing, damaged or out-of-range section reads as the defaults
/// (an invalid pair of percentages falls back to both defaults; <c>enabled</c> is kept when it parses).
/// </summary>
internal sealed class CompactionSettingsStore
{
    private const string SectionName = "compaction";

    private readonly string settingsPath;
    private CompactionSettings current = CompactionSettings.Default;

    public CompactionSettingsStore(string settingsPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settingsPath);
        this.settingsPath = settingsPath;
        Reload();
    }

    public CompactionSettings Current => Volatile.Read(ref current);

    /// <summary>Raised after a successful <see cref="Save"/>.</summary>
    public event EventHandler? Changed;

    public void Reload()
    {
        var loaded = CompactionSettings.Default;
        if (File.Exists(settingsPath))
        {
            try
            {
                if (JsonNode.Parse(File.ReadAllText(settingsPath)) is JsonObject root && root[SectionName] is JsonObject section)
                {
                    loaded = Parse(section);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
            {
                // A broken file reads as the defaults; a failed write is reported when the user saves.
            }
        }

        Volatile.Write(ref current, loaded);
    }

    /// <summary>
    /// Writes the section. Throws <see cref="ArgumentException"/> for out-of-range values and
    /// <see cref="InvalidOperationException"/> when the file is not valid JSON or its root is not an object.
    /// </summary>
    public void Save(CompactionSettings value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!value.IsValid)
        {
            throw new ArgumentException("Compaction percentages are out of range.", nameof(value));
        }

        using (SettingsFileMutex.Acquire(settingsPath))
        {
            var root = ProviderStore.ReadRootForEdit(settingsPath);
            // Edit the existing object so fields this build does not model survive.
            var section = root[SectionName] as JsonObject ?? new JsonObject();
            section["enabled"] = value.Enabled;
            section["triggerPercent"] = value.TriggerPercent;
            section["targetPercent"] = value.TargetPercent;
            root[SectionName] = section;
            var json = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            AtomicFile.WriteAllBytes(settingsPath, Encoding.UTF8.GetBytes(json));
        }

        Volatile.Write(ref current, value);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    internal static CompactionSettings Parse(JsonObject section)
    {
        var enabled = ReadBool(section["enabled"]) ?? true;
        var trigger = ReadInt(section["triggerPercent"]) ?? CompactionSettings.DefaultTriggerPercent;
        var target = ReadInt(section["targetPercent"]) ?? CompactionSettings.DefaultTargetPercent;
        var parsed = new CompactionSettings(enabled, trigger, target);
        return parsed.IsValid ? parsed : CompactionSettings.Default with { Enabled = enabled };
    }

    private static bool? ReadBool(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<bool>(out var result) ? result : null;

    private static int? ReadInt(JsonNode? node)
    {
        if (node is not JsonValue value)
        {
            return null;
        }

        if (value.TryGetValue<int>(out var number))
        {
            return number;
        }

        return value.TryGetValue<double>(out var real) && real == Math.Floor(real) && real is >= int.MinValue and <= int.MaxValue
            ? (int)real
            : null;
    }
}
