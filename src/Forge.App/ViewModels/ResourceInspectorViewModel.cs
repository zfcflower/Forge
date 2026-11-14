using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia.Threading;
using Forge.App.Localization;
using Forge.App.Logging;
using Forge.App.Preferences;
using ZLogger;

namespace Forge.App.ViewModels;

/// <summary>
/// One session as the resource inspector reads it: the live tool calls, the live sub-agents, and what it calls itself. The
/// inspector never holds a <see cref="SessionWorkspaceViewModel"/> - the shell answers this per pull, so the panel cannot keep
/// a closed session alive and cannot write anything into it.
/// </summary>
/// <param name="Tools">The calls running right now (<see cref="SessionWorkspaceViewModel.RunningTools"/>).</param>
internal sealed record ResourceInspectorSession(
    string Title,
    ICollection<LiveToolViewModel> Tools,
    IReadOnlyList<SubAgentViewModel> SubAgents);

/// <summary>
/// The resource inspector (wave W8, 缺口 #13): everything the active session is running right now - its streaming tool calls
/// and its sub-agents - in one list with the elapsed time, the tail of the output and a stop button.
/// <para>It is a <em>pull</em>: <see cref="Refresh"/> asks the session for what runs and reconciles the rows, and it runs on
/// the panel's own timer (<c>AppPreferences.InspectorRefreshMilliseconds</c>) only while the overlay is open. Progress itself
/// is not dispatched - the session publishes each snapshot into its entry and this panel reads it on the next tick - so the
/// cost of a running command never reaches the dispatcher, and a closed panel costs nothing at all.</para>
/// <para>Rows are never rebuilt: a row appears when something starts and goes away when it stops, so a binding, the scroll
/// position and a collapsed row all survive every tick. Stops go through the row's own command: a sub-agent stops through
/// <c>SubAgentControl</c>, a tool call stops its whole turn, because Core hands a tool the turn's token and nothing finer.</para>
/// </summary>
internal sealed class ResourceInspectorViewModel : NotifyingObject, IDisposable
{
    /// <summary>The default driver: a dispatcher timer at the UI's background priority. A static field, so the fallback costs no closure.</summary>
    private static readonly Func<TimeSpan, Action, IDisposable> DefaultSchedule =
        static (interval, tick) => new DispatcherTicker(interval, tick);

    /// <summary>Where the rows come from: the active session, or null when the window has none (the panel then says so).</summary>
    private readonly Func<ResourceInspectorSession?> sessionSource;
    private readonly AppPreferences preferences;
    private readonly Action? close;
    private readonly Func<TimeSpan, Action, IDisposable> schedule;
    private readonly ObservableCollection<object> items = [];

    /// <summary>The thread the panel was built on (the UI thread); a language notification from another one is not ours.</summary>
    private readonly int uiThread = Environment.CurrentManagedThreadId;

    private IDisposable? ticker;
    private bool disposed;

    /// <param name="sessionSource">The session the rows describe; asked once per pull, and once per property that depends on it.</param>
    /// <param name="preferences">The live preferences: the line limit is read on every pull, the refresh interval at every reschedule.</param>
    /// <param name="close">Closes the overlay (its close button, Escape or a click on the scrim).</param>
    /// <param name="schedule">
    /// How the periodic pull is driven. Defaults to a dispatcher timer at the UI's background priority; a test passes its own
    /// and calls <see cref="Refresh"/> directly, so the panel is testable without a message loop.
    /// </param>
    public ResourceInspectorViewModel(
        Func<ResourceInspectorSession?> sessionSource,
        AppPreferences preferences,
        Action? close = null,
        Func<TimeSpan, Action, IDisposable>? schedule = null)
    {
        ArgumentNullException.ThrowIfNull(sessionSource);
        ArgumentNullException.ThrowIfNull(preferences);
        this.sessionSource = sessionSource;
        this.preferences = preferences;
        this.close = close;
        this.schedule = schedule ?? DefaultSchedule;

        Items = new ReadOnlyObservableCollection<object>(items);
        CloseCommand = new RelayCommand(() => this.close?.Invoke(), () => !disposed);
        Loc.Instance.LanguageChanged += OnLanguageChanged;
        preferences.PropertyChanged += OnPreferencesChanged;
    }

    /// <summary>
    /// The rows: a <see cref="SubAgentViewModel"/> for a live child, a <see cref="LiveToolViewModel"/> for a running call, in
    /// the order they started.
    /// </summary>
    public ReadOnlyObservableCollection<object> Items { get; }

    public RelayCommand CloseCommand { get; }

    public string Title => ResourceInspectorStrings.Title;

    /// <summary>The session whose work is listed, beside the title: the overlay covers the window, and "which session" is the
    /// first thing it has to answer. Empty while no session exists (the state text says that instead).</summary>
    public string SessionTitle => sessionSource()?.Title ?? string.Empty;

    public bool HasSessionTitle => SessionTitle.Length > 0;

    public string CloseLabel => ResourceInspectorStrings.Close;

    public string CloseTooltip => ResourceInspectorStrings.CloseTooltip;

    public string Empty => ResourceInspectorStrings.Empty;

    public string EmptyHint => ResourceInspectorStrings.EmptyHint;

    public string NoSession => ResourceInspectorStrings.NoSession;

    /// <summary>False only in the window that has no session open at all; the shell closes the panel on a session switch.</summary>
    public bool HasSession => sessionSource() is not null;

    public bool HasRunning => items.Count > 0;

    /// <summary>"3 个运行中": the count beside the title, hidden while nothing runs.</summary>
    public string CountText => ResourceInspectorStrings.Count(items.Count);

    /// <summary>Open, a session exists and nothing runs: the one line that says the session is idle.</summary>
    public bool IsEmpty => HasSession && items.Count == 0;

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
    /// Reads the session's live work and reconciles the rows: a finished call goes, a started one is inserted at its
    /// start-time position. Also re-cuts every running call's tail at the current line limit. UI thread only.
    /// </summary>
    internal void Refresh()
    {
        if (disposed)
        {
            return;
        }

        var session = sessionSource();
        var maxLines = preferences.InspectorLogTailLines;

        var wanted = new List<object>((session?.SubAgents.Count ?? 0) + (session?.Tools.Count ?? 0));
        if (session is not null)
        {
            foreach (var agent in session.SubAgents)
            {
                wanted.Add(agent);
            }

            foreach (var tool in session.Tools)
            {
                wanted.Add(tool);
            }
        }

        // Rows that stopped running leave; the ones that remain keep their position, their binding and their collapsed state.
        for (var index = items.Count - 1; index >= 0; index--)
        {
            if (!wanted.Contains(items[index], ReferenceEqualityComparer.Instance))
            {
                items.RemoveAt(index);
            }
        }

        foreach (var row in wanted)
        {
            if (!items.Contains(row))
            {
                // Inserted at its start-time position, not appended: rows arrive here when the panel opens (all of them at
                // once) and when something starts (after older rows), and the list must read as a timeline either way.
                items.Insert(IndexOfStart(row), row);
            }
        }

        if (session is not null)
        {
            foreach (var tool in session.Tools)
            {
                // Tail only: the elapsed readings are the session clock's (see LiveToolViewModel.Tick), so this panel's own
                // interval never changes what a duration reads.
                tool.RefreshTail(maxLines);
            }
        }

        OnPropertyChanged(nameof(HasRunning));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(HasSession));
        OnPropertyChanged(nameof(SessionTitle));
        OnPropertyChanged(nameof(HasSessionTitle));
        OnPropertyChanged(nameof(CountText));
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
        items.Clear();
    }

    /// <summary>Where a row goes: the first existing row that started later, else the end. Linear over a handful of rows.</summary>
    private int IndexOfStart(object row)
    {
        var startedAt = StartOf(row);
        for (var index = 0; index < items.Count; index++)
        {
            if (StartOf(items[index]) > startedAt)
            {
                return index;
            }
        }

        return items.Count;
    }

    /// <summary>When a row's work started: a queued child reads <see cref="DateTimeOffset.MinValue"/>, so it sorts first.</summary>
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
        OnPropertyChanged(nameof(SessionTitle));
        OnPropertyChanged(nameof(HasSessionTitle));
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