namespace Forge.CodeMode;

/// <summary>
/// Limits and defaults for <see cref="CodeModeTool"/>. Instances are immutable; invalid values are rejected
/// when a tool is constructed rather than silently clamped.
/// </summary>
public sealed record CodeModeOptions
{
    public static CodeModeOptions Default { get; } = new();

    public TimeSpan DefaultTimeout { get; init; } = TimeSpan.FromSeconds(60);

    public TimeSpan MaxTimeout { get; init; } = TimeSpan.FromSeconds(600);

    public int MaxConcurrentToolCalls { get; init; } = 8;

    public int MaxScriptChars { get; init; } = 100_000;

    public int MaxResultChars { get; init; } = 40_000;

    public int MaxInnerResultChars { get; init; } = 20_000;

    public int MaxLogChars { get; init; } = 4_000;

    public int MaxDiagnosticChars { get; init; } = 4_000;

    public int MaxDiagnosticCount { get; init; } = 12;

    public int CompilationCacheCapacity { get; init; } = 64;

    /// <summary>Namespaces imported into every script on top of the default surface.</summary>
    public IReadOnlyList<string> AdditionalImports { get; init; } = [];

    /// <summary>Platform assemblies a script may reference on top of the default surface.</summary>
    public IReadOnlyList<string> AdditionalAssemblyNames { get; init; } = [];

    internal CodeModeOptions Validated()
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(DefaultTimeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(MaxTimeout, TimeSpan.Zero);
        if (MaxTimeout < DefaultTimeout)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxTimeout), MaxTimeout, "MaxTimeout must not be shorter than DefaultTimeout.");
        }

        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(MaxConcurrentToolCalls, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(MaxScriptChars, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(MaxResultChars, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(MaxInnerResultChars, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(MaxLogChars, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(MaxDiagnosticChars, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(MaxDiagnosticCount, 0);
        ArgumentOutOfRangeException.ThrowIfLessThan(CompilationCacheCapacity, 0);
        return this;
    }

    internal TimeSpan ClampTimeout(TimeSpan requested) =>
        requested <= TimeSpan.Zero ? DefaultTimeout : requested > MaxTimeout ? MaxTimeout : requested;
}
