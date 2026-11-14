using System.Text.RegularExpressions;
using Forge.App.Localization;
using Forge.App.Preferences;
using Xunit;

namespace Forge.App.Tests.Localization;

/// <summary>
/// Pins the integrity of the string tables (task design, "防回归"): every registered key carries a Chinese and an
/// English text, the English text holds no CJK characters, both sides use the same placeholders, count-dependent
/// nouns come in <c>.one</c>/<c>.other</c> pairs, key names carry a feature prefix, and every key an
/// <c>.axaml</c> file binds is registered.
/// <para>Keys starting with <c>test.</c> are skipped everywhere below: they are registered by other test classes in
/// this same process (e.g. <c>LocalizationTests</c> proving duplicate registration), not product strings.</para>
/// </summary>
public sealed class StringTableTests
{
    /// <summary>Every feature prefix the product tables use; a key with another first segment fails the test.</summary>
    private static readonly HashSet<string> KnownPrefixes = new(StringComparer.Ordinal)
    {
        "shell", "window", "workbench", "status", "provider", "providers", "diag", "dialog",
        "project", "session", "turn", "subagent", "files", "changes", "todo",
        "review", "spec", "problems", "language", "settings", "format", "terminal", "appearance", "Extensions", "git", "editor", "lifecycle", "notifications", "automations", "modelCatalog", "prompts", "questions", "web", "compaction", "network", "modelsVisibility", "agents", "idleArchive", "plan", "chat", "checks", "palette"
    };

    private static readonly Regex Cjk = new("[\u4E00-\u9FFF\u3000-\u303F\uFF00-\uFFEF]", RegexOptions.Compiled);

    private static readonly Regex Placeholders =
        new("\\{(\\d+)(:[^}]*)?\\}", RegexOptions.Compiled);

    /// <summary>
    /// Any <c>{Binding [key], Source={x:Static loc:Loc.Instance}}</c>. The Source value is itself wrapped in braces, so the
    /// match may not stop at the first <c>}</c> — it runs to the end of the binding. Without that, every binding written
    /// in this codebase's usual form escaped the check entirely.
    /// </summary>
    private static readonly Regex AxamlLocBinding =
        new("\\{Binding\\s+\\[([A-Za-z0-9_.]+)\\][^}]*?loc:Loc\\.Instance", RegexOptions.Compiled);

    private static bool IsTestKey(string key) =>
        key.StartsWith("test.", StringComparison.Ordinal);

    private static IReadOnlyList<string> ProductKeys()
    {
        Loc.EnsureRegistered();
        return Loc.Keys.Where(key => !IsTestKey(key)).ToList();
    }

    private static Dictionary<string, string> Texts(UiLanguage language)
    {
        using var _ = new LanguageScope(language);
        return ProductKeys().ToDictionary(key => key, key => Loc.Instance[key]);
    }

    [Fact]
    public void Every_key_has_a_Chinese_and_an_English_text()
    {
        var chinese = Texts(UiLanguage.Chinese);
        var english = Texts(UiLanguage.English);

        var violations = new List<string>();
        foreach (var key in chinese.Keys)
        {
            if (chinese[key] == "[" + key + "]" || string.IsNullOrWhiteSpace(chinese[key]))
            {
                violations.Add($"{key}: chinese is {(string.IsNullOrWhiteSpace(chinese[key]) ? "blank" : "missing")}");
            }

            if (english[key] == "[" + key + "]" || string.IsNullOrWhiteSpace(english[key]))
            {
                violations.Add($"{key}: english is {(string.IsNullOrWhiteSpace(english[key]) ? "blank" : "missing")}");
            }
        }

        Assert.True(violations.Count == 0, "keys without both texts:\n" + string.Join("\n", violations));
    }

    [Fact]
    public void No_English_text_contains_CJK_characters()
    {
        var english = Texts(UiLanguage.English);

        var violations = english
            .Where(pair => Cjk.IsMatch(pair.Value))
            .Select(pair => $"{pair.Key}: {pair.Value}")
            .ToList();

        Assert.True(violations.Count == 0, "english texts holding CJK characters:\n" + string.Join("\n", violations));
    }

    [Fact]
    public void Both_languages_use_the_same_placeholders()
    {
        var chinese = Texts(UiLanguage.Chinese);
        var english = Texts(UiLanguage.English);

        static string Indexes(string text) =>
            string.Join(",", Placeholders.Matches(text).Select(match => match.Groups[1].Value).Distinct().OrderBy(static x => x));

        var violations = new List<string>();
        foreach (var key in chinese.Keys)
        {
            var zh = Indexes(chinese[key]);
            var en = Indexes(english[key]);
            if (!string.Equals(zh, en, StringComparison.Ordinal))
            {
                violations.Add($"{key}: chinese {{{zh}}} vs english {{{en}}}");
            }
        }

        Assert.True(violations.Count == 0, "placeholder mismatches:\n" + string.Join("\n", violations));
    }

    [Fact]
    public void Plural_keys_come_in_one_other_pairs()
    {
        var keys = new HashSet<string>(ProductKeys(), StringComparer.Ordinal);

        var violations = new List<string>();
        foreach (var key in keys)
        {
            if (key.EndsWith(".one", StringComparison.Ordinal) &&
                !keys.Contains(key[..^".one".Length] + ".other"))
            {
                violations.Add($"{key} has no .other");
            }

            if (key.EndsWith(".other", StringComparison.Ordinal) &&
                !keys.Contains(key[..^".other".Length] + ".one"))
            {
                violations.Add($"{key} has no .one");
            }
        }

        Assert.True(violations.Count == 0, "unpaired plural keys:\n" + string.Join("\n", violations));
    }

    [Fact]
    public void Every_key_carries_a_known_feature_prefix()
    {
        var violations = ProductKeys()
            .Select(key => new { Key = key, Prefix = key.Contains('.') ? key[..key.IndexOf('.')] : key })
            .Where(entry => !KnownPrefixes.Contains(entry.Prefix))
            .Select(entry => $"{entry.Key}: unknown prefix '{entry.Prefix}'")
            .ToList();

        Assert.True(violations.Count == 0, "keys with unknown prefixes:\n" + string.Join("\n", violations));
    }

    [Fact]
    public void Every_axaml_loc_binding_refers_to_a_registered_key()
    {
        var root = FindRepositoryRoot();
        Assert.False(
            root is null,
            "no directory above the test binaries holds Forge.slnx, so the .axaml sources cannot be found; " +
            "the scan refuses to pass silently.");

        var registered = new HashSet<string>(ProductKeys(), StringComparer.Ordinal);
        var violations = new List<string>();
        var checkedFiles = 0;
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root!, "src", "Forge.App"), "*.axaml", SearchOption.AllDirectories))
        {
            if (file.Split(Path.DirectorySeparatorChar).Any(static part =>
                    string.Equals(part, "bin", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(part, "obj", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            checkedFiles++;
            var text = File.ReadAllText(file);
            foreach (Match match in AxamlLocBinding.Matches(text))
            {
                var key = match.Groups[1].Value;
                if (!registered.Contains(key))
                {
                    violations.Add($"{file}: [{key}] is bound but never registered");
                }
            }
        }

        Assert.True(checkedFiles > 0, "no .axaml files were found under src/Forge.App; the scan refuses to pass silently.");
        Assert.True(violations.Count == 0, "unregistered .axaml keys:\n" + string.Join("\n", violations));
    }

    /// <summary>Walks up from the test binaries to the checkout root (the directory holding Forge.slnx).</summary>
    private static string? FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Forge.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return null;
    }
}
