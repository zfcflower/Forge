namespace Forge.Workspace;

/// <summary>
/// The pull request creation gate for the <c>## Testing</c> section: it must exist and say something. Code cannot judge
/// whether the claimed testing happened, but it can refuse an empty section and the stock one-liners ("tests pass",
/// "tested", "n/a", "测试通过") that carry no evidence. After the banned phrases are taken out, the section must still hold
/// a minimum amount of text (a command, a count, a manual check); CJK characters count double because they carry more
/// per character. Pure, so it is unit-tested directly.
/// </summary>
public static class PrTestingEvidence
{
    /// <summary>Weight the section must keep once banned phrases are removed (one per letter or digit, two per CJK character).</summary>
    public const int MinimumEvidenceWeight = 12;

    // Whole-word phrases, matched against lower-cased word sequences ("n/a" is rewritten to "na" first).
    private static readonly string[][] BannedWords = BuildBannedWords(
    [
        "all tests are passing", "all tests pass", "all tests passed", "all tests passing", "tests are passing",
        "tests pass", "tests passed", "tests passing", "test passes", "test passed", "build passes", "builds pass",
        "all green", "looks good", "all good", "works as expected", "works fine", "it works", "tested locally",
        "tested manually", "manually tested", "not applicable", "no tests", "tested", "verified", "works", "passes",
        "passed", "passing", "pass", "green", "lgtm", "tbd", "todo", "none", "nothing", "na", "ok", "okay", "fine",
        "yes", "done", "all", "the", "a",
    ]);

    // CJK phrases carry no spaces, so they are removed as substrings before the text is split into words.
    private static readonly string[] BannedCjk =
    [
        "所有测试通过", "全部测试通过", "测试全部通过", "测试通过", "测试均通过", "全部通过", "已通过", "已测试", "已验证", "没有测试",
        "暂无", "不适用", "通过", "无",
    ];

    /// <summary>
    /// Null when <paramref name="body"/> has a usable <c>## Testing</c> section, otherwise the reason it is refused (one
    /// sentence, no gate number).
    /// </summary>
    public static string? Check(string? body)
    {
        var section = PrBodyRenderer.TryGetSection(body, "Testing");
        if (section is null)
        {
            return "the body has no '## Testing' section";
        }

        if (section.Trim().Length == 0)
        {
            return "the '## Testing' section is empty";
        }

        return Weigh(section) >= MinimumEvidenceWeight
            ? null
            : "the '## Testing' section only says that testing happened (for example 'tests pass', 'tested', 'n/a'); it " +
              "does not say what was run or checked and what it showed";
    }

    /// <summary>How much non-boilerplate text <paramref name="text"/> holds; exposed for tests.</summary>
    public static int Weigh(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var lowered = text.ToLowerInvariant().Replace("n/a", "na", StringComparison.Ordinal);
        foreach (var phrase in BannedCjk)
        {
            lowered = lowered.Replace(phrase, " ", StringComparison.Ordinal);
        }

        var words = Split(lowered);
        var weight = 0;
        var index = 0;
        while (index < words.Count)
        {
            var skipped = MatchBanned(words, index);
            if (skipped > 0)
            {
                index += skipped;
                continue;
            }

            weight += WeightOf(words[index]);
            index++;
        }

        return weight;
    }

    private static int MatchBanned(List<string> words, int index)
    {
        foreach (var phrase in BannedWords)
        {
            if (index + phrase.Length > words.Count)
            {
                continue;
            }

            var matches = true;
            for (var i = 0; i < phrase.Length; i++)
            {
                if (!string.Equals(words[index + i], phrase[i], StringComparison.Ordinal))
                {
                    matches = false;
                    break;
                }
            }

            if (matches)
            {
                return phrase.Length;
            }
        }

        return 0;
    }

    private static int WeightOf(string word)
    {
        var weight = 0;
        foreach (var character in word)
        {
            weight += IsCjk(character) ? 2 : 1;
        }

        return weight;
    }

    private static bool IsCjk(char character) => character >= '⺀';

    private static List<string> Split(string text)
    {
        var words = new List<string>();
        var start = -1;
        for (var i = 0; i <= text.Length; i++)
        {
            var inWord = i < text.Length && char.IsLetterOrDigit(text[i]);
            if (inWord && start < 0)
            {
                start = i;
            }
            else if (!inWord && start >= 0)
            {
                words.Add(text[start..i]);
                start = -1;
            }
        }

        return words;
    }

    private static string[][] BuildBannedWords(string[] phrases)
    {
        var result = new string[phrases.Length][];
        for (var i = 0; i < phrases.Length; i++)
        {
            result[i] = phrases[i].Split(' ');
        }

        // Longest phrase first, so "all tests pass" wins over "all".
        Array.Sort(result, static (left, right) => right.Length.CompareTo(left.Length));
        return result;
    }
}
