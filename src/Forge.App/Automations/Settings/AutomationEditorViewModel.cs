using System.Collections.ObjectModel;
using Forge.Abstractions;
using Forge.App.Localization;
using Forge.App.ViewModels;

namespace Forge.App.Automations.Settings;

/// <summary>
/// The new/edit dialog of a user automation. Validation errors block <see cref="SaveAsync"/>; the next-fire preview is
/// recomputed whenever a trigger field changes. Saving writes through <see cref="IAutomationEngine.UpdateConfigAsync"/>.
/// </summary>
internal sealed class AutomationEditorViewModel : NotifyingObject
{
    private const int PreviewCount = 5;

    private readonly IAutomationEngine engine;
    private readonly AutomationDefinition? original;
    private readonly Func<AutomationTrigger, int, AutomationPreview> preview;
    private string name;
    private bool enabled;
    private int actionIndex;
    private string prompt;
    private int placementIndex;
    private bool readOnlyTools;
    private AutomationProjectChoice? selectedProject;
    private AutomationSessionChoice? selectedSession;
    private string modelText;
    private string providerText;
    private bool catchUp;
    private string? error;
    private string? previewError;
    private bool saving;

    public AutomationEditorViewModel(
        IAutomationEngine engine,
        AutomationDefinition? original,
        Func<AutomationTrigger, int, AutomationPreview> preview,
        IReadOnlyList<AutomationProjectChoice> projects,
        IReadOnlyList<AutomationSessionChoice> sessions)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(preview);
        this.engine = engine;
        this.original = original;
        this.preview = preview;

        Projects = [.. projects.Where(static p => p.IsRepository)];
        var sessionList = sessions.ToList();
        if (original?.TargetSessionId is { Length: > 0 } target && sessionList.All(s => s.Id != target))
        {
            // The attached session is no longer an active root session; keep it selectable so an edit does not drop it.
            sessionList.Insert(0, new AutomationSessionChoice(target, target, null));
        }

        Sessions = sessionList;

        name = original?.Name ?? string.Empty;
        enabled = original?.Enabled ?? true;
        actionIndex = original?.Action == AutomationAction.Remind ? 1 : 0;
        prompt = original?.Prompt ?? string.Empty;
        placementIndex = original?.Placement switch
        {
            AutomationPlacement.FixedFork => 1,
            AutomationPlacement.Scout => 2,
            _ => 0,
        };
        readOnlyTools = original?.ReadOnlyTools ?? false;
        selectedProject = Projects.FirstOrDefault(p => p.Id == original?.ProjectId);
        selectedSession = Sessions.FirstOrDefault(s => s.Id == original?.TargetSessionId);
        modelText = original?.Model?.Model ?? string.Empty;
        providerText = original?.Model?.ProviderId ?? string.Empty;
        catchUp = original?.CatchUp ?? false;

        foreach (var trigger in original?.Triggers ?? [])
        {
            Triggers.Add(NewTriggerRow(trigger));
        }

        AddTriggerCommand = new RelayCommand(AddTrigger);
        SaveCommand = new AsyncRelayCommand(_ => SaveAsync(), () => !saving);
        CancelCommand = new RelayCommand(() => Cancelled?.Invoke(this, EventArgs.Empty));
        RefreshPreview();
    }

    /// <summary>Raised once the definition was written; the window closes with <c>true</c>.</summary>
    public event EventHandler<AutomationDefinition>? Saved;

    /// <summary>Raised when the user cancels; the window closes with <c>false</c>.</summary>
    public event EventHandler? Cancelled;

    public bool IsNew => original is null;

    public string Title => Loc.Instance[IsNew ? AutomationSettingsStrings.EditorNewTitleKey : AutomationSettingsStrings.EditorEditTitleKey];

    public RelayCommand AddTriggerCommand { get; }

    public AsyncRelayCommand SaveCommand { get; }

    public RelayCommand CancelCommand { get; }

    public ObservableCollection<AutomationTriggerEditorViewModel> Triggers { get; } = [];

    /// <summary>The next fire times across all valid triggers, formatted for display.</summary>
    public ObservableCollection<string> PreviewLines { get; } = [];

    public IReadOnlyList<AutomationProjectChoice> Projects { get; }

    public IReadOnlyList<AutomationSessionChoice> Sessions { get; }

    public IReadOnlyList<string> ActionOptions { get; } =
    [
        Loc.Instance[AutomationSettingsStrings.EditorActionPromptKey],
        Loc.Instance[AutomationSettingsStrings.EditorActionRemindKey],
    ];

    public IReadOnlyList<string> PlacementOptions { get; } =
    [
        Loc.Instance[AutomationSettingsStrings.EditorPlacementScratchKey],
        Loc.Instance[AutomationSettingsStrings.EditorPlacementForkKey],
        Loc.Instance[AutomationSettingsStrings.EditorPlacementScoutKey],
    ];

    public string Name
    {
        get => name;
        set => Set(ref name, value ?? string.Empty);
    }

    public bool Enabled
    {
        get => enabled;
        set => Set(ref enabled, value);
    }

    /// <summary>0 = run a prompt, 1 = remind only.</summary>
    public int ActionIndex
    {
        get => actionIndex;
        set
        {
            if (Set(ref actionIndex, Math.Clamp(value, 0, 1)))
            {
                OnPropertyChanged(nameof(ShowsPlacement));
                OnPropertyChanged(nameof(PromptLabel));
                NotifyPlacementBlocks();
            }
        }
    }

    public string Prompt
    {
        get => prompt;
        set => Set(ref prompt, value ?? string.Empty);
    }

    /// <summary>0 = scratch, 1 = fixed fork, 2 = scout.</summary>
    public int PlacementIndex
    {
        get => placementIndex;
        set
        {
            if (Set(ref placementIndex, Math.Clamp(value, 0, 2)))
            {
                NotifyPlacementBlocks();
            }
        }
    }

    /// <summary>Raises the bindings that pick which placement block (scratch, project, session) the editor shows.</summary>
    private void NotifyPlacementBlocks()
    {
        OnPropertyChanged(nameof(ShowsScratchOptions));
        OnPropertyChanged(nameof(ShowsProject));
        OnPropertyChanged(nameof(ShowsSession));
    }

    public bool ReadOnlyTools
    {
        get => readOnlyTools;
        set => Set(ref readOnlyTools, value);
    }

    public AutomationProjectChoice? SelectedProject
    {
        get => selectedProject;
        set => Set(ref selectedProject, value);
    }

    public AutomationSessionChoice? SelectedSession
    {
        get => selectedSession;
        set => Set(ref selectedSession, value);
    }

    public string ModelText
    {
        get => modelText;
        set => Set(ref modelText, value ?? string.Empty);
    }

    public string ProviderText
    {
        get => providerText;
        set => Set(ref providerText, value ?? string.Empty);
    }

    public bool CatchUp
    {
        get => catchUp;
        set => Set(ref catchUp, value);
    }

    public bool ShowsPlacement => actionIndex == 0;

    public bool ShowsScratchOptions => ShowsPlacement && placementIndex == 0;

    public bool ShowsProject => ShowsPlacement && placementIndex == 1;

    public bool ShowsSession => ShowsPlacement && placementIndex == 2;

    public string PromptLabel => Loc.Instance[actionIndex == 1 ? AutomationSettingsStrings.EditorRemindTextKey : AutomationSettingsStrings.EditorPromptKey];

    public bool HasNoTriggers => Triggers.Count == 0;

    /// <summary>Why the preview is empty (an invalid trigger, usually a cron expression), or <c>null</c>.</summary>
    public string? PreviewError
    {
        get => previewError;
        private set => SetAndNotify(ref previewError, value, nameof(HasPreviewError));
    }

    public bool HasPreviewError => PreviewError is { Length: > 0 };

    public bool HasPreviewLines => PreviewLines.Count > 0;

    public string? Error
    {
        get => error;
        private set => SetAndNotify(ref error, value, nameof(HasError));
    }

    public bool HasError => Error is { Length: > 0 };

    public string NameLabel => Loc.Instance[AutomationSettingsStrings.EditorNameKey];

    public string EnabledText => Loc.Instance[AutomationSettingsStrings.EditorEnabledKey];

    public string ActionLabel => Loc.Instance[AutomationSettingsStrings.EditorActionKey];

    public string TriggersLabel => Loc.Instance[AutomationSettingsStrings.EditorTriggersKey];

    public string AddTriggerText => Loc.Instance[AutomationSettingsStrings.EditorAddTriggerKey];

    public string NoTriggersText => Loc.Instance[AutomationSettingsStrings.EditorNoTriggersKey];

    public string IntervalLabel => Loc.Instance[AutomationSettingsStrings.EditorIntervalKey];

    public string MinuteLabel => Loc.Instance[AutomationSettingsStrings.EditorMinuteKey];

    public string TimesLabel => Loc.Instance[AutomationSettingsStrings.EditorTimesKey];

    public string TimesHint => Loc.Instance[AutomationSettingsStrings.EditorTimesHintKey];

    public string DaysLabel => Loc.Instance[AutomationSettingsStrings.EditorDaysKey];

    public string CronLabel => Loc.Instance[AutomationSettingsStrings.EditorCronKey];

    public string CronHint => Loc.Instance[AutomationSettingsStrings.EditorCronHintKey];

    public string PreviewLabel => Loc.Instance[AutomationSettingsStrings.EditorPreviewKey];

    public string PreviewNoneText => Loc.Instance[AutomationSettingsStrings.EditorPreviewNoneKey];

    public string PlacementLabel => Loc.Instance[AutomationSettingsStrings.EditorPlacementKey];

    public string ReadOnlyToolsText => Loc.Instance[AutomationSettingsStrings.EditorReadOnlyToolsKey];

    public string ProjectLabel => Loc.Instance[AutomationSettingsStrings.EditorProjectKey];

    public string SessionLabel => Loc.Instance[AutomationSettingsStrings.EditorSessionKey];

    public string ModelLabel => Loc.Instance[AutomationSettingsStrings.EditorModelKey];

    public string ProviderLabel => Loc.Instance[AutomationSettingsStrings.EditorProviderKey];

    public string CatchUpText => Loc.Instance[AutomationSettingsStrings.EditorCatchUpKey];

    public string SaveText => Loc.Instance[AutomationSettingsStrings.EditorSaveKey];

    public string CancelText => Loc.Instance[AutomationSettingsStrings.EditorCancelKey];

    /// <summary>Adds an empty daily trigger row.</summary>
    public void AddTrigger()
    {
        Triggers.Add(NewTriggerRow(null));
        OnTriggersChanged();
    }

    private AutomationTriggerEditorViewModel NewTriggerRow(AutomationTrigger? source) =>
        new(source, RefreshPreview, row =>
        {
            Triggers.Remove(row);
            OnTriggersChanged();
        });

    private void OnTriggersChanged()
    {
        OnPropertyChanged(nameof(HasNoTriggers));
        RefreshPreview();
    }

    /// <summary>Recomputes the next five fire times: all valid triggers merged, sorted, distinct.</summary>
    public void RefreshPreview()
    {
        var moments = new List<DateTimeOffset>();
        string? problem = null;
        foreach (var row in Triggers)
        {
            var (trigger, _) = row.TryBuild();
            if (trigger is null)
            {
                continue;
            }

            var result = preview(trigger, PreviewCount);
            if (result.Error is { Length: > 0 })
            {
                problem ??= result.Error;
                continue;
            }

            moments.AddRange(result.Next);
        }

        PreviewLines.Clear();
        foreach (var moment in moments.Distinct().Order().Take(PreviewCount))
        {
            PreviewLines.Add(AutomationText.Moment(moment));
        }

        PreviewError = problem;
        OnPropertyChanged(nameof(HasPreviewLines));
    }

    /// <summary>Validates the form and builds the definition; <c>null</c> when something is wrong (<see cref="Error"/> says what).</summary>
    public AutomationDefinition? TryBuild()
    {
        var loc = Loc.Instance;
        var trimmedName = Name.Trim();
        if (trimmedName.Length == 0)
        {
            Error = loc[AutomationSettingsStrings.ErrNeedNameKey];
            return null;
        }

        var triggers = new List<AutomationTrigger>();
        for (var i = 0; i < Triggers.Count; i++)
        {
            var (trigger, triggerError) = Triggers[i].TryBuild();
            if (trigger is null)
            {
                Error = loc.Format(AutomationSettingsStrings.ErrTriggerKey, (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), triggerError);
                return null;
            }

            var checkedPreview = preview(trigger, 1);
            if (checkedPreview.Error is { Length: > 0 })
            {
                Error = loc.Format(AutomationSettingsStrings.ErrTriggerKey, (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), checkedPreview.Error);
                return null;
            }

            triggers.Add(trigger);
        }

        var remind = ActionIndex == 1;
        if (Prompt.Trim().Length == 0)
        {
            Error = loc[AutomationSettingsStrings.ErrNeedPromptKey];
            return null;
        }

        var placement = remind ? AutomationPlacement.Scratch : PlacementIndex switch
        {
            1 => AutomationPlacement.FixedFork,
            2 => AutomationPlacement.Scout,
            _ => AutomationPlacement.Scratch,
        };
        if (placement == AutomationPlacement.FixedFork && SelectedProject is null)
        {
            Error = loc[AutomationSettingsStrings.ErrNeedProjectKey];
            return null;
        }

        if (placement == AutomationPlacement.Scout && SelectedSession is null)
        {
            Error = loc[AutomationSettingsStrings.ErrNeedSessionKey];
            return null;
        }

        var model = ModelText.Trim();
        var provider = ProviderText.Trim();
        ModelSettings? modelSettings = model.Length == 0
            ? null
            : (original?.Model ?? new ModelSettings(model)) with
            {
                Model = model,
                ProviderId = provider.Length == 0 ? null : provider,
            };

        Error = null;
        return (original ?? new AutomationDefinition("user." + Guid.NewGuid().ToString("N"), trimmedName)) with
        {
            Name = trimmedName,
            Enabled = Enabled,
            Triggers = triggers,
            Action = remind ? AutomationAction.Remind : AutomationAction.Prompt,
            Prompt = Prompt.Trim(),
            Placement = placement,
            ProjectId = placement == AutomationPlacement.FixedFork ? SelectedProject!.Id : null,
            TargetSessionId = placement == AutomationPlacement.Scout ? SelectedSession!.Id : null,
            ReadOnlyTools = placement == AutomationPlacement.Scratch && !remind && ReadOnlyTools,
            Model = remind ? null : modelSettings,
            CatchUp = CatchUp,
        };
    }

    /// <summary>Validates and writes the definition into the config; false (with <see cref="Error"/>) when it did not save.</summary>
    public async Task<bool> SaveAsync(CancellationToken cancellationToken = default)
    {
        var definition = TryBuild();
        if (definition is null)
        {
            return false;
        }

        saving = true;
        SaveCommand.NotifyCanExecuteChanged();
        try
        {
            await engine.UpdateConfigAsync(config => config with { Definitions = Upsert(config.Definitions, definition) }, cancellationToken)
                .ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            Error = Loc.Instance.Format(AutomationSettingsStrings.EditorSaveFailedKey, exception.Message);
            return false;
        }
        finally
        {
            saving = false;
            SaveCommand.NotifyCanExecuteChanged();
        }

        Saved?.Invoke(this, definition);
        return true;
    }

    private static IReadOnlyList<AutomationDefinition> Upsert(IReadOnlyList<AutomationDefinition> current, AutomationDefinition definition)
    {
        var list = current.ToList();
        var index = list.FindIndex(d => d.Id == definition.Id);
        if (index >= 0)
        {
            list[index] = definition;
        }
        else
        {
            list.Add(definition);
        }

        return list;
    }
}
