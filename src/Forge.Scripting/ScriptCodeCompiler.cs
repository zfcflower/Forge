using System.Globalization;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using System.Text.Json.Serialization;
using Forge.Plugin.Sdk;
using Forge.ScriptRuntime;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.Text;

namespace Forge.Scripting;

/// <summary>One compile attempt: the loadable image and its descriptor on success, a bounded error text on failure.</summary>
public sealed record ScriptCompilation(
    bool Ok,
    string CacheKey,
    string SourceHash,
    byte[]? Image,
    ScriptArtifactDescriptor? Descriptor,
    string? Error)
{
    /// <summary>The image is shared with the cache and must not be mutated.</summary>
    [JsonIgnore]
    public int ImageLength => Image?.Length ?? 0;
}

/// <summary>
/// The script compile service: it turns run_code source into one loadable PE image (embedded PDB, deterministic) whose
/// entry point is <c>Task&lt;object?&gt; (ScriptGlobals, CancellationToken)</c>, together with the
/// <see cref="ScriptArtifactDescriptor"/> an executor re-validates before loading.
/// <para>
/// One instance owns one bounded compilation cache. The host holds a single instance, so the cache has exactly one
/// owner; the key covers the compile-surface fingerprint (references, imports, compiler settings and the identity of
/// every assembly the script binds to), the source and every execution option that shapes the bounded error text.
/// Failed compilations are cached too, so a malformed script does not re-enter Roslyn.
/// </para>
/// <para>
/// Nothing here runs a script: this module compiles, and it resolves no tool and evaluates no approval.
/// </para>
/// </summary>
public sealed class ScriptCodeCompiler
{
    private const string CompileHeadline = "The script does not compile";

    private const string ViolationHeadline =
        "The script touches APIs run_code does not allow (no file, network, process, reflection, environment, threading or unsafe access):";

    private static readonly CSharpParseOptions RegularParseOptions =
        new(LanguageVersion.Latest, DocumentationMode.None, SourceCodeKind.Regular);

    private static readonly CSharpParseOptions ScriptParseOptions =
        new(LanguageVersion.Latest, DocumentationMode.None, SourceCodeKind.Script);

    private static readonly CSharpCompilationOptions CompilationOptions = new(
        OutputKind.DynamicallyLinkedLibrary,
        optimizationLevel: OptimizationLevel.Release,
        allowUnsafe: false,
        deterministic: true,
        concurrentBuild: false,
        nullableContextOptions: NullableContextOptions.Disable,
        generalDiagnosticOption: ReportDiagnostic.Default,
        warningLevel: 4);

    private static readonly EmitOptions EmitOptions = new(debugInformationFormat: DebugInformationFormat.Embedded);

    private readonly ScriptCompilationCache cache = new();

    /// <summary>Compiles through the cache. A malformed script returns a bounded error instead of throwing.</summary>
    public async Task<ScriptCompilation> CompileAsync(string code, CodeExecutionOptions options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(code);
        ArgumentNullException.ThrowIfNull(options);

        var key = CacheKey(code, options);
        if (cache.TryGet(key, out var cached))
        {
            return cached.Compilation;
        }

        var compiled = await Task.Run(() => Compile(code, options, key, ct), ct).ConfigureAwait(false);
        cache.Set(key, compiled, options.CompilationCacheCapacity);
        return compiled.Compilation;
    }

    /// <summary>Drops every cached image; the next call compiles again.</summary>
    public void Clear() => cache.Clear();

    /// <summary>
    /// Every input that can change a compiled script or its cached error text, length-prefixed so neighbouring fields
    /// cannot collide. Tool metadata is deliberately absent: it never reaches the compiler (scripts call tools by name at
    /// run time), and caching it here would carry one request's capabilities into the next.
    /// </summary>
    public static string CacheKey(string code, CodeExecutionOptions options)
    {
        ArgumentNullException.ThrowIfNull(code);
        ArgumentNullException.ThrowIfNull(options);

        var builder = new Hashing.Builder();
        builder.Add(ScriptSurface.Fingerprint);
        builder.Add(code);
        builder.Add(options.MaxScriptChars.ToString(CultureInfo.InvariantCulture));
        builder.Add(options.MaxConcurrentToolCalls.ToString(CultureInfo.InvariantCulture));
        builder.Add(options.MaxResultChars.ToString(CultureInfo.InvariantCulture));
        builder.Add(options.MaxInnerResultChars.ToString(CultureInfo.InvariantCulture));
        builder.Add(options.MaxLogChars.ToString(CultureInfo.InvariantCulture));
        builder.Add(options.MaxDiagnosticChars.ToString(CultureInfo.InvariantCulture));
        builder.Add(options.MaxDiagnosticCount.ToString(CultureInfo.InvariantCulture));
        builder.Add(options.CompilationCacheCapacity.ToString(CultureInfo.InvariantCulture));
        return builder.Finish();
    }

    private static CompiledScript Compile(string code, CodeExecutionOptions options, string cacheKey, CancellationToken ct)
    {
        var sourceHash = RequestIdentity.SourceHash(code);

        // The user code is parsed on its own first so the ban list runs before any compilation, exactly as before: #r,
        // #load, unsafe and typeof are refused without the compiler ever being asked to resolve a directive.
        var scanTree = CSharpSyntaxTree.ParseText(
            SourceText.From(code, Encoding.UTF8),
            ScriptParseOptions,
            ScriptSourceBuilder.SourceFile,
            ct);
        var banned = ScriptSecurityAnalyzer.ScanSyntax(scanTree, options.MaxDiagnosticCount, ct);
        if (banned.Count > 0)
        {
            return Failed(cacheKey, sourceHash, ApiViolationText.Format(ViolationHeadline, banned, options.MaxDiagnosticChars));
        }

        var assemblyName = "ForgeScript" + cacheKey[..16];
        var scriptRoot = scanTree.GetRoot(ct);
        var source = ScriptSourceBuilder.Build(code, scriptRoot);
        var tree = ParseWrapper(source, ct);
        var compilation = CSharpCompilation.Create(assemblyName, [tree], ScriptSurface.References, CompilationOptions);

        // A trailing statement expression is compiled once as a statement to bind it, and only a non-void type makes it
        // the script's result; the rewrite then needs a second compilation because the statement becomes a return.
        if (ScriptSourceBuilder.HasTrailingStatementExpression(scriptRoot) &&
            ScriptSourceBuilder.TrailingStatementReturnsValue(compilation.GetSemanticModel(tree), tree.GetRoot(ct)))
        {
            source = ScriptSourceBuilder.Build(code, scriptRoot, returnStatementExpression: true);
            tree = ParseWrapper(source, ct);
            compilation = CSharpCompilation.Create(assemblyName, [tree], ScriptSurface.References, CompilationOptions);
        }

        using var image = new MemoryStream(capacity: 64 * 1024);
        var emit = compilation.Emit(image, options: EmitOptions, cancellationToken: ct);

        var errors = new List<ScriptDiagnostic>(4);
        var total = 0;
        foreach (var diagnostic in emit.Diagnostics)
        {
            ct.ThrowIfCancellationRequested();
            if (diagnostic.Severity != DiagnosticSeverity.Error)
            {
                continue;
            }

            total++;
            if (errors.Count < options.MaxDiagnosticCount)
            {
                errors.Add(ToScriptDiagnostic(diagnostic));
            }
        }

        if (total > 0 || !emit.Success)
        {
            return Failed(
                cacheKey,
                sourceHash,
                DiagnosticText.Format(
                    string.Create(CultureInfo.InvariantCulture, $"{CompileHeadline} ({total} error(s)):"),
                    errors,
                    options.MaxDiagnosticCount,
                    options.MaxDiagnosticChars,
                    total));
        }

        var model = compilation.GetSemanticModel(tree);
        var violations = ScriptSecurityAnalyzer.Analyze(model, options.MaxDiagnosticCount, ct);
        if (violations.Count > 0)
        {
            return Failed(cacheKey, sourceHash, ApiViolationText.Format(ViolationHeadline, violations, options.MaxDiagnosticChars));
        }

        var bytes = image.ToArray();
        var descriptor = new ScriptArtifactDescriptor(
            assemblyName,
            sourceHash,
            cacheKey,
            ScriptArtifact.ContractVersion,
            ScriptArtifact.RuntimeVersion,
            ScriptArtifact.DefaultEntryPointType,
            ScriptArtifact.DefaultEntryPointMethod,
            ScriptArtifact.GlobalsMvid,
            ScriptArtifact.Sha256(bytes),
            bytes.Length);
        return CompiledScript.Success(new ScriptCompilation(true, cacheKey, sourceHash, bytes, descriptor, null));
    }

    private static CompiledScript Failed(string cacheKey, string sourceHash, string error) =>
        CompiledScript.Failed(new ScriptCompilation(false, cacheKey, sourceHash, null, null, error));

    /// <summary>Parses the generated wrapper as a regular (non-script) compilation unit.</summary>
    private static SyntaxTree ParseWrapper(string source, CancellationToken ct) =>
        CSharpSyntaxTree.ParseText(
            SourceText.From(source, Encoding.UTF8),
            RegularParseOptions,
            ScriptSourceBuilder.SourceFile,
            ct);

    private static ScriptDiagnostic ToScriptDiagnostic(Diagnostic diagnostic)
    {
        var inSource = diagnostic.Location.IsInSource;
        var span = inSource
            ? diagnostic.Location.GetMappedLineSpan().StartLinePosition
            : default;
        return new ScriptDiagnostic(
            ScriptDiagnosticSeverity.Error,
            diagnostic.Id,
            diagnostic.GetMessage(CultureInfo.InvariantCulture).Trim(),
            inSource ? span.Line + 1 : 0,
            inSource ? span.Character + 1 : 0,
            diagnostic.Location.SourceTree?.FilePath);
    }
}

/// <summary>
/// Loads one validated script image from memory into its own <see cref="AssemblyLoadContext"/> and binds the entry point
/// the descriptor names. There is no <c>Assembly.Load</c> fallback and no disk probing: the bytes that were hashed are
/// the bytes that are loaded.
/// </summary>
public static class ScriptCodeLoader
{
    private const BindingFlags EntryPointFlags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    /// <summary>Validates the descriptor against the bytes, loads the image and returns its entry point delegate.</summary>
    public static ScriptEntryPoint Load(byte[] image, ScriptArtifactDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(descriptor);
        ScriptArtifact.Validate(descriptor, image);

        var context = new AssemblyLoadContext("forge-script-" + descriptor.CacheKey[..12], isCollectible: true);
        var assembly = context.LoadFromStream(new MemoryStream(image, writable: false));
        var type = assembly.GetType(descriptor.EntryPointType, throwOnError: false) ??
            throw new ScriptArtifactException(
                $"The script image does not contain the entry point type '{descriptor.EntryPointType}'.");
        var method = type.GetMethod(descriptor.EntryPointMethod, EntryPointFlags) ??
            throw new ScriptArtifactException(
                $"The script image does not contain the entry point '{descriptor.EntryPointType}.{descriptor.EntryPointMethod}'.");
        return method.CreateDelegate<ScriptEntryPoint>();
    }
}
