using Forge.Abstractions;
using Forge.App.Localization;

namespace Forge.App.Approval;

/// <summary>
/// Typed access to <see cref="ExtensionsStrings"/>, plus the two lookups that turn an enum or a host status string into
/// text. Kept in the approval layer so the localization table stays free of approval types.
/// </summary>
internal static class ApprovalText
{
    /// <summary>The side effect of a tool call as shown on the card and in the remembered rules list.</summary>
    public static string SideEffect(ToolSideEffect effect) =>
        Loc.Instance[effect switch
        {
            ToolSideEffect.ReadOnly => ExtensionsStrings.SideEffectReadOnly,
            ToolSideEffect.Write => ExtensionsStrings.SideEffectWrite,
            _ => ExtensionsStrings.SideEffectExecute,
        }];

    /// <summary>The scope group of the settings section and the remembered-rules list.</summary>
    public static string Scope(ApprovalScope scope) =>
        Loc.Instance[scope switch
        {
            ApprovalScope.Session => ExtensionsStrings.SettingsScopeSession,
            ApprovalScope.Project => ExtensionsStrings.SettingsScopeProject,
            _ => ExtensionsStrings.SettingsScopeGlobal,
        }];

    /// <summary>A <c>plugins.status</c> value as localized text; an unknown status stays visible instead of being hidden.</summary>
    public static string Status(string status) =>
        Loc.Instance[status switch
        {
            "Draft" => ExtensionsStrings.StatusDraft,
            "Approved" => ExtensionsStrings.StatusApproved,
            "Disabled" => ExtensionsStrings.StatusDisabled,
            "Uninstalled" => ExtensionsStrings.StatusUninstalled,
            "Pending" => ExtensionsStrings.StatusPending,
            _ => ExtensionsStrings.StatusUnknown,
        }];

    /// <summary>The extension kind as shown on the plugin card.</summary>
    public static string Kind(string kind) =>
        Loc.Instance[string.Equals(kind, Forge.Core.Tools.ExtensionDraftKinds.Workflow, StringComparison.Ordinal)
            ? ExtensionsStrings.ExtensionKindWorkflow
            : ExtensionsStrings.ExtensionKindPlugin];
}
