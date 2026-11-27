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
    private string keepRecentTurnsText = string.Empty;
    private string emergencyText = string.Empty;
    private string? triggerError;
    private string? keepRecentTurnsError;
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

    public string Hint => CompactionStrings.SettingsHint;

    public string KeepRecentTurnsLabel => CompactionStrings.SettingsKeepRecentTurns;

    public string KeepRecentTurnsHint => CompactionStrings.SettingsKeepRecentTurnsHint;

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

    /// <summary>How many of the newest finished turns a fold leaves alone; turns, not tokens, because a turn folds whole.</summary>
    public string KeepRecentTurnsText
    {
        get => keepRecentTurnsText;
        set
        {
            if (Set(ref keepRecentTurnsText, value ?? string.Empty))
            {
                Validate();
            }
        }
    }

    public string? KeepRecentTurnsError
    {
        get => keepRecentTurnsError;
        private set => SetAndNotify(ref keepRecentTurnsError, value, nameof(HasKeepRecentTurnsError));
    }

    public bool HasKeepRecentTurnsError => keepRecentTurnsError is not null;

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

    public string? TriggerError
    {
        get => triggerError;
        private set => SetAndNotify(ref triggerError, value, nameof(HasTriggerError));
    }

    public bool HasTriggerError => triggerError is not null;

    public string? SaveError
    {
        get => saveError;
        private set => SetAndNotify(ref saveError, value, nameof(HasSaveError));
    }

    public bool HasSaveError => saveError is not null;

    /// <summary>Saves the percentages and the kept-turn count when all are valid; false when a value is invalid or the write failed.</summary>
    public bool Commit()
    {
        if (!Validate(out var trigger, out var keepRecentTurns, out var emergency))
        {
            return false;
        }

        var current = store.Current;
        if (current.TriggerPercent == trigger && current.KeepRecentTurns == keepRecentTurns
            && current.EmergencyPercent == emergency)
        {
            return true;
        }

        return Save(current with
        {
            TriggerPercent = trigger,
            KeepRecentTurns = keepRecentTurns,
            EmergencyPercent = emergency,
        });
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
            KeepRecentTurnsText = current.KeepRecentTurns.ToString(CultureInfo.InvariantCulture);
            EmergencyText = current.EmergencyPercent.ToString(CultureInfo.InvariantCulture);
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

    private void Validate() => Validate(out _, out _, out _);

    private bool Validate(out int trigger, out int keepRecentTurns, out int emergency)
    {
        var triggerOk = int.TryParse(triggerText.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out trigger)
                        && CompactionSettings.IsValidTrigger(trigger);
        TriggerError = triggerOk ? null : CompactionStrings.SettingsTriggerInvalid;
        // The emergency line is checked against the typed trigger when that is valid, else against the saved one.
        var against = triggerOk ? trigger : store.Current.TriggerPercent;
        var keepRecentTurnsOk = int.TryParse(keepRecentTurnsText.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out keepRecentTurns)
                           && CompactionSettings.IsValidKeepRecentTurns(keepRecentTurns);
        KeepRecentTurnsError = keepRecentTurnsOk ? null : CompactionStrings.SettingsKeepRecentTurnsInvalid;
        // The emergency line has to sit above the typed trigger (or the saved one when the trigger is invalid).
        var emergencyOk = int.TryParse(emergencyText.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out emergency)
                          && CompactionSettings.IsValidEmergency(emergency, against);
        EmergencyError = emergencyOk ? null : CompactionStrings.SettingsEmergencyInvalid;
        return triggerOk && keepRecentTurnsOk && emergencyOk;
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
        OnPropertyChanged(nameof(Hint));
        OnPropertyChanged(nameof(KeepRecentTurnsLabel));
        OnPropertyChanged(nameof(KeepRecentTurnsHint));
        OnPropertyChanged(nameof(EmergencyLabel));
        OnPropertyChanged(nameof(EmergencyHint));
        Validate();
        if (TriggerError is not null)
        {
            OnPropertyChanged(nameof(TriggerError));
        }

        if (KeepRecentTurnsError is not null)
        {
            OnPropertyChanged(nameof(KeepRecentTurnsError));
        }

        if (EmergencyError is not null)
        {
            OnPropertyChanged(nameof(EmergencyError));
        }
    }
}
