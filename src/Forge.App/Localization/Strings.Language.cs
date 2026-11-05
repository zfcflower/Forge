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
    /// <summary>The section heading the composition root registers with <see cref="Preferences.SettingsSections"/>.</summary>
    public const string SectionTitle = "language.section.title";

    public const string System = "language.system";
    public const string SystemResolved = "language.system.resolved";
    public const string Chinese = "language.chinese";
    public const string English = "language.english";
    public const string Hint = "language.hint";

    /// <summary>Registers this table before any code of the assembly runs (module initializer, AOT safe).</summary>
    [ModuleInitializer]
    internal static void Initialize() => Register();

    /// <summary>Idempotent: registering the same key with the same texts twice is a no-op.</summary>
    internal static void Register()
    {
        Loc.Register(SectionTitle, "语言", "Language");

        Loc.Register(System, "跟随系统", "Follow system");

        // The explanation under "follow the system": the argument is the name of the language it resolves to, taken from
        // Chinese or English below, so it follows the UI language with the rest of the section.
        Loc.Register(SystemResolved, "跟随系统(当前:{0})", "Follow system (currently {0})");

        // What each choice shows: "中文" in a Chinese UI and "Chinese" in an English one, while the English choice reads
        // "English" either way - somebody who cannot read the current UI language can still find their own.
        Loc.Register(Chinese, "中文", "Chinese");
        Loc.Register(English, "English", "English");

        Loc.Register(
            Hint,
            "语言只影响界面文案，切换后立即生效；对话内容、你的输入与文件内容不会翻译。",
            "The language only affects interface text and applies immediately; conversations, your input and file contents are not translated.");
    }

    /// <summary>The section heading, in the current UI language.</summary>
    public static string SectionHeading => Loc.Instance[SectionTitle];

    /// <summary>The "follow the system" choice.</summary>
    public static string SystemLabel => Loc.Instance[System];

    /// <summary>"Follow system (currently Chinese)": the choice plus the language it currently resolves to.</summary>
    public static string SystemResolvedText(string languageName) => Loc.Instance.Format(SystemResolved, languageName);

    /// <summary>The name of the Chinese language.</summary>
    public static string ChineseName => Loc.Instance[Chinese];

    /// <summary>The name of the English language.</summary>
    public static string EnglishName => Loc.Instance[English];

    /// <summary>The grey line that says what the setting does.</summary>
    public static string HintText => Loc.Instance[Hint];
}
