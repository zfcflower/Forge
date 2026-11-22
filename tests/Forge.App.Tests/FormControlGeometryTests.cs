using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.VisualTree;
using Xunit;

namespace Forge.App.Tests;

/// <summary>
/// The two geometry tables of the module SPEC's 「控件形态」: every field is 28 px high on a 3 px radius, every button is
/// 32 px (medium) or 26 px (small) on a 5 px radius, and a state change swaps the fill or the line - never the size. The
/// checks below pin the numbers the prototypes draw, so a hand-written `Padding` or a reordered style block cannot move a
/// control a pixel without this failing.
/// </summary>
[Collection("forge-app-ui")]
public sealed class FormControlGeometryTests
{
    [Fact]
    public Task A_field_is_one_geometry_with_forge_fills() => SharedUi.RunAsync(() =>
    {
        var box = new TextBox { Text = "abc", PlaceholderText = "输入…" };
        var multi = new TextBox { AcceptsReturn = true, Classes = { "multiline" } };
        var combo = new ComboBox { ItemsSource = new[] { "a", "b" }, SelectedIndex = 0 };
        var spinner = new NumericUpDown { Value = 1 };
        var window = Pane(new StackPanel { Children = { box, multi, combo, spinner } });

        Assert.Equal(28, box.Bounds.Height);
        Assert.Equal(new Thickness(10, 4), box.Padding);
        Assert.Equal(new CornerRadius(3), box.CornerRadius);
        Assert.Equal(13, box.FontSize);
        Assert.Same(Resource(window, "SurfaceInputBrush"), box.Background);
        Assert.Same(Resource(window, "TextPrimaryBrush"), box.Foreground);
        Assert.Same(Resource(window, "TextMutedBrush"), box.PlaceholderForeground);
        Assert.Same(Resource(window, "BorderBrush"), box.BorderBrush);

        // The multi-line field trades the one-line padding for the textarea's, and starts at 64 px.
        Assert.Equal(64, multi.Bounds.Height);
        Assert.Equal(new Thickness(8, 10), multi.Padding);

        Assert.Equal(28, combo.Bounds.Height);
        Assert.Equal(new Thickness(12, 4, 0, 4), combo.Padding);
        Assert.Equal(new CornerRadius(3), combo.CornerRadius);
        Assert.Same(Resource(window, "TextPrimaryBrush"), combo.Foreground);

        Assert.Equal(28, spinner.Bounds.Height);
        Assert.Same(Resource(window, "SurfaceInputBrush"), spinner.Background);

        window.Close();
        return Task.CompletedTask;
    });

    [Fact]
    public Task A_field_keeps_its_line_through_hover_focus_and_error() => SharedUi.RunAsync(() =>
    {
        var box = new TextBox { Text = "abc" };
        var invalid = new TextBox { Text = "x", Classes = { "invalid" } };
        var window = Pane(new StackPanel { Children = { box, invalid } });
        var frame = Frame(box);
        var edge = Edge(box);

        // The frame is a hairline on three sides; the fourth side is the edge element, set apart from the field's bottom.
        Assert.Equal(new Thickness(1, 1, 1, 0), frame.BorderThickness);
        Assert.Same(Resource(window, "BorderBrush"), frame.BorderBrush);
        Assert.Same(Resource(window, "FieldEdgeBrush"), edge.Background);
        Assert.Equal(1, edge.Bounds.Height);
        Assert.Equal(new CornerRadius(0, 0, 3, 3), edge.CornerRadius);
        Assert.Equal(edge.Bounds.Y + frame.Bounds.Y + 1, frame.Bounds.Height);

        ((IPseudoClasses)box.Classes).Set(":pointerover", true);
        SharedUi.Settle(window);
        Assert.Same(Resource(window, "BorderStrongBrush"), frame.BorderBrush);

        ((IPseudoClasses)box.Classes).Set(":pointerover", false);
        box.Focus();
        SharedUi.Settle(window);
        // Focus thickens only the bottom edge; the top and the sides stay on the divider's colour.
        Assert.Same(Resource(window, "BorderBrush"), frame.BorderBrush);
        Assert.Same(Resource(window, "BrandActionBrush"), edge.Background);
        Assert.Equal(2, edge.Bounds.Height);

        // An error paints both parts red, focused or not.
        Assert.Same(Resource(window, "StatusErrorBrush"), Frame(invalid).BorderBrush);
        Assert.Same(Resource(window, "StatusErrorBrush"), Edge(invalid).Background);

        window.Close();
        return Task.CompletedTask;
    });

    [Fact]
    public Task A_field_still_edits_text_through_the_forge_template() => SharedUi.RunAsync(() =>
    {
        var box = new TextBox { Width = 200, PlaceholderText = "输入…" };
        var secret = new TextBox { Width = 200, PasswordChar = '•' };
        var multi = new TextBox { Width = 200, AcceptsReturn = true, Classes = { "multiline" } };
        var window = Pane(new StackPanel { Children = { box, secret, multi } });

        // The presenter is the editor surface: it has to see the text, the caret, the selection and the mask.
        box.Text = "abc";
        box.CaretIndex = 2;
        box.SelectionStart = 1;
        box.SelectionEnd = 3;
        SharedUi.Settle(window);
        var presenter = box.GetVisualDescendants().OfType<TextPresenter>().Single(p => p.Name == "PART_TextPresenter");
        Assert.Equal("abc", presenter.Text);
        Assert.Equal(2, presenter.CaretIndex);
        Assert.Equal(1, presenter.SelectionStart);
        Assert.Equal(3, presenter.SelectionEnd);
        Assert.Equal('•', secret.GetVisualDescendants().OfType<TextPresenter>().Single(p => p.Name == "PART_TextPresenter").PasswordChar);

        // Typed text hides the placeholder, an empty box shows it again.
        var placeholder = box.GetVisualDescendants().OfType<TextBlock>().Single(p => p.Name == "PART_Placeholder");
        Assert.False(placeholder.IsVisible);
        box.Text = string.Empty;
        SharedUi.Settle(window);
        Assert.True(placeholder.IsVisible);

        // The scrolling host carries the control's padding, and a multi-line box keeps its own height.
        var scroller = box.GetVisualDescendants().OfType<ScrollViewer>().Single(v => v.Name == "PART_ScrollViewer");
        Assert.True(ScrollViewer.GetVerticalScrollBarVisibility(scroller) == Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);
        Assert.Equal(64, multi.Bounds.Height);

        window.Close();
        return Task.CompletedTask;
    });

    [Fact]
    public Task A_dropdown_and_a_number_field_share_the_same_frame() => SharedUi.RunAsync(() =>
    {
        var combo = new ComboBox { ItemsSource = new[] { "a", "b" }, SelectedIndex = 0, Width = 200 };
        var spinner = new NumericUpDown { Value = 10, Width = 200 };
        var window = Pane(new StackPanel { Children = { combo, spinner } });

        var comboTarget = combo.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "Background");
        var comboEdge = combo.GetVisualDescendants().OfType<Border>().First(b => b.Name == "PART_EdgeLine");
        Assert.Equal(new Thickness(1, 1, 1, 0), comboTarget.BorderThickness);
        Assert.Same(Resource(window, "FieldEdgeBrush"), comboEdge.Background);
        Assert.Equal(1, comboEdge.Bounds.Height);

        // Focus is Fluent's 1 px brand ring on the dropdown, so its edge steps aside instead of doubling the line.
        ((IPseudoClasses)combo.Classes).Set(":focus-visible", true);
        SharedUi.Settle(window);
        Assert.False(comboEdge.IsVisible);
        Assert.True(combo.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "HighlightBackground").IsVisible);
        ((IPseudoClasses)combo.Classes).Set(":focus-visible", false);

        // The number field: its own text box draws no frame, the stepper's edge carries the state.
        var spinnerEdge = spinner.GetVisualDescendants().OfType<Border>().First(b => b.Name == "PART_EdgeLine");
        Assert.Same(Resource(window, "FieldEdgeBrush"), spinnerEdge.Background);
        var inner = spinner.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "PART_TextBox");
        Assert.False(inner.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "PART_EdgeLine").IsVisible);
        // The inner box draws no frame of its own, so the stepper's frame and edge are the whole field - one control.
        Assert.False(inner.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "PART_BorderElement").IsVisible);

        // (the stepper's focus edge rides on :focus-within, which a headless window does not raise; the plain field
        // above covers the focused band.)

        // The dropdown still opens, and the frame stays out of its way.
        combo.IsDropDownOpen = true;
        SharedUi.Settle(window);
        Assert.True(combo.IsDropDownOpen);
        Assert.Equal("a", combo.SelectedItem);

        window.Close();
        return Task.CompletedTask;
    });

    [Fact]
    public Task A_button_has_two_geometries_and_four_palettes() => SharedUi.RunAsync(() =>
    {
        var buttons = new Dictionary<string, Button>();
        foreach (var name in new[] { "ghost", "outline", "flat", "tinted", "primary", "icon" })
        {
            // The icon tier carries a glyph, not a label, so its box stays the 26 px square its min sizes promise.
            Control content = name == "icon" ? new Border { Width = 14, Height = 14 } : new TextBlock { Text = "按钮" };
            var button = new Button { Content = content };
            button.Classes.Add(name);
            buttons[name] = button;
        }

        var stack = new StackPanel();
        foreach (var button in buttons.Values)
        {
            stack.Children.Add(button);
        }

        var window = Pane(stack);
        var radius = (CornerRadius)Resource(window, "ButtonCornerRadius");

        foreach (var name in new[] { "ghost", "primary" })
        {
            Assert.Equal(32, buttons[name].Bounds.Height);
            Assert.Equal(new Thickness(12, 4), buttons[name].Padding);
            Assert.Equal(14, buttons[name].FontSize);
            Assert.Equal(radius, buttons[name].CornerRadius);
        }

        foreach (var name in new[] { "outline", "flat", "tinted" })
        {
            Assert.Equal(26, buttons[name].Bounds.Height);
            Assert.Equal(new Thickness(8, 3), buttons[name].Padding);
            Assert.Equal(12, buttons[name].FontSize);
            Assert.Equal(radius, buttons[name].CornerRadius);
        }

        // The icon button is its own shape: a 26 px box with the small radius and no frame.
        Assert.Equal(26, buttons["icon"].Bounds.Width);
        Assert.Equal(26, buttons["icon"].Bounds.Height);
        Assert.Equal(new CornerRadius(4), buttons["icon"].CornerRadius);
        Assert.Equal(new Thickness(0), buttons["icon"].BorderThickness);

        window.Close();
        return Task.CompletedTask;
    });

    [Fact]
    public Task A_button_state_changes_only_its_fill() => SharedUi.RunAsync(() =>
    {
        var buttons = new Dictionary<string, Button>();
        foreach (var name in new[] { "ghost", "outline", "flat", "tinted", "primary" })
        {
            var button = new Button { Content = "按钮" };
            button.Classes.Add(name);
            buttons[name] = button;
        }

        var stack = new StackPanel();
        foreach (var button in buttons.Values)
        {
            stack.Children.Add(button);
        }

        var window = Pane(stack);

        void AssertStates(string name, string hover, string pressed)
        {
            var presenter = buttons[name].GetVisualDescendants().OfType<ContentPresenter>()
                .First(p => p.Name == "PART_ContentPresenter");
            ((IPseudoClasses)buttons[name].Classes).Set(":pointerover", true);
            SharedUi.Settle(window);
            Assert.Same(Resource(window, hover), presenter.Background);
            ((IPseudoClasses)buttons[name].Classes).Set(":pointerover", false);
            ((IPseudoClasses)buttons[name].Classes).Set(":pressed", true);
            SharedUi.Settle(window);
            Assert.Same(Resource(window, pressed), presenter.Background);
            ((IPseudoClasses)buttons[name].Classes).Set(":pressed", false);
            SharedUi.Settle(window);
        }

        // The two neutral tiers borrow the two greys, the brand tier its own three stops, the tinted tier the selection.
        AssertStates("ghost", "SurfaceHoverBrush", "IconButtonHoverBrush");
        AssertStates("outline", "SurfaceHoverBrush", "IconButtonHoverBrush");
        AssertStates("flat", "SurfaceHoverBrush", "IconButtonHoverBrush");
        AssertStates("primary", "BrandHoverBrush", "BrandPressedBrush");
        AssertStates("tinted", "SelectionBackgroundBrush", "SelectionBackgroundBrush");

        window.Close();
        return Task.CompletedTask;
    });


    [Fact]
    public Task A_number_field_stacks_its_chevrons_and_still_spins() => SharedUi.RunAsync(() =>
    {
        var spinner = new NumericUpDown { Value = 10, Width = 220 };
        var window = Pane(new StackPanel { Children = { spinner } });

        var increase = spinner.GetVisualDescendants().OfType<RepeatButton>().Single(b => b.Name == "PART_IncreaseButton");
        var decrease = spinner.GetVisualDescendants().OfType<RepeatButton>().Single(b => b.Name == "PART_DecreaseButton");

        // Stacked at the trailing edge, level with the field's height, with no rule and no fill of their own.
        Assert.Equal(increase.Bounds.Width, decrease.Bounds.Width);
        Assert.Equal(16, increase.Bounds.Width);
        Assert.Equal(12, increase.Bounds.Height);
        Assert.Equal(increase.Bounds.Y + 14, decrease.Bounds.Y);
        Assert.Same(Resource(window, "TextMutedBrush"), increase.GetVisualDescendants().OfType<PathIcon>().Single().Foreground);
        Assert.Equal(new Size(10, 6), increase.GetVisualDescendants().OfType<PathIcon>().Single().Bounds.Size);
        Assert.Equal(new Thickness(0), increase.BorderThickness);
        Assert.Equal(Colors.Transparent, Assert.IsAssignableFrom<ISolidColorBrush>(increase.Background).Color);

        // The theme draws the buttons itself, so the spinner has to find them by name and wire the clicks up.
        increase.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(11m, spinner.Value);

        window.Close();
        return Task.CompletedTask;
    });

    /// <summary>A shown window carrying the same geometry sheets a settings page gets (ChromeStyles is per-subtree).</summary>
    private static Window Pane(Control content)
    {
        var window = new Window { Width = 640, Height = 480, Content = content };
        window.Styles.Add(new StyleInclude(new Uri("avares://Forge.App/"))
        {
            Source = new Uri("avares://Forge.App/Resources/ChromeStyles.axaml"),
        });
        window.Show();
        SharedUi.Settle(window);
        return window;
    }

    /// <summary>The template's frame: the matched style rules land on it, not on the TextBox's own properties.</summary>
    private static Border Frame(TextBox box) =>
        box.GetVisualDescendants().OfType<Border>().First(b => b.Name == "PART_BorderElement");

    /// <summary>The field's bottom edge: its own element, because one Border cannot paint four edges in two colours.</summary>
    private static Border Edge(TextBox box) =>
        box.GetVisualDescendants().OfType<Border>().First(b => b.Name == "PART_EdgeLine");

    /// <summary>The named resource of the window's current theme; a missing key fails here instead of comparing two nulls.</summary>
    private static object Resource(Visual anchor, string key)
    {
        Assert.True(anchor.TryFindResource(key, anchor.ActualThemeVariant, out var value), "missing resource: " + key);
        Assert.NotNull(value);
        return value;
    }
}
