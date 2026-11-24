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
                return new ToolResult(
                    $"{Definition.Name}: no match for old_string in '{display}' ({CountLines(text)} lines); nothing changed.",
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
