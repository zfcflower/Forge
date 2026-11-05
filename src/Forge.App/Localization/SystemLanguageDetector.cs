using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using Microsoft.Win32;
using UiLanguage = Forge.App.Preferences.UiLanguage;

namespace Forge.App.Localization;

/// <summary>
/// Resolves "follow the system" to a concrete language. The build keeps <c>InvariantGlobalization</c> (no ICU), so the
/// culture properties are usually the invariant culture; on Windows the user's display language is read from the
/// registry (BCL <c>Microsoft.Win32.Registry</c>, no P/Invoke), elsewhere from the POSIX locale variables.
/// <para>Order: culture names → Windows registry → <c>LC_ALL</c> → <c>LC_MESSAGES</c> → <c>LANG</c> → English. The
/// registry goes before the variables because Git Bash exports <c>LANG=en_US.UTF-8</c>, and a Chinese Windows started
/// from there must stay Chinese.</para>
/// <para>Each step decides as soon as it has a value: a Windows display language or a locale variable that exists is
/// authoritative, not merely consulted for "is it Chinese". Only the culture-name step looks for Chinese alone, because
/// under invariant globalization both names are the invariant culture and never decide anything.</para>
/// </summary>
internal static class SystemLanguageDetector
{
    /// <summary>The <c>HKCU</c> subkey that holds the display language the user picked, when it exists.</summary>
    private const string DesktopKey = @"Control Panel\Desktop";

    /// <summary>The <c>HKCU</c> subkey that caches the languages the machine was set up with.</summary>
    private const string MuiCachedKey = @"Control Panel\Desktop\MuiCached";

    /// <summary>The <c>HKCU</c> subkey that holds the regional format settings.</summary>
    private const string InternationalKey = @"Control Panel\International";

    /// <summary>Everything the decision depends on, read once by <see cref="ReadCurrent"/>; tests build it by hand.</summary>
    /// <param name="CurrentUiCulture"><c>CultureInfo.CurrentUICulture.Name</c> (empty under invariant globalization).</param>
    /// <param name="InstalledUiCulture"><c>CultureInfo.InstalledUICulture.Name</c>.</param>
    /// <param name="WindowsUiLanguage">
    /// Windows only: the first entry of the first registry value that exists, in this order -
    /// <c>HKCU\Control Panel\Desktop</c> <c>PreferredUILanguages</c> (multi-string, set when the user picked a display
    /// language), <c>HKCU\Control Panel\Desktop\MuiCached</c> <c>MachinePreferredUILanguages</c> (multi-string),
    /// <c>HKCU\Control Panel\International</c> <c>LocaleName</c> (string). <see langword="null"/> elsewhere, when none
    /// exists, or when reading failed.
    /// </param>
    /// <param name="LcAll">The <c>LC_ALL</c> variable.</param>
    /// <param name="LcMessages">The <c>LC_MESSAGES</c> variable.</param>
    /// <param name="Lang">The <c>LANG</c> variable.</param>
    internal readonly record struct Inputs(
        string? CurrentUiCulture,
        string? InstalledUiCulture,
        string? WindowsUiLanguage,
        string? LcAll,
        string? LcMessages,
        string? Lang);

    /// <summary>Pure decision over <paramref name="inputs"/>; never returns <see cref="UiLanguage.System"/>.</summary>
    public static UiLanguage Resolve(in Inputs inputs)
    {
        // 1. The culture names. Chinese-only on purpose: this build has no ICU data, so both are the invariant culture
        //    and an empty name says nothing about the user's choice - it must not be read as "English decided".
        if (IsChinese(inputs.CurrentUiCulture) || IsChinese(inputs.InstalledUiCulture))
        {
            return UiLanguage.Chinese;
        }

        // 2. Windows: the registry is the display language the user actually picked, so it decides even when it says
        //    "not Chinese" - a locale variable from the launching shell must not override it.
        if (HasValue(inputs.WindowsUiLanguage))
        {
            return IsChinese(inputs.WindowsUiLanguage) ? UiLanguage.Chinese : UiLanguage.English;
        }

        // 3. Elsewhere (and on a Windows account whose registry has nothing): the POSIX variables, highest priority
        //    first. LC_ALL overrides the more specific variables by POSIX definition, so the first one that is set and
        //    not Chinese means English even if a lower-priority one is Chinese.
        if (HasValue(inputs.LcAll))
        {
            return IsChinese(inputs.LcAll) ? UiLanguage.Chinese : UiLanguage.English;
        }

        if (HasValue(inputs.LcMessages))
        {
            return IsChinese(inputs.LcMessages) ? UiLanguage.Chinese : UiLanguage.English;
        }

        if (HasValue(inputs.Lang))
        {
            return IsChinese(inputs.Lang) ? UiLanguage.Chinese : UiLanguage.English;
        }

        // 4. Nothing to go on: the app's built-in default.
        return UiLanguage.English;
    }

    /// <summary>
    /// Reads the current process's inputs. Never throws: a registry read that fails (security, IO, a value of the wrong
    /// kind) counts as "not present".
    /// </summary>
    public static Inputs ReadCurrent() =>
        new(
            CultureInfo.CurrentUICulture.Name,
            CultureInfo.InstalledUICulture.Name,
            ReadWindowsUiLanguage(),
            ReadVariable("LC_ALL"),
            ReadVariable("LC_MESSAGES"),
            ReadVariable("LANG"));

    /// <summary>
    /// True for <c>zh</c>, <c>zh-*</c> and <c>zh_*</c> (e.g. <c>zh-CN</c>, <c>zh-Hans-CN</c>, <c>zh_CN.UTF-8</c>),
    /// case-insensitive; false for <c>zhx</c>, empty and <see langword="null"/>.
    /// </summary>
    public static bool IsChinese(string? culture) =>
        !string.IsNullOrEmpty(culture) &&
        culture.StartsWith("zh", StringComparison.OrdinalIgnoreCase) &&
        (culture.Length == 2 || culture[2] is '-' or '_');

    private static bool HasValue(string? candidate) => !string.IsNullOrWhiteSpace(candidate);

    /// <summary>
    /// An environment variable, trimmed; <see langword="null"/> when it is unset, empty or blank (a variable set to
    /// nothing carries no choice).
    /// </summary>
    private static string? ReadVariable(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return HasValue(value) ? value!.Trim() : null;
    }

    /// <summary>
    /// The Windows display language, or <see langword="null"/> when the platform is not Windows or no value could be
    /// read. The order is the user's own pick first, then the machine's, then the regional format's language.
    /// </summary>
    private static string? ReadWindowsUiLanguage()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        return ReadFirstEntry(DesktopKey, "PreferredUILanguages")
            ?? ReadFirstEntry(MuiCachedKey, "MachinePreferredUILanguages")
            ?? ReadSingleValue(InternationalKey, "LocaleName");
    }

    /// <summary>The first non-blank entry of a <c>REG_MULTI_SZ</c> value; <see langword="null"/> when absent or not a multi-string.</summary>
    [SupportedOSPlatform("windows")]
    private static string? ReadFirstEntry(string path, string name)
    {
        if (ReadValue(path, name) is not string[] { Length: > 0 } entries)
        {
            return null;
        }

        foreach (var entry in entries)
        {
            if (HasValue(entry))
            {
                return entry.Trim();
            }
        }

        return null;
    }

    /// <summary>A <c>REG_SZ</c> value, trimmed; <see langword="null"/> when absent or not a string.</summary>
    [SupportedOSPlatform("windows")]
    private static string? ReadSingleValue(string path, string name)
    {
        if (ReadValue(path, name) is not string value)
        {
            return null;
        }

        return HasValue(value) ? value.Trim() : null;
    }

    /// <summary>
    /// The raw value under <c>HKCU</c>, or <see langword="null"/> when it cannot be read. Environment names are not
    /// expanded: a locale name is never an <c>%VAR%</c> reference, and expansion could only corrupt one.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static object? ReadValue(string path, string name)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(path, writable: false);
            return key?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Security, IO, a hive that does not exist for this account: the UI language is a preference, so a value
            // that cannot be read is "not present" instead of a start-up failure. Reported once per read, not swallowed.
            Trace.WriteLine($"[loc] reading HKCU\\{path}\\{name} failed: {exception.Message}");
            return null;
        }
    }
}
