using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.VisualTree;
using Xunit;

namespace Forge.App.Tests;

/// <summary>
/// The window title row is one cluster of icon buttons - one box, one gap - from the search button to the close button.
/// The defect this pins: the row used to carry two gaps (18 px for search / prompt reload / plan, 2 px for
/// terminal / settings / the caption buttons) and two box widths (30 and 34), so the same row read as two rows with
/// different rhythms. SPEC 控件形态「一行里的图标按钮是一个簇」.
/// </summary>
[Collection("forge-app-ui")]
public sealed class TitleBarIconSpacingTests
{
    [Fact]
    public Task The_title_row_is_one_cluster_with_one_box_and_one_gap() => SharedUi.RunAsync(() =>
    {
        var window = new MainWindow();
        window.Show();
        SharedUi.Settle(window);

        var cluster = window.GetVisualDescendants()
            .OfType<StackPanel>()
            .Single(p => p.Children.OfType<Button>().Any(b => b.Name == "CloseButton"));

        // Left to right: the app's own tools, the host readout, then the terminal, settings and the caption buttons -
        // one cluster, not two groups with their own rhythms.
        var buttons = cluster.Children.OfType<Button>().ToArray();
        Assert.Equal(
            new[] { "shell-search", "prompts-reload", "plan-open", "terminal-open", "open-settings", "window-minimize", "window-maximize", "window-close" },
            buttons.Select(b => AutomationProperties.GetAutomationId(b)));

        foreach (var button in buttons)
        {
            // The caption buttons are not wider targets: one box for the whole row.
            Assert.Equal(30, button.Bounds.Width);
            Assert.Equal(26, button.Bounds.Height);
        }

        // The readout between the plan button and the terminal is spaced like the buttons around it.
        var children = cluster.Children.Where(c => c.IsVisible).ToArray();
        Assert.Contains(children, c => c is StackPanel panel && panel.Children.OfType<Ellipse>().Any());
        for (var i = 1; i < children.Length; i++)
        {
            var previous = children[i - 1];
            var gap = children[i].Bounds.X - (previous.Bounds.X + previous.Bounds.Width);
            Assert.True(
                Math.Abs(gap - 2) < 0.01,
                $"gap before '{children[i].Name ?? children[i].GetType().Name}' is {gap}, expected 2");
        }

        // The glyph tiers of the title row: the app's own tools 15 px, the window controls 13 and the close 12.
        Assert.Equal(15, Glyph(buttons[1]).Bounds.Width);
        Assert.Equal(13, Glyph(buttons[5]).Bounds.Width);
        Assert.Equal(12, Glyph(buttons[7]).Bounds.Width);

        window.Close();
        return Task.CompletedTask;
    });

    private static PathIcon Glyph(Button button) => button.GetVisualDescendants().OfType<PathIcon>().First();
}
