using System.Collections.ObjectModel;
using Forge.Abstractions;
using Forge.App.Localization;
using Forge.App.ViewModels;

namespace Forge.App.Automations.Settings;

/// <summary>One repository project's "CI auto-push" switch under the CI built-in row.</summary>
internal sealed class AutomationCiProjectViewModel : ObservableObject
{
    private readonly AutomationsSettingsViewModel owner;
    private bool autoPush;

    public AutomationCiProjectViewModel(AutomationsSettingsViewModel owner, AutomationProjectChoice project, bool autoPush)
    {
        this.owner = owner;
        Id = project.Id;
        Name = project.Name;
        this.autoPush = autoPush;
    }

    public string Id { get; }

    public string Name { get; }

    public bool AutoPush
    {
        get => autoPush;
        set
        {
            if (Set(ref autoPush, value))
            {
                owner.Track(owner.SetCiAutoPushAsync(this, value));
            }
        }
    }

    internal void SetFromConfig(bool value) => Set(ref autoPush, value, nameof(AutoPush));
}

/// <summary>One automation of the list: built-ins first, then the user's.</summary>
internal sealed class AutomationRowViewModel : ObservableObject
{
    private readonly AutomationsSettingsViewModel owner;
    private string name = string.Empty;
    private string triggerSummary = string.Empty;
    private string nextText = string.Empty;
    private string lastStatusText = string.Empty;
    private bool isEnabled;
    private bool isCiExpanded;

    public AutomationRowViewModel(AutomationsSettingsViewModel owner, string id, bool isBuiltIn)
    {
        this.owner = owner;
        Id = id;
        IsBuiltIn = isBuiltIn;
        RunNowCommand = new AsyncRelayCommand(_ => owner.RunNowAsync(this), () => CanRunNow);
        EditCommand = new AsyncRelayCommand(_ => owner.EditAsync(this), () => CanEdit);
        DeleteCommand = new AsyncRelayCommand(_ => owner.DeleteAsync(this), () => CanDelete);
        ToggleCiCommand = new RelayCommand(() => IsCiExpanded = !IsCiExpanded);
    }

    public string Id { get; }

    public bool IsBuiltIn { get; }

    public bool CanRunNow => !AutomationText.IsEventOnly(Id);

    public bool CanEdit => !IsBuiltIn;

    public bool CanDelete => !IsBuiltIn;

    public bool IsCi => Id == BuiltInAutomations.CiChain;

    public AsyncRelayCommand RunNowCommand { get; }

    public AsyncRelayCommand EditCommand { get; }

    public AsyncRelayCommand DeleteCommand { get; }

    public RelayCommand ToggleCiCommand { get; }

    public ObservableCollection<AutomationCiProjectViewModel> CiProjects { get; } = [];

    public string BadgeText => Loc.Instance[AutomationSettingsStrings.BuiltInBadgeKey];

    public string RunNowText => Loc.Instance[AutomationSettingsStrings.RunNowKey];

    public string EditText => Loc.Instance[AutomationSettingsStrings.EditKey];

    public string DeleteText => Loc.Instance[AutomationSettingsStrings.DeleteKey];

    public string EnabledText => Loc.Instance[AutomationSettingsStrings.EnabledKey];

    public string CiTitle => Loc.Instance[AutomationSettingsStrings.CiTitleKey];

    public string CiHint => Loc.Instance[AutomationSettingsStrings.CiHintKey];

    public string CiNoProjectsText => Loc.Instance[AutomationSettingsStrings.CiNoProjectsKey];

    public string CiToggleText => Loc.Instance[IsCiExpanded ? AutomationSettingsStrings.CiCollapseKey : AutomationSettingsStrings.CiExpandKey];

    public bool HasNoCiProjects => CiProjects.Count == 0;

    public string Name
    {
        get => name;
        private set => Set(ref name, value);
    }

    public string TriggerSummary
    {
        get => triggerSummary;
        private set => Set(ref triggerSummary, value);
    }

    public string NextText
    {
        get => nextText;
        private set => Set(ref nextText, value);
    }

    public string LastStatusText
    {
        get => lastStatusText;
        private set => Set(ref lastStatusText, value);
    }

    public bool IsEnabled
    {
        get => isEnabled;
        set
        {
            if (Set(ref isEnabled, value))
            {
                owner.Track(owner.SetEnabledAsync(this, value));
            }
        }
    }

    public bool IsCiExpanded
    {
        get => isCiExpanded;
        set
        {
            if (Set(ref isCiExpanded, value))
            {
                OnPropertyChanged(nameof(CiToggleText));
            }
        }
    }

    internal void Apply(AutomationDefinition definition, string next, string lastStatus)
    {
        Name = AutomationText.DisplayName(definition);
        TriggerSummary = AutomationText.Summarize(definition);
        NextText = next;
        LastStatusText = lastStatus;
        Set(ref isEnabled, definition.Enabled, nameof(IsEnabled));
    }

    internal void ApplyCiProjects(IReadOnlyList<AutomationProjectChoice> projects, IReadOnlyList<string> autoPushOff)
    {
        var sameShape = CiProjects.Count == projects.Count && CiProjects.Select(static p => p.Id).SequenceEqual(projects.Select(static p => p.Id));
        if (sameShape)
        {
            foreach (var row in CiProjects)
            {
                row.SetFromConfig(!autoPushOff.Contains(row.Id));
            }

            return;
        }

        CiProjects.Clear();
        foreach (var project in projects)
        {
            CiProjects.Add(new AutomationCiProjectViewModel(owner, project, !autoPushOff.Contains(project.Id)));
        }

        OnPropertyChanged(nameof(HasNoCiProjects));
    }
}

/// <summary>One run record of the selected automation.</summary>
internal sealed class AutomationRunRowViewModel : ObservableObject
{
    public AutomationRunRowViewModel(AutomationRun run, Action<AutomationRunRowViewModel> open)
    {
        Run = run;
        OpenSessionId = !string.IsNullOrEmpty(run.SessionId) ? run.SessionId : run.TargetSessionId;
        StatusText = AutomationText.Status(run.Status);
        SourceText = AutomationText.Source(run.Source);
        var started = AutomationText.Moment(run.StartedAt);
        TimeText = run.EndedAt is { } ended
            ? Loc.Instance.Format(AutomationSettingsStrings.RunsRangeKey, started, AutomationText.Moment(ended))
            : Loc.Instance.Format(AutomationSettingsStrings.RunsStartedKey, started);
        UsageText = run.Usage is { } usage
            ? Loc.Instance.Format(
                AutomationSettingsStrings.RunsUsageKey,
                TextFormat.Number((long)usage.InputTokens),
                TextFormat.Number((long)usage.OutputTokens))
            : string.Empty;
        MessageText = !string.IsNullOrEmpty(run.Message) ? run.Message : run.Detail ?? string.Empty;
        OpenCommand = new RelayCommand(() => open(this), () => CanOpen);
    }

    public AutomationRun Run { get; }

    /// <summary>The session to open: the one the run created, else the one it attached to.</summary>
    public string? OpenSessionId { get; }

    public bool CanOpen => OpenSessionId is { Length: > 0 };

    public string StatusText { get; }

    public string SourceText { get; }

    public string TimeText { get; }

    public string UsageText { get; }

    public string MessageText { get; }

    public bool HasUsage => UsageText.Length > 0;

    public bool HasMessage => MessageText.Length > 0;

    public RelayCommand OpenCommand { get; }
}
