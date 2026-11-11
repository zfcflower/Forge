using System.Globalization;
using Forge.Abstractions;

namespace Forge.Core.Automations;

// Adapted from CodeAlta's AutomationSchedule (BSD-2-Clause, see THIRD_PARTY_NOTICES.md): bitset rules, a field-by-field
// walk of the local clock, an 8-year horizon and the DST rules below.

/// <summary>
/// When a trigger is due: one or more bitset rules (minutes, hours, days, months, weekdays) read on the local clock of a
/// time zone. A cron expression is one rule; hourly is one rule; daily and weekly are one rule per time of day.
/// DST: a local time the clock skips is due at the instant it would have been reached; a local time the clock shows
/// twice is due only the first time.
/// </summary>
public sealed class AutomationSchedule
{
    private static readonly string[] MonthNames = ["jan", "feb", "mar", "apr", "may", "jun", "jul", "aug", "sep", "oct", "nov", "dec"];
    private static readonly string[] DayNames = ["sun", "mon", "tue", "wed", "thu", "fri", "sat"];

    private const ulong AllDays = 0xFFFFFFFEUL; // 1..31
    private const ulong AllMonths = 0x1FFEUL; // 1..12
    private const ulong AllWeekDays = 0x7FUL; // Sunday(0)..Saturday(6)

    private readonly Rule[] _rules;

    private AutomationSchedule(Rule[] rules) => _rules = rules;

    /// <summary>Builds the schedule of a trigger; invalid user input yields a message, never an exception.</summary>
    public static bool TryCreate(AutomationTrigger trigger, out AutomationSchedule? schedule, out string? error)
    {
        ArgumentNullException.ThrowIfNull(trigger);
        schedule = null;
        error = null;
        switch (trigger.Kind)
        {
            case AutomationTriggerKind.Hourly:
                if (trigger.Minute is < 0 or > 59) error = "The minute of an hourly trigger must be between 0 and 59.";
                else if (trigger.IntervalHours is < 1 or > 12) error = "An hourly trigger runs every 1 to 12 hours.";
                else schedule = new([new Rule(Bit(trigger.Minute), Every(24, trigger.IntervalHours), AllDays, AllMonths, AllWeekDays, false)]);
                break;
            case AutomationTriggerKind.Daily:
                if (trigger.Times.Count == 0) error = "A daily trigger needs at least one time.";
                else if (TryTimes(trigger.Times, AllWeekDays, out var daily, out error)) schedule = new(daily);
                break;
            case AutomationTriggerKind.Weekly:
                if (trigger.Days.Count == 0) error = "A weekly trigger needs at least one day.";
                else if (trigger.Times.Count == 0) error = "A weekly trigger needs at least one time.";
                else
                {
                    var days = 0UL;
                    foreach (var day in trigger.Days)
                    {
                        if ((int)day is < 0 or > 6)
                        {
                            error = "A weekly trigger day must be Sunday to Saturday.";
                            return false;
                        }

                        days |= Bit((int)day);
                    }

                    if (TryTimes(trigger.Times, days, out var weekly, out error)) schedule = new(weekly);
                }

                break;
            case AutomationTriggerKind.Cron:
                if (TryParseCron(trigger.Cron, out var rule, out error)) schedule = new([rule]);
                break;
            default:
                error = "Unknown trigger kind.";
                break;
        }

        return schedule is not null;
    }

    /// <summary>The first time after <paramref name="after"/> the schedule is due on the clock of <paramref name="zone"/>; null when none within 8 years (e.g. 30 February).</summary>
    public DateTimeOffset? Next(DateTimeOffset after, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        DateTimeOffset? best = null;
        foreach (var rule in _rules)
        {
            if (Next(rule, after, zone) is { } next && (best is null || next < best)) best = next;
        }

        return best;
    }

    /// <summary>The next <paramref name="count"/> fire times after <paramref name="after"/> (the editor preview uses 5).</summary>
    public IReadOnlyList<DateTimeOffset> NextOccurrences(DateTimeOffset after, TimeZoneInfo zone, int count)
    {
        var result = new List<DateTimeOffset>(Math.Max(count, 0));
        var cursor = after;
        while (result.Count < count && Next(cursor, zone) is { } next)
        {
            result.Add(next);
            cursor = next;
        }

        return result;
    }

    private static bool TryTimes(IReadOnlyList<AutomationTime> times, ulong weekDays, out Rule[] rules, out string? error)
    {
        rules = new Rule[times.Count];
        for (var i = 0; i < times.Count; i++)
        {
            var t = times[i];
            if (t.Hour is < 0 or > 23 || t.Minute is < 0 or > 59)
            {
                error = $"{t.Hour:00}:{t.Minute:00} is not a time of day.";
                return false;
            }

            rules[i] = new Rule(Bit(t.Minute), Bit(t.Hour), AllDays, AllMonths, weekDays, false);
        }

        error = null;
        return true;
    }

    private static DateTimeOffset? Next(Rule rule, DateTimeOffset after, TimeZoneInfo zone)
    {
        // Walk the clock of the zone a field at a time, starting at the minute after `after`.
        var local = TimeZoneInfo.ConvertTime(after, zone).DateTime;
        local = new DateTime(local.Year, local.Month, local.Day, local.Hour, local.Minute, 0, DateTimeKind.Unspecified).AddMinutes(1);
        var limit = local.AddYears(8);
        while (local < limit)
        {
            if ((rule.Months & Bit(local.Month)) == 0)
            {
                local = new DateTime(local.Year, local.Month, 1, 0, 0, 0, DateTimeKind.Unspecified).AddMonths(1);
            }
            else if (!rule.MatchesDay(local))
            {
                local = local.Date.AddDays(1);
            }
            else if ((rule.Hours & Bit(local.Hour)) == 0)
            {
                local = local.Date.AddHours(local.Hour + 1);
            }
            else if ((rule.Minutes & Bit(local.Minute)) == 0)
            {
                local = local.AddMinutes(1);
            }
            else
            {
                var instant = InstantOf(local, zone);
                if (instant > after) return instant;
                local = local.AddMinutes(1);
            }
        }

        return null;
    }

    private static DateTimeOffset InstantOf(DateTime local, TimeZoneInfo zone)
    {
        if (zone.IsInvalidTime(local))
        {
            // Skipped by a forward jump: due when the clock reaches the gap, i.e. the offset before the jump applies.
            return new DateTimeOffset(local, zone.GetUtcOffset(local.AddHours(-1)));
        }

        if (zone.IsAmbiguousTime(local))
        {
            // Shown twice: the first (larger-offset) occurrence.
            var offsets = zone.GetAmbiguousTimeOffsets(local);
            var max = offsets[0];
            foreach (var o in offsets)
            {
                if (o > max) max = o;
            }

            return new DateTimeOffset(local, max);
        }

        return new DateTimeOffset(local, zone.GetUtcOffset(local));
    }

    /// <summary>
    /// Five fields: minute hour day-of-month month day-of-week. A field is <c>*</c>, a value, a range <c>a-b</c>, a list
    /// <c>a,b</c>, or any of those with <c>/n</c>; months and weekdays take three-letter English names (any case), Sunday is
    /// 0 or 7. With both day fields restricted a day matching either is due.
    /// </summary>
    private static bool TryParseCron(string? expression, out Rule rule, out string? error)
    {
        rule = default;
        var fields = (expression ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length != 5)
        {
            error = "A cron expression has five fields: minute, hour, day of month, month and day of week.";
            return false;
        }

        if (!TryParseField(fields[0], 0, 59, null, "minute", out var minutes, out error)
            || !TryParseField(fields[1], 0, 23, null, "hour", out var hours, out error)
            || !TryParseField(fields[2], 1, 31, null, "day of month", out var days, out error)
            || !TryParseField(fields[3], 1, 12, MonthNames, "month", out var months, out error)
            || !TryParseField(fields[4], 0, 7, DayNames, "day of week", out var weekDays, out error))
        {
            return false;
        }

        if ((weekDays & Bit(7)) != 0) weekDays = (weekDays & ~Bit(7)) | 1UL;
        rule = new Rule(minutes, hours, days, months, weekDays, !fields[2].StartsWith('*') && !fields[4].StartsWith('*'));
        return true;
    }

    private static bool TryParseField(string field, int minimum, int maximum, string[]? names, string what, out ulong bits, out string? error)
    {
        bits = 0;
        foreach (var part in field.Split(','))
        {
            var range = part;
            var step = 1;
            var slash = part.IndexOf('/');
            if (slash >= 0)
            {
                range = part[..slash];
                if (!int.TryParse(part.AsSpan(slash + 1), NumberStyles.None, CultureInfo.InvariantCulture, out step) || step < 1 || step > maximum)
                {
                    return Fail(out error);
                }
            }

            int first, last;
            if (range == "*")
            {
                first = minimum;
                last = ReferenceEquals(names, DayNames) ? 6 : maximum;
            }
            else
            {
                var dash = range.IndexOf('-');
                if (!TryParseValue(dash < 0 ? range : range[..dash], names, minimum, out first)) return Fail(out error);
                if (dash < 0) last = slash >= 0 ? maximum : first;
                else if (!TryParseValue(range[(dash + 1)..], names, minimum, out last)) return Fail(out error);
            }

            if (first < minimum || last > maximum || first > last) return Fail(out error);
            for (var value = first; value <= last; value += step) bits |= Bit(value);
        }

        error = null;
        return bits != 0 || Fail(out error);

        bool Fail(out string? message)
        {
            message = $"'{field}' is not a valid {what} field.";
            return false;
        }
    }

    private static bool TryParseValue(string text, string[]? names, int minimum, out int value)
    {
        if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value)) return true;
        value = 0;
        if (names is null) return false;
        for (var i = 0; i < names.Length; i++)
        {
            if (string.Equals(names[i], text, StringComparison.OrdinalIgnoreCase))
            {
                value = i + (ReferenceEquals(names, MonthNames) ? 1 : 0);
                return value >= minimum;
            }
        }

        return false;
    }

    private static ulong Bit(int value) => 1UL << value;

    private static ulong Every(int count, int step)
    {
        var bits = 0UL;
        for (var value = 0; value < count; value += step) bits |= Bit(value);
        return bits;
    }

    private readonly record struct Rule(ulong Minutes, ulong Hours, ulong Days, ulong Months, ulong WeekDays, bool Either)
    {
        public bool MatchesDay(DateTime date)
        {
            var day = (Days & Bit(date.Day)) != 0;
            var weekDay = (WeekDays & Bit((int)date.DayOfWeek)) != 0;
            return Either ? day || weekDay : day && weekDay;
        }
    }
}
