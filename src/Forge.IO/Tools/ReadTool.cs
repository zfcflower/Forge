using System.Globalization;
using System.Text;
using System.Text.Json;
using Forge.Abstractions;

namespace Forge.IO.Tools;

/// <summary>Reads a bounded window of a workspace text file and prints it with 1-based line numbers.</summary>
public sealed class ReadTool : ToolBase
{
    private const string Schema =
        """{"type":"object","properties":{"path":{"type":"string","description":"File to read, workspace-relative or absolute."},"offset":{"type":"integer","description":"First line to return, 1-based.","minimum":1},"limit":{"type":"integer","description":"How many lines to return.","minimum":1,"maximum":20000},"max_bytes":{"type":"integer","description":"Byte budget of the returned text.","minimum":1,"maximum":1048576}},"required":["path"],"additionalProperties":false}""";

    private const string Description =
        "Read a text file from the workspace with line numbers. A whole-file read is cut at the line budget; the byte " +
        "budget cuts at a byte offset, so its last line can be partial. Every cut is marked truncated.";

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
        var path = WorkspacePath.Resolve(root, requested);
        var display = Display(root, path);
        if (Directory.Exists(path))
        {
            return new ToolResult($"{Definition.Name}: '{display}' is a directory; use `ls` to list it.", IsError: true);
        }

        if (!File.Exists(path))
        {
            return new ToolResult($"{Definition.Name}: '{display}' does not exist.", IsError: true);
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
        var lastLine = window.FirstLine + lines.Length - 1;
        var width = lines.Length == 0 ? 1 : lastLine.ToString(CultureInfo.InvariantCulture).Length;
        var text = new StringBuilder(display.Length + 32 + (lines.Length * (width + 2)));
        text.Append(display).Append(": ").Append(lines.Length).Append(" lines\n");

        Span<char> number = stackalloc char[12];
        for (var index = 0; index < lines.Length; index++)
        {
            (window.FirstLine + index).TryFormat(number, out var digits, provider: CultureInfo.InvariantCulture);
            text.Append(' ', width - digits).Append(number[..digits]).Append('→').Append(lines[index]).Append('\n');
        }

        if (window.Truncated)
        {
            text.Append("… (truncated)");
        }

        return text.ToString().TrimEnd('\n');
    }

    private static string Display(string root, string path) =>
        WorkspacePath.RelativeTo(root, path) is { Length: > 0 } relative ? relative : path;
}
