using Forge.Abstractions;

namespace Forge.Core.Agents;

/// <summary>
/// Default approval rules: an explicit per-tool rule wins, read-only tools pass, tools running inside a
/// trusted directory pass, everything else asks the user.
/// </summary>
public sealed class DefaultApprovalPolicy : IApprovalPolicy
{
    private static readonly IReadOnlyDictionary<string, ApprovalMode> NoRules =
        new Dictionary<string, ApprovalMode>(StringComparer.Ordinal);

    public bool AutoAllowReadOnly { get; init; } = true;

    public IReadOnlyDictionary<string, ApprovalMode> ToolRules { get; init; } = NoRules;

    public IReadOnlyList<string> TrustedDirectories { get; init; } = [];

    public ApprovalMode Evaluate(ApprovalRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (ToolRules.TryGetValue(request.ToolName, out var rule))
        {
            return rule;
        }

        if (request.SideEffect == ToolSideEffect.ReadOnly && AutoAllowReadOnly)
        {
            return ApprovalMode.Allow;
        }

        foreach (var directory in TrustedDirectories)
        {
            if (IsUnder(request.WorkingDirectory, directory))
            {
                return ApprovalMode.Allow;
            }
        }

        return ApprovalMode.Ask;
    }

    private static bool IsUnder(string path, string root)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            return false;
        }

        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var candidate = Trim(Path.GetFullPath(path));
        var trusted = Trim(Path.GetFullPath(root));
        return candidate.Equals(trusted, comparison)
               || candidate.StartsWith(trusted + Path.DirectorySeparatorChar, comparison)
               || candidate.StartsWith(trusted + Path.AltDirectorySeparatorChar, comparison);
    }

    private static string Trim(string path) =>
        path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}
