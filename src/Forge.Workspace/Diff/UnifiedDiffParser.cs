using System.Globalization;

namespace Forge.Workspace;

/// <summary>One problem met while parsing a unified diff; the affected file is still reported when possible.</summary>
public sealed record DiffParseIssue(string Message, string? Path = null);

/// <summary>Parsed files plus the problems met while parsing them.</summary>
public sealed record DiffParseResult(IReadOnlyList<FileDiff> Files, IReadOnlyList<DiffParseIssue> Issues)
{
    public bool IsClean => Issues.Count == 0;
}

/// <summary>
/// Parses <c>git diff</c> unified output into files, hunks and numbered lines. Handles rename/copy headers,
/// mode-only changes, quoted paths, binary markers, CRLF line endings and "no newline at end of file" markers.
/// Hunk bodies are checked against the counts in their header, and a header path that cannot be split
/// unambiguously is reported instead of guessed.
/// </summary>
public static class UnifiedDiffParser
{
    private const string DiffHeader = "diff --git ";
    private const string NewSidePrefix = " b/";

    public static IReadOnlyList<FileDiff> Parse(string unifiedDiff) => ParseDetailed(unifiedDiff).Files;

    public static IReadOnlyList<FileDiff> Parse(TextReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        return ParseSections(ReadLines(reader), null).ToList();
    }

    public static async Task<IReadOnlyList<FileDiff>> ParseAsync(TextReader reader, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(reader);
        var text = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
        return Parse(text);
    }

    public static DiffParseResult ParseDetailed(string unifiedDiff)
    {
        ArgumentNullException.ThrowIfNull(unifiedDiff);
        var issues = new List<DiffParseIssue>();
        var files = ParseSections(SplitLines(unifiedDiff), issues).ToList();
        return new DiffParseResult(files, issues);
    }

    public static DiffParseResult ParseDetailed(TextReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        var issues = new List<DiffParseIssue>();
        var files = ParseSections(ReadLines(reader), issues).ToList();
        return new DiffParseResult(files, issues);
    }

    public static IEnumerable<string> SplitLines(string text) => TextLines.Split(text);

    private static IEnumerable<string> ReadLines(TextReader reader)
    {
        while (reader.ReadLine() is { } line)
        {
            yield return line;
        }
    }

    private static IEnumerable<FileDiff> ParseSections(IEnumerable<string> lines, List<DiffParseIssue>? issues)
    {
        Section? section = null;
        foreach (var line in lines)
        {
            if (line.StartsWith(DiffHeader, StringComparison.Ordinal))
            {
                if (section is not null)
                {
                    yield return section.Build();
                }

                section = new Section(issues);
                section.ReadHeaderPaths(line);
                continue;
            }

            if (section is null || section.Skip)
            {
                continue;
            }

            if (line.Length == 0)
            {
                section.FinishHunk();
                continue;
            }

            if (section.Hunk is not null)
            {
                if (section.TryReadHunkLine(line))
                {
                    continue;
                }

                section.FinishHunk();
            }

            switch (line[0])
            {
                case '@':
                    section.ReadHunkHeader(line);
                    break;
                case '-':
                    section.ReadMinusLine(line);
                    break;
                case '+':
                    if (line.StartsWith("+++ ", StringComparison.Ordinal))
                    {
                        section.ReadPlusLine(line);
                    }

                    break;
                case '\\':
                    break;
                default:
                    section.ReadMetaLine(line);
                    break;
            }
        }

        if (section is not null)
        {
            yield return section.Build();
        }
    }

    private static string? ParsePath(string value, string? prefix)
    {
        var text = value;
        var tab = text.IndexOf('\t');
        if (tab >= 0)
        {
            text = text[..tab];
        }

        if (text.Length == 0)
        {
            return null;
        }

        text = GitQuoting.Unquote(text);
        if (text == "/dev/null")
        {
            return null;
        }

        if (prefix is not null && text.StartsWith(prefix, StringComparison.Ordinal) && text.Length > prefix.Length)
        {
            text = text[prefix.Length..];
        }

        return text;
    }

    private static bool TryParseRange(string token, out int start, out int count)
    {
        start = 0;
        count = 1;
        if (token.Length < 2 || token[0] is not ('-' or '+'))
        {
            return false;
        }

        var body = token.AsSpan(1);
        var comma = body.IndexOf(',');
        var startSpan = comma < 0 ? body : body[..comma];
        if (!int.TryParse(startSpan, NumberStyles.None, CultureInfo.InvariantCulture, out start))
        {
            return false;
        }

        if (comma >= 0 && !int.TryParse(body[(comma + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out count))
        {
            return false;
        }

        return IsSaneRange(start, count);
    }

    /// <summary>
    /// Ranges must be non-negative and leave room for one more line number, so numbering a hunk can never
    /// overflow a 32-bit line counter.
    /// </summary>
    private static bool IsSaneRange(int start, int count) =>
        start >= 0 && count >= 0 && start <= int.MaxValue - count - 1;

    private sealed class Section(List<DiffParseIssue>? issues)
    {
        public string? OldPath;
        public string? NewPath;
        public string? OldMode;
        public string? NewMode;
        public int? Similarity;
        public bool IsNew;
        public bool IsDeleted;
        public bool IsRename;
        public bool IsCopy;
        public bool IsBinary;
        public bool Skip;
        public HunkBuilder? Hunk;
        public readonly List<DiffHunk> Hunks = [];

        private bool hasExplicitPaths;
        private bool headerPathsAmbiguous;

        private string PathForIssues => NewPath ?? OldPath ?? string.Empty;

        public void ReadHeaderPaths(string line)
        {
            var (oldPath, newPath, ambiguous) = SplitHeaderPaths(line);
            OldPath = oldPath;
            NewPath = newPath;
            headerPathsAmbiguous = ambiguous;
        }

        public void ReadMetaLine(string line)
        {
            if (line.StartsWith("old mode ", StringComparison.Ordinal))
            {
                OldMode = line["old mode ".Length..].Trim();
            }
            else if (line.StartsWith("new mode ", StringComparison.Ordinal))
            {
                NewMode = line["new mode ".Length..].Trim();
            }
            else if (line.StartsWith("new file mode ", StringComparison.Ordinal))
            {
                IsNew = true;
                NewMode = line["new file mode ".Length..].Trim();
            }
            else if (line.StartsWith("deleted file mode ", StringComparison.Ordinal))
            {
                IsDeleted = true;
                OldMode = line["deleted file mode ".Length..].Trim();
            }
            else if (line.StartsWith("similarity index ", StringComparison.Ordinal) || line.StartsWith("dissimilarity index ", StringComparison.Ordinal))
            {
                var percentIndex = line.IndexOf('%');
                var startIndex = percentIndex > 0 ? line.LastIndexOf(' ', percentIndex) + 1 : -1;
                if (startIndex > 0 && percentIndex > startIndex
                    && int.TryParse(line.AsSpan(startIndex, percentIndex - startIndex), NumberStyles.Integer, CultureInfo.InvariantCulture, out var percent))
                {
                    Similarity = percent;
                }
            }
            else if (line.StartsWith("rename from ", StringComparison.Ordinal))
            {
                IsRename = true;
                OldPath = GitQuoting.Unquote(line["rename from ".Length..]);
                hasExplicitPaths = true;
            }
            else if (line.StartsWith("rename to ", StringComparison.Ordinal))
            {
                IsRename = true;
                NewPath = GitQuoting.Unquote(line["rename to ".Length..]);
                hasExplicitPaths = true;
            }
            else if (line.StartsWith("copy from ", StringComparison.Ordinal))
            {
                IsCopy = true;
                OldPath = GitQuoting.Unquote(line["copy from ".Length..]);
                hasExplicitPaths = true;
            }
            else if (line.StartsWith("copy to ", StringComparison.Ordinal))
            {
                IsCopy = true;
                NewPath = GitQuoting.Unquote(line["copy to ".Length..]);
                hasExplicitPaths = true;
            }
            else if (line.StartsWith("Binary files ", StringComparison.Ordinal) || line == "GIT binary patch")
            {
                IsBinary = true;
            }
            else if (line.StartsWith("diff --cc ", StringComparison.Ordinal) || line.StartsWith("diff --combined ", StringComparison.Ordinal))
            {
                Skip = true;
            }
            else if (line.StartsWith("Submodule ", StringComparison.Ordinal))
            {
                IsBinary = true;
            }
        }

        public void ReadMinusLine(string line)
        {
            if (line.StartsWith("--- ", StringComparison.Ordinal))
            {
                if (!hasExplicitPaths)
                {
                    OldPath = ParsePath(line[4..], "a/");
                }

                return;
            }

            ReadMetaLine(line);
        }

        public void ReadPlusLine(string line)
        {
            if (!hasExplicitPaths)
            {
                NewPath = ParsePath(line[4..], "b/");
            }
        }

        public void ReadHunkHeader(string line)
        {
            if (line.StartsWith("@@@", StringComparison.Ordinal))
            {
                Skip = true;
                return;
            }

            var closing = line.IndexOf("@@", 2, StringComparison.Ordinal);
            if (closing < 0)
            {
                issues?.Add(new DiffParseIssue($"Hunk header '{line}' has no closing marker; the hunk was dropped.", PathForIssues));
                return;
            }

            var tokens = line[2..closing].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length < 2
                || !TryParseRange(tokens[0], out var oldStart, out var oldCount)
                || !TryParseRange(tokens[1], out var newStart, out var newCount))
            {
                issues?.Add(new DiffParseIssue($"Hunk header '{line}' has an invalid line range; the hunk was dropped.", PathForIssues));
                return;
            }

            Hunk = new HunkBuilder(oldStart, oldCount, newStart, newCount);
        }

        public void FinishHunk()
        {
            if (Hunk is null)
            {
                return;
            }

            var hunk = Hunk.Build();
            Hunk = null;
            Hunks.Add(hunk);
            if (hunk.IsCorrupt)
            {
                issues?.Add(new DiffParseIssue(
                    $"Hunk '{hunk.Header}' of '{PathForIssues}' does not match its declared line counts; its contents are marked corrupt.",
                    PathForIssues));
            }
        }

        public bool TryReadHunkLine(string line)
        {
            if (Hunk is null)
            {
                return false;
            }

            switch (line[0])
            {
                case ' ':
                    Hunk.AddContext(line[1..]);
                    return true;
                case '+':
                    Hunk.AddAdded(line[1..]);
                    return true;
                case '-':
                    Hunk.AddRemoved(line[1..]);
                    return true;
                case '\\':
                    Hunk.MarkNoNewLineAtEnd();
                    return true;
                default:
                    return false;
            }
        }

        public FileDiff Build()
        {
            FinishHunk();
            if (headerPathsAmbiguous && !hasExplicitPaths && (OldPath is null || NewPath is null))
            {
                issues?.Add(new DiffParseIssue(
                    $"A {DiffHeader.TrimEnd()} header could not be split into two paths unambiguously; the file is reported without a path.",
                    OldPath ?? NewPath));
            }

            var status = IsNew
                ? FileDiffStatus.Added
                : IsDeleted
                    ? FileDiffStatus.Deleted
                    : IsCopy
                        ? FileDiffStatus.Copied
                        : IsRename
                            ? FileDiffStatus.Renamed
                            : FileDiffStatus.Modified;

            var oldPath = status == FileDiffStatus.Added ? null : OldPath;
            var newPath = status == FileDiffStatus.Deleted ? null : NewPath;

            return new FileDiff(oldPath, newPath, status, IsBinary, Hunks.ToArray())
            {
                OldMode = OldMode,
                NewMode = NewMode,
                Similarity = Similarity,
            };
        }

        /// <summary>
        /// Splits the two paths of a <c>diff --git a/&lt;p&gt; b/&lt;p&gt;</c> header. Git does not quote spaces, so
        /// the first <c> b/</c> is not necessarily the separator: for a same-path header the halves are identical,
        /// which resolves a path that itself contains <c> b/</c>. When several different splits are possible the
        /// header is reported as ambiguous instead of guessing; the <c>---</c>/<c>+++</c>, rename and copy lines
        /// then supply the real paths.
        /// </summary>
        private static (string? Old, string? New, bool Ambiguous) SplitHeaderPaths(string line)
        {
            var rest = line.Length > DiffHeader.Length ? line[DiffHeader.Length..] : string.Empty;
            if (rest.Length == 0)
            {
                return (null, null, false);
            }

            if (rest[0] == '"')
            {
                var consumed = 0;
                var first = ReadQuoted(rest, ref consumed);
                while (consumed < rest.Length && rest[consumed] == ' ')
                {
                    consumed++;
                }

                var second = ReadQuoted(rest, ref consumed);
                return (StripPrefix(first, "a/"), StripPrefix(second, "b/"), false);
            }

            if (!rest.StartsWith("a/", StringComparison.Ordinal))
            {
                return (null, null, true);
            }

            string? singleOld = null;
            string? singleNew = null;
            var splits = 0;
            var search = rest.IndexOf(NewSidePrefix, StringComparison.Ordinal);
            while (search > 0)
            {
                var candidateOld = rest["a/".Length..search];
                var candidateNew = rest[(search + NewSidePrefix.Length)..];
                if (candidateOld.Length == candidateNew.Length
                    && string.Equals(candidateOld, candidateNew, StringComparison.Ordinal))
                {
                    return (candidateOld, candidateNew, false);
                }

                splits++;
                singleOld = candidateOld;
                singleNew = candidateNew;
                search = rest.IndexOf(NewSidePrefix, search + 1, StringComparison.Ordinal);
            }

            return splits == 1 ? (singleOld, singleNew, false) : (null, null, true);
        }

        private static string ReadQuoted(string text, ref int consumed)
        {
            var start = consumed;
            var inQuotes = false;
            while (consumed < text.Length)
            {
                var ch = text[consumed];
                if (ch == '\\' && inQuotes)
                {
                    consumed += 2;
                    continue;
                }

                if (ch == '"')
                {
                    inQuotes = !inQuotes;
                    consumed++;
                    if (!inQuotes)
                    {
                        break;
                    }

                    continue;
                }

                if (ch == ' ' && !inQuotes)
                {
                    break;
                }

                consumed++;
            }

            var raw = text[start..Math.Min(consumed, text.Length)];
            return raw.StartsWith('"') ? GitQuoting.Unquote(raw) : raw;
        }

        private static string StripPrefix(string path, string prefix) =>
            path.StartsWith(prefix, StringComparison.Ordinal) && path.Length > prefix.Length ? path[prefix.Length..] : path;
    }

    private sealed class HunkBuilder(int oldStart, int oldCount, int newStart, int newCount)
    {
        private readonly List<DiffLine> lines = [];

        public int OldStart { get; } = oldStart;

        public int OldCount { get; } = oldCount;

        public int NewStart { get; } = newStart;

        public int NewCount { get; } = newCount;

        private int oldLine = oldStart;

        private int newLine = newStart;

        public void AddContext(string text)
        {
            lines.Add(new DiffLine(DiffLineKind.Context, oldLine++, newLine++, text));
        }

        public void AddAdded(string text)
        {
            lines.Add(new DiffLine(DiffLineKind.Added, 0, newLine++, text));
        }

        public void AddRemoved(string text)
        {
            lines.Add(new DiffLine(DiffLineKind.Removed, oldLine++, 0, text));
        }

        public void MarkNoNewLineAtEnd()
        {
            if (lines.Count > 0)
            {
                lines[^1] = lines[^1] with { NoNewLineAtEnd = true };
            }
        }

        public DiffHunk Build()
        {
            var oldLines = 0;
            var newLines = 0;
            foreach (var line in lines)
            {
                if (line.Kind is DiffLineKind.Context or DiffLineKind.Removed)
                {
                    oldLines++;
                }

                if (line.Kind is DiffLineKind.Context or DiffLineKind.Added)
                {
                    newLines++;
                }
            }

            return new DiffHunk(OldStart, OldCount, NewStart, NewCount, lines.ToArray())
            {
                IsCorrupt = oldLines != OldCount || newLines != NewCount,
            };
        }
    }
}
