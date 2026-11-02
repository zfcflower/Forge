// Governed by THIRD_PARTY_NOTICES.md: the grammar regex semantics follow Oniguruma as vscode-textmate uses it, and the
// rewrite rules follow Tint (MIT, Sebastien Ros).

using System.Globalization;
using System.Text;

namespace Forge.Editor.TextMate.Internal.Regexes;

// Rewrites the Oniguruma constructs System.Text.RegularExpressions cannot express into ones it can, and reports the
// patterns that have no equivalent at all. The rewrites are textual and run once per grammar pattern.
internal static partial class OnigurumaTranslator
{
    private const int MaxCallExpansionDepth = 2;

    // Prepares a pattern: extended mode loses its whitespace and comments, the absent operator becomes a tempered
    // scan, and subexpression calls are expanded a bounded number of levels deep.
    private static string? Normalize(string pattern, out string? error)
    {
        error = null;
        var text = pattern;
        if (text.Contains("(?x", StringComparison.Ordinal) || text.StartsWith("(?i", StringComparison.Ordinal) || text.Contains("x)", StringComparison.Ordinal) || text.Contains("x:", StringComparison.Ordinal))
        {
            text = StripExtendedMode(text);
        }

        if (text.Contains("(?~", StringComparison.Ordinal) && !TryReplaceAbsentOperator(text, out text, out error))
        {
            return null;
        }

        if (text.Contains("&&[", StringComparison.Ordinal))
        {
            // [A&&[^B]] is A without B, which .NET writes [A-[B]].
            text = text.Contains("&&[^", StringComparison.Ordinal) ? text.Replace("&&[^", "-[", StringComparison.Ordinal) : text;
        }

        if (text.Contains("\\g<", StringComparison.Ordinal) || text.Contains("\\g'", StringComparison.Ordinal))
        {
            text = ExpandSubexpressionCalls(text);
        }

        return text;
    }

    // Extended mode ignores whitespace and #-comments outside character classes; .NET does the same, but the classes and
    // comments of the grammars are read here first, so the flag is dropped after the text is cleaned.
    private static string StripExtendedMode(string pattern)
    {
        var output = new StringBuilder(pattern.Length);
        var groups = new Stack<bool>();
        var extended = false;
        var pos = 0;
        while (pos < pattern.Length)
        {
            var c = pattern[pos];
            if (c == '[')
            {
                var start = pos;
                SkipClass(pattern, ref pos);
                output.Append(pattern, start, pos - start);
                continue;
            }

            if (c == '\\' && pos + 1 < pattern.Length)
            {
                output.Append(c).Append(pattern[pos + 1]);
                pos += 2;
                continue;
            }

            if (extended)
            {
                if (char.IsWhiteSpace(c))
                {
                    pos++;
                    continue;
                }

                if (c == '#')
                {
                    while (pos < pattern.Length && pattern[pos] != '\n')
                    {
                        pos++;
                    }

                    continue;
                }
            }

            if (c == '(' && pos + 2 < pattern.Length && pattern[pos + 1] == '?' && pattern[pos + 2] == '#')
            {
                var commentEnd = pattern.IndexOf(')', pos + 3);
                if (commentEnd < 0)
                {
                    output.Append(pattern, pos, pattern.Length - pos);
                    break;
                }

                output.Append(pattern, pos, commentEnd - pos + 1);
                pos = commentEnd + 1;
                continue;
            }

            if (c == '(')
            {
                if (pos + 1 < pattern.Length && pattern[pos + 1] == '?')
                {
                    var scan = pos + 2;
                    var on = new StringBuilder();
                    var off = new StringBuilder();
                    var negate = false;
                    while (scan < pattern.Length)
                    {
                        var flag = pattern[scan];
                        if (flag == '-')
                        {
                            negate = true;
                            scan++;
                            continue;
                        }

                        if (flag is 'i' or 'm' or 's' or 'n' or 'x' or 'W' or 'D' or 'S' or 'P')
                        {
                            (negate ? off : on).Append(flag);
                            scan++;
                            continue;
                        }

                        break;
                    }

                    if (scan < pattern.Length && (pattern[scan] == ')' || pattern[scan] == ':') && (on.Length > 0 || off.Length > 0))
                    {
                        var next = off.ToString().Contains('x', StringComparison.Ordinal) ? false : on.ToString().Contains('x', StringComparison.Ordinal) ? true : extended;
                        var keptOn = KeepDotNetFlags(on.ToString());
                        var keptOff = KeepDotNetFlags(off.ToString());
                        var kept = keptOn.Length > 0 || keptOff.Length > 0;
                        if (pattern[scan] == ':')
                        {
                            output.Append('(').Append('?').Append(keptOn);
                            if (keptOff.Length > 0)
                            {
                                output.Append('-').Append(keptOff);
                            }

                            output.Append(':');
                            groups.Push(extended);
                        }
                        else if (kept)
                        {
                            output.Append('(').Append('?').Append(keptOn);
                            if (keptOff.Length > 0)
                            {
                                output.Append('-').Append(keptOff);
                            }

                            output.Append(')');
                        }

                        extended = next;
                        pos = scan + 1;
                        continue;
                    }
                }

                output.Append('(');
                groups.Push(extended);
                pos++;
                continue;
            }

            if (c == ')')
            {
                output.Append(')');
                if (groups.Count > 0)
                {
                    extended = groups.Pop();
                }

                pos++;
                continue;
            }

            output.Append(c);
            pos++;
        }

        return output.ToString();
    }

    private static string KeepDotNetFlags(string flags)
    {
        var kept = new StringBuilder(flags.Length);
        foreach (var flag in flags)
        {
            if (flag is 'i' or 'm' or 's' or 'n')
            {
                kept.Append(flag);
            }
        }

        return kept.ToString();
    }

    // (?~expr): Oniguruma's absent operator matches text that does not contain expr. The .NET form is a tempered scan,
    // which stops before the first occurrence instead of consuming to the end of the subject.
    private static bool TryReplaceAbsentOperator(string pattern, out string replaced, out string? error)
    {
        error = null;
        replaced = pattern;
        while (true)
        {
            var index = replaced.IndexOf("(?~", StringComparison.Ordinal);
            if (index < 0)
            {
                return true;
            }

            var end = FindGroupEnd(replaced, index);
            if (end < 0)
            {
                error = "Unterminated (?~ group.";
                return false;
            }

            var inner = replaced.Substring(index + 3, end - index - 3);
            replaced = string.Concat(replaced.Substring(0, index), "(?:(?!(?:", inner, "))[\\s\\S])*", replaced.Substring(end + 1));
        }
    }

    // \g<name> re-runs the named subexpression. .NET has no subexpression calls, so the body is inlined; its own capture
    // groups become non-capturing to keep the numbering the grammar's capture rules refer to. Self-recursive calls are
    // inlined up to MaxCallExpansionDepth levels, past which they match a run without brackets.
    private static string ExpandSubexpressionCalls(string pattern)
    {
        var text = pattern;
        for (var depth = 0; depth <= MaxCallExpansionDepth; depth++)
        {
            if (!HasCall(text))
            {
                return text;
            }

            var bodies = FindGroupBodies(text);
            var output = new StringBuilder(text.Length * 2);
            var pos = 0;
            while (pos < text.Length)
            {
                var c = text[pos];
                if (c == '\\' && pos + 2 < text.Length && text[pos + 1] == 'g' && text[pos + 2] is '<' or '\'')
                {
                    var close = text[pos + 2] == '<' ? '>' : '\'';
                    var end = text.IndexOf(close, pos + 3);
                    if (end < 0)
                    {
                        output.Append(text, pos, text.Length - pos);
                        break;
                    }

                    var name = text.Substring(pos + 3, end - pos - 3);
                    if (depth == MaxCallExpansionDepth || !bodies.TryGetValue(name, out var body))
                    {
                        output.Append("(?:[^()<>]+)");
                    }
                    else
                    {
                        output.Append("(?:").Append(StripCaptures(body)).Append(')');
                    }

                    pos = end + 1;
                    continue;
                }

                output.Append(c);
                pos++;
            }

            text = output.ToString();
        }

        return text;
    }

    private static bool HasCall(string text) => text.Contains("\\g<", StringComparison.Ordinal) || text.Contains("\\g'", StringComparison.Ordinal);

    // The bodies of the named groups of a pattern, by name.
    private static Dictionary<string, string> FindGroupBodies(string pattern)
    {
        var bodies = new Dictionary<string, string>(StringComparer.Ordinal);
        var pos = 0;
        while (pos < pattern.Length)
        {
            var c = pattern[pos];
            if (c == '\\')
            {
                pos += 2;
                continue;
            }

            if (c == '[')
            {
                SkipClass(pattern, ref pos);
                continue;
            }

            if (c != '(' || pos + 2 >= pattern.Length || pattern[pos + 1] != '?')
            {
                pos++;
                continue;
            }

            var kind = pattern[pos + 2];
            string? name = null;
            var bodyStart = -1;
            if (kind is '<' or '\'' && pos + 3 < pattern.Length && pattern[pos + 3] is not ('=' or '!'))
            {
                var close = kind == '<' ? '>' : '\'';
                var end = pattern.IndexOf(close, pos + 3);
                if (end < 0)
                {
                    break;
                }

                name = pattern.Substring(pos + 3, end - pos - 3);
                bodyStart = end + 1;
            }
            else if (kind == 'P' && pos + 3 < pattern.Length && pattern[pos + 3] == '<')
            {
                var end = pattern.IndexOf('>', pos + 4);
                if (end < 0)
                {
                    break;
                }

                name = pattern.Substring(pos + 4, end - pos - 4);
                bodyStart = end + 1;
            }

            if (name is null)
            {
                pos += 2;
                continue;
            }

            var groupEnd = FindGroupEnd(pattern, pos);
            if (groupEnd < 0)
            {
                break;
            }

            bodies[name] = pattern.Substring(bodyStart, groupEnd - bodyStart);
            pos = bodyStart;
        }

        return bodies;
    }

    // Turns every capturing group of an inlined body into a non-capturing one, so inlining does not renumber the
    // captures the grammar's rules refer to.
    private static string StripCaptures(string body)
    {
        var output = new StringBuilder(body.Length);
        var pos = 0;
        while (pos < body.Length)
        {
            var c = body[pos];
            if (c == '\\' && pos + 1 < body.Length)
            {
                output.Append(c).Append(body[pos + 1]);
                pos += 2;
                continue;
            }

            if (c == '[')
            {
                var start = pos;
                SkipClass(body, ref pos);
                output.Append(body, start, pos - start);
                continue;
            }

            if (c != '(')
            {
                output.Append(c);
                pos++;
                continue;
            }

            if (pos + 1 < body.Length && body[pos + 1] == '?')
            {
                if (pos + 2 < body.Length && body[pos + 2] is '<' or '\'' && !(pos + 3 < body.Length && body[pos + 3] is '=' or '!'))
                {
                    var close = body[pos + 2] == '<' ? '>' : '\'';
                    var end = body.IndexOf(close, pos + 3);
                    output.Append("(?:");
                    pos = end < 0 ? pos + 3 : end + 1;
                    continue;
                }

                if (pos + 3 < body.Length && body[pos + 2] == 'P' && body[pos + 3] == '<')
                {
                    var end = body.IndexOf('>', pos + 4);
                    output.Append("(?:");
                    pos = end < 0 ? pos + 4 : end + 1;
                    continue;
                }

                output.Append("(?");
                pos += 2;
                continue;
            }

            output.Append("(?:");
            pos++;
        }

        return output.ToString();
    }

    // The index of the ')' that closes the group opened at start.
    private static int FindGroupEnd(string pattern, int start)
    {
        var depth = 0;
        var pos = start;
        while (pos < pattern.Length)
        {
            var c = pattern[pos];
            if (c == '\\')
            {
                pos += 2;
                continue;
            }

            if (c == '[')
            {
                SkipClass(pattern, ref pos);
                continue;
            }

            if (c == '(')
            {
                depth++;
                pos++;
                continue;
            }

            if (c == ')')
            {
                depth--;
                if (depth == 0)
                {
                    return pos;
                }

                pos++;
                continue;
            }

            pos++;
        }

        return -1;
    }
}
