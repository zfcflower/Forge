using System.Text.Json;
using System.Text.Json.Serialization;
using Forge.App.Composition;
using Forge.App.Logging;
using ZLogger;

namespace Forge.App.Services;

/// <summary>The <c>composer.json</c> document: the models the user pinned in the picker.</summary>
internal sealed class ComposerPreferencesDocument
{
    [JsonPropertyName("pinnedModels")]
    public List<string>? PinnedModels { get; set; }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(ComposerPreferencesDocument))]
internal sealed partial class ComposerPreferencesJsonContext : JsonSerializerContext
{
}

/// <summary>
/// What the chat composer remembers across sessions: the models the user pinned. Where a new session starts is not stored
/// here — <c>settings.json</c>'s per-agent rows (the provider page's "built-in agent defaults") are the one place a default
/// model and effort live, and a new session opens on the main agent. Kept in <c>composer.json</c> next to
/// <c>settings.json</c>; a store without a path lives in memory only. Reads happen once, writes go to the pool, and a
/// broken file is treated as empty.
/// </summary>
internal sealed class ComposerPreferences
{
    private static readonly Lazy<ComposerPreferences> SharedInstance = new(CreateShared);

    private readonly Lock gate = new();
    private readonly string? path;
    private readonly List<string> pinned = [];

    public ComposerPreferences(string? path = null)
    {
        this.path = path;
        Load();
    }

    /// <summary>The store of the running app, under the data root (<c>FORGE_DATA_DIR</c> moves it with the database).</summary>
    public static ComposerPreferences Shared => SharedInstance.Value;

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

            // Fields an older build wrote (defaultAgent/defaultModel/defaultEffort) are not read: settings.json owns the
            // defaults now, and the next save drops them from the file.
            foreach (var model in document.PinnedModels ?? [])
            {
                if (!string.IsNullOrWhiteSpace(model) && !pinned.Contains(model, StringComparer.Ordinal))
                {
                    pinned.Add(model);
                }
            }
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
            document = new ComposerPreferencesDocument { PinnedModels = [.. pinned] };
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
