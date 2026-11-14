using System.Globalization;
using System.Text;

namespace Forge.App.Services;

/// <summary>
/// Resolves the CSS colour functions in a Mermaider SVG into concrete <c>#rrggbb</c> values before Svg.Skia parses it.
/// <para>Svg.Skia (4.9.1) understands the custom properties Mermaider emits (<c>var(--surface)</c>, nested fallbacks, a
/// <c>&lt;style&gt;</c> block) but not CSS Color 5's <c>color-mix()</c>: it paints such a fill black. Mermaider derives most
/// node fills and strokes with exactly that function, so the diamond of a flowchart would come out a black box with the label
/// on top of it. Only the two forms Mermaider emits are supported - <c>color-mix(in srgb, A p%, B)</c> whose operands are a
/// hex colour or <c>var(--x[, fallback])</c> against the palette written into the SVG root - and anything else fails the
/// render instead of producing a wrong picture.</para>
/// </summary>
internal static class MermaidSvgColors
{
    private const string Marker = "color-mix(";
    private const int MaxFallbackDepth = 4;

    /// <summary>
    /// Replaces every <c>color-mix()</c> in <paramref name="svg"/> with the colour it mixes. Returns false with a
    /// human-readable <paramref name="problem"/> when one of them cannot be evaluated, so the caller can show the source and
    /// the error rather than a black diagram.
    /// </summary>
    internal static bool TryResolve(
        string svg,
        MermaidPalette palette,
        out string resolved,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(false)] out string? problem)
    {
        ArgumentNullException.ThrowIfNull(svg);
        if (svg.IndexOf(Marker, StringComparison.Ordinal) < 0)
        {
            resolved = svg;
            problem = null;
            return true;
        }

        var variables = palette.Variables();
        var builder = new StringBuilder(svg.Length + 64);
        var position = 0;
        while (position < svg.Length)
        {
            var open = svg.IndexOf('<', position);
            if (open < 0)
            {
                builder.Append(svg, position, svg.Length - position);
                break;
            }

            builder.Append(svg, position, open - position);
            var close = svg.IndexOf('>', open);
            if (close < 0)
            {
                builder.Append(svg, open, svg.Length - open);
                break;
            }

            // Only a tag's attribute values (and a style element's CSS body below) paint anything; a diagram label that
            // happens to spell "color-mix(" is text and must reach the picture unchanged.
            if (!ResolveSegment(svg, open, close + 1, variables, builder, out problem))
            {
                resolved = svg;
                return false;
            }

            position = close + 1;
            var tag = svg.AsSpan(open, close - open + 1);
            if (tag.StartsWith("<style", StringComparison.OrdinalIgnoreCase) &&
                (tag.Length == 6 || char.IsWhiteSpace(tag[6]) || tag[6] == '>'))
            {
                var end = svg.IndexOf("</style", close, StringComparison.OrdinalIgnoreCase);
                var bodyEnd = end < 0 ? svg.Length : end;
                if (!ResolveSegment(svg, position, bodyEnd, variables, builder, out problem))
                {
                    resolved = svg;
                    return false;
                }

                position = bodyEnd;
            }
        }

        resolved = builder.ToString();
        problem = null;
        return true;
    }

    /// <summary>Resolves every <c>color-mix()</c> that starts and closes inside <c>[start, end)</c>, appending the rest verbatim.</summary>
    private static bool ResolveSegment(
        string svg,
        int start,
        int end,
        IReadOnlyDictionary<string, string> variables,
        StringBuilder builder,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(false)] out string? problem)
    {
        var position = start;
        while (true)
        {
            var at = svg.IndexOf(Marker, position, StringComparison.Ordinal);
            if (at < 0 || at >= end)
            {
                builder.Append(svg, position, end - position);
                problem = null;
                return true;
            }

            builder.Append(svg, position, at - position);
            if (!TryFindClose(svg, at + Marker.Length, out var close) || close >= end)
            {
                problem = "unterminated color-mix()";
                return false;
            }

            var expression = svg.AsSpan(at + Marker.Length, close - at - Marker.Length);
            if (!TryEvaluate(expression, variables, out var hex, out var reason))
            {
                problem = $"color-mix({expression}): {reason}";
                return false;
            }

            builder.Append(hex);
            position = close + 1;
        }
    }

    /// <summary>Evaluates the inside of a <c>color-mix(</c> … <c>)</c>; the two-colour sRGB form is the only one Mermaider emits.</summary>
    internal static bool TryEvaluate(
        ReadOnlySpan<char> expression,
        IReadOnlyDictionary<string, string> variables,
        out string hex,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(false)] out string? problem)
    {
        hex = string.Empty;
        var parts = SplitTopLevel(expression, ',');
        if (parts.Count != 3 || !TrimmedEquals(parts[0], "in srgb"))
        {
            problem = "only color-mix(in srgb, A, B) is supported";
            return false;
        }

        if (!TryResolveOperand(parts[1], variables, out var first, out var firstWeight, out problem) ||
            !TryResolveOperand(parts[2], variables, out var second, out var secondWeight, out problem))
        {
            return false;
        }

        // CSS color-mix: an omitted percentage is 100% minus the other, both omitted are 50/50, and a sum below 100%
        // keeps the ratio but multiplies the result's alpha by that sum.
        double weight1;
        double weight2;
        if (firstWeight is null && secondWeight is null)
        {
            weight1 = 50;
            weight2 = 50;
        }
        else if (firstWeight is { } onlyFirst)
        {
            weight1 = onlyFirst;
            weight2 = secondWeight ?? 100 - onlyFirst;
        }
        else
        {
            weight2 = secondWeight!.Value;
            weight1 = 100 - weight2;
        }

        var total = weight1 + weight2;
        if (!(total > 0))
        {
            problem = "no weight";
            return false;
        }

        var alphaScale = total < 100 ? total / 100 : 1;
        hex = Mix(first, weight1 / total, second, weight2 / total, alphaScale);
        problem = null;
        return true;
    }

    /// <summary>One operand: a hex colour or a <c>var()</c>, optionally followed by a percentage (null when omitted).</summary>
    private static bool TryResolveOperand(
        ReadOnlySpan<char> part,
        IReadOnlyDictionary<string, string> variables,
        out Rgba color,
        out double? weight,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(false)] out string? problem)
    {
        color = default;
        weight = null;

        // A percentage is the last whitespace-separated token, and every space inside var(--a, b) is nested, so only a
        // depth-zero space can separate it.
        var text = part.Trim();
        var percentAt = LastTopLevelSpace(text);
        if (percentAt > 0)
        {
            var tail = text[(percentAt + 1)..].Trim();
            if (tail.EndsWith('%'))
            {
                if (!double.TryParse(tail[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var percentage) ||
                    percentage < 0 || percentage > 100)
                {
                    problem = "bad percentage";
                    return false;
                }

                weight = percentage;
                text = text[..percentAt].Trim();
            }
        }

        return TryResolveColor(text, variables, 0, out color, out problem);
    }

    /// <summary>A colour operand: <c>#rgb</c>, <c>#rrggbb</c>, <c>#rrggbbaa</c>, or a CSS variable with an optional fallback.</summary>
    private static bool TryResolveColor(
        ReadOnlySpan<char> text,
        IReadOnlyDictionary<string, string> variables,
        int depth,
        out Rgba color,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(false)] out string? problem)
    {
        color = default;
        if (text.StartsWith("#", StringComparison.Ordinal))
        {
            return TryParseHex(text, out color, out problem);
        }

        if (!text.StartsWith("var(", StringComparison.Ordinal) || text[^1] != ')')
        {
            problem = "operand is neither a hex colour nor var()";
            return false;
        }

        if (depth >= MaxFallbackDepth)
        {
            problem = "var() fallbacks nest too deep";
            return false;
        }

        var inner = text[4..^1];
        var comma = IndexOfTopLevel(inner, ',');
        var name = (comma < 0 ? inner : inner[..comma]).Trim();
        if (variables.TryGetValue(name.ToString(), out var value) &&
            TryParseHex(value, out color, out problem))
        {
            return true;
        }

        if (comma < 0)
        {
            problem = $"unknown variable {name}";
            return false;
        }

        return TryResolveColor(inner[(comma + 1)..].Trim(), variables, depth + 1, out color, out problem);
    }

    private static bool TryParseHex(ReadOnlySpan<char> text, out Rgba color, [System.Diagnostics.CodeAnalysis.NotNullWhen(false)] out string? problem)
    {
        color = default;
        if (!text.StartsWith("#", StringComparison.Ordinal))
        {
            problem = "not a hex colour";
            return false;
        }

        var digits = text[1..];
        if (digits.Length is not (3 or 6 or 8) || !uint.TryParse(digits, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _))
        {
            problem = "bad hex colour";
            return false;
        }

        if (digits.Length == 3)
        {
            color = new Rgba(Digit(digits[0]) * 17, Digit(digits[1]) * 17, Digit(digits[2]) * 17, 255);
            problem = null;
            return true;
        }

        if (!byte.TryParse(digits[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var r) ||
            !byte.TryParse(digits[2..4], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var g) ||
            !byte.TryParse(digits[4..6], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b))
        {
            problem = "bad hex colour";
            return false;
        }

        var a = (byte)255;
        if (digits.Length == 8 && !byte.TryParse(digits[6..8], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out a))
        {
            problem = "bad hex colour";
            return false;
        }

        color = new Rgba(r, g, b, a);
        problem = null;
        return true;

        static int Digit(char c) => c is >= '0' and <= '9' ? c - '0' : (c | 0x20) - 'a' + 10;
    }

    /// <summary>CSS <c>color-mix</c> in sRGB: premultiplied by alpha, so a translucent input darkens the result, not the weights.</summary>
    private static string Mix(Rgba first, double firstWeight, Rgba second, double secondWeight, double alphaScale)
    {
        var alpha1 = first.A / 255d;
        var alpha2 = second.A / 255d;

        // The channels are divided by the mixed alpha before the sum < 100% scaling: scaling the alpha is what the CSS
        // normalization adds on top of the mix, not part of the weight the premultiplied channels are divided by.
        var mixedAlpha = alpha1 * firstWeight + alpha2 * secondWeight;
        var alpha = mixedAlpha * alphaScale;
        if (mixedAlpha <= 0 || alpha <= 0)
        {
            return "#00000000";
        }

        var r = (first.R * alpha1 * firstWeight + second.R * alpha2 * secondWeight) / mixedAlpha;
        var g = (first.G * alpha1 * firstWeight + second.G * alpha2 * secondWeight) / mixedAlpha;
        var b = (first.B * alpha1 * firstWeight + second.B * alpha2 * secondWeight) / mixedAlpha;
        var a = (int)Math.Round(alpha * 255, MidpointRounding.AwayFromZero);
        return a >= 255
            ? string.Create(CultureInfo.InvariantCulture, $"#{Byte(r):X2}{Byte(g):X2}{Byte(b):X2}")
            : string.Create(CultureInfo.InvariantCulture, $"#{Byte(r):X2}{Byte(g):X2}{Byte(b):X2}{a:X2}");

        static int Byte(double value) => Math.Clamp((int)Math.Round(value, MidpointRounding.AwayFromZero), 0, 255);
    }

    private static List<string> SplitTopLevel(ReadOnlySpan<char> text, char separator)
    {
        var parts = new List<string>(3);
        var depth = 0;
        var from = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '(')
            {
                depth++;
            }
            else if (c == ')')
            {
                depth--;
            }
            else if (c == separator && depth == 0)
            {
                parts.Add(text[from..i].ToString());
                from = i + 1;
            }
        }

        parts.Add(text[from..].ToString());
        return parts;
    }

    private static int IndexOfTopLevel(ReadOnlySpan<char> text, char target)
    {
        var depth = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '(')
            {
                depth++;
            }
            else if (c == ')')
            {
                depth--;
            }
            else if (c == target && depth == 0)
            {
                return i;
            }
        }

        return -1;
    }

    private static int LastTopLevelSpace(ReadOnlySpan<char> text)
    {
        var depth = 0;
        for (var i = text.Length - 1; i >= 0; i--)
        {
            var c = text[i];
            if (c == ')')
            {
                depth++;
            }
            else if (c == '(')
            {
                depth--;
            }
            else if (char.IsWhiteSpace(c) && depth == 0)
            {
                return i;
            }
        }

        return -1;
    }

    private static bool TrimmedEquals(ReadOnlySpan<char> text, string expected) =>
        text.Trim().Equals(expected, StringComparison.OrdinalIgnoreCase);

    private static bool TryFindClose(string text, int from, out int close)
    {
        var depth = 1;
        for (var i = from; i < text.Length; i++)
        {
            if (text[i] == '(')
            {
                depth++;
            }
            else if (text[i] == ')' && --depth == 0)
            {
                close = i;
                return true;
            }
        }

        close = -1;
        return false;
    }

    private readonly record struct Rgba(int R, int G, int B, int A);
}
