using System.Globalization;
using Forge.App.Agents;
using Forge.App.Localization;
using Forge.App.Preferences;
using Forge.App.ViewModels;

namespace Forge.App.Settings;

/// <summary>
/// The compaction block of the provider page, under the default context window: on/off plus the trigger and target
/// percentages. The toggle saves at once; the two numbers are validated as they are typed (inline errors) and saved on
/// commit (focus loss or Enter) only when both are valid, so a half-typed value never reaches <c>settings.json</c>.
/// </summary>
internal sealed class CompactionSettingsViewModel : NotifyingObject, IDisposable
{
    private readonly CompactionSettingsStore store;
    private readonly AppPreferences? preferences;
    private readonly int uiThread = Environment.CurrentManagedThreadId;
    private bool enabled;
    private string triggerText = string.Empty;
    private string targetText = string.Empty;
    private string keepRecentText = string.Empty;
    private string emergencyText = string.Empty;
    private string? triggerError;
    private string? targetError;
    private string? keepRecentError;
    private string? emergencyError;
    private string? saveError;
    private bool loading;

    public CompactionSettingsViewModel(CompactionSettingsStore store, AppPreferences? preferences = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        this.store = store;
        this.preferences = preferences;
        Load();
        Loc.Instance.LanguageChanged += OnLanguageChanged;
    }

    public string Title => CompactionStrings.SettingsTitle;

    public string EnabledLabel => CompactionStrings.SettingsEnabled;

    public string TriggerLabel => CompactionStrings.SettingsTrigger;

    public string TriggerHint => CompactionStrings.SettingsTriggerHint;

    public string TargetHint => CompactionStrings.SettingsTargetHint;

    public string TargetLabel => CompactionStrings.SettingsTarget;

    public string Hint => CompactionStrings.SettingsHint;

    /// <summary>The context-budget line's switch, which lives in <c>preferences</c> rather than the compaction section.</summary>
    public string BudgetLabel => CompactionStrings.SettingsBudget;

    public string BudgetHint => CompactionStrings.SettingsBudgetHint;

    public string KeepRecentLabel => CompactionStrings.SettingsKeepRecent;

    public string KeepRecentHint => CompactionStrings.SettingsKeepRecentHint;

    public string EmergencyLabel => CompactionStrings.SettingsEmergency;

    public string EmergencyHint => CompactionStrings.SettingsEmergencyHint;

    /// <summary>The line a running turn compacts at, in percent of the usable input; empty is never valid.</summary>
    public string EmergencyText
    {
        get => emergencyText;
        set
        {
            if (Set(ref emergencyText, value ?? string.Empty))
            {
                Validate();
            }
        }
    }

    public string? EmergencyError
    {
        get => emergencyError;
        private set => SetAndNotify(ref emergencyError, value, nameof(HasEmergencyError));
    }

    public bool HasEmergencyError => emergencyError is not null;

    /// <summary>The raw tail kept verbatim, in tokens; empty means "derive it per model" (min(20 000, 0.2·U)).</summary>
    public string KeepRecentText
    {
        get => keepRecentText;
        set
        {
            if (Set(ref keepRecentText, value ?? string.Empty))
            {
                Validate();
            }
        }
    }

    public string? KeepRecentError
    {
        get => keepRecentError;
        private set => SetAndNotify(ref keepRecentError, value, nameof(HasKeepRecentError));
    }

    public bool HasKeepRecentError => keepRecentError is not null;

    /// <summary>
    /// Whether every request tells the model its own context usage: on by default, saved the moment it is toggled. Reads
    /// the live preference so a change made elsewhere shows up on the next reload.
    /// </summary>
    public bool ShowBudgetToModel
    {
        get => preferences?.ChatContextBudget ?? AppPreferences.DefaultChatContextBudget;
        set
        {
            if (preferences is not { } live || live.ChatContextBudget == value)
            {
                return;
            }

            live.ChatContextBudget = value;
            OnPropertyChanged(nameof(ShowBudgetToModel));
        }
    }

    public bool Enabled
    {
        get => enabled;
        set
        {
            if (Set(ref enabled, value) && !loading)
            {
                Save(store.Current with { Enabled = value });
            }
        }
    }

    public string TriggerText
    {
        get => triggerText;
        set
        {
            if (Set(ref triggerText, value ?? string.Empty))
            {
                Validate();
            }
        }
    }

    public string TargetText
    {
        get => targetText;
        set
        {
            if (Set(ref targetText, value ?? string.Empty))
            {
                Validate();
            }
        }
    }

    public string? TriggerError
    {
        get => triggerError;
        private set => SetAndNotify(ref triggerError, value, nameof(HasTriggerError));
    }

    public bool HasTriggerError => triggerError is not null;

    public string? TargetError
    {
        get => targetError;
        private set => SetAndNotify(ref targetError, value, nameof(HasTargetError));
    }

    public bool HasTargetError => targetError is not null;

    public string? SaveError
    {
        get => saveError;
        private set => SetAndNotify(ref saveError, value, nameof(HasSaveError));
    }

    public bool HasSaveError => saveError is not null;

    /// <summary>Saves the percentages and the raw tail when all are valid; false when a value is invalid or the write failed.</summary>
    public bool Commit()
    {
        if (!Validate(out var trigger, out var target, out var keepRecent, out var emergency))
        {
            return false;
        }

        var current = store.Current;
        if (current.TriggerPercent == trigger && current.TargetPercent == target && current.KeepRecentTokens == keepRecent && current.EmergencyPercent == emergency)
        {
            return true;
        }

        return Save(current with { TriggerPercent = trigger, TargetPercent = target, KeepRecentTokens = keepRecent, EmergencyPercent = emergency });
    }

    /// <summary>Re-reads the settings file (the page's refresh) and shows what it holds.</summary>
    public void Reload()
    {
        store.Reload();
        Load();
    }

    /// <summary>Shows the store's current values.</summary>
    public void Load()
    {
        var current = store.Current;
        loading = true;
        try
        {
            Enabled = current.Enabled;
            TriggerText = current.TriggerPercent.ToString(CultureInfo.InvariantCulture);
            TargetText = current.TargetPercent.ToString(CultureInfo.InvariantCulture);
            KeepRecentText = current.KeepRecentTokens?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
            EmergencyText = current.EmergencyPercent.ToString(CultureInfo.InvariantCulture);
            OnPropertyChanged(nameof(ShowBudgetToModel));
        }
        finally
        {
            loading = false;
        }

        Validate();
    }

    public void Dispose() => Loc.Instance.LanguageChanged -= OnLanguageChanged;

    private bool Save(CompactionSettings value)
    {
        try
        {
            store.Save(value);
            SaveError = null;
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            SaveError = CompactionStrings.SettingsSaveFailed(exception.Message);
            return false;
        }
    }

    private void Validate() => Validate(out _, out _, out _, out _);

    private bool Validate(out int trigger, out int target, out int? keepRecent, out int emergency)
    {
        var triggerOk = int.TryParse(triggerText.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out trigger)
                        && CompactionSettings.IsValidTrigger(trigger);
        TriggerError = triggerOk ? null : CompactionStrings.SettingsTriggerInvalid;
        var targetParsed = int.TryParse(targetText.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out target);
        // The target is checked against the typed trigger when that is valid, else against the saved one.
        var against = triggerOk ? trigger : store.Current.TriggerPercent;
        var targetOk = targetParsed && CompactionSettings.IsValidTarget(target, against);
        TargetError = targetOk ? null : CompactionStrings.SettingsTargetInvalid;
        // Empty means "derive it per model"; anything else has to be a number in range.
        var keepRecentText = this.keepRecentText.Trim();
        keepRecent = null;
        var keepRecentOk = keepRecentText.Length == 0
                           || (int.TryParse(keepRecentText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                               && CompactionSettings.IsValidKeepRecent(parsed));
        keepRecent = keepRecentOk && keepRecentText.Length > 0 ? int.Parse(keepRecentText, CultureInfo.InvariantCulture) : null;
        KeepRecentError = keepRecentOk ? null : CompactionStrings.SettingsKeepRecentInvalid;
        // The emergency line has to sit above the typed trigger (or the saved one when the trigger is invalid).
        var emergencyOk = int.TryParse(emergencyText.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out emergency)
                          && CompactionSettings.IsValidEmergency(emergency, against);
        EmergencyError = emergencyOk ? null : CompactionStrings.SettingsEmergencyInvalid;
        return triggerOk && targetOk && keepRecentOk && emergencyOk;
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        // A switch raised on another thread belongs to a window that is gone: the labels and the inline
        // errors are bound on the thread that built the block.
        if (Environment.CurrentManagedThreadId != uiThread)
        {
            return;
        }

        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(EnabledLabel));
        OnPropertyChanged(nameof(TriggerLabel));
        OnPropertyChanged(nameof(TriggerHint));
        OnPropertyChanged(nameof(TargetLabel));
        OnPropertyChanged(nameof(TargetHint));
        OnPropertyChanged(nameof(Hint));
        OnPropertyChanged(nameof(BudgetLabel));
        OnPropertyChanged(nameof(BudgetHint));
        OnPropertyChanged(nameof(KeepRecentLabel));
        OnPropertyChanged(nameof(KeepRecentHint));
        OnPropertyChanged(nameof(EmergencyLabel));
        OnPropertyChanged(nameof(EmergencyHint));
        Validate();
        if (TriggerError is not null)
        {
            OnPropertyChanged(nameof(TriggerError));
        }

        if (TargetError is not null)
        {
            OnPropertyChanged(nameof(TargetError));
        }
    }
}
