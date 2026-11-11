using System.Text.Json;
using System.Text.Json.Serialization;
using Forge.Abstractions;
using Forge.IO;

namespace Forge.App.Automations;

/// <summary>
/// <see cref="IAutomationDefinitionStore"/> over one user-level JSON file (<c>automations.json</c>). Serialization is
/// source-generated (no reflection). Every definition element is parsed on its own, so one broken element becomes one
/// <see cref="AutomationDefinitionError"/> while the rest load; a file that is not JSON at all loads as an empty config
/// plus one error (index -1) and is never overwritten by a load.
/// </summary>
internal sealed class AutomationDefinitionStore : IAutomationDefinitionStore
{
    /// <summary>Index used by an error that is about the file or a top-level field rather than one definition.</summary>
    public const int FileErrorIndex = -1;

    private readonly string _path;

    public AutomationDefinitionStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
    }

    public string FilePath => _path;

    public Task<AutomationConfigLoad> LoadAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(Parse(AtomicFile.ReadAllBytesShared(_path, cancellationToken: ct)));
    }

    public Task SaveAsync(AutomationConfig config, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(config);
        ct.ThrowIfCancellationRequested();
        Validate(config);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(ToDocument(config), AutomationJsonContext.Default.AutomationConfigDocument);
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        AtomicFile.WriteAllBytes(_path, bytes, cancellationToken: ct);
        var readBack = AtomicFile.ReadAllBytesShared(_path, cancellationToken: ct);
        if (readBack is null || !readBack.AsSpan().SequenceEqual(bytes))
        {
            throw new IOException($"The automation file '{_path}' does not hold what was just written.");
        }

        return Task.CompletedTask;
    }

    internal static AutomationConfigLoad Parse(byte[]? bytes)
    {
        if (bytes is null || bytes.Length == 0)
        {
            return new AutomationConfigLoad(new AutomationConfig(), []);
        }

        var errors = new List<AutomationDefinitionError>();
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(bytes, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        }
        catch (JsonException exception)
        {
            return new AutomationConfigLoad(new AutomationConfig(), [new AutomationDefinitionError(FileErrorIndex, null, "The automation file is not valid JSON: " + exception.Message)]);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return new AutomationConfigLoad(new AutomationConfig(), [new AutomationDefinitionError(FileErrorIndex, null, "The automation file must hold a JSON object.")]);
            }

            var paused = false;
            if (TryGet(root, "paused", out var pausedElement))
            {
                if (pausedElement.ValueKind is JsonValueKind.True or JsonValueKind.False)
                {
                    paused = pausedElement.GetBoolean();
                }
                else
                {
                    errors.Add(new AutomationDefinitionError(FileErrorIndex, null, "'paused' must be true or false."));
                }
            }

            var disabled = ReadStrings(root, "disabledBuiltIns", errors);
            var ciOff = ReadStrings(root, "ciAutoPushOffProjects", errors);
            var definitions = new List<AutomationDefinition>();
            if (TryGet(root, "definitions", out var list))
            {
                if (list.ValueKind != JsonValueKind.Array)
                {
                    errors.Add(new AutomationDefinitionError(FileErrorIndex, null, "'definitions' must be an array."));
                }
                else
                {
                    ReadDefinitions(list, definitions, errors);
                }
            }

            var config = new AutomationConfig
            {
                Paused = paused,
                Definitions = definitions,
                DisabledBuiltIns = disabled,
                CiAutoPushOffProjects = ciOff,
            };
            return new AutomationConfigLoad(config, errors);
        }
    }

    private static void ReadDefinitions(JsonElement list, List<AutomationDefinition> definitions, List<AutomationDefinitionError> errors)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var index = 0;
        foreach (var element in list.EnumerateArray())
        {
            string? id = null;
            try
            {
                if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty("id", out var idElement) && idElement.ValueKind == JsonValueKind.String)
                {
                    id = idElement.GetString();
                }

                var dto = element.Deserialize(AutomationJsonContext.Default.AutomationDefinitionDocument)
                    ?? throw new FormatException("The definition is null.");
                var definition = ToDefinition(dto);
                if (BuiltInAutomations.IsBuiltIn(definition.Id))
                {
                    throw new FormatException($"The id '{definition.Id}' is reserved for built-in automations.");
                }

                if (!seen.Add(definition.Id))
                {
                    throw new FormatException($"The id '{definition.Id}' is used by an earlier definition.");
                }

                definitions.Add(definition);
            }
            catch (Exception exception) when (exception is JsonException or FormatException or InvalidOperationException)
            {
                errors.Add(new AutomationDefinitionError(index, id, exception.Message));
            }

            index++;
        }
    }

    private static bool TryGet(JsonElement root, string name, out JsonElement value) =>
        root.TryGetProperty(name, out value) && value.ValueKind != JsonValueKind.Null;

    private static List<string> ReadStrings(JsonElement root, string name, List<AutomationDefinitionError> errors)
    {
        var result = new List<string>();
        if (!TryGet(root, name, out var element))
        {
            return result;
        }

        if (element.ValueKind != JsonValueKind.Array)
        {
            errors.Add(new AutomationDefinitionError(FileErrorIndex, null, $"'{name}' must be an array of strings."));
            return result;
        }

        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } text)
            {
                result.Add(text);
            }
            else
            {
                errors.Add(new AutomationDefinitionError(FileErrorIndex, null, $"'{name}' must hold only non-empty strings."));
            }
        }

        return result;
    }

    private static void Validate(AutomationConfig config)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var definition in config.Definitions)
        {
            if (string.IsNullOrWhiteSpace(definition.Id) || definition.IsBuiltIn)
            {
                throw new ArgumentException($"'{definition.Id}' is not a valid id for a user-defined automation.", nameof(config));
            }

            if (!seen.Add(definition.Id))
            {
                throw new ArgumentException($"Duplicate automation id '{definition.Id}'.", nameof(config));
            }
        }
    }

    private static AutomationDefinition ToDefinition(AutomationDefinitionDocument dto)
    {
        if (string.IsNullOrWhiteSpace(dto.Id))
        {
            throw new FormatException("The definition has no id.");
        }

        if (string.IsNullOrWhiteSpace(dto.Name))
        {
            throw new FormatException("The definition has no name.");
        }

        var triggers = new List<AutomationTrigger>();
        foreach (var trigger in dto.Triggers ?? [])
        {
            triggers.Add(ToTrigger(trigger ?? throw new FormatException("A trigger is null.")));
        }

        ModelSettings? model = null;
        if (dto.Model is { } m)
        {
            if (string.IsNullOrWhiteSpace(m.Model))
            {
                throw new FormatException("The model has no name.");
            }

            model = new ModelSettings(m.Model, m.Thinking, m.MaxOutputTokens, m.ProviderId);
        }

        return new AutomationDefinition(dto.Id, dto.Name)
        {
            Enabled = dto.Enabled,
            Triggers = triggers,
            Action = dto.Action,
            Prompt = dto.Prompt ?? string.Empty,
            Placement = dto.Placement,
            ProjectId = dto.ProjectId,
            TargetSessionId = dto.TargetSessionId,
            ReadOnlyTools = dto.ReadOnlyTools,
            Model = model,
            CatchUp = dto.CatchUp,
        };
    }

    private static AutomationTrigger ToTrigger(AutomationTriggerDocument dto)
    {
        var times = new List<AutomationTime>();
        foreach (var time in dto.Times ?? [])
        {
            if (time is null || time.Hour is < 0 or > 23 || time.Minute is < 0 or > 59)
            {
                throw new FormatException("A time of day must be hour 0-23 and minute 0-59.");
            }

            times.Add(new AutomationTime(time.Hour, time.Minute));
        }

        foreach (var day in dto.Days ?? [])
        {
            if (!Enum.IsDefined(day))
            {
                throw new FormatException("A weekday is out of range.");
            }
        }

        switch (dto.Kind)
        {
            case AutomationTriggerKind.Hourly when dto.IntervalHours is < 1 or > 12 || dto.Minute is < 0 or > 59:
                throw new FormatException("An hourly trigger needs an interval of 1-12 hours and a minute of 0-59.");
            case AutomationTriggerKind.Daily when times.Count == 0:
                throw new FormatException("A daily trigger needs at least one time.");
            case AutomationTriggerKind.Weekly when times.Count == 0 || (dto.Days ?? []).Count == 0:
                throw new FormatException("A weekly trigger needs at least one day and one time.");
            case AutomationTriggerKind.Cron when string.IsNullOrWhiteSpace(dto.Cron):
                throw new FormatException("A cron trigger needs an expression.");
        }

        return new AutomationTrigger(dto.Kind)
        {
            IntervalHours = dto.IntervalHours,
            Minute = dto.Minute,
            Times = times,
            Days = dto.Days ?? [],
            Cron = dto.Cron,
        };
    }

    private static AutomationConfigDocument ToDocument(AutomationConfig config) => new()
    {
        Paused = config.Paused,
        DisabledBuiltIns = [.. config.DisabledBuiltIns],
        CiAutoPushOffProjects = [.. config.CiAutoPushOffProjects],
        Definitions = [.. config.Definitions.Select(ToDocument)],
    };

    private static AutomationDefinitionDocument ToDocument(AutomationDefinition definition) => new()
    {
        Id = definition.Id,
        Name = definition.Name,
        Enabled = definition.Enabled,
        Triggers = [.. definition.Triggers.Select(ToDocument)],
        Action = definition.Action,
        Prompt = definition.Prompt,
        Placement = definition.Placement,
        ProjectId = definition.ProjectId,
        TargetSessionId = definition.TargetSessionId,
        ReadOnlyTools = definition.ReadOnlyTools,
        Model = definition.Model is { } model
            ? new ModelDocument { Model = model.Model, Thinking = model.Thinking, MaxOutputTokens = model.MaxOutputTokens, ProviderId = model.ProviderId }
            : null,
        CatchUp = definition.CatchUp,
    };

    private static AutomationTriggerDocument ToDocument(AutomationTrigger trigger) => new()
    {
        Kind = trigger.Kind,
        IntervalHours = trigger.IntervalHours,
        Minute = trigger.Minute,
        Times = [.. trigger.Times.Select(static t => new TimeDocument { Hour = t.Hour, Minute = t.Minute })],
        Days = [.. trigger.Days],
        Cron = trigger.Cron,
    };
}

internal sealed class AutomationConfigDocument
{
    public bool Paused { get; set; }

    public List<AutomationDefinitionDocument> Definitions { get; set; } = [];

    public List<string> DisabledBuiltIns { get; set; } = [];

    public List<string> CiAutoPushOffProjects { get; set; } = [];
}

internal sealed class AutomationDefinitionDocument
{
    public string? Id { get; set; }

    public string? Name { get; set; }

    public bool Enabled { get; set; } = true;

    public List<AutomationTriggerDocument?>? Triggers { get; set; }

    public AutomationAction Action { get; set; } = AutomationAction.Prompt;

    public string? Prompt { get; set; }

    public AutomationPlacement Placement { get; set; } = AutomationPlacement.Scratch;

    public string? ProjectId { get; set; }

    public string? TargetSessionId { get; set; }

    public bool ReadOnlyTools { get; set; }

    public ModelDocument? Model { get; set; }

    public bool CatchUp { get; set; }
}

internal sealed class AutomationTriggerDocument
{
    public AutomationTriggerKind Kind { get; set; }

    public int IntervalHours { get; set; } = 1;

    public int Minute { get; set; }

    public List<TimeDocument?>? Times { get; set; }

    public List<DayOfWeek>? Days { get; set; }

    public string? Cron { get; set; }
}

internal sealed class TimeDocument
{
    public int Hour { get; set; }

    public int Minute { get; set; }
}

internal sealed class ModelDocument
{
    public string? Model { get; set; }

    public ThinkingEffort Thinking { get; set; }

    public int? MaxOutputTokens { get; set; }

    public string? ProviderId { get; set; }
}

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    AllowTrailingCommas = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(AutomationConfigDocument))]
[JsonSerializable(typeof(AutomationDefinitionDocument))]
internal sealed partial class AutomationJsonContext : JsonSerializerContext
{
}
