using System.Runtime.CompilerServices;

namespace Forge.App.Localization;

/// <summary>
/// The inspector's table: the files panel, the preview, the changes document, TODO, review and the spec tree.
/// <para>Keys: <c>files.*</c>, <c>changes.*</c>, <c>todo.*</c>, <c>review.*</c>, <c>spec.*</c>. Each key has a Chinese and an English text; parameterized texts are exposed as typed accessor
/// methods (formatted through <see cref="Loc.Format"/>), parameterless ones as properties that read
/// <see cref="Loc.Instance"/>, so callers never cache a resolved string across a language switch.</para>
/// </summary>
internal static class InspectorStrings
{
    /// <summary>Registers this table before any code of the assembly runs (module initializer, AOT safe).</summary>
    [ModuleInitializer]
    internal static void Initialize() => Register();

    /// <summary>Idempotent: registering the same key with the same texts twice is a no-op.</summary>
    internal static void Register()
    {
    }
}
