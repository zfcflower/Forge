using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using Forge.App.Approval;
using Forge.App.Localization;
using Forge.App.Logging;
using ZLogger;

namespace Forge.App.Notifications;

/// <summary>
/// Where every user-facing notification of the shell ends up. A caller hands over a level and two <see cref="LocText"/>
/// lines through <see cref="INotifications"/>; this decides what the user actually sees:
/// <list type="bullet">
/// <item>switched off in the settings: nothing at all;</item>
/// <item>the app in the background (minimized, or no Forge window is the active one): one desktop notification through
/// <see cref="ISystemNotifier"/>, unchanged text;</item>
/// <item>otherwise a toast in the window: newest on top, at most <see cref="MaxItems"/> of them, each with its own timer,
/// repeats of the same notification collapsed into one item that counts them (<c>×N</c>) and restarts its timer.</item>
/// </list>
/// <para>Threading: <see cref="NotifyAsync"/> is callable from any thread; the toast list and every timer callback are
/// marshalled onto the UI thread through <c>post</c> (the app passes <see cref="UiThreadDispatcher.Instance"/>), so the
/// collection is only ever touched where the view reads it. <see cref="IsBackground"/> is written by the overlay from the
/// window's state and read here on whatever thread raised the notification.</para>
/// <para>One instance per composition root; the window overlay resolves it from <see cref="Current"/> because the root view
/// model is not a feature's to extend. It is created before the window and disposed with the graph.</para>
/// </summary>
internal sealed class NotificationCenter : INotifications, IDisposable
{
    /// <summary>Toasts on screen at once. A new one past this evicts the oldest (it still animates out).</summary>
    internal const int MaxItems = 5;

    /// <summary>How long a toast stays, per level; a repeat restarts the same span.</summary>
    internal static readonly TimeSpan InfoDuration = TimeSpan.FromSeconds(4);

    internal static readonly TimeSpan SuccessDuration = TimeSpan.FromSeconds(4);
    internal static readonly TimeSpan WarningDuration = TimeSpan.FromSeconds(6);
    internal static readonly TimeSpan ErrorDuration = TimeSpan.FromSeconds(8);

    /// <summary>Time an item is faded and slid out by the view before it is removed; also the reflow window of the rest.</summary>
    internal static readonly TimeSpan ExitDelay = TimeSpan.FromMilliseconds(180);

    private readonly ObservableCollection<NotificationItemViewModel> items = [];
    private readonly ReadOnlyObservableCollection<NotificationItemViewModel> itemsView;
    private readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);
    private readonly Func<bool> enabled;
    private readonly ISystemNotifier notifier;
    private readonly Action<Action> post;
    private readonly TimeProvider time;
    private readonly int uiThread = Environment.CurrentManagedThreadId;
    private volatile bool background;
    private int disposed;

    /// <param name="enabled">Read on every call (the <c>notifications</c> preference); false shows nothing at all.</param>
    /// <param name="notifier">Raises the desktop notification; the only implementation talks to the platform's own tool.</param>
    /// <param name="post">
    /// Runs an action on the UI thread without waiting; null uses the app's dispatcher, which degrades to an inline call in a
    /// process without a UI thread (a test).
    /// </param>
    /// <param name="time">Clock of every timer; a test passes a fake one so no test waits four seconds.</param>
    public NotificationCenter(
        Func<bool> enabled,
        ISystemNotifier notifier,
        Action<Action>? post = null,
        TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(enabled);
        ArgumentNullException.ThrowIfNull(notifier);

        this.enabled = enabled;
        this.notifier = notifier;
        this.post = post ?? UiThreadDispatcher.Instance.Post;
        this.time = time ?? TimeProvider.System;
        itemsView = new ReadOnlyObservableCollection<NotificationItemViewModel>(items);

        // A language switch re-resolves the texts of what is already on screen. The table is process-wide, so the
        // subscription is dropped in Dispose; a notification raised from another thread is not ours.
        Loc.Instance.LanguageChanged += OnLanguageChanged;
        Current = this;
    }

    /// <summary>
    /// The instance the window overlay resolves from its own constructor (the same seam <c>ApprovalServices.Current</c>
    /// uses). Set once per composition root and cleared on dispose.
    /// </summary>
    public static NotificationCenter? Current { get; private set; }

    /// <summary>The toasts on screen, newest first; UI thread only, changed only through this class.</summary>
    public ReadOnlyObservableCollection<NotificationItemViewModel> Items => itemsView;

    /// <summary>
    /// True while the app is in the background, in which case notifications go to the desktop. Written by the overlay from
    /// the window's minimized state and the activity of every Forge window; read from any thread.
    /// </summary>
    public bool IsBackground
    {
        get => background;
        set => background = value;
    }

    /// <summary>How long a toast of this level stays on screen.</summary>
    internal static TimeSpan DurationOf(NotificationLevel level) => level switch
    {
        NotificationLevel.Success => SuccessDuration,
        NotificationLevel.Warning => WarningDuration,
        NotificationLevel.Error => ErrorDuration,
        _ => InfoDuration,
    };

    /// <summary>True once <see cref="Dispose"/> ran; every queued action and timer callback checks it.</summary>
    private bool IsDisposed => Volatile.Read(ref disposed) != 0;

    /// <inheritdoc />
    public Task NotifyAsync(
        NotificationLevel level,
        LocText title,
        LocText body,
        string? key = null,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!enabled() || IsDisposed)
        {
            return Task.CompletedTask;
        }

        if (background)
        {
            return NotifyDesktopAsync(title, body, ct);
        }

        var identity = key ?? DefaultKeyOf(level, title, body);
        // The post may run after the graph was torn down; a toast must not be created for a window that is gone.
        post(() =>
        {
            if (!IsDisposed)
            {
                Show(level, title, body, identity);
            }
        });
        return Task.CompletedTask;
    }

    /// <summary>The de-duplication identity of a notification that did not name one: its level and its text right now.</summary>
    internal static string DefaultKeyOf(NotificationLevel level, LocText title, LocText body) =>
        ((int)level).ToString(System.Globalization.CultureInfo.InvariantCulture) +
        "\u0000" + title.Resolve() +
        "\u0000" + body.Resolve();

    private async Task NotifyDesktopAsync(LocText title, LocText body, CancellationToken ct)
    {
        try
        {
            await notifier.NotifyAsync(title.Resolve(), body.Resolve(), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Notifying the user must never be able to break the work that wanted to notify.
            AppLogs.Notify.ZLogWarning($"the desktop notification failed: {exception.Message}");
        }
    }

    /// <summary>UI thread: shows the toast, or counts and refreshes the one already carrying this identity.</summary>
    private void Show(NotificationLevel level, LocText title, LocText body, string identity)
    {
        if (entries.TryGetValue(identity, out var existing))
        {
            Repeat(existing);
            return;
        }

        var entry = new Entry(new NotificationItemViewModel(level, title, body, identity));
        entries.Add(identity, entry);

        // Newest on top: the items below are pushed down by the stack, which animates the move.
        items.Insert(0, entry.Item);
        StartVisibilityTimer(entry);

        // Over the cap the oldest toast starts leaving now: it keeps its exit animation, so the stack reflows once instead
        // of snapping. Only toasts that are still shown count towards the cap - an item that is already leaving keeps its
        // slot until the exit window is over, and counting it would evict one toast too many per overflow.
        while (items.Count(static candidate => !candidate.IsLeaving) > MaxItems &&
               items.LastOrDefault(static candidate => !candidate.IsLeaving) is { } oldest)
        {
            BeginLeave(entries[oldest.Key], ExitDelay);
        }
    }

    /// <summary>UI thread: one more occurrence of a notification that is already visible.</summary>
    private void Repeat(Entry entry)
    {
        if (entry.Item.IsLeaving)
        {
            // It was on its way out; the repeat claims it back and the pending removal is dropped by the version bump.
            entry.Item.IsLeaving = false;
        }

        entry.Item.Increment();
        StartVisibilityTimer(entry);
    }

    /// <summary>UI thread: (re)starts the stay of an item; any timer of an earlier stay or of a pending removal is dropped.</summary>
    private void StartVisibilityTimer(Entry entry)
    {
        var version = ++entry.Version;
        entry.Timer?.Dispose();
        entry.Timer = time.CreateTimer(
            _ => post(() =>
            {
                if (!IsDisposed && entry.Version == version)
                {
                    BeginLeave(entry, ExitDelay);
                }
            }),
            null,
            DurationOf(entry.Item.Level),
            Timeout.InfiniteTimeSpan);
    }

    /// <summary>UI thread: starts fading an item out; it is removed <paramref name="delay"/> later unless a repeat claims it.</summary>
    private void BeginLeave(Entry entry, TimeSpan delay)
    {
        if (entry.Item.IsLeaving)
        {
            return;
        }

        entry.Item.IsLeaving = true;
        entry.Timer?.Dispose();
        entry.Timer = null;

        var version = ++entry.Version;
        entry.LeaveTimer?.Dispose();
        entry.LeaveTimer = time.CreateTimer(
            _ => post(() =>
            {
                if (!IsDisposed && entry.Version == version)
                {
                    Remove(entry);
                }
            }),
            null,
            delay,
            Timeout.InfiniteTimeSpan);
    }

    /// <summary>UI thread: drops an item that finished leaving (or was evicted); the rest reflow.</summary>
    private void Remove(Entry entry)
    {
        entry.Timer?.Dispose();
        entry.Timer = null;
        entry.LeaveTimer?.Dispose();
        entry.LeaveTimer = null;
        entries.Remove(entry.Item.Key);
        items.Remove(entry.Item);
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        if (Environment.CurrentManagedThreadId != uiThread)
        {
            return;
        }

        post(() =>
        {
            if (IsDisposed)
            {
                return;
            }

            foreach (var item in items)
            {
                item.RefreshText();
            }
        });
    }

    /// <summary>
    /// Stops every timer and drops the process-wide reference; idempotent and callable from any thread.
    /// <para>The composition root tears the graph down with <c>ConfigureAwait(false)</c>, so this may run on a pool thread
    /// while the window is closing. The timers and the dictionary are not bound to anything, so they are stopped in place;
    /// the toast collection is read by the view, so it is only cleared on its own thread — directly when this is that
    /// thread, otherwise through <c>post</c>. Nothing posts into the collection after this point: every queued action and
    /// timer callback is dropped once <see cref="IsDisposed"/> is set.</para>
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        Loc.Instance.LanguageChanged -= OnLanguageChanged;
        if (ReferenceEquals(Current, this))
        {
            Current = null;
        }

        foreach (var entry in entries.Values)
        {
            entry.Timer?.Dispose();
            entry.LeaveTimer?.Dispose();
        }

        entries.Clear();

        if (Environment.CurrentManagedThreadId == uiThread)
        {
            items.Clear();
        }
        else
        {
            post(items.Clear);
        }
    }

    /// <summary>One visible toast and the two timers that decide its life.</summary>
    private sealed class Entry(NotificationItemViewModel item)
    {
        public NotificationItemViewModel Item { get; } = item;

        /// <summary>The stay timer; replaced when a repeat restarts the stay.</summary>
        public ITimer? Timer { get; set; }

        /// <summary>The removal timer of an item that is leaving.</summary>
        public ITimer? LeaveTimer { get; set; }

        /// <summary>Bumped by every (re)start, so a callback of a superseded timer does nothing.</summary>
        public int Version { get; set; }
    }
}
