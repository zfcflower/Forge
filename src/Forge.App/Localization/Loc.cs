using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using UiLanguage = Forge.App.Preferences.UiLanguage;

namespace Forge.App.Localization;

/// <summary>
/// The live UI string table. Every user-visible string is a key with a Chinese and an English text, declared in a
/// feature's own static table (one file per feature, e.g. <c>Strings.Terminal.cs</c>, each calling <see cref="Register"/>
/// from its own <c>[ModuleInitializer]</c>), so parallel features never edit one shared file.
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

    /// <summary>
    /// Formats a count-dependent text (see <see cref="LocText.PluralKey"/>) with its count grouped, so every counted
    /// sentence shows the number the same way.
    /// </summary>
    public string FormatPlural(string keyBase, long count) =>
        Format(LocText.PluralKey(keyBase, count), TextFormat.Number(count));

    /// <summary>Formats a text whose two placeholders are both counts (<c>kept {0}/{1}</c>), each grouped.</summary>
    public string FormatCounts(string key, long first, long second) =>
        Format(key, TextFormat.Number(first), TextFormat.Number(second));

    /// <summary>Formats a text whose argument is a diagnostic that git may not have given; it shows as <c>-</c> then.</summary>
    public string FormatReason(string key, string? reason) =>
        Format(key, string.IsNullOrWhiteSpace(reason) ? "-" : reason);

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

                // Developer-facing only (a defect, never a user path): English, like every exception text in this app.
                throw new InvalidOperationException("Localization key registered twice with different texts: " + key);
            }

            Entries.Add(key, new Entry(chinese, english));
            KeysInOrder.Add(key);
        }
    }

    /// <summary>
    /// Order point called once by the composition root before the first window: the feature tables register from their own
    /// module initializers; this only re-runs the core table's (idempotent) registration explicitly.
    /// </summary>
    public static void EnsureRegistered()
    {
        // Each table registers from its own [ModuleInitializer] (Strings.Core.cs is the reference), which the runtime
        // runs before any code of this assembly; the explicit call keeps the order point and makes the core table's
        // registration idempotent-by-contract instead of implicit.
        CoreStrings.Register();
    }

    /// <summary>
    /// Test seam: when set, <see cref="UiLanguage.System"/> resolves to this language instead of the machine's, so test
    /// assertions on texts do not depend on the OS display language of the machine they run on. Never set by the product.
    /// </summary>
    internal static UiLanguage? SystemLanguageOverride { get; set; }

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
    /// The concrete language behind <see cref="UiLanguage.System"/>, never <see cref="UiLanguage.System"/> itself.
    /// <see cref="SystemLanguageDetector"/> owns the decision - culture names, the Windows display language, the POSIX
    /// locale variables - so this build's invariant globalization does not pin the UI to English.
    /// <see cref="SystemLanguageOverride"/> is the test seam and wins when set.
    /// <para>Also read by the language settings section, which says under "follow the system" which language that choice
    /// currently resolves to; that is why the property is not private.</para>
    /// </summary>
    internal static UiLanguage SystemLanguage =>
        SystemLanguageOverride is { } forced && forced != UiLanguage.System
            ? forced
            : SystemLanguageDetector.Resolve(SystemLanguageDetector.ReadCurrent());

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
