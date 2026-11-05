using System.Runtime.CompilerServices;

namespace Forge.App.Localization;

/// <summary>The appearance (font) settings section's string table; keys are namespaced <c>appearance.*</c>.</summary>
internal static class AppearanceStrings
{
    public const string SectionTitle = "appearance.section";

    public const string UiGroupTitle = "appearance.ui.title";
    public const string CodeGroupTitle = "appearance.code.title";

    public const string FamilyLabel = "appearance.family";
    public const string SizeLabel = "appearance.size";
    public const string FamilyPlaceholder = "appearance.family.placeholder";
    public const string Reset = "appearance.reset";

    public const string UiPreview = "appearance.ui.preview";
    public const string CodePreview = "appearance.code.preview";

    [ModuleInitializer]
    internal static void Initialize() => Register();

    /// <summary>Idempotent: registering the same key with the same texts twice is a no-op.</summary>
    internal static void Register()
    {
    }
}
