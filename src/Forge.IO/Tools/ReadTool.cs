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
/// </summary>
public sealed class ReadTool : ToolBase, IBoundedOutputTool
{
    /// <summary>
    /// The most characters of file text one call returns. It equals the loop's spill threshold on purpose: a result this size
    /// stays in the conversation as it is, so a read is never turned into an artifact that needs a second tool call.
    /// </summary>
    internal const int MaxResultChars = 32_000;

    private const string Schema =
        """{"type":"object","properties":{"path":{"type":"string","description":"File to read, workspace-relative or absolute."},"offset":{"type":"integer","description":"First line to return, 1-based.","minimum":1},"limit":{"type":"integer","description":"How many lines to return.","minimum":1,"maximum":20000},"max_bytes":{"type":"integer","description":"Byte budget of the returned text.","minimum":1,"maximum":1048576}},"required":["path"],"additionalProperties":false}""";

    private const string Description =
        "Read a text file, inside the workspace by a relative path or anywhere by an absolute one, with line numbers. One " +
        "call returns at most about 32,000 characters; a cut result names the lines shown and the offset to continue from. " +
        "Read a large file by range (offset and limit), not whole. The byte budget cuts at a byte offset, so its last line " +
        "can be partial.";

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
        var requested = ToolJson.RequiredString(arguments, "path");
        var current = options();
        var firstLine = ToolJson.OptionalInt(arguments, "offset", ToolOptions.DefaultReadLineOffset, 1, int.MaxValue);
        var maxLines = ToolJson.OptionalInt(
            arguments,
            "limit",
            Math.Clamp(current.ReadMaxLines, 1, ToolOptions.MaxReadMaxLines),
            1,
            ToolOptions.MaxReadMaxLines);
        var maxBytes = ToolJson.OptionalInt(
            arguments,
            "max_bytes",
            Math.Clamp(current.ReadMaxBytes, 1, ToolOptions.MaxReadMaxBytes),
            1,
            ToolOptions.MaxReadMaxBytes);

        var root = Root(context);
        var path = Resolve(root, requested);
        var display = Display(root, path);
        if (Directory.Exists(path))
        {
            return new ToolResult($"{Definition.Name}: '{display}' is a directory; use `ls` to list it.", IsError: true);
        }

        // Stat'ed and recorded before the read: a change during the read leaves the older state behind, so a later
        // whole-file write is refused instead of silently replacing whatever arrived in between. Without a ledger (a
        // script, a plugin bridge) the read pays nothing beyond the existence check it always did.
        var ledger = context.FileVersions;
        var version = ledger is null ? null : FileVersionCheck.Stat(path);
        if (version is null && !File.Exists(path))
        {
            return new ToolResult($"{Definition.Name}: '{display}' does not exist.", IsError: true);
        }

        if (ledger is not null && version is { } state)
        {
            ledger.Record(path, state);
        }

        try
        {
            var window = await TextFileReader
                .ReadWindowAsync(path, firstLine, maxLines, maxBytes, ct)
                .ConfigureAwait(false);
            return new ToolResult(Format(display, window));
        }
        catch (NotTextFileException exception)
        {
            return new ToolResult(
                $"{Definition.Name}: '{display}' looks binary or is not UTF-8 text ({exception.ByteLength} bytes).",
                IsError: true);
        }
    }

    private static string Format(string display, in TextFileWindow window)
    {
        var lines = window.Lines;
        var width = lines.Length == 0 ? 1 : (window.FirstLine + lines.Length - 1).ToString(CultureInfo.InvariantCulture).Length;

        // Whole lines only, up to the character cap; a first line longer than the cap is cut itself so a call always makes progress.
        var shown = 0;
        var chars = 0;
        var firstCut = false;
        while (shown < lines.Length)
        {
            var cost = lines[shown].Length + width + 3;
            if (shown > 0 && chars + cost > MaxResultChars)
            {
                break;
            }

            chars += cost;
            shown++;
            if (chars > MaxResultChars)
            {
                firstCut = true;
                break;
            }
        }

        var cutByChars = shown < lines.Length || firstCut;
        var text = new StringBuilder(Math.Min(chars, MaxResultChars) + display.Length + 160);
        AppendHeader(text, display, window.FirstLine, shown, window.Truncated || cutByChars);

        Span<char> number = stackalloc char[12];
        for (var index = 0; index < shown; index++)
        {
            (window.FirstLine + index).TryFormat(number, out var digits, provider: CultureInfo.InvariantCulture);
            var line = lines[index];
            var room = MaxResultChars;
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

    /// <summary>
    /// The absolute path to read. Inside the workspace it goes through <see cref="WorkspacePath.Resolve"/>, so the read
    /// records the same key an <c>edit</c> later checks; outside it is taken as given (links resolved), which is the one
    /// place the workspace bound does not apply — a read cannot change anything, and a write in that place still refuses.
    /// </summary>
    private static string Resolve(string root, string requested)
    {
        var full = Path.IsPathRooted(requested)
            ? Path.GetFullPath(requested)
            : Path.GetFullPath(Path.Combine(root, requested));
        return WorkspacePath.IsUnder(full, root)
            ? WorkspacePath.Resolve(root, requested)
            : WorkspacePath.ResolveRealPath(full);
    }

    private static string Display(string root, string path) =>
        WorkspacePath.IsUnder(path, root) && WorkspacePath.RelativeTo(root, path) is { Length: > 0 } relative ? relative : path;
}
