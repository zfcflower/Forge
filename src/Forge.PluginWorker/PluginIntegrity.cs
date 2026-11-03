using System.Security.Cryptography;
using Forge.Scripting;

namespace Forge.PluginWorker;

/// <summary>The verified bytes a load may use: the entry image and the managed dependency images, both read and
/// hashed from the same memory so what runs is exactly what was checked.</summary>
internal sealed record VerifiedPluginContent(
    byte[] EntryImage,
    IReadOnlyDictionary<string, byte[]> DependencyImages);

/// <summary>Checks a plugin against the digests <c>worker.load</c> declares before a load context ever receives
/// its bytes: the entry image is read once, hashed, and handed to the context from that same memory, and every
/// declared file must exist and match while every other file of the plugin directory must be declared. A missing
/// or mismatching digest fails the load; nothing is downgraded to a warning. Each declared file is read once and
/// kept, so the managed dependencies the context loads later are the very bytes that were hashed, and a <c>.dll</c>
/// without managed metadata is rejected because such bytes cannot be loaded from memory.</summary>
internal static class PluginIntegrity
{
    private const int ReportedUndeclaredFiles = 5;

    private const string AssemblyExtension = ".dll";

    /// <summary>Verifies the entry assembly and the plugin directory and returns the verified images, so the caller
    /// loads exactly the bytes that were checked. Runs to completion before any plugin code can execute.</summary>
    public static async Task<VerifiedPluginContent> VerifyAsync(
        string assemblyPath,
        string pluginDirectory,
        string? assemblySha256,
        IReadOnlyDictionary<string, string>? files,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assemblyPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginDirectory);

        if (string.IsNullOrWhiteSpace(assemblySha256))
        {
            throw new PluginLoadException(
                "worker.load declares no 'assemblySha256', so the plugin assembly cannot be verified; refusing to load it.");
        }

        if (files is null)
        {
            throw new PluginLoadException(
                "worker.load declares no 'files' map, so the plugin directory cannot be verified; refusing to load the plugin.");
        }

        if (!File.Exists(assemblyPath))
        {
            throw new PluginLoadException($"Plugin assembly not found: {assemblyPath}");
        }

        var image = await File.ReadAllBytesAsync(assemblyPath, ct).ConfigureAwait(false);
        var assemblyDigest = Sha256(image);
        if (!string.Equals(assemblyDigest, assemblySha256.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            throw new PluginLoadException(
                $"Plugin assembly '{assemblyPath}' does not match the declared 'assemblySha256' (computed {assemblyDigest}); refusing to load it.");
        }

        var entryName = Normalize(Path.GetRelativePath(pluginDirectory, assemblyPath));
        var declared = new Dictionary<string, string>(files.Count, StringComparer.OrdinalIgnoreCase);
        var contents = new Dictionary<string, byte[]>(files.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var (name, digest) in files)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new PluginLoadException("worker.load declares a 'files' entry with an empty relative name.");
            }

            var relative = Normalize(name);
            var path = Resolve(pluginDirectory, relative);
            if (!declared.TryAdd(relative, digest ?? ""))
            {
                throw new PluginLoadException($"worker.load declares the plugin file '{relative}' more than once.");
            }

            if (!File.Exists(path))
            {
                throw new PluginLoadException(
                    $"Plugin file '{relative}' is declared in worker.load but missing from '{pluginDirectory}'.");
            }

            var bytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
            var fileDigest = Sha256(bytes);
            if (!string.Equals(fileDigest, (digest ?? "").Trim(), StringComparison.OrdinalIgnoreCase))
            {
                throw new PluginLoadException(
                    $"Plugin file '{relative}' does not match the declared SHA-256 (computed {fileDigest}).");
            }

            contents.Add(relative, bytes);
        }

        RejectUndeclared(pluginDirectory, entryName, declared);
        return new VerifiedPluginContent(image, ManagedDependencyImages(pluginDirectory, entryName, contents));
    }

    /// <summary>Keys the declared managed images by assembly simple name, which is what the load context asks for. A
    /// declared <c>.dll</c> without managed metadata cannot be loaded from memory, so it fails the load by name rather
    /// than being left for the loader to reach on disk.</summary>
    private static Dictionary<string, byte[]> ManagedDependencyImages(
        string pluginDirectory,
        string entryName,
        Dictionary<string, byte[]> contents)
    {
        var images = new Dictionary<string, byte[]>(contents.Count, StringComparer.OrdinalIgnoreCase);
        var origins = new Dictionary<string, string>(contents.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var (relative, bytes) in contents)
        {
            if (string.Equals(relative, entryName, StringComparison.OrdinalIgnoreCase) ||
                !relative.EndsWith(AssemblyExtension, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var assemblyName = ManagedImage.ReadAssemblyName(bytes);
            if (string.IsNullOrEmpty(assemblyName))
            {
                throw new PluginLoadException(
                    $"Plugin file '{relative}' in '{pluginDirectory}' is not a managed assembly; the worker loads verified dependencies only from memory, so native libraries and malformed DLLs cannot be loaded.");
            }

            if (!images.TryAdd(assemblyName, bytes))
            {
                throw new PluginLoadException(
                    $"Plugin files '{origins[assemblyName]}' and '{relative}' both hold the assembly '{assemblyName}', so that dependency cannot be resolved unambiguously.");
            }

            origins.Add(assemblyName, relative);
        }

        return images;
    }

    /// <summary>Rejects a plugin directory holding anything the declaration does not cover: an undeclared file is
    /// content nobody verified, whatever its name. The entry assembly is covered by its own digest.</summary>
    private static void RejectUndeclared(
        string pluginDirectory,
        string entryName,
        Dictionary<string, string> declared)
    {
        List<string>? undeclared = null;
        foreach (var path in EnumerateFiles(pluginDirectory))
        {
            var relative = Normalize(Path.GetRelativePath(pluginDirectory, path));
            if (string.Equals(relative, entryName, StringComparison.OrdinalIgnoreCase) || declared.ContainsKey(relative))
            {
                continue;
            }

            (undeclared ??= []).Add(relative);
        }

        if (undeclared is not { Count: > 0 })
        {
            return;
        }

        undeclared.Sort(StringComparer.Ordinal);
        var named = string.Join(", ", undeclared.Take(ReportedUndeclaredFiles).Select(static name => $"'{name}'"));
        var hidden = undeclared.Count > ReportedUndeclaredFiles
            ? $" and {undeclared.Count - ReportedUndeclaredFiles} more"
            : "";
        throw new PluginLoadException(
            $"The plugin directory '{pluginDirectory}' contains {named}{hidden}, which worker.load does not declare; refusing to load unverified plugin content.");
    }

    private static IEnumerable<string> EnumerateFiles(string pluginDirectory)
    {
        try
        {
            return [.. Directory.EnumerateFiles(pluginDirectory, "*", SearchOption.AllDirectories)];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new PluginLoadException(
                $"The plugin directory '{pluginDirectory}' could not be enumerated: {ex.Message}",
                ex);
        }
    }

    /// <summary>Declared names travel with <c>/</c> separators; the file system may use another one.</summary>
    private static string Normalize(string relativeName) => relativeName.Replace('\\', '/');

    private static string Resolve(string pluginDirectory, string relativeName)
    {
        string path;
        try
        {
            path = Path.GetFullPath(Path.Combine(pluginDirectory, relativeName));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new PluginLoadException(
                $"worker.load declares the plugin file '{relativeName}', which is not a usable relative path: {ex.Message}",
                ex);
        }

        if (Path.IsPathRooted(relativeName) ||
            Path.GetRelativePath(pluginDirectory, path).StartsWith("..", StringComparison.Ordinal))
        {
            throw new PluginLoadException(
                $"worker.load declares the plugin file '{relativeName}', which lies outside the plugin directory '{pluginDirectory}'.");
        }

        return path;
    }

    private static string Sha256(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
