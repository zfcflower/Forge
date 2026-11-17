using System.Globalization;

namespace Forge.App.ViewModels;

/// <summary>What a text input is expected to be. The message for each kind lives in <c>InputValidationStrings</c>.</summary>
internal enum InputRuleKind
{
    Required,
    PositiveInteger,
    NonNegativeDecimal,
    AbsoluteHttpUrl,
    AbsolutePath,
    EnvironmentVariable,
    PriceSet,
}

/// <summary>One failed rule. <paramref name="maxDecimals"/> is only used by <see cref="InputRuleKind.NonNegativeDecimal"/>.</summary>
internal readonly record struct InputError(InputRuleKind Kind, int MaxDecimals = 0);

/// <summary>
/// The one place a settings text field is parsed and judged. Views and view models call these rules instead of writing their own
/// <c>TryParse</c>/<c>Uri.TryCreate</c> checks, so an invalid value is recognized the same way (and described by the same
/// message) everywhere.
/// <para>Empty is not automatically an error: each caller states whether the field allows unknown/empty. A validator returns
/// <c>null</c> for a valid value and an <see cref="InputError"/> otherwise, so callers can both mark the cell and block a save.</para>
/// </summary>
internal static class InputRules
{
    /// <summary>Prices are stored per million tokens; six decimals is finer than any provider publishes.</summary>
    public const int PriceMaxDecimals = 6;

    public static InputError? ValidateRequired(string? text) =>
        string.IsNullOrWhiteSpace(text) ? new InputError(InputRuleKind.Required) : null;

    /// <summary>A positive whole number, or an empty value when <paramref name="allowEmpty"/> is true (unknown).</summary>
    public static InputError? ValidatePositiveInteger(string? text, bool allowEmpty = true)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return allowEmpty ? null : new InputError(InputRuleKind.PositiveInteger);
        }

        return TryParsePositiveInteger(text, out _) ? null : new InputError(InputRuleKind.PositiveInteger);
    }

    public static bool TryParsePositiveInteger(string? text, out int value)
    {
        value = 0;
        var trimmed = (text ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            return false;
        }

        foreach (var character in trimmed)
        {
            if (!char.IsAsciiDigit(character))
            {
                return false;
            }
        }

        return int.TryParse(trimmed, NumberStyles.None, CultureInfo.InvariantCulture, out value) && value > 0;
    }

    /// <summary>A non-negative decimal with at most <paramref name="maxDecimals"/> fraction digits; empty means unknown.</summary>
    public static InputError? ValidateNonNegativeDecimal(string? text, int maxDecimals = PriceMaxDecimals)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        return TryParseNonNegativeDecimal(text, maxDecimals, out _)
            ? null
            : new InputError(InputRuleKind.NonNegativeDecimal, maxDecimals);
    }

    public static bool TryParseNonNegativeDecimal(string? text, out double value) =>
        TryParseNonNegativeDecimal(text, PriceMaxDecimals, out value);

    public static bool TryParseNonNegativeDecimal(string? text, int maxDecimals, out double value)
    {
        value = 0;
        var trimmed = (text ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            return false;
        }

        var dot = -1;
        var digits = 0;
        for (var index = 0; index < trimmed.Length; index++)
        {
            var character = trimmed[index];
            if (character == '.')
            {
                if (dot >= 0)
                {
                    return false;
                }

                dot = index;
                continue;
            }

            if (!char.IsAsciiDigit(character))
            {
                return false;
            }

            digits++;
        }

        if (digits == 0 || dot == trimmed.Length - 1)
        {
            return false;
        }

        if (dot >= 0 && trimmed.Length - dot - 1 > maxDecimals)
        {
            return false;
        }

        return double.TryParse(trimmed, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>An absolute http/https URL; empty is allowed only when <paramref name="allowEmpty"/> is true.</summary>
    public static InputError? ValidateAbsoluteHttpUrl(string? text, bool allowEmpty = false)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return allowEmpty ? null : new InputError(InputRuleKind.AbsoluteHttpUrl);
        }

        return TryParseAbsoluteHttpUrl(text, out _) ? null : new InputError(InputRuleKind.AbsoluteHttpUrl);
    }

    public static bool TryParseAbsoluteHttpUrl(string? text, out Uri? uri)
    {
        uri = null;
        var trimmed = (text ?? string.Empty).Trim();
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var parsed) ||
            (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
        {
            return false;
        }

        uri = parsed;
        return true;
    }

    /// <summary>A fully qualified filesystem path; empty is allowed only when <paramref name="allowEmpty"/> is true.</summary>
    public static InputError? ValidateAbsolutePath(string? text, bool allowEmpty = true)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return allowEmpty ? null : new InputError(InputRuleKind.AbsolutePath);
        }

        return Path.IsPathFullyQualified(text.Trim()) ? null : new InputError(InputRuleKind.AbsolutePath);
    }

    /// <summary>A conventional environment variable name (letter or underscore first, then letters/digits/underscores).</summary>
    public static InputError? ValidateEnvironmentVariable(string? text, bool allowEmpty = false)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return allowEmpty ? null : new InputError(InputRuleKind.EnvironmentVariable);
        }

        var trimmed = text.Trim();
        if (trimmed.Length == 0 || (!char.IsAsciiLetter(trimmed[0]) && trimmed[0] != '_'))
        {
            return new InputError(InputRuleKind.EnvironmentVariable);
        }

        foreach (var character in trimmed)
        {
            if (!char.IsAsciiLetterOrDigit(character) && character != '_')
            {
                return new InputError(InputRuleKind.EnvironmentVariable);
            }
        }

        return null;
    }
}
