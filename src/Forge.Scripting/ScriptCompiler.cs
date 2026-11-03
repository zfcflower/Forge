using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using Forge.Plugin.Sdk;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.Text;

namespace Forge.Scripting;

public sealed record ScriptCompilerOptions
{
    public static ScriptCompilerOptions Default { get; } = new();

    public int CacheCapacity { get; init; } = 64;

    public int MaxDiagnosticCount { get; init; } = 16;

    public int MaxDiagnosticChars { get; init; } = 4_000;

    /// <summary>Replaces the built-in BCL reference set when set. <c>Forge.Plugin.Sdk</c> is always referenced.</summary>
    public IReadOnlyList<string>? ReferenceNames { get; init; }

    /// <summary>Default import list. A <see cref="CompilationRequest.Usings"/> value replaces it per request.</summary>
    public IReadOnlyList<string>? Usings { get; init; }

    internal ScriptCompilerOptions Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(CacheCapacity, 0);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxDiagnosticCount, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxDiagnosticChars, 1);
        return this;
    }
}

/// <summary>
/// Compiles C# plugin sources against the BCL and <c>Forge.Plugin.Sdk</c> into in-memory (or stream-backed) PE
/// images with embedded portable PDBs. Compilation is deterministic: the same request always yields the same
/// <see cref="CompilationResult.CacheKey"/> and byte-identical images.
/// </summary>
public sealed class ScriptCompiler
{
    public const string DefaultFilePath = "plugin.cs";

    private const string GeneratedUsingsPath = "forge.globalusings.g.cs";

    private const string RoslynInvalidAssemblyName = "CS8203";

    private static readonly string[] BuiltInReferenceNames =
    [
        "System.Private.CoreLib",
        "System.Runtime",
        "System.Collections",
        "System.Collections.Concurrent",
        "System.Console",
        "System.IO.FileSystem",
        "System.Linq",
        "System.Memory",
        "System.Numerics.Vectors",
        "System.Runtime.Numerics",
        "System.Text.Json",
        "System.Text.Encodings.Web",
        "System.Text.RegularExpressions",
        "System.Threading",
        "System.Threading.Tasks",
    ];

    private static readonly string[] BuiltInUsings =
    [
        "System",
        "System.Collections.Generic",
        "System.IO",
        "System.Linq",
        "System.Text",
        "System.Text.Json",
        "System.Threading",
        "System.Threading.Tasks",
        "Forge.Plugin.Sdk",
    ];

    public static string ContractVersion { get; } = ResolveContractVersion();

    private readonly ScriptCompilerOptions options;
    private readonly string[] defaultReferencePaths;
    private readonly ConcurrentDictionary<string, Task<CompilationResult>> inFlight =
        new(StringComparer.Ordinal);

    public ScriptCompiler(ScriptCompilerOptions? options = null)
        : this(new CompilationCache((options ?? ScriptCompilerOptions.Default).CacheCapacity), options)
    {
    }

    public ScriptCompiler(CompilationCache cache, ScriptCompilerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(cache);
        this.options = (options ?? ScriptCompilerOptions.Default).Validate();
        Cache = cache;
        defaultReferencePaths = BuildDefaultReferencePaths(this.options);
    }

    public CompilationCache Cache { get; }

    public ScriptCompilerOptions Options => options;

    /// <summary>Compiles through the cache; a warm hit returns the stored result without touching Roslyn.</summary>
    public CompilationResult Compile(CompilationRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ct.ThrowIfCancellationRequested();
        var key = request.CacheKey;
        if (Cache.TryGet(key, out var cached))
        {
            return cached;
        }

        var result = CompileDirect(request, ct);
        Cache.Set(key, result);
        return result;
    }

    /// <summary>Compiles through the cache on the thread pool; concurrent calls for one cache key share one compile.</summary>
    public async Task<CompilationResult> CompileAsync(CompilationRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var key = request.CacheKey;
        if (Cache.TryGet(key, out var cached))
        {
            return cached;
        }

        while (true)
        {
            if (inFlight.TryGetValue(key, out var shared))
            {
                return await shared.WaitAsync(ct).ConfigureAwait(false);
            }

            var completion = new TaskCompletionSource<CompilationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!inFlight.TryAdd(key, completion.Task))
            {
                continue;
            }

            try
            {
                var result = await Task.Run(() => CompileDirect(request, ct), ct).ConfigureAwait(false);
                Cache.Set(key, result);
                completion.SetResult(result);
                return result;
            }
            catch (OperationCanceledException ex)
            {
                completion.SetCanceled(ex.CancellationToken);
                throw;
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
                throw;
            }
            finally
            {
                inFlight.TryRemove(key, out _);
            }
        }
    }

    /// <summary>Compiles without consulting or filling the cache.</summary>
    public CompilationResult CompileDirect(CompilationRequest request, CancellationToken ct = default) =>
        CompileCore(request, destination: null, ct);

    /// <summary>Compiles without the cache and copies the image into <paramref name="destination"/> only on success.</summary>
    public CompilationResult CompileToStream(CompilationRequest request, Stream destination, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        return CompileCore(request, destination, ct);
    }

    public Task<CompilationResult> CompileToStreamAsync(CompilationRequest request, Stream destination, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        return Task.Run(() => CompileCore(request, destination, ct), ct);
    }

    private CompilationResult CompileCore(CompilationRequest request, Stream? destination, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.AssemblyName))
        {
            throw new ArgumentException("AssemblyName is required.", nameof(request));
        }

        ct.ThrowIfCancellationRequested();
        var startedAt = Stopwatch.GetTimestamp();
        var cacheKey = request.CacheKey;
        var sourceHash = request.ResolvedSourceHash;

        if (RequestIdentity.SourceHashDiffers(sourceHash, request.ExpectedSourceHash))
        {
            List<ScriptDiagnostic> mismatch =
            [
                new ScriptDiagnostic(
                    ScriptDiagnosticSeverity.Error,
                    ScriptDiagnosticCodes.SourceHashMismatch,
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"Expected source hash '{RequestIdentity.NormalizeExpectedHash(request.ExpectedSourceHash)}' does not match the source hash '{sourceHash}' of the supplied source."),
                    0,
                    0),
            ];
            return Finish(request, cacheKey, sourceHash, [], null, mismatch, startedAt);
        }

        if (!IsValidAssemblyName(request.AssemblyName))
        {
            List<ScriptDiagnostic> invalidName =
            [
                new ScriptDiagnostic(
                    ScriptDiagnosticSeverity.Error,
                    ScriptDiagnosticCodes.InvalidAssemblyName,
                    $"Assembly name '{request.AssemblyName}' is not valid.",
                    1,
                    1),
            ];
            return Finish(request, cacheKey, sourceHash, [], null, invalidName, startedAt);
        }

        var references = new List<MetadataReference>(defaultReferencePaths.Length + request.References.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        List<ScriptDiagnostic>? problems = null;

        foreach (var path in defaultReferencePaths)
        {
            if (seen.Add(path))
            {
                references.Add(ReferenceResolver.Get(path));
            }
        }

        foreach (var reference in request.References)
        {
            if (string.IsNullOrWhiteSpace(reference))
            {
                continue;
            }

            if (!ReferenceResolver.TryResolve(reference, out var path))
            {
                (problems ??= []).Add(new ScriptDiagnostic(
                    ScriptDiagnosticSeverity.Error,
                    ScriptDiagnosticCodes.MissingReference,
                    $"Assembly reference '{reference}' could not be resolved.",
                    0,
                    0));
                continue;
            }

            if (seen.Add(path))
            {
                references.Add(ReferenceResolver.Get(path));
            }
        }

        if (problems is not null)
        {
            return Finish(request, cacheKey, sourceHash, [], null, problems, startedAt);
        }

        var parseOptions = new CSharpParseOptions(LanguageVersion.Latest, DocumentationMode.None, SourceCodeKind.Regular);
        var tree = CSharpSyntaxTree.ParseText(
            SourceText.From(request.Source ?? string.Empty, System.Text.Encoding.UTF8),
            parseOptions,
            request.FilePath,
            ct);

        CSharpCompilation compilation;
        try
        {
            compilation = CSharpCompilation.Create(
                request.AssemblyName,
                [tree],
                references,
                CreateCompilationOptions(request));
        }
        catch (ArgumentException ex)
        {
            problems =
            [
                new ScriptDiagnostic(
                    ScriptDiagnosticSeverity.Error,
                    ScriptDiagnosticCodes.InvalidAssemblyName,
                    $"Assembly name '{request.AssemblyName}' is not valid: {ex.Message}",
                    0,
                    0),
            ];
            return Finish(request, cacheKey, sourceHash, [], null, problems, startedAt);
        }

        var requestedUsings = request.Usings ?? options.Usings ?? BuiltInUsings;
        var globalUsings = BuildGlobalUsings(requestedUsings, compilation, ref problems);
        if (globalUsings is not null)
        {
            compilation = compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText(
                SourceText.From(globalUsings, System.Text.Encoding.UTF8),
                parseOptions,
                GeneratedUsingsPath,
                ct));
        }

        using var image = new MemoryStream(capacity: 64 * 1024);
        var emitResult = compilation.Emit(
            image,
            options: new EmitOptions(debugInformationFormat: DebugInformationFormat.Embedded),
            cancellationToken: ct);

        var bytes = emitResult.Success ? image.ToArray() : null;
        var result = Finish(request, cacheKey, sourceHash, emitResult.Diagnostics, bytes, problems, startedAt);
        if (result.Success && destination is not null && result.AssemblyBytes is { } assemblyBytes)
        {
            destination.Write(assemblyBytes, 0, assemblyBytes.Length);
            destination.Flush();
        }

        return result;
    }

    private static CSharpCompilationOptions CreateCompilationOptions(CompilationRequest request) =>
        new(
            OutputKind.DynamicallyLinkedLibrary,
            optimizationLevel: request.OptimizationLevel == ScriptOptimizationLevel.Debug
                ? OptimizationLevel.Debug
                : OptimizationLevel.Release,
            allowUnsafe: false,
            deterministic: true,
            concurrentBuild: false,
            nullableContextOptions: NullableContextOptions.Disable,
            generalDiagnosticOption: ReportDiagnostic.Default,
            warningLevel: 4);

    private static string? BuildGlobalUsings(
        IReadOnlyList<string> requested,
        CSharpCompilation compilation,
        ref List<ScriptDiagnostic>? problems)
    {
        if (requested.Count == 0)
        {
            return null;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var builder = new System.Text.StringBuilder(requested.Count * 32);
        foreach (var import in requested)
        {
            if (string.IsNullOrWhiteSpace(import) || !seen.Add(import))
            {
                continue;
            }

            if (!NamespaceExists(compilation.GlobalNamespace, import))
            {
                (problems ??= []).Add(new ScriptDiagnostic(
                    ScriptDiagnosticSeverity.Error,
                    ScriptDiagnosticCodes.UnknownUsing,
                    $"Using directive '{import}' does not resolve to a namespace.",
                    0,
                    0));
                continue;
            }

            builder.Append("global using ").Append(import).Append(";\n");
        }

        return builder.Length == 0 ? null : builder.ToString();
    }

    private static bool NamespaceExists(INamespaceSymbol global, string name)
    {
        var current = global;
        var start = 0;
        while (start < name.Length)
        {
            var end = name.IndexOf('.', start);
            if (end < 0)
            {
                end = name.Length;
            }

            INamespaceSymbol? next = null;
            foreach (var member in current.GetMembers(name[start..end]))
            {
                if (member is INamespaceSymbol ns)
                {
                    next = ns;
                    break;
                }
            }

            if (next is null)
            {
                return false;
            }

            current = next;
            start = end + 1;
        }

        return true;
    }

    private CompilationResult Finish(
        CompilationRequest request,
        string cacheKey,
        string sourceHash,
        IReadOnlyList<Diagnostic> diagnostics,
        byte[]? bytes,
        List<ScriptDiagnostic>? extra,
        long startedAt)
    {
        var mapped = new List<ScriptDiagnostic>(diagnostics.Count + (extra?.Count ?? 0));
        foreach (var diagnostic in diagnostics)
        {
            if (diagnostic.Severity is not (DiagnosticSeverity.Error or DiagnosticSeverity.Warning))
            {
                continue;
            }

            var span = diagnostic.Location.IsInSource
                ? diagnostic.Location.GetLineSpan()
                : default(FileLinePositionSpan);
            mapped.Add(new ScriptDiagnostic(
                diagnostic.Severity == DiagnosticSeverity.Error ? ScriptDiagnosticSeverity.Error : ScriptDiagnosticSeverity.Warning,
                diagnostic.Id == RoslynInvalidAssemblyName ? ScriptDiagnosticCodes.InvalidAssemblyName : diagnostic.Id,
                diagnostic.GetMessage(CultureInfo.InvariantCulture).Trim(),
                span.StartLinePosition.Line + 1,
                span.StartLinePosition.Character + 1,
                diagnostic.Location.IsInSource ? span.Path : null));
        }

        if (extra is not null)
        {
            mapped.AddRange(extra);
        }

        mapped.Sort(CompareDiagnostics);

        var errorCount = 0;
        var warningCount = 0;
        for (var i = 0; i < mapped.Count; i++)
        {
            switch (mapped[i].Severity)
            {
                case ScriptDiagnosticSeverity.Error:
                    errorCount++;
                    break;
                case ScriptDiagnosticSeverity.Warning:
                    warningCount++;
                    break;
            }
        }

        var success = bytes is not null && errorCount == 0;
        var bounded = mapped;
        if (mapped.Count > options.MaxDiagnosticCount)
        {
            bounded = mapped.GetRange(0, options.MaxDiagnosticCount);
        }

        var errorText = success
            ? null
            : DiagnosticText.Format(
                string.Create(CultureInfo.InvariantCulture, $"The plugin source does not compile ({errorCount} error(s)):"),
                bounded,
                options.MaxDiagnosticCount,
                options.MaxDiagnosticChars,
                mapped.Count);

        return new CompilationResult
        {
            Success = success,
            AssemblyBytes = success ? bytes : null,
            Diagnostics = bounded.AsReadOnly(),
            TotalDiagnosticCount = mapped.Count,
            ErrorCount = errorCount,
            WarningCount = warningCount,
            SourceHash = sourceHash,
            CacheKey = cacheKey,
            AssemblyName = request.AssemblyName,
            Duration = Stopwatch.GetElapsedTime(startedAt),
            ErrorText = errorText,
        };
    }

    private static int CompareDiagnostics(ScriptDiagnostic left, ScriptDiagnostic right)
    {
        var compared = right.Severity.CompareTo(left.Severity);
        if (compared != 0)
        {
            return compared;
        }

        compared = left.Line.CompareTo(right.Line);
        if (compared != 0)
        {
            return compared;
        }

        compared = left.Column.CompareTo(right.Column);
        if (compared != 0)
        {
            return compared;
        }

        compared = string.CompareOrdinal(left.Code, right.Code);
        return compared != 0 ? compared : string.CompareOrdinal(left.Message, right.Message);
    }

    private static bool IsValidAssemblyName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        foreach (var c in name)
        {
            if (c is '\0' or ':' or '/' or '\\')
            {
                return false;
            }
        }

        return true;
    }

    private static string[] BuildDefaultReferencePaths(ScriptCompilerOptions options)
    {
        var names = options.ReferenceNames ?? BuiltInReferenceNames;
        var paths = new List<string>(names.Count + 1);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in names)
        {
            if (ReferenceResolver.TryResolve(name, out var path) && seen.Add(path))
            {
                paths.Add(path);
            }
        }

        if (ReferenceResolver.ForAssembly(typeof(IPlugin).Assembly) is { } sdk &&
            ReferenceResolver.TryResolve(sdk.Display, out var sdkPath) &&
            seen.Add(sdkPath))
        {
            paths.Add(sdkPath);
        }

        return [.. paths];
    }

    private static string ResolveContractVersion()
    {
        var version = typeof(IPlugin).Assembly.GetName().Version;
        return version is null ? "0.0.0" : version.ToString();
    }
}
