using System.Security.Cryptography;
using Forge.Approval;
using Forge.Plugin.Sdk;
using Forge.Scripting;

namespace Forge.ExtensionHost;

internal sealed record PluginScanResult(IReadOnlyList<PluginEntry> Plugins, IReadOnlyList<PluginIssue> Issues);

/// <summary>
/// Scans <c>&lt;plugins&gt;/&lt;id&gt;/</c> for <c>manifest.json</c>, resolves each plugin's content and records every
/// problem as an issue instead of throwing, so one unusable package never keeps the healthy ones from registering. A
/// source-only plugin is compiled with <see cref="ScriptCompiler"/> and its image is stored through
/// <see cref="VersionedOutputStore"/> under the plugin's own <c>bin</c> directory, keyed by the compilation cache key,
/// so an unchanged plugin never pays Roslyn twice.
/// </summary>
internal sealed class PluginCatalog
{
    private const int MaxSourceBytes = 1024 * 1024;

    private static readonly IReadOnlyDictionary<string, string> EmptyFiles =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Manifest file name of the retired 1.x plugin format; only used to explain why a package is not loaded.</summary>
    private const string LegacyManifestFileName = "plugin.json";

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
            try
            {
                await DiscoverOneAsync(directory, seen, plugins, issues, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                issues.Add(new PluginIssue(
                    Path.GetFileName(directory),
                    PluginIssueKind.InvalidPackage,
                    $"'{directory}' could not be discovered: {exception.Message}",
                    directory));
            }
        }

        return new PluginScanResult(plugins, issues);
    }

    /// <summary>
    /// Reads the plugin's content from disk now and returns it with the hashes the approval has to cover. Every file of
    /// the loadable directory is read and hashed afresh, so an artifact is never trusted because of the directory it
    /// sits in, and the returned image is the exact set of bytes the caller then hands to the worker.
    /// </summary>
    public static async Task<PluginContent> SnapshotAsync(PluginEntry entry, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.Image is not { } image)
        {
            throw new InvalidOperationException($"Plugin '{entry.Id}' has no loadable image.");
        }

        var files = await ReadLoadableFilesAsync(image.Directory, ct).ConfigureAwait(false);
        if (!files.TryGetValue(image.AssemblyFileName, out var assemblySha256))
        {
            throw new FileNotFoundException(
                $"Plugin assembly '{image.AssemblyPath}' is missing.",
                image.AssemblyPath);
        }

        var sourceHash = entry.ContentIsBinary
            ? assemblySha256
            : await SourceHashAsync(entry, ct).ConfigureAwait(false);
        var current = new PluginImage(image.Directory, image.AssemblyFileName, assemblySha256, files);
        return new PluginContent(
            current,
            sourceHash,
            DependencyFingerprint(entry.Manifest.Dependencies, entry.Manifest.Permissions, files));
    }

    /// <summary>
    /// Folds the private-dependency digests, the manifest dependencies and the declared permissions together, so replacing
    /// any loaded file <em>or changing what the plugin asks to be allowed to do</em> invalidates the approval. Permissions
    /// therefore never need an SDK version bump to force re-approval.
    /// <para>
    /// The encoding is the host's own <see cref="DependencyHashSchema.SchemaMarker"/> schema, not
    /// <see cref="Hashing.DependencyHash"/>: each tuple carries an explicit category domain and every field is length
    /// framed, because manifest dependency names and versions are arbitrary text and the old <c>name@version</c> line
    /// format let a crafted dependency produce the same bytes as an internal permission entry. Changing this hash is meant
    /// to invalidate every approval written under the previous format (fail closed, no migration): the user re-approves
    /// what is actually declared now.
    /// </para>
    /// </summary>
    internal static string DependencyFingerprint(
        IReadOnlyList<Dependency> dependencies,
        IReadOnlyList<Permission> permissions,
        IReadOnlyDictionary<string, string> files) =>
        DependencyHashSchema.Compute(dependencies, permissions, files);

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

    private async Task DiscoverOneAsync(
        string directory,
        HashSet<string> seen,
        List<PluginEntry> plugins,
        List<PluginIssue> issues,
        CancellationToken ct)
    {
        var directoryName = Path.GetFileName(directory);
        if (!PluginIds.IsSafeSegment(directoryName))
        {
            issues.Add(new PluginIssue(
                directoryName,
                PluginIssueKind.DirectoryNameInvalid,
                $"'{directory}' is not a usable plugin directory name; it was skipped.",
                directory));
            return;
        }

        if (!PluginManifestReader.TryRead(directory, out var manifest, out var kind, out var error))
        {
            issues.Add(new PluginIssue(directoryName, kind, error, directory));
            if (kind == PluginIssueKind.ManifestMissing && HasLegacyManifest(directory))
            {
                // A 1.x package is never adapted or executed: its plugin.json describes an IForgePlugin assembly the
                // current contract does not load, so the user gets an explicit migration diagnostic instead.
                issues.Add(new PluginIssue(
                    directoryName,
                    PluginIssueKind.LegacyPluginPackage,
                    $"'{directory}' contains a legacy plugin.json (IForgePlugin package). Forge 2.0 plugins declare manifest.json and implement Forge.Plugin.Sdk.IPlugin; this package is not compatible and was not loaded. Migrate the plugin or remove the directory.",
                    directory));
            }

            return;
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
            return;
        }

        var entry = new PluginEntry(manifest);
        await PrepareAsync(entry, ct).ConfigureAwait(false);
        issues.AddRange(entry.Issues);
        plugins.Add(entry);
    }

    /// <summary>Resolves the content behind one manifest: source text plus hash, or a prebuilt image, and then binds the
    /// bytes it is loaded from into <see cref="PluginEntry.Image"/> and the dependency hash.</summary>
    private async Task PrepareAsync(PluginEntry entry, CancellationToken ct)
    {
        var manifest = entry.Manifest;
        entry.SdkVersion = ScriptCompiler.ContractVersion;
        entry.DependencyHash = DependencyFingerprint(manifest.Dependencies, manifest.Permissions, EmptyFiles);

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
            entry.InlineSource = sourcePath is null ? sourceText : null;
            entry.SourceHash = Hashing.SourceHash(sourceText);
            await CompileAsync(entry, sourceText, sourcePath, ct).ConfigureAwait(false);
        }
        else
        {
            ResolvePrebuilt(entry);
        }

        await BindImageAsync(entry, ct).ConfigureAwait(false);
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
        var cached = FindOutput(store, manifest.Id, version, fileName);
        if (cached is not null)
        {
            entry.Image = ImageAt(cached);
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
            entry.Image = ImageAt(await store.SaveAsync(manifest.Id, result, version, fileName, ct).ConfigureAwait(false));
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

    private static void ResolvePrebuilt(PluginEntry entry)
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

            entry.ContentIsBinary = true;
            entry.ContentPath = Path.GetFullPath(candidate);
            entry.Image = ImageAt(candidate);
            return;
        }

        entry.Issues.Add(new PluginIssue(
            manifest.Id,
            PluginIssueKind.EntryAssemblyMissing,
            $"No plugin assembly was found; expected '{candidates[0]}'.",
            manifest.Directory));
    }

    /// <summary>Hashes every file of the image's loadable directory — the entry assembly and its siblings — and folds
    /// the digests into the dependency hash, so replacing any of them invalidates the approval.</summary>
    private static async Task BindImageAsync(PluginEntry entry, CancellationToken ct)
    {
        if (entry.Image is not { } image)
        {
            return;
        }

        try
        {
            var files = await ReadLoadableFilesAsync(image.Directory, ct).ConfigureAwait(false);
            if (!files.TryGetValue(image.AssemblyFileName, out var assemblySha256))
            {
                entry.Issues.Add(new PluginIssue(
                    entry.Id,
                    PluginIssueKind.EntryAssemblyMissing,
                    $"Plugin assembly '{image.AssemblyPath}' disappeared while it was being read.",
                    image.Directory));
                return;
            }

            entry.Image = image with { AssemblySha256 = assemblySha256, Files = files };
            if (entry.ContentIsBinary)
            {
                entry.SourceHash = assemblySha256;
            }

            entry.DependencyHash = DependencyFingerprint(entry.Manifest.Dependencies, entry.Manifest.Permissions, files);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            entry.Issues.Add(new PluginIssue(
                entry.Id,
                PluginIssueKind.EntryAssemblyMissing,
                $"The loadable files of '{entry.Id}' could not be read: {exception.Message}",
                image.Directory));
        }
    }

    private static PluginImage ImageAt(string assemblyPath)
    {
        var full = Path.GetFullPath(assemblyPath);
        return new PluginImage(
            Path.GetDirectoryName(full) ?? full,
            Path.GetFileName(full),
            string.Empty,
            new Dictionary<string, string>(StringComparer.Ordinal));
    }

    /// <summary>Hashes every file under the entry assembly's directory — the entry assembly, its siblings and anything
    /// in sub-directories — with <c>/</c>-separated relative names. These are exactly the names a verifying worker
    /// compares against, so nothing the loader can reach stays undeclared.</summary>
    private static async Task<IReadOnlyDictionary<string, string>> ReadLoadableFilesAsync(
        string directory,
        CancellationToken ct)
    {
        string[] files;
        try
        {
            files = [.. Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new IOException($"'{directory}' could not be listed: {exception.Message}", exception);
        }

        var digests = new Dictionary<string, string>(files.Length, StringComparer.Ordinal);
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            var bytes = await File.ReadAllBytesAsync(file, ct).ConfigureAwait(false);
            digests[Path.GetRelativePath(directory, file).Replace('\\', '/')] = Convert.ToHexStringLower(SHA256.HashData(bytes));
        }

        return digests;
    }

    private static async Task<string> SourceHashAsync(PluginEntry entry, CancellationToken ct)
    {
        if (entry.ContentPath is { } path)
        {
            return Hashing.SourceHash(StripBom(await File.ReadAllTextAsync(path, ct).ConfigureAwait(false)));
        }

        return Hashing.SourceHash(entry.InlineSource ?? string.Empty);
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

    private static bool HasLegacyManifest(string directory)
    {
        try
        {
            return File.Exists(Path.Combine(directory, LegacyManifestFileName));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Drops a leading byte-order mark so the approval hash and the compiler's own source hash agree.</summary>
    internal static string StripBom(string text) => text.Length > 0 && text[0] == '\uFEFF' ? text[1..] : text;
}
