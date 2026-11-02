using System.Globalization;
using System.Text;
using System.Text.Json;
using Forge.Abstractions;

namespace Forge.CodeMode;

/// <summary>
/// Builds <c>run_code</c>'s description: how the script surface works plus every callable tool with its side
/// effect and input schema. Output is ordinal-sorted and depends only on the registry, so two turns with the
/// same tools send identical bytes.
/// </summary>
internal static class ToolDescription
{
    private const int OneLineChars = 220;

    private static readonly (string Form, string Meaning)[] Surface =
    [
        ("""await Tools.Call("tool_name", new { arg = 1 })""", "tool result text; throws ToolCallException when the call fails or is denied"),
        ("""await Tools.Call<T>("tool_name", new { arg = 1 })""", "result deserialized from JSON into T"),
        ("""await Tools.CallJson("tool_name", "{\"arg\":1}")""", "same as Call, for hand-written JSON"),
        ("Tools.Json<T>(jsonText)", "parse JSON text into T"),
        ("Log(value)", "captured and appended to the result"),
        ("Cancellation", "token cancelled when the run times out; call ThrowIfCancellationRequested() in long loops"),
    ];

    private static readonly int SurfaceWidth = WidestForm() + 2;

    public static string Build(IReadOnlyList<ITool> tools, CodeModeOptions options, string selfName)
    {
        var callable = new List<ITool>(tools.Count);
        foreach (var tool in tools)
        {
            if (!string.Equals(tool.Definition.Name, selfName, StringComparison.Ordinal))
            {
                callable.Add(tool);
            }
        }

        callable.Sort(static (left, right) => string.CompareOrdinal(left.Definition.Name, right.Definition.Name));

        var text = new StringBuilder(2048 + (callable.Count * 320));
        text.Append("Runs one C# script in-process and returns its value once, so a chain of tool calls costs a single round trip instead of one per call.\n\n");
        text.Append("Script surface:\n");
        foreach (var (form, meaning) in Surface)
        {
            text.Append("  ").Append(form.PadRight(SurfaceWidth)).Append(meaning).Append('\n');
        }

        text.Append("  The last expression, or a top-level `return`, is serialized as the result; `await` and `var` work at top level.\n");
        text.Append("  Awaiting calls one by one serializes them; use Task.WhenAll(...) to overlap independent calls.\n\n");
        text.Append("Language: C# 14 on .NET 10 - async/await, LINQ, collections, Task.WhenAll, System.Text.Json, System.Text.RegularExpressions, string/Math/DateTime/Guid/CultureInfo.\n");
        text.Append("Refused at compile time (the error names the line and column): file and network access, processes, reflection, Environment/AppContext, AppDomain, assembly loading, Console, explicit threads, P/Invoke, unsafe code, #r and #load.\n");
        text.Append("Limits: default timeout ")
            .Append(Seconds(options.DefaultTimeout))
            .Append(" s, maximum ")
            .Append(Seconds(options.MaxTimeout))
            .Append(" s (send timeout_seconds); at most ")
            .Append(options.MaxConcurrentToolCalls.ToString(CultureInfo.InvariantCulture))
            .Append(" inner calls in flight; inner results are cut at ")
            .Append(options.MaxInnerResultChars.ToString(CultureInfo.InvariantCulture))
            .Append(" chars and the returned result at ")
            .Append(options.MaxResultChars.ToString(CultureInfo.InvariantCulture))
            .Append(" chars (logs at ")
            .Append(options.MaxLogChars.ToString(CultureInfo.InvariantCulture))
            .Append(").\n");
        text.Append("This is an in-process sandbox, not process isolation: a script burning CPU in a loop or a runaway regex keeps a thread busy after the timeout is reported, memory is not capped, and only the whole-run budget is enforced. Every inner tool call is approval-checked on its own, exactly like a direct call.\n\n");

        text.Append("Tools (")
            .Append(callable.Count.ToString(CultureInfo.InvariantCulture))
            .Append("), sorted by name:\n");
        foreach (var tool in callable)
        {
            text.Append("  ")
                .Append(tool.Definition.Name)
                .Append(" [")
                .Append(tool.SideEffect switch
                {
                    ToolSideEffect.ReadOnly => "read-only",
                    ToolSideEffect.Write => "write",
                    _ => "execute",
                })
                .Append("]: ")
                .Append(OneLine(tool.Definition.Description))
                .Append(" args=")
                .Append(Compact(tool.Definition.InputSchemaJson))
                .Append('\n');
        }

        return text.ToString();
    }

    private static int WidestForm()
    {
        var widest = 0;
        foreach (var (form, _) in Surface)
        {
            widest = Math.Max(widest, form.Length);
        }

        return widest;
    }

    private static string Seconds(TimeSpan value) =>
        value.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture);

    private static string OneLine(string description)
    {
        if (string.IsNullOrWhiteSpace(description))
        {
            return "(no description)";
        }

        var trimmed = description.AsSpan().TrimStart();
        var end = trimmed.IndexOf('\n');
        var line = (end < 0 ? trimmed : trimmed[..end]).Trim();
        return line.Length <= OneLineChars ? line.ToString() : string.Concat(line[..OneLineChars], "...");
    }

    private static string Compact(string schemaJson)
    {
        if (string.IsNullOrWhiteSpace(schemaJson))
        {
            return "{}";
        }

        try
        {
            using var document = JsonDocument.Parse(schemaJson);
            return JsonSerializer.Serialize(document.RootElement);
        }
        catch (JsonException)
        {
            return schemaJson.Trim();
        }
    }
}
