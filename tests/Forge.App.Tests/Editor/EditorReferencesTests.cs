using System.Globalization;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using Forge.App.Localization;
using Forge.App.Notifications;
using Forge.App.ViewModels;
using Forge.App.Views;
using Forge.Editor.Lsp;
using Xunit;

namespace Forge.App.Tests.Editor;

/// <summary>
/// Pins "find references" (Shift+F12) end to end over the scripted language server: the caret position goes out in the
/// request, the answer becomes one row per reported location with its file name and <c>line:column</c>, an empty answer is
/// the "no references" notice instead of an empty list (written to the editor's status line only, never raised as a
/// notification, which is the slot a real editor failure uses), and Enter on a row of the same file moves the caret to it.
/// </summary>
[Collection("forge-app-ui")]
public sealed class EditorReferencesTests : IDisposable
{
    private const string Source = "class Alpha { }\nAlpha();\n";

    private readonly string root = TestTempFiles.NewDirectory("forge-references-");

    public void Dispose() => TestTempFiles.DeleteTree(root);

    [Fact]
    public Task Shift_F12_lists_one_row_per_location_with_its_file_and_position() => Ui(async () =>
    {
        var other = Path.Combine(root, "Other.cs");
        await File.WriteAllTextAsync(other, "class Q { }\nint i;\nAlpha();\n");
        await using var rig = await EditorLspRig.OpenAsync(root, Source, ReferenceResults(
            Location(Path.Combine(root, "Program.cs"), line: 1), Location(other, line: 2)));

        rig.Editor.CaretOffset = "class ".Length + 2;
        rig.Window.KeyPressQwerty(PhysicalKey.F12, RawInputModifiers.Shift);

        using var request = JsonDocument.Parse(await rig.Server.WaitForAsync("textDocument/references"));
        var parameters = request.RootElement.GetProperty("params");
        Assert.Equal(LspUri.FromPath(rig.Path), parameters.GetProperty("textDocument").GetProperty("uri").GetString());
        Assert.Equal(0, parameters.GetProperty("position").GetProperty("line").GetInt32());
        Assert.Equal("class ".Length + 2, parameters.GetProperty("position").GetProperty("character").GetInt32());
        Assert.True(parameters.GetProperty("context").GetProperty("includeDeclaration").GetBoolean());

        var list = await OpenReferenceListAsync(rig);
        var model = Assert.IsType<ReferenceListViewModel>(list.DataContext);
        Assert.Equal("Alpha", model.SymbolName);
        Assert.Equal(
            [("Program.cs", "2:1"), ("Other.cs", "3:1")],
            model.Items.Select(static row => (row.FileName, row.Location)).ToArray());

        // The list the user sees is bound to those rows.
        var box = Assert.IsType<ListBox>(list.FindControl<ListBox>("ReferenceList"));
        await rig.Until(() => box.ItemCount == 2);
    });

    [Fact]
    public Task An_empty_answer_shows_the_no_references_notice_instead_of_a_list() => Ui(async () =>
    {
        await using var rig = await EditorLspRig.OpenAsync(root, Source, new() { ["textDocument/references"] = "[]" });

        rig.Editor.CaretOffset = "class ".Length + 2;
        rig.Window.KeyPressQwerty(PhysicalKey.F12, RawInputModifiers.Shift);

        await rig.Server.WaitForAsync("textDocument/references");
        await rig.Until(() => rig.Model.LspStatus == Loc.Instance[InspectorStrings.EditorLspReferencesNone]);
        Assert.Empty(rig.Window.GetVisualDescendants().OfType<ReferenceListView>());
    });

    [Fact]
    public Task An_empty_answer_writes_the_status_line_and_raises_no_notification() => Ui(async () =>
    {
        var desktop = new RecordingSystemNotifier();
        // The capture is the real centre: its toast list and the desktop path are both recorded, so either way of notifying
        // the answer would show up here. The post is inline, so a raised toast is in Items before the call returns.
        using var center = new NotificationCenter(() => true, desktop, post: action => action());
        Assert.Same(center, NotificationCenter.Current);
        await using var rig = await EditorLspRig.OpenAsync(root, Source, new() { ["textDocument/references"] = "[]" });
        var none = Loc.Instance[InspectorStrings.EditorLspReferencesNone];

        rig.Editor.CaretOffset = "class ".Length + 2;
        rig.Window.KeyPressQwerty(PhysicalKey.F12, RawInputModifiers.Shift);

        await rig.Server.WaitForAsync("textDocument/references");
        await rig.Until(() => rig.Model.LspStatus == none);
        // "This must not happen": give a notification the same window the status line had to appear.
        await rig.Pump(50);

        Assert.Equal(none, rig.Model.LspStatus);
        Assert.Empty(center.Items);
        Assert.Empty(desktop.Calls);

        // The capture is live: the notifying path of the same view model does raise exactly this warning.
        rig.Model.ReportEditorLspNotice(new LocText(InspectorStrings.EditorLspReferencesNone));
        Assert.Contains(center.Items, item => item.Level == NotificationLevel.Warning && item.Body == none);
        Assert.Empty(desktop.Calls);
    });

    [Fact]
    public Task Enter_on_a_same_file_reference_moves_the_caret_to_it() => Ui(async () =>
    {
        var other = Path.Combine(root, "Other.cs");
        await File.WriteAllTextAsync(other, "class Q { }\n");
        await using var rig = await EditorLspRig.OpenAsync(root, Source, ReferenceResults(
            Location(Path.Combine(root, "Program.cs"), line: 1), Location(other, line: 0)));

        rig.Editor.CaretOffset = "class ".Length + 2;
        rig.Window.KeyPressQwerty(PhysicalKey.F12, RawInputModifiers.Shift);

        var list = await OpenReferenceListAsync(rig);
        var box = Assert.IsType<ListBox>(list.FindControl<ListBox>("ReferenceList"));
        // The own-file reference sorts first, so the first row is the one in the open document.
        Assert.Equal(0, box.SelectedIndex);
        Assert.Equal("Program.cs", Assert.IsType<ReferenceRowViewModel>(box.SelectedItem).FileName);

        rig.Window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);

        // Line 1, column 0 of "class Alpha { }\nAlpha();\n" is the identifier at offset 16.
        await rig.Until(() => rig.Editor.CaretOffset == "class Alpha { }\n".Length);
        Assert.Equal("Alpha", rig.Editor.Document.GetText("class Alpha { }\n".Length, "Alpha".Length));
    });

    /// <summary>Waits for the flyout's list to appear in the window's visual tree (the flyout lives in its overlay layer).</summary>
    private static async Task<ReferenceListView> OpenReferenceListAsync(EditorLspRig rig)
    {
        await rig.Until(() => rig.Window.GetVisualDescendants().OfType<ReferenceListView>().Any());
        var list = rig.Window.GetVisualDescendants().OfType<ReferenceListView>().Single();
        Assert.NotEmpty(Assert.IsType<ReferenceListViewModel>(list.DataContext).Items);
        return list;
    }

    private static Dictionary<string, string> ReferenceResults(params string[] locations) =>
        new() { ["textDocument/references"] = "[" + string.Join(",", locations) + "]" };

    private static string Location(string path, int line) =>
        "{\"uri\":\"" + LspUri.FromPath(path) + "\",\"range\":{\"start\":{\"line\":" + line.ToString(CultureInfo.InvariantCulture) +
        ",\"character\":0},\"end\":{\"line\":" + line.ToString(CultureInfo.InvariantCulture) + ",\"character\":5}}}";

    private static Task Ui(Func<Task> body) => SharedUi.RunAsync(body);

    /// <summary>Records what <see cref="NotificationCenter"/> would have handed to the OS in the background.</summary>
    private sealed class RecordingSystemNotifier : ISystemNotifier
    {
        public List<(string Title, string Body)> Calls { get; } = [];

        public Task NotifyAsync(string title, string body, CancellationToken ct)
        {
            Calls.Add((title, body));
            return Task.CompletedTask;
        }

        public Task<bool> TryNotifyAsync(string title, string body, CancellationToken ct)
        {
            Calls.Add((title, body));
            return Task.FromResult(true);
        }
    }
}
