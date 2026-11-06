using System.Diagnostics;
using System.Threading.Channels;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using Forge.Abstractions;
using Forge.App.Localization;
using Forge.App.Preferences;
using Forge.App.Terminal.Vt;
using Forge.App.ViewModels;
using Forge.HostClient;
using Forge.Ipc;

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
internal sealed class TerminalViewModel : ObservableObject, ITerminalEmulatorHost, IAsyncDisposable
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
            if (Set(ref status, value))
            {
                OnPropertyChanged(nameof(ShowStatus));
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
            Trace.WriteLine("[terminal] foreground probe failed: " + exception.Message);
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
            Trace.WriteLine("[terminal] stopping the terminal failed: " + exception.Message);
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
        OnPropertyChanged(nameof(TabTitle));
        OnPropertyChanged(nameof(ClosePromptText));
        OnPropertyChanged(nameof(ConfirmCloseText));
        OnPropertyChanged(nameof(CancelCloseText));

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
                Trace.WriteLine("[terminal] output pump ended: " + exception.Message);
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
                Trace.WriteLine("[terminal] input writer ended: " + exception.Message);
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
                    Trace.WriteLine("[terminal] input was not written: " + exception.Message);
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
            Trace.WriteLine("[terminal] resize was not sent: " + exception.Message);
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
            Trace.WriteLine("[terminal] OSC 52 write failed: " + exception.Message);
        }
    }
}
