using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Xunit;

namespace Forge.App.Tests.Workbench;

/// <summary>
/// Built-in form controls take their height, corner radius and font size from the Fluent resource keys
/// <c>Resources/Tokens.axaml</c> overrides — there is no TextBox/ComboBox/CheckBox type style left to drift. Two things
/// are pinned here: the keys themselves (so a later "just this one control" tweak has to argue with a test), and the
/// laid-out result, because the second failure mode is silent — a type selector also reaches the <c>PART_TextBox</c>
/// inside a spinner or an auto-complete box, which used to leave a 28 px box inside a 32 px frame with two different
/// corner radii.
/// </summary>
[Collection("forge-app-ui")]
public sealed class FormControlGeometryTests
{
    /// <summary>One tier for every input: the height a TextBox, ComboBox, NumericUpDown and AutoCompleteBox share.</summary>
    private const double InputHeight = 28;

    /// <summary>The tick/radio row: low enough to sit on a 28 px input's baseline, high enough not to crowd it.</summary>
    private const double ChoiceHeight = 24;

    /// <summary>Fluent's own minimum: what an input box, a dropdown and an overlay frame use.</summary>
    private const double ControlRadius = 3;

    /// <summary>One step rounder than a field, so a button does not read as a square block beside it.</summary>
    private const double ButtonRadius = 5;

    private const double Tolerance = 1;

    [Fact]
    public async Task Fluent_geometry_keys_carry_one_tier_for_every_built_in_control()
    {
        var session = SharedUi.Session;

        var values = await WorkbenchUi.QueryAsync(session, () => new
        {
            ControlRadius = Resource<CornerRadius>("ControlCornerRadius"),
            OverlayRadius = Resource<CornerRadius>("OverlayCornerRadius"),
            ButtonRadius = Resource<CornerRadius>("ButtonCornerRadius"),
            TextHeight = Resource<double>("TextControlThemeMinHeight"),
            ComboHeight = Resource<double>("ComboBoxMinHeight"),
            CheckHeight = Resource<double>("CheckBoxMinHeight"),
            RadioHeight = Resource<double>("RadioButtonMinHeight"),
            FontSize = Resource<double>("ControlContentThemeFontSize"),
        });

        Assert.Equal(ControlRadius, values.ControlRadius.TopLeft);
        Assert.Equal(ControlRadius, values.OverlayRadius.TopLeft);
        Assert.Equal(ButtonRadius, values.ButtonRadius.TopLeft);
        Assert.Equal(InputHeight, values.TextHeight);
        Assert.Equal(InputHeight, values.ComboHeight);
        Assert.Equal(ChoiceHeight, values.CheckHeight);
        Assert.Equal(ChoiceHeight, values.RadioHeight);
        Assert.Equal(13, values.FontSize);
    }

    [Fact]
    public async Task Every_text_input_and_button_in_a_form_lays_out_on_the_same_row_height()
    {
        var session = SharedUi.Session;

        var measured = await WorkbenchUi.QueryAsync(session, () =>
        {
            var text = new TextBox { Text = "feature/fluent-theme" };
            var combo = new ComboBox { ItemsSource = new[] { "rebase", "merge" }, SelectedIndex = 0 };
            var spinner = new NumericUpDown { Value = 60 };
            var autoComplete = new AutoCompleteBox { Text = "Segoe UI" };
            var check = new CheckBox { Content = "commit each step" };
            var radio = new RadioButton { Content = "rebase" };
            var primary = new Button { Content = "Create", Classes = { "primary" } };
            var ghost = new Button { Content = "Cancel", Classes = { "ghost" } };

            var page = new StackPanel
            {
                Spacing = 6,
                Children = { text, combo, spinner, autoComplete, check, radio, primary, ghost },
            };

            var window = new Window { Width = 520, Height = 400, Content = page };
            window.Show();
            WorkbenchUi.Settle(window);

            return new Measured(
                text.Bounds.Height, combo.Bounds.Height, spinner.Bounds.Height, autoComplete.Bounds.Height,
                check.Bounds.Height, radio.Bounds.Height, primary.Bounds.Height, ghost.Bounds.Height,
                text.CornerRadius.TopLeft, combo.CornerRadius.TopLeft, spinner.CornerRadius.TopLeft,
                primary.CornerRadius.TopLeft, ghost.CornerRadius.TopLeft,
                text.FontSize, combo.FontSize, spinner.FontSize, autoComplete.FontSize);
        });

        Assert.Equal(InputHeight, measured.Text, Tolerance);
        Assert.Equal(InputHeight, measured.Combo, Tolerance);
        Assert.Equal(InputHeight, measured.Spinner, Tolerance);
        Assert.Equal(InputHeight, measured.AutoComplete, Tolerance);
        Assert.Equal(ChoiceHeight, measured.Check, Tolerance);
        Assert.Equal(ChoiceHeight, measured.Radio, Tolerance);

        foreach (var height in new[] { measured.Primary, measured.Ghost })
        {
            Assert.InRange(height, InputHeight - Tolerance, InputHeight + 2 + Tolerance);
        }

        Assert.Equal(ControlRadius, measured.TextRadius);
        Assert.Equal(ControlRadius, measured.ComboRadius);
        Assert.Equal(ControlRadius, measured.SpinnerRadius);

        // Forge's own button classes share one key of their own, so no two buttons can drift apart from each other.
        Assert.Equal(ButtonRadius, measured.PrimaryRadius);
        Assert.Equal(ButtonRadius, measured.GhostRadius);

        Assert.Equal(13, measured.TextFontSize);
        Assert.Equal(13, measured.ComboFontSize);
        Assert.Equal(13, measured.SpinnerFontSize);
        Assert.Equal(13, measured.AutoCompleteFontSize);
    }

    [Fact]
    public async Task A_spinner_holds_one_text_box_that_fills_its_frame()
    {
        var session = SharedUi.Session;

        var pair = await WorkbenchUi.QueryAsync(session, () =>
        {
            var spinner = new NumericUpDown { Value = 60 };
            var window = new Window { Width = 320, Height = 200, Content = spinner };
            window.Show();
            WorkbenchUi.Settle(window);

            var inner = spinner.GetVisualDescendants().OfType<TextBox>().FirstOrDefault();
            Assert.NotNull(inner);
            return (Outer: spinner.Bounds.Height, Inner: inner!.Bounds.Height);
        });

        // The template's own `Margin="-1"` exists so the inner box covers the frame's border: anything shorter than the
        // frame leaves the glyphs sitting high inside a spinner that is drawn a different height.
        Assert.InRange(pair.Inner, pair.Outer - Tolerance, pair.Outer + Tolerance);
    }

    private static T Resource<T>(string key)
    {
        Assert.True(Application.Current!.TryGetResource(key, ThemeVariant.Default, out var value),
            "missing resource: " + key);
        Assert.NotNull(value);
        return Assert.IsType<T>(value);
    }

    private sealed record Measured(
        double Text,
        double Combo,
        double Spinner,
        double AutoComplete,
        double Check,
        double Radio,
        double Primary,
        double Ghost,
        double TextRadius,
        double ComboRadius,
        double SpinnerRadius,
        double PrimaryRadius,
        double GhostRadius,
        double TextFontSize,
        double ComboFontSize,
        double SpinnerFontSize,
        double AutoCompleteFontSize);
}
