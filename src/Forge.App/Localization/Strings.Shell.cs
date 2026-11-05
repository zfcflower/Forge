using System.Runtime.CompilerServices;

namespace Forge.App.Localization;

/// <summary>
/// The shell's table: title bar and window buttons, confirmation dialogs, workbench panel and tab titles, panel title-bar button tooltips, tab context menus, the status bar, provider status and startup diagnostics.
/// <para>Keys: <c>shell.*</c>, <c>workbench.*</c>, <c>status.*</c>, <c>provider.*</c>, <c>diag.*</c>. Each key has a Chinese and an English text; parameterized texts are exposed as typed accessor
/// methods (formatted through <see cref="Loc.Format"/>), parameterless ones as properties that read
/// <see cref="Loc.Instance"/>, so callers never cache a resolved string across a language switch.</para>
/// </summary>
internal static class ShellStrings
{
    /// <summary>Registers this table before any code of the assembly runs (module initializer, AOT safe).</summary>
    [ModuleInitializer]
    internal static void Initialize() => Register();

    /// <summary>Idempotent: registering the same key with the same texts twice is a no-op.</summary>
    internal static void Register()
    {
    }
}
