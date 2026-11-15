using System.Text;
using Forge.Editor.Lsp;
using Forge.Editor.Text;
using EditorDocument = AvaloniaEdit.Document.TextDocument;

namespace Forge.App.Editing;

/// <summary>
/// Applies a server <see cref="WorkspaceEdit"/>: the open buffer's part through the editor document (one undo step, the same
/// path a completion takes) and every other file by reading it, applying the mapped edits and writing it back atomically
/// through <see cref="TextFileCodec"/>.
/// <para>All-or-nothing per file: an edit whose range does not map exactly onto the text is refused together with the rest of
/// that file's edits, so a rename can never write a half-applied file. Mapping runs against an immutable snapshot, which is
/// what lets it happen off the UI thread.</para>
/// </summary>
internal static class EditorWorkspaceEditApplier
{
    /// <summary>
    /// Maps one file's protocol edits onto offsets in <paramref name="snapshot"/>. False when a range does not map or two
    /// edits overlap — the caller drops the whole file rather than applying a subset.
    /// </summary>
    public static bool TryMapEdits(
        IReadOnlyList<LspTextEdit> edits,
        TextSnapshot snapshot,
        PositionEncoding encoding,
        string newLine,
        out List<EditorLspEdit> mapped)
    {
        ArgumentNullException.ThrowIfNull(edits);
        ArgumentNullException.ThrowIfNull(snapshot);

        mapped = new List<EditorLspEdit>(edits.Count);
        foreach (var edit in edits)
        {
            if (!EditorLspMapping.TryStrictOffset(snapshot, edit.Range.Start, encoding, out var start) ||
                !EditorLspMapping.TryStrictOffset(snapshot, edit.Range.End, encoding, out var end) || end < start)
            {
                return false;
            }

            mapped.Add(new EditorLspEdit(start, end, EditorLspMapping.NormalizeNewLines(edit.NewText, newLine)));
        }

        mapped.Sort(static (left, right) => left.Start != right.Start ? left.Start.CompareTo(right.Start) : left.End.CompareTo(right.End));
        for (var i = 1; i < mapped.Count; i++)
        {
            if (mapped[i].Start < mapped[i - 1].End || (mapped[i].Start == mapped[i - 1].Start && mapped[i].End == mapped[i - 1].End))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Applies already mapped edits to the open editor document, highest offset first so earlier offsets stay valid, inside one
    /// undo group. Returns false (and changes nothing) when an offset no longer fits the document.
    /// </summary>
    public static bool TryApply(EditorDocument document, IReadOnlyList<EditorLspEdit> edits)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(edits);
        if (edits.Count == 0)
        {
            return true;
        }

        foreach (var edit in edits)
        {
            if (edit.Start < 0 || edit.End > document.TextLength || edit.End < edit.Start)
            {
                return false;
            }
        }

        using (document.RunUpdate())
        {
            for (var i = edits.Count - 1; i >= 0; i--)
            {
                var edit = edits[i];
                document.Replace(edit.Start, edit.End - edit.Start, edit.NewText);
            }
        }

        return true;
    }

    /// <summary>
    /// Applies one file's protocol edits to the file on disk: read, map against its own text, splice, write atomically. Runs on
    /// the caller's thread — call it from the thread pool, never from the dispatcher.
    /// </summary>
    public static async Task<bool> ApplyToFileAsync(
        string path,
        IReadOnlyList<LspTextEdit> edits,
        PositionEncoding encoding,
        string newLine,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(edits);

        var file = await TextFileCodec.ReadAsync(path, ct).ConfigureAwait(false);
        if (file.IsBinary)
        {
            return false;
        }

        var snapshot = TextSnapshot.FromText(file.Text, version: 0, encoding);
        if (!TryMapEdits(edits, snapshot, encoding, newLine, out var mapped) || mapped.Count == 0)
        {
            return false;
        }

        var text = new StringBuilder(file.Text);
        for (var i = mapped.Count - 1; i >= 0; i--)
        {
            var edit = mapped[i];
            text.Remove(edit.Start, edit.End - edit.Start).Insert(edit.Start, edit.NewText);
        }

        await TextFileCodec.WriteAsync(path, TextSnapshot.FromText(text.ToString(), version: 0, encoding), file.Format, ct)
            .ConfigureAwait(false);
        return true;
    }
}
