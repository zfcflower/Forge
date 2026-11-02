using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Forge.CodeMode;

internal static class ScriptOutput
{
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
                line.Contains("Microsoft.CodeAnalysis", StringComparison.Ordinal) ||
                line.Contains("System.Runtime.CompilerServices", StringComparison.Ordinal))
            {
                continue;
            }

            frames.Add(line);
        }

        var inScript = frames.FindAll(static line => line.Contains("Submission#", StringComparison.Ordinal));
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
