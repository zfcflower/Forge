using Forge.App.Editing;
using Forge.Editor.Lsp;
using Forge.Editor.Text;
using Xunit;
using AvaloniaDocument = AvaloniaEdit.Document.TextDocument;

namespace Forge.App.Tests.Editor;

public class EditorLspMappingTests
{
    private const string Emoji = "a\uD83D\uDE00b\nxy";

    [Theory]
    [InlineData(PositionEncoding.Utf16, 0, 0, true, 0)]
    [InlineData(PositionEncoding.Utf16, 0, 1, true, 1)]
    [InlineData(PositionEncoding.Utf16, 0, 2, true, 2)]
    [InlineData(PositionEncoding.Utf16, 0, 3, true, 3)]
    [InlineData(PositionEncoding.Utf16, 0, 4, true, 4)]
    [InlineData(PositionEncoding.Utf16, 0, 5, false, 0)]
    [InlineData(PositionEncoding.Utf16, 1, 0, true, 5)]
    [InlineData(PositionEncoding.Utf16, 1, 2, true, 7)]
    [InlineData(PositionEncoding.Utf16, 1, 3, false, 0)]
    [InlineData(PositionEncoding.Utf16, 2, 0, false, 0)]
    [InlineData(PositionEncoding.Utf16, -1, 0, false, 0)]
    [InlineData(PositionEncoding.Utf16, 0, -1, false, 0)]
    [InlineData(PositionEncoding.Utf8, 0, 1, true, 1)]
    [InlineData(PositionEncoding.Utf8, 0, 2, false, 0)]
    [InlineData(PositionEncoding.Utf8, 0, 3, false, 0)]
    [InlineData(PositionEncoding.Utf8, 0, 4, false, 0)]
    [InlineData(PositionEncoding.Utf8, 0, 5, true, 3)]
    [InlineData(PositionEncoding.Utf8, 0, 6, true, 4)]
    [InlineData(PositionEncoding.Utf8, 0, 7, false, 0)]
    [InlineData(PositionEncoding.Utf8, 1, 1, true, 6)]
    public void Strict_offsets_accept_only_exact_character_boundaries(PositionEncoding encoding, int line, int character, bool valid, int offset)
    {
        var snapshot = TextSnapshot.FromText(Emoji, 0, encoding);
        var ok = EditorLspMapping.TryStrictOffset(snapshot, new LspPosition(line, character), encoding, out var actual);
        Assert.Equal(valid, ok);
        if (valid)
        {
            Assert.Equal(offset, actual);
        }
    }

    [Fact]
    public void Strict_offsets_reject_a_column_past_the_line_content_in_a_crlf_document()
    {
        var snapshot = TextSnapshot.FromText("ab\r\ncd");
        Assert.True(EditorLspMapping.TryStrictOffset(snapshot, new LspPosition(0, 2), PositionEncoding.Utf16, out var end));
        Assert.Equal(2, end);
        Assert.False(EditorLspMapping.TryStrictOffset(snapshot, new LspPosition(0, 3), PositionEncoding.Utf16, out _));
        Assert.True(EditorLspMapping.TryStrictOffset(snapshot, new LspPosition(1, 0), PositionEncoding.Utf16, out var next));
        Assert.Equal(4, next);
    }

    [Fact]
    public void Completion_mapping_uses_the_typed_prefix_and_counts_snippets_and_invalid_items()
    {
        var items = new[]
        {
            Item("bar", insert: "bar"),
            Item("viaEdit", edit: Edit(0, 4, 0, 6, "viaEdit")),
            Item("beforeCaret", edit: Edit(0, 0, 0, 2, "x")),
            Item("afterCaret", edit: Edit(0, 4, 0, 5, "x")),
            Item("snip", insert: "for ${1}", format: InsertTextFormat.Snippet),
            Item("lines", insert: "a\nb\r\nc"),
        };
        var map = Map("foo.ba", 6, items, malformed: 2, newLine: "\r\n");
        Assert.Equal(4, map.Completion.PrefixStart);
        Assert.Equal(6, map.Completion.RequestOffset);
        Assert.Equal(1, map.SkippedSnippets);
        Assert.Equal(4, map.InvalidItems);
        Assert.Equal(["bar", "lines", "viaEdit"], map.Completion.Items.Select(static i => i.Label));
        Assert.Equal(new EditorLspEdit(4, 6, "bar"), map.Completion.Items[0].Edit);
        Assert.Equal(new EditorLspEdit(4, 6, "a\r\nb\r\nc"), map.Completion.Items[1].Edit);
        Assert.Equal(new EditorLspEdit(4, 6, "viaEdit"), map.Completion.Items[2].Edit);
    }

    [Fact]
    public void Additional_edits_are_mapped_and_overlapping_or_same_point_edits_reject_the_item()
    {
        var main = Edit(1, 2, 1, 4, "main");
        var items = new[]
        {
            Item("ok", edit: main, additional: [Edit(0, 0, 0, 0, "import x;\n")]),
            Item("touchesMain", edit: main, additional: [Edit(1, 2, 1, 2, "y")]),
            Item("overlapsMain", edit: main, additional: [Edit(1, 3, 1, 5, "y")]),
            Item("twoAtSamePoint", edit: main, additional: [Edit(0, 0, 0, 0, "a"), Edit(0, 0, 0, 0, "b")]),
            Item("outOfRange", edit: main, additional: [Edit(9, 0, 9, 0, "z")]),
            Item("reversedMapped", edit: main, additional: [Edit(0, 1, 0, 0, "z")]),
        };
        var map = Map("abc\nxxmm", 8, items);
        var ok = Assert.Single(map.Completion.Items);
        Assert.Equal("ok", ok.Label);
        Assert.Equal(new EditorLspEdit(6, 8, "main"), ok.Edit);
        Assert.Equal([new EditorLspEdit(0, 0, "import x;\n")], ok.AdditionalEdits);
        Assert.Equal(5, map.InvalidItems);
    }

    [Fact]
    public void Main_and_additional_edits_apply_as_one_undo_step_and_leave_the_exact_text()
    {
        var document = new AvaloniaDocument("\nba");
        using var log = new EditorLspChangeLog(document);
        var item = Mapped("bar", new EditorLspEdit(1, 3, "bar"), new EditorLspEdit(0, 0, "import;"));
        var applied = EditorLspEditApplier.TryApply(document, 3, item, log, out var caret);
        Assert.True(applied);
        Assert.Equal("import;\nbar", document.Text);
        Assert.Equal(document.TextLength, caret);
        Assert.True(document.UndoStack.CanUndo);
        document.UndoStack.Undo();
        Assert.Equal("\nba", document.Text);
        Assert.False(document.UndoStack.CanUndo);
    }

    [Fact]
    public void Typing_a_prefix_character_while_the_popup_is_open_keeps_the_edit_valid()
    {
        var document = new AvaloniaDocument("foo.ba");
        using var log = new EditorLspChangeLog(document);
        var item = Mapped("bar", new EditorLspEdit(4, 6, "bar"));
        document.Insert(6, "r");
        var applied = EditorLspEditApplier.TryApply(document, 7, item, log, out var caret);
        Assert.True(applied);
        Assert.Equal("foo.bar", document.Text);
        Assert.Equal(7, caret);
    }

    [Fact]
    public void Completing_after_the_caret_left_the_prefix_changes_nothing()
    {
        var document = new AvaloniaDocument("foo.ba");
        using var log = new EditorLspChangeLog(document);
        var item = Mapped("bar", new EditorLspEdit(4, 6, "bar"));
        Assert.False(EditorLspEditApplier.TryApply(document, 2, item, log, out _));
        Assert.Equal("foo.ba", document.Text);
    }

    [Fact]
    public void Deleting_the_whole_main_range_while_open_rejects_without_changing_the_document()
    {
        var document = new AvaloniaDocument("foo.ba");
        using var log = new EditorLspChangeLog(document);
        var item = Mapped("bar", new EditorLspEdit(4, 6, "bar"));
        document.Remove(4, 2);
        var applied = EditorLspEditApplier.TryApply(document, 4, item, log, out _);
        Assert.False(applied);
        Assert.Equal("foo.", document.Text);
    }

    [Fact]
    public void An_additional_edit_touched_while_open_is_rejected_and_nothing_is_applied()
    {
        foreach (var mutate in new Action<AvaloniaDocument>[]
        {
            static d => d.Insert(3, "!"),
            static d => d.Insert(2, "!"),
            static d => d.Remove(1, 3),
        })
        {
            var document = new AvaloniaDocument("abcdef.ba");
            using var log = new EditorLspChangeLog(document);
            var item = Mapped("bar", new EditorLspEdit(7, 9, "bar"), new EditorLspEdit(2, 5, "XYZ"));
            mutate(document);
            var before = document.Text;
            var applied = EditorLspEditApplier.TryApply(document, document.TextLength, item, log, out _);
            Assert.False(applied);
            Assert.Equal(before, document.Text);
        }
    }

    [Fact]
    public void An_additional_insertion_before_the_edit_follows_earlier_typing_and_untouched_edits_still_apply()
    {
        var document = new AvaloniaDocument("\nfoo.ba");
        using var log = new EditorLspChangeLog(document);
        var item = Mapped("bar", new EditorLspEdit(5, 7, "bar"), new EditorLspEdit(0, 0, "I;"));
        document.Insert(7, "r");
        var applied = EditorLspEditApplier.TryApply(document, 8, item, log, out var caret);
        Assert.True(applied);
        Assert.Equal("I;\nfoo.bar", document.Text);
        Assert.Equal(document.TextLength, caret);
    }

    [Fact]
    public void Overlapping_edits_handed_straight_to_the_applier_are_rejected_without_changes()
    {
        var document = new AvaloniaDocument("foo.ba");
        using var log = new EditorLspChangeLog(document);
        var item = Mapped("bar", new EditorLspEdit(4, 6, "bar"), new EditorLspEdit(5, 6, "z"));
        Assert.False(EditorLspEditApplier.TryApply(document, 6, item, log, out _));
        Assert.Equal("foo.ba", document.Text);
        Assert.False(document.UndoStack.CanUndo);
    }

    private static EditorLspCompletionMap Map(string text, int offset, CompletionItem[] items, int malformed = 0, string newLine = "\n") =>
        EditorLspMapping.MapCompletion(
            new CompletionResult(items, false, malformed), TextSnapshot.FromText(text), offset, PositionEncoding.Utf16, newLine);
    private static CompletionItem Item(
        string label, string? insert = null, LspTextEdit? edit = null, LspTextEdit[]? additional = null,
        InsertTextFormat format = InsertTextFormat.PlainText) =>
        new(label, CompletionItemKind.Text, null, null, insert, edit, null, format, additional);
    private static LspTextEdit Edit(int line1, int ch1, int line2, int ch2, string text) =>
        new(new LspRange(new LspPosition(line1, ch1), new LspPosition(line2, ch2)), text);
    private static EditorLspCompletionItem Mapped(string label, EditorLspEdit main, params EditorLspEdit[] additional) =>
        new(label, label, CompletionItemKind.Text, null, null, label, main, additional);
}
