using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia.Threading;
using Forge.App.Localization;
using Forge.App.Logging;
using Forge.App.Preferences;
using ZLogger;

namespace Forge.App.ViewModels;

/// <summary>
/// One open session as the resource inspector reads it (implemented by <see cref="SessionWorkspaceViewModel"/>). The panel
/// only reads rows through it and listens for "the set changed"; it never writes into a session, and it forgets a session
/// (and its listener) on the first refresh after the shell stopped listing it.
/// </summary>
internal interface IResourceInspectorSource
{
    /// <summary>The session's title, shown on its group header.</summary>
    string Title { get; }

    /// <summary>
    /// UI thread: appends this session's live rows - a <see cref="SubAgentViewModel"/> per live child, a
    /// <see cref="LiveToolViewModel"/> per running call of the root or any child - in any order. Nothing for a closed session.
    /// </summary>
    void CollectRunning(List<object> into);

    /// <summary>UI thread: the rows <see cref="CollectRunning"/> would add changed, or the session is closing.</summary>
    event EventHandler? RunningResourcesChanged;
}

/// <summary>
/// The header row of one session's group in the inspector: its title, how many rows it lists and whether it is the session
/// the window shows. Owned by the panel (one per open session that has something running), never by the session.
/// </summary>
internal sealed class ResourceInspectorGroupViewModel : NotifyingObject
{
    private string title;
    private int count;
    private bool isActive;

    internal ResourceInspectorGroupViewModel(IResourceInspectorSource source)
    {
        Source = source;
        title = source.Title;
    }

    internal IResourceInspectorSource Source { get; }

    /// <summary>The refresh pass that last saw this session listed by the shell; an older value means it closed.</summary>
    internal int Generation { get; set; }

    public string Title => title;

    public int Count => count;

    /// <summary>"2 项进行中" for this session's rows.</summary>
    public string CountText => ResourceInspectorStrings.Count(count);

    /// <summary>True for the session the window shows; its group is listed first and tagged.</summary>
    public bool IsActive => isActive;

    public string ActiveText => ResourceInspectorStrings.ActiveSession;

    /// <summary>UI thread: re-reads the title and applies the pass's count and active flag, raising only what moved.</summary>
    internal void Update(int rows, bool active)
    {
        var currentTitle = Source.Title;
        if (!string.Equals(currentTitle, title, StringComparison.Ordinal))
        {
            title = currentTitle;
            OnPropertyChanged(nameof(Title));
        }

        if (rows != count)
        {
            count = rows;
            OnPropertyChanged(nameof(Count));
            OnPropertyChanged(nameof(CountText));
        }

        if (active != isActive)
        {
            isActive = active;
            OnPropertyChanged(nameof(IsActive));
        }
    }

    internal void RefreshText()
    {
        OnPropertyChanged(nameof(CountText));
        OnPropertyChanged(nameof(ActiveText));
    }
}

/// <summary>
/// The resource inspector (wave W8, 缺口 #13): everything the open sessions are running right now - their streaming tool
/// calls and their sub-agents - in one list grouped by session (the active session first), with the elapsed time, the tail
/// of the output and a stop button per row.
/// <para>It is a <em>pull</em>: <see cref="Refresh"/> asks every open session for what runs and reconciles the rows. It runs
/// on the panel's own timer (<c>AppPreferences.InspectorRefreshMilliseconds</c>) only while the overlay is open, and when a
/// session reports that its set changed (<see cref="IResourceInspectorSource.RunningResourcesChanged"/>) - those requests are
/// coalesced into one background-priority refresh, however many arrive in a burst. Progress itself is never dispatched: the
/// session publishes each snapshot into its entry and this panel reads it on the next pull.</para>
/// <para>Rows are never rebuilt: a row appears when something starts and goes away when it stops (or its session closes),
/// so a binding, the scroll position and a collapsed row all survive every pull. The pull reuses its buffers, so a steady
/// state allocates nothing beyond what the sessions' own enumerators need. Stops go through the row's own command, which
/// belongs to the row's session: a sub-agent stops through <c>SubAgentControl</c>, a tool call through Core's single-call
/// cancel (the turn goes on).</para>
/// </summary>
internal sealed class ResourceInspectorViewModel : NotifyingObject, IDisposable
{
    /// <summary>The default driver: a dispatcher timer at the UI's background priority. A static field, so the fallback costs no closure.</summary>
    private static readonly Func<TimeSpan, Action, IDisposable> DefaultSchedule =
        static (interval, tick) => new DispatcherTicker(interval, tick);

    /// <summary>The default coalesced refresh: one background-priority post, below input and render.</summary>
    private static readonly Action<Action> DefaultPost =
        static action => Dispatcher.UIThread.Post(action, DispatcherPriority.Background);

    private static readonly Comparison<object> RowOrder = CompareRows;

    /// <summary>Fills the list with every open session, in the shell's order; asked once per pull.</summary>
    private readonly Action<List<IResourceInspectorSource>> collectSessions;

    /// <summary>The session the window shows (its group goes first); null when none.</summary>
    private readonly Func<IResourceInspectorSource?> activeSession;
    private readonly AppPreferences preferences;
    private readonly Action? close;
    private readonly Func<TimeSpan, Action, IDisposable> schedule;
    private readonly Action<Action> post;
    private readonly ObservableCollection<object> items = [];

    /// <summary>One header per open session, keyed by the session itself; each entry also owns the panel's listener on it.</summary>
    private readonly Dictionary<IResourceInspectorSource, ResourceInspectorGroupViewModel> groups =
        new(ReferenceEqualityComparer.Instance);

    // Buffers reused by every pull, so a refresh at a steady state allocates nothing of its own.
    private readonly List<IResourceInspectorSource> sources = [];
    private readonly List<object> desired = [];
    private readonly List<object> scratch = [];
    private readonly List<IResourceInspectorSource> stale = [];
    private readonly HashSet<object> desiredSet = new(ReferenceEqualityComparer.Instance);

    private readonly EventHandler onSourceChanged;
    private readonly Action runQueuedRefresh;

    /// <summary>The thread the panel was built on (the UI thread); a language notification from another one is not ours.</summary>
    private readonly int uiThread = Environment.CurrentManagedThreadId;

    private IDisposable? ticker;
    private int generation;
    private int total;
    private bool hasSession;
    private int refreshQueued;
    private bool disposed;

    /// <param name="collectSessions">Appends every open session, in the shell's order; asked once per pull.</param>
    /// <param name="activeSession">The session the window shows; its group is listed first and tagged.</param>
    /// <param name="preferences">The live preferences: the line limit is read on every pull, the refresh interval at every reschedule.</param>
    /// <param name="close">Closes the overlay (its close button, Escape or a click on the scrim).</param>
    /// <param name="schedule">
    /// How the periodic pull is driven. Defaults to a dispatcher timer at the UI's background priority; a test passes its own
    /// and calls <see cref="Refresh"/> directly, so the panel is testable without a message loop.
    /// </param>
    /// <param name="post">How a coalesced refresh is queued; defaults to one background-priority dispatcher post.</param>
    public ResourceInspectorViewModel(
        Action<List<IResourceInspectorSource>> collectSessions,
        Func<IResourceInspectorSource?> activeSession,
        AppPreferences preferences,
        Action? close = null,
        Func<TimeSpan, Action, IDisposable>? schedule = null,
        Action<Action>? post = null)
    {
        ArgumentNullException.ThrowIfNull(collectSessions);
        ArgumentNullException.ThrowIfNull(activeSession);
        ArgumentNullException.ThrowIfNull(preferences);
        this.collectSessions = collectSessions;
        this.activeSession = activeSession;
        this.preferences = preferences;
        this.close = close;
        this.schedule = schedule ?? DefaultSchedule;
        this.post = post ?? DefaultPost;
        onSourceChanged = OnSourceChanged;
        runQueuedRefresh = RunQueuedRefresh;

        Items = new ReadOnlyObservableCollection<object>(items);
        CloseCommand = new RelayCommand(() => this.close?.Invoke(), () => !disposed);
        Loc.Instance.LanguageChanged += OnLanguageChanged;
        preferences.PropertyChanged += OnPreferencesChanged;
    }

    /// <summary>
    /// The rows, flat (the list stays virtualized): per session with something running, a
    /// <see cref="ResourceInspectorGroupViewModel"/> header followed by its <see cref="SubAgentViewModel"/> and
    /// <see cref="LiveToolViewModel"/> rows in the order they started. The active session's group comes first.
    /// </summary>
    public ReadOnlyObservableCollection<object> Items { get; }

    public RelayCommand CloseCommand { get; }

    public string Title => ResourceInspectorStrings.Title;

    public string CloseLabel => ResourceInspectorStrings.Close;

    public string CloseTooltip => ResourceInspectorStrings.CloseTooltip;

    public string Empty => ResourceInspectorStrings.Empty;

    public string EmptyHint => ResourceInspectorStrings.EmptyHint;

    public string NoSession => ResourceInspectorStrings.NoSession;

    /// <summary>False only while the window has no session open at all.</summary>
    public bool HasSession => hasSession;

    /// <summary>Rows listed across every session, headers not counted.</summary>
    public int RunningCount => total;

    public bool HasRunning => total > 0;

    /// <summary>"3 项进行中": the count beside the title, hidden while nothing runs.</summary>
    public string CountText => ResourceInspectorStrings.Count(total);

    /// <summary>Open, a session exists and nothing runs anywhere: the one line that says everything is idle.</summary>
    public bool IsEmpty => hasSession && total == 0;

    /// <summary>Sessions the panel currently listens to (one per open session); for tests, which check that none leaks.</summary>
    internal int ListenedSessions => groups.Count;

    /// <summary>Starts the periodic pull and reads the rows once, so the panel is drawn populated on its first frame.</summary>
    public void Start()
    {
        if (disposed)
        {
            return;
        }

        Schedule();
        Refresh();
    }

    /// <summary>
    /// Queues one refresh; further requests before it runs are absorbed. Safe from any thread (the refresh itself runs where
    /// <c>post</c> puts it, the UI thread by default).
    /// </summary>
    internal void RequestRefresh()
    {
        if (disposed || Interlocked.Exchange(ref refreshQueued, 1) == 1)
        {
            return;
        }

        post(runQueuedRefresh);
    }

    /// <summary>
    /// Reads every open session's live work and reconciles the rows: a finished call or a closed session's rows go, a started
    /// one is inserted at its start-time position inside its session's group. Also re-cuts every running call's tail at the
    /// current line limit. UI thread only.
    /// </summary>
    internal void Refresh()
    {
        if (disposed)
        {
            return;
        }

        var pass = ++generation;
        sources.Clear();
        collectSessions(sources);
        var active = activeSession();
        var maxLines = preferences.InspectorLogTailLines;

        // Listen to every open session (an idle one included: its first call must refresh the panel), forget closed ones.
        foreach (var source in sources)
        {
            if (!groups.TryGetValue(source, out var group))
            {
                group = new ResourceInspectorGroupViewModel(source);
                groups[source] = group;
                source.RunningResourcesChanged += onSourceChanged;
            }

            group.Generation = pass;
        }

        stale.Clear();
        foreach (var pair in groups)
        {
            if (pair.Value.Generation != pass)
            {
                stale.Add(pair.Key);
            }
        }

        foreach (var source in stale)
        {
            source.RunningResourcesChanged -= onSourceChanged;
            groups.Remove(source);
        }

        stale.Clear();

        // The rows wanted now: the active session's group first, then the others in the shell's order.
        desired.Clear();
        var rows = 0;
        if (active is not null && groups.TryGetValue(active, out var activeGroup))
        {
            rows += AppendGroup(activeGroup, isActive: true, maxLines);
        }

        foreach (var source in sources)
        {
            if (!ReferenceEquals(source, active))
            {
                rows += AppendGroup(groups[source], isActive: false, maxLines);
            }
        }

        sources.Clear();
        Reconcile();
        desired.Clear();

        var wasTotal = total;
        var hadSession = hasSession;
        total = rows;
        hasSession = groups.Count > 0;
        if (wasTotal != total)
        {
            OnPropertyChanged(nameof(RunningCount));
            OnPropertyChanged(nameof(HasRunning));
            OnPropertyChanged(nameof(CountText));
        }

        if (wasTotal != total || hadSession != hasSession)
        {
            OnPropertyChanged(nameof(IsEmpty));
        }

        if (hadSession != hasSession)
        {
            OnPropertyChanged(nameof(HasSession));
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        Loc.Instance.LanguageChanged -= OnLanguageChanged;
        preferences.PropertyChanged -= OnPreferencesChanged;
        ticker?.Dispose();
        ticker = null;
        foreach (var source in groups.Keys)
        {
            source.RunningResourcesChanged -= onSourceChanged;
        }

        groups.Clear();
        items.Clear();
        CloseCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Adds one session's header and rows to <see cref="desired"/> when it runs anything; returns its row count.</summary>
    private int AppendGroup(ResourceInspectorGroupViewModel group, bool isActive, int maxLines)
    {
        scratch.Clear();
        group.Source.CollectRunning(scratch);
        group.Update(scratch.Count, isActive);
        if (scratch.Count == 0)
        {
            return 0;
        }

        scratch.Sort(RowOrder);
        desired.Add(group);
        foreach (var row in scratch)
        {
            desired.Add(row);
            if (row is LiveToolViewModel tool)
            {
                // Tail only: the elapsed readings are the session clock's (see LiveToolViewModel.Tick), so this panel's own
                // interval never changes what a duration reads.
                tool.RefreshTail(maxLines);
            }
        }

        var count = scratch.Count;
        scratch.Clear();
        return count;
    }

    /// <summary>
    /// Brings <see cref="items"/> to <see cref="desired"/> with the fewest collection changes: rows that left are removed,
    /// rows that stayed keep their instance (and their container), new ones are inserted, a reordered group is moved.
    /// </summary>
    private void Reconcile()
    {
        desiredSet.Clear();
        foreach (var row in desired)
        {
            desiredSet.Add(row);
        }

        for (var index = items.Count - 1; index >= 0; index--)
        {
            if (!desiredSet.Contains(items[index]))
            {
                items.RemoveAt(index);
            }
        }

        desiredSet.Clear();
        for (var index = 0; index < desired.Count; index++)
        {
            var row = desired[index];
            if (index < items.Count && ReferenceEquals(items[index], row))
            {
                continue;
            }

            var at = IndexOf(row, index + 1);
            if (at >= 0)
            {
                items.Move(at, index);
            }
            else
            {
                items.Insert(index, row);
            }
        }
    }

    private int IndexOf(object row, int from)
    {
        for (var index = from; index < items.Count; index++)
        {
            if (ReferenceEquals(items[index], row))
            {
                return index;
            }
        }

        return -1;
    }

    /// <summary>
    /// Start time first (a queued child reads <see cref="DateTimeOffset.MinValue"/>, so it sorts first), then children before
    /// calls, then the ids - a total order, so equal start times never swap places between two pulls.
    /// </summary>
    private static int CompareRows(object left, object right)
    {
        var byStart = StartOf(left).CompareTo(StartOf(right));
        if (byStart != 0)
        {
            return byStart;
        }

        var byKind = KindOf(left).CompareTo(KindOf(right));
        if (byKind != 0)
        {
            return byKind;
        }

        return (left, right) switch
        {
            (SubAgentViewModel a, SubAgentViewModel b) => string.CompareOrdinal(a.AgentId, b.AgentId),
            (LiveToolViewModel a, LiveToolViewModel b) => string.CompareOrdinal(a.AgentId, b.AgentId) is var byAgent and not 0
                ? byAgent
                : string.CompareOrdinal(a.CallId, b.CallId),
            _ => 0,
        };
    }

    private static int KindOf(object row) => row is SubAgentViewModel ? 0 : 1;

    private static DateTimeOffset StartOf(object row) => row switch
    {
        LiveToolViewModel tool => tool.StartedAt,
        SubAgentViewModel agent => agent.StartedAt,
        _ => DateTimeOffset.MaxValue,
    };

    private void Schedule()
    {
        ticker?.Dispose();
        ticker = null;
        if (disposed)
        {
            return;
        }

        // The setter clamps, so the interval is always within the documented range even if the file was edited by hand.
        var interval = TimeSpan.FromMilliseconds(preferences.InspectorRefreshMilliseconds);
        ticker = schedule(interval, OnTick);
    }

    private void OnSourceChanged(object? sender, EventArgs e) => RequestRefresh();

    private void RunQueuedRefresh()
    {
        Volatile.Write(ref refreshQueued, 0);
        OnTick();
    }

    private void OnTick()
    {
        if (disposed)
        {
            return;
        }

        try
        {
            Refresh();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // A timer callback that throws takes the dispatcher's loop with it; a bad row is not worth the window.
            AppLogs.Instruments.ZLogWarning($"resource inspector refresh failed: {exception.Message}");
        }
    }

    /// <summary>A slider or a typed number: a new interval reschedules, a new line limit applies to the next pull now.</summary>
    private void OnPreferencesChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (disposed)
        {
            return;
        }

        switch (e.PropertyName)
        {
            case nameof(AppPreferences.InspectorRefreshMilliseconds):
                Schedule();
                break;
            case nameof(AppPreferences.InspectorLogTailLines):
                Refresh();
                break;
        }
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        // A notification from another thread belongs to a window that is gone (a test host); re-reading off this one would
        // touch bound controls from the wrong thread.
        if (disposed || Environment.CurrentManagedThreadId != uiThread)
        {
            return;
        }

        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(CloseLabel));
        OnPropertyChanged(nameof(CloseTooltip));
        OnPropertyChanged(nameof(Empty));
        OnPropertyChanged(nameof(EmptyHint));
        OnPropertyChanged(nameof(NoSession));
        OnPropertyChanged(nameof(CountText));
        foreach (var row in items)
        {
            switch (row)
            {
                case LiveToolViewModel tool:
                    tool.RefreshText();
                    break;
                case SubAgentViewModel agent:
                    agent.RefreshText();
                    break;
                case ResourceInspectorGroupViewModel group:
                    group.RefreshText();
                    break;
            }
        }
    }

    /// <summary>The default driver: a background-priority dispatcher timer, stopped by disposing the handle.</summary>
    private sealed class DispatcherTicker : IDisposable
    {
        private readonly DispatcherTimer timer;

        public DispatcherTicker(TimeSpan interval, Action tick)
        {
            ArgumentNullException.ThrowIfNull(tick);
            timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = interval };
            timer.Tick += (_, _) => tick();
            timer.Start();
        }

        public void Dispose() => timer.Stop();
    }
}
