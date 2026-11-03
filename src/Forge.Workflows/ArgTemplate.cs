using System.Text.Json;

namespace Forge.Workflows;

internal enum WriteOutcome : byte
{
    Written,
    Skipped,
    Failed,
}

/// <summary>Mutable state of one argument-resolution pass.</summary>
internal sealed class ResolutionContext(RunState state)
{
    public RunState State { get; } = state;

    public string? Error { get; private set; }

    public void Fail(string message) => Error ??= message;
}

/// <summary>
/// A compiled node of a step's <c>args</c>: literal JSON, or a structure with <c>$ref</c> leaves resolved
/// before the tool call. Templates are immutable and shared by every run of the same plan.
/// </summary>
internal abstract class ArgTemplate
{
    /// <summary>
    /// True when writing this template reproduces the definition's raw <c>args</c> element byte for byte, so the
    /// engine can hand the element to the host unchanged instead of re-serializing it.
    /// </summary>
    public abstract bool IsPassThrough { get; }

    /// <summary>
    /// Writes the value. <paramref name="propertyName"/> is written first when the node is an object member, so
    /// an optional binding that did not resolve can skip the member entirely without producing invalid JSON.
    /// </summary>
    public abstract WriteOutcome Write(Utf8JsonWriter writer, string? propertyName, ResolutionContext context);
}

internal sealed class LiteralArg(JsonElement value, bool passThrough = true) : ArgTemplate
{
    public JsonElement Value { get; } = value;

    public override bool IsPassThrough => passThrough;

    public override WriteOutcome Write(Utf8JsonWriter writer, string? propertyName, ResolutionContext context)
    {
        if (propertyName is not null)
        {
            writer.WritePropertyName(propertyName);
        }

        Value.WriteTo(writer);
        return WriteOutcome.Written;
    }
}

internal sealed class ObjectArg(KeyValuePair<string, ArgTemplate>[] entries) : ArgTemplate
{
    public static readonly ObjectArg Empty = new([]);

    private readonly bool _isPassThrough = entries.All(static entry => entry.Value.IsPassThrough);

    public override bool IsPassThrough => _isPassThrough;

    public override WriteOutcome Write(Utf8JsonWriter writer, string? propertyName, ResolutionContext context)
    {
        if (propertyName is not null)
        {
            writer.WritePropertyName(propertyName);
        }

        writer.WriteStartObject();
        foreach (var entry in entries)
        {
            if (entry.Value.Write(writer, entry.Key, context) == WriteOutcome.Failed)
            {
                return WriteOutcome.Failed;
            }
        }

        writer.WriteEndObject();
        return WriteOutcome.Written;
    }
}

internal sealed class ArrayArg(ArgTemplate[] items) : ArgTemplate
{
    private readonly bool _isPassThrough = items.All(static item => item.IsPassThrough);

    public override bool IsPassThrough => _isPassThrough;

    public override WriteOutcome Write(Utf8JsonWriter writer, string? propertyName, ResolutionContext context)
    {
        if (propertyName is not null)
        {
            writer.WritePropertyName(propertyName);
        }

        writer.WriteStartArray();
        foreach (var item in items)
        {
            var outcome = item.Write(writer, null, context);
            if (outcome == WriteOutcome.Failed)
            {
                return WriteOutcome.Failed;
            }

            if (outcome == WriteOutcome.Skipped)
            {
                writer.WriteNullValue();
            }
        }

        writer.WriteEndArray();
        return WriteOutcome.Written;
    }
}

/// <summary>A <c>{"$ref": "…"}</c> leaf. A missing value of an optional path is skipped, never null-injected.</summary>
internal sealed class RefArg : ArgTemplate
{
    public RefArg(BindingPath path, int producerIndex, int consumerIndex, string consumerId)
    {
        Path = path;
        ProducerIndex = producerIndex;
        ConsumerIndex = consumerIndex;
        ConsumerId = consumerId;
    }

    public BindingPath Path { get; }

    /// <summary>Index of the producing step, or -1 when the path names a step that does not exist.</summary>
    public int ProducerIndex { get; }

    public int ConsumerIndex { get; }

    public string ConsumerId { get; }

    public override bool IsPassThrough => false;

    public override WriteOutcome Write(Utf8JsonWriter writer, string? propertyName, ResolutionContext context)
    {
        if (ProducerIndex < 0)
        {
            if (Path.Optional)
            {
                return WriteOutcome.Skipped;
            }

            context.Fail($"step '{ConsumerId}': binding '{Path.Text}' names an unknown step '{Path.StepId}'");
            return WriteOutcome.Failed;
        }

        if (!context.State.TryResolve(Path, ProducerIndex, out var value, out var error))
        {
            if (Path.Optional)
            {
                return WriteOutcome.Skipped;
            }

            context.Fail($"step '{ConsumerId}': binding '{Path.Text}' could not be resolved: {error}");
            return WriteOutcome.Failed;
        }

        if (propertyName is not null)
        {
            writer.WritePropertyName(propertyName);
        }

        switch (value.Kind)
        {
            case ResolvedKind.Json:
                value.Element.WriteTo(writer);
                break;
            case ResolvedKind.String:
                writer.WriteStringValue(value.Text);
                break;
            case ResolvedKind.Boolean:
                writer.WriteBooleanValue(value.Boolean);
                break;
            default:
                writer.WriteNullValue();
                break;
        }

        return WriteOutcome.Written;
    }
}

/// <summary>Turns a raw <c>args</c> element into an executable <see cref="ArgTemplate"/> tree.</summary>
internal static class ArgCompiler
{
    public static ArgTemplate Compile(
        JsonElement args,
        int consumerIndex,
        string consumerId,
        IReadOnlyDictionary<string, int> indexById,
        List<WorkflowDiagnostic> diagnostics,
        List<RefArg> bindings)
    {
        switch (args.ValueKind)
        {
            case JsonValueKind.Undefined:
            case JsonValueKind.Null:
                return ObjectArg.Empty;
            case JsonValueKind.Object:
                return CompileObject(args, consumerIndex, consumerId, indexById, diagnostics, bindings);
            case JsonValueKind.Array:
                return CompileArray(args, consumerIndex, consumerId, indexById, diagnostics, bindings);
            default:
                return new LiteralArg(args);
        }
    }

    private static ArgTemplate CompileObject(
        JsonElement args,
        int consumerIndex,
        string consumerId,
        IReadOnlyDictionary<string, int> indexById,
        List<WorkflowDiagnostic> diagnostics,
        List<RefArg> bindings)
    {
        if (TryGetDirective(args, out var directive, out var payload))
        {
            switch (directive)
            {
                case "$ref":
                    return CompileRef(payload, args, consumerIndex, consumerId, indexById, diagnostics, bindings);
                case "$literal":
                    return new LiteralArg(payload, passThrough: false);
                default:
                    diagnostics.Add(new WorkflowDiagnostic(
                        WorkflowDiagnosticSeverity.Error,
                        WorkflowDiagnosticCode.InvalidBindingDirective,
                        $"step '{consumerId}': unknown binding directive '{directive}'; expected $ref or $literal",
                        consumerIndex,
                        consumerId));
                    return new LiteralArg(args);
            }
        }

        var entries = new KeyValuePair<string, ArgTemplate>[args.GetPropertyCount()];
        var position = 0;
        foreach (var property in args.EnumerateObject())
        {
            entries[position++] = new KeyValuePair<string, ArgTemplate>(
                property.Name,
                Compile(property.Value, consumerIndex, consumerId, indexById, diagnostics, bindings));
        }

        return position == 0 ? ObjectArg.Empty : new ObjectArg(entries);
    }

    private static ArgTemplate CompileArray(
        JsonElement args,
        int consumerIndex,
        string consumerId,
        IReadOnlyDictionary<string, int> indexById,
        List<WorkflowDiagnostic> diagnostics,
        List<RefArg> bindings)
    {
        var items = new ArgTemplate[args.GetArrayLength()];
        var position = 0;
        foreach (var item in args.EnumerateArray())
        {
            items[position++] = Compile(item, consumerIndex, consumerId, indexById, diagnostics, bindings);
        }

        return new ArrayArg(items);
    }

    private static ArgTemplate CompileRef(
        JsonElement payload,
        JsonElement original,
        int consumerIndex,
        string consumerId,
        IReadOnlyDictionary<string, int> indexById,
        List<WorkflowDiagnostic> diagnostics,
        List<RefArg> bindings)
    {
        if (payload.ValueKind != JsonValueKind.String)
        {
            diagnostics.Add(new WorkflowDiagnostic(
                WorkflowDiagnosticSeverity.Error,
                WorkflowDiagnosticCode.InvalidBindingDirective,
                $"step '{consumerId}': '$ref' must be a JSON string path",
                consumerIndex,
                consumerId));
            return new LiteralArg(original);
        }

        if (!BindingPathParser.TryParse(payload.GetString(), out var path, out var error))
        {
            diagnostics.Add(new WorkflowDiagnostic(
                WorkflowDiagnosticSeverity.Error,
                WorkflowDiagnosticCode.InvalidBindingPath,
                $"step '{consumerId}': invalid binding path: {error}",
                consumerIndex,
                consumerId));
            return new LiteralArg(original);
        }

        var producerIndex = indexById.TryGetValue(path!.StepId, out var index) ? index : -1;
        var reference = new RefArg(path, producerIndex, consumerIndex, consumerId);
        bindings.Add(reference);
        return reference;
    }

    private static bool TryGetDirective(JsonElement element, out string directive, out JsonElement payload)
    {
        directive = string.Empty;
        payload = default;
        var count = 0;
        var name = string.Empty;
        JsonElement value = default;
        foreach (var property in element.EnumerateObject())
        {
            count++;
            if (count > 1)
            {
                return false;
            }

            name = property.Name;
            value = property.Value;
        }

        if (count != 1 || name.Length == 0 || name[0] != '$')
        {
            return false;
        }

        directive = name;
        payload = value;
        return true;
    }
}
