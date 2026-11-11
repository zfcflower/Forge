using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.VisualTree;
using Forge.App.ViewModels;
using Forge.App.Workbench;
using Forge.App.Workbench.Controls;
using Xunit;

namespace Forge.App.Tests.Workbench;

/// <summary>
/// A tab's leading glyph follows its kind: a conversation wears the chat bubble (filled while it is the active tab of its
/// group), and a file preview wears the very same document geometry the file tree draws in front of the file's name — one
/// object, so the two can never drift into different drawings. Kinds that spell themselves out in the title reserve no
/// icon slot.
/// </summary>
[Collection("forge-app-ui")]
public sealed class WorkbenchTabIconTests
{
    [Fact]
    public async Task A_file_preview_tab_leads_with_the_document_the_file_tree_draws()
    {
        var session = SharedUi.Session;

        var state = await WorkbenchUi.QueryAsync(session, () =>
        {
            var scene = WorkbenchUi.Open(WorkbenchUi.NewLayout());
            scene.Layout.OpenTab(
                scene.Layout.PreviewTabs, "file:probe.cs", TabKind.FilePreview, new Border(), "probe.cs");
            WorkbenchUi.Settle(scene.Window);

            var header = scene.Window.GetVisualDescendants()
                .OfType<TabHeaderView>()
                .Single(view => view.Model?.Kind == TabKind.FilePreview);
            // The close box is a PanelIconButton and names its own icon PART_Icon too, so skip anything inside one.
            var icon = header.GetVisualDescendants()
                .OfType<FluentIcon>()
                .First(view => !view.GetVisualAncestors().OfType<PanelIconButton>().Any());
            return (SameAsTree: ReferenceEquals(icon.Data, TreeIcons.File),
                Control: ReferenceEquals(header.Icon, TreeIcons.File),
                Visible: icon.IsEffectivelyVisible,
                Size: icon.Bounds.Width);
        });

        Assert.True(state.SameAsTree,
            "a file preview tab must draw the file tree's own document geometry, not a second drawing of it.");
        Assert.True(state.Control, "the tab header must carry the file tree's document geometry.");
        Assert.True(state.Visible, "the file preview tab's icon must be shown.");
        Assert.Equal(14, state.Size);
    }

    [Fact]
    public async Task A_session_tab_keeps_its_chat_bubble_and_fills_it_while_active()
    {
        var session = SharedUi.Session;

        var state = await WorkbenchUi.QueryAsync(session, () =>
        {
            var scene = WorkbenchUi.Open(WorkbenchUi.NewLayout());
            WorkbenchUi.OpenSessionTab(scene.Layout, "s1");
            WorkbenchUi.OpenSessionTab(scene.Layout, "s2");
            WorkbenchUi.Settle(scene.Window);

            var outline = WorkbenchIcons.Find(scene.Window, "IconChat");
            var solid = WorkbenchIcons.Find(scene.Window, "IconChatFilled");
            var icons = scene.Window.GetVisualDescendants()
                .OfType<TabHeaderView>()
                .Where(view => view.Model?.Kind == TabKind.Session)
                .Select(view => (view.IsActive, view.Icon))
                .ToList();

            return (Icons: icons, Outline: outline, Solid: solid);
        });

        Assert.NotEmpty(state.Icons);
        foreach (var (isActive, icon) in state.Icons)
        {
            Assert.Same(isActive ? state.Solid : state.Outline, icon);
        }
    }
}
