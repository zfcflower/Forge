using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using Forge.App.ViewModels;
using Forge.App.Workbench.Controls;
using Xunit;

namespace Forge.App.Tests;

/// <summary>
/// A chevron in the chrome points right while its row is closed and down once the row is open. The turn belongs to the icon
/// (<see cref="FluentIcon.Rotated"/>): a style-set <c>RenderTransform</c> never reaches a <c>PathIcon</c>, so the
/// <c>tree-chevron</c> styles' rotate was dropped and every chevron — the reasoning row's included — stayed pointing right.
/// The assertion is the direction the glyph's right edge ends up pointing, which is the thing a reader sees.
/// </summary>
[Collection("forge-app-ui")]
public sealed class ExpandedChevronTests
{
    [Fact]
    public Task The_reasoning_row_turns_its_chevron_as_the_row_opens_and_closes() => SharedUi.RunAsync(() =>
    {
        var reasoning = new TurnThinkingViewModel();
        reasoning.Append("the first line of thought");

        // Rendered through the app's own TurnThinkingViewModel template, exactly as the transcript renders the row.
        var row = new ContentControl { Content = reasoning };
        var window = new Window { Width = 600, Height = 400, Content = row };
        window.Show();
        SharedUi.Settle(window);

        var chevron = row.GetVisualDescendants().OfType<FluentIcon>().Single(e => e.Classes.Contains("tree-chevron"));
        var toggle = row.GetVisualDescendants()
            .OfType<Button>()
            .Single(b => AutomationProperties.GetAutomationId(b) == "thinking-toggle");

        var closed = Direction(chevron);
        Assert.Equal(1, closed.X, 3);
        Assert.Equal(0, closed.Y, 3);

        Click(window, toggle);
        Assert.True(reasoning.IsExpanded);
        var open = Direction(chevron);
        Assert.Equal(0, open.X, 3);
        Assert.Equal(1, open.Y, 3);

        Click(window, toggle);
        Assert.False(reasoning.IsExpanded);
        var closedAgain = Direction(chevron);
        Assert.Equal(1, closedAgain.X, 3);
        Assert.Equal(0, closedAgain.Y, 3);
        return Task.CompletedTask;
    });

    /// <summary>Clicks the middle of the row's toggle: the way the row is opened and closed by hand.</summary>
    private static void Click(Window window, Button button)
    {
        var middle = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value;
        window.MouseDown(middle, MouseButton.Left);
        window.MouseUp(middle, MouseButton.Left);
        SharedUi.Settle(window);
    }

    /// <summary>Where the glyph's right edge points once the row's transform is applied: right is (1,0), down is (0,1).</summary>
    private static Point Direction(FluentIcon chevron) =>
        (chevron.RenderTransform?.Value ?? Matrix.Identity).Transform(new Point(1, 0));
}
