using System.Text;
using System.Text.Json;
using Forge.Abstractions;

namespace Forge.IO.Tools;

/// <summary>
/// <c>grep</c>: matching lines of the files below a directory (or of one file), as <c>path:line:text</c>, with
/// <c>path-line-text</c> for context lines. The managed engine is the default; when <see cref="ToolOptions.RipgrepPath"/>
/// names an existing executable the call goes through <see cref="RipgrepBackend"/> first and falls back to the managed
/// engine, with the reason, whenever ripgrep cannot be trusted. A query without a hit returns <c>no matches</c> and is
/// not an error.
/// </summary>
public sealed class GrepTool : ToolBase, IBoundedOutputTool
{
    private const int MaxContextLines = ToolOptions.MaxGrepContextLines;

    private const string Schema =
        """{"type":"object","properties":{"pattern":{"type":"string","description":"Text to find, or a .NET regular expression when `regex` is true."},"path":{"type":"string","description":"Directory or file to search, relative to the workspace root or absolute (inside or outside it); default the root."},"glob":{"type":"string","description":"Only search files whose path matches this glob (relative to the workspace root, or to the searched directory when `path` lies outside it): `*` inside one segment, `**` across segments, `?`, `[...]`. A glob without `/` matches a name at any depth."},"regex":{"type":"boolean","description":"Treat `pattern` as a .NET regular expression; default false."},"case_sensitive":{"type":"boolean","description":"Case-sensitive matching; default true."},"context":{"type":"integer","description":"Context lines before and after each match; default 0, at most 5 (a larger value is capped, and the result says so)."},"limit":{"type":"integer","description":"Maximum matching lines; default 1000, at most 10000. Past it the output says how many lines matched in all."},"files_only":{"type":"boolean","description":"Return only the files that matched, one `path: N match(es)` line each, with no line text."},"max_per_file":{"type":"integer","description":"Keep at most this many matching lines per file, so one dense file cannot fill the answer; default unlimited.","minimum":1,"maximum":10000},"total_chars":{"type":"integer","description":"Character budget for the whole answer; default 32000, at most 32000. The budget is shared across files: it is taken from the files with the most lines, not from the last ones.","minimum":1000,"maximum":32000}},"required":["pattern"],"additionalProperties":false}""";

    private readonly Func<ToolOptions> options;
    private readonly IProcessRunner? runner;

    public GrepTool()
        : this(ToolOptions.Default, null)
    {
    }

    public GrepTool(ToolOptions options, IProcessRunner? runner = null)
        : this(Fixed(options), runner)
    {
    }

    public GrepTool(Func<ToolOptions> options, IProcessRunner? runner = null)
        : base(
            "grep",
            "Search files for a literal string or a regular expression, one `path:line:text` line per matching line and "
            + "`path-line-text` for context lines. Applies .gitignore and the build-output directory list to what lies below "
            + "the path it searches, never to that path itself, so `path=obj` still searches obj. A path outside the workspace "
            + "is searched too (reading there needs no approval) and its files are reported by absolute path. Skips binary and oversized "
            + "files and reports how "
            + "many. A query with no hit says how many files it scanned (not an error), and warns when a pattern carrying "
            + "regular-expression syntax was searched literally. Three parameters shape one answer instead of a second "
            + "call: `files_only` lists the matching files with a per-file count, `max_per_file` caps how many lines one "
            + "file may contribute, and `total_chars` is a budget shared across files (taken from the densest files, never "
            + "from the last ones). A shaped answer always says how many matching lines it left out.",
            Schema,
            ToolSideEffect.ReadOnly)
    {
        ArgumentNullException.ThrowIfNull(options);
        this.options = options;
        this.runner = runner;
    }

    protected override async ValueTask<ToolResult> InvokeCoreAsync(
        JsonElement arguments,
        ToolContext context,
        CancellationToken ct)
    {
        var bounds = options();
        var pattern = ToolJson.RequiredString(arguments, "pattern");
        var requested = ToolJson.OptionalPath(arguments, "path");
        var glob = NormalizeGlob(ToolJson.OptionalString(arguments, "glob"));
        var regex = ToolJson.OptionalBool(arguments, "regex", false);
        var caseSensitive = ToolJson.OptionalBool(arguments, "case_sensitive", true);
        var (contextLines, contextNote) = ContextLines(arguments);
        var limit = ToolJson.OptionalInt(
            arguments,
            "limit",
            Math.Clamp(bounds.GrepMaxResults, 1, ToolOptions.MaxGrepMaxResults),
            1,
            ToolOptions.MaxGrepMaxResults);
        var shaping = new Shaping(
            FilesOnly: ToolJson.OptionalBool(arguments, "files_only", false),
            MaxPerFile: ToolJson.OptionalInt(arguments, "max_per_file", 0, 0, ToolOptions.MaxGrepMaxResults),
            TotalChars: ToolJson.OptionalInt(arguments, "total_chars", BoundedOutput.MaxChars, 1_000, BoundedOutput.MaxChars));

        var root = WorkspacePath.ResolveRealPath(Root(context));
        var start = WorkspacePath.ResolveReadable(root, requested);
        if (!Directory.Exists(start) && !File.Exists(start))
        {
            return new ToolResult($"grep: '{requested}' does not exist.", IsError: true);
        }

        // A read is not bounded by the workspace: a start inside it reports workspace-relative paths (as before), an outside
        // start is searched under its own directory and reports absolute paths, which is the only form `read` and `edit` can
        // take back.
        var (anchor, absolute) = FileOps.ReadFrame(root, start);

        var request = new GrepRequest
        {
            Root = anchor,
            StartPath = start,
            Pattern = pattern,
            Glob = glob,
            Regex = regex,
            CaseSensitive = caseSensitive,
            ContextLines = contextLines,
            Limit = limit,
            MaxFileBytes = Math.Max(1, bounds.GrepMaxFileBytes),
            Concurrency = bounds.SearchConcurrency,
            // A path the caller named is searched as it is (only its contents are pruned): both backends build the same
            // filter, so they return the same hits.
            IgnoredDirectories = bounds.IgnoredDirectories,
        };

        GrepResult result;
        string? fallback = null;
        if (runner is not null && RipgrepBackend.IsAvailable(bounds.RipgrepPath))
        {
            var outcome = await RipgrepBackend
                .SearchAsync(request, bounds.RipgrepPath!, runner, ct)
                .ConfigureAwait(false);
            if (outcome is { Succeeded: true, Result: { } fast })
            {
                result = fast;
            }
            else
            {
                fallback = outcome.FailureReason ?? "ripgrep was not used";
                result = await GrepEngine.SearchAsync(request, ct).ConfigureAwait(false);
            }
        }
        else
        {
            result = await GrepEngine.SearchAsync(request, ct).ConfigureAwait(false);
        }

        return new ToolResult(Format(result, request, fallback, contextNote, absolute ? anchor : null, shaping));
    }

    /// <summary>
    /// Context lines to carry around each match, and the line that says a larger request was clipped. Clipping rather than
    /// refusing is the point: this is a presentation parameter — it changes how much surrounding text arrives, never what
    /// matched — so the call still produces something, and saying the value was clipped keeps the answer honest. Refusing it
    /// cost a production run six turns, one per retry.
    /// </summary>
    private static (int Lines, string? Note) ContextLines(JsonElement arguments)
    {
        var requested = ToolJson.OptionalInt(arguments, "context", 0, int.MinValue, int.MaxValue);
        return (
            Math.Clamp(requested, 0, MaxContextLines),
            requested > MaxContextLines ? $"context {requested} requested, capped at {MaxContextLines}" : null);
    }

    /// <summary>
    /// The result: one line per match, and for a query with no hit the facts that make it actionable — how many files were
    /// read, whether anything was searched at all, and whether the pattern could ever have matched as written. The hit path
    /// gains nothing from them, so they are written only when there is no hit.
    /// <para>Matches come out in path order, not in the search's own completion order: the files are read concurrently, so an
    /// answer over a hundred files arrived as a shuffle, and a model that could not see which file held what simply called
    /// grep again. Sorting costs one pass over at most <c>limit</c> lines.</para>
    /// </summary>
    /// <summary>What the caller asked the answer to look like: a file list, a per-file cap, a total budget.</summary>
    private readonly record struct Shaping(bool FilesOnly, int MaxPerFile, int TotalChars);

    private static string Format(
        GrepResult result,
        GrepRequest request,
        string? fallback,
        string? contextNote,
        string? absoluteRoot,
        Shaping shaping)
    {
        var lines = new List<string>(result.Lines.Count + 4);
        var shapingNotes = new List<string>(2);

        if (result.Lines.Count == 0)
        {
            lines.Add("no matches");
            lines.Add(result.FilesScanned == 0
                ? "scanned 0 files: the path is empty or excluded by .gitignore — verify with ls"
                : $"scanned {result.FilesScanned} file(s)");
            if (!request.Regex && LooksLikeRegex(request.Pattern))
            {
                lines.Add("the pattern contains regular-expression syntax but `regex` was false, so it was searched literally; retry with `regex: true`");
            }

            if (request.CaseSensitive && HasUpper(request.Pattern))
            {
                lines.Add("`case_sensitive` is true (pass `case_sensitive: false` to ignore case)");
            }
        }

        else if (shaping.FilesOnly)
        {
            AppendFilesOnly(lines, result, absoluteRoot, shaping, shapingNotes);
        }
        else
        {
            AppendHits(lines, result, absoluteRoot, shaping, shapingNotes);
        }

        lines.AddRange(shapingNotes);

        if (result.Truncated)
        {
            // The count alone said how much was left out, not what to do about it: raising `limit` or narrowing with a glob is
            // the next step, and saying it costs a few characters.
            lines.Add(result.TotalMatches is { } total
                ? $"… (truncated: showing {request.Limit} of {total} matching lines; raise limit or narrow with glob)"
                : $"… (truncated: showing {request.Limit} of at least {request.Limit + 1} matching lines; raise limit or narrow with glob)");
        }

        if (contextNote is not null)
        {
            lines.Add(contextNote);
        }

        if (result.FilesSkippedTooLarge > 0)
        {
            lines.Add($"{result.FilesSkippedTooLarge} file(s) skipped: larger than {request.MaxFileBytes} bytes");
        }

        if (result.FilesSkippedBinary > 0)
        {
            lines.Add($"{result.FilesSkippedBinary} file(s) skipped: binary");
        }

        foreach (var file in result.RegexTimeoutFiles)
        {
            lines.Add($"regex match timeout: {(absoluteRoot is null ? file : Shown(absoluteRoot, file))}");
        }

        if (fallback is not null)
        {
            lines.Add($"ripgrep not used: {fallback}");
        }

        return BoundedOutput.Join(lines, "Narrow with `path`, `glob` or a more specific `pattern`.");
    }

    /// <summary>
    /// The hits as they are, grouped by file so the shaping parameters act on whole files rather than on the flat list: the
    /// answer keeps the path order the engine's output already has, and a file that loses lines is the one with the most.
    /// </summary>
    private static void AppendHits(
        List<string> lines,
        GrepResult result,
        string? absoluteRoot,
        Shaping shaping,
        List<string> notes)
    {
        var groups = new List<(string Path, List<GrepLine> Lines)>();
        foreach (var line in Ordered(result.Lines))
        {
            if (groups.Count == 0 || !string.Equals(groups[^1].Path, line.Path, StringComparison.Ordinal))
            {
                groups.Add((line.Path, []));
            }

            groups[^1].Lines.Add(line);
        }

        var overCap = new int[groups.Count];
        if (shaping.MaxPerFile > 0)
        {
            CapPerFile(groups, shaping.MaxPerFile, overCap);
        }

        var rendered = new List<List<string>>(groups.Count);
        foreach (var group in groups)
        {
            rendered.Add(Render(group.Path, group.Lines, absoluteRoot));
        }

        var overBudget = new int[groups.Count];
        FitBudget(rendered, shaping.TotalChars, overBudget);

        foreach (var file in rendered)
        {
            lines.AddRange(file);
        }

        var capped = overCap.Sum();
        if (capped > 0)
        {
            notes.Add($"… (max_per_file={shaping.MaxPerFile}: {capped} matching line(s) left out)");
        }

        var trimmed = overBudget.Sum();
        if (trimmed > 0)
        {
            notes.Add(
                $"… (total_chars={shaping.TotalChars}: {trimmed} more line(s) left out; raise total_chars or narrow with glob)");
        }
    }

    /// <summary>
    /// Keeps at most <paramref name="cap"/> matching lines per file, dropping the context lines that belong to a dropped
    /// match and keeping the ones that belong to a kept one. Context lines that arrive before their match are held until the
    /// match is accepted, so a dropped match cannot leave an orphaned excerpt behind.
    /// </summary>
    private static void CapPerFile(List<(string Path, List<GrepLine> Lines)> groups, int cap, int[] dropped)
    {
        for (var index = 0; index < groups.Count; index++)
        {
            var source = groups[index].Lines;
            var kept = new List<GrepLine>(source.Count);
            var pending = new List<GrepLine>();
            var matches = 0;
            var skipping = false;
            var accepted = false;
            foreach (var line in source)
            {
                if (line.IsMatch)
                {
                    if (matches >= cap)
                    {
                        skipping = true;
                        pending.Clear();
                        dropped[index]++;
                        continue;
                    }

                    matches++;
                    accepted = true;
                    skipping = false;
                    kept.AddRange(pending);
                    pending.Clear();
                    kept.Add(line);
                }
                else if (skipping)
                {
                    // Context of a match that is not shown: showing it would be an excerpt of something the answer omitted.
                }
                else if (accepted)
                {
                    kept.Add(line);
                }
                else
                {
                    pending.Add(line);
                }
            }

            groups[index] = (groups[index].Path, kept);
        }
    }

    /// <summary>One file's lines as the model reads them: <c>path:line:text</c>, <c>path-line-text</c> for context.</summary>
    private static List<string> Render(string path, List<GrepLine> source, string? absoluteRoot)
    {
        var shown = absoluteRoot is null ? path : Shown(absoluteRoot, path);
        var rendered = new List<string>(source.Count);
        foreach (var line in source)
        {
            var separator = line.IsMatch ? ':' : '-';
            var builder = new StringBuilder(shown.Length + line.Text.Length + 16);
            builder.Append(shown).Append(separator).Append(line.LineNumber).Append(separator).Append(line.Text);
            rendered.Add(builder.ToString());
        }

        return rendered;
    }

    /// <summary>
    /// Trims the answer to <paramref name="budget"/> characters by taking lines from the files that have the most of them,
    /// so a dense file cannot eat the budget on its own and the files that disappear first are the ones with the most lines
    /// rather than the ones that came last. Ties take the later file: path order is what the answer reads in, and the files
    /// the caller named first keep the benefit of the doubt.
    /// </summary>
    private static void FitBudget(List<List<string>> rendered, int budget, int[] dropped)
    {
        var total = 0;
        foreach (var file in rendered)
        {
            foreach (var line in file)
            {
                total += line.Length + 1;
            }
        }

        if (total <= budget)
        {
            return;
        }

        var queue = new PriorityQueue<int, (int Count, int Index)>();
        for (var index = 0; index < rendered.Count; index++)
        {
            if (rendered[index].Count > 0)
            {
                queue.Enqueue(index, (-rendered[index].Count, -index));
            }
        }

        while (total > budget && queue.TryDequeue(out var target, out _))
        {
            var file = rendered[target];
            if (file.Count == 0)
            {
                continue;
            }

            total -= file[^1].Length + 1;
            file.RemoveAt(file.Count - 1);
            dropped[target]++;
            if (file.Count > 0)
            {
                queue.Enqueue(target, (-file.Count, -target));
            }
        }
    }

    /// <summary>
    /// The file list alone: what a caller wants when the next step is "read these", and the answer that costs a few hundred
    /// characters instead of the matching lines' text. It honours <c>total_chars</c> as well, so the list can never grow
    /// past the budget either.
    /// </summary>
    private static void AppendFilesOnly(
        List<string> lines,
        GrepResult result,
        string? absoluteRoot,
        Shaping shaping,
        List<string> notes)
    {
        var order = new List<string>();
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var line in Ordered(result.Lines))
        {
            if (!line.IsMatch)
            {
                continue;
            }

            if (counts.TryGetValue(line.Path, out var count))
            {
                counts[line.Path] = count + 1;
            }
            else
            {
                counts[line.Path] = 1;
                order.Add(line.Path);
            }
        }

        var used = 0;
        var listed = 0;
        while (listed < order.Count)
        {
            var path = order[listed];
            var shown = absoluteRoot is null ? path : Shown(absoluteRoot, path);
            var count = counts[path];
            var entry = $"{shown}: {count} matching line{(count == 1 ? string.Empty : "s")}";
            if (used + entry.Length + 1 > shaping.TotalChars)
            {
                break;
            }

            lines.Add(entry);
            used += entry.Length + 1;
            listed++;
        }

        if (listed < order.Count)
        {
            notes.Add(
                $"… (total_chars={shaping.TotalChars}: {order.Count - listed} more file(s) not listed; raise total_chars or narrow with glob)");
        }
    }

    /// <summary>An outside search's own directory put back in front of the relative path the engine reported.</summary>
    private static string Shown(string root, string relative) =>
        Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>
    /// The matches in path order, each file's lines ascending: one call's answer becomes something that can be read from the
    /// top down rather than a shuffle of concurrently finished files.
    /// </summary>
    private static IReadOnlyList<GrepLine> Ordered(IReadOnlyList<GrepLine> found)
    {
        if (found.Count < 2)
        {
            return found;
        }

        var ordered = new GrepLine[found.Count];
        for (var index = 0; index < found.Count; index++)
        {
            ordered[index] = found[index];
        }

        Array.Sort(ordered, static (left, right) =>
        {
            var byPath = string.Compare(left.Path, right.Path, WorkspacePath.Comparison);
            return byPath != 0 ? byPath : left.LineNumber.CompareTo(right.LineNumber);
        });

        return ordered;
    }

    /// <summary>
    /// Whether the pattern carries an upper-case letter, the only case in which "nothing matched" is worth a note about the
    /// case-sensitivity switch. A lower-case pattern gains nothing from turning it off.
    /// </summary>
    private static bool HasUpper(ReadOnlySpan<char> pattern)
    {
        foreach (var character in pattern)
        {
            if (char.IsUpper(character))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether the pattern carries regular-expression syntax a literal search could never honour: alternation, an escape
    /// that only a regular expression reads (an escaped letter rather than a whole path), an inline option group, or one of
    /// the unambiguous two-character forms. Deliberately conservative — a lone `.`, `*`, `+` or `[` stays literal, because
    /// those are ordinary characters in a file name.
    /// </summary>
    private static bool LooksLikeRegex(ReadOnlySpan<char> pattern)
    {
        if (pattern.IsEmpty)
        {
            return false;
        }

        if (pattern.IndexOf('|') >= 0 || pattern[0] == '^' || pattern[^1] == '$')
        {
            return true;
        }

        if (pattern.Contains("(?".AsSpan(), StringComparison.Ordinal)
            || pattern.Contains(".*".AsSpan(), StringComparison.Ordinal)
            || pattern.Contains("[^".AsSpan(), StringComparison.Ordinal))
        {
            return true;
        }

        for (var index = 0; index < pattern.Length - 1; index++)
        {
            if (pattern[index] == '\\' && pattern[index + 1] is 's' or 'S' or 'd' or 'D' or 'w' or 'W' or 'b' or 'B' or '\\')
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Drops a leading separator: a glob with a separator is already anchored to the workspace root, and both backends
    /// agree only on the form without it.
    /// </summary>
    private static string? NormalizeGlob(string? glob)
    {
        if (string.IsNullOrWhiteSpace(glob))
        {
            return null;
        }

        var value = glob.Trim();
        return value.TrimStart('/') is { Length: > 0 } trimmed ? trimmed : null;
    }

    private static Func<ToolOptions> Fixed(ToolOptions options) => () => options;
}
