using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.VisualTree;
using Forge.App.Workbench;
using Forge.App.Workbench.Controls;
using Xunit;

namespace Forge.App.Tests.Workbench;

/// <summary>
/// A preview tab whose content is wider than its column (a file with long lines) stays inside the preview column: its
/// right edge ends where the files column begins, docked or in the Inspector's drawer, so no text runs under the files
/// panel.
/// </summary>
public sealed class PreviewColumnBoundsTests
{
    private const double Tolerance = 1;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Wide_preview_content_ends_at_the_files_column(bool drawer)
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));

        var bounds = await WorkbenchUi.QueryAsync(session, () =>
        {
            var layout = WorkbenchUi.NewLayout();
            var scene = WorkbenchUi.Open(layout, 1600);
            if (drawer)
            {
                layout.SetAutoHidden(RegionRole.Inspector, true);
                layout.ToggleDrawer(RegionRole.Inspector);
                WorkbenchUi.Settle(scene.Window);
            }

            var wide = new TextBlock
            {
                Text = new string('x', 2000),
                TextWrapping = TextWrapping.NoWrap,
                FontFamily = FontFamily.Default,
            };
            layout.OpenTab(layout.PreviewTabs, WorkbenchIds.PreviewTab, TabKind.FilePreview, wide, "file");
            WorkbenchUi.Settle(scene.Window);
            var preview = ChromeOf(scene.Window, layout.PreviewTabs);
            var files = ChromeOf(scene.Window, layout.FilesPanel);

            var content = wide.TranslatePoint(new Point(wide.Bounds.Width, 0), scene.Window)!.Value.X;
            return (
                PreviewRight: preview.TranslatePoint(new Point(preview.Bounds.Width, 0), scene.Window)!.Value.X,
                ContentRight: content,
                FilesLeft: files.TranslatePoint(default, scene.Window)!.Value.X);
        });

        Assert.True(bounds.PreviewRight <= bounds.FilesLeft + Tolerance, $"preview {bounds.PreviewRight} > files {bounds.FilesLeft}");
        Assert.True(bounds.ContentRight <= bounds.PreviewRight + Tolerance, $"content {bounds.ContentRight} > preview {bounds.PreviewRight}");
    }

    private static PanelChrome ChromeOf(Window window, PanelModel model) =>
        window.GetVisualDescendants().OfType<PanelChrome>().Single(chrome => ReferenceEquals(chrome.Model, model) && chrome.IsVisible);
}
