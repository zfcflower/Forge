using System.Collections.ObjectModel;
using System.Globalization;
using Forge.Abstractions;
using Forge.App.Localization;
using Forge.App.Preferences;
using Forge.App.ViewModels;

namespace Forge.App.Automations.Settings;

/// <summary>
/// The <c>automations</c> settings section: the global pause switch, the list (built-ins first), the run history of the
/// selected row and the per-project CI auto-push switches. It owns no window: the editor dialog and the delete confirmation
/// are callbacks, so the model is testable without an Avalonia application. The engine raises <c>Changed</c> off the UI
/// thread; every refresh goes through <c>post</c>.
/// </summary>
internal sealed class AutomationsSettingsViewModel : NotifyingObject, IDisposable, IReloadableSettingsSection
{
    private const int RunHistoryLimit = 100;
    private const int LastStatusScan = 500;

    private readonly IAutomationEngine engine;
    private readonly IAutomationRunStore runs;
    private readonly Func<AutomationTrigger, int, AutomationPreview> preview;
    private readonly Func<CancellationToken, Task<IReadOnlyList<AutomationProjectChoice>>> projects;
    private readonly Func<CancellationToken, Task<IReadOnlyList<AutomationSessionChoice>>> sessions;
    private readonly Action<string> openSession;
    private readonly Func<AutomationEditorViewModel, Task<bool>> showEditor;
    private readonly Action<Action> post;
    private readonly Func<string, Task<bool>> confirm;
    private readonly Dictionary<string, AutomationRun> lastRuns = new(StringComparer.Ordinal);
    private IReadOnlyList<AutomationProjectChoice> repositoryProjects = [];
    private AutomationRowViewModel? selectedRow;
    private bool isRunning = true;
    private string? problem;
    private int refreshVersion;
    private int runsVersion;
    private bool disposed;

    public AutomationsSettingsViewModel(
        IAutomationEngine engine,
        IAutomationRunStore runs,
        Func<AutomationTrigger, int, AutomationPreview> preview,
        Func<CancellationToken, Task<IReadOnlyList<AutomationProjectChoice>>> projects,
        Func<CancellationToken, Task<IReadOnlyList<AutomationSessionChoice>>> sessions,
        Action<string> openSession,
        Func<AutomationEditorViewModel, Task<bool>> showEditor,
        Action<Action> post,
        Func<string, Task<bool>> confirm)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(runs);
        ArgumentNullException.ThrowIfNull(preview);
        ArgumentNullException.ThrowIfNull(projects);
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(openSession);
        ArgumentNullException.ThrowIfNull(showEditor);
        ArgumentNullException.ThrowIfNull(post);
        ArgumentNullException.ThrowIfNull(confirm);
        this.engine = engine;
        this.runs = runs;
        this.preview = preview;
        this.projects = projects;
        this.sessions = sessions;
        this.openSession = openSession;
        this.showEditor = showEditor;
        this.post = post;
        this.confirm = confirm;
        NewCommand = new AsyncRelayCommand(_ => EditAsync(null));
        engine.Changed += OnEngineChanged;
        Loc.Instance.LanguageChanged += OnLanguageChanged;
    }

    public AsyncRelayCommand NewCommand { get; }

    /// <summary>Built-ins first, then the user's automations.</summary>
    public ObservableCollection<AutomationRowViewModel> Rows { get; } = [];

    public ObservableCollection<string> ConfigErrors { get; } = [];

    public ObservableCollection<AutomationRunRowViewModel> Runs { get; } = [];

    public bool HasConfigErrors => ConfigErrors.Count > 0;

    public bool HasRuns => Runs.Count > 0;

    public bool ShowsNoRuns => SelectedRow is not null && Runs.Count == 0;

    public string Title => Loc.Instance[AutomationSettingsStrings.SectionTitleKey];

    public string GlobalHint => Loc.Instance[AutomationSettingsStrings.GlobalHintKey];

    public string GlobalStatusText => Loc.Instance[IsRunning ? AutomationSettingsStrings.RunningKey : AutomationSettingsStrings.PausedKey];

    public string NewText => Loc.Instance[AutomationSettingsStrings.NewKey];

    public string NextLabel => Loc.Instance[AutomationSettingsStrings.NextKey];

    public string LastLabel => Loc.Instance[AutomationSettingsStrings.LastKey];

    public string RunsTitle => Loc.Instance[AutomationSettingsStrings.RunsTitleKey];

    public string RunsNoneText => Loc.Instance[AutomationSettingsStrings.RunsNoneKey];

    public string RunsSelectHint => Loc.Instance[AutomationSettingsStrings.RunsSelectHintKey];

    public string OpenSessionText => Loc.Instance[AutomationSettingsStrings.RunsOpenKey];

    /// <summary>The global switch: true = schedules fire, false = paused (<see cref="AutomationConfig.Paused"/>).</summary>
    public bool IsRunning
    {
        get => isRunning;
        set
        {
            if (Set(ref isRunning, value))
            {
                OnPropertyChanged(nameof(GlobalStatusText));
                Track(SetPausedAsync(!value));
            }
        }
    }

    public AutomationRowViewModel? SelectedRow
    {
        get => selectedRow;
        set
        {
            if (Set(ref selectedRow, value))
            {
                Track(LoadRunsAsync());
            }
        }
    }

    /// <summary>The last action failure (a refused start, a failed write), or <c>null</c>.</summary>
    public string? Problem
    {
        get => problem;
        private set => SetAndNotify(ref problem, value, nameof(HasProblem));
    }

    public bool HasProblem => Problem is { Length: > 0 };

    /// <summary>Loads the project list and the rows; the section is shown after this completes.</summary>
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var all = await projects(cancellationToken).ConfigureAwait(true);
            repositoryProjects = [.. all.Where(static p => p.IsRepository)];
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            repositoryProjects = [];
            Problem = Loc.Instance.Format(AutomationSettingsStrings.ActionFailedKey, exception.Message);
        }

        await RefreshAsync(cancellationToken).ConfigureAwait(true);
    }

    /// <summary>Called when the section is selected in the settings window.</summary>
    public void Reload() => Track(LoadAsync());

    /// <summary>Rereads the engine state and the run store, and updates the rows in place.</summary>
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        var version = ++refreshVersion;
        try
        {
            var recent = await runs.QueryAsync(new AutomationRunQuery { Limit = LastStatusScan }, cancellationToken).ConfigureAwait(true);
            if (version != refreshVersion || disposed)
            {
                return;
            }

            lastRuns.Clear();
            foreach (var run in recent)
            {
                lastRuns.TryAdd(run.AutomationId, run); // newest first
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Problem = Loc.Instance.Format(AutomationSettingsStrings.ActionFailedKey, exception.Message);
        }

        ApplyEngineState();
        await LoadRunsAsync(cancellationToken).ConfigureAwait(true);
    }

    private void ApplyEngineState()
    {
        var config = engine.Config;
        Set(ref isRunning, !config.Paused, nameof(IsRunning));
        OnPropertyChanged(nameof(GlobalStatusText));

        var definitions = engine.Definitions
            .Select((definition, index) => (definition, index))
            .OrderBy(static pair => pair.definition.IsBuiltIn ? 0 : 1)
            .ThenBy(static pair => pair.index)
            .Select(static pair => pair.definition)
            .ToList();

        var sameShape = Rows.Count == definitions.Count && Rows.Select(static r => r.Id).SequenceEqual(definitions.Select(static d => d.Id));
        if (!sameShape)
        {
            var existing = Rows.ToDictionary(static r => r.Id, StringComparer.Ordinal);
            var previouslySelected = SelectedRow?.Id;
            Rows.Clear();
            foreach (var definition in definitions)
            {
                Rows.Add(existing.TryGetValue(definition.Id, out var row) ? row : new AutomationRowViewModel(this, definition.Id, definition.IsBuiltIn));
            }

            if (previouslySelected is not null && Rows.All(r => r.Id != previouslySelected))
            {
                // The selected automation was deleted.
                selectedRow = null;
                OnPropertyChanged(nameof(SelectedRow));
            }
            else if (previouslySelected is not null)
            {
                selectedRow = Rows.First(r => r.Id == previouslySelected);
                OnPropertyChanged(nameof(SelectedRow));
            }
        }

        for (var i = 0; i < definitions.Count; i++)
        {
            var definition = definitions[i];
            var row = Rows[i];
            row.Apply(definition, NextText(definition), LastStatusText(definition.Id));
            if (row.IsCi)
            {
                row.ApplyCiProjects(repositoryProjects, config.CiAutoPushOffProjects);
            }
        }

        ConfigErrors.Clear();
        foreach (var error in engine.ConfigErrors)
        {
            var message = error.MessageKey is { } key
                ? Loc.Instance.Format(key, [.. error.MessageArgs])
                : error.Message;
            ConfigErrors.Add(error.Index < 0
                ? Loc.Instance.Format(AutomationSettingsStrings.ConfigFileErrorKey, message)
                : Loc.Instance.Format(
                    AutomationSettingsStrings.ConfigErrorKey,
                    (error.Index + 1).ToString(CultureInfo.InvariantCulture),
                    error.Id ?? "?",
                    message));
        }

        OnPropertyChanged(nameof(HasConfigErrors));
    }

    private string NextText(AutomationDefinition definition) =>
        engine.NextFireTime(definition.Id) is { } next
            ? AutomationText.Moment(next)
            : Loc.Instance[AutomationSettingsStrings.NoneKey];

    private string LastStatusText(string automationId) =>
        lastRuns.TryGetValue(automationId, out var run)
            ? AutomationText.Status(run.Status) + " " + AutomationText.Moment(run.StartedAt)
            : Loc.Instance[AutomationSettingsStrings.NeverRanKey];

    /// <summary>Loads the run history of the selected row.</summary>
    public async Task LoadRunsAsync(CancellationToken cancellationToken = default)
    {
        var version = ++runsVersion;
        var row = SelectedRow;
        if (row is null)
        {
            Runs.Clear();
            NotifyRuns();
            return;
        }

        IReadOnlyList<AutomationRun> list;
        try
        {
            list = await runs.QueryAsync(new AutomationRunQuery { AutomationId = row.Id, Limit = RunHistoryLimit }, cancellationToken)
                .ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Problem = Loc.Instance.Format(AutomationSettingsStrings.ActionFailedKey, exception.Message);
            return;
        }

        if (version != runsVersion || disposed)
        {
            return;
        }

        Runs.Clear();
        foreach (var run in list)
        {
            Runs.Add(new AutomationRunRowViewModel(run, OpenRun));
        }

        NotifyRuns();
    }

    private void NotifyRuns()
    {
        OnPropertyChanged(nameof(HasRuns));
        OnPropertyChanged(nameof(ShowsNoRuns));
    }

    /// <summary>Selects a row and loads its runs.</summary>
    public Task SelectRowAsync(AutomationRowViewModel? row)
    {
        if (!ReferenceEquals(selectedRow, row))
        {
            selectedRow = row;
            OnPropertyChanged(nameof(SelectedRow));
        }

        return LoadRunsAsync();
    }

    /// <summary>Opens the session a run created or attached to; runs without one do nothing.</summary>
    public void OpenRun(AutomationRunRowViewModel run)
    {
        if (run.OpenSessionId is { Length: > 0 } id)
        {
            openSession(id);
        }
    }

    public Task SetPausedAsync(bool paused) =>
        engine.UpdateConfigAsync(config => config with { Paused = paused }, CancellationToken.None);

    public Task SetEnabledAsync(AutomationRowViewModel row, bool enabled)
    {
        var id = row.Id;
        return engine.UpdateConfigAsync(
            config =>
            {
                if (row.IsBuiltIn)
                {
                    var disabled = config.DisabledBuiltIns.Where(existing => existing != id).ToList();
                    if (!enabled)
                    {
                        disabled.Add(id);
                    }

                    return config with { DisabledBuiltIns = disabled };
                }

                return config with
                {
                    Definitions = [.. config.Definitions.Select(d => d.Id == id ? d with { Enabled = enabled } : d)],
                };
            },
            CancellationToken.None);
    }

    public Task SetCiAutoPushAsync(AutomationCiProjectViewModel project, bool autoPush)
    {
        var id = project.Id;
        return engine.UpdateConfigAsync(
            config =>
            {
                var off = config.CiAutoPushOffProjects.Where(existing => existing != id).ToList();
                if (!autoPush)
                {
                    off.Add(id);
                }

                return config with { CiAutoPushOffProjects = off };
            },
            CancellationToken.None);
    }

    public async Task RunNowAsync(AutomationRowViewModel row)
    {
        try
        {
            var result = await engine.StartAsync(new AutomationRunRequest(row.Id, AutomationTriggerSource.Manual), CancellationToken.None)
                .ConfigureAwait(true);
            Problem = result.Status switch
            {
                AutomationStartStatus.Started => null,
                AutomationStartStatus.Busy => Loc.Instance[AutomationSettingsStrings.StartBusyKey],
                _ => Loc.Instance.Format(AutomationSettingsStrings.StartRefusedKey, result.Status.ToString()),
            };
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Problem = Loc.Instance.Format(AutomationSettingsStrings.ActionFailedKey, exception.Message);
        }
    }

    /// <summary>Opens the editor dialog for a user automation (<c>null</c> row = a new one).</summary>
    public async Task EditAsync(AutomationRowViewModel? row)
    {
        if (row is { IsBuiltIn: true })
        {
            return;
        }

        try
        {
            var original = row is null ? null : engine.Config.Definitions.FirstOrDefault(d => d.Id == row.Id);
            if (row is not null && original is null)
            {
                return;
            }

            var projectChoices = await projects(CancellationToken.None).ConfigureAwait(true);
            var sessionChoices = await sessions(CancellationToken.None).ConfigureAwait(true);
            var editor = new AutomationEditorViewModel(engine, original, preview, projectChoices, sessionChoices);
            await showEditor(editor).ConfigureAwait(true);
            await RefreshAsync().ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Problem = Loc.Instance.Format(AutomationSettingsStrings.ActionFailedKey, exception.Message);
        }
    }

    /// <summary>Deletes a user automation after the confirmation; built-ins are never deleted.</summary>
    public async Task DeleteAsync(AutomationRowViewModel row)
    {
        if (row.IsBuiltIn || !await confirm(row.Name).ConfigureAwait(true))
        {
            return;
        }

        try
        {
            var id = row.Id;
            await engine.UpdateConfigAsync(
                config => config with { Definitions = [.. config.Definitions.Where(d => d.Id != id)] },
                CancellationToken.None).ConfigureAwait(true);
            await RefreshAsync().ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Problem = Loc.Instance.Format(AutomationSettingsStrings.ActionFailedKey, exception.Message);
        }
    }

    /// <summary>Runs a fire-and-forget operation of a property setter; a failure becomes <see cref="Problem"/>.</summary>
    internal void Track(Task task) => _ = ObserveAsync(task);

    private async Task ObserveAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            Problem = Loc.Instance.Format(AutomationSettingsStrings.ActionFailedKey, exception.Message);
        }
    }

    private void OnEngineChanged(object? sender, EventArgs e) => post(() =>
    {
        if (!disposed)
        {
            Track(RefreshAsync());
        }
    });

    private void OnLanguageChanged(object? sender, EventArgs e) => post(() =>
    {
        if (!disposed)
        {
            OnPropertyChanged(null);
            foreach (var row in Rows)
            {
                row.Relocalize();
            }

            Track(RefreshAsync());
        }
    });

    public void Dispose()
    {
        disposed = true;
        engine.Changed -= OnEngineChanged;
        Loc.Instance.LanguageChanged -= OnLanguageChanged;
    }
}
