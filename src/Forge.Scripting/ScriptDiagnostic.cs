using System.Globalization;
using System.Text;

namespace Forge.Scripting;

public enum ScriptDiagnosticSeverity
{
    Info = 0,
    Warning = 1,
    Error = 2,
}

/// <summary>One compiler diagnostic. <see cref="Line"/> and <see cref="Column"/> are 1-based; both are 0 when
/// the diagnostic has no source location (for example an unresolvable assembly reference).</summary>
public sealed record ScriptDiagnostic(
    ScriptDiagnosticSeverity Severity,
    string Code,
    string Message,
    int Line,
    int Column,
    string? Path = null)
{
    public override string ToString() => Line > 0
        ? string.Create(CultureInfo.InvariantCulture, $"line {Line}, column {Column}: {Code}: {Message}")
        : $"{Code}: {Message}";
}

/// <summary>Codes the compiler emits itself, on top of the Roslyn <c>CSxxxx</c> codes.</summary>
public static class ScriptDiagnosticCodes
{
    public const string MissingReference = "FORGE0001";
    public const string UnknownUsing = "FORGE0002";
    public const string InvalidAssemblyName = "FORGE0003";

    public const string SourceHashMismatch = "FORGE0004";
}

/// <summary>Formats diagnostics as bounded text: at most <c>maxCount</c> entries and <c>maxChars</c> characters,
/// with a trailing note when entries or characters were dropped.</summary>
public static class DiagnosticText
{
    public static string Format(
        string headline,
        IReadOnlyList<ScriptDiagnostic> diagnostics,
        int maxCount,
        int maxChars,
        int? totalCount = null)
    {
        ArgumentNullException.ThrowIfNull(headline);
        ArgumentNullException.ThrowIfNull(diagnostics);

        var text = new StringBuilder(256 + (diagnostics.Count * 96));
        text.Append(headline);

        var total = totalCount ?? diagnostics.Count;
        var limit = maxCount <= 0 ? 0 : Math.Min(maxCount, diagnostics.Count);
        for (var i = 0; i < limit; i++)
        {
            var diagnostic = diagnostics[i];
            text.Append('\n').Append("  ").Append(diagnostic);
        }

        if (total > limit)
        {
            text.Append('\n')
                .Append("  ... ")
                .Append((total - limit).ToString(CultureInfo.InvariantCulture))
                .Append(" more");
        }

        return TextBounds.Truncate(text.ToString(), maxChars, "diagnostics");
    }
}
