using System.Text.Json;
using System.Text.Json.Nodes;
using Forge.App.Composition;
using Forge.IO;

namespace Forge.App.Providers;

/// <summary>The <c>settings.json</c> sections that switch a diagnostic record on; one per recorder.</summary>
internal static class LogSections
{
    /// <summary>Counts and timings of every model request, never its text.</summary>
    public const string Usage = "usageLog";
}

/// <summary>
/// Whether Forge keeps a diagnostic record of model traffic. It starts off: it is for diagnosis, so it runs only when the user
/// turns it on.
/// </summary>
internal sealed record LogSwitchSettings(bool Enabled = false)
{
    public static LogSwitchSettings Default { get; } = new();
}

/// <summary>
/// One section of <c>settings.json</c> (<c>{"enabled":false}</c>), following the same file rules as the other sections: only this
/// node is replaced on save, writes take the shared mutex and go through <see cref="AtomicFile"/>, a damaged file reads as the
/// defaults instead of throwing, and an existing section keeps every field this build does not model.
/// </summary>
internal sealed class LogSwitchSettingsStore
{
    private readonly string settingsPath;
    private readonly string sectionName;
    private LogSwitchSettings current = LogSwitchSettings.Default;

    public LogSwitchSettingsStore(string settingsPath, string sectionName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settingsPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(sectionName);
        this.settingsPath = settingsPath;
        this.sectionName = sectionName;
        Reload();
    }

    public LogSwitchSettings Current => Volatile.Read(ref current);

    /// <summary>Raised after a successful <see cref="Save"/>.</summary>
    public event EventHandler? Changed;

    public void Reload()
    {
        var loaded = LogSwitchSettings.Default;
        if (File.Exists(settingsPath))
        {
            try
            {
                if (JsonNode.Parse(File.ReadAllText(settingsPath)) is JsonObject root && root[sectionName] is JsonObject section)
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

    public void Save(LogSwitchSettings value)
    {
        ArgumentNullException.ThrowIfNull(value);
        using (SettingsFileMutex.Acquire(settingsPath))
        {
            var root = ProviderStore.ReadRootForEdit(settingsPath);
            var section = root[sectionName] as JsonObject ?? new JsonObject();
            section["enabled"] = value.Enabled;
            root[sectionName] = section;
            var json = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            AtomicFile.WriteAllBytes(settingsPath, System.Text.Encoding.UTF8.GetBytes(json));
        }

        Volatile.Write(ref current, value);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    internal static LogSwitchSettings Parse(JsonObject section) =>
        new(section["enabled"] is JsonValue value && value.TryGetValue<bool>(out var enabled) && enabled);
}
