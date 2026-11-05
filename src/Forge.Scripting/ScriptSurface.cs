using System.Globalization;
using System.Text;
using Microsoft.CodeAnalysis;

namespace Forge.Scripting;

/// <summary>
/// The reference and import whitelist a run_code script compiles against. Nothing outside it is nameable, so the
/// compiler itself rejects most of the BCL; <see cref="ScriptSecurityAnalyzer"/> narrows the rest. The set is fixed for
/// the process (there is no per-request extension point), so two scripts always compile against the same surface and the
/// compiled results can be cached across requests.
/// <para>
/// The surface is a plain <see cref="CSharpCompilation"/>: there is no metadata resolver and no source resolver, so
/// <c>#r</c>/<c>#load</c> cannot read a file (and are refused before parsing completes).
/// </para>
/// </summary>
internal static class ScriptSurface
{
    /// <summary>Bump when the whitelist, the imports or the compiler settings change in a way that alters compiled output.</summary>
    private const string SurfaceVersion = "forge-scripting-script/v2";

    public const string FilePath = "script.csx";

    private static readonly string[] DefaultAssemblyNames =
    [
        "System.Private.CoreLib",
        "System.Runtime",
        "System.Collections",
        "System.Collections.Concurrent",
        "System.Memory",
        "System.Linq",
        "System.Numerics.Vectors",
        "System.Runtime.Numerics",
        "System.Text.Json",
        "System.Text.Encodings.Web",
        "System.Text.RegularExpressions",
        "System.Threading",
        "System.Threading.Tasks",
    ];

    private static readonly string[] DefaultImports =
    [
        "System",
        "System.Collections.Generic",
        "System.Globalization",
        "System.Linq",
        "System.Text",
        "System.Text.Json",
        "System.Text.RegularExpressions",
        "System.Threading",
        "System.Threading.Tasks",
        "Forge.Scripting",
        "Forge.Plugin.Sdk",
    ];

    private static readonly Lazy<MetadataReference[]> LazyReferences =
        new(CreateReferences, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Immutability makes one shared instance safe for concurrent compiles.</summary>
    public static IReadOnlyList<MetadataReference> References => LazyReferences.Value;

    public static IReadOnlyList<string> Imports => DefaultImports;

    /// <summary>Identifies the compile surface: same fingerprint means same references, imports and compiler settings.</summary>
    public static string Fingerprint { get; } = ComputeFingerprint();

    private static MetadataReference[] CreateReferences()
    {
        var references = new List<MetadataReference>(DefaultAssemblyNames.Length + 4);
        var platform = ReferenceResolver.PlatformAssemblies;
        foreach (var simpleName in DefaultAssemblyNames)
        {
            if (platform.TryGetValue(simpleName, out var path) && File.Exists(path))
            {
                references.Add(ReferenceResolver.Get(path));
            }
        }

        // The globals surface a script binds to (Forge.ScriptRuntime) and the SDK it calls tools through.
        AddOwnReference(references, typeof(ScriptGlobals).Assembly);
        AddOwnReference(references, typeof(Forge.Plugin.Sdk.ToolDeclaration).Assembly);

        // This assembly stays referenced so the rest of it is nameable and therefore refusable by the semantic walk
        // instead of silently disappearing; the walk bans every type of it except the three script-facing ones, so no
        // compiled script ever binds to it.
        AddOwnReference(references, typeof(ScriptSurface).Assembly);

        return [.. references];
    }

    private static void AddOwnReference(List<MetadataReference> references, System.Reflection.Assembly assembly)
    {
        if (ReferenceResolver.ForAssembly(assembly) is { } reference &&
            !references.Exists(existing => string.Equals(existing.Display, reference.Display, StringComparison.OrdinalIgnoreCase)))
        {
            references.Add(reference);
        }
    }

    private static string ComputeFingerprint()
    {
        var builder = new Hashing.Builder();
        builder.Add(SurfaceVersion);
        builder.Add(FilePath);
        builder.Add(DefaultImports.Length.ToString(CultureInfo.InvariantCulture));
        foreach (var import in DefaultImports)
        {
            builder.Add(import);
        }

        // The identity of the assemblies the script compiles against is part of the surface: a new SDK, runtime or
        // compiler build must not reuse a script compiled against the previous one.
        builder.Add(typeof(ScriptGlobals).Assembly.ManifestModule.ModuleVersionId.ToString("N"));
        builder.Add(typeof(Forge.Plugin.Sdk.ToolDeclaration).Assembly.ManifestModule.ModuleVersionId.ToString("N"));
        builder.Add(typeof(ScriptSurface).Assembly.ManifestModule.ModuleVersionId.ToString("N"));

        var platform = ReferenceResolver.PlatformAssemblies;
        builder.Add(DefaultAssemblyNames.Length.ToString(CultureInfo.InvariantCulture));
        foreach (var name in DefaultAssemblyNames)
        {
            builder.Add(platform.TryGetValue(name, out var path) ? path : name + ":missing");
        }

        return builder.Finish();
    }
}
