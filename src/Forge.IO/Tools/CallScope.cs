using System.Text.Json;
using Forge.Abstractions;

namespace Forge.IO.Tools;

/// <summary>
/// The workspace boundary of one file or shell call, answered from its arguments alone: an argument that can only mean something
/// outside the workspace (an absolute path outside it, a home path, a <c>..</c> that escapes) makes the whole call an outside
/// call, so the approval policy asks the user once. Nothing here touches the disk - a plain relative path is inside, and a link
/// inside the workspace that points outside is left to the tool's own containment check.
/// </summary>
internal static class CallScope
{
    /// <summary>What a command line is cut on to get fragments that can stand alone as paths.</summary>
    private static readonly char[] ShellSeparators =
    [
        ' ', '\t', '\r', '\n', '"', '\'', '(', ')', ',', ';', '|', '&', '{', '}', '<', '>',
    ];

    /// <summary>The scope of a call from the path arguments it names (<c>path</c>, <c>from</c>, <c>to</c>, …).</summary>
    internal static ToolCallScope Of(string workingDirectory, ToolCall call, params string[] pathNames)
    {
        var values = Values(call.ArgumentsJson, pathNames);
        return values is null ? ToolCallScope.OutsideWorkspace : Of(workingDirectory, values);
    }

    /// <summary>
    /// Outside when any value names a path outside the workspace; blank values and <c>~</c> are skipped. The file tools never
    /// expand <c>~</c>: they answer it with an error (<see cref="WorkspacePath.Resolve"/>), so asking the user to cross a
    /// boundary that cannot be crossed would promise something the call does not do.
    /// </summary>
    internal static ToolCallScope Of(string workingDirectory, IEnumerable<string> values) =>
        Judge(workingDirectory, values, skipHomeRelative: true);

    /// <summary>
    /// The scope of one <c>bash</c> call: its work directory plus every piece of the command line that can stand alone as a path.
    /// Unlike the file tools, a shell does expand <c>~</c>, so a home path is an outside path here.
    /// </summary>
    internal static ToolCallScope OfCommand(string workingDirectory, ToolCall call)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson);
        }
        catch (JsonException)
        {
            return ToolCallScope.OutsideWorkspace;
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return ToolCallScope.OutsideWorkspace;
            }

            var values = new List<string>(16);
            if (Text(document.RootElement, "workdir") is { Length: > 0 } workdir)
            {
                values.Add(workdir);
            }

            if (Text(document.RootElement, "command") is { Length: > 0 } command)
            {
                CollectFragments(command, values);
            }

            return Judge(workingDirectory, values, skipHomeRelative: false);
        }
    }

    /// <summary>
    /// Every piece of a command line that can stand alone as a path: the words, whatever a quote, bracket or comma sets apart (an
    /// interpreter payload such as <c>python -c "open('/etc/x','w')"</c>), the right-hand side of an assignment
    /// (<c>OUT=/tmp/x tool</c>, whose left side the analyzer drops) and the tail glued to a switch (<c>-I/abs/include</c>,
    /// <c>--output=/tmp/x</c>). Still a guard rail, not a shell: a value a variable or a script file supplies is not seen.
    /// </summary>
    private static void CollectFragments(string command, List<string> into)
    {
        foreach (var word in command.Split(ShellSeparators, StringSplitOptions.RemoveEmptyEntries))
        {
            AddWord(word, into);
            if (word.IndexOf('=') < 0)
            {
                continue;
            }

            foreach (var part in word.Split('='))
            {
                if (part.Length > 0)
                {
                    AddWord(part, into);
                }
            }
        }
    }

    /// <summary>One word, plus the path glued to a switch in it (<c>-I/abs/include</c>, <c>/log:C:\x</c>).</summary>
    private static void AddWord(string word, List<string> into)
    {
        into.Add(word);
        if (word[0] is not ('-' or '/'))
        {
            return;
        }

        var separator = word.IndexOfAny(['/', '\\'], word[0] == '/' ? 1 : 0);
        if (separator >= 0)
        {
            into.Add(word[separator..]);
        }
    }

    private static ToolCallScope Judge(string workingDirectory, IEnumerable<string> values, bool skipHomeRelative)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory))
        {
            return ToolCallScope.OutsideWorkspace;
        }

        foreach (var value in values)
        {
            var trimmed = value.AsSpan().Trim();
            if (trimmed.IsEmpty || (skipHomeRelative && trimmed[0] == '~'))
            {
                continue;
            }

            if (ToolScopes.NamesOutside(value, workingDirectory))
            {
                return ToolCallScope.OutsideWorkspace;
            }
        }

        return ToolCallScope.InsideWorkspace;
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>The named arguments as strings; a glob is reduced to the literal directory it starts from. Null when the JSON is unreadable.</summary>
    private static List<string>? Values(string? json, string[] pathNames)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var values = new List<string>(2);
            foreach (var name in pathNames)
            {
                if (document.RootElement.TryGetProperty(name, out var value))
                {
                    Collect(value, values);
                }
            }

            return values;
        }
    }

    private static void Collect(JsonElement value, List<string> into)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.String:
                if (value.GetString() is { } text)
                {
                    into.Add(PathPatterns.LiteralPrefix(text));
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in value.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String && item.GetString() is { } element)
                    {
                        into.Add(PathPatterns.LiteralPrefix(element));
                    }
                }

                break;
        }
    }
}
