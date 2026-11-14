using System.ComponentModel;
using System.Diagnostics;
using System.Windows.Input;
using Avalonia;
using Avalonia.Media;
using Avalonia.Threading;
using Forge.App.Localization;
using Forge.App.Preferences;
using Forge.App.ViewModels;
using Forge.App.Logging;
using ZLogger;

namespace Forge.App.Appearance;

/// <summary>
/// The <c>appearance</c> settings section: one <see cref="FontGroupViewModel"/> for the UI font and one for the code font.
/// UI thread only. Edits go through the <see cref="AppPreferences"/> setters (validation and persistence belong to the
/// preferences foundation); external changes to the preferences are echoed back into the editors.
/// <para>The installed font list is enumerated once, in the background, and published to both groups on the UI thread
/// (<see cref="PublishFamilies"/>); <see cref="FamiliesLoaded"/> completes right after that, for tests.</para>
/// </summary>
internal sealed class AppearanceSettingsViewModel : ObservableObject, IDisposable
{
    private readonly AppPreferences preferences;
    private readonly TaskCompletionSource familiesLoaded = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// The dispatcher of the running application's UI thread when this view model was built on it, otherwise null (a plain
    /// unit test or pool thread, whose own dispatcher may be stale and unpumped, so a post to it would never run).
    /// Deliberately not <c>Dispatcher.UIThread</c>: reading that property off the UI thread creates a UI dispatcher
    /// on the calling thread, which then hijacks the platform the next headless session sets up.
    /// </summary>
    private readonly Dispatcher? dispatcher;
    private bool disposed;

    /// <summary>Starts loading <paramref name="catalog"/> in the background; the groups' <see cref="FontGroupViewModel.Families"/> fill in when it completes.</summary>
    public AppearanceSettingsViewModel(AppPreferences preferences, SystemFontCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        ArgumentNullException.ThrowIfNull(catalog);

        this.preferences = preferences;
        dispatcher = Application.Current is { } app && app.CheckAccess() ? app.Dispatcher : null;
        Ui = new FontGroupViewModel(preferences, FontGroupKind.Ui);
        Code = new FontGroupViewModel(preferences, FontGroupKind.Code);

        preferences.PropertyChanged += OnPreferencesChanged;
        Loc.Instance.LanguageChanged += OnLanguageChanged;

        // The load is started, not awaited: the constructor runs on the UI thread and must not block on font
        // enumeration. Nothing observes the task except FamiliesLoaded, and LoadFamiliesAsync never faults.
        _ = LoadFamiliesAsync(catalog);
    }

    public FontGroupViewModel Ui { get; }

    public FontGroupViewModel Code { get; }

    /// <summary>
    /// Whether looping animations are off. Bound straight to the preference: the <see cref="AppearanceApplier"/> that already
    /// listens to it pushes it into the global motion gate, so the switch needs no apply step of its own.
    /// </summary>
    public bool ReduceMotion
    {
        get => preferences.ReduceMotion;
        set => preferences.ReduceMotion = value;
    }

    /// <summary>Completes when the font list has been loaded into both groups (for tests).</summary>
    internal Task FamiliesLoaded => familiesLoaded.Task;

    /// <summary>Unsubscribes from the preferences and <c>Loc.LanguageChanged</c>. Idempotent.</summary>
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

    private async Task LoadFamiliesAsync(SystemFontCatalog catalog)
    {
        IReadOnlyList<string> families;
        try
        {
            // Resume on the thread that constructed the view model when it has a synchronization context (the app's UI
            // thread); a context-less host - a plain unit test - resumes on the pool, which PublishFamilies handles.
            families = await catalog.GetFamiliesAsync().ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // The catalog contract says it does not throw; a failing source must still leave an editable section.
            AppLogs.Appearance.ZLogWarning($"the font catalog failed: {exception.Message}");
            families = [];
        }

        PublishFamilies(families);
    }

    /// <summary>
    /// Hands the list over on the constructing thread: bound controls may only be told from their own thread, and the
    /// continuation after the background enumeration is only back on that thread when it has a synchronization context.
    /// A thread without a dispatcher (a unit test) assigns straight away; with a dispatcher the list is only posted and
    /// <see cref="FamiliesLoaded"/> completes from the posted action.
    /// </summary>
    private void PublishFamilies(IReadOnlyList<string> families)
    {
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            AssignFamilies(families);
            return;
        }

        // Off the constructing thread: only the posted action may touch the groups.
        dispatcher.Post(() => AssignFamilies(families));
    }

    /// <summary>
    /// Re-reads both groups' family text, size and preview from the preferences. The preferences finish loading after this
    /// view model is built and the load raises no PropertyChanged, so without this the editors would keep the defaults and
    /// a later commit would write that stale text over the saved value. UI thread only.
    /// </summary>
    internal void SyncAll()
    {
        if (disposed)
        {
            return;
        }

        Ui.SyncFamily();
        Ui.SyncSize();
        Code.SyncFamily();
        Code.SyncSize();
    }

    private void AssignFamilies(IReadOnlyList<string> families)
    {
        Ui.SetFamilies(families);
        Code.SetFamilies(families);
        familiesLoaded.TrySetResult();
    }

    /// <summary>Echoes an external edit (another view, a reset elsewhere) into the editors.</summary>
    private void OnPreferencesChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(AppPreferences.UiFontFamily):
                Ui.SyncFamily();
                break;
            case nameof(AppPreferences.UiFontSize):
                Ui.SyncSize();
                break;
            case nameof(AppPreferences.CodeFontFamily):
                Code.SyncFamily();
                break;
            case nameof(AppPreferences.CodeFontSize):
                Code.SyncSize();
                break;
            case nameof(AppPreferences.ReduceMotion):
                OnPropertyChanged(nameof(ReduceMotion));
                break;
        }
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        Ui.RefreshText();
        Code.RefreshText();
    }
}

/// <summary>Which pair of <see cref="AppPreferences"/> font properties a <see cref="FontGroupViewModel"/> edits.</summary>
internal enum FontGroupKind
{
    Ui,
    Code,
}

/// <summary>One font group (family + size + preview + reset) bound by <c>AppearanceSettingsView</c>.</summary>
internal sealed class FontGroupViewModel : ObservableObject
{
    public const int MinSize = (int)AppPreferences.MinFontSize;
    public const int MaxSize = (int)AppPreferences.MaxFontSize;
    public const int SizeStep = 1;

    // The numeric-up-down range takes decimal(?); the XAML compiler will not convert these int constants, so
    // AppearanceSettingsView binds the decimal mirrors below. The numbers themselves stay in one place.
    public static decimal MinSizeDecimal => MinSize;

    public static decimal MaxSizeDecimal => MaxSize;

    public static decimal SizeStepDecimal => SizeStep;

    private readonly AppPreferences preferences;
    private readonly FontGroupKind kind;
    private readonly RelayCommand resetCommand;
    private IReadOnlyList<string> families = [];
    private string familyText;
    private decimal? size;
    private string previewFamilyName;
    private FontFamily previewFamily;

    internal FontGroupViewModel(AppPreferences preferences, FontGroupKind kind)
    {
        this.preferences = preferences;
        this.kind = kind;
        familyText = StoredFamily;
        size = (decimal)StoredSize;
        previewFamilyName = familyText;
        previewFamily = ParseFamily(familyText);
        resetCommand = new RelayCommand(Reset);
    }

    /// <summary>Group heading in the current language.</summary>
    public string Title => Loc.Instance[kind == FontGroupKind.Ui ? AppearanceStrings.UiGroupTitle : AppearanceStrings.CodeGroupTitle];

    public string FamilyLabel => Loc.Instance[AppearanceStrings.FamilyLabel];

    public string SizeLabel => Loc.Instance[AppearanceStrings.SizeLabel];

    public string ResetText => Loc.Instance[AppearanceStrings.Reset];

    /// <summary>Sample sentence rendered with <see cref="PreviewFamily"/>/<see cref="PreviewSize"/>.</summary>
    public string PreviewText => Loc.Instance[kind == FontGroupKind.Ui ? AppearanceStrings.UiPreview : AppearanceStrings.CodePreview];

    /// <summary>Installed families offered by the editable box; empty until the catalog finished.</summary>
    public IReadOnlyList<string> Families => families;

    /// <summary>The text in the family editor. Typing does not apply; <see cref="CommitFamily"/> does.</summary>
    public string FamilyText
    {
        get => familyText;
        set => Set(ref familyText, value ?? string.Empty);
    }

    /// <summary>
    /// Writes <see cref="FamilyText"/> to the preference (on selection, Enter or focus loss), then re-reads the validated
    /// value into <see cref="FamilyText"/> (an empty text becomes the default family).
    /// </summary>
    public void CommitFamily()
    {
        if (kind == FontGroupKind.Ui)
        {
            preferences.UiFontFamily = familyText;
        }
        else
        {
            preferences.CodeFontFamily = familyText;
        }

        // The setter trims and substitutes the default for an empty text; the preference change may not have raised
        // PropertyChanged (the value can already be stored), so read the editor's value back unconditionally.
        SyncFamily();
    }

    /// <summary>Bound to <c>NumericUpDown.Value</c>; setting applies immediately (null is ignored).</summary>
    public decimal? Size
    {
        get => size;
        set
        {
            // NumericUpDown clears its value while the user edits the text into an invalid state; there is nothing to apply.
            if (value is not { } requested)
            {
                return;
            }

            // The spec's step is 1 (integer sizes); typed fractions round half away from zero.
            var rounded = (double)Math.Round(requested, MidpointRounding.AwayFromZero);
            if (kind == FontGroupKind.Ui)
            {
                preferences.UiFontSize = rounded;
            }
            else
            {
                preferences.CodeFontSize = rounded;
            }

            SyncSize();
        }
    }

    public FontFamily PreviewFamily => previewFamily;

    public double PreviewSize => StoredSize;

    /// <summary>Resets only this group's family and size to the defaults of <see cref="AppPreferences"/>.</summary>
    public ICommand ResetCommand => resetCommand;

    private string StoredFamily => kind == FontGroupKind.Ui ? preferences.UiFontFamily : preferences.CodeFontFamily;

    private double StoredSize => kind == FontGroupKind.Ui ? preferences.UiFontSize : preferences.CodeFontSize;

    internal void SetFamilies(IReadOnlyList<string> loaded) => Set(ref families, loaded, nameof(Families));

    /// <summary>Re-reads the family preference into the editor and the preview; called for external changes and after a write.</summary>
    internal void SyncFamily()
    {
        var stored = StoredFamily;
        if (!string.Equals(familyText, stored, StringComparison.Ordinal))
        {
            familyText = stored;
            OnPropertyChanged(nameof(FamilyText));
        }

        // Only a changed family needs a new preview; comparing the source text avoids relying on FontFamily equality.
        if (!string.Equals(previewFamilyName, stored, StringComparison.Ordinal))
        {
            previewFamilyName = stored;
            previewFamily = ParseFamily(stored);
            OnPropertyChanged(nameof(PreviewFamily));
        }
    }

    /// <summary>Re-reads the size preference into the spin box and the preview; the preference already clamped it.</summary>
    internal void SyncSize()
    {
        var stored = (decimal)StoredSize;
        if (size != stored)
        {
            size = stored;
            OnPropertyChanged(nameof(Size));
        }

        OnPropertyChanged(nameof(PreviewSize));
    }

    /// <summary>Re-reads the strings that come from <see cref="Loc"/> after a language switch.</summary>
    internal void RefreshText()
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(FamilyLabel));
        OnPropertyChanged(nameof(SizeLabel));
        OnPropertyChanged(nameof(ResetText));
        OnPropertyChanged(nameof(PreviewText));
    }

    private void Reset()
    {
        if (kind == FontGroupKind.Ui)
        {
            preferences.UiFontFamily = AppPreferences.DefaultUiFontFamily;
            preferences.UiFontSize = AppPreferences.DefaultUiFontSize;
        }
        else
        {
            preferences.CodeFontFamily = AppPreferences.DefaultCodeFontFamily;
            preferences.CodeFontSize = AppPreferences.DefaultCodeFontSize;
        }

        // Same reasoning as CommitFamily: the defaults may already be stored, so the editors are re-read explicitly.
        SyncFamily();
        SyncSize();
    }

    /// <summary>
    /// The preview font for a preference value. A family list that does not parse (a typo in a fallback chain) shows the
    /// default font instead of throwing; the text stays editable.
    /// </summary>
    private static FontFamily ParseFamily(string family)
    {
        try
        {
            return FontFamily.Parse(family);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            AppLogs.Appearance.ZLogWarning($"unparsable font family: {family} ({exception.Message})");
            return FontFamily.Default;
        }
    }
}
