using System.Security.Cryptography;
using System.Text;

namespace Forge.Mcp;

/// <summary>
/// Maps an MCP server tool onto a provider-safe tool name: <c>&lt;server&gt;__&lt;tool&gt;</c> restricted to
/// <c>[a-zA-Z0-9_-]</c>, at most 64 characters, with a hash suffix whenever truncation or a collision would
/// otherwise make two tools indistinguishable.
/// </summary>
public static class McpToolNaming
{
    public const int MaxLength = 64;
    public const string Separator = "__";

    public static string Sanitize(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var builder = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            builder.Append(char.IsAsciiLetterOrDigit(ch) || ch is '_' or '-' ? ch : '_');
        }

        return builder.Length == 0 ? "_" : builder.ToString();
    }

    public static string Build(string serverName, string toolName)
    {
        ArgumentNullException.ThrowIfNull(serverName);
        ArgumentNullException.ThrowIfNull(toolName);
        var server = Sanitize(serverName);
        var tool = Sanitize(toolName);
        var full = server + Separator + tool;
        if (full.Length <= MaxLength)
        {
            return full;
        }

        return AppendHash(full, server + Separator + toolName);
    }

    /// <summary>True when <see cref="Build"/> preserves both names verbatim, so no sanitization collapsed them.</summary>
    public static bool IsLossless(string serverName, string toolName)
    {
        ArgumentNullException.ThrowIfNull(serverName);
        ArgumentNullException.ThrowIfNull(toolName);
        return string.Equals(Sanitize(serverName), serverName, StringComparison.Ordinal) &&
               string.Equals(Sanitize(toolName), toolName, StringComparison.Ordinal);
    }

    public static string Disambiguate(string name, string uniqueKey) => AppendHash(name, uniqueKey);

    private static string AppendHash(string name, string uniqueKey)
    {
        var suffix = "_" + Hash8(uniqueKey);
        var budget = MaxLength - suffix.Length;
        return name.Length <= budget ? name + suffix : name[..budget] + suffix;
    }

    private static string Hash8(string value)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(value), hash);
        return Convert.ToHexStringLower(hash[..4]);
    }
}
