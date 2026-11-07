using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Forge.IO;

/// <summary>What the optional ripgrep backend did: either a complete result, or the reason the caller has to search itself.</summary>
public sealed record RipgrepOutcome(bool Succeeded, GrepResult? Result, string? FailureReason)
{
    public static RipgrepOutcome Completed(GrepResult result) => new(true, result, null);

    public static RipgrepOutcome Skipped(string reason) => new(false, null, reason);
}

/// <summary>
/// The optional <c>rg</c> search backend. It is only used when a caller configured an executable that exists; every
/// ignore decision is taken away from ripgrep (<c>--no-ignore-vcs</c>, <c>--no-ignore-parent</c>,
/// <c>--no-ignore-global</c>, <c>--no-ignore-dot</c>, <c>--no-ignore-exclude</c>) and re-applied to each reported path
/// through the same <see cref="GitIgnore"/> the managed engine uses, so both backends return the same hits for the same
/// query. A missing binary, a failed start, exit code 2, a timeout or output that cannot be trusted all return
/// <see cref="RipgrepOutcome.Succeeded"/> <c>false</c> with a reason, and the caller falls back instead of failing.
/// </summary>
public static class RipgrepBackend
{
    /// <summary>Bytes of JSON captured per requested match before the run is distrusted.</summary>
    private const long StdoutBytesPerMatch = 512;

    private const int MinStdoutBytes = 1024 * 1024;

    private const int MaxStdoutBytes = 16 * 1024 * 1024;

    private const int MaxStderrBytes = 64 * 1024;

    private const int MaxReasonLength = 300;

    /// <summary>True when <paramref name="executablePath"/> names an existing file, so ripgrep may be used at all.</summary>
    public static bool IsAvailable(string? executablePath) =>
        !string.IsNullOrWhiteSpace(executablePath) && File.Exists(executablePath);

    public static async Task<RipgrepOutcome> SearchAsync(
        GrepRequest request,
        string executablePath,
        IProcessRunner runner,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(runner);
        if (!IsAvailable(executablePath))
        {
            return RipgrepOutcome.Skipped($"'{executablePath}' is not an existing file");
        }

        var process = new ProcessRequest
        {
            FileName = executablePath,
            Arguments = BuildArguments(request),
            WorkingDirectory = request.Root,
            MaxStdoutBytes = (int)Math.Clamp(request.Limit * StdoutBytesPerMatch, MinStdoutBytes, MaxStdoutBytes),
            MaxStderrBytes = MaxStderrBytes,
        };

        ProcessResult processResult;
        try
        {
            processResult = await runner.ExecuteAsync(process, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException
            or Win32Exception or NotSupportedException or ArgumentException)
        {
            return RipgrepOutcome.Skipped($"ripgrep could not be started: {Shorten(exception.Message)}");
        }

        if (processResult.EndReason == ProcessEndReason.TimedOut)
        {
            return RipgrepOutcome.Skipped("ripgrep did not finish in time");
        }

        if (processResult.ExitCode > 1)
        {
            var detail = FirstLine(processResult.Stderr);
            return RipgrepOutcome.Skipped(
                detail.Length == 0
                    ? $"ripgrep exited with code {processResult.ExitCode}"
                    : $"ripgrep failed: {Shorten(detail)}");
        }

        var result = Parse(request, processResult, new GitIgnore(request.Root), out var failure);
        return result is null ? RipgrepOutcome.Skipped(failure!) : RipgrepOutcome.Completed(result);
    }

    private static IReadOnlyList<string> BuildArguments(GrepRequest request)
    {
        var arguments = new List<string>(24)
        {
            "--json",
            "--line-number",
            "--hidden",
            "--no-ignore-vcs",
            "--no-ignore-parent",
            "--no-ignore-global",
            "--no-ignore-dot",
            "--no-ignore-exclude",
            "--max-filesize",
            request.MaxFileBytes.ToString(CultureInfo.InvariantCulture),
        };

        if (!string.IsNullOrEmpty(request.Glob))
        {
            arguments.Add("--glob");
            arguments.Add(request.Glob);
        }

        arguments.Add(request.CaseSensitive ? "--case-sensitive" : "--ignore-case");
        if (!request.Regex)
        {
            arguments.Add("-F");
        }

        if (request.ContextLines > 0)
        {
            arguments.Add("--context");
            arguments.Add(request.ContextLines.ToString(CultureInfo.InvariantCulture));
        }

        // "--" so a pattern that starts with a dash stays a pattern.
        arguments.Add("--");
        arguments.Add(request.Pattern);
        var start = WorkspacePath.RelativeTo(request.Root, request.StartPath);
        arguments.Add(start.Length == 0 ? "." : start);
        return arguments;
    }

    private static GrepResult? Parse(GrepRequest request, ProcessResult process, GitIgnore filter, out string? failure)
    {
        failure = null;
        var output = new List<GrepLine>();
        var verdicts = new Dictionary<string, bool>(WorkspacePath.Comparer);
        var binaryOffsets = new Dictionary<string, long>(WorkspacePath.Comparer);
        var current = new List<(GrepLine Line, long Offset)>();
        string? currentPath = null;
        int remaining = request.Limit, accepted = 0, reported = 0, flushed = 0, binary = 0;
        bool truncated = false, sawMatch = false;

        foreach (var line in Lines(process.Stdout))
        {
            if (line.IsEmpty)
            {
                continue;
            }

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(line.ToString());
            }
            catch (JsonException)
            {
                // Only a truncated capture can leave a partial line; that is handled by the caller.
                continue;
            }

            using (document)
            {
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object
                    || !root.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var kind = type.GetString();
                if (kind == "summary")
                {
                    reported = ReadSearches(root);
                    continue;
                }

                if (kind == "end")
                {
                    // A file whose first NUL lies past the managed probe window: ripgrep stops reporting it there, and the
                    // managed engine truncates its scan at the same offset, so the two answer the same set.
                    if (root.TryGetProperty("data", out var end)
                        && ReadText(end, "path") is { } endPath
                        && end.TryGetProperty("binary_offset", out var offset)
                        && offset.ValueKind == JsonValueKind.Number
                        && offset.TryGetInt64(out var binaryOffset))
                    {
                        var endRelative = NormalizePath(endPath);
                        binaryOffsets[endRelative] = binaryOffset;
                        if (binaryOffsets.Count > binary)
                        {
                            binary = binaryOffsets.Count;
                        }
                    }

                    continue;
                }

                if (kind is not ("match" or "context")
                    || !root.TryGetProperty("data", out var data)
                    || ReadText(data, "path") is not { } path
                    || !data.TryGetProperty("line_number", out var number) || !number.TryGetInt32(out var lineNumber)
                    || ReadText(data, "lines") is not { } text)
                {
                    continue;
                }

                var relative = NormalizePath(path);
                if (!string.Equals(relative, currentPath, WorkspacePath.Comparison))
                {
                    Flush();
                    currentPath = relative;
                }

                if (kind == "match")
                {
                    sawMatch = true;
                }

                // ripgrep reports the offset of the line, not of the hit, and a hit can sit on the same line as the NUL that
                // ends the scan. Adding the first submatch start puts the comparison where the managed engine cuts, so a
                // hit past the NUL is dropped by both backends even when it shares its line with the NUL.
                var lineOffset = data.TryGetProperty("absolute_offset", out var absolute) && absolute.TryGetInt64(out var bytes)
                    ? bytes
                    : -1;
                if (lineOffset >= 0
                    && kind == "match"
                    && data.TryGetProperty("submatches", out var submatches)
                    && submatches.ValueKind == JsonValueKind.Array
                    && submatches.GetArrayLength() > 0
                    && submatches[0].TryGetProperty("start", out var start)
                    && start.TryGetInt64(out var startOffset))
                {
                    lineOffset += startOffset;
                }

                current.Add((new GrepLine(relative, lineNumber, TrimLineEnding(text), kind == "match"), lineOffset));
            }
        }

        Flush();

        if (process.StdoutTruncated && !truncated)
        {
            failure = "ripgrep's output did not fit in the capture buffer";
            return null;
        }

        if (!sawMatch && process.ExitCode == 0)
        {
            // Exit code 0 promises a match, so a run that produced none is an output problem, not "no matches".
            failure = "ripgrep reported a match but its output could not be read";
            return null;
        }

        return new GrepResult
        {
            Lines = output,
            MatchCount = accepted,
            Truncated = truncated,
            FilesScanned = reported > 0 ? reported : flushed,
            FilesSkippedBinary = binary,
        };

        void Flush()
        {
            if (current.Count == 0)
            {
                currentPath = null;
                return;
            }

            var path = currentPath ?? string.Empty;
            var cut = binaryOffsets.TryGetValue(path, out var binaryOffset) ? binaryOffset : long.MaxValue;
            var found = 0;
            foreach (var (line, offset) in current)
            {
                if (line.IsMatch && offset < cut)
                {
                    found++;
                }
            }

            if (found > 0 && !IsIgnored(path) && !IsUnderVcs(path))
            {
                if (remaining == 0)
                {
                    truncated = true;
                }
                else
                {
                    var take = Math.Min(found, remaining);
                    var taken = 0;
                    var past = false;
                    foreach (var (line, offset) in current)
                    {
                        if (offset >= cut)
                        {
                            continue;
                        }

                        if (line.IsMatch)
                        {
                            if (taken == take)
                            {
                                past = true;
                                truncated = true;
                                continue;
                            }

                            taken++;
                        }
                        else if (past)
                        {
                            continue;
                        }

                        output.Add(line);
                    }

                    accepted += taken;
                    remaining -= taken;
                }
            }

            flushed++;
            current.Clear();
            currentPath = null;
        }

        bool IsIgnored(string path)
        {
            if (verdicts.TryGetValue(path, out var ignored))
            {
                return ignored;
            }

            ignored = filter.IsIgnoredWithAncestors(path, isDirectory: false);
            verdicts[path] = ignored;
            return ignored;
        }
    }

    /// <summary>Counts the file the summary reports as searched.</summary>
    private static int ReadSearches(JsonElement root)
    {
        if (root.TryGetProperty("data", out var data)
            && data.TryGetProperty("stats", out var stats)
            && stats.TryGetProperty("searches", out var searches)
            && searches.TryGetInt32(out var count))
        {
            return count;
        }

        return 0;
    }

    private static string? ReadText(JsonElement owner, string name)
    {
        if (!owner.TryGetProperty(name, out var holder) || holder.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (holder.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
        {
            return text.GetString();
        }

        if (holder.TryGetProperty("bytes", out var bytes) && bytes.ValueKind == JsonValueKind.String
            && bytes.GetString() is { } encoded)
        {
            try
            {
                return Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
            }
            catch (FormatException)
            {
                return null;
            }
        }

        return null;
    }

    private static string NormalizePath(string path)
    {
        var value = path.Replace('\\', '/');
        while (value.StartsWith("./", StringComparison.Ordinal))
        {
            value = value[2..];
        }

        return value;
    }

    /// <summary>Drops the line terminator ripgrep keeps in <c>lines.text</c>; the managed engine strips the same.</summary>
    private static string TrimLineEnding(string text)
    {
        var span = text.AsSpan();
        if (span.Length > 0 && span[^1] == '\n')
        {
            span = span[..^1];
        }

        if (span.Length > 0 && span[^1] == '\r')
        {
            span = span[..^1];
        }

        return span.Length == text.Length ? text : span.ToString();
    }

    private static bool IsUnderVcs(ReadOnlySpan<char> path)
    {
        var remaining = path;
        while (!remaining.IsEmpty)
        {
            var separator = remaining.IndexOf('/');
            var segment = separator < 0 ? remaining : remaining[..separator];
            if (segment.Equals(FileWalker.VcsDirectory.AsSpan(), WorkspacePath.Comparison))
            {
                return true;
            }

            remaining = separator < 0 ? default : remaining[(separator + 1)..];
        }

        return false;
    }

    private static string FirstLine(string text)
    {
        var end = text.IndexOf('\n');
        return (end < 0 ? text : text[..end]).Trim();
    }

    private static string Shorten(string text) =>
        text.Length <= MaxReasonLength ? text : text[..MaxReasonLength] + "…";

    private static IEnumerable<ReadOnlyMemory<char>> Lines(string text)
    {
        var remaining = text.AsMemory();
        while (!remaining.IsEmpty)
        {
            var end = remaining.Span.IndexOf('\n');
            if (end < 0)
            {
                yield return remaining;
                yield break;
            }

            yield return remaining[..end];
            remaining = remaining[(end + 1)..];
        }
    }
}
