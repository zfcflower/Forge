using System.Runtime.CompilerServices;

namespace Forge.App.Localization;

/// <summary>
/// The <c>format.*</c> table: the elapsed-time patterns, the only part of <see cref="TextFormat"/> that differs per language.
/// The key constants stay on <see cref="TextFormat"/> so callers keep one entry point.
/// </summary>
internal static class FormatStrings
{
    /// <summary>Registers this table before any code of the assembly runs (module initializer, AOT safe).</summary>
    [ModuleInitializer]
    internal static void Initialize() => Register();

    /// <summary>Idempotent: registering the same key with the same texts twice is a no-op.</summary>
    internal static void Register()
    {
        Loc.Register(TextFormat.SecondsKey, "{0} 秒", "{0}s");
        Loc.Register(TextFormat.MinutesSecondsKey, "{0} 分 {1} 秒", "{0}m {1}s");
    }
}
