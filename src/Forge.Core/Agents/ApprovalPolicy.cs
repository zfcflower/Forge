using Forge.Abstractions;

namespace Forge.Core.Agents;

/// <summary>
/// The default approval rule, in this order: an explicit per-tool rule wins, a read-only tool passes, the user's global
/// "all authorized" mode passes, a call whose paths stay inside the workspace passes, and everything else - a write or an
/// execution that names a path outside the workspace, or a call whose scope could not be read - asks the user.
/// <para>Read-only covers network tools as well (<c>web_fetch</c>, <c>web_search</c>): with the workspace as the boundary,
/// egress is no longer a reason to interrupt. A tool that must always ask keeps that by rule.</para>
/// </summary>
public sealed class DefaultApprovalPolicy : IApprovalPolicy
{
    private static readonly IReadOnlyDictionary<string, ApprovalMode> NoRules =
        new Dictionary<string, ApprovalMode>(StringComparer.Ordinal);

    /// <summary>
    /// The user's global permission setting, read on every evaluation rather than captured, so flipping it in the settings
    /// reaches the next call of every running session. Null means <see cref="ToolPermissionMode.Workspace"/>.
    /// </summary>
    public Func<ToolPermissionMode>? Mode { get; init; }

    public IReadOnlyDictionary<string, ApprovalMode> ToolRules { get; init; } = NoRules;

    public ApprovalMode Evaluate(ApprovalRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (ToolRules.TryGetValue(request.ToolName, out var rule))
        {
            return rule;
        }

        if (request.SideEffect == ToolSideEffect.ReadOnly)
        {
            return ApprovalMode.Allow;
        }

        if ((Mode?.Invoke() ?? ToolPermissionMode.Workspace) == ToolPermissionMode.All)
        {
            return ApprovalMode.Allow;
        }

        return request.Scope == ToolCallScope.OutsideWorkspace ? ApprovalMode.Ask : ApprovalMode.Allow;
    }
}
