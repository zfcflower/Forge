using System.Runtime.CompilerServices;
using Forge.App.Localization;
using Forge.App.Preferences;

namespace Forge.App.Tests.Localization;

/// <summary>
/// Pins "follow the system" to Chinese for the whole test assembly, before any test runs: the product texts asserted by
/// the tests are the Chinese ones, and without this the outcome would depend on the display language of the machine (CI
/// runners resolve to English). A test that needs English sets <see cref="Loc.Language"/> explicitly and restores it.
/// </summary>
internal static class TestLanguageSetup
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        Loc.SystemLanguageOverride = UiLanguage.Chinese;
        Loc.Instance.Language = UiLanguage.System;
    }
}
