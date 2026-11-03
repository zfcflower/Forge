namespace Forge.Scripting;

/// <summary>Outcome of one compile. <see cref="AssemblyBytes"/> is a PE image with an embedded portable PDB and is
/// non-null only on success. <see cref="Diagnostics"/> is ordered errors-first, then by position and code.</summary>
public sealed class CompilationResult
{
    public bool Success { get; init; }

    /// <summary>PE image; the same array instance is shared with the cache and must not be mutated.</summary>
    public byte[]? AssemblyBytes { get; init; }

    public IReadOnlyList<ScriptDiagnostic> Diagnostics { get; init; } = [];

    /// <summary>Number of diagnostics before the bounded list was truncated.</summary>
    public int TotalDiagnosticCount { get; init; }

    /// <summary>Hash of the normalized source, always computed by the compiler from the request's source; a
    /// caller-supplied expected hash is validated against it, never copied into it.</summary>
    public string SourceHash { get; init; } = string.Empty;

    /// <summary>Hash of source, assembly name, contract version, compilation settings and reference identities.
    /// This is the cache key and the default output version.</summary>
    public string CacheKey { get; init; } = string.Empty;

    public string AssemblyName { get; init; } = string.Empty;

    public TimeSpan Duration { get; init; }

    /// <summary>Bounded, human-readable error text; <c>null</c> on success.</summary>
    public string? ErrorText { get; init; }

    /// <summary>Number of errors found, including diagnostics dropped by the bounded list.</summary>
    public int ErrorCount { get; init; }

    /// <summary>Number of warnings found, including diagnostics dropped by the bounded list.</summary>
    public int WarningCount { get; init; }
}
