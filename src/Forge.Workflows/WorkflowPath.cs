using System.Globalization;
using System.Text;

namespace Forge.Workflows;

/// <summary>The part of a step result a binding path can start from.</summary>
internal enum BindingNode : byte
{
    Content,
    IsError,
    Status,
    Error,
    Json,
}

/// <summary>One navigation step: an object property or an array index.</summary>
internal readonly struct PathSegment
{
    private PathSegment(string? name, int index, bool isIndex)
    {
        Name = name;
        Index = index;
        IsIndex = isIndex;
    }

    public string? Name { get; }

    public int Index { get; }

    public bool IsIndex { get; }

    public static PathSegment Property(string name) => new(name, 0, false);

    public static PathSegment Element(int index) => new(null, index, true);
}

/// <summary>Which root a <c>$ref</c> path reads from: an earlier step's result, or the run's own input.</summary>
internal enum BindingRoot : byte
{
    /// <summary><c>$.steps.&lt;id&gt;.&lt;field&gt;</c>: a field of an earlier step's result.</summary>
    Step,

    /// <summary><c>$input</c> or <c>$input.a.b</c>: <see cref="RunOptions.Input"/> of this run.</summary>
    Input,
}

/// <summary>A parsed <c>$ref</c> path: <c>$.steps.&lt;id&gt;.&lt;field&gt;</c> or <c>$input</c>, followed by navigation segments.</summary>
internal sealed class BindingPath
{
    public BindingPath(string text, BindingRoot root, string stepId, BindingNode node, PathSegment[] tail, bool optional)
    {
        Text = text;
        Root = root;
        StepId = stepId;
        Node = node;
        Tail = tail;
        Optional = optional;
    }

    /// <summary>The path as written in the definition, used verbatim in error messages.</summary>
    public string Text { get; }

    /// <summary>Which root this path reads from.</summary>
    public BindingRoot Root { get; }

    /// <summary>The producing step id; empty for an <c>$input</c> path.</summary>
    public string StepId { get; }

    /// <summary>Which part of the producing step's result the path starts from.</summary>
    public BindingNode Node { get; }

    /// <summary>Segments after the field, empty when the path ends at the field.</summary>
    public PathSegment[] Tail { get; }

    /// <summary>When true a failed resolution omits the value instead of failing the step.</summary>
    public bool Optional { get; }

    public string RenderPrefix(int tailCount) => Root == BindingRoot.Input
        ? BindingPathParser.RenderInputPrefix(Tail.AsSpan(0, tailCount))
        : BindingPathParser.RenderPrefix(StepId, Node, Tail.AsSpan(0, tailCount));
}

/// <summary>Parses the binding path subset documented in <c>README.md</c>. No allocation on the happy path
/// beyond the segment array; failures return a message instead of throwing.</summary>
internal static class BindingPathParser
{
    /// <summary>Spelling of the run-input root. The <c>$</c> is required: a bare <c>input</c> would be read as a step name.</summary>
    public const string InputRoot = "$input";

    public static bool TryParse(string? text, out BindingPath? path, out string? error)
    {
        path = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            error = "binding path is empty";
            return false;
        }

        var span = text.AsSpan().Trim();
        var segments = new List<PathSegment>(4);
        var i = 0;

        // The $input root is parsed first and stands alone: it is not a step id, so the step grammar below never sees it.
        if (span.StartsWith(InputRoot, StringComparison.Ordinal) &&
            (span.Length == InputRoot.Length || span[InputRoot.Length] is '.' or '[' or '?'))
        {
            i = InputRoot.Length;
            if (!TryReadTail(span, ref i, segments, out var optionalInput, out error))
            {
                return false;
            }

            path = new BindingPath(
                text,
                BindingRoot.Input,
                string.Empty,
                BindingNode.Json,
                segments.Count == 0 ? Array.Empty<PathSegment>() : [.. segments],
                optionalInput);
            error = null;
            return true;
        }

        if (span[0] == '$')
        {
            i = 1;
            if (i == span.Length)
            {
                error = "binding path '$' has no segments";
                return false;
            }
        }
        else
        {
            if (!ReadProperty(span, ref i, out var firstName, out error))
            {
                return false;
            }

            segments.Add(PathSegment.Property(firstName));
        }

        if (!TryReadTail(span, ref i, segments, out var optional, out error))
        {
            return false;
        }

        if (segments.Count < 3)
        {
            error = "binding path must be $.steps.<stepId>.<field>";
            return false;
        }

        var root = segments[0];
        if (root.IsIndex || !string.Equals(root.Name, "steps", StringComparison.Ordinal))
        {
            error = "binding path must start with 'steps' (for example $.steps.fetch.json.name)";
            return false;
        }

        var stepSegment = segments[1];
        if (stepSegment.IsIndex || string.IsNullOrEmpty(stepSegment.Name))
        {
            error = "binding path must name a step id after 'steps'";
            return false;
        }

        var fieldSegment = segments[2];
        if (fieldSegment.IsIndex || !TryParseNode(fieldSegment.Name!, out var node))
        {
            error = $"unknown field '{fieldSegment.Name}' in binding path; expected content, isError, status, error or json";
            return false;
        }

        PathSegment[] tail = segments.Count == 3
            ? Array.Empty<PathSegment>()
            : segments.GetRange(3, segments.Count - 3).ToArray();
        path = new BindingPath(text, BindingRoot.Step, stepSegment.Name!, node, tail, optional);
        error = null;
        return true;
    }

    /// <summary>
    /// Reads the navigation segments that follow a root (<c>.name</c>, <c>[index]</c>, <c>['quoted name']</c>) plus the
    /// trailing optional marker. Returns <c>false</c> with a message instead of throwing; a path that ends right where the
    /// caller left off is a valid root-only path.
    /// </summary>
    private static bool TryReadTail(
        ReadOnlySpan<char> span,
        ref int i,
        List<PathSegment> segments,
        out bool optional,
        out string? error)
    {
        optional = false;
        while (i < span.Length)
        {
            var c = span[i];
            if (c == '?')
            {
                optional = true;
                i++;
                if (i < span.Length)
                {
                    error = $"unexpected text after '?' at position {i}";
                    return false;
                }

                break;
            }

            if (c == '.')
            {
                i++;
                if (!ReadProperty(span, ref i, out var name, out error))
                {
                    return false;
                }

                segments.Add(PathSegment.Property(name));
                continue;
            }

            if (c == '[')
            {
                if (!ReadBracket(span, ref i, out var segment, out error))
                {
                    return false;
                }

                segments.Add(segment);
                continue;
            }

            error = $"unexpected character '{c}' at position {i}";
            return false;
        }

        error = null;
        return true;
    }

    /// <summary>Renders an <c>$input</c> path prefix for error messages.</summary>
    public static string RenderInputPrefix(ReadOnlySpan<PathSegment> tail)
    {
        var sb = new StringBuilder(InputRoot);
        foreach (var segment in tail)
        {
            if (segment.IsIndex)
            {
                sb.Append('[').Append(segment.Index).Append(']');
            }
            else
            {
                AppendName(sb, segment.Name!);
            }
        }

        return sb.ToString();
    }

    public static string RenderPrefix(string stepId, BindingNode node, ReadOnlySpan<PathSegment> tail)
    {
        var sb = new StringBuilder("$.steps");
        AppendName(sb, stepId);
        sb.Append('.').Append(NodeName(node));
        foreach (var segment in tail)
        {
            if (segment.IsIndex)
            {
                sb.Append('[').Append(segment.Index).Append(']');
            }
            else
            {
                AppendName(sb, segment.Name!);
            }
        }

        return sb.ToString();
    }

    private static void AppendName(StringBuilder sb, string name)
    {
        if (IsBareName(name))
        {
            sb.Append('.').Append(name);
            return;
        }

        sb.Append("['");
        foreach (var c in name)
        {
            if (c is '\\' or '\'')
            {
                sb.Append('\\');
            }

            sb.Append(c);
        }

        sb.Append("']");
    }

    private static bool IsBareName(string name)
    {
        if (name.Length == 0)
        {
            return false;
        }

        foreach (var c in name)
        {
            if (!IsBareChar(c))
            {
                return false;
            }
        }

        return true;
    }

    private static string NodeName(BindingNode node) => node switch
    {
        BindingNode.Content => "content",
        BindingNode.IsError => "isError",
        BindingNode.Status => "status",
        BindingNode.Error => "error",
        _ => "json",
    };

    private static bool TryParseNode(string name, out BindingNode node)
    {
        switch (name)
        {
            case "content":
                node = BindingNode.Content;
                return true;
            case "isError":
                node = BindingNode.IsError;
                return true;
            case "status":
                node = BindingNode.Status;
                return true;
            case "error":
                node = BindingNode.Error;
                return true;
            case "json":
                node = BindingNode.Json;
                return true;
            default:
                node = default;
                return false;
        }
    }

    private static bool IsBareChar(char c) =>
        c is not ('.' or '[' or ']' or '?' or '\'' or '"' or '\\') && !char.IsWhiteSpace(c) && !char.IsControl(c);

    private static void SkipWhitespace(ReadOnlySpan<char> span, ref int i)
    {
        while (i < span.Length && char.IsWhiteSpace(span[i]))
        {
            i++;
        }
    }

    private static bool ReadProperty(ReadOnlySpan<char> span, ref int i, out string name, out string? error)
    {
        var start = i;
        while (i < span.Length && IsBareChar(span[i]))
        {
            i++;
        }

        if (i == start)
        {
            name = string.Empty;
            error = $"expected a name at position {start}";
            return false;
        }

        name = span[start..i].ToString();
        error = null;
        return true;
    }

    private static bool ReadBracket(ReadOnlySpan<char> span, ref int i, out PathSegment segment, out string? error)
    {
        segment = default;
        i++;
        SkipWhitespace(span, ref i);
        if (i >= span.Length)
        {
            error = "unterminated '['";
            return false;
        }

        var c = span[i];
        if (c is '\'' or '"')
        {
            var quote = c;
            i++;
            var sb = new StringBuilder();
            var closed = false;
            while (i < span.Length)
            {
                var ch = span[i];
                if (ch == '\\' && i + 1 < span.Length)
                {
                    sb.Append(span[i + 1]);
                    i += 2;
                    continue;
                }

                if (ch == quote)
                {
                    i++;
                    closed = true;
                    break;
                }

                sb.Append(ch);
                i++;
            }

            if (!closed)
            {
                error = "unterminated quoted name";
                return false;
            }

            segment = PathSegment.Property(sb.ToString());
        }
        else
        {
            var start = i;
            while (i < span.Length && char.IsAsciiDigit(span[i]))
            {
                i++;
            }

            if (i == start)
            {
                error = $"expected a name or a non-negative index at position {start}";
                return false;
            }

            if (!int.TryParse(span[start..i], NumberStyles.None, CultureInfo.InvariantCulture, out var index))
            {
                error = $"array index '{span[start..i]}' is too large";
                return false;
            }

            segment = PathSegment.Element(index);
        }

        SkipWhitespace(span, ref i);
        if (i >= span.Length || span[i] != ']')
        {
            error = "expected ']'";
            return false;
        }

        i++;
        error = null;
        return true;
    }
}
