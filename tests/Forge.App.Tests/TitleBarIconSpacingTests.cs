using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.VisualTree;
using Xunit;

namespace Forge.App.Tests;

/// <summary>
/// The window title row is one cluster of icon buttons - one box, one gap - from the search button to the close button.
/// The defects this pins: the row used to carry two gaps (18 px for search / prompt reload / plan, 2 px for
/// terminal / settings / the caption buttons) and two box widths (30 and 34), so the same row read as two rows with
/// different rhythms (SPEC 控件形态「一行里的图标按钮是一个簇」); the extension host readout used to sit inside that cluster,
/// between the plan button and the terminal, which made a background state read as one of the window's own tools.
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

        // Left to right: the app's own tools, then the terminal, settings and the caption buttons - one cluster, not two
        // groups with their own rhythms. The host readout is not one of them: it lives beside the automations entry.
        var buttons = cluster.Children.OfType<Button>().ToArray();
        Assert.Equal(
            new[] { "shell-search", "prompts-reload", "plan-open", "terminal-open", "open-settings", "window-minimize", "window-maximize", "window-close" },
            buttons.Select(b => AutomationProperties.GetAutomationId(b)));
        Assert.DoesNotContain(
            cluster.Children,
            c => AutomationProperties.GetAutomationId(c) == "host-state");

        foreach (var button in buttons)
        {
            // The caption buttons are not wider targets: one box for the whole row.
            Assert.Equal(30, button.Bounds.Width);
            Assert.Equal(26, button.Bounds.Height);
        }

        // Everything in the cluster is spaced alike, the separator included: 2 px on each side of it.
        var children = cluster.Children.Where(c => c.IsVisible).ToArray();
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
        Assert.Equal(new Avalonia.Size(13, 13), Glyph(buttons[5]).Bounds.Size);
        Assert.Equal(new Avalonia.Size(12, 12), Glyph(buttons[7]).Bounds.Size);

        // One hairline splits the window controls off from the app's own tools: 1 px wide, as tall as the caption glyph
        // plus a pixel above and below, on the row's own centre line, in the region dividers' colour.
        var separator = Assert.IsType<Border>(Assert.Single(children, c => c is Border));
        Assert.Equal("window-controls-separator", AutomationProperties.GetAutomationId(separator));
        Assert.Equal(Array.IndexOf(children, buttons[5]) - 1, Array.IndexOf(children, separator));
        Assert.Equal(1, separator.Bounds.Width);
        Assert.Equal(Glyph(buttons[5]).Bounds.Height + 2, separator.Bounds.Height);
        // Layout rounds to whole device pixels - a 15 px line in a 26 px box starts at 6, not 5.5 - so the row's centre
        // line is matched within half a pixel rather than exactly.
        Assert.True(
            Math.Abs(buttons[5].Bounds.Center.Y - separator.Bounds.Center.Y) <= 0.5,
            $"separator centre is {separator.Bounds.Center.Y}, the caption buttons' is {buttons[5].Bounds.Center.Y}");
        Assert.True(window.TryFindResource("BorderBrush", window.ActualThemeVariant, out var divider), "missing resource: BorderBrush");
        Assert.Same(divider, separator.Background);

        // The host readout sits on the left, immediately before the automations entry, so a background state reads beside
        // the thing that reports it instead of among the window's own tools.
        var left = Assert.IsType<StackPanel>(window.GetVisualDescendants()
            .OfType<Button>()
            .Single(b => AutomationProperties.GetAutomationId(b) == "automations-open")
            .Parent);
        var readout = Assert.IsType<StackPanel>(left.Children[left.Children.Count - 2]);
        Assert.Equal("host-state", AutomationProperties.GetAutomationId(readout));
        Assert.True(readout.Children.OfType<Ellipse>().Any(), "the readout lost its state dot");
        Assert.Equal("FORGE", Assert.IsType<TextBlock>(left.Children[1]).Text);
        Assert.Equal(2, left.Children.IndexOf(readout));

        window.Close();
        return Task.CompletedTask;
    });

    private static PathIcon Glyph(Button button) => button.GetVisualDescendants().OfType<PathIcon>().First();
}
