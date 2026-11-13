using System.Text.Json;
using System.Text.Json.Serialization;
using Forge.Abstractions;
using Forge.App.Composition;
using Forge.App.Providers;
using Forge.App.Logging;
using ZLogger;

namespace Forge.App.Services;

/// <summary>The <c>composer.json</c> document: pinned models and the composer defaults for new sessions.</summary>
internal sealed class ComposerPreferencesDocument
{
    [JsonPropertyName("pinnedModels")]
    public List<string>? PinnedModels { get; set; }

    [JsonPropertyName("defaultAgent")]
    public string? DefaultAgent { get; set; }

    [JsonPropertyName("defaultModel")]
    public string? DefaultModel { get; set; }

    [JsonPropertyName("defaultEffort")]
    public string? DefaultEffort { get; set; }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(ComposerPreferencesDocument))]
internal sealed partial class ComposerPreferencesJsonContext : JsonSerializerContext
{
}

/// <summary>
/// What the chat composer remembers across sessions: the pinned models and the agent, model and effort a new session
/// starts with. Kept in <c>composer.json</c> next to <c>settings.json</c>; a store without a path lives in memory only
/// (tests). Reads happen once, writes go to the pool, and a broken file is treated as empty.
/// </summary>
internal sealed class ComposerPreferences
{
    private static readonly Lazy<ComposerPreferences> SharedInstance = new(CreateShared);

    private readonly Lock gate = new();
    private readonly string? path;
    private readonly List<string> pinned = [];
    private string? defaultAgent;
    private string? defaultModel;
    private ThinkingEffort? defaultEffort;

    public ComposerPreferences(string? path = null)
    {
        this.path = path;
        Load();
    }

    /// <summary>The store of the running app, under the data root (<c>FORGE_DATA_DIR</c> moves it with the database).</summary>
    public static ComposerPreferences Shared => SharedInstance.Value;

    public string? DefaultAgent
    {
        get
        {
            lock (gate)
            {
                return defaultAgent;
            }
        }
    }

    public string? DefaultModel
    {
        get
        {
            lock (gate)
            {
                return defaultModel;
            }
        }
    }

    public ThinkingEffort? DefaultEffort
    {
        get
        {
            lock (gate)
            {
                return defaultEffort;
            }
        }
    }

    public bool IsPinned(string model)
    {
        lock (gate)
        {
            return pinned.Contains(model, StringComparer.Ordinal);
        }
    }

    public IReadOnlyList<string> PinnedModels
    {
        get
        {
            lock (gate)
            {
                return [.. pinned];
            }
        }
    }

    public void SetPinned(string model, bool value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        lock (gate)
        {
            var index = pinned.FindIndex(entry => string.Equals(entry, model, StringComparison.Ordinal));
            if (value == index >= 0)
            {
                return;
            }

            if (value)
            {
                pinned.Add(model);
            }
            else
            {
                pinned.RemoveAt(index);
            }
        }

        Save();
    }

    public void SetDefaults(string? agent, string? model, ThinkingEffort effort)
    {
        lock (gate)
        {
            defaultAgent = string.IsNullOrWhiteSpace(agent) ? null : agent;
            defaultModel = string.IsNullOrWhiteSpace(model) ? null : model;
            defaultEffort = effort;
        }

        Save();
    }

    internal static string EffortName(ThinkingEffort effort) => ThinkingEfforts.Name(effort);

    internal static ThinkingEffort? ParseEffort(string? text) =>
        ThinkingEfforts.TryParse(text, out var effort) ? effort : null;

    private static ComposerPreferences CreateShared()
    {
        try
        {
            return new ComposerPreferences(Path.Combine(AppPaths.Create().UserDataDirectory, "composer.json"));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new ComposerPreferences();
        }
    }

    private void Load()
    {
        if (path is null || !File.Exists(path))
        {
            return;
        }

        try
        {
            using var stream = File.OpenRead(path);
            if (JsonSerializer.Deserialize(stream, ComposerPreferencesJsonContext.Default.ComposerPreferencesDocument) is not { } document)
            {
                return;
            }

            foreach (var model in document.PinnedModels ?? [])
            {
                if (!string.IsNullOrWhiteSpace(model) && !pinned.Contains(model, StringComparer.Ordinal))
                {
                    pinned.Add(model);
                }
            }

            defaultAgent = string.IsNullOrWhiteSpace(document.DefaultAgent) ? null : document.DefaultAgent;
            defaultModel = string.IsNullOrWhiteSpace(document.DefaultModel) ? null : document.DefaultModel;
            defaultEffort = ParseEffort(document.DefaultEffort);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
        }
    }

    private void Save()
    {
        if (path is null)
        {
            return;
        }

        ComposerPreferencesDocument document;
        lock (gate)
        {
            document = new ComposerPreferencesDocument
            {
                PinnedModels = [.. pinned],
                DefaultAgent = defaultAgent,
                DefaultModel = defaultModel,
                DefaultEffort = defaultEffort is { } effort ? EffortName(effort) : null,
            };
        }

        var target = path;
        _ = Task.Run(() =>
        {
            try
            {
                lock (SharedInstance)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    var temporary = target + ".tmp";
                    using (var stream = File.Create(temporary))
                    {
                        JsonSerializer.Serialize(stream, document, ComposerPreferencesJsonContext.Default.ComposerPreferencesDocument);
                    }

                    File.Move(temporary, target, overwrite: true);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                AppLogs.Composer.ZLogError(exception, $"preferences could not be saved");
            }
        });
    }
}
