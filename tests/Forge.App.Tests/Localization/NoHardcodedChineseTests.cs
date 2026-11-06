using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace Forge.App.Tests.Localization;

/// <summary>
/// Guards the task rule that no user-visible Chinese may bypass <see cref="Forge.App.Localization.Loc"/>: every
/// <c>.cs</c> and <c>.axaml</c> under <c>src/Forge.App</c> (except <c>bin</c>/<c>obj</c> and the
/// <c>Localization/Strings.*.cs</c> tables) is scanned with comments removed, and what remains must hold no CJK
/// character (unified ideographs, CJK punctuation U+3000-U+303F, fullwidth U+FF00-U+FFEF).
/// <para>C# comments are removed by a small lexical scanner (<see cref="StripCSharpComments"/>), not a rough regex,
/// so a <c>//</c> inside a string (a URL) never starts a comment; the scanner itself is pinned by the unit tests at
/// the bottom of this file. There is no whitelist: a violation below is a product defect for the owning agent to fix,
/// and the failure lists every <c>file:line: text</c> so it can be located.</para>
/// </summary>
public sealed class NoHardcodedChineseTests
{
    private static readonly Regex Cjk = new("[\u4E00-\u9FFF\u3000-\u303F\uFF00-\uFFEF]", RegexOptions.Compiled);

    private static readonly Regex XamlComment =
        new("<!--[\\s\\S]*?-->", RegexOptions.Compiled);

    // ---------------------------------------------------------------- product scan

    [Fact]
    public void No_source_file_outside_the_string_tables_holds_CJK_characters()
    {
        var root = FindSourceRoot();
        Assert.False(
            root is null,
            "no directory above the test binaries holds Forge.slnx, so the sources cannot be found; " +
            "the scan refuses to pass silently.");

        var violations = new List<string>();
        foreach (var file in Directory.EnumerateFiles(root!, "*.*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root!, file);
            if (IsSkippedDirectory(relative))
            {
                continue;
            }

            var isCs = file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);
            var isAxaml = file.EndsWith(".axaml", StringComparison.OrdinalIgnoreCase);
            if (!isCs && !isAxaml)
            {
                continue;
            }

            if (isCs && IsStringTable(relative))
            {
                continue;
            }

            var original = File.ReadAllText(file);
            var stripped = isCs ? StripCSharpComments(original) : StripXamlComments(original);
            violations.AddRange(FindViolations(relative, original, stripped));
        }

        Assert.True(
            violations.Count == 0,
            "hardcoded CJK text outside Localization/Strings.*.cs " +
            $"(file:line:text, {violations.Count} places):\n" + string.Join("\n", violations));
    }

    private static IEnumerable<string> FindViolations(string relative, string original, string stripped)
    {
        var originalLines = original.Split('\n');
        var strippedLines = stripped.Split('\n');
        for (var i = 0; i < strippedLines.Length && i < originalLines.Length; i++)
        {
            if (Cjk.IsMatch(strippedLines[i]))
            {
                yield return $"{relative}:{i + 1}: {originalLines[i].Trim().TrimEnd('\r')}";
            }
        }
    }

    private static string? FindSourceRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Forge.slnx")))
            {
                return Path.Combine(directory.FullName, "src", "Forge.App");
            }

            directory = directory.Parent;
        }

        return null;
    }

    private static bool IsSkippedDirectory(string relative) =>
        relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(static part =>
            string.Equals(part, "bin", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(part, "obj", StringComparison.OrdinalIgnoreCase));

    private static bool IsStringTable(string relative)
    {
        var file = Path.GetFileName(relative);
        return file.StartsWith("Strings.", StringComparison.Ordinal) &&
               file.EndsWith(".cs", StringComparison.Ordinal) &&
               relative.Contains("Localization", StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- the scanner

    /// <summary>
    /// Removes C# line and block comments, keeping every line break (so line numbers survive) and leaving string,
    /// verbatim, raw and interpolated-string contents and character literals untouched. A <c>//</c> inside any of
    /// those never starts a comment.
    /// </summary>
    internal static string StripCSharpComments(string source)
    {
        var keep = new StringBuilder(source.Length);
        var index = 0;
        var holeDepth = 0;
        ScanCode(source, ref index, keep, inHole: false, ref holeDepth);
        return keep.ToString();
    }

    internal static string StripXamlComments(string source) =>
        XamlComment.Replace(source, static match => new string('\n', match.Value.Count(static c => c == '\n')));

    /// <summary>Scans code; inside an interpolation hole a lone <c>}</c> ends the hole (the caller resumes the string).</summary>
    private static void ScanCode(string source, ref int index, StringBuilder keep, bool inHole, ref int holeDepth)
    {
        while (index < source.Length)
        {
            var current = source[index];
            if (current == '/' && Peek(source, index + 1) == '/')
            {
                SkipLineComment(source, ref index);
            }
            else if (current == '/' && Peek(source, index + 1) == '*')
            {
                SkipBlockComment(source, ref index, keep);
            }
            else if (current == '\'')
            {
                ScanCharLiteral(source, ref index, keep);
            }
            else if (TryScanString(source, ref index, keep))
            {
            }
            else if (inHole && current == '{')
            {
                // Inside a hole the text is code, so braces nest instead of escaping: {{ is two opens.
                keep.Append(current);
                index++;
                holeDepth++;
            }
            else if (inHole && current == '}')
            {
                keep.Append(current);
                index++;
                if (holeDepth <= 1)
                {
                    return;
                }

                holeDepth--;
            }
            else
            {
                keep.Append(current);
                index++;
            }
        }
    }

    private static bool TryScanString(string source, ref int index, StringBuilder keep)
    {
        var cursor = index;
        while (cursor < source.Length && source[cursor] == '$')
        {
            cursor++;
        }

        var verbatim = cursor < source.Length && source[cursor] == '@';
        if (verbatim)
        {
            cursor++;
        }

        var quotes = 0;
        while (cursor + quotes < source.Length && source[cursor + quotes] == '"')
        {
            quotes++;
        }

        if (quotes == 0)
        {
            return false;
        }

        var interpolated = cursor > index;
        keep.Append(source, index, (cursor - index) + quotes);
        index = cursor + quotes;

        if (quotes == 2)
        {
            // An empty string literal ("", @"" or $""): both quotes are already consumed, so there is
            // no closer to scan for; scanning for one would swallow the next string as its content.
            return true;
        }

        if (quotes >= 3)
        {
            ScanRawString(source, ref index, keep, quotes);
            return true;
        }

        if (!interpolated)
        {
            ScanPlainString(source, ref index, keep, verbatim);
            return true;
        }

        ScanInterpolatedString(source, ref index, keep, verbatim);
        return true;
    }

    private static void ScanPlainString(string source, ref int index, StringBuilder keep, bool verbatim)
    {
        while (index < source.Length)
        {
            var current = source[index];
            if (!verbatim && current == '\\' && index + 1 < source.Length)
            {
                keep.Append(source, index, 2);
                index += 2;
            }
            else if (verbatim && current == '"' && Peek(source, index + 1) == '"')
            {
                keep.Append("\"\"");
                index += 2;
            }
            else
            {
                keep.Append(current);
                index++;
                if (current == '"')
                {
                    return;
                }
            }
        }
    }

    private static void ScanInterpolatedString(string source, ref int index, StringBuilder keep, bool verbatim)
    {
        while (index < source.Length)
        {
            var current = source[index];
            if (!verbatim && current == '\\' && index + 1 < source.Length)
            {
                keep.Append(source, index, 2);
                index += 2;
            }
            else if (verbatim && current == '"' && Peek(source, index + 1) == '"')
            {
                keep.Append("\"\"");
                index += 2;
            }
            else if (current == '"')
            {
                keep.Append(current);
                index++;
                return;
            }
            else if (current == '{')
            {
                if (Peek(source, index + 1) == '{')
                {
                    keep.Append("{{");
                    index += 2;
                }
                else
                {
                    keep.Append(current);
                    index++;
                    var holeDepth = 1;
                    ScanCode(source, ref index, keep, inHole: true, ref holeDepth);
                }
            }
            else
            {
                keep.Append(current);
                index++;
            }
        }
    }

    private static void ScanRawString(string source, ref int index, StringBuilder keep, int delimiter)
    {
        while (index < source.Length)
        {
            if (source[index] != '"')
            {
                keep.Append(source[index]);
                index++;
                continue;
            }

            var run = 0;
            while (index + run < source.Length && source[index + run] == '"')
            {
                run++;
            }

            if (run == delimiter)
            {
                keep.Append(source, index, run);
                index += run;
                return;
            }

            // A shorter run is content; a longer one closes and reopens, so only the delimiter part ends the string.
            if (run > delimiter)
            {
                keep.Append(source, index, delimiter);
                index += delimiter;
                return;
            }

            keep.Append(source, index, run);
            index += run;
        }
    }

    private static void ScanCharLiteral(string source, ref int index, StringBuilder keep)
    {
        keep.Append(source[index++]);
        if (index >= source.Length)
        {
            return;
        }

        if (source[index] == '\\' && index + 1 < source.Length)
        {
            keep.Append(source, index, 2);
            index += 2;
        }
        else
        {
            keep.Append(source[index++]);
        }

        if (index < source.Length && source[index] == '\'')
        {
            keep.Append(source[index++]);
        }
    }

    private static void SkipLineComment(string source, ref int index)
    {
        while (index < source.Length && source[index] != '\n')
        {
            index++;
        }
    }

    private static void SkipBlockComment(string source, ref int index, StringBuilder keep)
    {
        index += 2;
        while (index < source.Length)
        {
            if (source[index] == '*' && Peek(source, index + 1) == '/')
            {
                index += 2;
                return;
            }

            if (source[index] == '\n')
            {
                keep.Append('\n');
            }

            index++;
        }
    }

    private static char Peek(string source, int index) =>
        index < source.Length ? source[index] : '\0';

    // ---------------------------------------------------------------- scanner unit tests

    [Fact]
    public void Scanner_keeps_a_slash_slash_inside_a_string_and_strips_the_real_comment()
    {
        const string source = "var u = \"https://example/x\"; // real comment";

        var stripped = StripCSharpComments(source);

        Assert.Contains("https://example/x", stripped);
        Assert.DoesNotContain("real comment", stripped);
    }

    [Fact]
    public void Scanner_survives_an_escaped_quote_before_a_comment()
    {
        const string source = "var s = \"a\\\"//b\"; // gone";

        var stripped = StripCSharpComments(source);

        Assert.Contains("\"a\\\"//b\"", stripped);
        Assert.DoesNotContain("gone", stripped);
    }

    [Fact]
    public void Scanner_keeps_a_verbatim_string_with_doubled_quotes()
    {
        const string source = "var p = @\"a\"\"//b\"; // gone";

        var stripped = StripCSharpComments(source);

        Assert.Contains("@\"a\"\"//b\"", stripped);
        Assert.DoesNotContain("gone", stripped);
    }

    [Fact]
    public void Scanner_keeps_a_raw_string_holding_comment_markers()
    {
        const string source = "var r = \"\"\"a//b /*c*/\"\"\"; // gone";

        var stripped = StripCSharpComments(source);

        Assert.Contains("\"\"\"a//b /*c*/\"\"\"", stripped);
        Assert.DoesNotContain("gone", stripped);
    }

    [Fact]
    public void Scanner_keeps_interpolation_holes_and_strips_comments_around_them()
    {
        const string source = "var s = $\"a{x}\"; // comment";

        var stripped = StripCSharpComments(source);

        Assert.Contains("$\"a{x}\"", stripped);
        Assert.DoesNotContain("comment", stripped);
    }

    [Fact]
    public void Scanner_keeps_a_slash_slash_inside_an_interpolated_string()
    {
        const string source = "var s = $\"a//b{x}\"; // gone";

        var stripped = StripCSharpComments(source);

        Assert.Contains("$\"a//b{x}\"", stripped);
        Assert.DoesNotContain("gone", stripped);
    }

    [Fact]
    public void Scanner_handles_a_string_nested_in_an_interpolation_hole()
    {
        const string source = "var s = $\"{d[\"k\"]}//c\"; // gone";

        var stripped = StripCSharpComments(source);

        Assert.Contains("d[\"k\"]", stripped);
        Assert.DoesNotContain("gone", stripped);
    }

    [Fact]
    public void Scanner_strips_a_comment_inside_an_interpolation_hole()
    {
        const string source = "var s = $\"{x /* gone */}\";";

        var stripped = StripCSharpComments(source);

        Assert.DoesNotContain("gone", stripped);
        Assert.Contains("{x", stripped);
    }

    [Fact]
    public void Scanner_tracks_nested_braces_inside_a_hole()
    {
        const string source = "var s = $\"{new[]{1,2}}!\"; // gone";

        var stripped = StripCSharpComments(source);

        Assert.Contains("new[]{1,2}", stripped);
        Assert.DoesNotContain("gone", stripped);
    }

    [Fact]
    public void Scanner_ignores_a_quote_character_literal()
    {
        const string source = "var q = '\"'; // gone";

        var stripped = StripCSharpComments(source);

        Assert.Contains("'\"'", stripped);
        Assert.DoesNotContain("gone", stripped);
    }

    [Fact]
    public void Scanner_ignores_a_slash_character_literal()
    {
        const string source = "var s = '/'; // gone";

        var stripped = StripCSharpComments(source);
        var line = stripped.Split('\n')[0];

        Assert.Contains("'/'", line);
        Assert.DoesNotContain("gone", stripped);
    }

    [Fact]
    public void Scanner_removes_doc_and_block_comments_but_keeps_line_numbers()
    {
        const string source = "var a = 1;\n/// <summary>中文说明</summary>\n/* 第一行\n第二行 */\nvar b = 2;";

        var stripped = StripCSharpComments(source);

        Assert.DoesNotContain("中文", stripped);
        Assert.DoesNotContain("第一行", stripped);
        Assert.Equal(5, stripped.Split('\n').Length);
        Assert.Contains("var b = 2;", stripped);
    }

    [Fact]
    public void Scanner_treats_an_empty_string_as_complete()
    {
        const string source = "M(string prefix = \"\")\n// gone\nvar x = 1;";

        var stripped = StripCSharpComments(source);

        Assert.Contains("prefix = \"\"", stripped);
        Assert.Contains("var x = 1;", stripped);
        Assert.DoesNotContain("gone", stripped);
    }

    [Fact]
    public void Scanner_reports_CJK_left_in_code()
    {
        const string source = "var s = \"秒\"; // 秒";

        var stripped = StripCSharpComments(source);
        var violations = new List<string>();
        var strippedLines = stripped.Split('\n');
        for (var i = 0; i < strippedLines.Length; i++)
        {
            if (Cjk.IsMatch(strippedLines[i]))
            {
                violations.Add($"file.cs:{i + 1}: {strippedLines[i].Trim()}");
            }
        }

        Assert.Single(violations);
    }
}
