using System.Globalization;
using Avalonia;
using Avalonia.Controls;

namespace Forge.App.Views;

/// <summary>
/// What a number field lets in. Every <see cref="NumericUpDown"/> in the app looks like a number field (the arrow pair from
/// <c>ForgeNumberStepper</c>) and behaves like one: its text box only ever holds something that reads as a number, and a
/// field with no number has nothing to step from.
/// <para>The stock control accepts any text and drops it on blur: type <c>abc</c> over <c>200000</c> and the box keeps the
/// letters while the value stays 200000, so what is saved disagrees with what is shown. The filter here keeps the text the
/// field already accepted, so a character, a second separator or pasted prose never enters the box in the first place. It
/// runs as a class handler rather than per view because the rule is about the control, not about one settings page.</para>
/// </summary>
internal static class NumberField
{
    /// <summary>The value the field held before its current one, i.e. what a spin would step from.</summary>
    private static readonly AttachedProperty<SpinStart?> SpinStartProperty =
        AvaloniaProperty.RegisterAttached<NumericUpDown, SpinStart?>("SpinStart", typeof(NumberField));

    private static bool installed;

    /// <summary>Hooks the two rules into every number field; called while the application initializes.</summary>
    public static void Install()
    {
        if (installed)
        {
            return;
        }

        installed = true;
        TextBox.TextProperty.Changed.AddClassHandler<TextBox>(static (box, e) => FilterText(box, e.NewValue as string));
        NumericUpDown.ValueProperty.Changed.AddClassHandler<NumericUpDown>(static (field, e) => Remember(field, e.OldValue));
    }

    private static void FilterText(TextBox box, string? text)
    {
        // Only a number field's own input box: every other text box in the app types whatever it likes.
        if (box.TemplatedParent is not NumericUpDown field || IsNumberText(text, field.Minimum < 0))
        {
            return;
        }

        // SetCurrentValue, not Text =: the box's text is a template binding to the field's own Text, and taking the property
        // locally would stop that binding from ever updating this box again.
        var previous = field.Text ?? string.Empty;
        box.SetCurrentValue(TextBox.TextProperty, IsNumberText(previous, field.Minimum < 0) ? previous : string.Empty);
    }

    /// <summary>
    /// Remembers the value a spin would step away from. The stock control anchors a spin on a field with no number to its
    /// bounds (`▼` on an empty cell lands on <see cref="decimal.MaxValue"/>), which would fill a cell the user meant to leave
    /// unknown with 79228162514264337593543950335; a field with no number stays empty instead.
    /// </summary>
    private static void Remember(NumericUpDown field, object? previous)
    {
        if (field.GetValue(SpinStartProperty) is not { } start)
        {
            start = new SpinStart();
            field.SetValue(SpinStartProperty, start);
            field.Spinned += (_, _) =>
            {
                if (start.Before is null)
                {
                    field.Value = null;
                }
            };
        }

        start.Before = previous as decimal?;
    }

    /// <summary>
    /// Whether <paramref name="text"/> is a number the user could still be typing: nothing at all (unknown), an optional
    /// leading minus when the range has negatives, digits, and at most one decimal separator with digits on either side.
    /// A trailing separator is allowed so <c>1.</c> can become <c>1.5</c>; <c>abc</c>, <c>1.2.3</c>, <c>1e5</c> and
    /// <c>1,000</c> are not numbers this app writes into <c>settings.json</c>.
    /// </summary>
    private static bool IsNumberText(string? text, bool allowNegative)
    {
        if (string.IsNullOrEmpty(text))
        {
            return true;
        }

        var separator = CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator;
        var index = allowNegative && text[0] == '-' ? 1 : 0;
        var separators = 0;
        for (; index < text.Length; index++)
        {
            if (char.IsAsciiDigit(text[index]))
            {
                continue;
            }

            if (separators == 0 && string.CompareOrdinal(text, index, separator, 0, separator.Length) == 0)
            {
                separators++;
                index += separator.Length - 1;
                continue;
            }

            return false;
        }

        return true;
    }

    private sealed class SpinStart
    {
        public decimal? Before { get; set; }
    }
}
