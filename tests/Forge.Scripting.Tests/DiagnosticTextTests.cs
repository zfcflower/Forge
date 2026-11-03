using Xunit;

namespace Forge.Scripting.Tests;

public class DiagnosticTextTests
{
    [Fact]
    public void Diagnostic_with_location_formats_line_column_code_and_message()
    {
        var diagnostic = new ScriptDiagnostic(ScriptDiagnosticSeverity.Error, "CS0103", "The name 'X' does not exist", 3, 7);

        Assert.Equal("line 3, column 7: CS0103: The name 'X' does not exist", diagnostic.ToString());
    }

    [Fact]
    public void Diagnostic_without_location_formats_code_and_message()
    {
        var diagnostic = new ScriptDiagnostic(
            ScriptDiagnosticSeverity.Error,
            ScriptDiagnosticCodes.MissingReference,
            "Assembly reference 'X' could not be resolved.",
            0,
            0);

        Assert.Equal("FORGE0001: Assembly reference 'X' could not be resolved.", diagnostic.ToString());
    }

    [Fact]
    public void Format_lists_the_first_entries_and_counts_the_rest()
    {
        var text = DiagnosticText.Format("head", Diagnostics(5), maxCount: 2, maxChars: 10_000);

        Assert.Equal(
            "head\n  line 1, column 1: CS0001: m1\n  line 2, column 1: CS0001: m2\n  ... 3 more",
            text);
    }

    [Fact]
    public void Format_counts_dropped_entries_against_an_explicit_total()
    {
        var text = DiagnosticText.Format("head", Diagnostics(2), maxCount: 2, maxChars: 10_000, totalCount: 9);

        Assert.EndsWith("\n  ... 7 more", text);
    }

    [Fact]
    public void Zero_entry_limit_keeps_only_the_headline_and_the_count()
    {
        var text = DiagnosticText.Format("head", Diagnostics(5), maxCount: 0, maxChars: 10_000);

        Assert.Equal("head\n  ... 5 more", text);
    }

    [Fact]
    public void Text_beyond_the_character_budget_is_cut_and_marked()
    {
        var text = DiagnosticText.Format("head", Diagnostics(5), maxCount: 5, maxChars: 20);

        Assert.StartsWith("head\n  line 1, colum\n[... diagnostics truncated: kept 20 of ", text);
        Assert.EndsWith(" chars]", text);
    }

    [Fact]
    public void Zero_character_budget_yields_empty_text()
    {
        Assert.Equal(string.Empty, DiagnosticText.Format("head", Diagnostics(2), maxCount: 2, maxChars: 0));
    }

    [Fact]
    public void Null_headline_and_null_diagnostics_are_rejected()
    {
        Assert.Throws<ArgumentNullException>(() => DiagnosticText.Format(null!, [], 1, 100));
        Assert.Throws<ArgumentNullException>(() => DiagnosticText.Format("head", null!, 1, 100));
    }

    private static List<ScriptDiagnostic> Diagnostics(int count) =>
        Enumerable.Range(1, count)
            .Select(i => new ScriptDiagnostic(ScriptDiagnosticSeverity.Error, "CS0001", $"m{i}", i, 1))
            .ToList();
}
