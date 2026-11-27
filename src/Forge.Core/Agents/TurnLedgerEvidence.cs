using System.Collections.Frozen;
using System.Text.Json;
using Forge.Abstractions;

namespace Forge.Core.Agents;

/// <summary>
/// The evidence pins of one turn, collected by the harness while the turn runs (forge-core "Turn Ledger"): the paths the
/// turn's tools read, the paths they changed, and the spec ids it read or asked for. The two file sets stay apart because a
/// later turn re-checks what changed, not what was merely opened. The model never sees or writes any of
/// it - the seq range lives on the completion record instead - which is also why no line numbers appear here: a seq
/// points at a stored message forever, a line number drifts with the next edit.
/// <para>Nothing here reads a file: the paths come out of the calls' own arguments, and a spec id comes either from
/// <c>spec_get</c>'s <c>id</c> argument or out of the <c>read</c> result text already in memory - the collection is pure
/// in-memory work on the loop thread, so it opens nothing, blocks nothing and cancels nothing.</para>
/// </summary>
internal sealed class TurnLedgerEvidence
{
    /// <summary>How many file paths one row stores; the paths beyond it are simply not pinned.</summary>
    public const int MaxFiles = 50;

    /// <summary>
    /// The file tools and the argument that names what they touched. <c>copy</c> and <c>move</c> name their target, the
    /// same argument <see cref="CompactionFiles"/> records them under - the one precedent this mirrors. <c>read</c> is
    /// not listed here: it names its file through <c>path</c> or the <c>paths</c> array, both read below.
    /// </summary>
    private static readonly FrozenDictionary<string, string> PathArguments =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["edit"] = "path",
            ["write"] = "path",
            ["delete"] = "path",
            ["copy"] = "to",
            ["move"] = "to",
        }.ToFrozenDictionary(StringComparer.Ordinal);

    private readonly SortedSet<string> readFiles = new(StringComparer.Ordinal);
    private readonly SortedSet<string> changedFiles = new(StringComparer.Ordinal);
    private readonly SortedSet<string> specs = new(StringComparer.Ordinal);

    public IReadOnlyList<string> ReadFiles => readFiles.Count == 0 ? [] : [.. readFiles];

    /// <summary>The paths the turn changed: a path both read and written is reported here only - the change is the fact worth
    /// keeping, and one path in both lists would read as two different files.</summary>
    public IReadOnlyList<string> ChangedFiles => changedFiles.Count == 0 ? [] : [.. changedFiles];

    public IReadOnlyList<string> Specs => specs.Count == 0 ? [] : [.. specs];

    /// <summary>
    /// Reads one batch of the turn's tool calls together with the results that batch produced, aligned by call index: the
    /// paths come out of the calls' arguments (a <c>read</c> names one file through <c>path</c> or several through the
    /// <c>paths</c> array), and a spec id comes out of a single-file <c>read</c> result whose text begins with a frontmatter
    /// block carrying <c>id</c>. A multi-file <c>read</c> result is not frontmatter-scanned: its text interleaves several
    /// files' headers, so nothing in it can be attributed to one spec.
    /// </summary>
    public void Collect(ToolCallBlock[] calls, ContentBlock[] results)
    {
        ArgumentNullException.ThrowIfNull(calls);
        ArgumentNullException.ThrowIfNull(results);
        for (var index = 0; index < calls.Length; index++)
        {
            var call = calls[index];
            if (string.Equals(call.Name, "spec_get", StringComparison.Ordinal))
            {
                AddSpecs(StringArguments(call.ArgumentsJson, "id"));
                continue;
            }

            if (string.Equals(call.Name, "read", StringComparison.Ordinal))
            {
                var single = StringArguments(call.ArgumentsJson, "path");
                var many = StringArguments(call.ArgumentsJson, "paths");
                AddFiles(readFiles, single);
                AddFiles(readFiles, many);

                // Only a single-file read result can be one file's frontmatter; an error result says why the file
                // could not be read, so it is never scanned either.
                if (many.Count == 0
                    && single.Count == 1
                    && index < results.Length
                    && results[index] is ToolResultBlock { IsError: false } result)
                {
                    AddSpecs(SpecIdOfReadResult(result.Content));
                }

                continue;
            }

            if (!PathArguments.TryGetValue(call.Name, out var argument))
            {
                continue;
            }

            AddFiles(changedFiles, StringArguments(call.ArgumentsJson, argument));
        }
    }

    private static void AddFiles(SortedSet<string> into, List<string> paths)
    {
        foreach (var path in paths)
        {
            // The cap keeps the column bounded; which paths fall off is unrecorded, the same way a read list's cap does.
            if (into.Count < MaxFiles)
            {
                into.Add(path);
            }
        }
    }

    private void AddSpecs(IEnumerable<string> ids)
    {
        foreach (var id in ids)
        {
            specs.Add(id);
        }
    }

    /// <summary>How much of a read result is ever looked at for frontmatter: the block of any real spec file fits in this head.</summary>
    private const int MaxResultScanChars = 8192;

    /// <summary>
    /// The spec id of a single-file <c>read</c> result, when the result's own text carries a complete frontmatter block.
    /// The production <c>read</c> answer is not the file's raw text: it opens with a header line (<c>path: N lines</c>, or
    /// the range a window covered) and every body line is prefixed <c>N→</c> with the digits right-aligned, so the scan
    /// skips the header, strips each line's prefix and reads the block the file itself would show - an opening
    /// <c>---</c>, the key lines, a closing <c>---</c> before any blank-separated body. A block that closes without an
    /// <c>id</c>, never closes, or is cut by the scan bound pins nothing, and a result whose first body line is not the
    /// opening <c>---</c> is left without a copy of any of it. Bounded in-memory work only: the file's head is already
    /// in the result, no file is ever opened.
    /// </summary>
    private static IReadOnlyList<string> SpecIdOfReadResult(string content)
    {
        var text = content.AsSpan();
        if (text.Length > MaxResultScanChars)
        {
            text = text[..MaxResultScanChars];
        }

        // The header line first: the file's own opening line is the first numbered one under it.
        var position = 0;
        if (!TryReadLine(text, ref position, out _))
        {
            return [];
        }

        if (!TryReadNumberedLine(text, ref position, out var opening) || !opening.Trim().SequenceEqual("---"))
        {
            return [];
        }

        string? id = null;
        while (TryReadNumberedLine(text, ref position, out var line))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0)
            {
                // A blank line before the block closed: the body has begun, and a block without its closing --- is not
                // a frontmatter block the result can pin anything by.
                return [];
            }

            if (trimmed[0] == '#')
            {
                continue;
            }

            if (trimmed.SequenceEqual("---"))
            {
                return id is { Length: > 0 } value ? [value] : [];
            }

            if (id is null)
            {
                var colon = trimmed.IndexOf(':');
                if (colon > 0
                    && trimmed[..colon].Trim().SequenceEqual("id")
                    && trimmed[(colon + 1)..].Trim() is { Length: > 0 } value)
                {
                    id = value.ToString();
                }
            }
        }

        // The block never closed inside what was scanned (or the line shapes stopped matching): nothing is pinned.
        return [];
    }

    /// <summary>
    /// One line of a read answer: the <c>N→</c> prefix (right-aligned digits, then the arrow) stripped, so the line reads
    /// as the file's own text. A line without the prefix - the ellipsis of a pattern gap, the truncation note - fails, and
    /// the block it sat in ends incomplete.
    /// </summary>
    private static bool TryReadNumberedLine(ReadOnlySpan<char> text, ref int position, out ReadOnlySpan<char> line)
    {
        if (!TryReadLine(text, ref position, out var raw))
        {
            line = default;
            return false;
        }

        var index = 0;
        while (index < raw.Length && raw[index] == ' ')
        {
            index++;
        }

        var digits = index;
        while (index < raw.Length && char.IsDigit(raw[index]))
        {
            index++;
        }

        if (index == digits || index >= raw.Length || raw[index] != '→')
        {
            line = default;
            return false;
        }

        line = raw[(index + 1)..];
        return true;
    }

    private static bool TryReadLine(ReadOnlySpan<char> text, ref int position, out ReadOnlySpan<char> line)
    {
        if (position >= text.Length)
        {
            line = default;
            return false;
        }

        var end = text[position..].IndexOf('\n');
        if (end < 0)
        {
            line = text[position..];
            position = text.Length;
        }
        else
        {
            line = text[position..(position + end)];
            position += end + 1;
        }

        return true;
    }

    /// <summary>One string argument of a call: a single string, or the strings of an array (delete and copy take arrays).</summary>
    private static List<string> StringArguments(string argumentsJson, string name)
    {
        var values = new List<string>();
        if (string.IsNullOrWhiteSpace(argumentsJson))
        {
            return values;
        }

        try
        {
            using var document = JsonDocument.Parse(argumentsJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty(name, out var value))
            {
                return values;
            }

            var items = value.ValueKind == JsonValueKind.Array ? value.EnumerateArray().ToArray() : [value];
            foreach (var item in items)
            {
                // The same normalization CompactionFiles applies: the trimmed text, nothing else.
                if (item.ValueKind == JsonValueKind.String && item.GetString()?.Trim() is { Length: > 0 } trimmed)
                {
                    values.Add(trimmed);
                }
            }
        }
        catch (JsonException)
        {
        }

        return values;
    }
}
