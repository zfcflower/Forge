// Governed by THIRD_PARTY_NOTICES.md: the grammar grammar-regex semantics follow Oniguruma as vscode-textmate uses it,
// and the rewrite rules follow Tint (MIT, Sebastien Ros).

using System.Globalization;
using System.Text;

namespace Forge.Editor.TextMate.Internal.Regexes;

// Translates the Oniguruma subset TextMate grammars use into System.Text.RegularExpressions syntax. Constructs without
// a .NET equivalent are reported instead of silently matching something else.
internal static partial class OnigurumaTranslator
{
    private const char UnsupportedAnchor = '\uffff';

    private static readonly OnigurumaCharClass[] s_posixClasses =
    [
        new("alnum", "0-9A-Za-z"),
        new("alpha", "A-Za-z"),
        new("ascii", "\\x00-\\x7F"),
        new("blank", " \\t"),
        new("cntrl", "\\x00-\\x1F\\x7F"),
        new("digit", "0-9"),
        new("graph", "!-~"),
        new("lower", "a-z"),
        new("print", " -~"),
        new("punct", "!-/:-@\\[-`{-~"),
        new("space", " \\t\\n\\v\\f\\r"),
        new("upper", "A-Z"),
        new("word", "0-9A-Za-z_"),
        new("xdigit", "0-9A-Fa-f"),
    ];

    private static readonly OnigurumaCharClass[] s_unicodeProperties =
    [
        new("AHex", "0-9A-Fa-f"),
        new("Alnum", "0-9A-Za-z"),
        new("Alpha", "A-Za-z"),
        new("ASCII", "\\x00-\\x7F"),
        new("Any", "\\x00-\\uffff"),
        new("Blank", " \\t"),
        new("Cntrl", "\\x00-\\x1F\\x7F"),
        new("Digit", "0-9"),
        new("Graph", "!-~"),
        new("Lower", "a-z"),
        new("Print", " -~"),
        new("Punct", "!-/:-@\\[-`{-~"),
        new("Space", " \\t\\n\\v\\f\\r"),
        new("Upper", "A-Z"),
        new("XDigit", "0-9A-Fa-f"),
        new("Word", "0-9A-Za-z_"),
        new("Alphanumeric", "0-9A-Za-z"),
        new("Letter", "A-Za-z"),
        new("Ideographic", "\\u3006\\u3007\\u3021-\\u3029\\u3400-\\u4DBF\\u4E00-\\u9FFF\\uF900-\\uFA2D"),
        new("Han", "\\u2E80-\\u2EFF\\u3005\\u3007\\u3400-\\u4DBF\\u4E00-\\u9FFF\\uF900-\\uFAFF"),
        new("Latin", "A-Za-z\\u00C0-\\u024F\\u1E00-\\u1EFF"),
        new("Greek", "\\u0370-\\u03FF\\u1F00-\\u1FFF"),
        new("Cyrillic", "\\u0400-\\u052F\\u2DE0-\\u2DFF\\uA640-\\uA69F"),
        new("Hebrew", "\\u0590-\\u05FF"),
        new("Arabic", "\\u0600-\\u06FF\\u0750-\\u077F\\u08A0-\\u08FF"),
        new("Hiragana", "\\u3040-\\u309F"),
        new("Katakana", "\\u30A0-\\u30FF\\u31F0-\\u31FF"),
        new("Hangul", "\\u1100-\\u11FF\\u3130-\\u318F\\uAC00-\\uD7AF"),
        new("Thai", "\\u0E00-\\u0E7F"),
        new("Devanagari", "\\u0900-\\u097F"),
    ];

    public static bool TryTranslate(string original, out string? translated, out string? error)
    {
        translated = null;
        error = null;

        var pattern = Normalize(original, out error);
        if (pattern is null)
        {
            return false;
        }

        var groups = CountGroups(pattern, out var groupCount, out var countError);
        if (countError is not null)
        {
            error = countError;
            return false;
        }

        var output = new StringBuilder(pattern.Length + 16);
        var atomStart = -1;
        var groupIndex = 0;
        var pos = 0;
        while (pos < pattern.Length)
        {
            var c = pattern[pos];
            if (c == '\\')
            {
                atomStart = output.Length;
                if (!TranslateEscape(pattern, ref pos, output, inClass: false, groups, groupCount, out error))
                {
                    return false;
                }

                continue;
            }

            if (c == '[')
            {
                atomStart = output.Length;
                if (!TranslateClass(pattern, ref pos, output, groups, groupCount, out error))
                {
                    return false;
                }

                continue;
            }

            if (c == '(')
            {
                atomStart = output.Length;
                if (!TranslateGroupOpen(pattern, ref pos, output, groups, ref groupIndex, out error))
                {
                    return false;
                }

                continue;
            }

            if (c is '*' or '+' or '?' || (c == '{' && TryReadQuantifier(pattern, pos, out _, out _)))
            {
                if (!TranslateQuantifier(pattern, ref pos, output, ref atomStart, out error))
                {
                    return false;
                }

                continue;
            }

            if (c is ')' or '|' or '^' or '$')
            {
                atomStart = -1;
                output.Append(c);
                pos++;
                continue;
            }

            if (c == '.')
            {
                atomStart = output.Length;
                output.Append('.');
                pos++;
                continue;
            }

            atomStart = output.Length;
            AppendLiteral(output, c);
            pos++;
        }

        if (groupIndex != groupCount)
        {
            // A group opened or closed unevenly leaves .NET's numbering different from the counts taken up front.
            error = "Unbalanced groups after translation.";
            return false;
        }

        translated = output.ToString();
        return true;
    }

    // The number of each named group, highest number on repeated names, as Oniguruma resolves a name back-reference.
    private static Dictionary<string, int> CountGroups(string pattern, out int groupCount, out string? error)
    {
        error = null;
        groupCount = 0;
        var groups = new Dictionary<string, int>(StringComparer.Ordinal);
        var index = 0;
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

            if (c != '(')
            {
                pos++;
                continue;
            }

            pos++;
            if (pos >= pattern.Length || pattern[pos] != '?')
            {
                index++;
                continue;
            }

            pos++;
            if (pos >= pattern.Length)
            {
                break;
            }

            var kind = pattern[pos];
            switch (kind)
            {
                case '#' when TrySkipComment(pattern, ref pos, out var commentLength):
                    pos += commentLength;
                    continue;
                case '<' when pos + 1 < pattern.Length && pattern[pos + 1] is '=' or '!':
                case '\'' when pos + 1 < pattern.Length && pattern[pos + 1] is '=' or '!':
                    pos++;
                    continue;
                case '<' or '\'':
                    var close = kind == '<' ? '>' : '\'';
                    var start = pos + 1;
                    var end = pattern.IndexOf(close, start);
                    if (end < 0)
                    {
                        error = "Unterminated group name in pattern.";
                        groupCount = index;
                        return groups;
                    }

                    index++;
                    groups[pattern.Substring(start, end - start)] = index;
                    pos = end + 1;
                    continue;
                case 'P' when pos + 1 < pattern.Length && pattern[pos + 1] == '<':
                    var namedStart = pos + 2;
                    var namedEnd = pattern.IndexOf('>', namedStart);
                    if (namedEnd < 0)
                    {
                        error = "Unterminated group name in pattern.";
                        groupCount = index;
                        return groups;
                    }

                    index++;
                    groups[pattern.Substring(namedStart, namedEnd - namedStart)] = index;
                    pos = namedEnd + 1;
                    continue;
                default:
                    pos++;
                    continue;
            }
        }

        groupCount = index;
        return groups;
    }

    private static void SkipClass(string pattern, ref int pos)
    {
        pos++;
        if (pos < pattern.Length && pattern[pos] == '^')
        {
            pos++;
        }

        if (pos < pattern.Length && pattern[pos] == ']')
        {
            pos++;
        }

        while (pos < pattern.Length)
        {
            if (pattern[pos] == '\\')
            {
                pos += 2;
                continue;
            }

            if (pattern[pos] == '[' && pos + 1 < pattern.Length && pattern[pos + 1] is ':' or '.' or '=')
            {
                var terminator = pattern[pos + 1] == ':' ? ":]" : pattern[pos + 1] == '.' ? ".]" : "=]";
                var end = pattern.IndexOf(terminator, pos + 2, StringComparison.Ordinal);
                if (end >= 0)
                {
                    pos = end + 2;
                    continue;
                }
            }

            if (pattern[pos] == ']')
            {
                pos++;
                return;
            }

            pos++;
        }
    }

    private static bool TrySkipComment(string pattern, ref int pos, out int length)
    {
        var end = pattern.IndexOf(')', pos + 1);
        if (end < 0)
        {
            length = 0;
            return false;
        }

        length = end - pos + 1;
        return true;
    }

    private static bool TranslateGroupOpen(string pattern, ref int pos, StringBuilder output, Dictionary<string, int> groups, ref int groupIndex, out string? error)
    {
        error = null;
        if (pos + 1 >= pattern.Length || pattern[pos + 1] != '?')
        {
            groupIndex++;
            output.Append('(');
            pos++;
            return true;
        }

        if (pos + 2 >= pattern.Length)
        {
            error = "Unterminated group.";
            return false;
        }

        var kind = pattern[pos + 2];
        switch (kind)
        {
            case '#':
                var end = pattern.IndexOf(')', pos + 3);
                if (end < 0)
                {
                    error = "Unterminated comment group.";
                    return false;
                }

                output.Append(pattern, pos, end - pos + 1);
                pos = end + 1;
                return true;

            case '<' or '\'':
                if (pos + 3 < pattern.Length && pattern[pos + 3] is '=' or '!')
                {
                    output.Append(pattern, pos, 3);
                    pos += 3;
                    return true;
                }

                var close = kind == '<' ? '>' : '\'';
                var endName = pattern.IndexOf(close, pos + 3);
                if (endName < 0)
                {
                    error = "Unterminated group name.";
                    return false;
                }

                groupIndex++;
                output.Append('(');
                pos = endName + 1;
                return true;

            case 'P' when pos + 3 < pattern.Length && pattern[pos + 3] == '<':
                var endP = pattern.IndexOf('>', pos + 4);
                if (endP < 0)
                {
                    error = "Unterminated group name.";
                    return false;
                }

                groupIndex++;
                output.Append('(');
                pos = endP + 1;
                return true;

            case 'P' when pos + 3 < pattern.Length && pattern[pos + 3] == '=':
                var endRef = pattern.IndexOf(')', pos + 4);
                if (endRef < 0)
                {
                    error = "Unterminated named back-reference.";
                    return false;
                }

                if (!groups.TryGetValue(pattern.Substring(pos + 4, endRef - pos - 4), out var referenced))
                {
                    error = "Back-reference to an undefined group name.";
                    return false;
                }

                output.Append('\\').Append(referenced.ToString(CultureInfo.InvariantCulture));
                pos = endRef + 1;
                return true;

            case '~':
                error = "(?~ absent repeater is not supported.";
                return false;

            default:
                output.Append(pattern, pos, 3);
                pos += 3;
                return true;
        }
    }

    private static bool TranslateQuantifier(string pattern, ref int pos, StringBuilder output, ref int atomStart, out string? error)
    {
        error = null;
        var c = pattern[pos];
        if (c == '{')
        {
            if (!TryReadQuantifier(pattern, pos, out var closeIndex, out var possessive))
            {
                error = "Invalid repetition.";
                return false;
            }

            output.Append('{');
            var content = pattern.AsSpan(pos + 1, closeIndex - pos - 1);
            var comma = content.IndexOf(',');
            if (comma == 0)
            {
                output.Append('0').Append(content);
            }
            else if (comma > 0
                && int.TryParse(content.Slice(0, comma), NumberStyles.None, CultureInfo.InvariantCulture, out var minimum)
                && int.TryParse(content.Slice(comma + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var maximum)
                && minimum > maximum)
            {
                // Oniguruma accepts an inverted bound; the grammars use it as "at most", so the upper bound wins.
                output.Append('0').Append(',').Append(maximum.ToString(CultureInfo.InvariantCulture));
            }
            else
            {
                output.Append(content);
            }

            output.Append('}');
            pos = closeIndex + 1;
            if (possessive && pos < pattern.Length && pattern[pos] == '+')
            {
                pos++;
                WrapAtomic(output, atomStart);
            }

            return true;
        }

        output.Append(c);
        pos++;
        var possessiveQuantifier = pos < pattern.Length && pattern[pos] == '+';
        if (possessiveQuantifier)
        {
            pos++;
            WrapAtomic(output, atomStart);
            return true;
        }

        if (pos < pattern.Length && pattern[pos] == '?')
        {
            output.Append('?');
            pos++;
        }

        return true;
    }

    private static void WrapAtomic(StringBuilder output, int atomStart)
    {
        if (atomStart < 0 || atomStart > output.Length)
        {
            return;
        }

        output.Insert(atomStart, "(?>");
        output.Append(')');
    }

    private static bool TryReadQuantifier(string pattern, int pos, out int closeIndex, out bool possessive)
    {
        closeIndex = -1;
        possessive = false;
        if (pos >= pattern.Length || pattern[pos] != '{')
        {
            return false;
        }

        var i = pos + 1;
        var digits = 0;
        while (i < pattern.Length && char.IsAsciiDigit(pattern[i]))
        {
            digits++;
            i++;
        }

        if (i < pattern.Length && pattern[i] == ',')
        {
            i++;
            while (i < pattern.Length && char.IsAsciiDigit(pattern[i]))
            {
                digits++;
                i++;
            }
        }
        else if (digits == 0)
        {
            return false;
        }

        if (digits == 0 || i >= pattern.Length || pattern[i] != '}')
        {
            return false;
        }

        closeIndex = i;
        possessive = i + 1 < pattern.Length && pattern[i + 1] == '+';
        return true;
    }

    private static bool TranslateEscape(string pattern, ref int pos, StringBuilder output, bool inClass, Dictionary<string, int> groups, int groupCount, out string? error)
    {
        error = null;
        if (pos + 1 >= pattern.Length)
        {
            output.Append('\\');
            pos++;
            return true;
        }

        var c = pattern[pos + 1];
        switch (c)
        {
            case UnsupportedAnchor:
                output.Append("(?!)");
                pos += 2;
                return true;

            case 'z':
                output.Append(PatternRewriter.EndOfLine);
                pos += 2;
                return true;

            case 'x':
                return TranslateHexEscape(pattern, ref pos, output, inClass, out error);

            case 'h':
                output.Append(inClass ? "0-9A-Fa-f" : "[0-9A-Fa-f]");
                pos += 2;
                return true;

            case 'H':
                if (inClass)
                {
                    error = "\\H inside a character class is not supported.";
                    return false;
                }

                output.Append("[^0-9A-Fa-f]");
                pos += 2;
                return true;

            case 'R':
                if (inClass)
                {
                    error = "\\R inside a character class is not supported.";
                    return false;
                }

                output.Append("(?:\\r\\n|[\\n\\v\\f\\r\\u0085\\u2028\\u2029])");
                pos += 2;
                return true;

            case 'X':
                if (inClass)
                {
                    error = "\\X inside a character class is not supported.";
                    return false;
                }

                output.Append("(?:\\P{M}\\p{M}*)");
                pos += 2;
                return true;

            case 'N':
                if (inClass)
                {
                    error = "\\N inside a character class is not supported.";
                    return false;
                }

                output.Append("[^\\n]");
                pos += 2;
                return true;

            case 'O':
                if (inClass)
                {
                    error = "\\O inside a character class is not supported.";
                    return false;
                }

                output.Append("[\\s\\S]");
                pos += 2;
                return true;

            case 'K':
                error = "\\K is not supported.";
                return false;

            case 'g':
                error = "Subexpression calls (\\g<...>) are not supported.";
                return false;

            case 'p' or 'P':
                return TranslateProperty(pattern, ref pos, output, c == 'P', inClass, out error);

            case 'k':
                return TranslateNamedBackReference(pattern, ref pos, output, groups, out error);

            case >= '1' and <= '9':
                var numberStart = pos + 1;
                var numberEnd = numberStart;
                while (numberEnd < pattern.Length && char.IsAsciiDigit(pattern[numberEnd]))
                {
                    numberEnd++;
                }

                if (int.TryParse(pattern.AsSpan(numberStart, numberEnd - numberStart), NumberStyles.None, CultureInfo.InvariantCulture, out var reference) && reference <= groupCount)
                {
                    output.Append('\\').Append(pattern, numberStart, numberEnd - numberStart);
                }
                else
                {
                    // Oniguruma rejects a reference to a group the pattern does not have; the rule then never matches.
                    output.Append("(?!)");
                }

                pos = numberEnd;
                return true;

            default:
                if (IsDotNetEscape(c))
                {
                    output.Append('\\').Append(c);
                }
                else if (inClass)
                {
                    AppendClassLiteral(output, c);
                }
                else
                {
                    AppendLiteral(output, c);
                }

                pos += 2;
                return true;
        }
    }

    private static bool IsDotNetEscape(char c)
        => c is 'a' or 'b' or 'B' or 'd' or 'D' or 'e' or 'f' or 'G' or 'k' or 'n' or 'p' or 'P' or 'r' or 's' or 'S' or 't' or 'v' or 'w' or 'W' or 'x' or 'z' or 'A' or 'Z' or 'u' or 'c' or '0' or '1' or '2' or '3' or '4' or '5' or '6' or '7' or '8' or '9';

    private static void AppendClassLiteral(StringBuilder output, char c)
    {
        if (c is '\\' or ']' or '^' or '-' or '[')
        {
            output.Append('\\');
        }

        output.Append(c);
    }

    private static bool TranslateNamedBackReference(string pattern, ref int pos, StringBuilder output, Dictionary<string, int> groups, out string? error)
    {
        error = null;
        if (pos + 2 >= pattern.Length || pattern[pos + 2] is not ('<' or '\''))
        {
            error = "Unsupported \\k back-reference form.";
            return false;
        }

        var close = pattern[pos + 2] == '<' ? '>' : '\'';
        var end = pattern.IndexOf(close, pos + 3);
        if (end < 0)
        {
            error = "Unterminated named back-reference.";
            return false;
        }

        if (!groups.TryGetValue(pattern.Substring(pos + 3, end - pos - 3), out var index))
        {
            error = "Back-reference to an undefined group name.";
            return false;
        }

        output.Append('\\').Append(index.ToString(CultureInfo.InvariantCulture));
        pos = end + 1;
        return true;
    }

    private static bool TranslateHexEscape(string pattern, ref int pos, StringBuilder output, bool inClass, out string? error)
    {
        error = null;
        var i = pos + 2;
        if (i < pattern.Length && pattern[i] == '{')
        {
            var end = pattern.IndexOf('}', i);
            if (end < 0)
            {
                error = "Unterminated \\x{...} escape.";
                return false;
            }

            // Oniguruma accepts a space-separated list of code points in one escape: a set of characters inside a class,
            // an alternation outside one.
            var codes = new List<int>();
            var codeStart = i + 1;
            while (codeStart < end)
            {
                var codeEnd = pattern.IndexOf(' ', codeStart);
                if (codeEnd < 0 || codeEnd > end)
                {
                    codeEnd = end;
                }

                if (codeEnd > codeStart)
                {
                    if (!int.TryParse(pattern.AsSpan(codeStart, codeEnd - codeStart), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code))
                    {
                        error = "Invalid hexadecimal escape.";
                        return false;
                    }

                    codes.Add(code);
                }

                codeStart = codeEnd + 1;
            }

            if (codes.Count == 0)
            {
                error = "Invalid hexadecimal escape.";
                return false;
            }

            if (codes.Count > 1 && !inClass)
            {
                output.Append("(?:");
                for (var index = 0; index < codes.Count; index++)
                {
                    if (index > 0)
                    {
                        output.Append('|');
                    }

                    AppendCodePoint(output, codes[index], inClass);
                }

                output.Append(')');
            }
            else
            {
                foreach (var code in codes)
                {
                    AppendCodePoint(output, code, inClass);
                }
            }

            pos = end + 1;
            return true;
        }

        var digits = 0;
        while (digits < 8 && i + digits < pattern.Length && char.IsAsciiHexDigit(pattern[i + digits]))
        {
            digits++;
        }

        if (digits >= 2)
        {
            AppendCodePoint(output, int.Parse(pattern.AsSpan(i, digits), NumberStyles.HexNumber, CultureInfo.InvariantCulture), inClass);
            pos = i + digits;
            return true;
        }

        error = "Invalid hexadecimal escape.";
        return false;
    }

    // Inside a character class an astral code point cannot be written as its surrogate pair, so it becomes the whole
    // surrogate range: a superset that keeps the class usable.
    private static void AppendCodePoint(StringBuilder output, int code, bool inClass)
    {
        if (code < 0 || code > 0x10FFFF)
        {
            code = 0xFFFD;
        }

        if (code <= 0xFFFF)
        {
            output.Append("\\u").Append(code.ToString("X4", CultureInfo.InvariantCulture));
            return;
        }

        if (inClass)
        {
            output.Append("\\uD800-\\uDFFF");
            return;
        }

        var value = code - 0x10000;
        output.Append("\\u").Append((0xD800 + (value >> 10)).ToString("X4", CultureInfo.InvariantCulture));
        output.Append("\\u").Append((0xDC00 + (value & 0x3FF)).ToString("X4", CultureInfo.InvariantCulture));
    }

    private static bool TranslateProperty(string pattern, ref int pos, StringBuilder output, bool negated, bool inClass, out string? error)
    {
        error = null;
        var i = pos + 2;
        string? netClass = null;
        var name = string.Empty;
        if (i < pattern.Length && pattern[i] == '{')
        {
            var end = pattern.IndexOf('}', i);
            if (end < 0)
            {
                error = "Unterminated \\p{...} escape.";
                return false;
            }

            name = pattern.Substring(i + 1, end - i - 1);
            pos = end + 1;
        }
        else if (i < pattern.Length)
        {
            name = pattern.Substring(i, 1);
            pos = i + 1;
        }
        else
        {
            error = "Unterminated \\p escape.";
            return false;
        }

        var ranges = FindRanges(name);
        if (ranges is not null)
        {
            if (inClass)
            {
                if (negated)
                {
                    error = "A negated property inside a character class is not supported.";
                    return false;
                }

                output.Append(ranges);
                return true;
            }

            output.Append(negated ? "[^" : "[").Append(ranges).Append(']');
            return true;
        }

        if (TryMapUnicodeCategory(name, out netClass))
        {
            output.Append(negated ? "\\P{" : "\\p{").Append(netClass).Append('}');
            return true;
        }

        error = $"Unknown character property '{name}'.";
        return false;
    }

    private static string? FindRanges(string name)
    {
        var trimmed = name.StartsWith("Is", StringComparison.Ordinal) || name.StartsWith("In", StringComparison.Ordinal) ? name.Substring(2) : name;
        foreach (var property in s_unicodeProperties)
        {
            if (string.Equals(property.Name, trimmed, StringComparison.OrdinalIgnoreCase))
            {
                return property.Ranges;
            }
        }

        return null;
    }

    private static bool TryMapUnicodeCategory(string name, out string netClass)
    {
        netClass = name switch
        {
            "L" or "Letter" => "L",
            "Lu" or "Uppercase_Letter" => "Lu",
            "Ll" or "Lowercase_Letter" => "Ll",
            "Lt" or "Titlecase_Letter" => "Lt",
            "Lm" or "Modifier_Letter" => "Lm",
            "Lo" or "Other_Letter" => "Lo",
            "M" or "Mark" => "M",
            "Mn" or "Nonspacing_Mark" => "Mn",
            "Mc" or "Spacing_Mark" => "Mc",
            "Me" or "Enclosing_Mark" => "Me",
            "N" or "Number" => "N",
            "Nd" or "Decimal_Number" => "Nd",
            "Nl" or "Letter_Number" => "Nl",
            "No" or "Other_Number" => "No",
            "P" or "Punctuation" => "P",
            "Pc" or "Connector_Punctuation" => "Pc",
            "Pd" or "Dash_Punctuation" => "Pd",
            "Ps" or "Open_Punctuation" => "Ps",
            "Pe" or "Close_Punctuation" => "Pe",
            "Pi" or "Initial_Punctuation" => "Pi",
            "Pf" or "Final_Punctuation" => "Pf",
            "Po" or "Other_Punctuation" => "Po",
            "S" or "Symbol" => "S",
            "Sm" or "Math_Symbol" => "Sm",
            "Sc" or "Currency_Symbol" => "Sc",
            "Sk" or "Modifier_Symbol" => "Sk",
            "So" or "Other_Symbol" => "So",
            "Z" or "Separator" => "Z",
            "Zs" or "Space_Separator" => "Zs",
            "Zl" or "Line_Separator" => "Zl",
            "Zp" or "Paragraph_Separator" => "Zp",
            "C" or "Other" => "C",
            "Cc" or "Control" => "Cc",
            "Cf" or "Format" => "Cf",
            "Cn" or "Unassigned" => "Cn",
            "Co" or "Private_Use" => "Co",
            "Cs" or "Surrogate" => "Cs",
            var block when block.StartsWith("Is", StringComparison.Ordinal) => "Is" + block.Substring(2),
            _ => string.Empty,
        };

        return netClass.Length > 0;
    }

    private static bool TranslateClass(string pattern, ref int pos, StringBuilder output, Dictionary<string, int> groups, int groupCount, out string? error)
    {
        error = null;
        var classStart = output.Length;
        var negated = false;
        output.Append('[');
        pos++;
        if (pos < pattern.Length && pattern[pos] == '^')
        {
            output.Append('^');
            negated = true;
            pos++;
        }

        if (pos < pattern.Length && pattern[pos] == ']')
        {
            output.Append("\\]");
            pos++;
        }

        var subtracted = 0;
        while (pos < pattern.Length)
        {
            var c = pattern[pos];
            if (c == ']')
            {
                output.Append(']');
                pos++;
                if (subtracted == 0)
                {
                    return true;
                }

                subtracted--;
                continue;
            }

            if (c == '-' && pos + 1 < pattern.Length && pattern[pos + 1] == '[')
            {
                // [A-[B]] is .NET's class subtraction, and how an Oniguruma intersection with a negated class is written.
                output.Append("-[");
                subtracted++;
                pos += 2;
                continue;
            }

            if (c == '\\')
            {
                if (!TranslateEscape(pattern, ref pos, output, inClass: true, groups, groupCount, out error))
                {
                    return false;
                }

                continue;
            }

            if (c == '&' && pos + 1 < pattern.Length && pattern[pos + 1] == '&')
            {
                error = "Character class intersection (&&) is not supported.";
                return false;
            }

            if (c == '[' && pos + 2 < pattern.Length && pattern[pos + 1] == ':')
            {
                var close = pattern.IndexOf(":]", pos + 2, StringComparison.Ordinal);
                if (close < 0)
                {
                    error = "Unterminated POSIX character class.";
                    return false;
                }

                var name = pattern.Substring(pos + 2, close - pos - 2);
                var negate = name.Length > 0 && name[0] == '^';
                if (negate)
                {
                    error = "A negated POSIX character class is not supported.";
                    return false;
                }

                var ranges = FindPosix(name);
                if (ranges is null)
                {
                    error = $"Unknown POSIX character class '{name}'.";
                    return false;
                }

                output.Append(ranges);
                pos = close + 2;
                continue;
            }

            if (c == '[' && !negated && subtracted == 0 && pos + 1 < pattern.Length && pattern[pos + 1] == '^')
            {
                // [A[^B]] unions a set with the complement of B, which .NET cannot write. The complement is the wider
                // set, so the interpretation that keeps the grammar working is B's complement alone.
                output.Length = classStart;
                if (!TranslateClass(pattern, ref pos, output, groups, groupCount, out error))
                {
                    return false;
                }

                while (pos < pattern.Length && pattern[pos] != ']')
                {
                    if (pattern[pos] == '\\')
                    {
                        pos += 2;
                        continue;
                    }

                    if (pattern[pos] == '[')
                    {
                        SkipClass(pattern, ref pos);
                        continue;
                    }

                    pos++;
                }

                if (pos >= pattern.Length)
                {
                    error = "Unterminated character class.";
                    return false;
                }

                pos++;
                return true;
            }

            if (c == '[')
            {
                error = "A character class nested in another is not supported.";
                return false;
            }

            if (c == ']' || c == '\\')
            {
                output.Append('\\').Append(c);
                pos++;
                continue;
            }

            AppendLiteral(output, c);
            pos++;
        }

        error = "Unterminated character class.";
        return false;
    }

    private static string? FindPosix(string name)
    {
        foreach (var posix in s_posixClasses)
        {
            if (string.Equals(posix.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return posix.Ranges;
            }
        }

        return null;
    }

    // A literal that .NET's parser would read as syntax keeps a backslash.
    private static void AppendLiteral(StringBuilder output, char c)
    {
        if (c is '\\' or '.' or '$' or '^' or '{' or '[' or '(' or '|' or ')' or '*' or '+' or '?')
        {
            output.Append('\\');
        }

        output.Append(c);
    }

    private readonly record struct OnigurumaCharClass(string Name, string Ranges);
}
