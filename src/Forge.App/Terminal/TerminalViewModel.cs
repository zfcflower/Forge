using System.Diagnostics;
using System.Globalization;
using System.Threading.Channels;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using Forge.Abstractions;
using Forge.App.Localization;
using Forge.App.Notifications;
using Forge.App.Preferences;
using Forge.App.Terminal.Vt;
using Forge.App.ViewModels;
using Forge.HostClient;
using Forge.Ipc;
using Forge.App.Logging;
using ZLogger;

namespace Forge.App.Terminal;

/// <summary>Where a terminal is in its lifetime; the tab keeps the screen whatever the state is.</summary>
internal enum TerminalStatus
{
    /// <summary>The host was asked for a pseudo-terminal and has not answered yet.</summary>
    Starting,

    /// <summary>The process tree is alive; output is being parsed.</summary>
    Running,

    /// <summary>The process finished; the screen keeps its content and the exit code is shown below it.</summary>
    Exited,

    /// <summary>The terminal could not start, or the connection carrying it ended.</summary>
    Failed,
}

/// <summary>
/// One terminal tab: it owns a <see cref="TerminalEmulator"/>, pumps the session's output into it on a background thread
/// and coalesces the resulting redraws into at most one UI-thread notification per frame (the same scheme as
/// <see cref="FrameCoalescer{T}"/>). The UI thread only renders and takes input: it briefly holds <see cref="Gate"/> to
/// read the visible rows, while parsing happens on the pump thread under the same lock.
/// <para>The emulator's side effects (device answers, title, bell, OSC 52) come back through
/// <see cref="ITerminalEmulatorHost"/>: answers are written to the session's input, the title becomes the tab title,
/// clipboard text goes to the Avalonia clipboard of whichever view is attached.</para>
/// </summary>
internal sealed class TerminalViewModel : NotifyingObject, ITerminalEmulatorHost, IAsyncDisposable
{
    /// <summary>Columns a terminal starts with before its view has been laid out.</summary>
    internal const int InitialColumns = 100;

    /// <summary>Rows a terminal starts with before its view has been laid out.</summary>
    internal const int InitialRows = 30;

    private readonly Lock gate = new();
    private readonly AppPreferences preferences;
    private readonly ITerminalHost? host;
    private readonly TerminalStartRequest request;
    private readonly TerminalEmulator emulator;
    private readonly CancellationTokenSource lifetime = new();
    private readonly List<byte[]> pendingReplies = [];
    private readonly int displayIndex;
    private readonly int uiThread = Environment.CurrentManagedThreadId;

    private ITerminalSession? session;
    private IClipboard? clipboard;
    private Task? pump;

    // The ordered input path: UI keystrokes and the emulator's device answers all go through this one channel, drained by
    // a single writer task, so the host sees the bytes in exactly the order they were produced.
    private ChannelWriter<byte[]>? inputWriter;
    private Task? inputTask;
    private string title = string.Empty;
    private string statusText = string.Empty;
    private string? problemKey;
    private string? problemDetail;
    private int exitCode;
    private int dirtyScheduled;
    private int disposed;

    // Pending UI-thread state, written by the pump (and by the emulator's host callbacks) under `gate`.
    private TerminalStatus? pendingStatus;
    private bool pendingTitleChanged;

    private TerminalStatus status = TerminalStatus.Starting;
    private bool isClosePromptVisible;
    private TerminalColor defaultForeground;
    private TerminalColor defaultBackground;

    // Buffer search: the query, the last result set and the cursor into it. A scan runs off the UI thread after a short
    // debounce; the generation counter makes every result that a later keystroke already superseded a no-op.
    private string searchText = string.Empty;
    private bool searchCaseSensitive;
    private bool isSearchVisible;
    private TerminalSearchMatch[] searchMatches = [];
    private int searchIndex = -1;
    private int searchGeneration;
    private bool searchPending;
    private CancellationTokenSource? searchDebounce;

    /// <summary>How long the query box waits after a keystroke before the buffer is scanned.</summary>
    private static readonly TimeSpan SearchDebounceDelay = TimeSpan.FromMilliseconds(150);

    /// <param name="sessionId">The session whose working directory the terminal runs in.</param>
    /// <param name="displayIndex">1-based number used for the fallback tab title ("Terminal n").</param>
    /// <param name="host">The terminal host, or null when the extension host is not available.</param>
    /// <param name="request">The start request; its working directory is the session worktree.</param>
    /// <param name="preferences">Live preferences: font, shell and scrollback are read from here.</param>
    public TerminalViewModel(
        string sessionId,
        int displayIndex,
        ITerminalHost? host,
        TerminalStartRequest request,
        AppPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(preferences);

        Id = Guid.NewGuid().ToString("N")[..12];
        SessionId = sessionId;
        this.displayIndex = displayIndex;
        this.host = host;
        this.request = request;
        this.preferences = preferences;

        emulator = new TerminalEmulator(
            request.Size.Columns,
            request.Size.Rows,
            preferences.TerminalScrollbackLines,
            this);

        var palette = TerminalPalette.For(dark: false);
        defaultForeground = TerminalColor.Rgb(
            palette.FallbackForeground.R, palette.FallbackForeground.G, palette.FallbackForeground.B);
        defaultBackground = TerminalColor.Rgb(
            palette.FallbackBackground.R, palette.FallbackBackground.G, palette.FallbackBackground.B);

        preferences.PropertyChanged += OnPreferencesChanged;
        Loc.Instance.LanguageChanged += OnLanguageChanged;
        statusText = Loc.Instance[TerminalStrings.StatusStarting];

        ConfirmCloseCommand = new RelayCommand(ConfirmClose);
        CancelCloseCommand = new RelayCommand(CancelClose);
        SearchNextCommand = new RelayCommand(() => StepSearch(1));
        SearchPreviousCommand = new RelayCommand(() => StepSearch(-1));
        CloseSearchCommand = new RelayCommand(CloseSearch);
        RestartCommand = new RelayCommand(Restart, () => CanRestart);
    }

    /// <summary>Stable id of the terminal; the tab id is <c>terminal:</c> plus this.</summary>
    public string Id { get; }

    public string SessionId { get; }

    /// <summary>Guards <see cref="Emulator"/>: the pump feeds it, the renderer reads the visible rows.</summary>
    internal Lock Gate => gate;

    /// <summary>The screen; only touched while <see cref="Gate"/> is held.</summary>
    internal TerminalEmulator Emulator => emulator;

    /// <summary>The live preferences: the code font drives the cell size, the scrollback line count the ring.</summary>
    internal AppPreferences Preferences => preferences;

    /// <summary>Raised on the UI thread after the screen changed; the view invalidates and repaints once.</summary>
    public event EventHandler? Invalidated;

    /// <summary>Raised on the UI thread when the OSC title changed, so the coordinator can retitle the tab.</summary>
    public event EventHandler? TitleChanged;

    /// <summary>Called when the user confirms the close banner; the coordinator ends the process and closes the tab.</summary>
    public Action? ConfirmCloseRequested { get; set; }

    public TerminalStatus Status
    {
        get => status;
        private set
        {
            if (SetAndNotify(ref status, value, nameof(ShowStatus)))
            {
                OnPropertyChanged(nameof(CanRestart));
                RestartCommand.NotifyCanExecuteChanged();
            }
        }
    }

    /// <summary>The line under the screen: starting, the exit code, or why the terminal stopped. Empty while running.</summary>
    public string StatusText
    {
        get => statusText;
        private set => Set(ref statusText, value);
    }

    /// <summary>True while the status line has something to say (anything but a running terminal).</summary>
    public bool ShowStatus => status != TerminalStatus.Running;

    /// <summary>The OSC 0/1/2 title, or empty when the application never set one.</summary>
    public string Title => title;

    /// <summary>What the tab shows: the application's title, or the localized "Terminal n".</summary>
    public string TabTitle => title.Length > 0 ? title : Loc.Instance.Format(TerminalStrings.TabTitle, displayIndex);

    public bool IsClosePromptVisible
    {
        get => isClosePromptVisible;
        private set => Set(ref isClosePromptVisible, value);
    }

    public string ClosePromptText => Loc.Instance[TerminalStrings.ClosePrompt];

    public string ConfirmCloseText => Loc.Instance[TerminalStrings.CloseConfirm];

    public string CancelCloseText => Loc.Instance[TerminalStrings.CloseCancel];

    public RelayCommand ConfirmCloseCommand { get; }

    public RelayCommand CancelCloseCommand { get; }

    // ---------------------------------------------------------------- restart

    /// <summary>
    /// True once the process is over: a terminal that exited or failed can be restarted in place, and the tab, its screen and
    /// its scrollback stay where they are (forge-app spec, "TerminalViewModel": a finished tab is kept).
    /// </summary>
    public bool CanRestart => status is TerminalStatus.Exited or TerminalStatus.Failed;

    /// <summary>Restarts an exited or failed terminal; the command is disabled while the terminal is starting or running.</summary>
    public RelayCommand RestartCommand { get; }

    public string RestartText => Loc.Instance[TerminalStrings.Restart];

    /// <summary>
    /// Starts a fresh process over the same request (working directory, shell, arguments) in this same view model: the
    /// emulator is never reset, so the tab keeps the old screen and its scrollback, and the new output continues underneath.
    /// A restart that cannot start reports the same localized failure as the first attempt instead of throwing.
    /// </summary>
    public void Restart()
    {
        if (!CanRestart || Volatile.Read(ref disposed) != 0 || pump is { IsCompleted: false })
        {
            return;
        }

        ITerminalSession? previous;
        lock (gate)
        {
            previous = session;
            session = null;
            pendingStatus = null;
            pendingTitleChanged = false;
            exitCode = 0;
            problemKey = null;
            problemDetail = null;
        }

        // The old writer belongs to the process that just ended; the new pump publishes its own before it reports Running.
        Volatile.Read(ref inputWriter)?.TryComplete();
        Volatile.Write(ref inputWriter, null);
        inputTask = null;
        pump = null;

        Status = TerminalStatus.Starting;
        StatusText = Loc.Instance[TerminalStrings.StatusStarting];
        if (previous is not null)
        {
            _ = DisposeSessionAsync(previous);
        }

        pump = Task.Run(PumpAsync);
        Invalidate();
    }

    // ---------------------------------------------------------------- buffer search

    /// <summary>Raised on the UI thread when the search bar opens, so the view can focus and select the query box.</summary>
    public event EventHandler? SearchOpened;

    /// <summary>Raised on the UI thread when the current match changed, so the surface can scroll it into view.</summary>
    public event EventHandler? SearchMatchChanged;

    /// <summary>True while the search bar is part of the view.</summary>
    public bool IsSearchVisible
    {
        get => isSearchVisible;
        private set => Set(ref isSearchVisible, value);
    }

    /// <summary>The query. Typing schedules a debounced scan; clearing the box drops the highlights immediately.</summary>
    public string SearchText
    {
        get => searchText;
        set
        {
            if (!Set(ref searchText, value ?? string.Empty))
            {
                return;
            }

            OnPropertyChanged(nameof(SearchPlaceholder));
            if (searchText.Length == 0)
            {
                CancelPendingSearch();
                ApplySearchResults([]);
                return;
            }

            ScheduleSearch();
        }
    }

    /// <summary>Case sensitivity of the scan; flipping it re-runs the search.</summary>
    public bool SearchCaseSensitive
    {
        get => searchCaseSensitive;
        set
        {
            if (Set(ref searchCaseSensitive, value) && searchText.Length > 0)
            {
                ScheduleSearch();
            }
        }
    }

    /// <summary>The result set of the last completed scan (empty when there is no query).</summary>
    public IReadOnlyList<TerminalSearchMatch> SearchMatches => searchMatches;

    public int SearchMatchCount => searchMatches.Length;

    /// <summary>The match the next/previous buttons walk; null while there is no result.</summary>
    public TerminalSearchMatch? CurrentSearchMatch =>
        searchIndex >= 0 && searchIndex < searchMatches.Length ? searchMatches[searchIndex] : null;

    /// <summary>"2/7" for the current position; "0/0" while nothing matched.</summary>
    public string SearchMatchLabel => searchMatches.Length == 0
        ? "0/0"
        : (searchIndex + 1).ToString(CultureInfo.InvariantCulture) + "/" +
          searchMatches.Length.ToString(CultureInfo.InvariantCulture);

    /// <summary>True while a query is typed but the buffer holds no match: the bar says so instead of showing "0/0".</summary>
    public bool ShowSearchNoMatches => searchText.Length > 0 && searchMatches.Length == 0 && !searchPending;

    public string SearchPlaceholder => Loc.Instance[TerminalStrings.SearchPlaceholder];

    public string SearchCaseText => Loc.Instance[TerminalStrings.SearchCase];

    public string SearchPreviousText => Loc.Instance[TerminalStrings.SearchPrevious];

    public string SearchNextText => Loc.Instance[TerminalStrings.SearchNext];

    public string SearchCloseText => Loc.Instance[TerminalStrings.SearchClose];

    public string SearchNoMatchesText => Loc.Instance[TerminalStrings.SearchNoMatches];

    public RelayCommand SearchNextCommand { get; }

    public RelayCommand SearchPreviousCommand { get; }

    public RelayCommand CloseSearchCommand { get; }

    /// <summary>Shows the search bar; the query and its matches are kept, so reopening it continues where the user left off.</summary>
    public void OpenSearch()
    {
        if (Volatile.Read(ref disposed) != 0)
        {
            return;
        }

        IsSearchVisible = true;
        SearchOpened?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Hides the bar and drops the highlights; the screen itself is untouched.</summary>
    public void CloseSearch()
    {
        IsSearchVisible = false;
        CancelPendingSearch();
        ApplySearchResults([]);
    }

    /// <summary>
    /// Scans the buffer now for the current query and applies the result before it returns. The debounced path calls the same
    /// scan; this entry point is what the view uses when it needs the result synchronously.
    /// </summary>
    public Task RunSearchAsync() => RunSearchCoreAsync();

    /// <summary>Moves to the next match, wrapping at the end.</summary>
    public void SearchNext() => StepSearch(1);

    /// <summary>Moves to the previous match, wrapping at the start.</summary>
    public void SearchPrevious() => StepSearch(-1);

    private void StepSearch(int delta)
    {
        if (searchMatches.Length == 0)
        {
            return;
        }

        searchIndex = ((searchIndex + delta) % searchMatches.Length + searchMatches.Length) % searchMatches.Length;
        RaiseSearchState();
        SearchMatchChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>One pending debounce at a time; the newer keystroke cancels the older scan and invalidates its result.</summary>
    private void ScheduleSearch()
    {
        CancelPendingSearch();
        searchPending = true;
        OnPropertyChanged(nameof(ShowSearchNoMatches));
        var generation = Volatile.Read(ref searchGeneration);
        var pending = new CancellationTokenSource();
        searchDebounce = pending;
        _ = DebouncedSearchAsync(generation, pending);
    }

    private async Task DebouncedSearchAsync(int generation, CancellationTokenSource pending)
    {
        try
        {
            await Task.Delay(SearchDebounceDelay, pending.Token).ConfigureAwait(false);
            var matches = await Task.Run(() => FindMatches(searchText, searchCaseSensitive), pending.Token)
                .ConfigureAwait(false);
            PostToUi(() =>
            {
                if (generation == Volatile.Read(ref searchGeneration) && isSearchVisible)
                {
                    ApplySearchResults(matches);
                }
            });
        }
        catch (OperationCanceledException)
        {
            // A newer keystroke, a closed bar or a disposed tab: this result is stale by definition.
        }
    }

    private async Task RunSearchCoreAsync()
    {
        CancelPendingSearch();
        var generation = Volatile.Read(ref searchGeneration);
        var query = searchText;
        if (query.Length == 0)
        {
            ApplySearchResults([]);
            return;
        }

        searchPending = true;
        TerminalSearchMatch[] matches;
        try
        {
            matches = await Task.Run(() => FindMatches(query, searchCaseSensitive), lifetime.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        // The caller is the UI thread (or a bare host): applying here keeps the result of an awaited scan observable.
        if (generation == Volatile.Read(ref searchGeneration) && Volatile.Read(ref disposed) == 0)
        {
            ApplySearchResults(matches);
        }
    }

    /// <summary>
    /// Extracts the buffer and matches it. The emulator is only read under <see cref="Gate"/>; blank rows are left as the
    /// default line, which the matcher skips, so a mostly empty screen costs no allocations.
    /// </summary>
    private TerminalSearchMatch[] FindMatches(string query, bool caseSensitive)
    {
        TerminalSearchLine[] lines;
        long dropped;
        lock (gate)
        {
            dropped = emulator.ScrollbackDropped;
            var total = emulator.TotalLines;
            lines = new TerminalSearchLine[total];
            for (var line = 0; line < total; line++)
            {
                var row = emulator.GetLine(line);
                if (IsBlank(row))
                {
                    continue;
                }

                lines[line] = TerminalSearch.SnapshotLine(row);
            }
        }

        return [.. TerminalSearch.Find(lines, query, caseSensitive, dropped)];
    }

    private static bool IsBlank(TerminalLine line)
    {
        for (var column = 0; column < line.Count; column++)
        {
            if (line[column].Rune != 0)
            {
                return false;
            }
        }

        return true;
    }

    private void ApplySearchResults(TerminalSearchMatch[] matches)
    {
        searchMatches = matches;
        searchIndex = matches.Length > 0 ? 0 : -1;
        searchPending = false;
        RaiseSearchState();
        // Unconditional: an empty result set must reach the control too, or the previous highlights stay on screen until
        // the next output/scroll happens to redraw it.
        SearchMatchChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RaiseSearchState()
    {
        OnPropertyChanged(nameof(SearchMatches));
        OnPropertyChanged(nameof(SearchMatchCount));
        OnPropertyChanged(nameof(CurrentSearchMatch));
        OnPropertyChanged(nameof(SearchMatchLabel));
        OnPropertyChanged(nameof(ShowSearchNoMatches));
    }

    /// <summary>Drops the pending debounce and invalidates every scan already on its way.</summary>
    private void CancelPendingSearch()
    {
        Interlocked.Increment(ref searchGeneration);
        var pending = Interlocked.Exchange(ref searchDebounce, null);
        if (pending is null)
        {
            return;
        }

        try
        {
            pending.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already cancelled and disposed by a concurrent close; nothing is waiting on it any more.
        }

        pending.Dispose();
    }

    /// <summary>Runs the action on the UI thread when it is not already there: the debounced scan finishes on the pool.</summary>
    private static void PostToUi(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            action();
            return;
        }

        Dispatcher.UIThread.Post(action);
    }

    /// <summary>
    /// Starts the pseudo-terminal and the output pump. A missing host, an unavailable working directory or a host refusal
    /// becomes <see cref="TerminalStatus.Failed"/>: starting a terminal never throws at the caller.
    /// </summary>
    public void Start()
    {
        if (pump is not null || Volatile.Read(ref disposed) != 0)
        {
            return;
        }

        pump = Task.Run(PumpAsync);
    }

    /// <summary>Writes input bytes to the pseudo-terminal; after exit the session drops them, so nothing throws here.</summary>
    public void SendInput(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
        {
            return;
        }

        // Before the session exists (or after it was released) there is no channel and the bytes are dropped.
        Volatile.Read(ref inputWriter)?.TryWrite(bytes.ToArray());
    }

    /// <summary>
    /// Applies a new character grid: the emulator is resized (truncating, as a real terminal without reflow does) and the
    /// host is told, so full-screen programs see the same size. Called on the UI thread after the view's 50 ms debounce.
    /// </summary>
    public void Resize(int columns, int rows)
    {
        columns = Math.Clamp(columns, 1, TerminalEmulator.MaxColumns);
        rows = Math.Clamp(rows, 1, TerminalEmulator.MaxRows);

        ITerminalSession? active;
        bool changed;
        lock (gate)
        {
            changed = columns != emulator.Columns || rows != emulator.Rows;
            if (changed)
            {
                emulator.Resize(columns, rows);
            }

            active = session;
        }

        if (!changed)
        {
            return;
        }

        Invalidate();
        if (active is not null)
        {
            ResizeQuietly(active, new TerminalSize(columns, rows));
        }
    }

    /// <summary>Hands the theme's foreground and background over, so OSC 10/11 queries are answered with what is on screen.</summary>
    public void SetDefaultColors(TerminalColor foreground, TerminalColor background)
    {
        lock (gate)
        {
            defaultForeground = foreground;
            defaultBackground = background;
        }
    }

    /// <summary>Attaches the clipboard of the view that shows this terminal (OSC 52 writes go there).</summary>
    public void AttachClipboard(IClipboard? target) => Volatile.Write(ref clipboard, target);

    /// <summary>True while the shell has a foreground child, i.e. closing the terminal would end work in progress.</summary>
    public async ValueTask<bool> HasForegroundProcessAsync(CancellationToken ct)
    {
        ITerminalSession? active;
        lock (gate)
        {
            active = session;
        }

        if (active is null || status != TerminalStatus.Running)
        {
            return false;
        }

        try
        {
            return await active.HasForegroundProcessAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // A probe that ran out of its budget says nothing about the process tree; the caller decides what a timeout
            // means (closing a terminal treats it as "a program may still be running").
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            AppLogs.Terminal.ZLogWarning($"foreground probe failed: {exception.Message}");
            return false;
        }
    }

    /// <summary>Shows the close banner: the coordinator vetoed the close because a program is still running.</summary>
    public void ShowClosePrompt() => IsClosePromptVisible = true;

    public ValueTask DisposeAsync() => DisposeCoreAsync();

    // ---------------------------------------------------------------- emulator host

    void ITerminalEmulatorHost.Respond(ReadOnlySpan<byte> bytes)
    {
        // Called under `gate` while the pump parses; the bytes are written to the session after the lock is released.
        pendingReplies.Add(bytes.ToArray());
    }

    void ITerminalEmulatorHost.TitleChanged(string value)
    {
        lock (gate)
        {
            title = value;
            pendingTitleChanged = true;
        }

        MarkDirty();
    }

    void ITerminalEmulatorHost.Bell()
    {
        // BEL is deliberately silent: the tab is usually in a window the user is not looking at, and a system beep on every
        // completion would be worse than none.
    }

    void ITerminalEmulatorHost.ClipboardWrite(string text)
    {
        var target = Volatile.Read(ref clipboard);
        if (target is null || string.IsNullOrEmpty(text))
        {
            return;
        }

        if (Dispatcher.UIThread.CheckAccess())
        {
            WriteClipboard(target, text);
            return;
        }

        Dispatcher.UIThread.Post(() => WriteClipboard(target, text));
    }

    TerminalColor ITerminalEmulatorHost.DefaultForeground
    {
        get
        {
            lock (gate)
            {
                return defaultForeground;
            }
        }
    }

    TerminalColor ITerminalEmulatorHost.DefaultBackground
    {
        get
        {
            lock (gate)
            {
                return defaultBackground;
            }
        }
    }

    // ---------------------------------------------------------------- pump

    private async Task PumpAsync()
    {
        var ct = lifetime.Token;
        ITerminalSession? started = null;
        var handedOff = false;
        try
        {
            if (host is null)
            {
                Fail(TerminalStrings.FailureHostUnavailable, null);
                return;
            }

            if (request.WorkingDirectory.Length == 0)
            {
                Fail(TerminalStrings.FailureNoSession, null);
                return;
            }

            started = await host.StartAsync(request, ct).ConfigureAwait(false);
            int columns = 0;
            int rows = 0;
            bool released;
            lock (gate)
            {
                // DisposeAsync may have run while the host was still starting: taking the session under the same lock
                // that DisposeAsync nulls it under closes the window where nobody would ever stop it.
                released = Volatile.Read(ref disposed) != 0;
                if (!released)
                {
                    session = started;
                    handedOff = true;
                    var channel = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions
                    {
                        SingleReader = true,
                        SingleWriter = false,
                        AllowSynchronousContinuations = false,
                    });
                    Volatile.Write(ref inputWriter, channel.Writer);
                    inputTask = Task.Run(() => RunInputAsync(started, channel.Reader));
                    columns = emulator.Columns;
                    rows = emulator.Rows;
                }
            }

            if (released)
            {
                await DisposeSessionAsync(started).ConfigureAwait(false);
                started = null;
                return;
            }

            PostStatus(TerminalStatus.Running);

            // The view may have been laid out while the host was still starting: tell it the real grid now, not only on the
            // next size change, or a full-screen program would keep the placeholder size for the whole session.
            if (columns != request.Size.Columns || rows != request.Size.Rows)
            {
                ResizeQuietly(started, new TerminalSize(columns, rows));
            }

            try
            {
                while (await started.Output.WaitToReadAsync(ct).ConfigureAwait(false))
                {
                    while (started.Output.TryRead(out var chunk))
                    {
                        Feed(chunk.Span);
                    }
                }
            }
            catch (ChannelClosedException closed) when (closed.InnerException is { } reason)
            {
                // The reader completes with the connection error wrapped: the unwrapped exception is what to show.
                Fail(null, Describe(reason));
                return;
            }

            int code;
            try
            {
                code = await started.Exited.ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                Fail(null, Describe(exception));
                return;
            }

            PostStatus(TerminalStatus.Exited, code);
            if (code != 0)
            {
                // A non-zero exit is the one terminal event worth interrupting for; a clean exit is what the user asked for.
                NotificationCenter.Current?.Notify(
                    NotificationLevel.Warning,
                    NotificationsStrings.TerminalExitedTitle,
                    NotificationsStrings.TerminalExited(TabTitle, code),
                    "terminal:" + SessionId + ":" + displayIndex);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Disposed: the tab is gone and the session is being stopped by DisposeAsync.
        }
        catch (ChannelClosedException closed)
        {
            Fail(null, Describe(closed.InnerException ?? closed));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Fail(null, Describe(exception));
        }
        finally
        {
            // The terminal is over (exit, failure or dispose): no further input can matter, so the writer task drains and ends.
            Volatile.Read(ref inputWriter)?.TryComplete();

            // Every path that never handed the session over (a cancelled start, a failure between StartAsync and the
            // assignment) stops it here; a handed-off session belongs to DisposeAsync.
            if (!handedOff && started is not null)
            {
                await DisposeSessionAsync(started).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Stops a session nobody else owns, swallowing the failure: the terminal is already gone.</summary>
    private static async ValueTask DisposeSessionAsync(ITerminalSession session)
    {
        try
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            AppLogs.Terminal.ZLogWarning($"stopping the terminal failed: {exception.Message}");
        }
    }

    /// <summary>Feeds one chunk and forwards the answers the parser produced while the lock was held.</summary>
    private void Feed(ReadOnlySpan<byte> data)
    {
        byte[]? replies = null;
        lock (gate)
        {
            emulator.Feed(data);
            if (pendingReplies.Count > 0)
            {
                var total = 0;
                for (var i = 0; i < pendingReplies.Count; i++)
                {
                    total += pendingReplies[i].Length;
                }

                replies = new byte[total];
                var offset = 0;
                foreach (var reply in pendingReplies)
                {
                    reply.CopyTo(replies, offset);
                    offset += reply.Length;
                }

                pendingReplies.Clear();
            }
        }

        MarkDirty();
        if (replies is not null)
        {
            Volatile.Read(ref inputWriter)?.TryWrite(replies);
        }
    }

    private void PostStatus(TerminalStatus value, int code = 0, string? key = null, string? detail = null)
    {
        lock (gate)
        {
            pendingStatus = value;
            exitCode = code;
            problemKey = key;
            problemDetail = detail;
        }

        MarkDirty();
    }

    private void Fail(string? key, string? detail) => PostStatus(TerminalStatus.Failed, 0, key, detail);

    /// <summary>The sentence the failure state shows: the transport's own message says what happened to the connection.</summary>
    private static string Describe(Exception exception) => exception.Message;

    // ---------------------------------------------------------------- UI-thread refresh

    private void MarkDirty()
    {
        if (Volatile.Read(ref disposed) != 0)
        {
            return;
        }

        if (Interlocked.Exchange(ref dirtyScheduled, 1) == 0)
        {
            Dispatcher.UIThread.Post(Flush, DispatcherPriority.Background);
        }
    }

    /// <summary>Runs on the UI thread: applies the queued state and repaints at most once per dispatcher pass.</summary>
    private void Flush()
    {
        if (Interlocked.Exchange(ref dirtyScheduled, 0) == 0 || Volatile.Read(ref disposed) != 0)
        {
            return;
        }

        TerminalStatus? next;
        string nextTitle;
        bool titleChanged;
        int code;
        string? key;
        string? detail;
        lock (gate)
        {
            next = pendingStatus;
            pendingStatus = null;
            nextTitle = title;
            titleChanged = pendingTitleChanged;
            pendingTitleChanged = false;
            code = exitCode;
            key = problemKey;
            detail = problemDetail;
        }

        if (next is { } value)
        {
            Status = value;
            StatusText = BuildStatusText(value, code, key, detail);
        }

        if (titleChanged)
        {
            OnPropertyChanged(nameof(Title));
            OnPropertyChanged(nameof(TabTitle));
            TitleChanged?.Invoke(this, EventArgs.Empty);
        }

        Invalidated?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Paints the change immediately when the caller is already on the UI thread (resize, clear, preference change).</summary>
    private void Invalidate()
    {
        if (Volatile.Read(ref disposed) != 0)
        {
            return;
        }

        if (Dispatcher.UIThread.CheckAccess())
        {
            Invalidated?.Invoke(this, EventArgs.Empty);
            return;
        }

        MarkDirty();
    }

    private string BuildStatusText(TerminalStatus value, int code, string? key, string? detail) => value switch
    {
        TerminalStatus.Starting => Loc.Instance[TerminalStrings.StatusStarting],
        TerminalStatus.Running => string.Empty,
        TerminalStatus.Exited => Loc.Instance.Format(TerminalStrings.StatusExited, code),
        _ => Loc.Instance.Format(
            TerminalStrings.StatusFailed,
            key is not null ? Loc.Instance[key] : detail ?? string.Empty),
    };

    private void ConfirmClose()
    {
        IsClosePromptVisible = false;
        ConfirmCloseRequested?.Invoke();
    }

    private void CancelClose() => IsClosePromptVisible = false;

    private void OnPreferencesChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(AppPreferences.TerminalScrollbackLines))
        {
            return;
        }

        lock (gate)
        {
            emulator.ScrollbackLimit = preferences.TerminalScrollbackLines;
        }

        Invalidate();
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        // A switch raised on another thread belongs to a window that is gone: the tab and the close prompt
        // are bound on the thread that built the terminal.
        if (Volatile.Read(ref disposed) != 0 || Environment.CurrentManagedThreadId != uiThread)
        {
            return;
        }

        OnPropertyChanged(nameof(TabTitle));
        OnPropertyChanged(nameof(ClosePromptText));
        OnPropertyChanged(nameof(ConfirmCloseText));
        OnPropertyChanged(nameof(CancelCloseText));
        OnPropertyChanged(nameof(RestartText));
        OnPropertyChanged(nameof(SearchPlaceholder));
        OnPropertyChanged(nameof(SearchCaseText));
        OnPropertyChanged(nameof(SearchPreviousText));
        OnPropertyChanged(nameof(SearchNextText));
        OnPropertyChanged(nameof(SearchCloseText));
        OnPropertyChanged(nameof(SearchNoMatchesText));

        TerminalStatus value;
        int code;
        string? key;
        string? detail;
        lock (gate)
        {
            value = status;
            code = exitCode;
            key = problemKey;
            detail = problemDetail;
        }

        StatusText = BuildStatusText(value, code, key, detail);
    }

    private async ValueTask DisposeCoreAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        preferences.PropertyChanged -= OnPreferencesChanged;
        Loc.Instance.LanguageChanged -= OnLanguageChanged;
        CancelPendingSearch();
        lifetime.Cancel();
        Volatile.Read(ref inputWriter)?.TryComplete();

        ITerminalSession? active;
        lock (gate)
        {
            active = session;
            session = null;
        }

        if (active is not null)
        {
            await DisposeSessionAsync(active).ConfigureAwait(false);
        }

        if (pump is { } task)
        {
            try
            {
                await task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                // Cancelled or already failed: the pump has nothing left to report at this point.
                AppLogs.Terminal.ZLogWarning($"output pump ended: {exception.Message}");
            }
        }

        if (inputTask is { } writing)
        {
            try
            {
                await writing.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                AppLogs.Terminal.ZLogWarning($"input writer ended: {exception.Message}");
            }
        }

        lifetime.Dispose();
    }

    // ---------------------------------------------------------------- input helpers

    /// <summary>The single writer of the terminal's input: it forwards queued chunks one at a time, in order. A dead
    /// session only leaves a diagnostic; the loop ends when the channel is completed or the tab is disposed.</summary>
    private async Task RunInputAsync(ITerminalSession active, ChannelReader<byte[]> reader)
    {
        var ct = lifetime.Token;
        try
        {
            await foreach (var bytes in reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                try
                {
                    await active.WriteAsync(bytes, ct).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OutOfMemoryException
                    && !(exception is OperationCanceledException && ct.IsCancellationRequested))
                {
                    // Any failure of one chunk (closed session, timeout, ...) must not end the writer: later input
                    // still goes out in order. Only the tab's own cancellation ends the loop (outer catch).
                    AppLogs.Terminal.ZLogWarning($"input was not written: {exception.Message}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Disposed while input was queued: the session is being stopped, so the rest is dropped.
        }
    }

    private static void ResizeQuietly(ITerminalSession active, TerminalSize size) =>
        _ = ResizeCoreAsync(active, size);

    private static async Task ResizeCoreAsync(ITerminalSession active, TerminalSize size)
    {
        try
        {
            await active.ResizeAsync(size, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IpcConnectionClosedException or ObjectDisposedException
            or InvalidOperationException or IOException or OperationCanceledException)
        {
            AppLogs.Terminal.ZLogWarning($"resize was not sent: {exception.Message}");
        }
    }

    private static void WriteClipboard(IClipboard target, string text) => _ = WriteClipboardAsync(target, text);

    private static async Task WriteClipboardAsync(IClipboard target, string text)
    {
        try
        {
            await target.SetTextAsync(text).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            AppLogs.Terminal.ZLogWarning($"OSC 52 write failed: {exception.Message}");
        }
    }
}
