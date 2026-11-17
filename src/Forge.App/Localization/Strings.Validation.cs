using System.Globalization;
using System.Runtime.CompilerServices;
using Forge.App.ViewModels;

namespace Forge.App.Localization;

/// <summary>
/// The shared messages of <see cref="InputRules"/>. Every settings field that judges a text value reports its failure through
/// these keys, so the same invalid input reads the same way on every page. Callers only choose the rule; the wording and the
/// save-blocked summary stay here.
/// </summary>
internal static class InputValidationStrings
{
    public const string RequiredKey = "validation.required";
    public const string PositiveIntegerKey = "validation.positiveInteger";
    public const string NonNegativeDecimalKey = "validation.nonNegativeDecimal";
    public const string AbsoluteHttpUrlKey = "validation.absoluteHttpUrl";
    public const string AbsolutePathKey = "validation.absolutePath";
    public const string EnvironmentVariableKey = "validation.environmentVariable";
    public const string PriceSetKey = "validation.priceSet";
    public const string SaveBlockedKey = "validation.saveBlocked";

    public static string Required => Loc.Instance[RequiredKey];

    public static string PositiveInteger => Loc.Instance[PositiveIntegerKey];

    public static string NonNegativeDecimal(int maxDecimals) =>
        Loc.Instance.Format(NonNegativeDecimalKey, maxDecimals.ToString(CultureInfo.InvariantCulture));

    public static string AbsoluteHttpUrl => Loc.Instance[AbsoluteHttpUrlKey];

    public static string AbsolutePath => Loc.Instance[AbsolutePathKey];

    public static string EnvironmentVariable => Loc.Instance[EnvironmentVariableKey];

    public static string PriceSet => Loc.Instance[PriceSetKey];

    /// <summary>The footer line when a save was stopped by invalid fields; <paramref name="count"/> is the number of them.</summary>
    public static string SaveBlocked(int count) =>
        Loc.Instance.Format(SaveBlockedKey, count.ToString(CultureInfo.InvariantCulture));

    public static string Describe(InputError? error) => error?.Kind switch
    {
        InputRuleKind.Required => Required,
        InputRuleKind.PositiveInteger => PositiveInteger,
        InputRuleKind.NonNegativeDecimal => NonNegativeDecimal(error.Value.MaxDecimals),
        InputRuleKind.AbsoluteHttpUrl => AbsoluteHttpUrl,
        InputRuleKind.AbsolutePath => AbsolutePath,
        InputRuleKind.EnvironmentVariable => EnvironmentVariable,
        InputRuleKind.PriceSet => PriceSet,
        _ => string.Empty,
    };

    [ModuleInitializer]
    internal static void Initialize() => Register();

    internal static void Register()
    {
        Loc.Register(RequiredKey, "不能为空", "Required");
        Loc.Register(PositiveIntegerKey, "请输入正整数，或留空表示未知", "Enter a positive whole number, or leave it empty for unknown");
        Loc.Register(
            NonNegativeDecimalKey,
            "请输入非负数，最多 {0} 位小数；留空表示未知",
            "Enter a non-negative number with at most {0} decimals, or leave it empty for unknown");
        Loc.Register(AbsoluteHttpUrlKey, "请输入完整的 http(s) 地址", "Enter a complete http(s) URL");
        Loc.Register(AbsolutePathKey, "请输入绝对路径", "Enter an absolute path");
        Loc.Register(EnvironmentVariableKey, "环境变量名只能包含字母、数字与下划线，且不能以数字开头", "Use letters, digits and underscores, and do not start with a digit");
        Loc.Register(PriceSetKey, "价格要成套填写：要么全部留空（未知），要么把可见的价格都填上", "Fill every visible price, or leave them all empty for unknown");
        Loc.Register(SaveBlockedKey, "有 {0} 处输入格式不正确，未保存", "{0} fields are invalid; nothing was saved");
    }
}
