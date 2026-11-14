using System.Runtime.CompilerServices;

namespace Forge.App.Localization;

/// <summary>
/// The <c>models-visibility</c> settings section (wave W1): its heading, the one sentence that explains what hiding does,
/// the search box, the per-row switch, the hidden count and the empty state. Keys are namespaced <c>modelsVisibility.</c>;
/// the row label itself is the model name, which is user data and never translated.
/// </summary>
internal static class ModelsVisibilityStrings
{
    /// <summary>Title of the settings section; the section registration passes this key.</summary>
    public const string SectionTitleKey = "modelsVisibility.section";

    public const string HintKey = "modelsVisibility.hint";

    /// <summary>Placeholder of the search box: it filters this page only, so it is not a preference.</summary>
    public const string SearchPlaceholderKey = "modelsVisibility.search";

    /// <summary>Name of the per-row switch (the column heading and each checkbox's accessible name).</summary>
    public const string VisibilityColumnKey = "modelsVisibility.shown";

    /// <summary>How many of the listed models are hidden; count-dependent (<c>.one</c>/<c>.other</c>).</summary>
    public const string HiddenCountKeyBase = "modelsVisibility.hiddenCount";

    /// <summary>Shown instead of the list while no provider is connected.</summary>
    public const string EmptyKey = "modelsVisibility.empty";

    /// <summary>The button that clears every hidden model, listed or not.</summary>
    public const string ShowAllKey = "modelsVisibility.showAll";

    public static string SectionTitle => Loc.Instance[SectionTitleKey];

    public static string Hint => Loc.Instance[HintKey];

    public static string SearchPlaceholder => Loc.Instance[SearchPlaceholderKey];

    public static string VisibilityColumn => Loc.Instance[VisibilityColumnKey];

    public static string HiddenCount(int count) => Loc.Instance.FormatPlural(HiddenCountKeyBase, count);

    public static string Empty => Loc.Instance[EmptyKey];

    public static string ShowAll => Loc.Instance[ShowAllKey];

    [ModuleInitializer]
    internal static void Initialize() => Register();

    /// <summary>Idempotent: registering the same key with the same texts twice is a no-op.</summary>
    internal static void Register()
    {
        Loc.Register(SectionTitleKey, "模型可见性", "Model visibility");
        Loc.Register(
            HintKey,
            "取消勾选后,该模型不再出现在输入框的选型器和内置 Agent 的默认模型下拉里。当前正在用的模型、已固定的模型与已保存的默认值照常显示,随时可以改回来。搜索只过滤本页列表,不是偏好项。",
            "An unchecked model is no longer offered by the chat model picker or the built-in agents' default-model combo. The model in use, a pinned model and a stored default still show, and can be chosen again. The search only filters this page — it is not a preference.");

        Loc.Register(ShowAllKey, "全部显示", "Show all");
        Loc.Register(SearchPlaceholderKey, "搜索模型", "Search models");
        Loc.Register(VisibilityColumnKey, "在选型器中显示", "Shown in pickers");
        Loc.Register(HiddenCountKeyBase + ".one", "{0} 个模型已隐藏", "{0} model hidden");
        Loc.Register(HiddenCountKeyBase + ".other", "{0} 个模型已隐藏", "{0} models hidden");
        Loc.Register(
            EmptyKey,
            "还没有连接任何提供商。先在「提供商」分区添加一个,它的模型就会出现在这里。",
            "No provider is connected yet. Add one in the Providers section and its models appear here.");
    }
}
