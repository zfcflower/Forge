using System.Security.Cryptography;
using Forge.Approval;
using Forge.Scripting;

namespace Forge.ExtensionHost;

internal sealed record PluginScanResult(IReadOnlyList<PluginEntry> Plugins, IReadOnlyList<PluginIssue> Issues);

/// <summary>
/// Scans <c>&lt;plugins&gt;/&lt;id&gt;/</c> for <c>manifest.json</c>, resolves each plugin's content and records every
/// problem as an issue instead of throwing. A source-only plugin is compiled with <see cref="ScriptCompiler"/> and its
/// image is stored through <see cref="VersionedOutputStore"/> under the plugin's own <c>bin</c> directory, keyed by the
/// compilation cache key, so an unchanged plugin never pays Roslyn twice.
/// </summary>
internal sealed class PluginCatalog
{
    private const int MaxSourceBytes = 1024 * 1024;

    private readonly ScriptCompiler compiler;

    public PluginCatalog(string pluginsDirectory, ScriptCompiler? compiler = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginsDirectory);
        PluginsDirectory = Path.GetFullPath(pluginsDirectory);
        this.compiler = compiler ?? new ScriptCompiler();
    }

    public string PluginsDirectory { get; }

    public async Task<PluginScanResult> DiscoverAsync(CancellationToken ct = default)
    {
        var plugins = new List<PluginEntry>();
        var issues = new List<PluginIssue>();
        string[] directories;
        try
        {
            if (!Directory.Exists(PluginsDirectory))
            {
                issues.Add(new PluginIssue(
                    string.Empty,
                    PluginIssueKind.PluginsRootMissing,
                    $"Plugin directory '{PluginsDirectory}' does not exist."));
                return new PluginScanResult(plugins, issues);
            }

            directories = Directory.GetDirectories(PluginsDirectory);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            issues.Add(new PluginIssue(
                string.Empty,
                PluginIssueKind.PluginsRootUnreadable,
                $"Plugin directory '{PluginsDirectory}' could not be read: {exception.Message}"));
            return new PluginScanResult(plugins, issues);
        }

        Array.Sort(directories, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var directory in directories)
        {
            ct.ThrowIfCancellationRequested();
            var directoryName = Path.GetFileName(directory);
            if (!PluginIds.IsSafeSegment(directoryName))
            {
                issues.Add(new PluginIssue(
                    directoryName,
                    PluginIssueKind.DirectoryNameInvalid,
                    $"'{directory}' is not a usable plugin directory name; it was skipped.",
                    directory));
                continue;
            }

            if (!PluginManifestReader.TryRead(directory, out var manifest, out var kind, out var error))
            {
                issues.Add(new PluginIssue(directoryName, kind, error, directory));
                continue;
            }

            if (!string.Equals(manifest.Id, directoryName, StringComparison.Ordinal))
            {
                issues.Add(new PluginIssue(
                    manifest.Id,
                    PluginIssueKind.IdMismatch,
                    $"Plugin id '{manifest.Id}' does not match its directory name '{directoryName}'.",
                    directory));
            }

            if (!seen.Add(manifest.Id))
            {
                issues.Add(new PluginIssue(
                    manifest.Id,
                    PluginIssueKind.DuplicateId,
                    $"Plugin '{manifest.Id}' is declared by more than one directory; '{directory}' was ignored.",
                    directory));
                continue;
            }

            var entry = new PluginEntry(manifest);
            await PrepareAsync(entry, ct).ConfigureAwait(false);
            issues.AddRange(entry.Issues);
            plugins.Add(entry);
        }

        return new PluginScanResult(plugins, issues);
    }

    /// <summary>Resolves the content behind one manifest: source text plus hash, or a prebuilt image plus its hash.</summary>
    private async Task PrepareAsync(PluginEntry entry, CancellationToken ct)
    {
        var manifest = entry.Manifest;
        entry.SdkVersion = ScriptCompiler.ContractVersion;
        entry.DependencyHash = Hashing.DependencyHash(manifest.Dependencies);

        if (!string.IsNullOrWhiteSpace(manifest.Source))
        {
            if (!TryLoadSource(manifest, out var sourcePath, out var sourceText))
            {
                entry.Issues.Add(new PluginIssue(
                    manifest.Id,
                    PluginIssueKind.SourceMissing,
                    $"Plugin source '{manifest.Source}' was not found in '{manifest.Directory}'.",
                    manifest.Directory));
                return;
            }

            entry.ContentPath = sourcePath;
            entry.ContentIsBinary = false;
            entry.SourceHash = Hashing.SourceHash(sourceText);
            await CompileAsync(entry, sourceText, sourcePath, ct).ConfigureAwait(false);
            return;
        }

        await ResolvePrebuiltAsync(entry, ct).ConfigureAwait(false);
    }

    private async Task CompileAsync(PluginEntry entry, string sourceText, string? sourcePath, CancellationToken ct)
    {
        var manifest = entry.Manifest;
        var store = new VersionedOutputStore(Path.Combine(manifest.Directory, "bin"));
        var request = new CompilationRequest
        {
            Source = sourceText,
            AssemblyName = PluginIds.AssemblyName(manifest.Id),
            ExpectedSourceHash = entry.SourceHash,
            ContractVersion = entry.SdkVersion,
            FilePath = sourcePath is null ? ScriptCompiler.DefaultFilePath : Path.GetFileName(sourcePath),
        };

        var version = VersionedOutputStore.VersionFor(request.CacheKey);
        var fileName = manifest.OutputFileName;
        if (FindOutput(store, manifest.Id, version, fileName) is { } cached)
        {
            entry.AssemblyPath = cached;
            return;
        }

        CompilationResult result;
        try
        {
            result = await compiler.CompileAsync(request, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            entry.Issues.Add(new PluginIssue(
                manifest.Id,
                PluginIssueKind.CompileFailed,
                $"Compiling '{manifest.Id}' failed: {exception.Message}",
                manifest.Directory));
            return;
        }

        if (!result.Success || result.AssemblyBytes is not { Length: > 0 })
        {
            entry.Issues.Add(new PluginIssue(
                manifest.Id,
                PluginIssueKind.CompileFailed,
                result.ErrorText ?? $"Plugin '{manifest.Id}' did not compile.",
                manifest.Directory));
            return;
        }

        try
        {
            entry.AssemblyPath = await store.SaveAsync(manifest.Id, result, version, fileName, ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            entry.Issues.Add(new PluginIssue(
                manifest.Id,
                PluginIssueKind.CompileFailed,
                $"The compiled image of '{manifest.Id}' could not be stored under '{store.BaseDirectory}': {exception.Message}",
                manifest.Directory));
        }
    }

    private static async Task ResolvePrebuiltAsync(PluginEntry entry, CancellationToken ct)
    {
        var manifest = entry.Manifest;
        var store = new VersionedOutputStore(Path.Combine(manifest.Directory, "bin"));
        var fileName = manifest.OutputFileName;
        string[] candidates =
        [
            Path.Combine(manifest.Directory, "bin", manifest.Version, fileName),
            store.GetOutputPath(manifest.Id, manifest.Version, fileName),
            Path.Combine(manifest.Directory, fileName),
        ];

        foreach (var candidate in candidates)
        {
            if (!File.Exists(candidate))
            {
                continue;
            }

            var full = Path.GetFullPath(candidate);
            try
            {
                entry.SourceHash = await HashFileAsync(full, ct).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                entry.Issues.Add(new PluginIssue(
                    manifest.Id,
                    PluginIssueKind.EntryAssemblyMissing,
                    $"Plugin assembly '{full}' could not be read: {exception.Message}",
                    manifest.Directory));
                return;
            }

            entry.AssemblyPath = full;
            entry.ContentPath = full;
            entry.ContentIsBinary = true;
            return;
        }

        entry.Issues.Add(new PluginIssue(
            manifest.Id,
            PluginIssueKind.EntryAssemblyMissing,
            $"No plugin assembly was found; expected '{candidates[0]}'.",
            manifest.Directory));
    }

    /// <summary>The stored image for a compilation version: the store layout, then the literal
    /// <c>bin/&lt;version&gt;/</c> layout, so an image produced elsewhere is still picked up.</summary>
    private static string? FindOutput(VersionedOutputStore store, string pluginId, string version, string fileName)
    {
        string[] candidates =
        [
            store.GetOutputPath(pluginId, version, fileName),
            Path.Combine(store.BaseDirectory, version, fileName),
        ];

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }

        return null;
    }

    private static bool TryLoadSource(PluginManifest manifest, out string? path, out string text)
    {
        var source = manifest.Source!;
        path = null;
        if (source.Contains('\n') || source.Contains('\r'))
        {
            text = StripBom(source);
            return true;
        }

        var candidate = Path.IsPathRooted(source) ? source : Path.Combine(manifest.Directory, source);
        if (File.Exists(candidate))
        {
            var info = new FileInfo(candidate);
            if (info.Length <= MaxSourceBytes)
            {
                text = StripBom(File.ReadAllText(candidate));
                path = Path.GetFullPath(candidate);
                return true;
            }
        }

        if (source.Contains('{') || source.Contains(';'))
        {
            text = StripBom(source);
            return true;
        }

        text = string.Empty;
        return false;
    }

    /// <summary>Drops a leading byte-order mark so the approval hash and the compiler's own source hash agree.</summary>
    internal static string StripBom(string text) => text.Length > 0 && text[0] == '\uFEFF' ? text[1..] : text;

    private static async Task<string> HashFileAsync(string path, CancellationToken ct)
    {
        var bytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }
}
