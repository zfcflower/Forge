using System.Security.Cryptography;
using System.Text;

namespace Forge.Approval;

/// <summary>One plugin dependency as it enters the dependency hash (a NuGet id plus its exact version).</summary>
public sealed record Dependency(string Name, string Version);

/// <summary>
/// Stable SHA-256 helpers for the approval engine. Source text is normalized (CRLF and lone CR become LF, one
/// trailing newline is dropped) so an editor or OS that rewrites line endings does not invalidate an approval.
/// Every result is lowercase hex.
/// </summary>
public static class Hashing
{
    /// <summary>Hash of plugin source after <see cref="NormalizeSource"/>.</summary>
    public static string SourceHash(string source) => Sha256Hex(NormalizeSource(source));

    /// <summary>
    /// Canonical dependency hash: entries are ordered by name then version (ordinal), duplicate entries collapse,
    /// and each entry is written as <c>name@version</c> separated by a newline.
    /// </summary>
    public static string DependencyHash(IEnumerable<Dependency> dependencies)
    {
        ArgumentNullException.ThrowIfNull(dependencies);

        var entries = new List<Dependency>();
        foreach (var dependency in dependencies)
        {
            ArgumentNullException.ThrowIfNull(dependency);
            entries.Add(dependency);
        }

        entries.Sort(CompareDependencies);

        var builder = new StringBuilder();
        Dependency? previous = null;
        foreach (var entry in entries)
        {
            if (previous is not null &&
                string.Equals(previous.Name, entry.Name, StringComparison.Ordinal) &&
                string.Equals(previous.Version, entry.Version, StringComparison.Ordinal))
            {
                continue;
            }

            if (builder.Length != 0)
            {
                builder.Append('\n');
            }

            builder.Append(entry.Name).Append('@').Append(entry.Version);
            previous = entry;
        }

        return Sha256Hex(builder.ToString());
    }

    /// <summary>The single hash an approval covers: source, dependencies and the SDK version they compile against.</summary>
    public static string ApprovalFingerprint(string sourceHash, string dependencyHash, string sdkVersion)
    {
        ArgumentNullException.ThrowIfNull(sourceHash);
        ArgumentNullException.ThrowIfNull(dependencyHash);
        ArgumentNullException.ThrowIfNull(sdkVersion);
        return Sha256Hex(string.Concat(sourceHash, "\n", dependencyHash, "\n", sdkVersion));
    }

    /// <summary>Line-ending and trailing-newline normalization applied before hashing source text.</summary>
    public static string NormalizeSource(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var normalized = source.Contains('\r')
            ? source.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n')
            : source;
        return normalized.EndsWith('\n') ? normalized[..^1] : normalized;
    }

    private static int CompareDependencies(Dependency left, Dependency right)
    {
        var byName = string.CompareOrdinal(left.Name, right.Name);
        return byName != 0 ? byName : string.CompareOrdinal(left.Version, right.Version);
    }

    private static string Sha256Hex(string text) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
