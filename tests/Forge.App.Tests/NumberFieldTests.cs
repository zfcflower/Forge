using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Xunit;
using TextBox = Avalonia.Controls.TextBox;

namespace Forge.App.Tests;

/// <summary>
/// Every number in the app is typed into a <see cref="NumericUpDown"/>, and that field only takes text that reads as a
/// number. The stock control is happy with prose: type <c>abc</c> over <c>200000</c> and the box keeps the letters while the
/// value stays 200000, so the screen and what gets saved disagree. The same control also anchors a step on a field with no
/// number to its bounds, so a click on ▼ in an empty cell used to fill in 79228162514264337593543950335.
/// </summary>
[Collection("forge-app-ui")]
public sealed class NumberFieldTests
{
    [Fact]
    public Task Text_that_does_not_read_as_a_number_never_enters_the_field() => SharedUi.RunAsync(() =>
    {
        var (window, field, box) = Open(-100);
        try
        {
            field.Value = 2000000m;

            // Letters, an appended letter, a second separator, an exponent and a grouped number are all refused.
            foreach (var refused in new[] { "abc", "2000000x", "1.2.3", "1e5", "1,000" })
            {
                box.Text = refused;
                Assert.Equal("2000000", box.Text);
                Assert.Equal(2000000m, field.Value);
            }

            // A refused edit leaves the box attached to the field: a later value still shows up in it.
            field.Value = 7m;
            Assert.Equal("7", box.Text);
            field.Value = 2000000m;

            // A number that is still being typed is not disturbed: the trailing separator has to survive for 1.5 to exist.
            box.Text = "18.";
            Assert.Equal("18.", box.Text);
            box.Text = "18.75";
            Assert.Equal("18.75", box.Text);
            Assert.Equal(18.75m, field.Value);

            // The empty box is "unknown", and it is reachable: clearing is not a refused edit.
            box.Text = string.Empty;
            Assert.Null(field.Value);

            // A negative is only a number where the range has one.
            var (positive, positiveField, positiveBox) = Open(0);
            try
            {
                positiveBox.Text = "-5";
                Assert.Equal(string.Empty, positiveBox.Text ?? string.Empty);
                Assert.Null(positiveField.Value);
                box.Text = "-5";
                Assert.Equal(-5m, field.Value);
            }
            finally
            {
                positive.Close();
            }

            return Task.CompletedTask;
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task A_spin_on_an_empty_field_leaves_it_empty() => SharedUi.RunAsync(() =>
    {
        var (window, field, box) = Open(0);
        try
        {
            var spinner = field.GetVisualDescendants().OfType<ButtonSpinner>().Single();
            var increase = spinner.GetVisualDescendants().OfType<RepeatButton>().Single(b => b.Name == "PART_IncreaseButton");
            var decrease = spinner.GetVisualDescendants().OfType<RepeatButton>().Single(b => b.Name == "PART_DecreaseButton");
            Assert.True(increase.IsVisible && decrease.IsVisible);

            decrease.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Null(field.Value);
            Assert.Equal(string.Empty, box.Text ?? string.Empty);

            increase.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Null(field.Value);

            // With a number in the field the arrows step it as usual.
            field.Value = 10m;
            increase.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(11m, field.Value);

            return Task.CompletedTask;
        }
        finally
        {
            window.Close();
        }
    });

    /// <summary>A window with one number field, its arrow pair on screen and its own text box.</summary>
    private static (Window Window, NumericUpDown Field, TextBox Box) Open(decimal minimum)
    {
        var field = new NumericUpDown { Value = null, Minimum = minimum, Maximum = decimal.MaxValue, FormatString = "0.######" };
        var window = new Window { Width = 320, Height = 160, Content = field };
        window.Show();
        SharedUi.Settle(window);
        return (window, field, field.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "PART_TextBox"));
    }
}
