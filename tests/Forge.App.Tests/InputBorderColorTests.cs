using Avalonia;
using Avalonia.Controls;
using Forge.App.Appearance;
using Xunit;

namespace Forge.App.Tests;

/// <summary>
/// Form controls take Forge's own line colours instead of Fluent's greys: the divider's hairline at rest, one step deeper
/// under the pointer, the brand tier on focus and on error. The wiring is a set of resource aliases in
/// <c>Resources/Tokens.axaml</c> (<c>TextControlBorderBrush*</c>, <c>ComboBox*</c>, <c>SystemControlErrorTextForegroundBrush</c>),
/// so this test pins the two halves that can silently drift: the alias resolves to the same brush object as the token it
/// names, and an attached control's own border is bound to that alias.
/// </summary>
[Collection("forge-app-ui")]
public sealed class InputBorderColorTests
{
    [Fact]
    public Task A_form_control_draws_the_divider_line() => SharedUi.RunAsync(() =>
    {
        var box = new TextBox();
        var combo = new ComboBox();
        var spinner = new NumericUpDown();
        var window = new Window { Content = new StackPanel { Children = { box, combo, spinner } } };
        window.Show();
        SharedUi.Settle(window);

        var divider = Resource(window, "BorderBrush");
        Assert.Same(divider, box.BorderBrush);
        Assert.Same(divider, combo.BorderBrush);
        Assert.Same(divider, spinner.BorderBrush);

        window.Close();
        return Task.CompletedTask;
    });

    [Fact]
    public Task Every_input_state_points_at_a_forge_token() => SharedUi.RunAsync(() =>
    {
        var window = new Window();
        object Shared(string key) => Resource(window, key);

        Assert.Same(Shared("BorderBrush"), Shared("TextControlBorderBrush"));
        Assert.Same(Shared("BorderStrongBrush"), Shared("TextControlBorderBrushPointerOver"));
        Assert.Same(Shared("BorderBrush"), Shared("TextControlBorderBrushDisabled"));
        Assert.Same(Shared("BrandActionBrush"), Shared("TextControlBorderBrushFocused"));
        Assert.Same(Shared("BrandActionBrush"), Shared("ComboBoxBackgroundBorderBrushFocused"));
        Assert.Same(Shared("BorderBrush"), Shared("ComboBoxBorderBrush"));
        Assert.Same(Shared("BorderStrongBrush"), Shared("ComboBoxBorderBrushPointerOver"));
        Assert.Same(Shared("BorderStrongBrush"), Shared("ComboBoxBorderBrushPressed"));
        Assert.Same(Shared("BorderBrush"), Shared("ComboBoxBorderBrushDisabled"));
        Assert.Same(Shared("StatusErrorBrush"), Shared("SystemControlErrorTextForegroundBrush"));
        return Task.CompletedTask;
    });

    /// <summary>The named resource of the window's current theme; a missing key fails here instead of comparing two nulls.</summary>
    private static object Resource(Visual anchor, string key)
    {
        Assert.True(anchor.TryFindResource(key, anchor.ActualThemeVariant, out var value), "missing resource: " + key);
        Assert.NotNull(value);
        return value;
    }
}
