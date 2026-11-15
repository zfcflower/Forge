using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Forge.App.Services;

/// <summary>
/// Pure version handling for update checks (wave W10b). The version the app reports is the assembly
/// <c>InformationalVersion</c>, which CI stamps with <c>-p:Version</c>: a tag builds <c>1.2.0</c> (or
/// <c>1.2.0-beta.1</c>) and any other CI build stamps <c>0.0.0-ci.&lt;run&gt;</c>. That CI marker is a development build,
/// which may check for an update only when the user asks, never on the automatic schedule.
/// <para>
/// Comparison follows SemVer precedence: the numeric fields first (as <see cref="Version"/>), then the dot-separated
/// pre-release identifiers — numeric identifiers compare as numbers (<c>beta.2 &lt; beta.10</c>), alphanumeric ones ordinally,
/// a shorter identifier list of otherwise equal identifiers sorts lower, and any pre-release sorts before the release itself.
/// Build metadata (<c>+sha</c>) is ignored. Without the pre-release rule the beta channel could never advance: every beta of
/// the same triplet compared equal to the next one. <c>null</c> means one side is not a parseable version; the caller turns
/// that into a readable failure.
/// </para>
/// </summary>
internal static class UpdateVersion
{
    /// <summary>The CI stamp of a non-tag build: <c>0.0.0-ci</c> or <c>0.0.0-ci.&lt;run&gt;</c>.</summary>
    public const string CiBuildPrefix = "0.0.0-ci";

    public static bool IsDevBuild(string version)
    {
        ArgumentNullException.ThrowIfNull(version);
        return version.Length >= CiBuildPrefix.Length
            && version.StartsWith(CiBuildPrefix, StringComparison.Ordinal)
            && (version.Length == CiBuildPrefix.Length || version[CiBuildPrefix.Length] == '.');
    }

    /// <summary>
    /// <c>&lt; 0</c> when <paramref name="offered"/> is older than <paramref name="current"/>, <c>0</c> when they have the same
    /// precedence, <c>&gt; 0</c> when an update exists, and <c>null</c> when either side does not parse. Build metadata is
    /// ignored; a pre-release is older than its release.
    /// </summary>
    public static int? Compare(string current, string offered)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(offered);

        if (!TryParse(current, out var currentNumbers, out var currentPrerelease) ||
            !TryParse(offered, out var offeredNumbers, out var offeredPrerelease))
        {
            return null;
        }

        var byNumbers = offeredNumbers.CompareTo(currentNumbers);
        return byNumbers != 0 ? byNumbers : ComparePrerelease(currentPrerelease, offeredPrerelease);
    }

    /// <summary>Splits <c>1.2.0-beta.1+build.7</c> into its numeric prefix and the pre-release part (empty for a release).</summary>
    private static bool TryParse(string version, [NotNullWhen(true)] out Version? numbers, out string prerelease)
    {
        var text = version.AsSpan();
        var metadata = text.IndexOf('+');
        if (metadata >= 0)
        {
            text = text[..metadata];
        }

        prerelease = string.Empty;
        var dash = text.IndexOf('-');
        if (dash >= 0)
        {
            prerelease = text[(dash + 1)..].ToString();
            text = text[..dash];
        }

        return Version.TryParse(text.ToString(), out numbers);
    }

    /// <summary>SemVer precedence of two pre-release identifier lists, as a comparison of <paramref name="offered"/> to <paramref name="current"/>.</summary>
    private static int ComparePrerelease(string current, string offered)
    {
        if (current.Length == 0 && offered.Length == 0)
        {
            return 0;
        }

        if (offered.Length == 0)
        {
            // A release (no identifier at all) outranks every pre-release of the same numeric version.
            return 1;
        }

        if (current.Length == 0)
        {
            return -1;
        }

        var currentIdentifiers = current.Split('.');
        var offeredIdentifiers = offered.Split('.');
        var shared = Math.Min(currentIdentifiers.Length, offeredIdentifiers.Length);
        for (var index = 0; index < shared; index++)
        {
            var byIdentifier = CompareIdentifier(currentIdentifiers[index], offeredIdentifiers[index]);
            if (byIdentifier != 0)
            {
                return byIdentifier;
            }
        }

        return offeredIdentifiers.Length.CompareTo(currentIdentifiers.Length);
    }

    /// <summary>
    /// Numeric identifiers compare numerically (<c>10 &gt; 2</c>) and sort below alphanumeric ones; alphanumeric ones compare by
    /// ordinal code point, which is what SemVer's ASCII rule means.
    /// </summary>
    private static int CompareIdentifier(string current, string offered)
    {
        var currentIsNumber = long.TryParse(current, NumberStyles.None, CultureInfo.InvariantCulture, out var currentNumber);
        var offeredIsNumber = long.TryParse(offered, NumberStyles.None, CultureInfo.InvariantCulture, out var offeredNumber);
        return (currentIsNumber, offeredIsNumber) switch
        {
            (true, true) => offeredNumber.CompareTo(currentNumber),
            (true, false) => 1,
            (false, true) => -1,
            _ => string.CompareOrdinal(offered, current),
        };
    }
}
