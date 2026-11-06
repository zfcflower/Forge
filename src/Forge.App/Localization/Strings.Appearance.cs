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
        Loc.Register(SectionTitle, "外观", "Appearance");

        Loc.Register(UiGroupTitle, "界面字体", "Interface font");
        Loc.Register(CodeGroupTitle, "代码字体", "Code font");

        Loc.Register(FamilyLabel, "字体", "Font family");
        Loc.Register(SizeLabel, "字号", "Size");
        Loc.Register(FamilyPlaceholder, "从已安装字体中选择，或输入逗号分隔的回退列表", "Pick an installed font or type a comma-separated fallback list");
        Loc.Register(Reset, "恢复默认", "Restore defaults");

        Loc.Register(UiPreview, "界面文字预览 The quick brown fox", "Interface preview — 界面文字 The quick brown fox");
        Loc.Register(CodePreview, "代码预览 0O1lI {}[] => != 中文", "Code preview 0O1lI {}[] => != 中文");
    }
}
