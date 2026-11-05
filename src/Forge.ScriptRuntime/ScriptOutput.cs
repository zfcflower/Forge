using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Forge.Scripting;

/// <summary>Captured <c>Log(...)</c> output of one script run, bounded to <c>maxChars</c>.</summary>
internal sealed class ScriptLog(int limit)
{
    private readonly StringBuilder buffer = new();
    private readonly Lock gate = new();
    private bool truncated;

    public void Write(object? value)
    {
        var line = value?.ToString() ?? "null";
        lock (gate)
        {
            if (buffer.Length >= limit)
            {
                truncated = true;
                return;
            }

            var room = limit - buffer.Length;
            if (line.Length > room)
            {
                buffer.Append(line, 0, room);
                truncated = true;
                return;
            }

            buffer.Append(line).Append('\n');
        }
    }

    public string Snapshot()
    {
        lock (gate)
        {
            if (buffer.Length == 0)
            {
                return string.Empty;
            }

            return truncated
                ? buffer.ToString().TrimEnd('\n') + "\n[log truncated at " + limit.ToString(CultureInfo.InvariantCulture) + " chars]"
                : buffer.ToString().TrimEnd('\n');
        }
    }
}

/// <summary>Turns a script's return value into the text the model sees, and describes script exceptions compactly.</summary>
internal static class ScriptOutput
{
    /// <summary>The file name the compiled entry point maps its source lines to, so a stack frame names the script.</summary>
    internal const string SourcePath = "script.csx";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = false };

    public static string Format(object? value)
    {
        switch (value)
        {
            case null:
                return "null";
            case string text:
                return text;
            case JsonElement element:
                return element.GetRawText();
            case JsonDocument document:
                return document.RootElement.GetRawText();
            case JsonNode node:
                return node.ToJsonString(JsonOptions);
            case bool flag:
                return flag ? "true" : "false";
            case char character:
                return character.ToString();
        }

        try
        {
            return JsonSerializer.Serialize(value, value.GetType(), JsonOptions);
        }
        catch (Exception ex) when (ex is NotSupportedException or JsonException or InvalidOperationException)
        {
            return Convert.ToString(value, CultureInfo.InvariantCulture) ?? "null";
        }
    }

    public static string Describe(Exception exception)
    {
        var text = new StringBuilder(512);
        Append(text, exception);
        if (exception is AggregateException aggregate && aggregate.InnerException is { } inner)
        {
            text.Append("\ncaused by ");
            Append(text, inner);
        }

        return text.ToString();
    }

    private static void Append(StringBuilder text, Exception exception)
    {
        text.Append(exception.GetType().Name);
        if (!string.IsNullOrWhiteSpace(exception.Message))
        {
            text.Append(": ").Append(exception.Message.Trim());
        }

        var stack = (exception.StackTrace ?? string.Empty).Split('\n');
        var frames = new List<string>(8);
        foreach (var raw in stack)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("---", StringComparison.Ordinal) ||
                line.Contains("System.Runtime.CompilerServices", StringComparison.Ordinal))
            {
                continue;
            }

            frames.Add(line);
        }

        var inScript = frames.FindAll(
            static line => line.Contains(SourcePath, StringComparison.Ordinal) ||
                           line.Contains("Submission#", StringComparison.Ordinal));
        var shown = inScript.Count > 0 ? inScript : frames;
        var limit = inScript.Count > 0 ? 4 : 2;
        for (var i = 0; i < shown.Count; i++)
        {
            if (i >= limit)
            {
                text.Append("\n  ...");
                break;
            }

            text.Append('\n').Append(shown[i]);
        }
    }
}

/// <summary>Bounded text: at most <c>maxChars</c> characters, with a trailing note when characters were dropped.</summary>
public static class TextBounds
{
    public static string Truncate(string text, int maxChars, string what)
    {
        if (maxChars <= 0)
        {
            return string.Empty;
        }

        if (text.Length <= maxChars)
        {
            return text;
        }

        return new StringBuilder(maxChars + 96)
            .Append(text, 0, maxChars)
            .Append("\n[... ")
            .Append(what)
            .Append(" truncated: kept ")
            .Append(maxChars.ToString(CultureInfo.InvariantCulture))
            .Append(" of ")
            .Append(text.Length.ToString(CultureInfo.InvariantCulture))
            .Append(" chars]")
            .ToString();
    }
}
