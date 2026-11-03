using System.Text;

namespace Forge.Scripting;

public enum ScriptOptimizationLevel
{
    Debug = 0,
    Release = 1,
}

/// <summary>Everything that determines a compiled plugin assembly. Two requests with the same source, usings,
/// references, contract version and compilation settings produce the same <see cref="CacheKey"/>.</summary>
public sealed record CompilationRequest
{
    public required string Source { get; init; }

    public required string AssemblyName { get; init; }

    /// <summary>Additional references: an absolute path to an assembly, or a simple name resolved from the
    /// shared framework (for example <c>System.Text.Json</c>). The BCL surface and <c>Forge.Plugin.Sdk</c>
    /// are always referenced.</summary>
    public IReadOnlyList<string> References { get; init; } = [];

    /// <summary>Replaces the default import list when set; <c>null</c> keeps the compiler defaults.</summary>
    public IReadOnlyList<string>? Usings { get; init; }

    public ScriptOptimizationLevel OptimizationLevel { get; init; } = ScriptOptimizationLevel.Release;

    /// <summary>SDK/contract version the source is compiled against. <c>null</c> or empty means the version of
    /// the <c>Forge.Plugin.Sdk</c> assembly the compiler was built with.</summary>
    public string? ContractVersion { get; init; }

    /// <summary>Hash of the normalized <see cref="Source"/>. Always computed here; a caller can never supply or
    /// replace it.</summary>
    public string SourceHash => RequestIdentity.SourceHash(Source);

    /// <summary>Optional expected hash of the source (for example the approved hash from a manifest). It is only
    /// validated against the computed <see cref="SourceHash"/>: a mismatch fails the compilation with
    /// <see cref="ScriptDiagnosticCodes.SourceHashMismatch"/> and never replaces the computed value.</summary>
    public string? ExpectedSourceHash { get; init; }

    public string FilePath { get; init; } = ScriptCompiler.DefaultFilePath;

    public string ResolvedContractVersion =>
        string.IsNullOrWhiteSpace(ContractVersion) ? ScriptCompiler.ContractVersion : ContractVersion;

    public string ResolvedSourceHash => SourceHash;

    public string CacheKey => RequestIdentity.CacheKey(this);
}

/// <summary>Normalizes request inputs and derives the two hashes the compiler publishes.</summary>
internal static class RequestIdentity
{
    private const string FormatVersion = "forge-scripting/v3";

    private const string ResolvedReferencePrefix = "reference:";

    private const string UnresolvedReferencePrefix = "unresolved:";

    public static string SourceHash(string? source) => Hashing.Sha256Hex(Normalize(source));

    /// <summary>A blank expected hash is the same as no expectation at all; surrounding whitespace is not part of
    /// the hash.</summary>
    public static string? NormalizeExpectedHash(string? expected) =>
        string.IsNullOrWhiteSpace(expected) ? null : expected.Trim();

    /// <summary>True when the caller supplied an expected hash and it differs from the computed one.</summary>
    public static bool SourceHashDiffers(string computed, string? expected) =>
        NormalizeExpectedHash(expected) is { } wanted &&
        !string.Equals(wanted, computed, StringComparison.OrdinalIgnoreCase);

    public static string CacheKey(CompilationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var identities = ReferenceIdentities(request.References);

        var builder = new Hashing.Builder();
        builder.Add(FormatVersion);
        builder.Add(request.AssemblyName ?? string.Empty);
        builder.Add(request.ResolvedContractVersion);
        builder.Add(request.ResolvedSourceHash);
        builder.Add(NormalizeExpectedHash(request.ExpectedSourceHash) is { } expected
            ? "expected:" + expected
            : "expected:absent");
        builder.Add(((int)request.OptimizationLevel).ToString(System.Globalization.CultureInfo.InvariantCulture));
        builder.Add(request.FilePath ?? string.Empty);

        var usings = request.Usings;
        builder.Add(usings is null ? "usings:default" : "usings:explicit");
        builder.Add(Count(usings?.Count ?? 0));
        if (usings is not null)
        {
            foreach (var import in usings)
            {
                builder.Add(import ?? string.Empty);
            }
        }

        builder.Add(Count(identities.Count));
        foreach (var identity in identities)
        {
            builder.Add(identity);
        }

        return builder.Finish();
    }

    private static List<string> ReferenceIdentities(IReadOnlyList<string> references)
    {
        var identities = new List<string>(references.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var reference in references)
        {
            if (string.IsNullOrWhiteSpace(reference))
            {
                continue;
            }

            var identity = ReferenceResolver.TryResolve(reference, out var path)
                ? ResolvedReferencePrefix + path
                : UnresolvedReferencePrefix + reference;
            if (seen.Add(identity))
            {
                identities.Add(identity);
            }
        }

        identities.Sort(StringComparer.Ordinal);
        return identities;
    }

    private static string Count(int value) => value.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public static string Normalize(string? source)
    {
        if (string.IsNullOrEmpty(source))
        {
            return string.Empty;
        }

        var start = source[0] == '\uFEFF' ? 1 : 0;
        var span = source.AsSpan(start);
        string normalized;
        if (span.IndexOf('\r') < 0)
        {
            normalized = start == 0 ? source : span.ToString();
        }
        else
        {
            var builder = new StringBuilder(source.Length);
            for (var i = start; i < source.Length; i++)
            {
                var c = source[i];
                if (c == '\r')
                {
                    builder.Append('\n');
                    if (i + 1 < source.Length && source[i + 1] == '\n')
                    {
                        i++;
                    }
                }
                else
                {
                    builder.Append(c);
                }
            }

            normalized = builder.ToString();
        }

        return normalized.Length > 0 && normalized[^1] == '\n' ? normalized[..^1] : normalized;
    }
}

/// <summary>SHA-256 helpers over UTF-16 strings and length-prefixed field sequences, deterministic and allocation-light.</summary>
internal static class Hashing
{
    public static string Sha256Hex(string text)
    {
        var max = System.Text.Encoding.UTF8.GetMaxByteCount(text.Length);
        var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(max);
        try
        {
            var written = System.Text.Encoding.UTF8.GetBytes(text.AsSpan(), buffer);
            return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(buffer.AsSpan(0, written)));
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>Length-prefixes each field so <c>["ab","c"]</c> and <c>["a","bc"]</c> hash differently.</summary>
    public sealed class Builder
    {
        private readonly System.Security.Cryptography.IncrementalHash hash =
            System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);

        private byte[] buffer = new byte[256];

        public void Add(string field)
        {
            var required = System.Text.Encoding.UTF8.GetMaxByteCount(field.Length) + 4;
            if (buffer.Length < required)
            {
                Array.Resize(ref buffer, required);
            }

            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(buffer, field.Length);
            var written = System.Text.Encoding.UTF8.GetBytes(field.AsSpan(), buffer.AsSpan(4));
            hash.AppendData(buffer, 0, written + 4);
        }

        public string Finish()
        {
            try
            {
                return Convert.ToHexStringLower(hash.GetHashAndReset());
            }
            finally
            {
                hash.Dispose();
            }
        }
    }
}
