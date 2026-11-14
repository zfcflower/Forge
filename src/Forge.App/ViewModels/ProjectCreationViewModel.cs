using System.Diagnostics;
using Avalonia.Threading;
using Forge.App.Localization;
using Forge.App.Logging;
using Forge.App.Services;
using Forge.Workspace;
using ZLogger;

namespace Forge.App.ViewModels;

/// <summary>
/// The add-project dialog's model: it collects a parent directory and a folder name (plus the remote for a clone), shows why
/// the input is not usable yet, and runs the creation once — reporting git's clone progress while it runs, cancelling on
/// request. The dialog window is only a view over this model, exactly like <see cref="Lifecycle.HandoverDialogModel"/>; the
/// shell receives the created directory and registers it.
/// </summary>
internal sealed class ProjectCreationViewModel : NotifyingObject, IDisposable
{
    /// <summary>How often a clone's progress may reach this model; git reports far more often than a bar can move.</summary>
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Quiet time after the last keystroke before the folder's occupancy is read from disk. The name rules themselves need
    /// no IO and apply at once; only "a folder of that name is already there" costs a directory read, and doing that per
    /// keystroke on the UI thread is exactly the blocking file call this app keeps off the dispatcher.
    /// </summary>
    private static readonly TimeSpan OccupancyDelay = TimeSpan.FromMilliseconds(200);

    private readonly ProjectCreationMode mode;
    private readonly ProjectCreationService service;
    private readonly bool isClone;
    private string url = "";
    private string parentDirectory;
    private string name = "";
    private bool nameEdited;
    private bool derivingName;
    private bool isRunning;
    private LocText? errorText;
    private string? createdDirectory;
    private CancellationTokenSource? runSource;
    private Task<bool>? runTask;
    private bool disposed;

    /// <summary>The name rules that need no disk, recomputed on every keystroke (see <see cref="OccupancyDelay"/>).</summary>
    private ProjectNameProblem shapeProblem;

    /// <summary>Whether a folder of that name already exists in the chosen parent, as the last off-thread check found it.</summary>
    private bool targetOccupied;

    /// <summary>Which occupancy check is the current one; a late answer for an older input is dropped.</summary>
    private int occupancyGeneration;

    private CancellationTokenSource? occupancySource;

    /// <summary>
    /// The newest progress update, written on the thread that reads git's stderr and read here to be shown. Only the
    /// latest value matters, so overwriting it (with <see cref="Volatile"/>) is the whole hand-off.
    /// </summary>
    private GitCloneProgress? latestProgress;

    /// <summary>What the progress line currently shows; touched on the UI thread only.</summary>
    private GitCloneProgress? shownProgress;

    /// <summary>
    /// The throttle clock, read and restarted on the stderr-reader thread only (git's updates all arrive there), so no
    /// UI-thread access ever races it.
    /// </summary>
    private readonly Stopwatch progressClock = Stopwatch.StartNew();

    /// <param name="mode">Clone a remote or create an empty repository.</param>
    /// <param name="service">The creator: validation rules, defaults and the git calls.</param>
    /// <param name="initialUrl">A remote the caller already knows (the clipboard is not read; a shell flow may prefill it).</param>
    public ProjectCreationViewModel(
        ProjectCreationMode mode,
        ProjectCreationService service,
        string? initialUrl = null)
    {
        ArgumentNullException.ThrowIfNull(service);
        this.mode = mode;
        this.service = service;
        isClone = mode == ProjectCreationMode.Clone;
        parentDirectory = service.DefaultParentDirectory;
        if (isClone)
        {
            url = initialUrl ?? string.Empty;
            name = ProjectCreationService.SuggestNameFromUrl(url);
        }

        StartCommand = new AsyncRelayCommand(_ => StartAsync(), () => CanStart);
        CancelCommand = new RelayCommand(Cancel, () => IsRunning);
        Loc.Instance.LanguageChanged += OnLanguageChanged;
        RefreshInputState();
    }

    public event EventHandler? Completed;

    public ProjectCreationMode Mode => mode;

    /// <summary>True for a clone: the remote row and the progress bar only exist then.</summary>
    public bool IsClone => isClone;

    /// <summary>What the dialog is for, in the current language; re-read after a language switch.</summary>
    public string TitleText => isClone ? ProjectCreationStrings.CloneTitle : ProjectCreationStrings.CreateTitle;

    /// <summary>The one sentence under the title saying what this dialog does; re-read after a language switch.</summary>
    public string IntroText => isClone ? ProjectCreationStrings.CloneIntro : ProjectCreationStrings.CreateIntro;

    /// <summary>The remote address; changing it re-derives the folder name while the user has not typed one.</summary>
    public string Url
    {
        get => url;
        set
        {
            if (!Set(ref url, value ?? string.Empty))
            {
                return;
            }

            if (!nameEdited)
            {
                DeriveName();
            }

            RefreshInputState();
        }
    }

    /// <summary>The directory the project folder is created in; chosen with the folder picker or typed.</summary>
    public string ParentDirectory
    {
        get => parentDirectory;
        set
        {
            if (Set(ref parentDirectory, value ?? string.Empty))
            {
                RefreshInputState();
            }
        }
    }

    /// <summary>The project folder's name. Typed by the user, or derived from the URL until it is edited.</summary>
    public string Name
    {
        get => name;
        set
        {
            if (derivingName)
            {
                // The derived suggestion arriving through this setter is not the user's edit; it must not lock the name.
                Set(ref name, value ?? string.Empty);
                return;
            }

            if (!Set(ref name, value ?? string.Empty))
            {
                return;
            }

            nameEdited = true;
            RefreshInputState();
        }
    }

    /// <summary>The folder the run will create, or empty while the input is incomplete.</summary>
    public string DestinationText
    {
        get
        {
            var parent = parentDirectory.Trim();
            var candidate = name.Trim();
            if (candidate.Length == 0 || !Path.IsPathFullyQualified(parent))
            {
                // A relative parent would resolve against the process's directory, which is not where the project goes.
                return string.Empty;
            }

            try
            {
                return ProjectCreationService.ResolveDirectory(
                    new ProjectCreationRequest(mode, parent, candidate, isClone ? url : null));
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
                // A name that cannot resolve to a path yet is exactly what the hint below is complaining about.
                return string.Empty;
            }
        }
    }

    /// <summary>
    /// Why the input cannot be started, or null: one localized sentence for the name, the directory or the URL. The name
    /// rules and the remote's shape are answered synchronously; "a folder of that name exists" arrives from the off-thread
    /// check and raises this property when it does.
    /// </summary>
    public string? Hint => DescribeProblem();

    /// <summary>Whether the hint row takes space; the dialog binds its visibility to this instead of testing the text.</summary>
    public bool HasHint => DescribeProblem() is not null;

    /// <summary>Whether <see cref="StartCommand"/> may run: complete input, not already running, not already created.</summary>
    public bool CanStart => !isRunning && createdDirectory is null && DescribeProblem() is null;

    public bool IsRunning => isRunning;

    /// <summary>The canned failure text of the last attempt (git's own message for a failed clone), or null.</summary>
    public string? Error => errorText?.Resolve();

    /// <summary>Whether the error row takes space; the dialog binds its visibility to this instead of testing the text.</summary>
    public bool HasError => errorText is not null;

    /// <summary>What the clone is doing right now, localized ("Receiving objects 47% · 3.82 MiB/s"); empty for a new repository.</summary>
    public string ProgressText => shownProgress is null ? string.Empty : ProjectCreationStrings.ProgressLine(shownProgress);

    /// <summary>The current phase's own percentage (0–100); 0 means the phase has no ratio and the bar is indeterminate.</summary>
    public double ProgressPercent => shownProgress?.Percent ?? 0;

    public bool IsProgressIndeterminate => shownProgress is not null && shownProgress.Percent is null;

    /// <summary>The created folder once the run succeeded, otherwise null.</summary>
    public string? CreatedDirectory => createdDirectory;

    /// <summary>The primary button's label, from the mode; re-read after a language switch.</summary>
    public string StartText => isClone ? ProjectCreationStrings.CloneStart : ProjectCreationStrings.CreateStart;

    /// <summary>The footer's second label: "Stop" while a creation runs (it can be interrupted), "Cancel" otherwise.</summary>
    public string CancelText => isRunning ? ProjectCreationStrings.Stop : ProjectCreationStrings.Cancel;

    /// <summary>Starts the run; false when it was refused, failed or cancelled. Twice in a row is a no-op.</summary>
    public Task<bool> StartAsync()
    {
        // One run at a time: a second click (or the dialog's own retry) joins the run already going instead of stacking
        // a second one on the same inputs.
        if (runTask is { } current)
        {
            return current;
        }

        if (createdDirectory is not null)
        {
            return Task.FromResult(true);
        }

        runTask = RunOnceAsync();
        return runTask;
    }

    /// <summary>Stops a running creation; the folder it half-wrote is removed by the service.</summary>
    public void Cancel() => runSource?.Cancel();

    public AsyncRelayCommand StartCommand { get; }

    public RelayCommand CancelCommand { get; }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        Loc.Instance.LanguageChanged -= OnLanguageChanged;
        Interlocked.Increment(ref occupancyGeneration);
        var pendingCheck = Interlocked.Exchange(ref occupancySource, null);
        pendingCheck?.Cancel();
        pendingCheck?.Dispose();

        // The source is cancelled but not disposed: the creation still running on the thread pool holds its token, and
        // disposing a source whose token is in flight makes Register throw inside unrelated code.
        runSource?.Cancel();
        runSource = null;
    }

    // ---------------------------------------------------------------- the run

    /// <summary>One attempt: the service's whole body runs off the UI thread; only progress comes back through it.</summary>
    private async Task<bool> RunOnceAsync()
    {
        var request = new ProjectCreationRequest(
            mode,
            parentDirectory.Trim(),
            name.Trim(),
            isClone ? url.Trim() : null);

        var source = new CancellationTokenSource();
        runSource = source;
        errorText = null;
        shownProgress = null;
        RaiseRunState(beginning: true);
        var created = false;
        try
        {
            var result = await Task.Run(() => service.RunAsync(request, OnCloneProgress, source.Token), source.Token)
                .ConfigureAwait(true);

            if (result.Directory is { } directory)
            {
                createdDirectory = directory;
                created = true;
                OnPropertyChanged(nameof(CreatedDirectory));
                OnPropertyChanged(nameof(CanStart));
                StartCommand.NotifyCanExecuteChanged();
                return true;
            }

            errorText = result.Error;
            return false;
        }
        catch (OperationCanceledException)
        {
            // "Stopped" is an answer of its own: the dialog stays open with the input it had.
            errorText = ProjectCreationStrings.Cancelled;
            return false;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Nothing in the creation chain is allowed to escape into an async-void command handler.
            errorText = LocText.Literal(exception.Message);
            return false;
        }
        finally
        {
            // Whatever the outcome, the last progress frame is applied before the run state comes down, so the bar's
            // final position is the one git actually reached.
            var latest = Volatile.Read(ref latestProgress);
            if (latest is not null && !Equals(latest, shownProgress))
            {
                ApplyProgress(latest);
            }

            // The run is over, so nothing holds the token any more and the source can go; a Dispose that already let go
            // of it (the dialog closing mid-run) has cancelled it, which is all it needed.
            runSource = null;
            source.Dispose();
            runTask = null;
            RaiseRunState(beginning: false);

            // A failed or cancelled creation removes the folder it half-wrote, so the occupancy answer taken before the run
            // is stale from here on. It is dropped at once — on this thread, before the run reports its end — because the
            // very next thing the dialog does is ask whether the input is usable again; the check scheduled below then
            // confirms it off the UI thread.
            if (createdDirectory is null)
            {
                ForgetOccupancy();
            }

            ScheduleOccupancyCheck();

            // The completion notification goes out last, after the run state is down: its listener closes the dialog, and a
            // dialog that still believes a run is in progress refuses to close.
            if (created)
            {
                RaiseCompleted();
            }
        }
    }

    /// <summary>Raises everything a running state change touches: the flag, the button labels and both commands.</summary>
    private void RaiseRunState(bool beginning)
    {
        isRunning = beginning;
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(CancelText));
        OnPropertyChanged(nameof(Error));
        OnPropertyChanged(nameof(HasError));
        OnPropertyChanged(nameof(ProgressText));
        OnPropertyChanged(nameof(ProgressPercent));
        OnPropertyChanged(nameof(IsProgressIndeterminate));
        StartCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
    }

    // ---------------------------------------------------------------- clone progress

    /// <summary>
    /// Receives one update on the thread that reads git's stderr: the newest frame is parked and at most one frame per
    /// <see cref="ProgressInterval"/> is posted to the UI thread, so a fast clone cannot flood the dispatcher. A frame
    /// dropped here is not lost — the run's completion applies whatever arrived last.
    /// </summary>
    private void OnCloneProgress(GitCloneProgress progress)
    {
        Volatile.Write(ref latestProgress, progress);
        if (progressClock.Elapsed < ProgressInterval)
        {
            return;
        }

        progressClock.Restart();
        Dispatcher.UIThread.Post(() => ApplyProgress(progress), DispatcherPriority.Background);
    }

    /// <summary>Shows one frame on the UI thread; a frame that lands after the run ended describes no running clone.</summary>
    private void ApplyProgress(GitCloneProgress progress)
    {
        if (!isRunning)
        {
            return;
        }

        shownProgress = progress;
        OnPropertyChanged(nameof(ProgressText));
        OnPropertyChanged(nameof(ProgressPercent));
        OnPropertyChanged(nameof(IsProgressIndeterminate));
    }

    // ---------------------------------------------------------------- input state

    /// <summary>Recomputes everything derived from the input: the name rules, the hint, the destination, whether Start may run.</summary>
    private void RefreshInputState()
    {
        shapeProblem = ProjectCreationService.ValidateNameShape(name);
        OnPropertyChanged(nameof(Hint));
        OnPropertyChanged(nameof(HasHint));
        OnPropertyChanged(nameof(DestinationText));
        OnPropertyChanged(nameof(CanStart));
        StartCommand.NotifyCanExecuteChanged();
        ScheduleOccupancyCheck();
    }

    /// <summary>Why Start is unavailable, or null when it is: name first, then the remote, then the parent directory.</summary>
    private string? DescribeProblem()
    {
        if (ProjectCreationService.DescribeParentProblem(parentDirectory) is { } parentProblem)
        {
            return parentProblem.Resolve();
        }

        if (shapeProblem != ProjectNameProblem.None)
        {
            return ProjectCreationStrings.NameProblem(shapeProblem).Resolve();
        }

        if (isClone && ProjectCreationService.DescribeUrlProblem(url) is { } urlProblem)
        {
            return urlProblem.Resolve();
        }

        return targetOccupied ? ProjectCreationStrings.NameProblem(ProjectNameProblem.Exists).Resolve() : null;
    }

    /// <summary>
    /// Drops the answer the last check gave and makes every answer still in flight stale, so a read that started before the
    /// disk changed cannot write its verdict back afterwards (a failed run and an in-flight check race otherwise: the flag
    /// is set from the thread pool, so clearing it alone would leave the race open).
    /// </summary>
    private void ForgetOccupancy()
    {
        Interlocked.Increment(ref occupancyGeneration);
        SetOccupancy(occupied: false);
    }

    /// <summary>
    /// Asks a newer check to read the target folder off the UI thread; a name that already breaks a rule needs no disk read,
    /// so that case simply clears the previous answer.
    /// </summary>
    private void ScheduleOccupancyCheck()
    {
        var generation = Interlocked.Increment(ref occupancyGeneration);
        var source = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref occupancySource, source);
        if (previous is not null)
        {
            previous.Cancel();
            previous.Dispose();
        }

        _ = CheckOccupancyAsync(generation, source.Token);
    }

    private async Task CheckOccupancyAsync(int generation, CancellationToken ct)
    {
        var parent = parentDirectory.Trim();
        var candidate = name.Trim();
        if (shapeProblem != ProjectNameProblem.None || candidate.Length == 0 || !Path.IsPathFullyQualified(parent))
        {
            ApplyOccupancy(generation, occupied: false);
            return;
        }

        try
        {
            await Task.Delay(OccupancyDelay, ct).ConfigureAwait(true);
            var occupied = await Task.Run(
                    () => ProjectCreationService.ValidateName(parent, candidate) == ProjectNameProblem.Exists,
                    ct)
                .ConfigureAwait(true);
            ApplyOccupancy(generation, occupied);
        }
        catch (OperationCanceledException)
        {
            // A newer keystroke, a closed dialog or the flow going away: this answer describes an input nobody has anymore.
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // A folder that cannot be listed is not a blocker on its own: the run itself re-checks and reports.
            AppLogs.Shell.ZLogWarning(exception, $"could not read the project folder");
            ApplyOccupancy(generation, occupied: false);
        }
    }

    /// <summary>Applies one occupancy answer unless a newer input already asked for another one.</summary>
    private void ApplyOccupancy(int generation, bool occupied)
    {
        if (generation != Volatile.Read(ref occupancyGeneration) || disposed)
        {
            return;
        }

        SetOccupancy(occupied);
    }

    /// <summary>Stores one occupancy answer and tells every reader of it; the hint and the button follow from the flag.</summary>
    private void SetOccupancy(bool occupied)
    {
        if (targetOccupied == occupied)
        {
            return;
        }

        targetOccupied = occupied;
        OnPropertyChanged(nameof(Hint));
        OnPropertyChanged(nameof(HasHint));
        OnPropertyChanged(nameof(CanStart));
        StartCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Derives the folder name from the URL without marking it as the user's edit.</summary>
    private void DeriveName()
    {
        derivingName = true;
        try
        {
            Name = ProjectCreationService.SuggestNameFromUrl(url);
        }
        finally
        {
            derivingName = false;
        }
    }

    /// <summary>Recomputes every cached text after a language switch; resolved paths are language-independent.</summary>
    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        RefreshInputState();
        OnPropertyChanged(nameof(TitleText));
        OnPropertyChanged(nameof(IntroText));
        OnPropertyChanged(nameof(StartText));
        OnPropertyChanged(nameof(CancelText));
        OnPropertyChanged(nameof(Error));
        OnPropertyChanged(nameof(HasError));
        OnPropertyChanged(nameof(ProgressText));
    }

    private void RaiseCompleted() => Completed?.Invoke(this, EventArgs.Empty);
}
