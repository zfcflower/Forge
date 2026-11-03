using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.CodeAnalysis;

namespace Forge.Scripting;

/// <summary>Maps reference specifications to assembly paths and caches the <see cref="MetadataReference"/> for each path,
/// so a recompile never re-reads metadata.</summary>
internal static class ReferenceResolver
{
    private static readonly ConcurrentDictionary<string, MetadataReference> MetadataByPath =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly Lazy<IReadOnlyDictionary<string, string>> PlatformBySimpleName =
        new(IndexPlatformAssemblies, LazyThreadSafetyMode.ExecutionAndPublication);

    public static IReadOnlyDictionary<string, string> PlatformAssemblies => PlatformBySimpleName.Value;

    public static bool TryResolve(string? reference, out string path)
    {
        path = string.Empty;
        if (string.IsNullOrWhiteSpace(reference))
        {
            return false;
        }

        if (Path.IsPathRooted(reference) || reference.Contains('/') || reference.Contains('\\'))
        {
            var full = Path.GetFullPath(reference);
            if (!File.Exists(full))
            {
                return false;
            }

            path = full;
            return true;
        }

        return PlatformAssemblies.TryGetValue(reference, out path!);
    }

    public static MetadataReference Get(string path) =>
        MetadataByPath.GetOrAdd(path, static p => MetadataReference.CreateFromFile(p));

    public static MetadataReference? ForAssembly(Assembly assembly)
    {
        var location = assembly.Location;
        return string.IsNullOrEmpty(location) || !File.Exists(location) ? null : Get(Path.GetFullPath(location));
    }

    private static IReadOnlyDictionary<string, string> IndexPlatformAssemblies()
    {
        var index = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") is string trusted)
        {
            var paths = trusted.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
            foreach (var path in paths)
            {
                if (path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                {
                    index.TryAdd(Path.GetFileNameWithoutExtension(path), path);
                }
            }
        }

        return index;
    }
}
