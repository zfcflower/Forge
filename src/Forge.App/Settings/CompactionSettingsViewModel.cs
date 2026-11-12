using System.Globalization;
using Forge.App.Agents;
using Forge.App.Localization;
using Forge.App.ViewModels;

namespace Forge.App.Settings;

/// <summary>
/// The compaction block of the provider page, under the default context window: on/off plus the trigger and target
/// percentages. The toggle saves at once; the two numbers are validated as they are typed (inline errors) and saved on
/// commit (focus loss or Enter) only when both are valid, so a half-typed value never reaches <c>settings.json</c>.
/// </summary>
internal sealed class CompactionSettingsViewModel : ObservableObject, IDisposable
{
    private readonly CompactionSettingsStore store;
    private bool enabled;
    private string triggerText = string.Empty;
    private string targetText = string.Empty;
    private string? triggerError;
    private string? targetError;
    private string? saveError;
    private bool loading;

    public CompactionSettingsViewModel(CompactionSettingsStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        this.store = store;
        Load();
        Loc.Instance.LanguageChanged += OnLanguageChanged;
    }

    public string Title => CompactionStrings.SettingsTitle;

    public string EnabledLabel => CompactionStrings.SettingsEnabled;

    public string TriggerLabel => CompactionStrings.SettingsTrigger;

    public string TargetLabel => CompactionStrings.SettingsTarget;

    public string Hint => CompactionStrings.SettingsHint;

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
        private set
        {
            if (Set(ref triggerError, value))
            {
                OnPropertyChanged(nameof(HasTriggerError));
            }
        }
    }

    public bool HasTriggerError => triggerError is not null;

    public string? TargetError
    {
        get => targetError;
        private set
        {
            if (Set(ref targetError, value))
            {
                OnPropertyChanged(nameof(HasTargetError));
            }
        }
    }

    public bool HasTargetError => targetError is not null;

    public string? SaveError
    {
        get => saveError;
        private set
        {
            if (Set(ref saveError, value))
            {
                OnPropertyChanged(nameof(HasSaveError));
            }
        }
    }

    public bool HasSaveError => saveError is not null;

    /// <summary>Saves the percentages when both are valid; returns false when a value is invalid or the write failed.</summary>
    public bool Commit()
    {
        if (!Validate(out var trigger, out var target))
        {
            return false;
        }

        var current = store.Current;
        if (current.TriggerPercent == trigger && current.TargetPercent == target)
        {
            return true;
        }

        return Save(current with { TriggerPercent = trigger, TargetPercent = target });
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

    private void Validate() => Validate(out _, out _);

    private bool Validate(out int trigger, out int target)
    {
        var triggerOk = int.TryParse(triggerText.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out trigger)
                        && CompactionSettings.IsValidTrigger(trigger);
        TriggerError = triggerOk ? null : CompactionStrings.SettingsTriggerInvalid;
        var targetParsed = int.TryParse(targetText.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out target);
        // The target is checked against the typed trigger when that is valid, else against the saved one.
        var against = triggerOk ? trigger : store.Current.TriggerPercent;
        var targetOk = targetParsed && CompactionSettings.IsValidTarget(target, against);
        TargetError = targetOk ? null : CompactionStrings.SettingsTargetInvalid;
        return triggerOk && targetOk;
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(EnabledLabel));
        OnPropertyChanged(nameof(TriggerLabel));
        OnPropertyChanged(nameof(TargetLabel));
        OnPropertyChanged(nameof(Hint));
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
