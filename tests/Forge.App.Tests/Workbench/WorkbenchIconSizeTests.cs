using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Headless;
using Avalonia.VisualTree;
using Forge.App.Workbench.Controls;
using Xunit;

namespace Forge.App.Tests.Workbench;

/// <summary>
/// The workbench's icons come in two sizes only: every panel title-bar tool — cover, pin, minimize, float, close, the
/// header tools and the tab close boxes — is the same 14 px box, and the one deliberate exception is the accent
/// ("primary action") tool a panel may carry, which <see cref="PanelToolButton.IconSize"/> grows. A stray 13 or 15 in a
/// title bar reads as a mistake next to its neighbours, so the rule is pinned here rather than left to review.
/// </summary>
[Collection("forge-app-ui")]
public sealed class WorkbenchIconSizeTests
{
    [Fact]
    public async Task Every_panel_title_bar_icon_is_the_same_size_apart_from_the_accent_tools()
    {
        var session = SharedUi.Session;

        var sizes = await WorkbenchUi.QueryAsync(session, () =>
        {
            var scene = WorkbenchUi.Open(WorkbenchUi.NewLayout());
            WorkbenchUi.Settle(scene.Window);
            return scene.Window.GetVisualDescendants()
                .OfType<PanelIconButton>()
                .Where(static button => button.IsEffectivelyVisible && button.IsEffectivelyEnabled)
                .Select(static button => (button.IconSize, button.IsAccent))
                .ToList();
        });

        Assert.NotEmpty(sizes);
        var plain = sizes.Where(static size => !size.IsAccent).Select(static size => size.IconSize).Distinct().ToList();
        Assert.True(plain.Count == 1,
            "every panel title-bar icon must share one size, found: " + string.Join(", ", plain));
        Assert.Equal(PanelIconButton.DefaultIconSize, plain[0]);

        foreach (var accent in sizes.Where(static size => size.IsAccent))
        {
            Assert.True(accent.IconSize > PanelIconButton.DefaultIconSize,
                "an accent tool is the panel's primary action and must read larger than the chrome icons.");
        }
    }
}
