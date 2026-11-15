using Forge.App.Editing;
using Forge.Editor.Lsp;
using Forge.Editor.Text;
using Xunit;
using AvaloniaDocument = AvaloniaEdit.Document.TextDocument;

namespace Forge.App.Tests.Editor;

/// <summary>
/// The workspace edit applier and the inlay hint index without a window: the protocol edit is mapped against the text it was
/// computed for, a file is refused whole when any range does not fit, the open buffer is one undo step, and hints are bucketed
/// per line (two hints at one offset cannot both be drawn).
/// </summary>
public class EditorWorkspaceEditApplierTests : IDisposable
{
    private readonly string root = TestTempFiles.NewDirectory("forge-workspace-edit-");

    public void Dispose() => TestTempFiles.DeleteTree(root);

    [Fact]
    public async Task A_file_on_disk_is_read_mapped_and_written()
    {
        var path = Path.Combine(root, "Other.cs");
        await File.WriteAllTextAsync(path, "alpha = 1;\r\nalpha++;\r\n");
        var edits = new[]
        {
            new LspTextEdit(Range(0, 0, 5), "beta"),
            new LspTextEdit(Range(1, 0, 5), "beta"),
        };

        Assert.True(await EditorWorkspaceEditApplier.ApplyToFileAsync(path, edits, PositionEncoding.Utf16, "\r\n", CancellationToken.None));

        Assert.Equal("beta = 1;\r\nbeta++;\r\n", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task A_range_the_file_does_not_have_refuses_the_whole_file_and_leaves_it_untouched()
    {
        var path = Path.Combine(root, "Other.cs");
        await File.WriteAllTextAsync(path, "alpha\n");
        var edits = new[]
        {
            new LspTextEdit(Range(0, 0, 5), "beta"),
            new LspTextEdit(Range(4, 0, 2), "beta"),
        };

        Assert.False(await EditorWorkspaceEditApplier.ApplyToFileAsync(path, edits, PositionEncoding.Utf16, "\n", CancellationToken.None));

        Assert.Equal("alpha\n", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public void Overlapping_edits_of_one_file_are_refused_instead_of_applied()
    {
        var snapshot = TextSnapshot.FromText("abcdef\n", version: 0, PositionEncoding.Utf16);
        var edits = new[]
        {
            new LspTextEdit(Range(0, 0, 4), "x"),
            new LspTextEdit(Range(0, 2, 6), "y"),
        };

        Assert.False(EditorWorkspaceEditApplier.TryMapEdits(edits, snapshot, PositionEncoding.Utf16, "\n", out _));
    }

    [Fact]
    public void Applying_to_the_open_document_is_one_undo_step()
    {
        var document = new AvaloniaDocument("alpha\nbeta\n");
        var snapshot = TextSnapshot.FromText("alpha\nbeta\n", version: 0, PositionEncoding.Utf16);
        var edits = new[]
        {
            new LspTextEdit(Range(0, 0, 5), "gamma"),
            new LspTextEdit(Range(1, 0, 4), "delta"),
        };

        Assert.True(EditorWorkspaceEditApplier.TryMapEdits(edits, snapshot, PositionEncoding.Utf16, "\n", out var mapped));
        Assert.True(EditorWorkspaceEditApplier.TryApply(document, mapped));
        Assert.Equal("gamma\ndelta\n", document.Text);
        Assert.True(document.UndoStack.CanUndo);

        document.UndoStack.Undo();
        Assert.Equal("alpha\nbeta\n", document.Text);
    }

    [Fact]
    public void Inlay_hints_are_bucketed_per_line_and_one_offset_keeps_only_the_first()
    {
        var hints = new[]
        {
            new EditorInlayHint(1, 14, "b:", InlayHintKind.Parameter, false, true),
            new EditorInlayHint(0, 6, ": int", InlayHintKind.Type, true, false),
            new EditorInlayHint(0, 6, "second", null, false, false),
            new EditorInlayHint(0, 2, "first", null, false, false),
            new EditorInlayHint(9, 0, "outside", null, false, false),
        };

        var index = InlayHintLineIndex.Build(hints, lineCount: 3, version: 7);

        Assert.Equal(7, index.Version);
        Assert.Equal(3, index.HintCount);
        Assert.Equal(2, index.Dropped);
        Assert.Equal([("first", 2), (": int", 6)], index.GetLine(0).ToArray().Select(static hint => (hint.Label, hint.Offset)).ToArray());
        Assert.Equal("b:", Assert.Single(index.GetLine(1).ToArray()).Label);
        Assert.True(index.GetLine(2).IsEmpty);
        Assert.True(InlayHintLineIndex.Empty.IsEmpty);
    }

    private static LspRange Range(int line, int start, int end) =>
        new(new LspPosition(line, start), new LspPosition(line, end));
}
