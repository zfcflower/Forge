using System.Security.Cryptography;
using System.Text;

namespace Forge.Mcp.Tests.Fixtures;

internal static class Pkce
{
    /// <summary><c>BASE64URL(SHA256(ASCII(verifier)))</c>, the S256 challenge of RFC 7636, for asserting what the client sent.</summary>
    public static string Challenge(string verifier) =>
        Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
}
