namespace Forge.Core.Providers;

/// <summary>
/// The one place model ids are compared across sources. A provider, a gateway and the catalog spell the same model
/// differently (<c>anthropic/claude-sonnet-4-5-20250929</c>, <c>claude-sonnet-4-5:free</c>, <c>claude-sonnet-4-5-latest</c>),
/// so matching normalizes first and only then agrees on a shape.
/// </summary>
internal static class ModelIdRules
{
    /// <summary>
    /// Trims, lower-cases, drops an OpenRouter-style <c>vendor/</c> prefix and a trailing <c>:free</c> or <c>-latest</c>, so
    /// the same model matches whatever spelling the gateway uses.
    /// </summary>
    internal static string Normalize(string? modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId))
        {
            return string.Empty;
        }

        var trimmed = modelId.AsSpan().Trim();
        var slash = trimmed.LastIndexOf('/');
        if (slash >= 0 && slash < trimmed.Length - 1)
        {
            trimmed = trimmed[(slash + 1)..];
        }

        if (trimmed.EndsWith(":free", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[..^5];
        }
        else if (trimmed.EndsWith("-latest", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[..^7];
        }

        return trimmed.ToString().ToLowerInvariant();
    }

    /// <summary>
    /// True for the only suffixes a known id may carry to still name a snapshot of it: <c>-yyyyMMdd</c>, that plus
    /// <c>-v&lt;digits&gt;</c>, or <c>-latest</c>. Anything else — a version bump such as <c>.7</c> or extra words — names a
    /// different model, not a snapshot of a known one, so it must not inherit the known id's window or prices.
    /// </summary>
    internal static bool IsSnapshotSuffix(ReadOnlySpan<char> remainder)
    {
        if (remainder.Equals("-latest", StringComparison.Ordinal))
        {
            return true;
        }

        // "-yyyyMMdd": exactly eight digits, because a shorter or longer run is a release number, not a date.
        if (remainder.Length < 9 || remainder[0] != '-')
        {
            return false;
        }

        var index = 1;
        while (index < remainder.Length && char.IsAsciiDigit(remainder[index]))
        {
            index++;
        }

        if (index != 9)
        {
            return false;
        }

        if (index == remainder.Length)
        {
            return true;
        }

        // Optional "-v<digits>": a dated snapshot re-released by the vendor.
        if (remainder[index] != '-' || index + 2 >= remainder.Length || remainder[index + 1] != 'v')
        {
            return false;
        }

        index += 2;
        var versionStart = index;
        while (index < remainder.Length && char.IsAsciiDigit(remainder[index]))
        {
            index++;
        }

        return index > versionStart && index == remainder.Length;
    }
}
