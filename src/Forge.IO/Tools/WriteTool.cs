using System.Text.Json;
using Forge.Abstractions;

namespace Forge.IO.Tools;

/// <summary>
/// Writes a workspace text file, atomically by default and by appending when asked. A whole-file write over an existing
/// file is the one place a stale context can destroy somebody else's work, so it is refused until this agent has read the
/// file and while the file no longer matches that read (see <see cref="IFileVersionLedger"/>); the append adds text
/// instead of replacing it and is never refused.
/// </summary>
public sealed class WriteTool() : ToolBase("write", Description, Schema, ToolSideEffect.Write), IToolCallScope
{
    private const string Schema =
        """{"type":"object","properties":{"path":{"type":"string","description":"File to write, workspace-relative or absolute."},"content":{"type":"string","description":"Text to write, UTF-8."},"append":{"type":"boolean","description":"Append instead of replacing the file."}},"required":["path","content"],"additionalProperties":false}""";

    private const string Description =
        "Write a text file as UTF-8. A path inside the workspace is written straight away; a path outside it asks the user " +
        "for that call. By default the file is replaced atomically, so it is either the old or the new content; with append " +
        "the text is added to the end and the write is not atomic. Read an existing file before overwriting it: the write is " +
        "refused when this agent has not read the file, or when the file changed since that read.";

    public ToolCallScope ScopeOf(ToolCall call, string workingDirectory) =>
        CallScope.Of(workingDirectory, call, "path");

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
        var path = FileOps.ResolveTarget(root, requested, context.OutOfWorkspaceAllowed);
        var display = FileOps.Display(root, path);
        if (Directory.Exists(path))
        {
            return new ToolResult($"{Definition.Name}: '{display}' is a directory.", IsError: true);
        }

        long written;
        using (await PathGate.Shared.AcquireAsync(path, ct).ConfigureAwait(false))
        {
            // The stat and the comparison share the lease with the write, so the file cannot be replaced between them.
            var ledger = context.FileVersions;
            var before = ledger is null ? null : FileVersionCheck.Stat(path);
            if (!append && ledger is not null && before is { } current)
            {
                if (!ledger.TryGet(path, out var seen))
                {
                    return new ToolResult(
                        $"{Definition.Name}: '{display}' exists and this agent has not read it; read it before overwriting it.",
                        IsError: true);
                }

                if (seen != current)
                {
                    return new ToolResult(
                        $"{Definition.Name}: '{display}' was changed after this agent read it; read it again before " +
                        "overwriting it.",
                        IsError: true);
                }
            }

            written = append
                ? await TextFileWriter.AppendAsync(path, content, ct).ConfigureAwait(false)
                : await TextFileWriter.WriteAsync(path, content, ct).ConfigureAwait(false);

            // A whole-file write leaves the file in the state just written, and a new file is recorded the same way. An
            // append only moves the record forward when what it appended to was still the version this agent read;
            // otherwise the old record has to stand, or the next whole-file write would overwrite that change with the
            // agent's older text.
            if (ledger is not null && (!append || FileVersionCheck.Matches(ledger, path, before)))
            {
                FileVersionCheck.Record(ledger, path);
            }
        }

        return new ToolResult($"{(append ? "appended" : "wrote")} {written} bytes to {display}");
    }
}

/// <summary>
/// The filesystem half of the overwrite check, shared by <c>read</c>, <c>write</c> and <c>edit</c>: what a path says right
/// now, whether this agent's record still describes it, and moving the record forward after a write. <c>edit</c> and
/// <c>append</c> take the last step only when the state they wrote over was still the one the agent read, so an edit over
/// somebody else's change cannot launder a stale record into a fresh one - the agent would then whole-write its older text
/// over that change and pass the check.
/// </summary>
internal static class FileVersionCheck
{
    /// <summary>The state of <paramref name="path"/> right now; null when there is no such file (a directory reads as absent).</summary>
    public static FileVersion? Stat(string path)
    {
        var info = new FileInfo(path);
        return info.Exists ? new FileVersion(info.Length, info.LastWriteTimeUtc.Ticks) : null;
    }

    /// <summary>True when this agent's record for <paramref name="path"/> still describes <paramref name="before"/>.</summary>
    public static bool Matches(IFileVersionLedger ledger, string path, FileVersion? before) =>
        before is { } version && ledger.TryGet(path, out var seen) && seen == version;

    /// <summary>Records what <paramref name="path"/> says now; only a caller whose write was safe may call this.</summary>
    public static void Record(IFileVersionLedger ledger, string path)
    {
        if (Stat(path) is { } version)
        {
            ledger.Record(path, version);
        }
    }
}
