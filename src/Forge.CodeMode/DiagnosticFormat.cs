using System.Globalization;
using System.Text;
using Microsoft.CodeAnalysis;

namespace Forge.CodeMode;

internal static class DiagnosticFormat
{
    public static string Errors(string headline, IEnumerable<Diagnostic> diagnostics, int maxCount, int maxChars)
    {
        var text = new StringBuilder(512);
        text.Append(headline);

        var shown = 0;
        var total = 0;
        foreach (var diagnostic in diagnostics)
        {
            total++;
            if (shown >= maxCount)
            {
                continue;
            }

            shown++;
            var span = diagnostic.Location.GetLineSpan().StartLinePosition;
            text.Append("\n  line ")
                .Append((span.Line + 1).ToString(CultureInfo.InvariantCulture))
                .Append(", column ")
                .Append((span.Character + 1).ToString(CultureInfo.InvariantCulture))
                .Append(": ")
                .Append(diagnostic.Id)
                .Append(": ")
                .Append(diagnostic.GetMessage(CultureInfo.InvariantCulture).Trim());
        }

        if (total > shown)
        {
            text.Append("\n  ... ")
                .Append((total - shown).ToString(CultureInfo.InvariantCulture))
                .Append(" more");
        }

        return TextBounds.Truncate(text.ToString(), maxChars, "diagnostics");
    }

    public static string Violations(string headline, IReadOnlyList<ApiViolation> violations, int maxChars)
    {
        var text = new StringBuilder(512);
        text.Append(headline);
        foreach (var violation in violations)
        {
            text.Append("\n  line ")
                .Append(violation.Line.ToString(CultureInfo.InvariantCulture))
                .Append(", column ")
                .Append(violation.Column.ToString(CultureInfo.InvariantCulture))
                .Append(": ")
                .Append(violation.Description);
        }

        return TextBounds.Truncate(text.ToString(), maxChars, "diagnostics");
    }
}
