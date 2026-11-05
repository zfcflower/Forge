using System.Text.Json;

namespace Forge.Workflows;

/// <summary>The recorded result of one executed step, the source of binding values for later steps.</summary>
internal sealed record StepOutcome(StepStatus Status, string? Content, bool IsError, string? Error);

/// <summary>A resolved binding value: raw JSON from a step's output, or a scalar computed from the envelope.</summary>
internal readonly struct ResolvedValue
{
    private ResolvedValue(ResolvedKind kind, JsonElement element, string? text, bool boolean)
    {
        Kind = kind;
        Element = element;
        Text = text;
        Boolean = boolean;
    }

    public ResolvedKind Kind { get; }

    public JsonElement Element { get; }

    public string? Text { get; }

    public bool Boolean { get; }

    public static ResolvedValue None => default;

    public static ResolvedValue FromJson(JsonElement element) => new(ResolvedKind.Json, element, null, false);

    public static ResolvedValue FromString(string text) => new(ResolvedKind.String, default, text, false);

    public static ResolvedValue FromBoolean(bool value) => new(ResolvedKind.Boolean, default, null, value);
}

internal enum ResolvedKind : byte
{
    None,
    Json,
    String,
    Boolean,
}

/// <summary>
/// Shared state of one run: the outcomes published by finished steps and the lazily parsed JSON documents of
/// their contents. Steps only bind values of steps they depend on, so publishing happens-before reading;
/// slots are still written and read through <see cref="Volatile"/> because independent branches may parse the
/// same upstream document concurrently.
/// </summary>
internal sealed class RunState : IDisposable
{
    private readonly string[] _stepIds;
    private readonly StepOutcome?[] _outcomes;
    private readonly JsonSlot?[] _json;
    private readonly JsonElement? _input;

    public RunState(WorkflowPlan plan, JsonElement? input = null)
    {
        _stepIds = new string[plan.Steps.Length];
        for (var i = 0; i < plan.Steps.Length; i++)
        {
            _stepIds[i] = plan.Steps[i].Id;
        }

        _outcomes = new StepOutcome?[_stepIds.Length];
        _json = new JsonSlot?[_stepIds.Length];
        _input = input;
    }

    public void Publish(int index, StepOutcome outcome) => Volatile.Write(ref _outcomes[index], outcome);

    public StepOutcome? Read(int index) => Volatile.Read(ref _outcomes[index]);

    /// <summary>
    /// Resolves an <c>$input</c> path against the arguments of the call that started this run. A run without input (or an
    /// input that is <see cref="JsonValueKind.Undefined"/>) fails every such binding: the engine never invents a value,
    /// so a step that needs the input fails instead of calling its tool with something the caller did not supply.
    /// </summary>
    public bool TryResolveInput(BindingPath path, out ResolvedValue value, out string? error)
    {
        if (_input is not { } input || input.ValueKind == JsonValueKind.Undefined)
        {
            value = ResolvedValue.None;
            error = "the run has no $input; RunOptions.Input was not supplied";
            return false;
        }

        return TryNavigate(input, path, out value, out error);
    }

    public bool TryResolve(BindingPath path, int producerIndex, out ResolvedValue value, out string? error)
    {
        value = ResolvedValue.None;
        if (Read(producerIndex) is not { } outcome)
        {
            error = $"step '{_stepIds[producerIndex]}' has not run";
            return false;
        }

        switch (path.Node)
        {
            case BindingNode.Content:
                return Scalar(path, outcome.Content ?? string.Empty, out value, out error);
            case BindingNode.IsError:
                if (path.Tail.Length != 0)
                {
                    error = $"'{path.RenderPrefix(0)}' is a boolean and cannot be navigated";
                    return false;
                }

                value = ResolvedValue.FromBoolean(outcome.IsError);
                error = null;
                return true;
            case BindingNode.Status:
                return Scalar(path, StatusText(outcome.Status), out value, out error);
            case BindingNode.Error:
                if (outcome.Error is null)
                {
                    error = $"step '{_stepIds[producerIndex]}' did not record an error";
                    return false;
                }

                return Scalar(path, outcome.Error, out value, out error);
            default:
                if (!TryGetJson(producerIndex, outcome, out var document, out error))
                {
                    return false;
                }

                return TryNavigate(document!.RootElement, path, out value, out error);
        }
    }

    public void Dispose()
    {
        foreach (var slot in _json)
        {
            slot?.Document?.Dispose();
        }
    }

    internal static string StatusText(StepStatus status) => status switch
    {
        StepStatus.Succeeded => "succeeded",
        StepStatus.Failed => "failed",
        StepStatus.Cancelled => "cancelled",
        StepStatus.Skipped => "skipped",
        _ => "notRun",
    };

    private static bool Scalar(BindingPath path, string text, out ResolvedValue value, out string? error)
    {
        if (path.Tail.Length != 0)
        {
            value = ResolvedValue.None;
            error = $"'{path.RenderPrefix(0)}' is a string and cannot be navigated";
            return false;
        }

        value = ResolvedValue.FromString(text);
        error = null;
        return true;
    }

    private static bool TryNavigate(JsonElement root, BindingPath path, out ResolvedValue value, out string? error)
    {
        var current = root;
        var tail = path.Tail;
        for (var i = 0; i < tail.Length; i++)
        {
            var segment = tail[i];
            if (segment.IsIndex)
            {
                if (current.ValueKind != JsonValueKind.Array)
                {
                    value = ResolvedValue.None;
                    error = $"cannot index into a {KindText(current.ValueKind)} at '{path.RenderPrefix(i + 1)}'";
                    return false;
                }

                var length = current.GetArrayLength();
                if ((uint)segment.Index >= (uint)length)
                {
                    value = ResolvedValue.None;
                    error = $"index {segment.Index} is out of range at '{path.RenderPrefix(i + 1)}' (length {length})";
                    return false;
                }

                current = current[segment.Index];
                continue;
            }

            if (current.ValueKind != JsonValueKind.Object)
            {
                value = ResolvedValue.None;
                error = $"cannot read property '{segment.Name}' of a {KindText(current.ValueKind)} at '{path.RenderPrefix(i + 1)}'";
                return false;
            }

            if (!current.TryGetProperty(segment.Name!, out var property))
            {
                value = ResolvedValue.None;
                error = $"property '{segment.Name}' not found at '{path.RenderPrefix(i + 1)}'";
                return false;
            }

            current = property;
        }

        value = ResolvedValue.FromJson(current);
        error = null;
        return true;
    }

    private static string KindText(JsonValueKind kind) => kind switch
    {
        JsonValueKind.Object => "object",
        JsonValueKind.Array => "array",
        JsonValueKind.String => "string",
        JsonValueKind.Number => "number",
        JsonValueKind.True or JsonValueKind.False => "boolean",
        JsonValueKind.Null => "null",
        _ => "value",
    };

    private bool TryGetJson(int index, StepOutcome outcome, out JsonDocument? document, out string? error)
    {
        var slot = Volatile.Read(ref _json[index]);
        if (slot is null)
        {
            var built = JsonSlot.Parse(_stepIds[index], outcome.Content);
            slot = Interlocked.CompareExchange(ref _json[index], built, null) ?? built;
        }

        document = slot.Document;
        error = slot.Error;
        return document is not null;
    }

    private sealed record JsonSlot(JsonDocument? Document, string? Error)
    {
        public static JsonSlot Parse(string stepId, string? content)
        {
            if (string.IsNullOrWhiteSpace(content))
            {
                return new JsonSlot(null, $"step '{stepId}' produced no content, so it has no JSON");
            }

            try
            {
                return new JsonSlot(JsonDocument.Parse(content), null);
            }
            catch (JsonException ex)
            {
                return new JsonSlot(null, $"content of step '{stepId}' is not JSON ({ex.Message})");
            }
        }
    }
}
