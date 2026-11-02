using System.Collections.Concurrent;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Scripting;

namespace Forge.CodeMode;

/// <summary>
/// The reference and import whitelist a script compiles against. Nothing outside it is nameable, so the
/// compiler itself rejects most of the BCL; <see cref="ScriptSecurityAnalyzer"/> narrows the rest.
/// </summary>
internal static class ScriptSurface
{
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
        "Forge.CodeMode",
    ];

    private static readonly Lazy<IReadOnlyDictionary<string, string>> RuntimeAssemblies = new(IndexRuntimeAssemblies);

    private static readonly ConcurrentDictionary<string, MetadataReference> ReferenceCache =
        new(StringComparer.OrdinalIgnoreCase);

    public static ScriptOptions Create(CodeModeOptions options)
    {
        var references = new List<MetadataReference>(DefaultAssemblyNames.Length + options.AdditionalAssemblyNames.Count + 2);
        AddPlatformReferences(references, DefaultAssemblyNames);
        AddPlatformReferences(references, options.AdditionalAssemblyNames);
        AddOwnReference(references, typeof(ScriptGlobals).Assembly);
        AddOwnReference(references, typeof(Forge.Abstractions.ITool).Assembly);

        var imports = new List<string>(DefaultImports.Length + options.AdditionalImports.Count);
        imports.AddRange(DefaultImports);
        foreach (var import in options.AdditionalImports)
        {
            if (!string.IsNullOrWhiteSpace(import) && !imports.Contains(import, StringComparer.Ordinal))
            {
                imports.Add(import);
            }
        }

        return ScriptOptions.Default
            .WithReferences(references)
            .WithImports(imports)
            .WithAllowUnsafe(false)
            .WithOptimizationLevel(OptimizationLevel.Release)
            .WithEmitDebugInformation(true)
            .WithFilePath("script.csx")
            .WithFileEncoding(Encoding.UTF8)
            .WithMetadataResolver(null!)
            .WithSourceResolver(null!);
    }

    private static void AddPlatformReferences(List<MetadataReference> references, IReadOnlyList<string> simpleNames)
    {
        var index = RuntimeAssemblies.Value;
        foreach (var simpleName in simpleNames)
        {
            if (index.TryGetValue(simpleName, out var path))
            {
                references.Add(ReferenceCache.GetOrAdd(path, static p => MetadataReference.CreateFromFile(p)));
            }
        }
    }

    private static void AddOwnReference(List<MetadataReference> references, System.Reflection.Assembly assembly)
    {
        var location = assembly.Location;
        if (!string.IsNullOrEmpty(location) && File.Exists(location))
        {
            references.Add(ReferenceCache.GetOrAdd(location, static p => MetadataReference.CreateFromFile(p)));
        }
    }

    private static IReadOnlyDictionary<string, string> IndexRuntimeAssemblies()
    {
        var index = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") is not string trusted)
        {
            return index;
        }

        foreach (var path in trusted.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            if (path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            {
                index.TryAdd(Path.GetFileNameWithoutExtension(path), path);
            }
        }

        return index;
    }
}
