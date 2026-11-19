using System.Text.Json;
using Forge.Abstractions;

namespace Forge.IO.Tools;

/// <summary>Writes a workspace text file, atomically by default and by appending when asked.</summary>
public sealed class WriteTool() : ToolBase("write", Description, Schema, ToolSideEffect.Write)
{
    private const string Schema =
        """{"type":"object","properties":{"path":{"type":"string","description":"File to write, workspace-relative or absolute."},"content":{"type":"string","description":"Text to write, UTF-8."},"append":{"type":"boolean","description":"Append instead of replacing the file."}},"required":["path","content"],"additionalProperties":false}""";

    private const string Description =
        "Write a text file in the workspace as UTF-8. By default the file is replaced atomically, so it is either the old " +
        "or the new content; with append the text is added to the end and the write is not atomic.";

    protected override async ValueTask<ToolResult> InvokeCoreAsync(
        JsonElement arguments,
        ToolContext context,
        CancellationToken ct)
    {
        var requested = ToolJson.RequiredString(arguments, "path");
        if (ToolJson.OptionalString(arguments, "content") is not { } content)
        {
            throw new ToolArgumentException("'content' is required.");
        }

        var append = ToolJson.OptionalBool(arguments, "append", false);
        var root = Root(context);
        var path = FileOps.ResolveTarget(root, requested);
        var display = FileOps.Display(root, path);
        if (Directory.Exists(path))
        {
            return new ToolResult($"{Definition.Name}: '{display}' is a directory.", IsError: true);
        }

        long written;
        using (await PathGate.Shared.AcquireAsync(path, ct).ConfigureAwait(false))
        {
            written = append
                ? await TextFileWriter.AppendAsync(path, content, ct).ConfigureAwait(false)
                : await TextFileWriter.WriteAsync(path, content, ct).ConfigureAwait(false);
        }

        return new ToolResult($"{(append ? "appended" : "wrote")} {written} bytes to {display}");
    }
}
