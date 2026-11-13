using System.Text.Json;
using System.Text.Json.Nodes;
using Forge.App.Composition;
using Forge.IO;

namespace Forge.App.Providers;

/// <summary>
/// Whether Forge records what every model request cost: provider, model, the shape it claimed, the tokens by category, the account's
/// meters and the latency. Off by default — it is a diagnostic record, so it runs only when the user turns it on.
/// </summary>
internal sealed record UsageLogSettings(bool Enabled = false)
{
    public static UsageLogSettings Default { get; } = new();
}

/// <summary>
/// The <c>usageLog</c> section of <c>settings.json</c> (<c>{"enabled":false}</c>), following the same file rules as the other sections:
/// only this node is replaced on save, writes take the shared mutex and go through <see cref="AtomicFile"/>, a damaged file reads as
/// the defaults instead of throwing, and an existing section keeps every field this build does not model.
/// </summary>
internal sealed class UsageLogSettingsStore
{
    private const string SectionName = "usageLog";

    private readonly string settingsPath;
    private UsageLogSettings current = UsageLogSettings.Default;

    public UsageLogSettingsStore(string settingsPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settingsPath);
        this.settingsPath = settingsPath;
        Reload();
    }

    public UsageLogSettings Current => Volatile.Read(ref current);

    /// <summary>Raised after a successful <see cref="Save"/>.</summary>
    public event EventHandler? Changed;

    public void Reload()
    {
        var loaded = UsageLogSettings.Default;
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
                // A broken file reads as the default (off); a failed write is reported when the user saves.
            }
        }

        Volatile.Write(ref current, loaded);
    }

    public void Save(UsageLogSettings value)
    {
        ArgumentNullException.ThrowIfNull(value);
        using (SettingsFileMutex.Acquire(settingsPath))
        {
            var root = ProviderStore.ReadRootForEdit(settingsPath);
            var section = root[SectionName] as JsonObject ?? new JsonObject();
            section["enabled"] = value.Enabled;
            root[SectionName] = section;
            var json = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            AtomicFile.WriteAllBytes(settingsPath, System.Text.Encoding.UTF8.GetBytes(json));
        }

        Volatile.Write(ref current, value);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    internal static UsageLogSettings Parse(JsonObject section) =>
        new(section["enabled"] is JsonValue value && value.TryGetValue<bool>(out var enabled) && enabled);
}
