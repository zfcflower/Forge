using System.ComponentModel;

namespace Forge.App.Localization;

/// <summary>
/// The live UI string table. Every user-visible string is a key with a Chinese and an English text, declared in a
/// feature's own static table (one file per feature, e.g. <c>Strings.Terminal.cs</c>, each calling <see cref="Register"/>
/// from a static initializer that <see cref="EnsureRegistered"/> triggers), so parallel features never edit one shared file.
/// <para>XAML binds <c>{Binding [Key], Source={x:Static loc:Loc.Instance}}</c> (compiled indexer binding, AOT safe);
/// C# reads <c>Loc.Instance["Key"]</c> or the typed accessors the feature table exposes. Switching
/// <see cref="Language"/> raises PropertyChanged for <c>Item[]</c> so every bound text refreshes without a restart;
/// view models that cache formatted strings listen to <see cref="LanguageChanged"/>.</para>
/// <para>A missing key returns the key itself wrapped in brackets and is reported once through <c>Trace</c>; a key
/// registered twice with different texts throws at registration (caught by a test that loads all tables).</para>
/// </summary>
internal sealed class Loc : INotifyPropertyChanged
{
    public static Loc Instance { get; } = new();

    private Loc()
    {
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public event EventHandler? LanguageChanged;

    /// <summary>The resolved language (never <c>System</c>); set from <see cref="Preferences.AppPreferences.Language"/>.</summary>
    public Preferences.UiLanguage Language { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }

    public string this[string key] => throw new NotImplementedException();

    /// <summary>Formats the localized text of <paramref name="key"/> with invariant-culture arguments.</summary>
    public string Format(string key, params ReadOnlySpan<object?> args) => throw new NotImplementedException();

    public static void Register(string key, string chinese, string english) => throw new NotImplementedException();

    /// <summary>Runs every feature table's static registration; called once by the composition root before the first window.</summary>
    public static void EnsureRegistered() => throw new NotImplementedException();

    /// <summary>All registered keys; for the completeness test.</summary>
    public static IReadOnlyCollection<string> Keys => throw new NotImplementedException();

    private void Raise() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));

    private void RaiseLanguageChanged() => LanguageChanged?.Invoke(this, EventArgs.Empty);
}
