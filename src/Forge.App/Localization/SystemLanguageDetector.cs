using UiLanguage = Forge.App.Preferences.UiLanguage;

namespace Forge.App.Localization;

/// <summary>
/// Resolves "follow the system" to a concrete language. The build keeps <c>InvariantGlobalization</c> (no ICU), so the
/// culture properties are usually the invariant culture; on Windows the user's display language is read from the
/// registry (BCL <c>Microsoft.Win32.Registry</c>, no P/Invoke), elsewhere from the POSIX locale variables.
/// <para>Order: culture names → Windows registry → <c>LC_ALL</c> → <c>LC_MESSAGES</c> → <c>LANG</c> → English. The
/// registry goes before the variables because Git Bash exports <c>LANG=en_US.UTF-8</c>, and a Chinese Windows started
/// from there must stay Chinese.</para>
/// </summary>
internal static class SystemLanguageDetector
{
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
    public static UiLanguage Resolve(in Inputs inputs) => throw new NotImplementedException();

    /// <summary>
    /// Reads the current process's inputs. Never throws: a registry read that fails (security, IO, a value of the wrong
    /// kind) counts as "not present".
    /// </summary>
    public static Inputs ReadCurrent() => throw new NotImplementedException();

    /// <summary>
    /// True for <c>zh</c>, <c>zh-*</c> and <c>zh_*</c> (e.g. <c>zh-CN</c>, <c>zh-Hans-CN</c>, <c>zh_CN.UTF-8</c>),
    /// case-insensitive; false for <c>zhx</c>, empty and <see langword="null"/>.
    /// </summary>
    public static bool IsChinese(string? culture) =>
        !string.IsNullOrEmpty(culture) &&
        culture.StartsWith("zh", StringComparison.OrdinalIgnoreCase) &&
        (culture.Length == 2 || culture[2] is '-' or '_');
}
