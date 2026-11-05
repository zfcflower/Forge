using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using UiLanguage = Forge.App.Preferences.UiLanguage;

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
    private static readonly Lock Gate = new();
    private static readonly Dictionary<string, Entry> Entries = new(StringComparer.Ordinal);
    private static readonly List<string> KeysInOrder = [];
    private static readonly HashSet<string> ReportedMissing = new(StringComparer.Ordinal);
    private static readonly PropertyChangedEventArgs ItemChanged = new("Item[]");
    private static readonly PropertyChangedEventArgs IndexerChanged = new("Item");

    private UiLanguage language = SystemLanguage;

    private Loc()
    {
    }

    public static Loc Instance { get; } = new();

    public event PropertyChangedEventHandler? PropertyChanged;

    public event EventHandler? LanguageChanged;

    /// <summary>The resolved language (never <c>System</c>); set from <see cref="Preferences.AppPreferences.Language"/>.</summary>
    public UiLanguage Language
    {
        get => language;
        set
        {
            var resolved = value == UiLanguage.System ? SystemLanguage : value;
            if (language == resolved)
            {
                return;
            }

            language = resolved;
            Raise();
            RaiseLanguageChanged();
        }
    }

    public string this[string key]
    {
        get
        {
            if (!string.IsNullOrEmpty(key))
            {
                lock (Gate)
                {
                    if (Entries.TryGetValue(key, out var entry))
                    {
                        return language == UiLanguage.Chinese ? entry.Chinese : entry.English;
                    }
                }

                ReportMissing(key);
            }

            return "[" + key + "]";
        }
    }

    /// <summary>Formats the localized text of <paramref name="key"/> with invariant-culture arguments.</summary>
    public string Format(string key, params ReadOnlySpan<object?> args) =>
        string.Format(CultureInfo.InvariantCulture, this[key], args);

    public static void Register(string key, string chinese, string english)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(chinese);
        ArgumentException.ThrowIfNullOrWhiteSpace(english);

        lock (Gate)
        {
            if (Entries.TryGetValue(key, out var existing))
            {
                // A table that is registered twice (module initializer plus an explicit call) is a no-op; two tables
                // fighting over one key are a defect.
                if (string.Equals(existing.Chinese, chinese, StringComparison.Ordinal) &&
                    string.Equals(existing.English, english, StringComparison.Ordinal))
                {
                    return;
                }

                throw new InvalidOperationException("文案 key 重复注册且文本不同：" + key);
            }

            Entries.Add(key, new Entry(chinese, english));
            KeysInOrder.Add(key);
        }
    }

    /// <summary>Runs every feature table's static registration; called once by the composition root before the first window.</summary>
    public static void EnsureRegistered()
    {
        // Each table registers from its own [ModuleInitializer] (Strings.Core.cs is the reference), which the runtime
        // runs before any code of this assembly; the explicit call keeps the order point and makes the core table's
        // registration idempotent-by-contract instead of implicit.
        CoreStrings.Register();
    }

    /// <summary>All registered keys; for the completeness test.</summary>
    public static IReadOnlyCollection<string> Keys
    {
        get
        {
            lock (Gate)
            {
                return KeysInOrder.ToArray();
            }
        }
    }

    /// <summary>
    /// The concrete language behind <see cref="UiLanguage.System"/>. This build runs with invariant globalization, so
    /// <see cref="CultureInfo.CurrentUICulture"/> is the invariant culture; the POSIX locale variables are the fallback
    /// that still carries the user's choice there.
    /// </summary>
    private static UiLanguage SystemLanguage
    {
        get
        {
            if (IsChinese(CultureInfo.CurrentUICulture.Name) || IsChinese(CultureInfo.InstalledUICulture.Name))
            {
                return UiLanguage.Chinese;
            }

            // A local, not a static field: Loc's instance is created by the static initializer, and a field declared
            // later in the type would still be null here.
            ReadOnlySpan<string> localeVariables = ["LC_ALL", "LC_MESSAGES", "LANG"];
            foreach (var variable in localeVariables)
            {
                if (IsChinese(Environment.GetEnvironmentVariable(variable)))
                {
                    return UiLanguage.Chinese;
                }
            }

            return UiLanguage.English;
        }
    }

    private static bool IsChinese(string? culture) =>
        !string.IsNullOrEmpty(culture) &&
        culture.StartsWith("zh", StringComparison.OrdinalIgnoreCase) &&
        (culture.Length == 2 || culture[2] is '-' or '_');

    private static void ReportMissing(string key)
    {
        lock (Gate)
        {
            if (!ReportedMissing.Add(key))
            {
                return;
            }
        }

        Trace.WriteLine("[loc] missing key: " + key);
    }

    /// <summary>
    /// Raises both indexer notifications: <c>Item[]</c> is the documented INPC convention, and Avalonia's compiled
    /// indexer binding - the <c>{Binding [Key], Source=...}</c> pattern this table is bound with - only refreshes on the
    /// indexer property name itself, so a single name would leave every bound text stale.
    /// </summary>
    private void Raise()
    {
        RaisePropertyChanged(ItemChanged);
        RaisePropertyChanged(IndexerChanged);
    }

    private void RaiseLanguageChanged() => RaiseAll(LanguageChanged, handler => handler(this, EventArgs.Empty));

    private void RaisePropertyChanged(PropertyChangedEventArgs changed) => RaiseAll(PropertyChanged, handler => handler(this, changed));

    /// <summary>
    /// Invokes every subscriber on its own: one failing subscriber - a control of a closed window whose dispatcher is
    /// gone, in a test host - must not stop the rest from refreshing or turn a language switch into an error for the
    /// caller. The failure is reported, not hidden.
    /// </summary>
    private static void RaiseAll<THandler>(THandler? callbacks, Action<THandler> invoke)
        where THandler : Delegate
    {
        if (callbacks is null)
        {
            return;
        }

        foreach (var callback in callbacks.GetInvocationList())
        {
            try
            {
                invoke((THandler)callback);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                Trace.WriteLine("[loc] a language notification subscriber failed: " + exception.Message);
            }
        }
    }

    private readonly record struct Entry(string Chinese, string English);
}
