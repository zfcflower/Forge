using System.Globalization;
using System.Runtime.CompilerServices;

namespace Forge.App.Localization;

/// <summary>
/// Culture-independent formatting shared by every table (task decision: sentence patterns follow the language, numbers
/// and times do not). Thousands are always grouped with <c>,</c>; clock times are always 24-hour; identifiers (process
/// ids, line numbers) never go through <see cref="Number(long)"/> because they must not be grouped.
/// Only the elapsed-time pattern differs per language, so this class registers the <c>format.*</c> keys itself.
/// </summary>
internal static class TextFormat
{
    /// <summary>Shown instead of a value the provider did not report.</summary>
    public const string None = "—";

    public const string SecondsKey = "format.elapsed.seconds";
    public const string MinutesSecondsKey = "format.elapsed.minutesSeconds";

    [ModuleInitializer]
    internal static void Initialize() => Register();

    internal static void Register()
    {
        Loc.Register(SecondsKey, "{0} 秒", "{0}s");
        Loc.Register(MinutesSecondsKey, "{0} 分 {1} 秒", "{0}m {1}s");
    }

    /// <summary>Grouped count: <c>49,712</c>.</summary>
    public static string Number(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>A count the provider may not have reported; <see cref="None"/> when it did not.</summary>
    public static string Number(long? value) => value is { } known ? Number(known) : None;

    /// <summary>An identifier: never grouped (<c>49712</c>).</summary>
    public static string Id(long value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>Local wall-clock time, 24-hour: <c>12:30:45</c>.</summary>
    public static string Clock(DateTimeOffset at) => at.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>Local month-day and minute, 24-hour: <c>11-05 12:30</c> (default session titles).</summary>
    public static string ShortStamp(DateTimeOffset at) => at.ToLocalTime().ToString("MM-dd HH:mm", CultureInfo.InvariantCulture);

    /// <summary>
    /// Elapsed time in the current language: under a minute <c>4.2 秒</c> / <c>4.2s</c>, otherwise
    /// <c>3 分 05 秒</c> / <c>3m 05s</c>.
    /// </summary>
    public static string Elapsed(TimeSpan elapsed) =>
        elapsed.TotalSeconds < 60
            ? Loc.Instance.Format(SecondsKey, elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture))
            : Loc.Instance.Format(
                MinutesSecondsKey,
                ((long)elapsed.TotalMinutes).ToString(CultureInfo.InvariantCulture),
                elapsed.Seconds.ToString("00", CultureInfo.InvariantCulture));
}
