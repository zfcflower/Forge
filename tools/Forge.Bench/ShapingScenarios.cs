using System.Globalization;
using System.Text;
using Forge.Abstractions;
using Forge.IO.Tools;

namespace Forge.Bench;

/// <summary>
/// The shaped file tools (wave W1): one <c>read</c> call that returns several files under a character budget, one that
/// returns only the matching lines of several files, and one <c>grep</c> whose answer is capped per file and in total.
/// <para>
/// Each row is measured against the shape it replaces — N single-file reads, N whole-file reads, one unbounded grep — and
/// its <see cref="OpResult.Signature"/> is the character count of the answer, which is the number the change is about:
/// the tool's own cost is microseconds either way, and what the model pays is what comes back.
/// </para>
/// </summary>
internal static class ShapingScenarios
{
    /// <summary>How many files one shaped read names, and how large each is (128 lines of 64 bytes).</summary>
    private const int ReadFiles = 20;
    private const int ReadFileBytes = 8 * 1024;

    private const int PatternFiles = 3;

    /// <summary>Files in the grep fixture, matching lines per file, and the line length that makes the total bite.</summary>
    private const int GrepFiles = 20;
    private const int GrepHitsPerFile = 40;

    public static IEnumerable<BenchScenario> Create(Fixtures fixtures)
    {
        yield return ReadPaths(fixtures);
        yield return ReadPattern(fixtures);
        yield return GrepShaped(fixtures);
    }

    /// <summary>One <c>read</c> with <c>paths</c> against the same files read one call at a time.</summary>
    private static BenchScenario ReadPaths(Fixtures fixtures) =>
        new(
            "shaping.read.paths20",
            "read",
            Heavy: false,
            PrepareAsync: _ =>
            {
                var names = new List<string>(ReadFiles);
                for (var index = 0; index < ReadFiles; index++)
                {
                    var relative = $"shaping/read/f{index:D2}.txt";
                    Write(fixtures, relative, Fixtures.MakeText(ReadFileBytes));
                    names.Add(relative);
                }

                var root = fixtures.Workspace.Root;
                var tool = new ReadTool();
                var one = PathsJson(names, maxChars: Budget);

                return Task.FromResult(new BenchCase
                {
                    Run = async ct =>
                    {
                        var result = await tool
                            .InvokeAsync(new ToolCall("call", "read", one), new ToolContext("bench", root), ct)
                            .ConfigureAwait(false);
                        return new OpResult(result.Content.Length, names.Count, result.Content.Length, $"chars={result.Content.Length}");
                    },
                    Baseline = async ct =>
                    {
                        var total = 0;
                        foreach (var name in names)
                        {
                            var argument = SinglePath(name);
                            var result = await tool
                                .InvokeAsync(new ToolCall("call", "read", argument), new ToolContext("bench", root), ct)
                                .ConfigureAwait(false);
                            total += result.Content.Length;
                        }

                        return new OpResult(total, names.Count, total, $"chars={total}");
                    },
                    BaselineName = $"{ReadFiles} read calls",
                    ProcessWideAllocation = true,
                    Check = comparison => comparison.Implementation.Signature <= Budget
                        ? new Verdict("one shaped read stays inside the budget", $"{comparison.Implementation.Signature} chars", true)
                        : new Verdict(
                            "one shaped read stays inside the budget",
                            $"{comparison.Implementation.Signature} chars",
                            false),
                });
            });

    /// <summary>Writes one fixture file, creating its directory: the workspace's FileAt only maps the name.</summary>
    private static void Write(Fixtures fixtures, string relative, string content)
    {
        var path = fixtures.Workspace.FileAt(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    /// <summary>The tool's own answer cap, the same number <c>read</c> enforces.</summary>
    private const int Budget = 32_000;

    /// <summary>Fixture names are generated ASCII with no quote or backslash, so the argument JSON is written by hand:
    /// the bench is AOT-analyzed and reflection-based serialization is an error there.</summary>
    private static string SinglePath(string name) => """{"path":"?"}""".Replace("?", name, StringComparison.Ordinal);

    private static string PathsJson(IReadOnlyList<string> names, int? maxChars = null)
    {
        var text = new StringBuilder(names.Count * 32);
        text.Append("""{"paths":[""");
        for (var index = 0; index < names.Count; index++)
        {
            if (index > 0)
            {
                text.Append(',');
            }

            text.Append('"').Append(names[index]).Append('"');
        }

        text.Append(']');
        if (maxChars is { } budget)
        {
            text.Append(",\"max_chars\":").Append(budget.ToString(CultureInfo.InvariantCulture));
        }

        return text.Append('}').ToString();
    }

    /// <summary>One <c>read</c> with <c>path</c>s and <c>pattern</c> against the same files read whole.</summary>
    private static BenchScenario ReadPattern(Fixtures fixtures) =>
        new(
            "shaping.read.pattern3",
            "read",
            Heavy: false,
            PrepareAsync: _ =>
            {
                var names = new List<string>(PatternFiles);
                for (var index = 0; index < PatternFiles; index++)
                {
                    var relative = $"shaping/pattern/f{index:D2}.txt";
                    Write(fixtures, relative, Fixtures.MakeText(ReadFileBytes));
                    names.Add(relative);
                }

                var root = fixtures.Workspace.Root;
                var tool = new ReadTool();
                // One line out of 128 in every file, the shape of "show me where this appears in each of them".
                var paths = PathsJson(names);
                var shaped = paths[..^1] + ", \"pattern\": \"line 0000040 \", \"context\": 2}";

                return Task.FromResult(new BenchCase
                {
                    Run = async ct =>
                    {
                        var result = await tool
                            .InvokeAsync(new ToolCall("call", "read", shaped), new ToolContext("bench", root), ct)
                            .ConfigureAwait(false);
                        return new OpResult(result.Content.Length, names.Count, result.Content.Length, $"chars={result.Content.Length}");
                    },
                    Baseline = async ct =>
                    {
                        var total = 0;
                        foreach (var name in names)
                        {
                            var argument = SinglePath(name);
                            var result = await tool
                                .InvokeAsync(new ToolCall("call", "read", argument), new ToolContext("bench", root), ct)
                                .ConfigureAwait(false);
                            total += result.Content.Length;
                        }

                        return new OpResult(total, names.Count, total, $"chars={total}");
                    },
                    BaselineName = $"{PatternFiles} whole-file reads",
                    ProcessWideAllocation = true,
                });
            });

    /// <summary>One <c>grep</c> capped per file and in total against the same query uncapped.</summary>
    private static BenchScenario GrepShaped(Fixtures fixtures) =>
        new(
            "shaping.grep.maxPerFile",
            "grep",
            Heavy: false,
            PrepareAsync: _ =>
            {
                for (var index = 0; index < GrepFiles; index++)
                {
                    var lines = new List<string>(GrepHitsPerFile);
                    for (var hit = 0; hit < GrepHitsPerFile; hit++)
                    {
                        lines.Add(string.Create(
                            System.Globalization.CultureInfo.InvariantCulture,
                            $"const int Value{index:D2}_{hit:D3} = Compute(ApprovalRequest, {hit});"));
                    }

                    Write(fixtures, $"shaping/grep/f{index:D2}.cs", string.Join('\n', lines) + "\n");
                }

                var root = fixtures.Workspace.Root;
                var tool = new GrepTool(new ToolOptions { RipgrepPath = null });
                const string plain = """{"pattern":"ApprovalRequest","path":"shaping/grep"}""";
                const string shaped =
                    """{"pattern":"ApprovalRequest","path":"shaping/grep","max_per_file":3,"total_chars":4000}""";

                return Task.FromResult(new BenchCase
                {
                    Run = async ct =>
                    {
                        var result = await tool
                            .InvokeAsync(new ToolCall("call", "grep", shaped), new ToolContext("bench", root), ct)
                            .ConfigureAwait(false);
                        return new OpResult(result.Content.Length, GrepFiles, result.Content.Length, $"chars={result.Content.Length}");
                    },
                    Baseline = async ct =>
                    {
                        var result = await tool
                            .InvokeAsync(new ToolCall("call", "grep", plain), new ToolContext("bench", root), ct)
                            .ConfigureAwait(false);
                        return new OpResult(result.Content.Length, GrepFiles, result.Content.Length, $"chars={result.Content.Length}");
                    },
                    BaselineName = "uncapped grep",
                    ProcessWideAllocation = true,
                    Check = comparison => comparison.Implementation.Signature <= 4_600
                        ? new Verdict("shaped grep stays inside total_chars", $"{comparison.Implementation.Signature} chars", true)
                        : new Verdict("shaped grep stays inside total_chars", $"{comparison.Implementation.Signature} chars", false),
                });
            });
}
