using Forge.App.Localization;
using Forge.App.Providers;
using Forge.App.ViewModels;

namespace Forge.App.Settings;

/// <summary>
/// The usage-log row of the provider page: one switch, off by default, that starts recording what every model request cost — the shape
/// it claimed, the cache lifetime, the tokens by category, the account's meters and the latency — into a file per day under the data
/// root.
/// </summary>
/// <remarks>
/// The toggle saves at once and the recorder reads the live store before each write, so it takes effect on the next request without a
/// restart. Nothing about a prompt's text is recorded; the hint says so, because a log of a conversation is exactly the kind of thing a
/// user deserves to know the scope of before turning it on.
/// </remarks>
internal sealed class UsageLogSettingsViewModel : NotifyingObject, IDisposable
{
    private readonly UsageLogSettingsStore store;
    private readonly Func<string> directory;
    private bool enabled;
    private string? saveError;
    private bool loading;

    /// <param name="directory">Where the files land, for the hint under the switch; a function so a moved data root shows up.</param>
    public UsageLogSettingsViewModel(UsageLogSettingsStore store, Func<string> directory)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(directory);
        this.store = store;
        this.directory = directory;
        Load();
        Loc.Instance.LanguageChanged += OnLanguageChanged;
    }

    public string Label => ProviderStrings.UsageLogEnabled;

    public string Hint => ProviderStrings.UsageLogHint(directory());

    public bool HasSaveError => saveError is not null;

    public string? SaveError => saveError;

    public bool Enabled
    {
        get => enabled;
        set
        {
            if (!Set(ref enabled, value) || loading)
            {
                return;
            }

            try
            {
                store.Save(new UsageLogSettings(value));
                saveError = null;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                saveError = ProviderStrings.UsageLogSaveFailed(exception.Message);
            }

            OnPropertyChanged(nameof(HasSaveError));
            OnPropertyChanged(nameof(SaveError));
        }
    }

    public void Reload() => Load();

    public void Dispose() => Loc.Instance.LanguageChanged -= OnLanguageChanged;

    private void Load()
    {
        loading = true;
        try
        {
            enabled = store.Current.Enabled;
        }
        finally
        {
            loading = false;
        }

        saveError = null;
        OnPropertyChanged(nameof(Enabled));
        OnPropertyChanged(nameof(Hint));
        OnPropertyChanged(nameof(HasSaveError));
        OnPropertyChanged(nameof(SaveError));
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(Label));
        OnPropertyChanged(nameof(Hint));
        if (SaveError is not null)
        {
            OnPropertyChanged(nameof(SaveError));
        }
    }
}
