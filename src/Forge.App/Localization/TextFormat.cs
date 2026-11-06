using System.Globalization;

namespace Forge.App.Localization;

/// <summary>
/// Culture-independent formatting shared by every table (task decision: sentence patterns follow the language, numbers
/// and times do not). Thousands are always grouped with <c>,</c>; clock times are always 24-hour; identifiers (process
/// ids, line numbers) never go through <see cref="Number(long)"/> because they must not be grouped.
/// Only the elapsed-time pattern differs per language; its texts live in <see cref="FormatStrings"/>.
/// </summary>
internal static class TextFormat
{
    /// <summary>Shown instead of a value the provider did not report.</summary>
    public const string None = "—";

    public const string SecondsKey = "format.elapsed.seconds";
    public const string MinutesSecondsKey = "format.elapsed.minutesSeconds";

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

    /// <summary>59.95 s: from here the one-decimal seconds would display as 60.0, so it switches to minutes.</summary>
    private const long MinuteRoundsUpTicks = 599_500_000;

    /// <summary>
    /// Elapsed time in the current language: under a minute <c>4.2 秒</c> / <c>4.2s</c>, otherwise
    /// <c>3 分 05 秒</c> / <c>3m 05s</c>. The branch is decided on the value as displayed (rounded to 0.1 s), so 59.96 s is <c>1m 00s</c>, never 60.0s;
    /// the minutes branch truncates seconds, so it can never show 60.
    /// </summary>
    public static string Elapsed(TimeSpan elapsed)
    {
        long ticks = elapsed.Ticks;
        if (ticks < MinuteRoundsUpTicks)
        {
            return Loc.Instance.Format(SecondsKey, elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture));
        }

        // [59.95 s, 60 s) displays as exactly one minute; beyond that minutes and seconds are truncated.
        bool roundedUp = ticks < TimeSpan.TicksPerMinute;
        return Loc.Instance.Format(
            MinutesSecondsKey,
            roundedUp ? "1" : ((long)elapsed.TotalMinutes).ToString(CultureInfo.InvariantCulture),
            roundedUp ? "00" : elapsed.Seconds.ToString("00", CultureInfo.InvariantCulture));
    }
}
