using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Forge.App.Views;
using Xunit;

namespace Forge.App.Tests;

/// <summary>
/// Where a tooltip opens. Centred on its control one gap below it (growing to both sides), above it when the window leaves no
/// room below, and - only when it fits neither - out of the control's side, growing away from the control. What it never does
/// is drift away from the thing it explains, which is what the pointer anchor did: 20 px under the cursor, then slid a whole
/// tip-width sideways whenever a window edge got in the way.
/// </summary>
[Collection("forge-app-ui")]
public sealed class ToolTipPlacementTests
{
    [Fact]
    public Task A_tooltip_hangs_below_its_control_centred_on_it() => SharedUi.RunAsync(() =>
    {
        var (window, button, tip, position) = OpenTip(left: 200, top: 60, windowHeight: 300, tipWidth: 200, tipHeight: 40);

        var anchor = button.TranslatePoint(default, window)!.Value;
        Assert.Equal(anchor.Y + button.Bounds.Height + ToolTipPlacement.Gap, position.Y, 1);
        Assert.Equal(anchor.X + button.Bounds.Width / 2, position.X + tip.Bounds.Width / 2, 1);

        window.Close();
        return Task.CompletedTask;
    });

    [Fact]
    public Task A_tooltip_with_no_room_below_sits_above_its_control_centred() => SharedUi.RunAsync(() =>
    {
        var (window, button, tip, position) = OpenTip(left: 200, top: 240, windowHeight: 300, tipWidth: 200, tipHeight: 40);

        var anchor = button.TranslatePoint(default, window)!.Value;
        Assert.Equal(anchor.Y - ToolTipPlacement.Gap, position.Y + tip.Bounds.Height, 1);
        Assert.Equal(anchor.X + button.Bounds.Width / 2, position.X + tip.Bounds.Width / 2, 1);

        window.Close();
        return Task.CompletedTask;
    });

    [Fact]
    public Task A_tooltip_too_tall_for_either_side_grows_out_of_the_right_of_its_control() => SharedUi.RunAsync(() =>
    {
        // 100 px of tip in a 120 px window: there is no room above or below, so it leaves through the right edge and grows
        // rightwards, vertically centred on the control.
        var (window, button, tip, position) = OpenTip(left: 8, top: 50, windowHeight: 120, tipWidth: 200, tipHeight: 100);

        var anchor = button.TranslatePoint(default, window)!.Value;
        Assert.Equal(anchor.X + button.Bounds.Width + ToolTipPlacement.Gap, position.X, 1);
        Assert.Equal(anchor.Y + button.Bounds.Height / 2, position.Y + tip.Bounds.Height / 2, 1);

        window.Close();
        return Task.CompletedTask;
    });

    [Fact]
    public Task A_tooltip_with_more_room_on_the_left_grows_out_of_the_left_of_its_control() => SharedUi.RunAsync(() =>
    {
        var (window, button, tip, position) = OpenTip(left: 340, top: 50, windowHeight: 120, tipWidth: 200, tipHeight: 100);

        var anchor = button.TranslatePoint(default, window)!.Value;
        Assert.Equal(anchor.X - ToolTipPlacement.Gap, position.X + tip.Bounds.Width, 1);
        Assert.Equal(anchor.Y + button.Bounds.Height / 2, position.Y + tip.Bounds.Height / 2, 1);

        window.Close();
        return Task.CompletedTask;
    });

    /// <summary>
    /// One window whose tooltip is opened by hand (no pointer, no delay): the tip is a fixed-size block, so the assertion can
    /// name the rectangle it must land on instead of depending on text measurement. It is shown in the window's overlay layer,
    /// where its position can be read as coordinates of that window.
    /// </summary>
    private static (Window Window, Button Button, ToolTip Tip, Point Position) OpenTip(
        double left,
        double top,
        double windowHeight,
        double tipWidth,
        double tipHeight)
    {
        var button = new Button { Content = "attach", Width = 26, Height = 26 };
        ToolTip.SetTip(button, new Border
        {
            Width = tipWidth,
            Height = tipHeight,
            Child = new TextBlock { Text = "the tip" },
        });
        ToolTip.SetShouldUseOverlayLayer(button, true);

        var canvas = new Canvas();
        Canvas.SetLeft(button, left);
        Canvas.SetTop(button, top);
        canvas.Children.Add(button);

        var window = new Window { Width = 400, Height = windowHeight, Content = canvas };
        window.Show();
        SharedUi.Settle(window);

        ToolTip.SetIsOpen(button, true);
        SharedUi.Settle(window);

        // The default anchor is the bug: the app's placement must have replaced it before the popup opened.
        Assert.Equal(PlacementMode.Custom, ToolTip.GetPlacement(button));
        Assert.NotNull(ToolTip.GetCustomPopupPlacementCallback(button));

        // The popup's child is the ToolTip control itself, so its own rectangle is the one the placement computes.
        var tip = window.GetVisualDescendants().OfType<ToolTip>().Single();
        var position = tip.TranslatePoint(default, window)
            ?? throw new InvalidOperationException("the tooltip is not hosted in the window's overlay layer");
        return (window, button, tip, position);
    }
}
