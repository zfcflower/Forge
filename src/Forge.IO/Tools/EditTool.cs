using System.Text.Json;
using Forge.Abstractions;

namespace Forge.IO.Tools;

/// <summary>
/// Replaces an exact string in a workspace text file. The read, the replacement and the atomic write back run under one
/// <see cref="PathGate"/> lease, so a concurrent write of the same path cannot be lost in between. Matching runs in the LF
/// space read hands out (<see cref="LineEndings"/>). A match count other
/// than one is reported instead of guessed, unless <c>replace_all</c> asks for every occurrence. The call is not subject
/// to the overwrite check - an exact match is its own optimistic check - but it moves the agent's read record forward only
/// when the file still matched that record (see <see cref="IFileVersionLedger"/>).
/// </summary>
public sealed class EditTool : ToolBase
{
    private const string Schema =
        """{"type":"object","properties":{"path":{"type":"string","description":"File to edit, workspace-relative or absolute."},"old_string":{"type":"string","description":"Exact text to replace; must occur once unless replace_all is true."},"new_string":{"type":"string","description":"Replacement text; an empty string deletes the match."},"replace_all":{"type":"boolean","description":"Replace every occurrence instead of requiring a unique one."}},"required":["path","old_string","new_string"],"additionalProperties":false}""";

    private const string Description =
        "Replace an exact string in a workspace text file and write it back atomically. Fails without touching the file " +
        "when old_string is missing or occurs more than once; replace_all replaces every occurrence.";

    private readonly Func<ToolOptions> options;

    public EditTool()
        : this(static () => ToolOptions.Default)
    {
    }

    public EditTool(Func<ToolOptions> options)
        : base("edit", Description, Schema, ToolSideEffect.Write)
    {
        ArgumentNullException.ThrowIfNull(options);
        this.options = options;
    }

    protected override async ValueTask<ToolResult> InvokeCoreAsync(
        JsonElement arguments,
        ToolContext context,
        CancellationToken ct)
    {
        var requested = ToolJson.RequiredString(arguments, "path");
        if (ToolJson.OptionalString(arguments, "old_string") is not { Length: > 0 } oldText)
        {
            throw new ToolArgumentException("'old_string' must be a non-empty string.");
        }

        if (ToolJson.OptionalString(arguments, "new_string") is not { } newString)
        {
            throw new ToolArgumentException("'new_string' is required.");
        }

        // A payload that is nothing but line breaks loses its meaning to the normalization - a lone '\r' asked to strip the
        // CRs, which the LF standard no longer offers - so it is refused instead of silently matching every line break.
        if (IsLineBreaksOnly(oldText))
        {
            return new ToolResult(
                $"{Definition.Name}: old_string is only line breaks and contains '\\r'; text files are written with LF, so it " +
                "would match every line break. Rewrite the file with `write` instead.",
                IsError: true);
        }

        var oldString = LineEndings.Normalize(oldText);

        var replaceAll = ToolJson.OptionalBool(arguments, "replace_all", false);
        var limit = Math.Clamp(options().EditMaxFileBytes, 1, ToolOptions.MaxEditMaxFileBytes);
        var root = Root(context);
        var path = WorkspacePath.Resolve(root, requested);
        var display = Display(root, path);
        if (Directory.Exists(path))
        {
            return new ToolResult($"{Definition.Name}: '{display}' is a directory.", IsError: true);
        }

        if (!File.Exists(path))
        {
            return new ToolResult($"{Definition.Name}: '{display}' does not exist.", IsError: true);
        }

        using (await PathGate.Shared.AcquireAsync(path, ct).ConfigureAwait(false))
        {
            var info = new FileInfo(path);
            var length = info.Length;

            // The state this call writes over, read under the same lease: it is what decides whether the record follows
            // the edit or has to stand (see the record step below).
            var before = new FileVersion(length, info.LastWriteTimeUtc.Ticks);
            if (length > limit)
            {
                return new ToolResult(
                    $"{Definition.Name}: '{display}' is {length} bytes, over the {limit}-byte edit limit; " +
                    "use read and write on parts of it instead.",
                    IsError: true);
            }

            string text;
            try
            {
                text = await TextFileReader.ReadAllTextAsync(path, limit, ct).ConfigureAwait(false);
            }
            catch (NotTextFileException exception)
            {
                return new ToolResult(
                    $"{Definition.Name}: '{display}' looks binary or is not UTF-8 text ({exception.ByteLength} bytes).",
                    IsError: true);
            }

            text = LineEndings.Normalize(text);
            var occurrences = CountOccurrences(text, oldString, out var first, out var second);
            if (occurrences == 0)
            {
                // The most common reason a payload that was right stops matching: somebody else wrote the file between the
                // agent's read and this call. The ledger holds what that read saw, so the difference is already known —
                // saying it turns "your text is wrong" into "read it again", which is the only actionable advice there is.
                var changed = string.Empty;
                if (context.FileVersions is { } versions
                    && versions.TryGet(path, out var read)
                    && read != before)
                {
                    changed = $"\nthe file changed after your read ({read.Length} → {before.Length} bytes); "
                        + "read it again and edit against what is there now.";
                }

                return new ToolResult(
                    $"{Definition.Name}: no match for old_string in '{display}' ({CountLines(text)} lines); nothing changed."
                    + changed
                    + Diagnose(text, oldString),
                    IsError: true);
            }

            if (occurrences > 1 && !replaceAll)
            {
                var lines = $"{LineOf(text, first)}, {LineOf(text, second)}";
                return new ToolResult(
                    $"{Definition.Name}: old_string occurs {occurrences} times in '{display}' (first at lines {lines}); " +
                    "make it unique or pass replace_all=true.",
                    IsError: true);
            }

            var line = LineOf(text, first);
            var replaced = replaceAll
                ? text.Replace(oldString, newString, StringComparison.Ordinal)
                : string.Concat(text.AsSpan(0, first), newString, text.AsSpan(first + oldString.Length));
            await TextFileWriter.WriteAsync(path, replaced, ct).ConfigureAwait(false);

            // Only a write over the state the agent itself had read adopts the new state. Editing over somebody else's
            // change leaves the old record in place: the agent's context still holds the text before that change, and a
            // later whole-file write with it has to be refused rather than allowed through a laundered record.
            if (context.FileVersions is { } ledger && FileVersionCheck.Matches(ledger, path, before))
            {
                FileVersionCheck.Record(ledger, path);
            }

            var replacedCount = replaceAll ? occurrences : 1;
            var unit = replacedCount == 1 ? "occurrence" : "occurrences";
            return new ToolResult($"replaced {replacedCount} {unit} in {display} (line {line})");
        }
    }

    /// <summary>
    /// Why a literal match missed, in at most two lines: the line of the file closest to the payload's own lines and where
    /// it diverges, and whether the payload carries the two characters <c>\</c> + <c>r</c> that a model writes when it
    /// believes the file is CRLF (matching runs in LF space, so no <c>\r</c> is ever needed). Empty when there is nothing
    /// to add, and cheap: one pass over the lines whose first character could start an anchor.
    /// </summary>
    private static string Diagnose(string text, string oldString)
    {
        var hints = new List<string>(2);
        if (HasLiteralEscape(oldString))
        {
            hints.Add("old_string contains '\\' followed by 'r' or 'n': those are two literal characters, not a line break; matching runs on LF text, so no \\r is needed.");
        }

        if (NearestLine(text, oldString) is { } nearest)
        {
            hints.Add(nearest);
        }

        return hints.Count == 0 ? string.Empty : "\n" + string.Join('\n', hints);
    }

    /// <summary>True when the payload carries a backslash before <c>r</c> or <c>n</c> — the escape a model writes when it
    /// thinks it has to spell a line break itself.</summary>
    private static bool HasLiteralEscape(string text)
    {
        var span = text.AsSpan();
        for (var index = 0; index < span.Length - 1; index++)
        {
            if (span[index] == '\\' && span[index + 1] is 'r' or 'n')
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The line of the file closest to the payload's first three usable lines: the longest shared prefix, reported with the
    /// character where the two part. A line that matches on its own is a different mistake from a payload that is wrong,
    /// so it says so; a payload line the file continues is reported with the tail. One pass, no index.
    /// </summary>
    private static string? NearestLine(string text, string oldString)
    {
        var anchors = new List<string>(3);
        foreach (var line in oldString.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length < 4)
            {
                continue;
            }

            anchors.Add(trimmed);
            if (anchors.Count == 3)
            {
                break;
            }
        }

        if (anchors.Count == 0)
        {
            return null;
        }

        var best = (Shared: 0, Number: 0, Line: string.Empty, Anchor: string.Empty);
        var number = 0;
        var remaining = text.AsSpan();
        while (remaining.Length > 0)
        {
            number++;
            var newline = remaining.IndexOf('\n');
            var line = newline < 0 ? remaining : remaining[..newline];
            var trimmedLine = line.Trim();
            foreach (var anchor in anchors)
            {
                var shared = CommonPrefix(anchor, trimmedLine);
                if (shared == anchor.Length && shared == trimmedLine.Length)
                {
                    return $"line {number} matches an old_string line on its own; the surrounding lines are what differ.";
                }

                // Four shared characters is enough to recognize a line; the best one alone becomes the hint.
                if (shared >= Math.Min(anchor.Length, 4) && shared > best.Shared)
                {
                    best = (shared, number, trimmedLine.ToString(), anchor);
                }
            }

            if (newline < 0)
            {
                break;
            }

            remaining = remaining[(newline + 1)..];
        }

        if (best.Shared == 0)
        {
            return null;
        }

        return best.Shared == best.Anchor.Length
            ? $"closest line {best.Number}: your line ends at character {best.Shared}; the file continues \"{Clip(best.Line.AsSpan(best.Shared))}\"."
            : $"closest line {best.Number} differs at character {best.Shared + 1}: \"{Clip(best.Anchor)}\" vs \"{Clip(best.Line)}\".";
    }

    /// <summary>How many leading characters two lines share.</summary>
    private static int CommonPrefix(ReadOnlySpan<char> left, ReadOnlySpan<char> right)
    {
        var limit = Math.Min(left.Length, right.Length);
        var index = 0;
        while (index < limit && left[index] == right[index])
        {
            index++;
        }

        return index;
    }

    /// <summary>One short excerpt for a hint: enough to recognize the line, never a whole one.</summary>
    private static string Clip(ReadOnlySpan<char> text) =>
        text.Length <= 60 ? text.ToString() : string.Concat(text[..60], "…");

    /// <summary>True when the payload holds nothing but line breaks and carries a carriage return.</summary>
    private static bool IsLineBreaksOnly(string text)
    {
        var carriage = false;
        foreach (var character in text)
        {
            switch (character)
            {
                case '\r':
                    carriage = true;
                    break;
                case '\n':
                    break;
                default:
                    return false;
            }
        }

        return carriage;
    }

    /// <summary>Counts the occurrences of <paramref name="value"/> and remembers the 0-based index of the first two.</summary>
    private static int CountOccurrences(string text, string value, out int first, out int second)
    {
        first = -1;
        second = -1;
        var count = 0;
        var index = 0;
        var haystack = text.AsSpan();
        while (index <= haystack.Length - value.Length)
        {
            var found = haystack[index..].IndexOf(value.AsSpan(), StringComparison.Ordinal);
            if (found < 0)
            {
                break;
            }

            index += found;
            if (count == 0)
            {
                first = index;
            }
            else if (count == 1)
            {
                second = index;
            }

            count++;
            index += value.Length;
        }

        return count;
    }

    private static int LineOf(string text, int index) => text.AsSpan(0, index).Count('\n') + 1;

    private static int CountLines(string text) =>
        text.AsSpan().Count('\n') + (text.Length > 0 && text[^1] != '\n' ? 1 : 0);

    private static string Display(string root, string path) =>
        WorkspacePath.RelativeTo(root, path) is { Length: > 0 } relative ? relative : path;
}
