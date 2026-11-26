using System.Globalization;
using System.Text;
using System.Text.Json;
using Forge.Abstractions;

namespace Forge.IO.Tools;

/// <summary>
/// Reads a bounded window of a text file and prints it with 1-based line numbers. A path inside the workspace is resolved
/// exactly as every other tool resolves it; an absolute path outside it is read too, because a document the work needs
/// (a vendor's <c>skills/*/SKILL.md</c> under the package cache, a user-level config) cannot otherwise be reached: the
/// working-directory bound belongs to the tools that write.
/// <para>
/// <c>paths</c>, <c>pattern</c> and <c>max_chars</c> shape one answer instead of buying a second round trip: several files
/// under one character budget, and only the lines that carry the evidence. Reading is not searching — <c>pattern</c> is a
/// literal filter over the files the caller named; <c>grep</c> stays the tool that discovers files.
/// </para>
/// </summary>
public sealed class ReadTool : ToolBase, IBoundedOutputTool
{
    /// <summary>
    /// The most characters of file text one call returns. It equals the loop's spill threshold on purpose: a result this size
    /// stays in the conversation as it is, so a read is never turned into an artifact that needs a second tool call.
    /// </summary>
    internal const int MaxResultChars = 32_000;

    /// <summary>
    /// Files one call may name. Past this the call is refused rather than clamped: the caller asked to see more than the
    /// budget can ever answer, and silently dropping half the list reads as "I saw them all".
    /// </summary>
    private const int MaxPathsPerCall = 64;

    /// <summary>Same cap as <c>grep</c>'s context: a presentation parameter, clipped with a note rather than refused.</summary>
    private const int MaxPatternContextLines = 5;

    private const int MinMaxChars = 1_000;

    private const string Schema =
        """{"type":"object","properties":{"path":{"type":"string","description":"File to read, workspace-relative or absolute. Omit when using `paths`."},"paths":{"type":"array","items":{"type":"string"},"description":"Several files in one call: each gets the same window (`offset`, `limit`, `max_bytes`) and they share `max_chars`. At most 64."},"offset":{"type":"integer","description":"First line to return, 1-based.","minimum":1},"limit":{"type":"integer","description":"How many lines per file.","minimum":1,"maximum":20000},"max_bytes":{"type":"integer","description":"Byte budget per file.","minimum":1,"maximum":1048576},"pattern":{"type":"string","description":"Return only lines containing this literal text (case-sensitive), with `context` lines around each match."},"context":{"type":"integer","description":"Lines around each pattern match; default 0, at most 5."},"max_chars":{"type":"integer","description":"Character budget for the whole answer; default 32000, at most 32000.","minimum":1000,"maximum":32000}},"additionalProperties":false}""";

    private const string Description =
        "Read text files, inside the workspace by a relative path or anywhere by an absolute one, with line numbers. One " +
        "call returns at most about 32,000 characters; a cut result names the lines shown and the offset to continue from. " +
        "Read a large file by range (offset and limit), not whole. The byte budget cuts at a byte offset, so its last line " +
        "can be partial. `paths` reads several files in one call under one character budget: every file keeps its own " +
        "`path: lines A–B` header and the answer ends with how many files were not returned. `pattern` returns only the " +
        "lines containing that literal text, with `context` lines around each match, so a comparison arrives as three " +
        "excerpts instead of three whole files. A missing or binary file is reported in place and the other files still come " +
        "back.";

    private readonly Func<ToolOptions> options;

    public ReadTool()
        : this(static () => ToolOptions.Default)
    {
    }

    public ReadTool(Func<ToolOptions> options)
        : base("read", Description, Schema, ToolSideEffect.ReadOnly)
    {
        ArgumentNullException.ThrowIfNull(options);
        this.options = options;
    }

    protected override async ValueTask<ToolResult> InvokeCoreAsync(
        JsonElement arguments,
        ToolContext context,
        CancellationToken ct)
    {
        var single = OptionalText(arguments, "path");
        var many = ToolJson.OptionalStrings(arguments, "paths");
        if (single is null && many is null)
        {
            throw new ToolArgumentException("'path' is required, or 'paths' for several files in one call.");
        }

        if (single is not null && many is not null)
        {
            throw new ToolArgumentException("pass either 'path' or 'paths', not both.");
        }

        if (many is { Count: > MaxPathsPerCall } tooMany)
        {
            throw new ToolArgumentException(
                $"'paths' names {tooMany.Count} files; the limit is {MaxPathsPerCall}. Read them in groups.");
        }

        var pattern = OptionalText(arguments, "pattern");
        var (contextLines, contextNote) = ContextLines(arguments);
        var maxChars = ToolJson.OptionalInt(arguments, "max_chars", MaxResultChars, MinMaxChars, MaxResultChars);
        var current = options();
        var window = new ReadWindow(
            ToolJson.OptionalInt(arguments, "offset", ToolOptions.DefaultReadLineOffset, 1, int.MaxValue),
            ToolJson.OptionalInt(
                arguments,
                "limit",
                Math.Clamp(current.ReadMaxLines, 1, ToolOptions.MaxReadMaxLines),
                1,
                ToolOptions.MaxReadMaxLines),
            ToolJson.OptionalInt(
                arguments,
                "max_bytes",
                Math.Clamp(current.ReadMaxBytes, 1, ToolOptions.MaxReadMaxBytes),
                1,
                ToolOptions.MaxReadMaxBytes));

        // No shaping parameter: the answer is the one this tool always gave, byte for byte. This is the hot path of every
        // session, and a format change here would cost the prompt cache and every existing test for nothing.
        if (many is null && pattern is null)
        {
            return await ReadOneAsync(single!, window, maxChars, context, ct).ConfigureAwait(false);
        }

        return await ReadShapedAsync(
                many ?? [single!],
                window,
                pattern,
                pattern is null ? 0 : contextLines,
                pattern is null ? null : contextNote,
                maxChars,
                context,
                ct)
            .ConfigureAwait(false);
    }

    /// <summary>The window every file of one call is read with.</summary>
    private readonly record struct ReadWindow(int FirstLine, int MaxLines, int MaxBytes);

    /// <summary>A parameter that takes a string, where absent and blank both mean "not given".</summary>
    private static string? OptionalText(JsonElement arguments, string name) =>
        ToolJson.OptionalString(arguments, name) is { } value && !string.IsNullOrWhiteSpace(value) ? value : null;

    /// <summary>
    /// Context lines, and the line that says a larger request was clipped: clipping is the choice <c>grep</c> already makes,
    /// because a presentation parameter must never turn a call into a refusal.
    /// </summary>
    private static (int Lines, string? Note) ContextLines(JsonElement arguments)
    {
        var requested = ToolJson.OptionalInt(arguments, "context", 0, int.MinValue, int.MaxValue);
        return (
            Math.Clamp(requested, 0, MaxPatternContextLines),
            requested > MaxPatternContextLines
                ? $"context {requested} requested, capped at {MaxPatternContextLines}"
                : null);
    }

    /// <summary>One file, unshaped: exactly the answer this tool has always given.</summary>
    private async ValueTask<ToolResult> ReadOneAsync(
        string requested,
        ReadWindow window,
        int maxChars,
        ToolContext context,
        CancellationToken ct)
    {
        var root = Root(context);
        var path = WorkspacePath.ResolveReadable(root, requested);
        var display = FileOps.Display(root, path);
        if (Directory.Exists(path))
        {
            return new ToolResult($"{Definition.Name}: '{display}' is a directory; use `ls` to list it.", IsError: true);
        }

        if (!Record(context, path))
        {
            return new ToolResult($"{Definition.Name}: '{display}' does not exist.", IsError: true);
        }

        try
        {
            var file = await TextFileReader
                .ReadWindowAsync(path, window.FirstLine, window.MaxLines, window.MaxBytes, ct)
                .ConfigureAwait(false);
            return new ToolResult(Format(display, file, maxChars));
        }
        catch (NotTextFileException exception)
        {
            return new ToolResult(
                $"{Definition.Name}: '{display}' looks binary or is not UTF-8 text ({exception.ByteLength} bytes).",
                IsError: true);
        }
    }

    /// <summary>
    /// Several files, or one file filtered by <c>pattern</c>. Every file gets a header that says what it covers, a file that
    /// cannot be read says so in place, and the answer stops at <c>max_chars</c> — a cut answer names the line to continue
    /// from and how many files were not returned, so "the rest was not there" and "the rest did not fit" never read the same.
    /// </summary>
    private async ValueTask<ToolResult> ReadShapedAsync(
        IReadOnlyList<string> requested,
        ReadWindow window,
        string? pattern,
        int contextLines,
        string? contextNote,
        int maxChars,
        ToolContext context,
        CancellationToken ct)
    {
        var root = Root(context);
        var text = new StringBuilder(Math.Min(maxChars, 8 * 1024));
        var unreturned = new List<string>();
        var failures = 0;
        var complete = 0;

        for (var index = 0; index < requested.Count; index++)
        {
            var display = Display(root, requested[index], out var path, out var resolution);
            if (resolution is not null)
            {
                if (!AppendLine(text, maxChars, $"{display}: {resolution}"))
                {
                    AddUnreturned(index, requested, unreturned, root);
                    break;
                }

                failures++;
                continue;
            }

            if (Directory.Exists(path))
            {
                if (!AppendLine(text, maxChars, $"{display}: is a directory; use `ls` to list it"))
                {
                    AddUnreturned(index, requested, unreturned, root);
                    break;
                }

                failures++;
                continue;
            }

            if (!Record(context, path))
            {
                if (!AppendLine(text, maxChars, $"{display}: does not exist"))
                {
                    AddUnreturned(index, requested, unreturned, root);
                    break;
                }

                failures++;
                continue;
            }

            TextFileWindow file;
            try
            {
                file = await TextFileReader
                    .ReadWindowAsync(path, window.FirstLine, window.MaxLines, window.MaxBytes, ct)
                    .ConfigureAwait(false);
            }
            catch (NotTextFileException exception)
            {
                if (!AppendLine(
                        text,
                        maxChars,
                        $"{display}: looks binary or is not UTF-8 text ({exception.ByteLength} bytes)"))
                {
                    AddUnreturned(index, requested, unreturned, root);
                    break;
                }

                failures++;
                continue;
            }

            var block = RenderBlock(display, file, pattern, contextLines);
            var written = AppendBlock(text, maxChars, block);
            if (written < block.Entries)
            {
                // Nothing of this file fit (its header is already too long for what is left), so it is one more file the
                // answer never contained; a partly written block is shown and says where it stopped.
                AddUnreturned(written == 0 ? index : index + 1, requested, unreturned, root);
                break;
            }

            complete++;
        }

        if (text.Length == 0)
        {
            return new ToolResult($"{Definition.Name}: nothing could be read.", IsError: true);
        }

        if (contextNote is not null)
        {
            text.Append('\n').Append(contextNote);
        }

        if (unreturned.Count > 0)
        {
            text.Append("\n… (").Append(unreturned.Count.ToString(CultureInfo.InvariantCulture))
                .Append(unreturned.Count == 1 ? " more file not returned: " : " more files not returned: ")
                .Append(Names(unreturned))
                .Append("; raise max_chars, read them in a smaller group, or narrow with pattern)");
        }

        // Every file failing is a failed call, not an answer: an error result is what makes the loop and the model treat it
        // as one instead of reasoning from a list of problems as if they were content.
        return new ToolResult(text.ToString(), IsError: complete == 0 && failures > 0);
    }

    /// <summary>How the answer names a file: the workspace-relative display, or the raw request when it cannot be resolved.</summary>
    private static string Display(string root, string requested, out string path, out string? resolution)
    {
        try
        {
            path = WorkspacePath.ResolveReadable(root, requested);
            resolution = null;
            return FileOps.Display(root, path);
        }
        catch (Exception exception) when (exception is WorkspacePathException or ArgumentException or IOException)
        {
            path = string.Empty;
            resolution = exception.Message;
            return requested;
        }
    }

    /// <summary>The files from <paramref name="index"/> on, named the way the answer would have named them.</summary>
    private static void AddUnreturned(int index, IReadOnlyList<string> requested, List<string> unreturned, string root)
    {
        for (var later = index; later < requested.Count; later++)
        {
            unreturned.Add(Display(root, requested[later], out _, out _));
        }
    }

    /// <summary>At most three names, so a long unreturned list costs a line rather than a paragraph.</summary>
    private static string Names(IReadOnlyList<string> names)
    {
        const int shown = 3;
        return names.Count <= shown ? string.Join(", ", names) : string.Join(", ", names.Take(shown)) + ", …";
    }

    /// <summary>One file's rendered lines: the header, then the numbered lines, before any budget is applied.</summary>
    private sealed record Block(string Header, List<(int Number, string Text)> Lines)
    {
        public int Entries => Lines.Count + 1;
    }

    /// <summary>The lines a file contributes: everything, or the pattern's matches with their context merged.</summary>
    private static Block RenderBlock(string display, in TextFileWindow window, string? pattern, int contextLines)
    {
        var lines = new List<(int Number, string Text)>(window.Lines.Length);
        if (pattern is null)
        {
            for (var index = 0; index < window.Lines.Length; index++)
            {
                lines.Add((window.FirstLine + index, window.Lines[index]));
            }

            return new Block(RawHeader(display, window, window.Lines.Length), lines);
        }

        var matching = 0;
        var selected = new List<int>();
        for (var index = 0; index < window.Lines.Length; index++)
        {
            if (!window.Lines[index].Contains(pattern, StringComparison.Ordinal))
            {
                continue;
            }

            matching++;
            var from = Math.Max(0, index - contextLines);
            var to = Math.Min(window.Lines.Length - 1, index + contextLines);
            for (var line = from; line <= to; line++)
            {
                if (selected.Count == 0 || selected[^1] < line)
                {
                    selected.Add(line);
                }
            }
        }

        var previous = -2;
        foreach (var index in selected)
        {
            if (previous >= 0 && index != previous + 1)
            {
                lines.Add((0, "…"));
            }

            lines.Add((window.FirstLine + index, window.Lines[index]));
            previous = index;
        }

        return new Block(PatternHeader(display, window, pattern, matching), lines);
    }

    /// <summary>
    /// A pattern header that cannot be mistaken for a fact about the whole file: the count is of matching lines, and a
    /// windowed read says which lines it looked at.
    /// </summary>
    private static string PatternHeader(string display, in TextFileWindow window, string pattern, int matching)
    {
        var text = new StringBuilder(display.Length + pattern.Length + 48);
        text.Append(display).Append(": ");
        if (matching == 0)
        {
            text.Append("no line contains '").Append(pattern).Append('\'');
        }
        else
        {
            text.Append(matching.ToString(CultureInfo.InvariantCulture))
                .Append(matching == 1 ? " matching line" : " matching lines");
        }

        if (window.Truncated)
        {
            var last = window.FirstLine + Math.Max(window.Lines.Length, 1) - 1;
            text.Append(" in lines ").Append(window.FirstLine.ToString(CultureInfo.InvariantCulture))
                .Append('–').Append(last.ToString(CultureInfo.InvariantCulture));
        }

        return text.ToString();
    }

    private static string RawHeader(string display, in TextFileWindow window, int shown)
    {
        var header = new StringBuilder(display.Length + 32);
        AppendHeader(header, display, window.FirstLine, shown, window.Truncated);
        return header.ToString().TrimEnd('\n');
    }

    /// <summary>
    /// Appends as much of one file's block as the budget allows and says where it stopped. Returns how many entries were
    /// written, so the caller can tell a complete block from a cut one.
    /// </summary>
    private static int AppendBlock(StringBuilder text, int maxChars, Block block)
    {
        var written = 0;
        if (!AppendLine(text, maxChars, block.Header))
        {
            return 0;
        }

        written = 1;
        var lastLine = 0;
        foreach (var (number, body) in block.Lines)
        {
            var entry = number == 0 ? body : number.ToString(CultureInfo.InvariantCulture) + "→" + body;
            if (!AppendLine(text, maxChars, entry))
            {
                text.Append("\n… (cut at line ").Append(lastLine.ToString(CultureInfo.InvariantCulture))
                    .Append(" by max_chars; continue that file with offset=")
                    .Append((lastLine + 1).ToString(CultureInfo.InvariantCulture)).Append(')');
                return written;
            }

            if (number != 0)
            {
                lastLine = number;
            }

            written++;
        }

        return written;
    }

    /// <summary>Appends one line, or reports that it did not fit.</summary>
    private static bool AppendLine(StringBuilder text, int maxChars, string line)
    {
        if (text.Length + line.Length + 1 > maxChars)
        {
            return false;
        }

        if (text.Length > 0)
        {
            text.Append('\n');
        }

        text.Append(line);
        return true;
    }

    /// <summary>
    /// Stat and record the file before reading it: a change during the read leaves the older state behind, so a later
    /// whole-file write is refused instead of silently replacing whatever arrived in between. Without a ledger (a script, a
    /// plugin bridge) the read pays nothing beyond the existence check it always did.
    /// </summary>
    private static bool Record(ToolContext context, string path)
    {
        var ledger = context.FileVersions;
        if (ledger is null)
        {
            return File.Exists(path);
        }

        if (FileVersionCheck.Stat(path) is not { } version)
        {
            return File.Exists(path);
        }

        ledger.Record(path, version);
        return true;
    }

    private static string Format(string display, in TextFileWindow window, int maxChars)
    {
        var lines = window.Lines;
        var width = lines.Length == 0
            ? 1
            : (window.FirstLine + lines.Length - 1).ToString(CultureInfo.InvariantCulture).Length;

        // Whole lines only, up to the character cap; a first line longer than the cap is cut itself so a call always makes progress.
        var shown = 0;
        var chars = 0;
        var firstCut = false;
        while (shown < lines.Length)
        {
            var cost = lines[shown].Length + width + 3;
            if (shown > 0 && chars + cost > maxChars)
            {
                break;
            }

            chars += cost;
            shown++;
            if (chars > maxChars)
            {
                firstCut = true;
                break;
            }
        }

        var cutByChars = shown < lines.Length || firstCut;
        var text = new StringBuilder(Math.Min(chars, maxChars) + display.Length + 160);
        AppendHeader(text, display, window.FirstLine, shown, window.Truncated || cutByChars);

        Span<char> number = stackalloc char[12];
        for (var index = 0; index < shown; index++)
        {
            (window.FirstLine + index).TryFormat(number, out var digits, provider: CultureInfo.InvariantCulture);
            var line = lines[index];
            var room = maxChars;
            text.Append(' ', width - digits).Append(number[..digits]).Append('→');
            if (firstCut && index == 0 && line.Length > room)
            {
                line = line[..room];
            }

            text.Append(line).Append('\n');
        }

        if (window.Truncated || cutByChars)
        {
            var last = window.FirstLine + Math.Max(shown, 1) - 1;
            text.Append("… (truncated) Shown lines ")
                .Append(window.FirstLine.ToString(CultureInfo.InvariantCulture)).Append('–')
                .Append(last.ToString(CultureInfo.InvariantCulture))
                .Append(". Continue with offset=").Append((last + 1).ToString(CultureInfo.InvariantCulture)).Append('.');
            if (firstCut)
            {
                text.Append(" Line ").Append(window.FirstLine.ToString(CultureInfo.InvariantCulture)).Append(" itself was cut.");
            }
        }

        return text.ToString().TrimEnd('\n');
    }

    /// <summary>
    /// The first line: the file's own line count only when the whole file came back, otherwise the range this answer covers.
    /// "path: 72 lines" over a 1 461-line file reads as a fact about the file, which is how a partial read turns into a wrong
    /// belief about its size — the same failure shape as an empty search that does not say what it searched.
    /// </summary>
    private static void AppendHeader(StringBuilder text, string display, int firstLine, int shown, bool truncated)
    {
        text.Append(display).Append(": ");
        if (shown == 0 && firstLine > 1)
        {
            text.Append("the file ends before line ").Append(firstLine.ToString(CultureInfo.InvariantCulture)).Append('\n');
            return;
        }

        if (firstLine == 1 && !truncated)
        {
            text.Append(shown.ToString(CultureInfo.InvariantCulture)).Append(" lines\n");
            return;
        }

        var last = firstLine + Math.Max(shown, 1) - 1;
        text.Append("lines ").Append(firstLine.ToString(CultureInfo.InvariantCulture)).Append('–')
            .Append(last.ToString(CultureInfo.InvariantCulture)).Append('\n');
    }
}
