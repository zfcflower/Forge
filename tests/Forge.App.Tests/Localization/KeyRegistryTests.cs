using System.Reflection;
using System.Text.RegularExpressions;
using Forge.App.Localization;
using Forge.App.Preferences;
using Xunit;

namespace Forge.App.Tests.Localization;

/// <summary>
/// Key registry completeness for C# usages (the .axaml side lives in <see cref="StringTableTests"/>): every key
/// constant a table declares, and every key passed to the <see cref="Loc"/>/<see cref="LocText"/> entry points anywhere
/// in <c>src/Forge.App</c> (as a string literal or as a reference to a key constant), must be registered in both
/// languages. Both checks fail when they find nothing to check, so a broken reflection filter or regex cannot pass
/// silently. A key argument that is a variable (not a literal and not a known constant) cannot be checked statically
/// and is skipped; the constants check covers the tables those variables are filled from.
/// </summary>
public sealed class KeyRegistryTests
{
    private const string Argument = @"(""[^""]+""|[A-Za-z_][A-Za-z0-9_.]*)";

    private static readonly Regex KeyShape = new(@"^[a-z][A-Za-z0-9]*(\.[A-Za-z0-9]+)+$", RegexOptions.Compiled);

    private static readonly Regex Indexer = new(@"Loc\.Instance\[\s*" + Argument + @"\s*\]", RegexOptions.Compiled);
    private static readonly Regex Format = new(@"Loc\.Instance\.Format\(\s*" + Argument, RegexOptions.Compiled);
    private static readonly Regex NewLocText = new(@"new\s+LocText\(\s*" + Argument, RegexOptions.Compiled);
    private static readonly Regex Plural = new(@"LocText\.Plural(?:Key)?\(\s*" + Argument, RegexOptions.Compiled);

    private static HashSet<string> Registered()
    {
        Loc.EnsureRegistered();
        return new HashSet<string>(Loc.Keys, StringComparer.Ordinal);
    }

    private static bool HasText(string key, UiLanguage language)
    {
        using var _ = new LanguageScope(language);
        var text = Loc.Instance[key];
        return text != "[" + key + "]" && !string.IsNullOrWhiteSpace(text);
    }

    /// <summary>Every key-shaped string constant in the Forge.App.Localization namespace, as (Type, Field, Value).</summary>
    private static List<(string Type, string Field, string Value)> KeyConstants()
    {
        var constants = new List<(string, string, string)>();
        foreach (var type in typeof(Loc).Assembly.GetTypes().Where(static t => t.Namespace == "Forge.App.Localization"))
        {
            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                if (field.IsLiteral && field.FieldType == typeof(string) && field.GetRawConstantValue() is string value &&
                    KeyShape.IsMatch(value))
                {
                    constants.Add((type.Name, field.Name, value));
                }
            }
        }

        return constants;
    }

    /// <summary>A constant holding a count-dependent base ("x.y"), registered as "x.y.one" and "x.y.other".</summary>
    private static bool IsPluralBase(string field, string value, HashSet<string> registered) =>
        !registered.Contains(value) &&
        field.EndsWith("Base", StringComparison.Ordinal) &&
        registered.Contains(value + ".one") &&
        registered.Contains(value + ".other");

    [Fact]
    public void Every_key_constant_declared_in_the_Localization_tables_is_registered_in_both_languages()
    {
        var constants = KeyConstants();
        Assert.True(constants.Count > 50, $"reflection found only {constants.Count} key constants; the filter is broken.");

        var registered = Registered();
        var violations = new List<string>();
        foreach (var (type, field, value) in constants)
        {
            var owner = type + "." + field;
            var keys = IsPluralBase(field, value, registered) ? new[] { value + ".one", value + ".other" } : new[] { value };
            foreach (var key in keys)
            {
                if (!registered.Contains(key))
                {
                    violations.Add($"{owner} = \"{value}\": \"{key}\" is not registered");
                    continue;
                }

                if (!HasText(key, UiLanguage.Chinese))
                {
                    violations.Add($"{owner}: \"{key}\" has no Chinese text");
                }

                if (!HasText(key, UiLanguage.English))
                {
                    violations.Add($"{owner}: \"{key}\" has no English text");
                }
            }
        }

        Assert.True(violations.Count == 0, "key constants without texts:\n" + string.Join("\n", violations));
    }

    [Fact]
    public void Every_key_used_through_Loc_or_LocText_in_the_sources_is_registered()
    {
        var root = FindSourceRoot();
        Assert.False(root is null, "no directory above the test binaries holds Forge.slnx; the scan refuses to pass silently.");

        var constants = KeyConstants();
        var registered = Registered();
        var usages = 0;
        var violations = new List<string>();
        foreach (var file in Directory.EnumerateFiles(root!, "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root!, file);
            if (relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(static part =>
                    string.Equals(part, "bin", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(part, "obj", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                if (lines[i].TrimStart().StartsWith("//", StringComparison.Ordinal))
                {
                    continue;
                }

                var where = $"{relative}:{i + 1}";
                foreach (var regex in new[] { Indexer, Format, NewLocText })
                {
                    foreach (Match match in regex.Matches(lines[i]))
                    {
                        if (Resolve(match.Groups[1].Value, constants) is { } key)
                        {
                            usages++;
                            Check(key, where);
                        }
                    }
                }

                foreach (Match match in Plural.Matches(lines[i]))
                {
                    if (Resolve(match.Groups[1].Value, constants) is { } keyBase)
                    {
                        usages++;
                        Check(keyBase + ".one", where);
                        Check(keyBase + ".other", where);
                    }
                }
            }
        }

        Assert.True(usages > 0, "the scan resolved no Loc/LocText key usages at all; the regexes or the constant lookup are broken.");
        Assert.True(violations.Count == 0, "unregistered keys:\n" + string.Join("\n", violations));

        void Check(string key, string where)
        {
            if (!registered.Contains(key))
            {
                violations.Add($"{where}: \"{key}\" is not registered");
            }
        }
    }

    [Theory]
    [InlineData("Loc.Instance[\"a.b\"]", "a.b")]
    [InlineData("Loc.Instance.Format(\"a.b\", 1)", "a.b")]
    [InlineData("new LocText(\"a.b\", x)", "a.b")]
    [InlineData("LocText.Plural(\"a.b\", n)", "a.b")]
    [InlineData("new LocText(CoreStrings.SettingsTitle)", "CoreStrings.SettingsTitle")]
    public void The_scan_patterns_recognise_each_entry_point(string code, string expected)
    {
        var hits = new[] { Indexer, Format, NewLocText, Plural }
            .SelectMany(regex => regex.Matches(code).Select(static m => m.Groups[1].Value.Trim('"')))
            .ToList();

        Assert.Equal([expected], hits);
    }

    [Fact]
    public void A_constant_reference_resolves_to_its_key_and_a_variable_does_not()
    {
        var constants = KeyConstants();
        var sample = constants[0];

        Assert.Equal(sample.Value, Resolve(sample.Type + "." + sample.Field, constants));
        Assert.Null(Resolve("someVariable", constants));
        Assert.Equal("x.y", Resolve("\"x.y\"", constants));
    }

    /// <summary>A string literal is its own key; an identifier resolves through the key constants (by qualified or, when unambiguous, simple name); anything else is a variable.</summary>
    private static string? Resolve(string argument, List<(string Type, string Field, string Value)> constants)
    {
        if (argument.StartsWith('"'))
        {
            return argument.Trim('"');
        }

        var parts = argument.Split('.');
        var name = parts[^1];
        var typeName = parts.Length > 1 ? parts[^2] : null;
        var matches = constants
            .Where(c => c.Field == name && (typeName is null || c.Type == typeName))
            .Select(static c => c.Value)
            .Distinct()
            .ToList();
        return matches.Count == 1 ? matches[0] : null;
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
}
