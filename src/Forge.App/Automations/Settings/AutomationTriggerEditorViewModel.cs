using System.Collections.ObjectModel;
using System.Globalization;
using Forge.Abstractions;
using Forge.App.Localization;
using Forge.App.ViewModels;

namespace Forge.App.Automations.Settings;

/// <summary>One weekday check box of a weekly trigger.</summary>
internal sealed class AutomationDayToggleViewModel : ObservableObject
{
    private readonly Action changed;
    private bool isChecked;

    public AutomationDayToggleViewModel(DayOfWeek day, bool isChecked, Action changed)
    {
        Day = day;
        this.isChecked = isChecked;
        this.changed = changed;
    }

    public DayOfWeek Day { get; }

    public string Label => AutomationText.DayName(Day);

    public bool IsChecked
    {
        get => isChecked;
        set
        {
            if (Set(ref isChecked, value))
            {
                changed();
            }
        }
    }
}

/// <summary>
/// One trigger row of the editor: a kind and the text fields of that kind. <see cref="TryBuild"/> validates the fields and
/// produces the <see cref="AutomationTrigger"/>; cron syntax itself is validated by the engine's preview.
/// </summary>
internal sealed class AutomationTriggerEditorViewModel : ObservableObject
{
    private static readonly AutomationTriggerKind[] Kinds =
        [AutomationTriggerKind.Hourly, AutomationTriggerKind.Daily, AutomationTriggerKind.Weekly, AutomationTriggerKind.Cron];

    private static readonly char[] TimeSeparators = [',', ';', ' '];

    private readonly Action changed;
    private AutomationTriggerKind kind;
    private string intervalText = "1";
    private string minuteText = "0";
    private string timesText = "09:00";
    private string cronText = string.Empty;

    public AutomationTriggerEditorViewModel(AutomationTrigger? source, Action changed, Action<AutomationTriggerEditorViewModel> remove)
    {
        this.changed = changed;
        var days = source?.Days ?? [];
        Days = [.. AutomationText.WeekOrder.Select(day => new AutomationDayToggleViewModel(day, days.Contains(day), changed))];
        kind = source?.Kind ?? AutomationTriggerKind.Daily;
        if (source is not null)
        {
            intervalText = source.IntervalHours.ToString(CultureInfo.InvariantCulture);
            minuteText = source.Minute.ToString(CultureInfo.InvariantCulture);
            timesText = string.Join(", ", source.Times.Select(AutomationText.Time));
            cronText = source.Cron ?? string.Empty;
        }
        else
        {
            foreach (var day in Days.Where(static d => d.Day is >= DayOfWeek.Monday and <= DayOfWeek.Friday))
            {
                day.IsChecked = false;
            }
        }

        RemoveCommand = new RelayCommand(() => remove(this));
    }

    public RelayCommand RemoveCommand { get; }

    public string RemoveText => Loc.Instance[AutomationSettingsStrings.EditorRemoveTriggerKey];

    public ObservableCollection<AutomationDayToggleViewModel> Days { get; }

    public IReadOnlyList<string> KindOptions { get; } =
    [
        Loc.Instance[AutomationSettingsStrings.EditorKindHourlyKey],
        Loc.Instance[AutomationSettingsStrings.EditorKindDailyKey],
        Loc.Instance[AutomationSettingsStrings.EditorKindWeeklyKey],
        Loc.Instance[AutomationSettingsStrings.EditorKindCronKey],
    ];

    public AutomationTriggerKind Kind
    {
        get => kind;
        set
        {
            if (Set(ref kind, value))
            {
                OnPropertyChanged(nameof(KindIndex));
                OnPropertyChanged(nameof(IsHourly));
                OnPropertyChanged(nameof(ShowsTimes));
                OnPropertyChanged(nameof(IsWeekly));
                OnPropertyChanged(nameof(IsCron));
                changed();
            }
        }
    }

    /// <summary>The combo box position of <see cref="Kind"/>.</summary>
    public int KindIndex
    {
        get => Array.IndexOf(Kinds, kind);
        set
        {
            if (value >= 0 && value < Kinds.Length)
            {
                Kind = Kinds[value];
            }
        }
    }

    public bool IsHourly => kind == AutomationTriggerKind.Hourly;

    public bool ShowsTimes => kind is AutomationTriggerKind.Daily or AutomationTriggerKind.Weekly;

    public bool IsWeekly => kind == AutomationTriggerKind.Weekly;

    public bool IsCron => kind == AutomationTriggerKind.Cron;

    public string IntervalText
    {
        get => intervalText;
        set
        {
            if (Set(ref intervalText, value ?? string.Empty))
            {
                changed();
            }
        }
    }

    public string MinuteText
    {
        get => minuteText;
        set
        {
            if (Set(ref minuteText, value ?? string.Empty))
            {
                changed();
            }
        }
    }

    public string TimesText
    {
        get => timesText;
        set
        {
            if (Set(ref timesText, value ?? string.Empty))
            {
                changed();
            }
        }
    }

    public string CronText
    {
        get => cronText;
        set
        {
            if (Set(ref cronText, value ?? string.Empty))
            {
                changed();
            }
        }
    }

    /// <summary>Validates the fields of the current kind; <c>null</c> trigger plus the reason when something is wrong.</summary>
    public (AutomationTrigger? Trigger, string? Error) TryBuild()
    {
        var loc = Loc.Instance;
        switch (kind)
        {
            case AutomationTriggerKind.Hourly:
                if (!TryInt(IntervalText, 1, 12, out var interval))
                {
                    return (null, loc[AutomationSettingsStrings.ErrIntervalKey]);
                }

                if (!TryInt(MinuteText, 0, 59, out var minute))
                {
                    return (null, loc[AutomationSettingsStrings.ErrMinuteKey]);
                }

                return (new AutomationTrigger(kind) { IntervalHours = interval, Minute = minute }, null);

            case AutomationTriggerKind.Daily:
            case AutomationTriggerKind.Weekly:
                var times = ParseTimes(TimesText, out var badTime);
                if (badTime is not null)
                {
                    return (null, loc.Format(AutomationSettingsStrings.ErrTimeKey, badTime));
                }

                if (times.Count == 0)
                {
                    return (null, loc[AutomationSettingsStrings.ErrNeedTimeKey]);
                }

                if (kind == AutomationTriggerKind.Daily)
                {
                    return (new AutomationTrigger(kind) { Times = times }, null);
                }

                var days = Days.Where(static d => d.IsChecked).Select(static d => d.Day).ToList();
                return days.Count == 0
                    ? (null, loc[AutomationSettingsStrings.ErrNeedDayKey])
                    : (new AutomationTrigger(kind) { Times = times, Days = days }, null);

            default:
                var cron = CronText.Trim();
                return cron.Length == 0
                    ? (null, loc[AutomationSettingsStrings.ErrNeedCronKey])
                    : (new AutomationTrigger(kind) { Cron = cron }, null);
        }
    }

    private static bool TryInt(string text, int min, int max, out int value) =>
        int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out value) && value >= min && value <= max;

    /// <summary>Parses "HH:mm" entries separated by comma, semicolon or space; sorted and distinct. <paramref name="bad"/> is the first bad entry.</summary>
    private static List<AutomationTime> ParseTimes(string text, out string? bad)
    {
        bad = null;
        var result = new List<AutomationTime>();
        foreach (var part in text.Split(TimeSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var pieces = part.Split(':');
            if (pieces.Length != 2 || !TryInt(pieces[0], 0, 23, out var hour) || !TryInt(pieces[1], 0, 59, out var minute) ||
                pieces[1].Length != 2)
            {
                bad = part;
                return result;
            }

            var time = new AutomationTime(hour, minute);
            if (!result.Contains(time))
            {
                result.Add(time);
            }
        }

        result.Sort(static (a, b) => (a.Hour * 60 + a.Minute).CompareTo(b.Hour * 60 + b.Minute));
        return result;
    }
}
