using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia.Input;
using Forge.App.Input;
using Forge.App.Localization;
using Forge.App.Preferences;
using Forge.App.ViewModels;

namespace Forge.App.Settings;

/// <summary>
/// One row of the <c>keymap</c> section: a rebindable action, its current gesture (default or override), and the affordances
/// that start a rebind and reset the override. The row carries no gesture state of its own — the section owns the one capture
/// session at a time — so a row is refreshed by <see cref="Refresh"/> after any change that affects it.
/// </summary>
internal sealed class KeymapRowViewModel : ObservableObject
{
    private readonly KeymapSettingsSectionViewModel owner;

    public KeymapRowViewModel(KeymapSettingsSectionViewModel owner, KeymapAction action)
    {
        this.owner = owner;
        Action = action;
        ResetCommand = new RelayCommand(() => owner.Reset(this), () => IsOverridden);
    }

    public KeymapAction Action { get; }

    public string Id => Action.Id;

    public KeymapScope Scope => Action.Scope;

    /// <summary>The localized display name, resolved at call time so a language switch re-reads it.</summary>
    public string DisplayName => KeymapStrings.ActionName(Action.DisplayNameKey);

    /// <summary>The gesture text the action actually uses right now; follows a rebind immediately.</summary>
    public string CurrentGestureText => KeymapApplier.DisplayGesture(Action.Id);

    /// <summary>The catalog default, shown next to an override so a reset's destination is visible.</summary>
    public string DefaultGestureText => KeymapGesture.Format(Action.DefaultGesture);

    /// <summary>The “default {0}” label shown next to an override; formatted at call time for the current language.</summary>
    public string DefaultGestureLabel => KeymapStrings.DefaultGesture(DefaultGestureText);

    /// <summary>True when a stored override differs from the catalog; resetting removes it.</summary>
    public bool IsOverridden => owner.IsOverridden(Action.Id);

    /// <summary>True while this row is the one waiting for a new key combination.</summary>
    public bool IsCapturing => ReferenceEquals(owner.CaptureRow, this);

    /// <summary>Removes the override; disabled when the action already uses its default.</summary>
    public RelayCommand ResetCommand { get; }

    /// <summary>Re-reads every language- or preference-derived value; cheap and safe to call per row after any change.</summary>
    internal void Refresh()
    {
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(CurrentGestureText));
        OnPropertyChanged(nameof(DefaultGestureText));
        OnPropertyChanged(nameof(DefaultGestureLabel));
        OnPropertyChanged(nameof(IsOverridden));
        OnPropertyChanged(nameof(IsCapturing));
        ResetCommand.NotifyCanExecuteChanged();
    }
}

/// <summary>
/// The <c>keymap</c> settings section (<see cref="SettingsSectionIds.Keymap"/>): search, per-scope grouping, capture-and-confirm
/// rebinding, per-row reset and reset-all. It edits <see cref="AppPreferences.KeymapOverrides"/> directly and keeps
/// <see cref="Input.KeymapApplier"/> subscribed through the composition module, so a confirmed rebind reaches the window,
/// the editor menu and the view key handlers immediately. The rows are built once from the static catalog; the search only
/// re-fills the three bound scope lists, so a keystroke allocates nothing.
/// </summary>
internal sealed class KeymapSettingsSectionViewModel : ObservableObject, IDisposable, IReloadableSettingsSection
{
    private readonly AppPreferences preferences;
    private readonly List<KeymapRowViewModel> allRows = [];
    private readonly List<KeymapRowViewModel> windowRows = [];
    private readonly List<KeymapRowViewModel> editorRows = [];
    private readonly List<KeymapRowViewModel> viewRows = [];

    private string searchText = string.Empty;
    private KeymapRowViewModel? captureRow;
    private KeyGesture? captureGesture;
    private string captureGestureText = string.Empty;
    private string captureConflictText = string.Empty;
    private bool disposed;

    public KeymapSettingsSectionViewModel(AppPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        this.preferences = preferences;

        foreach (var action in KeymapCatalog.Actions)
        {
            allRows.Add(new KeymapRowViewModel(this, action));
        }

        ResetAllCommand = new RelayCommand(ResetAll, () => preferences.KeymapOverrides.Count > 0);
        ConfirmCaptureCommand = new RelayCommand(ConfirmCapture, () => captureRow is not null && captureGesture is not null);
        CancelCaptureCommand = new RelayCommand(CancelCapture, () => captureRow is not null);

        preferences.PropertyChanged += OnPreferencesChanged;
        Loc.Instance.LanguageChanged += OnLanguageChanged;
        ApplyFilter();
    }

    /// <summary>The rows the search currently matches, split by scope so the view renders three headed groups.</summary>
    public ObservableCollection<KeymapRowViewModel> WindowRows { get; } = [];

    public ObservableCollection<KeymapRowViewModel> EditorRows { get; } = [];

    public ObservableCollection<KeymapRowViewModel> ViewRows { get; } = [];

    /// <summary>
    /// The section's own texts. Each one reads the resolved static (<see cref="KeymapStrings"/>) rather than the key
    /// constant: a property that returned <c>keymap.hint</c> would draw the key itself into the page.
    /// </summary>
    public string Hint => KeymapStrings.Hint;

    public string SearchPlaceholder => KeymapStrings.SearchPlaceholder;

    public string WindowScopeHeading => KeymapStrings.ScopeWindow;

    public string EditorScopeHeading => KeymapStrings.ScopeEditor;

    public string ViewScopeHeading => KeymapStrings.ScopeView;

    public string ResetAllText => KeymapStrings.ResetAll;

    /// <summary>The search box: filters this page only, matching the action name and its current gesture text.</summary>
    public string SearchText
    {
        get => searchText;
        set
        {
            if (Set(ref searchText, value ?? string.Empty))
            {
                ApplyFilter();
            }
        }
    }

    /// <summary>Removes every override, so all actions fall back to the catalog defaults.</summary>
    public RelayCommand ResetAllCommand { get; }

    /// <summary>Applies the captured gesture to the capturing row, even when it conflicts (the hint said so).</summary>
    public RelayCommand ConfirmCaptureCommand { get; }

    /// <summary>Discards the capture without writing anything.</summary>
    public RelayCommand CancelCaptureCommand { get; }

    /// <summary>The row waiting for a new key combination, or null when no rebind is in progress.</summary>
    public KeymapRowViewModel? CaptureRow
    {
        get => captureRow;
        private set
        {
            if (Set(ref captureRow, value))
            {
                OnPropertyChanged(nameof(HasCapture));
                ConfirmCaptureCommand.NotifyCanExecuteChanged();
                CancelCaptureCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public bool HasCapture => captureRow is not null;

    /// <summary>The captured gesture, human-friendly; empty until a complete key combination is pressed.</summary>
    public string CaptureGestureText
    {
        get => captureGestureText;
        private set
        {
            if (Set(ref captureGestureText, value))
            {
                OnPropertyChanged(nameof(HasCapturedGesture));
            }
        }
    }

    public bool HasCapturedGesture => captureGestureText.Length > 0;

    /// <summary>The conflict notice naming the action that already uses the captured gesture; empty when it is free.</summary>
    public string CaptureConflictText
    {
        get => captureConflictText;
        private set
        {
            if (Set(ref captureConflictText, value))
            {
                OnPropertyChanged(nameof(HasConflict));
            }
        }
    }

    public bool HasConflict => captureConflictText.Length > 0;

    public string CapturePrompt => KeymapStrings.CapturePrompt;

    public string ConfirmText => KeymapStrings.CaptureConfirm;

    public string CancelText => KeymapStrings.CaptureCancel;

    /// <summary>Re-syncs every row from the live preferences; the settings page calls it when the section is selected.</summary>
    public void Reload()
    {
        RefreshAllRows();
        ApplyFilter();
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        preferences.PropertyChanged -= OnPreferencesChanged;
        Loc.Instance.LanguageChanged -= OnLanguageChanged;
    }

    /// <summary>True when <paramref name="actionId"/> has a stored override; a read of the normalized preference list, no state.</summary>
    internal bool IsOverridden(string actionId)
    {
        foreach (var entry in preferences.KeymapOverrides)
        {
            if (entry.StartsWith(actionId + AppPreferences.KeymapOverrideSeparator, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Starts (or restarts) the capture for <paramref name="row"/>.</summary>
    internal void BeginRebind(KeymapRowViewModel row)
    {
        var previous = captureRow;
        CaptureRow = row;
        captureGesture = null;
        CaptureGestureText = string.Empty;
        CaptureConflictText = string.Empty;
        ConfirmCaptureCommand.NotifyCanExecuteChanged();
        previous?.Refresh();
        if (!ReferenceEquals(previous, row))
        {
            row.Refresh();
        }
    }

    /// <summary>
    /// Takes a key press from the capture surface. A lone modifier is not a gesture yet, so it is ignored and the user keeps
    /// pressing; anything else is normalized and checked against the row's scope.
    /// </summary>
    internal void CaptureKey(Key key, KeyModifiers modifiers)
    {
        if (captureRow is null || IsModifierOnly(key))
        {
            return;
        }

        captureGesture = new KeyGesture(key, modifiers);
        CaptureGestureText = KeymapGesture.Format(captureGesture);
        var conflictId = KeymapApplier.FindConflict(captureRow.Action.Id, captureGesture);
        CaptureConflictText = conflictId is null
            ? string.Empty
            : KeymapStrings.Conflict(KeymapStrings.ActionName(KeymapCatalog.Find(conflictId)!.DisplayNameKey));
        ConfirmCaptureCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Removes one action's override; the action falls back to its catalog default.</summary>
    internal void Reset(KeymapRowViewModel row)
    {
        var entries = new List<string>(preferences.KeymapOverrides);
        var removed = 0;
        for (var index = entries.Count - 1; index >= 0; index--)
        {
            if (entries[index].StartsWith(row.Action.Id + AppPreferences.KeymapOverrideSeparator, StringComparison.Ordinal))
            {
                entries.RemoveAt(index);
                removed++;
            }
        }

        if (removed > 0)
        {
            preferences.KeymapOverrides = entries;
        }

        if (ReferenceEquals(captureRow, row))
        {
            CancelCapture();
        }
    }

    private void ResetAll()
    {
        preferences.KeymapOverrides = [];
        CancelCapture();
    }

    private void ConfirmCapture()
    {
        if (captureRow is null || captureGesture is null)
        {
            return;
        }

        var canonical = KeymapGesture.Normalize(captureGesture);
        var entries = new List<string>(preferences.KeymapOverrides);
        for (var index = entries.Count - 1; index >= 0; index--)
        {
            if (entries[index].StartsWith(captureRow.Action.Id + AppPreferences.KeymapOverrideSeparator, StringComparison.Ordinal))
            {
                entries.RemoveAt(index);
            }
        }

        entries.Add(captureRow.Action.Id + AppPreferences.KeymapOverrideSeparator + canonical);
        preferences.KeymapOverrides = entries;
        CancelCapture();
    }

    private void CancelCapture()
    {
        var previous = captureRow;
        CaptureRow = null;
        captureGesture = null;
        CaptureGestureText = string.Empty;
        CaptureConflictText = string.Empty;
        ConfirmCaptureCommand.NotifyCanExecuteChanged();
        previous?.Refresh();
    }

    private static bool IsModifierOnly(Key key) => key is
        Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt or
        Key.LWin or Key.RWin or Key.None;

    /// <summary>Re-fills the three bound scope lists from the built rows; only touched when the match set moved.</summary>
    private void ApplyFilter()
    {
        var query = searchText.Trim();
        windowRows.Clear();
        editorRows.Clear();
        viewRows.Clear();
        foreach (var row in allRows)
        {
            if (query.Length > 0 &&
                !row.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase) &&
                !row.CurrentGestureText.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            switch (row.Scope)
            {
                case KeymapScope.Window:
                    windowRows.Add(row);
                    break;
                case KeymapScope.Editor:
                    editorRows.Add(row);
                    break;
                default:
                    viewRows.Add(row);
                    break;
            }
        }

        Replace(WindowRows, windowRows);
        Replace(EditorRows, editorRows);
        Replace(ViewRows, viewRows);
    }

    private static void Replace(ObservableCollection<KeymapRowViewModel> target, List<KeymapRowViewModel> source)
    {
        if (target.Count == source.Count && target.Zip(source, static (a, b) => ReferenceEquals(a, b)).All(static same => same))
        {
            return;
        }

        target.Clear();
        foreach (var row in source)
        {
            target.Add(row);
        }
    }

    private void RefreshAllRows()
    {
        foreach (var row in allRows)
        {
            row.Refresh();
        }

        ResetAllCommand.NotifyCanExecuteChanged();
    }

    private void OnPreferencesChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (disposed || !string.Equals(e.PropertyName, nameof(AppPreferences.KeymapOverrides), StringComparison.Ordinal))
        {
            return;
        }

        RefreshAllRows();
        ApplyFilter();
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(Hint));
        OnPropertyChanged(nameof(SearchPlaceholder));
        OnPropertyChanged(nameof(WindowScopeHeading));
        OnPropertyChanged(nameof(EditorScopeHeading));
        OnPropertyChanged(nameof(ViewScopeHeading));
        OnPropertyChanged(nameof(ResetAllText));
        OnPropertyChanged(nameof(CapturePrompt));
        OnPropertyChanged(nameof(ConfirmText));
        OnPropertyChanged(nameof(CancelText));
        if (captureConflictText.Length > 0)
        {
            OnPropertyChanged(nameof(CaptureConflictText));
        }

        foreach (var row in allRows)
        {
            row.Refresh();
        }
    }
}
