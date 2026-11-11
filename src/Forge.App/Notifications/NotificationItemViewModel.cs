using System.Globalization;
using Forge.App.Localization;
using Forge.App.ViewModels;

namespace Forge.App.Notifications;

/// <summary>
/// One toast on screen: its severity, the two lines of text, and how many times the same notification arrived while it was
/// visible (<c>×3</c>). Built by <see cref="NotificationCenter"/>, which also owns the timers, and shown by
/// <c>Views/NotificationOverlay</c>; nothing here decides when the item leaves.
/// <para>The stored texts are <see cref="LocText"/> values, so the item re-resolves them after a language switch
/// (<see cref="RefreshText"/>, called by the center); <see cref="Key"/> stays the identity it was de-duplicated by, because
/// the wording of an already-visible notification is not a new notification.</para>
/// </summary>
internal sealed class NotificationItemViewModel : ObservableObject
{
    private readonly LocText title;
    private readonly LocText body;
    private int count = 1;
    private bool isLeaving;

    /// <param name="level">Severity: colour of the level bar and of the level word.</param>
    /// <param name="title">Bold first line.</param>
    /// <param name="body">Text under the title.</param>
    /// <param name="key">De-duplication identity, already resolved by the center.</param>
    public NotificationItemViewModel(NotificationLevel level, LocText title, LocText body, string key)
    {
        ArgumentNullException.ThrowIfNull(key);

        Level = level;
        this.title = title;
        this.body = body;
        Key = key;
    }

    public NotificationLevel Level { get; }

    /// <summary>The identity two notifications were merged by; never changes for the lifetime of the item.</summary>
    public string Key { get; }

    public string Title => title.IsEmpty ? string.Empty : title.Resolve();

    public string Body => body.IsEmpty ? string.Empty : body.Resolve();

    /// <summary>The localized severity word shown above the title.</summary>
    public string LevelText => Level switch
    {
        NotificationLevel.Success => Loc.Instance[NotificationsStrings.LevelSuccess],
        NotificationLevel.Warning => Loc.Instance[NotificationsStrings.LevelWarning],
        NotificationLevel.Error => Loc.Instance[NotificationsStrings.LevelError],
        _ => Loc.Instance[NotificationsStrings.LevelInfo],
    };

    public bool IsInfo => Level == NotificationLevel.Info;

    public bool IsSuccess => Level == NotificationLevel.Success;

    public bool IsWarning => Level == NotificationLevel.Warning;

    public bool IsError => Level == NotificationLevel.Error;

    /// <summary>How many times this notification arrived while it was visible; 1 for a single occurrence.</summary>
    public int Count => count;

    /// <summary>The <c>×N</c> badge is only drawn for a repeat.</summary>
    public bool HasCount => count > 1;

    public string CountText => "×" + count.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// True from the moment the item started leaving (its timer ran out, it was evicted, or its toast is being animated
    /// away) until the center removes it. The view fades the item out on this flag and the center drops it afterwards.
    /// </summary>
    public bool IsLeaving
    {
        get => isLeaving;
        internal set => Set(ref isLeaving, value);
    }

    /// <summary>Counts one more occurrence of the same notification.</summary>
    internal void Increment()
    {
        count++;
        OnPropertyChanged(nameof(Count));
        OnPropertyChanged(nameof(HasCount));
        OnPropertyChanged(nameof(CountText));
    }

    /// <summary>Re-resolves the stored texts after a language switch.</summary>
    internal void RefreshText()
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Body));
        OnPropertyChanged(nameof(LevelText));
    }
}
