using Avalonia.Controls;
using Avalonia.Headless;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using Forge.App.Editing;
using Forge.App.Tests.Workbench;
using Forge.Editor.Lsp;
using Xunit;

namespace Forge.App.Tests.Editor;

/// <summary>
/// The inlay-hint element generator's construction guard (wave W17a, review fix): the library asks again at the same offset
/// right after a zero-length element, which must be refused; a <em>different</em> offset — forward on the next line, or
/// backward when a layout rebuild starts over — must start a fresh pass, or a hint at the end of the last visible line is
/// dropped from every later rebuild until the index changes.
/// </summary>
[Collection("forge-app-ui")]
public sealed class EditorInlayHintGeneratorTests : IDisposable
{
    private readonly string root = TestTempFiles.NewDirectory("forge-inlay-generator-");

    public void Dispose() => TestTempFiles.DeleteTree(root);

    [Fact]
    public Task A_second_construction_at_the_same_offset_is_refused() => Ui(async () =>
    {
        var (editor, document) = Open(root);
        using var generator = new EditorInlayHints(editor, document);
        generator.SetIndex(Index(new EditorInlayHint(0, 3, "x: int", null, false, false)));

        Assert.Equal(3, generator.GetFirstInterestedOffset(0));
        Assert.NotNull(generator.ConstructElement(3));
        // The library's immediate repeat at the same offset must not build a second zero-length element.
        Assert.Null(generator.ConstructElement(3));
        await Task.CompletedTask;
    });

    [Fact]
    public Task A_later_pass_starting_backwards_still_builds_the_hint() => Ui(async () =>
    {
        var (editor, document) = Open(root);
        using var generator = new EditorInlayHints(editor, document);
        generator.SetIndex(Index(new EditorInlayHint(0, 3, "x: int", null, false, false)));

        // First pass: the hint at offset 3 is built and the guard remembers it.
        Assert.Equal(3, generator.GetFirstInterestedOffset(0));
        Assert.NotNull(generator.ConstructElement(3));

        // A rebuild starts over from the document start (scroll up / width change / fold): the guard must reset, or the hint
        // would be invisible for the rest of the session.
        Assert.Equal(3, generator.GetFirstInterestedOffset(0));
        Assert.NotNull(generator.ConstructElement(3));
        await Task.CompletedTask;
    });

    [Fact]
    public Task A_forward_pass_builds_the_next_line_hint() => Ui(async () =>
    {
        var (editor, document) = Open(root);
        using var generator = new EditorInlayHints(editor, document);
        generator.SetIndex(Index(
            new EditorInlayHint(0, 3, "a: int", null, false, false),
            new EditorInlayHint(1, 7, "b: int", null, false, false)));

        Assert.Equal(3, generator.GetFirstInterestedOffset(0));
        Assert.NotNull(generator.ConstructElement(3));
        // The next line: a bigger offset is a new pass and the second hint must build.
        Assert.Equal(7, generator.GetFirstInterestedOffset(4));
        Assert.NotNull(generator.ConstructElement(7));
        await Task.CompletedTask;
    });

    [Fact]
    public Task An_offset_without_a_hint_builds_nothing() => Ui(async () =>
    {
        var (editor, document) = Open(root);
        using var generator = new EditorInlayHints(editor, document);
        generator.SetIndex(Index(new EditorInlayHint(1, 4, "x: int", null, false, false)));

        Assert.Null(generator.ConstructElement(2));
        await Task.CompletedTask;
    });

    private static (TextEditor Editor, TextDocument Document) Open(string root)
    {
        var path = Path.Combine(root, "Program.cs");
        File.WriteAllText(path, "one\ntwo\nthree\n");
        var editor = new TextEditor { Document = new TextDocument(File.ReadAllText(path)) };
        var window = new Window { Width = 600, Height = 400, Content = editor };
        window.Show();
        WorkbenchUi.Settle(window);
        return (editor, editor.Document);
    }

    private static InlayHintLineIndex Index(params EditorInlayHint[] hints) =>
        InlayHintLineIndex.Build(hints, lineCount: 3, version: 1);

    private static Task Ui(Func<Task> body) => SharedUi.RunAsync(body);
}