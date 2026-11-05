using System.Runtime.CompilerServices;

namespace Forge.App.Localization;

/// <summary>
/// The language settings section's table.
/// <para>Keys: <c>language.*</c>. Each key has a Chinese and an English text; parameterized texts are exposed as typed accessor
/// methods (formatted through <see cref="Loc.Format"/>), parameterless ones as properties that read
/// <see cref="Loc.Instance"/>, so callers never cache a resolved string across a language switch.</para>
/// </summary>
internal static class LanguageStrings
{
    /// <summary>Registers this table before any code of the assembly runs (module initializer, AOT safe).</summary>
    [ModuleInitializer]
    internal static void Initialize() => Register();

    /// <summary>Idempotent: registering the same key with the same texts twice is a no-op.</summary>
    internal static void Register()
    {
    }
}
